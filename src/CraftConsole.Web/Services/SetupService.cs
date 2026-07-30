using CraftConsole.Core.Java;
using CraftConsole.Core.Models;
using CraftConsole.Infrastructure.Http;
using CraftConsole.Infrastructure.Java;

namespace CraftConsole.Web.Services;

/// <summary>Static metadata describing a server type option.</summary>
public record ServerTypeInfo(
    ServerType Type,
    string DisplayName,
    string Tag,
    string Description,
    bool HasAutoDownload);

/// <summary>
/// Orchestrates server-JAR and Java downloads. One download of each kind at a
/// time; progress streams to clients as SSE "setup" events.
/// </summary>
public sealed class SetupService
{
    public static readonly IReadOnlyList<ServerTypeInfo> AllServerTypes =
    [
        new(ServerType.Vanilla, "Vanilla", "OFFICIAL",
            "The official Mojang server. No plugins — purest Minecraft experience.",
            HasAutoDownload: true),
        new(ServerType.Paper, "PaperMC", "RECOMMENDED",
            "High-performance Spigot fork with extra optimisations. Supports all Bukkit/Spigot plugins.",
            HasAutoDownload: true),
        new(ServerType.Spigot, "Spigot", "PLUGIN",
            "Community-driven Bukkit fork. Requires BuildTools to compile — manual install.",
            HasAutoDownload: false),
        new(ServerType.Fabric, "Fabric", "MODDED",
            "Lightweight mod loader for performance mods and technical gameplay. Manual install.",
            HasAutoDownload: false),
        new(ServerType.Forge, "Forge", "MODDED",
            "The most popular platform for large modpacks. Installer required — manual install.",
            HasAutoDownload: false),
        new(ServerType.Purpur, "Purpur", "EXTENDED",
            "Paper fork with extra configuration and gameplay tweaks.",
            HasAutoDownload: true),
    ];

    private readonly ServerDownloadService _serverDownload;
    private readonly JavaDownloadService _javaDownload;
    private readonly EventBroker _broker;
    private readonly ILogger<SetupService> _log;

    private readonly object _lock = new();
    private CancellationTokenSource? _serverCts;
    private CancellationTokenSource? _javaCts;

    public SetupService(
        ServerDownloadService serverDownload, JavaDownloadService javaDownload,
        EventBroker broker, ILogger<SetupService> log)
    {
        _serverDownload = serverDownload;
        _javaDownload = javaDownload;
        _broker = broker;
        _log = log;
    }

    public Task<List<string>> FetchServerVersionsAsync(ServerType type, CancellationToken ct)
        => _serverDownload.FetchVersionsAsync(type, ct);

    public Task<List<JavaVersionInfo>> FetchJavaVersionsAsync(CancellationToken ct)
        => _javaDownload.FetchVersionsAsync(ct);

