using CraftConsole.Core.Models;
using CraftConsole.Infrastructure.Config;
using CraftConsole.Infrastructure.Http;

namespace CraftConsole.Web.Services;

public record CurseForgeRequiredDependency(int ModId, string ModName);

/// <summary>Same shape and reasoning as ModrinthInstallResult — see its own doc comment.</summary>
public record CurseForgeInstallResult(
    bool NeedsDependencyConfirmation,
    List<CurseForgeRequiredDependency> RequiredDependencies,
    List<CurseForgeInstall> Installed,
    List<string> Warnings);

/// <summary>
/// CurseForge's counterpart to ModrinthService — same shape and the same
/// reasoning throughout (search/file-listing work off the profile alone and
/// don't require a started server; install/list/remove need a resolved
/// supervisor). The one addition CurseForge needs that Modrinth doesn't is an
/// API key, required on every call — RequireApiKeyAsync below is the single
/// place that's enforced.
/// </summary>
public sealed class CurseForgeService
{
    // CurseForge's Minecraft (gameId 432) root category ids: Mods and Bukkit
    // Plugins are separate classes, not a "categories" facet the way Modrinth
    // treats loaders — https://api.curseforge.com/v1/categories?gameId=432 is
    // the authoritative source if these ever need re-confirming.
    private const int ClassMods = 6;
    private const int ClassPlugins = 5;

    private readonly CurseForgeClient _client;
    private readonly DownloadService _downloader;
    private readonly CurseForgeSecretStore _apiKey;
    private readonly JsonFileStore<List<CurseForgeInstall>> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<CurseForgeInstall>? _installs;

    public CurseForgeService(CurseForgeClient client, DownloadService downloader, CurseForgeSecretStore apiKey, SettingsHolder settings)
    {
        _client = client;
        _downloader = downloader;
        _apiKey = apiKey;
        _store = new JsonFileStore<List<CurseForgeInstall>>(settings.AppDataPath, "curseforge-installs.json");
    }

    /// <summary>
    /// (class id, mod loader type) for a server type, or a null class id for
    /// Vanilla, which has no plugin or mod system. CurseForge's modLoaderType
    /// is only meaningful for the Mods class — plugins have no loader filter
    /// of their own, they're implicitly Bukkit-family.
    /// </summary>
    private static (int? ClassId, int? ModLoaderType) LoaderInfo(ServerType type) => type switch
    {
        ServerType.Paper or ServerType.Purpur or ServerType.Spigot => (ClassPlugins, null),
        ServerType.Fabric => (ClassMods, 4),
        ServerType.Forge => (ClassMods, 1),
        ServerType.NeoForge => (ClassMods, 6),
        _ => (null, null),
    };

    private static string TargetFolder(ServerType type)
        => type is ServerType.Fabric or ServerType.Forge or ServerType.NeoForge ? "mods" : "plugins";

    private async Task<string> RequireApiKeyAsync()
        => await _apiKey.TryGetAsync()
           ?? throw new InvalidOperationException("No CurseForge API key is configured — add one in Settings first.");

    public async Task<CurseForgeSearchResult> SearchAsync(
        ServerProfile profile, string query, int offset, int limit, CancellationToken ct)
    {
        var (classId, modLoaderType) = LoaderInfo(profile.Type);
        if (classId is null) return new CurseForgeSearchResult([], 0);

        var apiKey = await RequireApiKeyAsync();
        return await _client.SearchAsync(apiKey, query, classId.Value, modLoaderType, profile.MinecraftVersion, offset, limit, ct);
    }

    public async Task<List<CurseForgeFile>> GetFilesAsync(ServerProfile profile, int modId, CancellationToken ct)
    {
        var (_, modLoaderType) = LoaderInfo(profile.Type);
        var apiKey = await RequireApiKeyAsync();
        return await _client.GetModFilesAsync(apiKey, modId, modLoaderType, profile.MinecraftVersion, ct);
    }

