using CraftConsole.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace CraftConsole.Tests.Web;

public class RconSecretStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SettingsHolder _settings;

    public RconSecretStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-rcon-secrets-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _settings = new SettingsHolder(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A file-backed provider needs no DI container. Instances built
    /// against the same key-ring folder can decrypt each other's output; those
    /// built against different folders cannot — used below to simulate a key
    /// ring that has changed out from under a stored secret.</summary>
    private RconSecretStore NewStore(string keyRingFolder = "dpkeys")
        => new(_settings, DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(_dir, keyRingFolder))));

    [Fact]
    public async Task An_unset_profile_has_no_password()
    {
        var store = NewStore();
        var id = Guid.NewGuid();

        Assert.False(await store.HasAsync(id));
        Assert.Null(await store.TryGetAsync(id));
    }

    [Fact]
    public async Task Set_then_get_round_trips_the_password()
    {
        var store = NewStore();
        var id = Guid.NewGuid();

        await store.SetAsync(id, "hunter2");

        Assert.True(await store.HasAsync(id));
        Assert.Equal("hunter2", await store.TryGetAsync(id));
    }

    [Fact]
    public async Task Round_trips_across_instances_sharing_the_same_key_ring()
    {
        var id = Guid.NewGuid();
        await NewStore().SetAsync(id, "hunter2");

        var reloaded = NewStore();

        Assert.Equal("hunter2", await reloaded.TryGetAsync(id));
    }

    [Fact]
    public async Task Setting_again_replaces_the_previous_password()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.SetAsync(id, "first");

        await store.SetAsync(id, "second");

        Assert.Equal("second", await store.TryGetAsync(id));
    }

    [Fact]
    public async Task Remove_clears_the_password()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        await store.SetAsync(id, "hunter2");

        await store.RemoveAsync(id);

        Assert.False(await store.HasAsync(id));
        Assert.Null(await store.TryGetAsync(id));
    }

    [Fact]
    public async Task Removing_an_unset_profile_is_a_no_op()
    {
        var store = NewStore();

        await store.RemoveAsync(Guid.NewGuid()); // must not throw
    }

    [Fact]
    public async Task Different_profiles_do_not_collide()
    {
        var store = NewStore();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await store.SetAsync(a, "password-a");
        await store.SetAsync(b, "password-b");

        Assert.Equal("password-a", await store.TryGetAsync(a));
        Assert.Equal("password-b", await store.TryGetAsync(b));
    }

    [Fact]
    public async Task A_password_that_cannot_be_decrypted_returns_null_instead_of_throwing()
    {
        var id = Guid.NewGuid();
        await NewStore("dpkeys-a").SetAsync(id, "hunter2");

        // Same rcon-secrets.json (both stores share _dir), but this instance's
        // protector was built from a different key ring, so Unprotect fails.
        var withDifferentKeyRing = NewStore("dpkeys-b");

        Assert.Null(await withDifferentKeyRing.TryGetAsync(id));
    }
}
