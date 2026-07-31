using System.Security.Cryptography;
using CraftConsole.Infrastructure.Config;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace CraftConsole.Web.Services;

/// <summary>
/// Stores RCON passwords encrypted at rest, keyed by profile id, in
/// rcon-secrets.json. Kept entirely separate from <see cref="Core.Models.ServerProfile"/>
/// — that type is serialized straight into HTTP responses and every SSE status
/// frame, so a password living on it would ship to every connected browser.
///
/// Unlike the panel's own login password (see AuthService, PBKDF2 — one-way by
/// design), an RCON password must be replayed verbatim on every connect, so it
/// is encrypted rather than hashed. Data Protection's key ring is pinned to the
/// app data directory (see Program.cs) so a restart — including the Debian
/// service, which needs to reconnect unattended — can still decrypt it.
/// </summary>
public sealed class RconSecretStore
{
    private readonly JsonFileStore<Dictionary<string, string>> _store;
    private readonly IDataProtector _protector;
    private readonly ILogger<RconSecretStore> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _secrets;

    public RconSecretStore(SettingsHolder settings, IDataProtectionProvider dataProtection, ILogger<RconSecretStore> log)
    {
        _store = new JsonFileStore<Dictionary<string, string>>(settings.AppDataPath, "rcon-secrets.json");
        _protector = dataProtection.CreateProtector("CraftConsole.RconSecretStore.v1");
        _log = log;
    }

    public async Task SetAsync(Guid profileId, string password)
    {
        await _gate.WaitAsync();
        try
        {
            _secrets ??= await _store.LoadAsync() ?? [];
            _secrets[profileId.ToString()] = _protector.Protect(password);
            await _store.SaveAsync(_secrets);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HasAsync(Guid profileId)
    {
        await _gate.WaitAsync();
        try
        {
            _secrets ??= await _store.LoadAsync() ?? [];
            return _secrets.ContainsKey(profileId.ToString());
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The decrypted password, or null if none is stored, or if it could not be
    /// decrypted (e.g. the Data Protection key ring changed). Both are "no
    /// usable password" to a caller, but a decrypt failure is logged rather than
    /// silently treated the same as JsonFileStore's swallow-and-return-null —
    /// otherwise the next SetAsync would overwrite it without any signal that
    /// the previous value was lost, not merely absent.
    /// </summary>
    public async Task<string?> TryGetAsync(Guid profileId)
    {
        await _gate.WaitAsync();
        try
        {
            _secrets ??= await _store.LoadAsync() ?? [];
            if (!_secrets.TryGetValue(profileId.ToString(), out var protectedValue))
                return null;

            try { return _protector.Unprotect(protectedValue); }
            catch (CryptographicException ex)
            {
                _log.LogWarning(ex,
                    "Could not decrypt the RCON password stored for profile {ProfileId} (the Data Protection key ring likely changed).",
                    profileId);
                return null;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid profileId)
    {
        await _gate.WaitAsync();
        try
        {
            _secrets ??= await _store.LoadAsync() ?? [];
            if (_secrets.Remove(profileId.ToString()))
                await _store.SaveAsync(_secrets);
        }
        finally { _gate.Release(); }
    }
}
