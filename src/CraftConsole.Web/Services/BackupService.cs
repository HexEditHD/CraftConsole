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
