using System.Security.Cryptography;
using CraftConsole.Infrastructure.Config;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace CraftConsole.Web.Services;

/// <summary>
/// Stores the single, global CurseForge API key encrypted at rest, in
/// curseforge-key.json. Unlike <see cref="RconSecretStore"/> there's only ever
/// one value — this isn't per-profile — so the store is a single protected
/// string rather than a dictionary. Same reasoning as RconSecretStore for why
/// this is encrypted rather than hashed: CurseForge needs the key back
/// verbatim on every request, it's never compared, only replayed.
/// </summary>
public sealed class CurseForgeSecretStore
{
    private readonly JsonFileStore<string> _store;
    private readonly IDataProtector _protector;
    private readonly ILogger<CurseForgeSecretStore> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private string? _encrypted;

    public CurseForgeSecretStore(SettingsHolder settings, IDataProtectionProvider dataProtection, ILogger<CurseForgeSecretStore> log)
    {
        _store = new JsonFileStore<string>(settings.AppDataPath, "curseforge-key.json");
        _protector = dataProtection.CreateProtector("CraftConsole.CurseForgeSecretStore.v1");
        _log = log;
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _encrypted = await _store.LoadAsync();
        _loaded = true;
    }

    public async Task SetAsync(string apiKey)
    {
        await _gate.WaitAsync();
        try
        {
            _encrypted = _protector.Protect(apiKey);
            _loaded = true;
            await _store.SaveAsync(_encrypted);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HasAsync()
    {
        await _gate.WaitAsync();
        try { await EnsureLoadedAsync(); return !string.IsNullOrEmpty(_encrypted); }
        finally { _gate.Release(); }
    }

    /// <summary>The decrypted key, or null if none is stored or it couldn't be decrypted — see RconSecretStore.TryGetAsync for why those are logged differently.</summary>
    public async Task<string?> TryGetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            if (string.IsNullOrEmpty(_encrypted)) return null;

            try { return _protector.Unprotect(_encrypted); }
            catch (CryptographicException ex)
            {
                _log.LogWarning(ex, "Could not decrypt the stored CurseForge API key (the Data Protection key ring likely changed).");
                return null;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _encrypted = null;
            _loaded = true;
            await _store.SaveAsync("");
        }
        finally { _gate.Release(); }
    }
}
