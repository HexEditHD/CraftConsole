using System.Net.Http.Json;
using System.Text.Json;
using CraftConsole.Web.Api;
using CraftConsole.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CraftConsole.Tests.Web;

/// <summary>
/// HTTP-level coverage for /api/servers' cross-profile port and
/// working-directory conflict detection — see CapabilityGatingTests' own doc
/// comment for why this needs the real pipeline rather than a unit test
/// against ServerApi's private helpers.
/// </summary>
[Collection(nameof(WebAppFactoryCollection))]
public sealed class ServerApiConflictTests : IAsyncDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ServerApiConflictTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "cc-server-conflict-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable(DataPath.EnvironmentVariable, _dataDir);

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var auth = _factory.Services.GetRequiredService<AuthService>();
        auth.SetupAdminAsync("test-password-not-real").GetAwaiter().GetResult();
        var token = auth.CreateSession(auth.ListUsers()[0].Id);
        _client.DefaultRequestHeaders.Add("Cookie", $"{AuthApi.CookieName}={token}");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        Environment.SetEnvironmentVariable(DataPath.EnvironmentVariable, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort */ }
    }

    private string NewServerDir()
    {
        var dir = Path.Combine(_dataDir, "server-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<Guid> CreateProfileAsync(object body)
    {
        var res = await _client.PostAsJsonAsync("/api/profiles", body);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateManagedProfileAsync(string name, string workingDirectory, int? serverPort = null)
    {
        if (serverPort is not null)
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "server.properties"), $"server-port={serverPort}\n");

        return await CreateProfileAsync(new
        {
            name, jarPath = Path.Combine(workingDirectory, "server.jar"), workingDirectory,
        });
    }

    private async Task<JsonElement> GetServersAsync()
        => (await (await _client.GetAsync("/api/servers")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("servers");

    private static JsonElement Find(JsonElement servers, Guid id)
        => servers.EnumerateArray().First(s => s.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task Two_fresh_profiles_with_no_server_properties_yet_both_default_to_25565_and_conflict()
    {
        // server.properties doesn't exist until Minecraft's first launch — the
        // normal state for a just-created profile. Without a default, this pair
        // would silently pass as "no conflict" even though both bind 25565.
        var a = await CreateManagedProfileAsync("A", NewServerDir());
        var b = await CreateManagedProfileAsync("B", NewServerDir());

        var servers = await GetServersAsync();

        Assert.True(Find(servers, a).GetProperty("portConflict").GetBoolean());
        Assert.True(Find(servers, b).GetProperty("portConflict").GetBoolean());
    }

    [Fact]
    public async Task Explicit_differing_ports_do_not_conflict()
    {
        var a = await CreateManagedProfileAsync("A", NewServerDir(), serverPort: 25565);
        var b = await CreateManagedProfileAsync("B", NewServerDir(), serverPort: 25566);

        var servers = await GetServersAsync();

        Assert.False(Find(servers, a).GetProperty("portConflict").GetBoolean());
        Assert.False(Find(servers, b).GetProperty("portConflict").GetBoolean());
    }

    [Fact]
    public async Task Rcon_profiles_are_never_flagged_for_port_or_directory_conflicts()
    {
        var a = await CreateProfileAsync(new { name = "A", mode = "Rcon", rconHost = "127.0.0.1", rconPort = 25575 });
        var b = await CreateProfileAsync(new { name = "B", mode = "Rcon", rconHost = "127.0.0.1", rconPort = 25575 });

        var servers = await GetServersAsync();

        Assert.False(Find(servers, a).GetProperty("portConflict").GetBoolean());
        Assert.False(Find(servers, b).GetProperty("portConflict").GetBoolean());
        Assert.False(Find(servers, a).GetProperty("workingDirectoryConflict").GetBoolean());
        Assert.False(Find(servers, b).GetProperty("workingDirectoryConflict").GetBoolean());
    }

    [Fact]
    public async Task Two_managed_profiles_sharing_a_working_directory_are_flagged()
    {
        // Sharing a directory means sharing server.properties too — world
        // corruption, strictly worse than a port clash. The point of this test
        // is the directory flag specifically, so it doesn't assert on port.
        var dir = NewServerDir();
        var a = await CreateManagedProfileAsync("A", dir, serverPort: 25565);
        var b = await CreateManagedProfileAsync("B", dir);

        var servers = await GetServersAsync();

        Assert.True(Find(servers, a).GetProperty("workingDirectoryConflict").GetBoolean());
        Assert.True(Find(servers, b).GetProperty("workingDirectoryConflict").GetBoolean());
    }

    [Fact]
    public async Task Two_managed_profiles_in_different_directories_are_not_flagged()
    {
        var a = await CreateManagedProfileAsync("A", NewServerDir(), serverPort: 25565);
        var b = await CreateManagedProfileAsync("B", NewServerDir(), serverPort: 25566);

        var servers = await GetServersAsync();

        Assert.False(Find(servers, a).GetProperty("workingDirectoryConflict").GetBoolean());
        Assert.False(Find(servers, b).GetProperty("workingDirectoryConflict").GetBoolean());
    }

    // ── Changing the port ────────────────────────────────────────────────

    [Fact]
    public async Task Changing_the_port_clears_a_conflict_between_two_profiles()
    {
        var a = await CreateManagedProfileAsync("A", NewServerDir(), serverPort: 25565);
        var b = await CreateManagedProfileAsync("B", NewServerDir(), serverPort: 25565);
        Assert.True(Find(await GetServersAsync(), a).GetProperty("portConflict").GetBoolean());

        var change = await _client.PutAsJsonAsync($"/api/servers/{b}/server-port", new { port = 25566 });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, change.StatusCode);

        var servers = await GetServersAsync();
        Assert.False(Find(servers, a).GetProperty("portConflict").GetBoolean());
        Assert.False(Find(servers, b).GetProperty("portConflict").GetBoolean());
        Assert.Equal(25566, Find(servers, b).GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task Changing_the_port_preserves_the_rest_of_server_properties()
    {
        var dir = NewServerDir();
        var a = await CreateManagedProfileAsync("A", dir, serverPort: 25565);
        await File.AppendAllTextAsync(Path.Combine(dir, "server.properties"), "motd=Hello world\n");

        await _client.PutAsJsonAsync($"/api/servers/{a}/server-port", new { port = 25580 });

        var text = await File.ReadAllTextAsync(Path.Combine(dir, "server.properties"));
        Assert.Contains("motd=Hello world", text);
        Assert.Contains("server-port=25580", text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(-1)]
    public async Task Changing_the_port_rejects_an_out_of_range_value(int port)
    {
        var a = await CreateManagedProfileAsync("A", NewServerDir(), serverPort: 25565);

        var res = await _client.PutAsJsonAsync($"/api/servers/{a}/server-port", new { port });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Changing_the_port_is_rejected_for_an_rcon_profile()
    {
        var a = await CreateProfileAsync(new { name = "A", mode = "Rcon", rconHost = "127.0.0.1", rconPort = 25575 });

        var res = await _client.PutAsJsonAsync($"/api/servers/{a}/server-port", new { port = 25566 });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Changing_the_port_for_an_unknown_profile_returns_not_found()
    {
        var res = await _client.PutAsJsonAsync($"/api/servers/{Guid.NewGuid()}/server-port", new { port = 25566 });

        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
    }
}
