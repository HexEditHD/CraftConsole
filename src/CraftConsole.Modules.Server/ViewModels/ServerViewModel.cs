using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftConsole.Core.Java;
using CraftConsole.Core.Models;
using CraftConsole.Core.Servers;
using CraftConsole.Infrastructure.Http;

namespace CraftConsole.Modules.Server.ViewModels;

/// <summary>Static metadata describing a server type option.</summary>
public record ServerTypeInfo(
    ServerType Type,
    string DisplayName,
    string Tag,
    string Description,
    bool HasAutoDownload);

public partial class ServerViewModel : ObservableObject
{
    private readonly ServerDownloadService _serverDownload;
    private readonly JavaDownloadService   _javaDownload;
    private IMinecraftServer? _server;
    private IDisposable? _statusSubscription;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _javaDownloadCts;

    // ── Server identity ─────────────────────────────────────────────────
    [ObservableProperty] private string _serverName = "My Server";
    [ObservableProperty] private string _jarPath = string.Empty;
    [ObservableProperty] private string _workingDirectory = string.Empty;
    [ObservableProperty] private string _minecraftVersion = string.Empty;
    [ObservableProperty] private string _jvmArguments = string.Empty;

    // ── Java detection ──────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<JavaInstallation> _javaInstallations = [];
    [ObservableProperty] private JavaInstallation? _selectedJava;
    [ObservableProperty] private bool _isDetectingJava;

    // ── RAM ─────────────────────────────────────────────────────────────
    [ObservableProperty] private int _minRamMb = 512;
    [ObservableProperty] private int _maxRamMb = 2048;

    // ── Server type selection ───────────────────────────────────────────
    [ObservableProperty] private ServerTypeInfo? _selectedServerType;
    [ObservableProperty] private bool _serverTypeSelected;

    // ── Server download ─────────────────────────────────────────────────
    [ObservableProperty] private string _downloadStatusText = string.Empty;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private ObservableCollection<string> _availableVersions = ["Latest"];
    [ObservableProperty] private string _selectedVersion = "Latest";
    [ObservableProperty] private bool _isFetchingVersions;

    // ── Java download ────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<JavaVersionInfo> _availableJavaVersions = [];
    [ObservableProperty] private JavaVersionInfo? _selectedJavaVersion;
    [ObservableProperty] private bool _isFetchingJavaVersions;
    [ObservableProperty] private bool _isDownloadingJava;
    [ObservableProperty] private double _javaDownloadProgress;
    [ObservableProperty] private string _javaDownloadStatusText = string.Empty;
    [ObservableProperty] private bool _canDownloadJava;

    // ── Server runtime ──────────────────────────────────────────────────
    [ObservableProperty] private ServerStatus _status = ServerStatus.Stopped;
    [ObservableProperty] private bool _canStart = true;
    [ObservableProperty] private bool _canStop;

    // ── Delegates set by MainWindowViewModel ────────────────────────────
    public Func<Task<string?>>? BrowseJarRequested { get; set; }
    public Action<IMinecraftServer>? ServerStarted { get; set; }
    public Action? NavigateToConsoleRequested { get; set; }

    // ── Static server type catalogue ─────────────────────────────────────
    public static readonly IReadOnlyList<ServerTypeInfo> AllServerTypes =
    [
        new(ServerType.Vanilla, "Vanilla",  "OFFICIAL",
            "The official Mojang server. No plugins — purest Minecraft experience.",
            HasAutoDownload: true),
        new(ServerType.Paper,   "PaperMC",  "RECOMMENDED",
            "High-performance Spigot fork with extra optimisations. Supports all Bukkit/Spigot plugins.",
            HasAutoDownload: true),
        new(ServerType.Spigot,  "Spigot",   "PLUGIN",
            "Community-driven Bukkit fork. Requires BuildTools to compile — manual install.",
            HasAutoDownload: false),
        new(ServerType.Fabric,  "Fabric",   "MODDED",
            "Lightweight mod loader for performance mods and technical gameplay. Manual install.",
            HasAutoDownload: false),
        new(ServerType.Forge,   "Forge",    "MODDED",
            "The most popular platform for large modpacks. Installer required — manual install.",
            HasAutoDownload: false),
        new(ServerType.Purpur,  "Purpur",   "EXTENDED",
            "Paper fork with extra configuration and gameplay tweaks.",
            HasAutoDownload: true),
    ];

