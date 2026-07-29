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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ServerProfile>? _profiles;

    public ProfilesService(SettingsHolder settings)
    {
        _settings = settings;
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
        await _gate.WaitAsync();
        try
        {
            _profiles ??= await _store.LoadAsync();
            var profile = _profiles.FirstOrDefault(p => p.Id == id);
            if (profile is null) return false;

            profile.Name = updated.Name;
            profile.JarPath = updated.JarPath;
            profile.WorkingDirectory = updated.WorkingDirectory;
            profile.JavaPath = updated.JavaPath;
            profile.MinRamMb = updated.MinRamMb;
            profile.MaxRamMb = updated.MaxRamMb;
            profile.MinecraftVersion = updated.MinecraftVersion;
            profile.JvmArguments = updated.JvmArguments;
            profile.Type = updated.Type;

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

        if (_settings.Current.ActiveProfileId == id.ToString())
            await _settings.UpdateAsync(s => s.ActiveProfileId = null);
        return true;
    }

    public Task SetActiveAsync(Guid id)
        => _settings.UpdateAsync(s => s.ActiveProfileId = id.ToString());
}
