using CraftConsole.Core.Models;
using CraftConsole.Infrastructure.Config;

namespace CraftConsole.Web.Services;

/// <summary>
/// CRUD over saved server profiles (profiles.json) plus the notion of the
/// "active" profile — the one Start launches by default.
/// </summary>
public sealed class ProfilesService
{
    private readonly ServerProfileStore _store;
    private readonly SettingsHolder _settings;
    private readonly RconSecretStore _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ServerProfile>? _profiles;

    public ProfilesService(SettingsHolder settings, RconSecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
        _store = new ServerProfileStore(settings.AppDataPath);
    }

    public async Task<List<ServerProfile>> ListAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _profiles ??= await _store.LoadAsync();
            return [.. _profiles];
        }
        finally { _gate.Release(); }
    }

    public async Task<ServerProfile?> GetAsync(Guid id)
        => (await ListAsync()).FirstOrDefault(p => p.Id == id);

    public async Task<ServerProfile?> GetActiveAsync()
    {
        var profiles = await ListAsync();
        if (Guid.TryParse(_settings.Current.ActiveProfileId, out var id)
            && profiles.FirstOrDefault(p => p.Id == id) is { } active)
            return active;
        return profiles.FirstOrDefault();
    }

    public async Task<ServerProfile> AddAsync(ServerProfile profile)
    {
        Validate(profile);

        await _gate.WaitAsync();
        try
        {
            _profiles ??= await _store.LoadAsync();
            _profiles.Add(profile);
            await _store.SaveAsync(_profiles);
        }
        finally { _gate.Release(); }

        if (_settings.Current.ActiveProfileId is null)
            await SetActiveAsync(profile.Id);
        return profile;
    }

    public async Task<bool> UpdateAsync(Guid id, ServerProfile updated)
    {
        Validate(updated);

        await _gate.WaitAsync();
        try
        {
            _profiles ??= await _store.LoadAsync();
            var profile = _profiles.FirstOrDefault(p => p.Id == id);
            if (profile is null) return false;

            profile.Name = updated.Name;
            profile.Mode = updated.Mode;
            profile.JarPath = updated.JarPath;
            profile.WorkingDirectory = updated.WorkingDirectory;
            profile.JavaPath = updated.JavaPath;
            profile.MinRamMb = updated.MinRamMb;
            profile.MaxRamMb = updated.MaxRamMb;
            profile.MinecraftVersion = updated.MinecraftVersion;
            profile.JvmArguments = updated.JvmArguments;
            profile.Type = updated.Type;
            profile.RconHost = updated.RconHost;
            profile.RconPort = updated.RconPort;

            await _store.SaveAsync(_profiles);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            _profiles ??= await _store.LoadAsync();
            var profile = _profiles.FirstOrDefault(p => p.Id == id);
            if (profile is null) return false;
            _profiles.Remove(profile);
            await _store.SaveAsync(_profiles);
        }
        finally { _gate.Release(); }

        await _secrets.RemoveAsync(id);

        if (_settings.Current.ActiveProfileId == id.ToString())
            await _settings.UpdateAsync(s => s.ActiveProfileId = null);
        return true;
    }

    public Task SetActiveAsync(Guid id)
        => _settings.UpdateAsync(s => s.ActiveProfileId = id.ToString());

    /// <summary>
    /// Each connection mode needs different fields; a profile missing the ones
    /// its own mode requires is rejected rather than silently accepted and
    /// failing later at start time.
    /// </summary>
    private static void Validate(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("A profile name is required.");

        switch (profile.Mode)
        {
            case ConnectionMode.Managed when string.IsNullOrWhiteSpace(profile.JarPath):
                throw new InvalidOperationException("A server JAR path is required for a managed profile.");

            case ConnectionMode.Rcon when string.IsNullOrWhiteSpace(profile.RconHost):
                throw new InvalidOperationException("A host is required for an RCON profile.");

            case ConnectionMode.Rcon when profile.RconPort is <= 0 or > 65535:
                throw new InvalidOperationException("The RCON port must be between 1 and 65535.");
        }
    }
}
