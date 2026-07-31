using CraftConsole.Core.Models;
using CraftConsole.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CraftConsole.Tests.Web;

public class ProfilesServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly SettingsHolder _settings;
    private readonly RconSecretStore _secrets;
    private readonly ProfilesService _profiles;

    public ProfilesServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-profiles-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _settings = new SettingsHolder(_dir);
        _secrets = NewSecrets();
        _profiles = new ProfilesService(_settings, _secrets);
    }

    /// <summary>
    /// A file-backed IDataProtectionProvider needs no DI container. Every call
    /// points at the same physical key folder under _dir, so instances built
    /// this way — including "reloaded" ones in the round-trip tests below —
    /// can decrypt what an earlier instance encrypted.
    /// </summary>
    private RconSecretStore NewSecrets() => new(
        new SettingsHolder(_dir),
        Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(_dir, "dpkeys"))),
        NullLogger<RconSecretStore>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ServerProfile NewProfile(string name = "Survival") => new()
    {
        Name = name,
        JarPath = $@"C:\servers\{name}\paper.jar",
        WorkingDirectory = $@"C:\servers\{name}",
        MinRamMb = 1024,
        MaxRamMb = 4096,
        Type = ServerType.Paper,
    };

    private static ServerProfile NewRconProfile(string name = "Remote") => new()
    {
        Name = name,
        Mode = ConnectionMode.Rcon,
        RconHost = "192.168.1.50",
        RconPort = 25575,
    };

    [Fact]
    public async Task Starts_empty()
    {
        Assert.Empty(await _profiles.ListAsync());
        Assert.Null(await _profiles.GetActiveAsync());
    }

    [Fact]
    public async Task Profiles_round_trip_through_disk()
    {
        var added = await _profiles.AddAsync(NewProfile());

        var reloaded = new ProfilesService(new SettingsHolder(_dir), NewSecrets());
        var list = await reloaded.ListAsync();

        Assert.Single(list);
        Assert.Equal(added.Id, list[0].Id);
        Assert.Equal("Survival", list[0].Name);
        Assert.Equal(4096, list[0].MaxRamMb);
    }

    [Fact]
    public async Task The_first_profile_added_becomes_active()
    {
        var first = await _profiles.AddAsync(NewProfile("First"));
        var second = await _profiles.AddAsync(NewProfile("Second"));

        var active = await _profiles.GetActiveAsync();

        Assert.Equal(first.Id, active!.Id);
        Assert.NotEqual(second.Id, active.Id);
    }

    [Fact]
    public async Task Setting_a_profile_active_persists_across_instances()
    {
        await _profiles.AddAsync(NewProfile("First"));
        var second = await _profiles.AddAsync(NewProfile("Second"));

        await _profiles.SetActiveAsync(second.Id);

        var reloaded = new ProfilesService(new SettingsHolder(_dir), NewSecrets());
        Assert.Equal(second.Id, (await reloaded.GetActiveAsync())!.Id);
    }

    [Fact]
    public async Task Update_replaces_the_fields_but_keeps_the_id()
    {
        var profile = await _profiles.AddAsync(NewProfile());

        var replacement = NewProfile("Renamed");
        replacement.MaxRamMb = 8192;

        Assert.True(await _profiles.UpdateAsync(profile.Id, replacement));

        var stored = await _profiles.GetAsync(profile.Id);
        Assert.Equal("Renamed", stored!.Name);
        Assert.Equal(8192, stored.MaxRamMb);
        Assert.Equal(profile.Id, stored.Id);
    }

    [Fact]
    public async Task Update_and_delete_report_false_for_an_unknown_id()
    {
        Assert.False(await _profiles.UpdateAsync(Guid.NewGuid(), NewProfile()));
        Assert.False(await _profiles.DeleteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Deleting_the_active_profile_falls_back_to_another_one()
    {
        var first = await _profiles.AddAsync(NewProfile("First"));
        var second = await _profiles.AddAsync(NewProfile("Second"));
        await _profiles.SetActiveAsync(second.Id);

        Assert.True(await _profiles.DeleteAsync(second.Id));

        // The stored active id is cleared, so the first remaining profile is used.
        var active = await _profiles.GetActiveAsync();
        Assert.Equal(first.Id, active!.Id);
    }

    [Fact]
    public async Task An_active_id_pointing_at_a_deleted_profile_falls_back_rather_than_returning_null()
    {
        var profile = await _profiles.AddAsync(NewProfile("Kept"));

        // Simulate a stale pointer, e.g. profiles.json edited by hand.
        await _settings.UpdateAsync(s => s.ActiveProfileId = Guid.NewGuid().ToString());

        Assert.Equal(profile.Id, (await _profiles.GetActiveAsync())!.Id);
    }

    [Fact]
    public async Task A_malformed_active_id_falls_back_rather_than_throwing()
    {
        var profile = await _profiles.AddAsync(NewProfile("Kept"));

        await _settings.UpdateAsync(s => s.ActiveProfileId = "not-a-guid");

        Assert.Equal(profile.Id, (await _profiles.GetActiveAsync())!.Id);
    }

    [Fact]
    public async Task Deleting_the_last_profile_leaves_no_active_profile()
    {
        var profile = await _profiles.AddAsync(NewProfile());

        Assert.True(await _profiles.DeleteAsync(profile.Id));

        Assert.Empty(await _profiles.ListAsync());
        Assert.Null(await _profiles.GetActiveAsync());
    }

    // ── Per-mode validation ──────────────────────────────────────────────

    [Fact]
    public async Task A_profile_without_a_name_is_rejected()
    {
        var profile = NewProfile();
        profile.Name = "  ";

        await Assert.ThrowsAsync<InvalidOperationException>(() => _profiles.AddAsync(profile));
    }

    [Fact]
    public async Task A_managed_profile_without_a_jar_path_is_rejected()
    {
        var profile = NewProfile();
        profile.JarPath = "";

        await Assert.ThrowsAsync<InvalidOperationException>(() => _profiles.AddAsync(profile));
    }

    [Fact]
    public async Task An_rcon_profile_needs_no_jar_path()
    {
        var added = await _profiles.AddAsync(NewRconProfile());

        Assert.Equal(ConnectionMode.Rcon, added.Mode);
        Assert.Empty(added.JarPath);
    }

    [Fact]
    public async Task An_rcon_profile_without_a_host_is_rejected()
    {
        var profile = NewRconProfile();
        profile.RconHost = "";

        await Assert.ThrowsAsync<InvalidOperationException>(() => _profiles.AddAsync(profile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task An_rcon_profile_with_a_port_outside_1_to_65535_is_rejected(int port)
    {
        var profile = NewRconProfile();
        profile.RconPort = port;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _profiles.AddAsync(profile));
    }

    // ── Editing across modes ─────────────────────────────────────────────

    /// <summary>
    /// UpdateAsync copies fields one at a time rather than replacing the stored
    /// object outright, so a field the copy list misses would persist on create
    /// but silently fail to save on every future edit. The base
    /// Update_replaces_the_fields_but_keeps_the_id test only checks Name and
    /// MaxRamMb, which would not catch that — this checks every RCON field.
    /// </summary>
    [Fact]
    public async Task Editing_an_rcon_profile_preserves_every_rcon_field()
    {
        var profile = await _profiles.AddAsync(NewRconProfile());

        var replacement = NewRconProfile("Renamed");
        replacement.RconHost = "10.0.0.7";
        replacement.RconPort = 25580;

        Assert.True(await _profiles.UpdateAsync(profile.Id, replacement));

        var stored = await _profiles.GetAsync(profile.Id);
        Assert.Equal("Renamed", stored!.Name);
        Assert.Equal(ConnectionMode.Rcon, stored.Mode);
        Assert.Equal("10.0.0.7", stored.RconHost);
        Assert.Equal(25580, stored.RconPort);
    }

    [Fact]
    public async Task Editing_a_managed_profile_into_rcon_mode_switches_it_over()
    {
        var profile = await _profiles.AddAsync(NewProfile());

        var replacement = NewRconProfile();
        Assert.True(await _profiles.UpdateAsync(profile.Id, replacement));

        var stored = await _profiles.GetAsync(profile.Id);
        Assert.Equal(ConnectionMode.Rcon, stored!.Mode);
        Assert.Equal("192.168.1.50", stored.RconHost);
        Assert.Equal(25575, stored.RconPort);
    }

    // ── Secret lifecycle ─────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_a_profile_removes_its_rcon_secret_too()
    {
        var profile = await _profiles.AddAsync(NewRconProfile());
        await _secrets.SetAsync(profile.Id, "hunter2");
        Assert.True(await _secrets.HasAsync(profile.Id));

        await _profiles.DeleteAsync(profile.Id);

        Assert.False(await _secrets.HasAsync(profile.Id));
    }
}
