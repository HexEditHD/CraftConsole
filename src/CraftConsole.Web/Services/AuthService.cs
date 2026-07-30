using System.Collections.Concurrent;
using System.Security.Cryptography;
using CraftConsole.Infrastructure.Config;

namespace CraftConsole.Web.Services;

public record AuthRecord(string SaltBase64, string HashBase64, int Iterations);

/// <summary>
/// Single-operator password auth: one PBKDF2 hash persisted to auth.json,
/// server-held session tokens (an app restart clears them — the browser just
/// logs in again), and a per-IP lockout on repeated failed attempts.
/// </summary>
public sealed class AuthService
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int PbkdfIterations = 210_000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private const int MaxFailuresBeforeLockout = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private readonly JsonFileStore<AuthRecord> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();
    private readonly ConcurrentDictionary<string, FailureState> _failures = new();

    private AuthRecord? _record;

    public AuthService(SettingsHolder settings)
    {
        _store = new JsonFileStore<AuthRecord>(settings.AppDataPath, "auth.json");
    }

    public async Task InitializeAsync() => _record = await _store.LoadAsync();

    public bool IsConfigured => _record is not null;

    public async Task SetPasswordAsync(string password)
    {
        await _gate.WaitAsync();
        try
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PbkdfIterations, HashAlgorithmName.SHA256, KeySize);
            _record = new AuthRecord(Convert.ToBase64String(salt), Convert.ToBase64String(hash), PbkdfIterations);
            await _store.SaveAsync(_record);
        }
        finally { _gate.Release(); }
    }

    public bool VerifyPassword(string password)
    {
        if (_record is not { } record) return false;

        var salt = Convert.FromBase64String(record.SaltBase64);
        var expected = Convert.FromBase64String(record.HashBase64);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, record.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // ── Sessions ─────────────────────────────────────────────────────────

    public string CreateSession()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _sessions[token] = DateTimeOffset.UtcNow.Add(SessionLifetime);
        return token;
    }

    public bool TryValidateSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_sessions.TryGetValue(token, out var expiry)) return false;
        if (expiry < DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        _sessions[token] = DateTimeOffset.UtcNow.Add(SessionLifetime); // sliding expiry
        return true;
    }

    public void RevokeSession(string? token)
    {
        if (token is not null) _sessions.TryRemove(token, out _);
    }

    public void RevokeAllSessions() => _sessions.Clear();

    // ── Lockout ──────────────────────────────────────────────────────────

    public bool IsLockedOut(string ip)
        => _failures.TryGetValue(ip, out var state) && state.LockedUntil > DateTimeOffset.UtcNow;

    public void RegisterFailure(string ip)
    {
        var state = _failures.GetOrAdd(ip, _ => new FailureState());
        lock (state)
        {
            state.Count++;
            if (state.Count >= MaxFailuresBeforeLockout)
            {
                state.LockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
                state.Count = 0;
            }
        }
    }

    public void ClearFailures(string ip) => _failures.TryRemove(ip, out _);

    private sealed class FailureState
    {
        public int Count;
        public DateTimeOffset LockedUntil;
    }
}
