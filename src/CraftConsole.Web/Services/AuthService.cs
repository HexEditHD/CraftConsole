using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using CraftConsole.Infrastructure.Config;

namespace CraftConsole.Web.Services;

public enum UserMutationResult { Success, NotFound, LastAdminProtected, UsernameTaken }

public sealed record SessionInfo(Guid UserId, string Username, Role Role);

/// <summary>
/// Multi-user password auth: PBKDF2 hashes persisted to auth.json, server-held
/// per-user session tokens (an app restart clears them — the browser just logs
/// in again), and a per-IP lockout on repeated failed attempts.
///
/// Migration: a pre-RBAC install has auth.json shaped as a single
/// <c>{saltBase64, hashBase64, iterations}</c> record. On first load that legacy
/// shape is detected explicitly and converted to one enabled Admin user
/// (username "admin") whose password hash — and therefore whose password — is
/// unchanged, then immediately rewritten to disk in the new shape so migration
/// only runs once. A file that is neither the legacy shape nor the new shape
/// throws rather than being treated as "unconfigured": silently reopening
/// first-run setup on a corrupted file would let anyone on loopback claim admin.
/// </summary>
public sealed class AuthService
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int PbkdfIterations = 210_000;
    private const string LegacyAdminUsername = "admin";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private const int MaxFailuresBeforeLockout = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;
    private readonly JsonFileStore<AuthState> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, FailureState> _failures = new();

    private List<UserRecord> _users = [];

    public AuthService(SettingsHolder settings)
    {
        _filePath = Path.Combine(settings.AppDataPath, "auth.json");
        _store = new JsonFileStore<AuthState>(settings.AppDataPath, "auth.json");
    }

    public async Task InitializeAsync() => _users = await LoadUsersAsync();

    public bool IsConfigured => _users.Count > 0;

    private sealed record AuthState(List<UserRecord> Users);
    private readonly record struct Session(Guid UserId, DateTimeOffset Expiry);

    // ── Load & migrate ───────────────────────────────────────────────────

    private async Task<List<UserRecord>> LoadUsersAsync()
    {
        if (!File.Exists(_filePath)) return [];

        string text;
        try { text = await File.ReadAllTextAsync(_filePath); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not read {_filePath}: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(text)) return [];

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{_filePath} is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{_filePath} does not contain a recognized auth format.");

            // JsonFileStore writes property names exactly as declared (PascalCase, no
            // naming policy) — matched case-insensitively here since JsonDocument's own
            // TryGetProperty is case-sensitive and this is deliberately robust to either.
            if (TryGetPropertyCI(root, "users", out var usersEl) && usersEl.ValueKind == JsonValueKind.Array)
            {
                var users = JsonSerializer.Deserialize<List<UserRecord>>(usersEl.GetRawText(), JsonOptions);
                if (users is null)
                    throw new InvalidOperationException($"{_filePath}'s \"users\" array could not be parsed.");
                return users;
            }

            if (TryGetPropertyCI(root, "saltBase64", out var saltEl)
                && TryGetPropertyCI(root, "hashBase64", out var hashEl)
                && TryGetPropertyCI(root, "iterations", out var iterEl))
            {
                var migrated = new UserRecord(
                    Guid.NewGuid(), LegacyAdminUsername,
                    saltEl.GetString() ?? throw new InvalidOperationException($"{_filePath} is missing saltBase64."),
                    hashEl.GetString() ?? throw new InvalidOperationException($"{_filePath} is missing hashBase64."),
                    iterEl.GetInt32(), Role.Admin, Enabled: true, DateTimeOffset.UtcNow);

                var migratedList = new List<UserRecord> { migrated };
                await _store.SaveAsync(new AuthState(migratedList));
                return migratedList;
            }

            throw new InvalidOperationException($"{_filePath} does not contain a recognized auth format.");
        }
    }

    private static bool TryGetPropertyCI(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private Task SaveUsersAsync() => _store.SaveAsync(new AuthState(_users));

    // ── First-run setup ──────────────────────────────────────────────────

    /// <summary>Creates the single seed Admin. Only valid before any user exists.</summary>
    public async Task SetupAdminAsync(string password)
    {
        await _gate.WaitAsync();
        try
        {
            if (_users.Count > 0) throw new InvalidOperationException("A user already exists.");
            _users = [NewUser(LegacyAdminUsername, password, Role.Admin)];
            await SaveUsersAsync();
        }
        finally { _gate.Release(); }
    }

    // ── Authentication ───────────────────────────────────────────────────

    public UserRecord? VerifyCredentials(string username, string password)
    {
        var user = _users.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        if (user is null || !user.Enabled) return null;
        return VerifyPasswordHash(user, password) ? user : null;
    }

    private static bool VerifyPasswordHash(UserRecord user, string password)
    {
        var salt = Convert.FromBase64String(user.SaltBase64);
        var expected = Convert.FromBase64String(user.HashBase64);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, user.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static UserRecord NewUser(string username, string password, Role role)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PbkdfIterations, HashAlgorithmName.SHA256, KeySize);
        return new UserRecord(
            Guid.NewGuid(), username, Convert.ToBase64String(salt), Convert.ToBase64String(hash),
            PbkdfIterations, role, Enabled: true, DateTimeOffset.UtcNow);
    }

    // ── User management ──────────────────────────────────────────────────

    public IReadOnlyList<UserRecord> ListUsers() => _users;

    public UserRecord? GetUser(Guid userId) => _users.FirstOrDefault(u => u.Id == userId);

    public async Task<(UserMutationResult Result, UserRecord? User)> CreateUserAsync(
        string username, string password, Role role)
    {
        await _gate.WaitAsync();
        try
        {
            if (_users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                return (UserMutationResult.UsernameTaken, null);

            var user = NewUser(username, password, role);
            _users = [.. _users, user];
            await SaveUsersAsync();
            return (UserMutationResult.Success, user);
        }
        finally { _gate.Release(); }
    }

    public async Task<UserMutationResult> SetEnabledAsync(Guid userId, bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            if (GetUser(userId) is null) return UserMutationResult.NotFound;
            if (!enabled && IsLastEnabledAdmin(userId)) return UserMutationResult.LastAdminProtected;

            _users = [.. _users.Select(u => u.Id == userId ? u with { Enabled = enabled } : u)];
            await SaveUsersAsync();
            if (!enabled) RevokeAllSessionsForUser(userId);
            return UserMutationResult.Success;
        }
        finally { _gate.Release(); }
    }

    public async Task<UserMutationResult> SetRoleAsync(Guid userId, Role role)
    {
        await _gate.WaitAsync();
        try
        {
            if (GetUser(userId) is null) return UserMutationResult.NotFound;
            if (role != Role.Admin && IsLastEnabledAdmin(userId)) return UserMutationResult.LastAdminProtected;

            _users = [.. _users.Select(u => u.Id == userId ? u with { Role = role } : u)];
            await SaveUsersAsync();
            return UserMutationResult.Success;
        }
        finally { _gate.Release(); }
    }

    public async Task<UserMutationResult> DeleteUserAsync(Guid userId)
    {
        await _gate.WaitAsync();
        try
        {
            if (GetUser(userId) is null) return UserMutationResult.NotFound;
            if (IsLastEnabledAdmin(userId)) return UserMutationResult.LastAdminProtected;

            _users = [.. _users.Where(u => u.Id != userId)];
            await SaveUsersAsync();
            RevokeAllSessionsForUser(userId);
            return UserMutationResult.Success;
        }
        finally { _gate.Release(); }
    }

    public async Task<UserMutationResult> SetPasswordAsync(Guid userId, string newPassword)
    {
        await _gate.WaitAsync();
        try
        {
            if (GetUser(userId) is not { } user) return UserMutationResult.NotFound;

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Rfc2898DeriveBytes.Pbkdf2(newPassword, salt, PbkdfIterations, HashAlgorithmName.SHA256, KeySize);
            var updated = user with
            {
                SaltBase64 = Convert.ToBase64String(salt),
                HashBase64 = Convert.ToBase64String(hash),
                Iterations = PbkdfIterations,
            };
            _users = [.. _users.Select(u => u.Id == userId ? updated : u)];
            await SaveUsersAsync();
            return UserMutationResult.Success;
        }
        finally { _gate.Release(); }
    }

    /// <summary>True if disabling/demoting/deleting this user would leave zero enabled Admins.</summary>
    private bool IsLastEnabledAdmin(Guid userId)
    {
        var target = GetUser(userId);
        if (target is null || target.Role != Role.Admin || !target.Enabled) return false;
        return !_users.Any(u => u.Id != userId && u.Role == Role.Admin && u.Enabled);
    }

    // ── Sessions ─────────────────────────────────────────────────────────

    public string CreateSession(Guid userId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _sessions[token] = new Session(userId, DateTimeOffset.UtcNow.Add(SessionLifetime));
        return token;
    }

    /// <summary>
    /// Re-reads the user's current role/enabled state on every call rather than
    /// caching it in the session, so a role change or disable takes effect on
    /// the user's very next request instead of only after their session expires.
    /// </summary>
    public SessionInfo? TryValidateSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (session.Expiry < DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        if (GetUser(session.UserId) is not { Enabled: true } user)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        _sessions[token] = session with { Expiry = DateTimeOffset.UtcNow.Add(SessionLifetime) }; // sliding expiry
        return new SessionInfo(user.Id, user.Username, user.Role);
    }

    public void RevokeSession(string? token)
    {
        if (token is not null) _sessions.TryRemove(token, out _);
    }

    public void RevokeAllSessions() => _sessions.Clear();

    public void RevokeAllSessionsForUser(Guid userId)
    {
        foreach (var (token, session) in _sessions)
            if (session.UserId == userId) _sessions.TryRemove(token, out _);
    }

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
