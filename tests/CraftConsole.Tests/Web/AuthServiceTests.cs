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

    [Fact]
    public async Task IsConfigured_is_false_until_a_password_is_set()
    {
        await _auth.InitializeAsync();
        Assert.False(_auth.IsConfigured);

        await _auth.SetPasswordAsync("correct horse battery staple");
        Assert.True(_auth.IsConfigured);
    }

    [Fact]
    public async Task VerifyPassword_accepts_the_correct_password_and_rejects_others()
    {
        await _auth.InitializeAsync();
        await _auth.SetPasswordAsync("correct horse battery staple");

        Assert.True(_auth.VerifyPassword("correct horse battery staple"));
        Assert.False(_auth.VerifyPassword("wrong password"));
    }

    [Fact]
    public void VerifyPassword_returns_false_before_a_password_has_ever_been_set()
    {
        Assert.False(_auth.VerifyPassword("anything"));
    }

    [Fact]
    public async Task Password_hash_persists_and_reloads_across_instances()
    {
        await _auth.InitializeAsync();
        await _auth.SetPasswordAsync("correct horse battery staple");

        var reloaded = new AuthService(new SettingsHolder(_tempDir));
        await reloaded.InitializeAsync();

        Assert.True(reloaded.IsConfigured);
        Assert.True(reloaded.VerifyPassword("correct horse battery staple"));
    }

    [Fact]
    public void CreateSession_produces_a_token_that_TryValidateSession_accepts()
    {
        var token = _auth.CreateSession();

        Assert.True(_auth.TryValidateSession(token));
    }

    [Fact]
    public void TryValidateSession_rejects_unknown_null_or_revoked_tokens()
    {
        Assert.False(_auth.TryValidateSession("not-a-real-token"));
        Assert.False(_auth.TryValidateSession(null));

        var token = _auth.CreateSession();
        _auth.RevokeSession(token);
        Assert.False(_auth.TryValidateSession(token));
    }

    [Fact]
    public void RevokeAllSessions_invalidates_every_outstanding_token()
    {
        var a = _auth.CreateSession();
        var b = _auth.CreateSession();

        _auth.RevokeAllSessions();

        Assert.False(_auth.TryValidateSession(a));
        Assert.False(_auth.TryValidateSession(b));
    }

    [Fact]
    public void Lockout_engages_after_repeated_failures_and_does_not_block_other_ips()
    {
        const string ip = "203.0.113.5";

        Assert.False(_auth.IsLockedOut(ip));
        for (var i = 0; i < 5; i++) _auth.RegisterFailure(ip);

        Assert.True(_auth.IsLockedOut(ip));
        Assert.False(_auth.IsLockedOut("203.0.113.6"));
    }

    [Fact]
    public void ClearFailures_lifts_the_counter_for_that_ip()
    {
        const string ip = "203.0.113.5";
        for (var i = 0; i < 5; i++) _auth.RegisterFailure(ip);
        Assert.True(_auth.IsLockedOut(ip));

        _auth.ClearFailures(ip);

        Assert.False(_auth.IsLockedOut(ip));
    }
}
