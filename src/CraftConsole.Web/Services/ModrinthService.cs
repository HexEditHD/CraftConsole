using CraftConsole.Core.Models;
using CraftConsole.Infrastructure.Config;
using CraftConsole.Infrastructure.Http;

namespace CraftConsole.Web.Services;

public record ModrinthRequiredDependency(string ProjectId, string ProjectTitle);

/// <summary>
/// Installed is empty and NeedsDependencyConfirmation is true when the chosen
/// version has required dependencies the caller hasn't confirmed yet — nothing
/// is written to disk in that case. Call InstallAsync again with
/// includeDependencies: true to install the version and every required
/// dependency together. Warnings covers partial successes — the file is
/// installed, but something adjacent needs a human (most often: a stale jar
/// from a previous version couldn't be removed because the server is running).
/// </summary>
public record ModrinthInstallResult(
    bool NeedsDependencyConfirmation,
    List<ModrinthRequiredDependency> RequiredDependencies,
    List<ModrinthInstall> Installed,
    List<string> Warnings);

/// <summary>
/// Search and install orchestration for the Plugins screen's Browse tab.
/// Search and version listing work off the profile alone — they're pure
/// network calls — so they're available for a profile that has never been
/// started. Install/list/remove take a resolved supervisor instead, the same
/// gate WorkspaceApi's plugin/file routes already use, since writing into
/// plugins/ or mods/ needs a known local working directory the same way
/// editing a config file does.
/// </summary>
public sealed class ModrinthService
{
    private readonly ModrinthClient _client;
    private readonly DownloadService _downloader;
    private readonly JsonFileStore<List<ModrinthInstall>> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ModrinthInstall>? _installs;

    public ModrinthService(ModrinthClient client, DownloadService downloader, SettingsHolder settings)
    {
        _client = client;
        _downloader = downloader;
        _store = new JsonFileStore<List<ModrinthInstall>>(settings.AppDataPath, "modrinth-installs.json");
    }

    /// <summary>
    /// (project type, OR-matched loader categories) for a server type, or a
    /// null project type for Vanilla, which has no plugin or mod system.
    /// A Paper/Purpur profile also matches "spigot"/"bukkit" plugins since
    /// most declare only one of the three even though all are compatible.
    /// </summary>
    private static (string? ProjectType, List<string> Loaders) LoaderInfo(ServerType type) => type switch
    {
        ServerType.Paper => ("plugin", ["paper", "spigot", "bukkit"]),
        ServerType.Purpur => ("plugin", ["purpur", "paper", "spigot", "bukkit"]),
        ServerType.Spigot => ("plugin", ["spigot", "bukkit"]),
        ServerType.Fabric => ("mod", ["fabric"]),
        ServerType.Forge => ("mod", ["forge"]),
        ServerType.NeoForge => ("mod", ["neoforge"]),
        _ => (null, []),
    };

    private static string TargetFolder(ServerType type) => LoaderInfo(type).ProjectType == "mod" ? "mods" : "plugins";

    public Task<ModrinthSearchResult> SearchAsync(
        ServerProfile profile, string query, int offset, int limit, CancellationToken ct)
    {
        var (projectType, loaders) = LoaderInfo(profile.Type);
        return projectType is null
            ? Task.FromResult(new ModrinthSearchResult([], 0))
            : _client.SearchAsync(query, projectType, loaders, profile.MinecraftVersion, offset, limit, ct);
    }

    public Task<List<ModrinthVersion>> GetVersionsAsync(ServerProfile profile, string projectId, CancellationToken ct)
    {
        var (_, loaders) = LoaderInfo(profile.Type);
        return _client.GetProjectVersionsAsync(projectId, loaders, profile.MinecraftVersion, ct);
    }

