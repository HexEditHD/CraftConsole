using System.Text.Json;
using CraftConsole.Web.Services;
using Xunit;

namespace CraftConsole.Tests.Web;

public class AuthServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-auth-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _auth = new AuthService(new SettingsHolder(_tempDir));
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string AuthJsonPath => Path.Combine(_tempDir, "auth.json");

    // ── First-run setup ──────────────────────────────────────────────────

    [Fact]
    public async Task IsConfigured_is_false_until_the_admin_is_set_up()
    {
        await _auth.InitializeAsync();
        Assert.False(_auth.IsConfigured);

        await _auth.SetupAdminAsync("correct horse battery staple");
        Assert.True(_auth.IsConfigured);
    }

    [Fact]
    public async Task SetupAdminAsync_refuses_once_a_user_already_exists()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.SetupAdminAsync("second attempt"));
    }

    [Fact]
    public async Task The_seed_admin_is_enabled_and_holds_the_Admin_role()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");

        var admin = _auth.ListUsers().Single();
        Assert.Equal(Role.Admin, admin.Role);
        Assert.True(admin.Enabled);
        Assert.Equal("admin", admin.Username);
    }

    // ── Authentication ───────────────────────────────────────────────────

    [Fact]
    public async Task VerifyCredentials_accepts_the_correct_password_and_rejects_others()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");

        Assert.NotNull(_auth.VerifyCredentials("admin", "correct horse battery staple"));
        Assert.Null(_auth.VerifyCredentials("admin", "wrong password"));
        Assert.Null(_auth.VerifyCredentials("nobody", "correct horse battery staple"));
    }

    [Fact]
    public async Task VerifyCredentials_matches_the_username_case_insensitively()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");

        Assert.NotNull(_auth.VerifyCredentials("ADMIN", "correct horse battery staple"));
    }

    [Fact]
    public async Task VerifyCredentials_rejects_a_disabled_user()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var (_, operatorUser) = await _auth.CreateUserAsync("op", "op-password-123", Role.Operator);

        await _auth.SetEnabledAsync(operatorUser!.Id, false);

        Assert.Null(_auth.VerifyCredentials("op", "op-password-123"));
    }

    [Fact]
    public void VerifyCredentials_returns_null_before_any_user_exists()
    {
        Assert.Null(_auth.VerifyCredentials("admin", "anything"));
    }

    [Fact]
    public async Task Password_hash_persists_and_reloads_across_instances()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");

        var reloaded = new AuthService(new SettingsHolder(_tempDir));
        await reloaded.InitializeAsync();

        Assert.True(reloaded.IsConfigured);
        Assert.NotNull(reloaded.VerifyCredentials("admin", "correct horse battery staple"));
    }

    // ── Legacy migration ─────────────────────────────────────────────────

    [Fact]
    public async Task A_legacy_single_password_file_migrates_to_one_enabled_admin_with_the_same_password()
    {
        // Shape written by the pre-RBAC AuthService: {saltBase64, hashBase64, iterations}.
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("legacy password 123");
        var legacyUser = _auth.ListUsers().Single();
        await File.WriteAllTextAsync(AuthJsonPath, JsonSerializer.Serialize(new
        {
            saltBase64 = legacyUser.SaltBase64,
            hashBase64 = legacyUser.HashBase64,
            iterations = legacyUser.Iterations,
        }));

        var migrated = new AuthService(new SettingsHolder(_tempDir));
        await migrated.InitializeAsync();

        Assert.True(migrated.IsConfigured);
        var admin = migrated.ListUsers().Single();
        Assert.Equal("admin", admin.Username);
        Assert.Equal(Role.Admin, admin.Role);
        Assert.True(admin.Enabled);
        Assert.NotNull(migrated.VerifyCredentials("admin", "legacy password 123"));
    }

    [Fact]
    public async Task Migration_rewrites_the_file_immediately_so_it_only_runs_once()
    {
        await File.WriteAllTextAsync(AuthJsonPath, JsonSerializer.Serialize(new
        {
            saltBase64 = Convert.ToBase64String([1, 2, 3]),
            hashBase64 = Convert.ToBase64String([4, 5, 6]),
            iterations = 210_000,
        }));

        var first = new AuthService(new SettingsHolder(_tempDir));
        await first.InitializeAsync();
        var idAfterFirstLoad = first.ListUsers().Single().Id;

        var onDisk = await File.ReadAllTextAsync(AuthJsonPath);
        using var doc = JsonDocument.Parse(onDisk);
        Assert.Contains(doc.RootElement.EnumerateObject(),
            p => string.Equals(p.Name, "users", StringComparison.OrdinalIgnoreCase));

        var second = new AuthService(new SettingsHolder(_tempDir));
        await second.InitializeAsync();

        Assert.Single(second.ListUsers());
        Assert.Equal(idAfterFirstLoad, second.ListUsers().Single().Id);
    }

    [Fact]
    public async Task A_corrupt_auth_file_fails_loudly_instead_of_reporting_unconfigured()
    {
        await File.WriteAllTextAsync(AuthJsonPath, "{ this is not valid json");

        var broken = new AuthService(new SettingsHolder(_tempDir));

        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.InitializeAsync());
    }

    [Fact]
    public async Task An_auth_file_in_an_unrecognized_shape_fails_loudly()
    {
        await File.WriteAllTextAsync(AuthJsonPath, """{"somethingElse": true}""");

        var broken = new AuthService(new SettingsHolder(_tempDir));

        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.InitializeAsync());
    }

    // ── User management ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateUserAsync_rejects_a_username_already_taken_case_insensitively()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");

        var (result, user) = await _auth.CreateUserAsync("Admin", "another-password-123", Role.Operator);

        Assert.Equal(UserMutationResult.UsernameTaken, result);
        Assert.Null(user);
    }

    [Fact]
    public async Task SetEnabledAsync_refuses_to_disable_the_last_enabled_admin()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var admin = _auth.ListUsers().Single();

        var result = await _auth.SetEnabledAsync(admin.Id, false);

        Assert.Equal(UserMutationResult.LastAdminProtected, result);
        Assert.True(_auth.GetUser(admin.Id)!.Enabled);
    }

    [Fact]
    public async Task SetEnabledAsync_allows_disabling_an_admin_when_another_enabled_admin_exists()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var admin = _auth.ListUsers().Single();
        var (_, secondAdmin) = await _auth.CreateUserAsync("admin2", "second-admin-pw-123", Role.Admin);

        var result = await _auth.SetEnabledAsync(admin.Id, false);

        Assert.Equal(UserMutationResult.Success, result);
        Assert.False(_auth.GetUser(admin.Id)!.Enabled);
        Assert.True(_auth.GetUser(secondAdmin!.Id)!.Enabled);
    }

    [Fact]
    public async Task SetRoleAsync_refuses_to_demote_the_last_enabled_admin()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var admin = _auth.ListUsers().Single();

        var result = await _auth.SetRoleAsync(admin.Id, Role.Operator);

        Assert.Equal(UserMutationResult.LastAdminProtected, result);
        Assert.Equal(Role.Admin, _auth.GetUser(admin.Id)!.Role);
    }

    [Fact]
    public async Task DeleteUserAsync_refuses_to_delete_the_last_enabled_admin()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var admin = _auth.ListUsers().Single();

        var result = await _auth.DeleteUserAsync(admin.Id);

        Assert.Equal(UserMutationResult.LastAdminProtected, result);
        Assert.NotNull(_auth.GetUser(admin.Id));
    }

    [Fact]
    public async Task DeleteUserAsync_removes_a_non_protected_user_and_reports_missing_ones_as_not_found()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var (_, op) = await _auth.CreateUserAsync("op", "op-password-123", Role.Operator);

        Assert.Equal(UserMutationResult.Success, await _auth.DeleteUserAsync(op!.Id));
        Assert.Null(_auth.GetUser(op.Id));
        Assert.Equal(UserMutationResult.NotFound, await _auth.DeleteUserAsync(op.Id));
    }

    [Fact]
    public async Task SetPasswordAsync_changes_which_password_verifies()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var admin = _auth.ListUsers().Single();

        await _auth.SetPasswordAsync(admin.Id, "a brand new password");

        Assert.Null(_auth.VerifyCredentials("admin", "correct horse battery staple"));
        Assert.NotNull(_auth.VerifyCredentials("admin", "a brand new password"));
    }

    [Fact]
    public async Task SetPasswordAsync_revokes_the_users_existing_sessions()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var (_, op) = await _auth.CreateUserAsync("op", "op-password-123", Role.Operator);
        var token = _auth.CreateSession(op!.Id);
        Assert.NotNull(_auth.TryValidateSession(token));

        await _auth.SetPasswordAsync(op.Id, "a brand new password");

        Assert.Null(_auth.TryValidateSession(token));
    }

    // ── Sessions ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSession_produces_a_token_TryValidateSession_resolves_to_that_users_identity()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var admin = _auth.ListUsers().Single();

        var token = _auth.CreateSession(admin.Id);
        var session = _auth.TryValidateSession(token);

        Assert.NotNull(session);
        Assert.Equal(admin.Id, session!.UserId);
        Assert.Equal("admin", session.Username);
        Assert.Equal(Role.Admin, session.Role);
    }

    [Fact]
    public async Task TryValidateSession_rejects_unknown_null_or_revoked_tokens()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var admin = _auth.ListUsers().Single();

        Assert.Null(_auth.TryValidateSession("not-a-real-token"));
        Assert.Null(_auth.TryValidateSession(null));

        var token = _auth.CreateSession(admin.Id);
        _auth.RevokeSession(token);
        Assert.Null(_auth.TryValidateSession(token));
    }

    [Fact]
    public async Task RevokeAllSessions_invalidates_every_outstanding_token()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var admin = _auth.ListUsers().Single();
        var a = _auth.CreateSession(admin.Id);
        var b = _auth.CreateSession(admin.Id);

        _auth.RevokeAllSessions();

        Assert.Null(_auth.TryValidateSession(a));
        Assert.Null(_auth.TryValidateSession(b));
    }

    [Fact]
    public async Task RevokeAllSessionsForUser_only_touches_that_users_tokens()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var admin = _auth.ListUsers().Single();
        var (_, op) = await _auth.CreateUserAsync("op", "op-password-123", Role.Operator);

        var adminToken = _auth.CreateSession(admin.Id);
        var opToken = _auth.CreateSession(op!.Id);

        _auth.RevokeAllSessionsForUser(op.Id);

        Assert.NotNull(_auth.TryValidateSession(adminToken));
        Assert.Null(_auth.TryValidateSession(opToken));
    }

    [Fact]
    public async Task Disabling_a_user_immediately_invalidates_their_outstanding_sessions()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var (_, op) = await _auth.CreateUserAsync("op", "op-password-123", Role.Operator);
        var token = _auth.CreateSession(op!.Id);
        Assert.NotNull(_auth.TryValidateSession(token));

        await _auth.SetEnabledAsync(op.Id, false);

        Assert.Null(_auth.TryValidateSession(token));
    }

    [Fact]
    public async Task A_role_change_takes_effect_on_the_next_validation_without_a_new_token()
    {
        await _auth.InitializeAsync();
        await _auth.SetupAdminAsync("correct horse battery staple");
        var (_, op) = await _auth.CreateUserAsync("op", "op-password-123", Role.Operator);
        var token = _auth.CreateSession(op!.Id);
        Assert.Equal(Role.Operator, _auth.TryValidateSession(token)!.Role);

        await _auth.SetRoleAsync(op.Id, Role.Admin);

        Assert.Equal(Role.Admin, _auth.TryValidateSession(token)!.Role);
    }

    // ── Lockout ──────────────────────────────────────────────────────────

    [Fact]
    public void Lockout_engages_after_repeated_failures_and_does_not_block_other_ips()
    {
        const string ip = "203.0.113.5";
        const string username = "irrelevant-for-this-test";

        Assert.False(_auth.IsLockedOut(ip, username));
        for (var i = 0; i < 5; i++) _auth.RegisterFailure(ip, username);

        Assert.True(_auth.IsLockedOut(ip, username));
        Assert.False(_auth.IsLockedOut("203.0.113.6", username));
    }

    [Fact]
    public void ClearFailures_lifts_the_counter_for_that_ip()
    {
        const string ip = "203.0.113.5";
        const string username = "irrelevant-for-this-test";
        for (var i = 0; i < 5; i++) _auth.RegisterFailure(ip, username);
        Assert.True(_auth.IsLockedOut(ip, username));

        _auth.ClearFailures(ip, username);

        Assert.False(_auth.IsLockedOut(ip, username));
    }

    [Fact]
    public void Lockout_engages_against_one_username_regardless_of_source_ip()
    {
        const string username = "someone";
        Assert.False(_auth.IsLockedOut("203.0.113.1", username));

        for (var i = 0; i < 10; i++) _auth.RegisterFailure($"203.0.113.{i}", username); // a distinct IP each time

        Assert.True(_auth.IsLockedOut("203.0.113.99", username));
        Assert.False(_auth.IsLockedOut("203.0.113.99", "someone-else"));
    }

    [Fact]
    public void Username_lockout_threshold_is_higher_than_the_per_ip_threshold()
    {
        const string username = "someone";
        for (var i = 0; i < 5; i++) _auth.RegisterFailure($"203.0.113.{i}", username);
        Assert.False(_auth.IsLockedOut("203.0.113.50", username)); // 5 distinct IPs, still under the username threshold of 10

        for (var i = 5; i < 10; i++) _auth.RegisterFailure($"203.0.113.{i}", username);
        Assert.True(_auth.IsLockedOut("203.0.113.50", username));
    }

    [Fact]
    public void Username_lockout_is_case_insensitive()
    {
        for (var i = 0; i < 10; i++) _auth.RegisterFailure($"203.0.113.{i}", "Someone");

        Assert.True(_auth.IsLockedOut("203.0.113.50", "SOMEONE"));
    }
}