    /// <summary>Starts a server-JAR download in the background. Returns false if one is already running.</summary>
    public bool StartServerDownload(ServerType type, string? version, string directory)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_serverCts is not null) return false;
            cts = _serverCts = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Publish("server", "resolving", 0, "Resolving version…");
                var (resolved, url) = await _serverDownload.ResolveVersionAsync(type, version, cts.Token);

                Directory.CreateDirectory(directory);
                var typeInfo = AllServerTypes.First(t => t.Type == type);
                var fileName = $"{typeInfo.DisplayName.ToLowerInvariant()}-{resolved}.jar";
                var destPath = Path.Combine(directory, fileName);

                Publish("server", "downloading", 0, $"Downloading {typeInfo.DisplayName} {resolved}…");
                var progress = new Progress<double>(p =>
                    Publish("server", "downloading", p, $"Downloading {typeInfo.DisplayName} {resolved}…"));

                await _serverDownload.DownloadAsync(url, destPath, progress, cts.Token);
                Publish("server", "done", 1, $"Downloaded {fileName}", new { JarPath = destPath, Version = resolved });
            }
            catch (NotSupportedException ex) { Publish("server", "error", 0, ex.Message); }
            catch (OperationCanceledException) { Publish("server", "cancelled", 0, "Download cancelled."); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Server download failed");
                Publish("server", "error", 0, $"Download failed: {ex.Message}");
            }
            finally
            {
                lock (_lock) { _serverCts?.Dispose(); _serverCts = null; }
            }
        });
        return true;
    }

    /// <summary>Starts a Java (Temurin) download to the user's Downloads folder.</summary>
    public bool StartJavaDownload(int major)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_javaCts is not null) return false;
            cts = _javaCts = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Publish("java", "resolving", 0, $"Resolving Java {major}…");
                var (fileName, url) = await _javaDownload.ResolveAsync(major, cts.Token);

                var downloads = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                var destPath = Path.Combine(downloads, fileName);

                Publish("java", "downloading", 0, $"Downloading {fileName}…");
                var progress = new Progress<double>(p =>
                    Publish("java", "downloading", p, $"Downloading {fileName}…"));

                await _javaDownload.DownloadAsync(url, destPath, progress, cts.Token);

                if (OperatingSystem.IsWindows())
                {
                    await InstallOnWindowsAsync(destPath, fileName, cts.Token);
                }
                else
                {
                    var doneMessage = OperatingSystem.IsMacOS()
                        ? "Saved to Downloads. Run the installer to complete setup."
                        : $"Saved to Downloads. Extract it (tar xzf {fileName}) and point a profile's Java path at the java binary inside its bin/ folder.";
                    Publish("java", "done", 1, doneMessage, new { Path = destPath });
                }
            }
            catch (OperationCanceledException) { Publish("java", "cancelled", 0, "Download cancelled."); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Java download failed");
                Publish("java", "error", 0, $"Download failed: {ex.Message}");
            }
            finally
            {
                lock (_lock) { _javaCts?.Dispose(); _javaCts = null; }
            }
        });
        return true;
    }

    /// <summary>
    /// Runs the downloaded MSI elevated (one UAC prompt) instead of just telling the user to.
    /// Always ends in a "done" phase — the download itself already succeeded by this point, so
    /// even a declined prompt or a failed install isn't a failure of *this* operation, just one
    /// that leaves the file for the user to run by hand (message says so either way).
    /// </summary>
    private async Task InstallOnWindowsAsync(string msiPath, string fileName, CancellationToken ct)
    {
        Publish("java", "installing", 0, "Installing…");
        var result = await JavaInstallRunner.InstallWindowsAsync(msiPath, ct);

        switch (result.Outcome)
        {
            case JavaInstallOutcome.Succeeded:
                var installs = await JavaInstallationDetector.DetectAsync(ct);
                var newest = installs.OrderByDescending(i => i.MajorVersion).FirstOrDefault();
                Publish("java", "done", 1, "Java installed.", new { Path = msiPath, JavaPath = newest?.ExecutablePath });
                break;

            case JavaInstallOutcome.UserCancelledElevation:
                Publish("java", "done", 1,
                    $"Downloaded but not installed (elevation was declined). Run \"{fileName}\" from Downloads to install it yourself.",
                    new { Path = msiPath });
                break;

            default:
                Publish("java", "done", 1,
                    $"Downloaded but the installer failed ({result.Detail}). Run \"{fileName}\" from Downloads to install it yourself.",
                    new { Path = msiPath });
                break;
        }
    }

    public void Cancel(string kind)
    {
        lock (_lock)
        {
            if (kind == "server") _serverCts?.Cancel();
            else if (kind == "java") _javaCts?.Cancel();
        }
    }

    private void Publish(string kind, string phase, double progress, string message, object? extra = null)
        => _broker.Publish("setup", new
        {
            Kind = kind,
            Phase = phase,
            Progress = Math.Round(progress, 3),
            Message = message,
            Extra = extra,
        });
}
