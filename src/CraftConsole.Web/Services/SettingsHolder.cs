using CraftConsole.Infrastructure.Config;

namespace CraftConsole.Web.Services;

/// <summary>Single mutable AppSettings instance shared across the app, persisted on change.</summary>
public sealed class SettingsHolder
{
    public string AppDataPath { get; }
    public AppSettings Current { get; private set; }

    public SettingsHolder(string appDataPath)
    {
        AppDataPath = appDataPath;
        Current = AppSettings.Load(appDataPath);
    }

    public async Task UpdateAsync(Action<AppSettings> mutate)
    {
        mutate(Current);
        await Current.SaveAsync(AppDataPath);
    }
}
