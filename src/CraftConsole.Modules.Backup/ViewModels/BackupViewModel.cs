using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftConsole.Core.Models;

namespace CraftConsole.Modules.Backup.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private readonly string _appDataPath;
    private string _jobsFile => Path.Combine(_appDataPath, "backups.json");

    public ObservableCollection<BackupJob> Jobs { get; } = [];

    // ── Add/Edit form ────────────────────────────────────────────────────
    [ObservableProperty] private bool _isAddingNew;
    [ObservableProperty] private string _formName = string.Empty;
    [ObservableProperty] private string _formSourcePaths = string.Empty;
    [ObservableProperty] private string _formDestination = string.Empty;
    [ObservableProperty] private CompressionLevel _formCompression = CompressionLevel.Optimal;

    private BackupJob? _editingJob;

    public static CompressionLevel[] AllLevels =>
        [CompressionLevel.Optimal, CompressionLevel.Fastest, CompressionLevel.NoCompression];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public BackupViewModel(string appDataPath)
    {
        _appDataPath = appDataPath;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (!File.Exists(_jobsFile)) return;
        try
        {
            await using var stream = File.OpenRead(_jobsFile);
            var list = await JsonSerializer.DeserializeAsync<List<BackupJob>>(stream, JsonOptions);
            if (list is null) return;
            foreach (var job in list) Jobs.Add(job);
        }
        catch { /* ignore */ }
    }

    private async Task SaveAsync()
    {
        Directory.CreateDirectory(_appDataPath);
        await using var stream = File.Create(_jobsFile);
        await JsonSerializer.SerializeAsync(stream, Jobs.ToList(), JsonOptions);
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void StartAdd()
    {
        _editingJob = null;
        FormName = string.Empty;
        FormSourcePaths = string.Empty;
        FormDestination = string.Empty;
        FormCompression = CompressionLevel.Optimal;
        IsAddingNew = true;
    }

    [RelayCommand]
    private void EditJob(BackupJob job)
    {
        _editingJob = job;
        FormName = job.Name;
        FormSourcePaths = string.Join(";", job.SourcePaths);
        FormDestination = job.DestinationPath;
        FormCompression = job.Compression;
        IsAddingNew = true;
    }

    [RelayCommand]
    private void CancelAdd() => IsAddingNew = false;

    [RelayCommand]
    private async Task SaveJobAsync()
    {
        if (string.IsNullOrWhiteSpace(FormName)) return;

        var sources = FormSourcePaths
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (_editingJob is not null)
        {
            _editingJob.Name           = FormName;
            _editingJob.SourcePaths    = sources;
            _editingJob.DestinationPath = FormDestination;
            _editingJob.Compression    = FormCompression;
        }
        else
        {
            Jobs.Add(new BackupJob
            {
                Name            = FormName,
                SourcePaths     = sources,
                DestinationPath = FormDestination,
                Compression     = FormCompression,
            });
        }

        IsAddingNew = false;
        await SaveAsync();
    }

    [RelayCommand]
    private async Task DeleteJobAsync(BackupJob job)
    {
        Jobs.Remove(job);
        await SaveAsync();
    }

    [RelayCommand]
    private async Task RunJobAsync(BackupJob job)
    {
        await Task.Run(() => ExecuteBackup(job));
        job.LastRun = DateTimeOffset.UtcNow;
        await SaveAsync();
    }

    private static void ExecuteBackup(BackupJob job)
    {
        Directory.CreateDirectory(job.DestinationPath);
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var zipPath = Path.Combine(job.DestinationPath, $"{job.Name}_{timestamp}.zip");

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var source in job.SourcePaths)
        {
            if (File.Exists(source))
            {
                archive.CreateEntryFromFile(source, Path.GetFileName(source), job.Compression);
            }
            else if (Directory.Exists(source))
            {
                AddDirectory(archive, source, Path.GetFileName(source.TrimEnd('/', '\\')), job.Compression);
            }
        }
    }

    private static void AddDirectory(ZipArchive archive, string directory, string entryRoot, CompressionLevel compression)
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file);
            var entryName = Path.Combine(entryRoot, relative).Replace('\\', '/');
            archive.CreateEntryFromFile(file, entryName, compression);
        }
    }
}