    public ServerViewModel(ServerDownloadService serverDownload, JavaDownloadService javaDownload)
    {
        _serverDownload = serverDownload;
        _javaDownload   = javaDownload;

        // Default to Vanilla so the download pane is populated immediately
        SelectedServerType = AllServerTypes.First(t => t.Type == ServerType.Vanilla);

        _ = DetectJavaAsync();
        _ = FetchJavaVersionsAsync();
    }

    // ── Commands ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DetectJavaAsync()
    {
        IsDetectingJava = true;
        JavaInstallations.Clear();
        try
        {
            var found = await JavaInstallationDetector.DetectAsync();
            foreach (var j in found.OrderByDescending(j => j.MajorVersion))
                JavaInstallations.Add(j);
            SelectedJava = JavaInstallations.FirstOrDefault();
        }
        finally { IsDetectingJava = false; }
    }

    [RelayCommand]
    private async Task BrowseJarAsync()
    {
        if (BrowseJarRequested is null) return;
        var result = await BrowseJarRequested.Invoke();
        if (result is not null) JarPath = result;
    }

    // ── Server download commands ──────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadServerAsync()
    {
        if (SelectedServerType is null) return;

        IsDownloading = true;
        CanDownload = false;
        DownloadProgress = 0;
        DownloadStatusText = "Resolving version…";
        _downloadCts = new CancellationTokenSource();

        try
        {
            var requestedVersion = SelectedVersion == "Latest" ? null : SelectedVersion;
            var (version, url) = await _serverDownload.ResolveVersionAsync(
                SelectedServerType.Type, requestedVersion, _downloadCts.Token);

            MinecraftVersion = version;
            DownloadStatusText = $"Downloading {SelectedServerType.DisplayName} {version}…";

            var dir = string.IsNullOrWhiteSpace(WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : WorkingDirectory;
            Directory.CreateDirectory(dir);

            var fileName = $"{SelectedServerType.DisplayName.ToLower()}-{version}.jar";
            var destPath = Path.Combine(dir, fileName);

            var progress = new Progress<double>(p =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => DownloadProgress = p));

            await _serverDownload.DownloadAsync(url, destPath, progress, _downloadCts.Token);
            JarPath = destPath;
            DownloadStatusText = $"Downloaded: {fileName}";
            DownloadProgress = 1.0;
        }
        catch (NotSupportedException ex) { DownloadStatusText = ex.Message; }
        catch (OperationCanceledException)
        {
            DownloadStatusText = "Download cancelled.";
            DownloadProgress = 0;
        }
        catch (Exception ex) { DownloadStatusText = $"Download failed: {ex.Message}"; }
        finally
        {
            IsDownloading = false;
            CanDownload = SelectedServerType?.HasAutoDownload == true;
            DownloadServerCommand.NotifyCanExecuteChanged();
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    // ── Java download commands ─────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanDownloadJava))]
    private async Task DownloadJavaAsync()
    {
        if (SelectedJavaVersion is null) return;

        IsDownloadingJava = true;
        CanDownloadJava = false;
        JavaDownloadProgress = 0;
        JavaDownloadStatusText = $"Resolving Java {SelectedJavaVersion.Major}…";
        _javaDownloadCts = new CancellationTokenSource();

        try
        {
            var (fileName, url) = await _javaDownload.ResolveAsync(
                SelectedJavaVersion.Major, _javaDownloadCts.Token);

            JavaDownloadStatusText = $"Downloading {fileName}…";

            var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            downloads = Path.Combine(downloads, "Downloads");
            Directory.CreateDirectory(downloads);
            var destPath = Path.Combine(downloads, fileName);

            var progress = new Progress<double>(p =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => JavaDownloadProgress = p));

            await _javaDownload.DownloadAsync(url, destPath, progress, _javaDownloadCts.Token);
            JavaDownloadStatusText = $"Saved to Downloads. Run the installer to complete setup.";
            JavaDownloadProgress = 1.0;

            // Re-scan Java installs after a download
            _ = DetectJavaAsync();
        }
        catch (OperationCanceledException)
        {
            JavaDownloadStatusText = "Download cancelled.";
            JavaDownloadProgress = 0;
        }
        catch (Exception ex) { JavaDownloadStatusText = $"Download failed: {ex.Message}"; }
        finally
        {
            IsDownloadingJava = false;
            CanDownloadJava = SelectedJavaVersion is not null;
            DownloadJavaCommand.NotifyCanExecuteChanged();
            _javaDownloadCts?.Dispose();
            _javaDownloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelJavaDownload() => _javaDownloadCts?.Cancel();

    // ── Property change reactions ──────────────────────────────────────────

    partial void OnJarPathChanged(string value)
    {
        var dir = Path.GetDirectoryName(value);
        if (!string.IsNullOrWhiteSpace(dir))
            WorkingDirectory = dir;
    }

    partial void OnSelectedServerTypeChanged(ServerTypeInfo? value)
    {
        ServerTypeSelected = value is not null;
        CanDownload = value?.HasAutoDownload == true && !IsDownloading;
        DownloadServerCommand.NotifyCanExecuteChanged();
        DownloadProgress = 0;
        DownloadStatusText = value?.HasAutoDownload == false
            ? "Automated download not available. Visit the official website to get the installer."
            : string.Empty;

        AvailableVersions.Clear();
        AvailableVersions.Add("Latest");
        SelectedVersion = "Latest";

        if (value?.HasAutoDownload == true)
            _ = FetchServerVersionsAsync(value.Type);
    }

    partial void OnSelectedJavaVersionChanged(JavaVersionInfo? value)
    {
        CanDownloadJava = value is not null && !IsDownloadingJava;
        DownloadJavaCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanStartChanged(bool value) => StartServerCommand.NotifyCanExecuteChanged();
    partial void OnCanStopChanged(bool value)  => StopServerCommand.NotifyCanExecuteChanged();

    private async Task FetchServerVersionsAsync(ServerType type)
    {
        IsFetchingVersions = true;
        try
        {
            var versions = await _serverDownload.FetchVersionsAsync(type);
            foreach (var v in versions) AvailableVersions.Add(v);
        }
        catch { /* network failure — keep "Latest" only */ }
        finally { IsFetchingVersions = false; }
    }

    private async Task FetchJavaVersionsAsync()
    {
        IsFetchingJavaVersions = true;
        try
        {
            var versions = await _javaDownload.FetchVersionsAsync();
            foreach (var v in versions) AvailableJavaVersions.Add(v);
            SelectedJavaVersion = AvailableJavaVersions.FirstOrDefault();
        }
        catch { }
        finally { IsFetchingJavaVersions = false; }
    }

    // ── Server attach / start / stop ──────────────────────────────────────

    public void Attach(IMinecraftServer server)
    {
        _server = server;
        ServerName       = server.Profile.Name;
        JarPath          = server.Profile.JarPath;
        WorkingDirectory = server.Profile.WorkingDirectory;
        MinRamMb         = server.Profile.MinRamMb;
        MaxRamMb         = server.Profile.MaxRamMb;
        JvmArguments     = server.Profile.JvmArguments;
        SubscribeToStatus(server);
    }

    private void SubscribeToStatus(IMinecraftServer server)
    {
        _statusSubscription?.Dispose();
        _statusSubscription = server.StatusChanged.Subscribe(s =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Status   = s;
                CanStart = s is ServerStatus.Stopped or ServerStatus.Crashed;
                CanStop  = s is ServerStatus.Running;
            }));
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartServerAsync()
    {
        if (_server is null)
        {
            var profile = new ServerProfile
            {
                Name             = ServerName,
                JarPath          = JarPath,
                WorkingDirectory = WorkingDirectory,
                JavaPath         = SelectedJava?.ExecutablePath ?? "java",
                MinRamMb         = MinRamMb,
                MaxRamMb         = MaxRamMb,
                JvmArguments     = JvmArguments,
                Type             = SelectedServerType?.Type ?? ServerType.Paper,
                MinecraftVersion = MinecraftVersion,
            };
            _server = new ServerProcessManager(profile);
            SubscribeToStatus(_server);
        }

        ServerStarted?.Invoke(_server);
        NavigateToConsoleRequested?.Invoke();
        await _server.StartAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopServerAsync()
    {
        if (_server is null) return;
        await _server.StopAsync();
    }

    /// <summary>Gracefully stops the server if it is running. Called on app exit.</summary>
    public async Task ShutdownAsync()
    {
        if (_server is null) return;
        if (Status is ServerStatus.Running or ServerStatus.Starting)
            await _server.StopAsync();
    }
}
