using System.IO.Compression;
using CraftConsole.Core.Models;
using CraftConsole.Infrastructure.Config;

namespace CraftConsole.Web.Services;

/// <summary>
/// Manages backup job definitions (persisted to backups.json) and runs them:
/// each run zips the job's source files/folders into a timestamped archive.
/// </summary>
public sealed class BackupService
{
    private readonly EventBroker _broker;
    private readonly ILogger<BackupService> _log;
    private readonly JsonFileStore<List<BackupJob>> _store;

    private readonly object _lock = new();
    private List<BackupJob>? _jobs;

    public BackupService(EventBroker broker, SettingsHolder settings, ILogger<BackupService> log)
    {
        _broker = broker;
        _log = log;
        _store = new JsonFileStore<List<BackupJob>>(settings.AppDataPath, "backups.json");
    }

    private async Task<List<BackupJob>> JobsAsync()
    {
        if (_jobs is null)
        {
            var loaded = await _store.LoadAsync() ?? [];
            lock (_lock) _jobs ??= loaded;
        }
        return _jobs;
    }

    public async Task<List<BackupJob>> SnapshotAsync()
    {
        var jobs = await JobsAsync();
        lock (_lock) return [.. jobs];
    }

    public async Task<BackupJob> AddAsync(BackupJob job)
    {
        var jobs = await JobsAsync();
        lock (_lock) jobs.Add(job);
        await SaveAndPublishAsync();
        return job;
    }

    public async Task<bool> UpdateAsync(Guid id, BackupJob updated)
    {
        var jobs = await JobsAsync();
        lock (_lock)
        {
            var job = jobs.FirstOrDefault(j => j.Id == id);
            if (job is null) return false;
            job.Name = updated.Name;
            job.SourcePaths = updated.SourcePaths;
            job.DestinationPath = updated.DestinationPath;
            job.Compression = updated.Compression;
        }
        await SaveAndPublishAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var jobs = await JobsAsync();
        lock (_lock)
        {
            var job = jobs.FirstOrDefault(j => j.Id == id);
            if (job is null) return false;
            jobs.Remove(job);
        }
        await SaveAndPublishAsync();
        return true;
    }

    public async Task<bool> RunAsync(Guid id)
    {
        var jobs = await JobsAsync();
        BackupJob? job;
        lock (_lock) job = jobs.FirstOrDefault(j => j.Id == id);
        if (job is null) return false;

        _broker.Publish("backup-run", new { job.Id, job.Name, Phase = "running" });
        try
        {
            var zipPath = await Task.Run(() => ExecuteBackup(job));
            job.LastRun = DateTimeOffset.UtcNow;
            await SaveAndPublishAsync();
            _broker.Publish("backup-run", new { job.Id, job.Name, Phase = "done", ZipPath = zipPath });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Backup job {Name} failed", job.Name);
            _broker.Publish("backup-run", new { job.Id, job.Name, Phase = "error", Message = ex.Message });
        }
        return true;
    }

    // ── Restore ───────────────────────────────────────────────────────────

    public sealed record ArchiveInfo(string FileName, long SizeBytes, DateTimeOffset CreatedAt);

    /// <summary>Archives previously written by a job, newest first.</summary>
    public async Task<List<ArchiveInfo>?> ListArchivesAsync(Guid jobId)
    {
        var jobs = await JobsAsync();
        BackupJob? job;
        lock (_lock) job = jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null) return null;

        try
        {
            if (!Directory.Exists(job.DestinationPath)) return [];

            return [.. new DirectoryInfo(job.DestinationPath)
                .EnumerateFiles($"{job.Name}_*.zip")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new ArchiveInfo(f.Name, f.Length, f.LastWriteTimeUtc))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not list archives for {Job}", job.Name);
            return [];
        }
    }

    /// <summary>
    /// Extracts an archive into <paramref name="targetDirectory"/>, overwriting
    /// files it contains. Existing files not present in the archive are left alone.
    /// </summary>
    public async Task RestoreAsync(Guid jobId, string archiveFileName, string targetDirectory)
    {
        var jobs = await JobsAsync();
        BackupJob? job;
        lock (_lock) job = jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null) throw new InvalidOperationException("That backup job no longer exists.");

        // The archive name comes from the client; keep it inside the job's folder.
        if (archiveFileName.Contains("..")
            || archiveFileName.Contains('/')
            || archiveFileName.Contains('\\'))
            throw new InvalidOperationException("Invalid archive name.");

        var archivePath = Path.Combine(job.DestinationPath, archiveFileName);
        if (!File.Exists(archivePath))
            throw new InvalidOperationException($"Archive \"{archiveFileName}\" was not found.");

        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new InvalidOperationException("A target directory is required.");

        _broker.Publish("backup-restore", new { job.Id, job.Name, Phase = "running", Archive = archiveFileName });
        try
        {
            await Task.Run(() => ExtractArchive(archivePath, targetDirectory));
            _broker.Publish("backup-restore",
                new { job.Id, job.Name, Phase = "done", Archive = archiveFileName, Target = targetDirectory });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore of {Archive} failed", archiveFileName);
            _broker.Publish("backup-restore",
                new { job.Id, job.Name, Phase = "error", Archive = archiveFileName, Message = ex.Message });
            throw new InvalidOperationException($"Restore failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extracts with an explicit containment check on every entry. A crafted
    /// archive can carry entry names like "../../etc/cron.d/x" (zip slip);
    /// ExtractToDirectory guards this in modern .NET, but the check is done
    /// here too because this method writes wherever the operator points it.
    /// </summary>
    internal static void ExtractArchive(string archivePath, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var targetRoot = Path.GetFullPath(targetDirectory);
        if (!targetRoot.EndsWith(Path.DirectorySeparatorChar))
            targetRoot += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            // Directory entries have an empty name and a trailing separator.
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var destination = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName));

            if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Archive entry \"{entry.FullName}\" would be written outside the target directory.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static string ExecuteBackup(BackupJob job)
    {
        Directory.CreateDirectory(job.DestinationPath);
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var zipPath = Path.Combine(job.DestinationPath, $"{job.Name}_{timestamp}.zip");

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var source in job.SourcePaths)
        {
            if (File.Exists(source))
                archive.CreateEntryFromFile(source, Path.GetFileName(source), job.Compression);
            else if (Directory.Exists(source))
                AddDirectory(archive, source, Path.GetFileName(source.TrimEnd('/', '\\')), job.Compression);
        }
        return zipPath;
    }

    private static void AddDirectory(
        ZipArchive archive, string directory, string entryRoot, CompressionLevel compression)
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file);
            var entryName = Path.Combine(entryRoot, relative).Replace('\\', '/');
            archive.CreateEntryFromFile(file, entryName, compression);
        }
    }

    private async Task SaveAndPublishAsync()
    {
        _broker.Publish("backups", new { Jobs = await SnapshotAsync() });
        await _store.SaveAsync(await SnapshotAsync());
    }
}