    public async Task<ModrinthInstallResult> InstallAsync(
        ServerSupervisor sup, string versionId, bool includeDependencies, CancellationToken ct)
    {
        if (sup.LocalFileUnavailableReason is { } reason)
            throw new InvalidOperationException(reason);
        var profile = sup.ActiveProfile!;
        if (LoaderInfo(profile.Type).ProjectType is null)
            throw new InvalidOperationException($"{profile.Type} has no plugin or mod system to install into.");

        var version = await _client.GetVersionAsync(versionId, ct);
        var required = version.Dependencies.Where(d => d.DependencyType == "required").ToList();

        if (required.Count > 0 && !includeDependencies)
        {
            List<ModrinthRequiredDependency> deps = [];
            foreach (var dep in required)
            {
                var projectId = dep.ProjectId
                    ?? (dep.VersionId is { } vid ? (await _client.GetVersionAsync(vid, ct)).ProjectId : null);
                if (projectId is null) continue;
                deps.Add(new ModrinthRequiredDependency(projectId, await _client.GetProjectTitleAsync(projectId, ct)));
            }
            return new ModrinthInstallResult(true, deps, [], []);
        }

        List<ModrinthInstall> installed = [];
        List<string> warnings = [];
        var (primary, primaryWarning) = await InstallOneAsync(sup, version, ct);
        installed.Add(primary);
        if (primaryWarning is not null) warnings.Add(primaryWarning);

        if (includeDependencies)
        {
            var (_, loaders) = LoaderInfo(profile.Type);
            foreach (var dep in required)
            {
                ModrinthVersion? depVersion = null;
                if (dep.VersionId is { } vid)
                {
                    depVersion = await _client.GetVersionAsync(vid, ct);
                }
                else if (dep.ProjectId is { } pid)
                {
                    var versions = await _client.GetProjectVersionsAsync(pid, loaders, profile.MinecraftVersion, ct);
                    depVersion = versions.Count > 0 ? versions[0] : null;
                }
                // No compatible version found for this dependency — skip it rather
                // than failing the whole install; the primary file already succeeded.
                if (depVersion is not null)
                {
                    var (depInstall, depWarning) = await InstallOneAsync(sup, depVersion, ct);
                    installed.Add(depInstall);
                    if (depWarning is not null) warnings.Add(depWarning);
                }
            }
        }

        return new ModrinthInstallResult(false, [], installed, warnings);
    }

    private async Task<(ModrinthInstall Install, string? Warning)> InstallOneAsync(
        ServerSupervisor sup, ModrinthVersion version, CancellationToken ct)
    {
        var profile = sup.ActiveProfile!;
        var dir = Path.Combine(profile.WorkingDirectory, TargetFolder(profile.Type));
        Directory.CreateDirectory(dir);

        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.First();
        // Modrinth-supplied, not user input, but it still flows into a filesystem
        // path — GetFileName strips any directory component defensively.
        var fileName = Path.GetFileName(file.FileName);

        // A re-install of this project replaces the tracked entry (see TrackAsync
        // below) — look up what it's replacing before writing, so the writer can
        // clean up the old file when the new one has a different name.
        var previousFileName = (await LoadAsync())
            .FirstOrDefault(i => i.ServerId == sup.ServerId && i.ProjectId == version.ProjectId)?.FileName;
        var warning = await InstalledJarWriter.WriteAsync(_downloader, file.Url, dir, fileName, previousFileName, ct);

        var install = new ModrinthInstall
        {
            ServerId = sup.ServerId,
            ProjectId = version.ProjectId,
            ProjectTitle = await _client.GetProjectTitleAsync(version.ProjectId, ct),
            VersionId = version.Id,
            VersionNumber = version.VersionNumber,
            FileName = fileName,
            InstalledAt = DateTimeOffset.UtcNow,
        };
        await TrackAsync(install);
        return (install, warning);
    }

    public async Task<List<ModrinthInstall>> ListInstalledAsync(ServerSupervisor sup)
        => [.. (await LoadAsync()).Where(i => i.ServerId == sup.ServerId)];

    public async Task<bool> RemoveAsync(ServerSupervisor sup, string projectId)
    {
        var install = (await ListInstalledAsync(sup)).FirstOrDefault(i => i.ProjectId == projectId);
        if (install is null) return false;

        // A tracked install can outlive the process that made it — the store
        // has no cleanup of its own — so this can be reached for a profile that
        // has never been started this run, whose ActiveProfile is still null.
        if (sup.LocalFileUnavailableReason is { } reason)
            throw new InvalidOperationException(reason);

        var path = Path.Combine(sup.ActiveProfile!.WorkingDirectory, TargetFolder(sup.ActiveProfile.Type), install.FileName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Don't drop the tracking row on a failed delete — that would orphan
            // the file on disk with nothing left pointing at it, and no way to
            // retry from the UI.
            throw new InvalidOperationException(
                $"Could not remove \"{install.FileName}\" — if the server is running it may be " +
                "holding it open. Stop it and try again.", ex);
        }

        await _gate.WaitAsync();
        try
        {
            _installs!.RemoveAll(i => i.ServerId == sup.ServerId && i.ProjectId == projectId);
            await _store.SaveAsync(_installs);
        }
        finally { _gate.Release(); }
        return true;
    }

    private async Task<List<ModrinthInstall>> LoadAsync()
    {
        await _gate.WaitAsync();
        try { _installs ??= await _store.LoadAsync() ?? []; return _installs; }
        finally { _gate.Release(); }
    }

    private async Task TrackAsync(ModrinthInstall install)
    {
        await _gate.WaitAsync();
        try
        {
            _installs ??= await _store.LoadAsync() ?? [];
            // A re-install of the same project (an update, or retrying after a
            // failed dependency) replaces its own tracking entry rather than
            // appending a duplicate.
            _installs.RemoveAll(i => i.ServerId == install.ServerId && i.ProjectId == install.ProjectId);
            _installs.Add(install);
            await _store.SaveAsync(_installs);
        }
        finally { _gate.Release(); }
    }
}
