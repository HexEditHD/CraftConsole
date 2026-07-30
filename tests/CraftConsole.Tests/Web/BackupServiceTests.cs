using System.IO.Compression;
using System.Text;
using CraftConsole.Core.Models;
using CraftConsole.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CraftConsole.Tests.Web;

public class BackupServiceTests : IDisposable
{
    private readonly string _root;
    private readonly EventBroker _broker = new();
    private readonly BackupService _backups;

    public BackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-backup-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
        _backups = new BackupService(
            _broker, new SettingsHolder(DataDir), NullLogger<BackupService>.Instance);
    }

    private string DataDir => Path.Combine(_root, "appdata");
    private string SourceDir => Path.Combine(_root, "server");
    private string DestDir => Path.Combine(_root, "backups");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void SeedSource()
    {
        Directory.CreateDirectory(Path.Combine(SourceDir, "world", "region"));
        File.WriteAllText(Path.Combine(SourceDir, "server.properties"), "max-players=20\n");
        File.WriteAllText(Path.Combine(SourceDir, "world", "level.dat"), "level");
        File.WriteAllText(Path.Combine(SourceDir, "world", "region", "r.0.0.mca"), "region");
    }

    private BackupJob NewJob(string name = "World") => new()
    {
        Name = name,
        SourcePaths = [Path.Combine(SourceDir, "world"), Path.Combine(SourceDir, "server.properties")],
        DestinationPath = DestDir,
        Compression = CompressionLevel.Fastest,
    };

    // ── CRUD and persistence ──────────────────────────────────────────────

    [Fact]
    public async Task Jobs_round_trip_through_disk()
    {
        var job = await _backups.AddAsync(NewJob());

        // A second instance over the same folder must see it.
        var reloaded = new BackupService(
            _broker, new SettingsHolder(DataDir), NullLogger<BackupService>.Instance);

        var jobs = await reloaded.SnapshotAsync();

        Assert.Single(jobs);
        Assert.Equal(job.Id, jobs[0].Id);
        Assert.Equal("World", jobs[0].Name);
    }

    [Fact]
    public async Task Update_and_delete_report_whether_the_job_existed()
    {
        var job = await _backups.AddAsync(NewJob());

        Assert.True(await _backups.UpdateAsync(job.Id, NewJob("Renamed")));
        Assert.False(await _backups.UpdateAsync(Guid.NewGuid(), NewJob()));

        Assert.Equal("Renamed", (await _backups.SnapshotAsync())[0].Name);

        Assert.True(await _backups.DeleteAsync(job.Id));
        Assert.False(await _backups.DeleteAsync(job.Id));
        Assert.Empty(await _backups.SnapshotAsync());
    }

    // ── Running a backup ──────────────────────────────────────────────────

    [Fact]
    public async Task Running_a_job_writes_an_archive_containing_every_source()
    {
        SeedSource();
        var job = await _backups.AddAsync(NewJob());

        Assert.True(await _backups.RunAsync(job.Id));
        await WaitForArchiveAsync();

        var archive = Directory.GetFiles(DestDir, "*.zip").Single();
        using var zip = ZipFile.OpenRead(archive);
        var entries = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("server.properties", entries);
        Assert.Contains("world/level.dat", entries);
        // Nested directories keep forward slashes regardless of host separator.
        Assert.Contains("world/region/r.0.0.mca", entries);
        Assert.DoesNotContain(entries, e => e.Contains('\\'));
    }

    [Fact]
    public async Task Sources_that_do_not_exist_are_skipped_rather_than_failing_the_run()
    {
        SeedSource();
        var job = NewJob();
        job.SourcePaths = [.. job.SourcePaths, Path.Combine(SourceDir, "does-not-exist")];
        var added = await _backups.AddAsync(job);

        Assert.True(await _backups.RunAsync(added.Id));
        await WaitForArchiveAsync();

        Assert.Single(Directory.GetFiles(DestDir, "*.zip"));
    }

    // ── Listing archives ──────────────────────────────────────────────────

    [Fact]
    public async Task Listing_archives_returns_null_for_an_unknown_job_and_empty_before_any_run()
    {
        Assert.Null(await _backups.ListArchivesAsync(Guid.NewGuid()));

        var job = await _backups.AddAsync(NewJob());
        Assert.Empty((await _backups.ListArchivesAsync(job.Id))!);
    }

    [Fact]
    public async Task Listing_archives_reports_name_and_size()
    {
        SeedSource();
        var job = await _backups.AddAsync(NewJob());
        await _backups.RunAsync(job.Id);
        await WaitForArchiveAsync();

        var archives = (await _backups.ListArchivesAsync(job.Id))!;

        Assert.Single(archives);
        Assert.StartsWith("World_", archives[0].FileName);
        Assert.EndsWith(".zip", archives[0].FileName);
        Assert.True(archives[0].SizeBytes > 0);
    }

    // ── Restore ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_puts_the_files_back()
    {
        SeedSource();
        var job = await _backups.AddAsync(NewJob());
        await _backups.RunAsync(job.Id);
        await WaitForArchiveAsync();

        // Lose the world, then restore it.
        Directory.Delete(Path.Combine(SourceDir, "world"), recursive: true);
        Assert.False(File.Exists(Path.Combine(SourceDir, "world", "level.dat")));

        var archive = (await _backups.ListArchivesAsync(job.Id))![0].FileName;
        await _backups.RestoreAsync(job.Id, archive, SourceDir);

        Assert.Equal("level", File.ReadAllText(Path.Combine(SourceDir, "world", "level.dat")));
        Assert.Equal("region", File.ReadAllText(Path.Combine(SourceDir, "world", "region", "r.0.0.mca")));
    }

    [Fact]
    public async Task Restore_overwrites_matching_files_but_leaves_others_alone()
    {
        SeedSource();
        var job = await _backups.AddAsync(NewJob());
        await _backups.RunAsync(job.Id);
        await WaitForArchiveAsync();

        File.WriteAllText(Path.Combine(SourceDir, "world", "level.dat"), "modified");
        File.WriteAllText(Path.Combine(SourceDir, "world", "unrelated.txt"), "keep me");

        var archive = (await _backups.ListArchivesAsync(job.Id))![0].FileName;
        await _backups.RestoreAsync(job.Id, archive, SourceDir);

        Assert.Equal("level", File.ReadAllText(Path.Combine(SourceDir, "world", "level.dat")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(SourceDir, "world", "unrelated.txt")));
    }

    [Theory]
    [InlineData("../escape.zip")]
    [InlineData("sub/escape.zip")]
    [InlineData("sub\\escape.zip")]
    public async Task Restore_rejects_archive_names_that_try_to_leave_the_job_folder(string name)
    {
        var job = await _backups.AddAsync(NewJob());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backups.RestoreAsync(job.Id, name, SourceDir));

        Assert.Contains("Invalid archive name", ex.Message);
    }

    [Fact]
    public async Task Restore_reports_a_missing_archive_clearly()
    {
        var job = await _backups.AddAsync(NewJob());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backups.RestoreAsync(job.Id, "nope.zip", SourceDir));

        Assert.Contains("was not found", ex.Message);
    }

    [Fact]
    public void Extract_refuses_an_entry_that_would_escape_the_target_directory()
    {
        // Zip slip: an entry named ../../pwned lands outside the target when a
        // naive extractor joins it to the destination path.
        var malicious = Path.Combine(_root, "malicious.zip");
        using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../../pwned.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("owned");
        }

        var target = Path.Combine(_root, "extract-here");

        var ex = Assert.Throws<InvalidOperationException>(
            () => BackupService.ExtractArchive(malicious, target));

        Assert.Contains("outside the target directory", ex.Message);
        Assert.False(File.Exists(Path.Combine(_root, "pwned.txt")));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_root, "..", "pwned.txt"))));
    }

    /// <summary>RunAsync reports completion over the broker, not by awaiting.</summary>
    private async Task WaitForArchiveAsync(int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(DestDir) && Directory.GetFiles(DestDir, "*.zip").Length > 0)
            {
                await Task.Delay(60); // let the handle close before reading
                return;
            }
            await Task.Delay(25);
        }

        throw new TimeoutException($"No archive appeared in {DestDir}.");
    }
}