    public async Task<CurseForgeInstallResult> InstallAsync(
        ServerSupervisor sup, int modId, int fileId, bool includeDependencies, CancellationToken ct)
    {
        if (sup.LocalFileUnavailableReason is { } reason)
            throw new InvalidOperationException(reason);
        var profile = sup.ActiveProfile!;
        if (LoaderInfo(profile.Type).ClassId is null)
            throw new InvalidOperationException($"{profile.Type} has no plugin or mod system to install into.");

        var apiKey = await RequireApiKeyAsync();
        var file = await _client.GetFileAsync(apiKey, modId, fileId, ct);
        var required = file.Dependencies.Where(d => d.RelationType == "required").ToList();

        if (required.Count > 0 && !includeDependencies)
        {
            List<CurseForgeRequiredDependency> deps = [];
            foreach (var dep in required)
                deps.Add(new CurseForgeRequiredDependency(dep.ModId, await _client.GetModNameAsync(apiKey, dep.ModId, ct)));
            return new CurseForgeInstallResult(true, deps, [], []);
        }

        List<CurseForgeInstall> installed = [];
        List<string> warnings = [];
        var (primary, primaryWarning) = await InstallOneAsync(sup, apiKey, file, ct);
        installed.Add(primary);
        if (primaryWarning is not null) warnings.Add(primaryWarning);

        if (includeDependencies)
        {
            var (_, modLoaderType) = LoaderInfo(profile.Type);
            foreach (var dep in required)
            {
                var depFiles = await _client.GetModFilesAsync(apiKey, dep.ModId, modLoaderType, profile.MinecraftVersion, ct);
                // No compatible file found for this dependency — skip it rather
                // than failing the whole install; the primary file already succeeded.
                if (depFiles.Count > 0)
                {
                    var (depInstall, depWarning) = await InstallOneAsync(sup, apiKey, depFiles[0], ct);
                    installed.Add(depInstall);
                    if (depWarning is not null) warnings.Add(depWarning);
                }
            }
        }

        return new CurseForgeInstallResult(false, [], installed, warnings);
    }

    private async Task<(CurseForgeInstall Install, string? Warning)> InstallOneAsync(
        ServerSupervisor sup, string apiKey, CurseForgeFile file, CancellationToken ct)
    {
        var profile = sup.ActiveProfile!;
        var dir = Path.Combine(profile.WorkingDirectory, TargetFolder(profile.Type));
        var downloadUrl = file.DownloadUrl ?? await _client.ResolveDownloadUrlAsync(apiKey, file.ModId, file.Id, ct);
        if (downloadUrl is null)
            throw new InvalidOperationException(
                $"\"{file.FileName}\" can't be downloaded automatically — its author disabled third-party downloads. " +
                "Download it from the CurseForge website and upload it through the Files tab instead.");

        // Created only once a download is actually going to happen — creating
        // it earlier would leave a stray empty plugins/mods folder behind
        // every time a file with no resolvable download URL is rejected above.
        Directory.CreateDirectory(dir);

        // CurseForge-supplied, not user input, but it still flows into a
        // filesystem path — GetFileName strips any directory component defensively.
        var fileName = Path.GetFileName(file.FileName);

        // A re-install of this mod replaces the tracked entry (see TrackAsync
        // below) — look up what it's replacing before writing, so the writer can
        // clean up the old file when the new one has a different name.
        var previousFileName = (await LoadAsync())
            .FirstOrDefault(i => i.ServerId == sup.ServerId && i.ModId == file.ModId)?.FileName;
        var warning = await InstalledJarWriter.WriteAsync(_downloader, downloadUrl, dir, fileName, previousFileName, ct);

        var install = new CurseForgeInstall
        {
            ServerId = sup.ServerId,
            ModId = file.ModId,
            ModName = await _client.GetModNameAsync(apiKey, file.ModId, ct),
            FileId = file.Id,
            FileName = fileName,
            InstalledAt = DateTimeOffset.UtcNow,
        };
        await TrackAsync(install);
        return (install, warning);
    }

    public async Task<List<CurseForgeInstall>> ListInstalledAsync(ServerSupervisor sup)
        => [.. (await LoadAsync()).Where(i => i.ServerId == sup.ServerId)];

    public async Task<bool> RemoveAsync(ServerSupervisor sup, int modId)
    {
        var install = (await ListInstalledAsync(sup)).FirstOrDefault(i => i.ModId == modId);
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
            _installs!.RemoveAll(i => i.ServerId == sup.ServerId && i.ModId == modId);
            await _store.SaveAsync(_installs);
        }
        finally { _gate.Release(); }
        return true;
    }

    private async Task<List<CurseForgeInstall>> LoadAsync()
    {
        await _gate.WaitAsync();
        try { _installs ??= await _store.LoadAsync() ?? []; return _installs; }
        finally { _gate.Release(); }
    }

    private async Task TrackAsync(CurseForgeInstall install)
    {
        await _gate.WaitAsync();
        try
        {
            _installs ??= await _store.LoadAsync() ?? [];
            // A re-install of the same mod (an update, or retrying after a
            // failed dependency) replaces its own tracking entry rather than
            // appending a duplicate.
            _installs.RemoveAll(i => i.ServerId == install.ServerId && i.ModId == install.ModId);
            _installs.Add(install);
            await _store.SaveAsync(_installs);
        }
        finally { _gate.Release(); }
    }
}
