using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraftConsole.Web.Api;
using CraftConsole.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CraftConsole.Tests.Web;

/// <summary>
/// PlayersApi rejects a Target/Reason containing control characters before it
/// ever reaches SendCommandAsync — see ServerSupervisorTests for the
/// lower-level guard this backs up. HTTP-level, mirroring
/// ServerApiConflictTests, because the interesting bug lived at the route
/// mapping call site (whitelist add/remove build the command string before
/// RunWhitelistCommand runs any check), which a unit test calling PlayersApi's
/// own helpers directly would not exercise.
/// </summary>
[Collection(nameof(WebAppFactoryCollection))]
public sealed class PlayersApiValidationTests : IAsyncDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public PlayersApiValidationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "cc-players-validation-test-" + Guid.NewGuid());
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

    private async Task<Guid> CreateProfileAsync()
    {
        // Deliberately never started — proves these checks do not depend on a
        // running (or even fake) server process.
        var dir = Path.Combine(_dataDir, "server-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var res = await _client.PostAsJsonAsync("/api/profiles", new
        {
            name = "A", jarPath = Path.Combine(dir, "server.jar"), workingDirectory = dir,
        });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetGuid();
    }

    public static IEnumerable<object[]> KickBanRoutes()
    {
        yield return ["kick"];
        yield return ["ban"];
        yield return ["ban-ip"];
        yield return ["pardon"];
        yield return ["pardon-ip"];
    }

    [Theory]
    [MemberData(nameof(KickBanRoutes))]
    public async Task A_target_containing_a_line_break_is_rejected(string verb)
    {
        var id = await CreateProfileAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/servers/{id}/players/{verb}", new { target = "Steve\nop Steve" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_kick_reason_containing_a_line_break_is_rejected()
    {
        var id = await CreateProfileAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/servers/{id}/players/kick", new { target = "Steve", reason = "AFK\nop Steve" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task A_whitelist_target_with_a_line_break_is_rejected_even_with_no_server_running(string action)
    {
        var id = await CreateProfileAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/servers/{id}/players/whitelist/{action}", new { target = "Steve\nop Steve" });

        // If the control-character check ran after the "must be running" guard
        // this would come back 400 for the wrong reason (or worse, later on a
        // running server, not at all) — pin the message to prove which guard fired.
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("Player name contains invalid characters.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_ordinary_target_is_not_caught_by_the_new_guard()
    {
        var id = await CreateProfileAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/servers/{id}/players/kick", new { target = "Steve", reason = "AFK" });

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }
}
