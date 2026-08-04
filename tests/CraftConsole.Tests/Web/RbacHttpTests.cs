using System.Net;
using CraftConsole.Web.Api;
using CraftConsole.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CraftConsole.Tests.Web;

/// <summary>
/// HTTP-level RBAC coverage, following CapabilityGatingTests' shape: role
/// enforcement happens in the real auth-gate middleware in Program.cs, not in
/// a unit that can be exercised in isolation.
/// </summary>
[Collection(nameof(WebAppFactoryCollection))]
public sealed class RbacHttpTests : IAsyncDisposable
{
    // Endpoints that intentionally sit outside the gate — see publicAuthPaths in Program.cs.
    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth/status", "/api/auth/setup", "/api/auth/login",
    };

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly AuthService _auth;

    public RbacHttpTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "cc-rbac-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable(DataPath.EnvironmentVariable, _dataDir);

        _factory = new WebApplicationFactory<Program>();
        _auth = _factory.Services.GetRequiredService<AuthService>();
        _auth.SetupAdminAsync("admin-password-not-real").GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        Environment.SetEnvironmentVariable(DataPath.EnvironmentVariable, null);
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private HttpClient ClientFor(Guid userId)
    {
        var client = _factory.CreateClient();
        var token = _auth.CreateSession(userId);
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthApi.CookieName}={token}");
        return client;
    }

    [Fact]
    public async Task An_operator_is_forbidden_from_an_admin_only_endpoint()
    {
        var (_, op) = await _auth.CreateUserAsync("op", "operator-password-123", Role.Operator);
        using var client = ClientFor(op!.Id);

        var res = await client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task An_operator_can_reach_an_operator_tier_endpoint()
    {
        var (_, op) = await _auth.CreateUserAsync("op", "operator-password-123", Role.Operator);
        using var client = ClientFor(op!.Id);

        var res = await client.GetAsync("/api/status");

        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_admin_can_reach_both_tiers()
    {
        var admin = _auth.ListUsers().Single();
        using var client = ClientFor(admin.Id);

        Assert.True((await client.GetAsync("/api/status")).IsSuccessStatusCode);
        Assert.True((await client.GetAsync("/api/settings")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_disabled_users_session_is_rejected_even_though_the_cookie_is_still_valid_looking()
    {
        var (_, op) = await _auth.CreateUserAsync("op", "operator-password-123", Role.Operator);
        using var client = ClientFor(op!.Id);
        await _auth.SetEnabledAsync(op.Id, false);

        var res = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>
    /// Every /api endpoint behind the gate must explicitly declare a role — see
    /// RequiredRoleExtensions.RequireRole. This is the guard against a future
    /// endpoint accidentally shipping without one and defaulting open to any
    /// authenticated user regardless of intended sensitivity.
    /// </summary>
    [Fact]
    public void Every_gated_api_endpoint_declares_a_required_role()
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

        var missing = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } path
                && path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                && !PublicPaths.Contains(path))
            .Where(e => e.Metadata.GetMetadata<RequiredRole>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.True(missing.Count == 0, $"Endpoints missing .RequireRole(...): {string.Join(", ", missing)}");
    }
}
