using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraftConsole.Tests.Rcon;
using CraftConsole.Web.Api;
using CraftConsole.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CraftConsole.Tests.Web;

/// <summary>
/// The first HTTP-level test in the repo. Program.cs declares
/// <c>public partial class Program</c> specifically so WebApplicationFactory
/// can host it; nothing exercised this path before. These tests hit the real
/// pipeline — auth gate, minimal API routing, JSON options — to catch what a
/// unit test calling ServerSupervisor/ProfilesService directly structurally
/// cannot: a wrong route, a field lost in serialization, a status code that
/// doesn't match what the frontend expects.
///
/// CRAFTCONSOLE_DATA is a process-wide environment variable (see DataPath),
/// so these tests must not run concurrently with each other or with anything
/// else that touches it.
/// </summary>
[Collection(nameof(WebAppFactoryCollection))]
public sealed class CapabilityGatingTests : IAsyncDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public CapabilityGatingTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "cc-webapp-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable(DataPath.EnvironmentVariable, _dataDir);

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // The auth gate only checks for a valid session token (see Program.cs) —
        // minting one directly skips the loopback-only /api/auth/setup dance,
        // which a WebApplicationFactory's TestServer connection doesn't satisfy.
        var auth = _factory.Services.GetRequiredService<AuthService>();
        var token = auth.CreateSession();
        _client.DefaultRequestHeaders.Add("Cookie", $"{AuthApi.CookieName}={token}");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        Environment.SetEnvironmentVariable(DataPath.EnvironmentVariable, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private async Task<Guid> CreateProfileAsync(object body)
    {
        var res = await _client.PostAsJsonAsync("/api/profiles", body);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        using var anonymous = _factory.CreateClient();

        var res = await anonymous.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task With_no_active_profile_the_file_endpoints_report_unavailable()
    {
        var tree = await (await _client.GetAsync("/api/files/tree")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(tree.GetProperty("available").GetBoolean());
        Assert.Equal("No server has been started yet.", tree.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Activating_a_profile_without_starting_it_does_not_change_availability()
    {
        // ServerSupervisor.Capabilities and LocalFileUnavailableReason read its
        // own ActiveProfile, set only by StartAsync — not ProfilesService's
        // notion of "active" (settings.ActiveProfileId). Marking a profile
        // active without starting it must not flip file availability.
        var profileId = await CreateProfileAsync(new
        {
            name = "Local", jarPath = @"C:\nonexistent\server.jar", workingDirectory = @"C:\nonexistent",
        });

        var activate = await _client.PostAsync($"/api/profiles/{profileId}/activate", null);
        Assert.True(activate.IsSuccessStatusCode);

        var tree = await (await _client.GetAsync("/api/files/tree")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(tree.GetProperty("available").GetBoolean());
        Assert.Equal("No server has been started yet.", tree.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_connected_rcon_server_reports_files_and_plugins_unavailable_with_a_reason()
    {
        await using var fake = new FakeRconServer("hunter2");
        var profileId = await CreateProfileAsync(new
        {
            name = "Remote", mode = "Rcon", rconHost = "127.0.0.1", rconPort = fake.Port,
        });
        await _client.PutAsJsonAsync($"/api/profiles/{profileId}/rcon-password", new { password = "hunter2" });

        var start = await _client.PostAsJsonAsync("/api/server/start", new { profileId });
        Assert.True(start.IsSuccessStatusCode, await start.Content.ReadAsStringAsync());

        var tree = await (await _client.GetAsync("/api/files/tree")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(tree.GetProperty("available").GetBoolean());
        Assert.Contains("RCON", tree.GetProperty("reason").GetString());

        var plugins = await (await _client.GetAsync("/api/plugins")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(plugins.GetProperty("available").GetBoolean());

        var banned = await (await _client.GetAsync("/api/players/banned")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(banned.GetProperty("available").GetBoolean());
        Assert.Empty(banned.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task A_connected_rcon_server_reports_the_right_capability_flags()
    {
        await using var fake = new FakeRconServer("hunter2");
        var profileId = await CreateProfileAsync(new
        {
            name = "Remote", mode = "Rcon", rconHost = "127.0.0.1", rconPort = fake.Port,
        });
        await _client.PutAsJsonAsync($"/api/profiles/{profileId}/rcon-password", new { password = "hunter2" });
        await _client.PostAsJsonAsync("/api/server/start", new { profileId });

        var status = await (await _client.GetAsync("/api/status")).Content.ReadFromJsonAsync<JsonElement>();
        var caps = status.GetProperty("capabilities");

        Assert.True(caps.GetProperty("canStart").GetBoolean());
        Assert.True(caps.GetProperty("canStop").GetBoolean());
        Assert.False(caps.GetProperty("canRestart").GetBoolean());
        Assert.False(caps.GetProperty("hasConsoleStream").GetBoolean());
        Assert.False(caps.GetProperty("hasLocalFiles").GetBoolean());
        Assert.False(caps.GetProperty("hasUptime").GetBoolean());
    }

    [Fact]
    public async Task Restarting_an_rcon_connection_is_rejected_with_a_clear_message()
    {
        await using var fake = new FakeRconServer("hunter2");
        var profileId = await CreateProfileAsync(new
        {
            name = "Remote", mode = "Rcon", rconHost = "127.0.0.1", rconPort = fake.Port,
        });
        await _client.PutAsJsonAsync($"/api/profiles/{profileId}/rcon-password", new { password = "hunter2" });
        await _client.PostAsJsonAsync("/api/server/start", new { profileId });

        var restart = await _client.PostAsync("/api/server/restart", null);

        Assert.Equal(HttpStatusCode.BadRequest, restart.StatusCode);
        var body = await restart.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("RCON", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_rcon_password_never_appears_in_the_profiles_response()
    {
        var profileId = await CreateProfileAsync(new
        {
            name = "Remote", mode = "Rcon", rconHost = "127.0.0.1", rconPort = 25575,
        });
        await _client.PutAsJsonAsync($"/api/profiles/{profileId}/rcon-password", new { password = "hunter2-secret" });

        var raw = await (await _client.GetAsync("/api/profiles")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("hunter2-secret", raw);
        var profile = JsonDocument.Parse(raw).RootElement.GetProperty("profiles")[0];
        Assert.True(profile.GetProperty("hasRconPassword").GetBoolean());
    }

    [Fact]
    public async Task Creating_a_managed_profile_without_a_jar_path_is_rejected()
    {
        var res = await _client.PostAsJsonAsync("/api/profiles", new { name = "Local" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}

[CollectionDefinition(nameof(WebAppFactoryCollection), DisableParallelization = true)]
public sealed class WebAppFactoryCollection;
