using System.Text.Json;

namespace CraftConsole.Infrastructure.Config;

public class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string? ActiveProfileId { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoScrollConsole { get; set; } = true;
    public int MaxConsoleLines { get; set; } = 2000;

    // Console display prefs
    public bool ShowTimestamp { get; set; } = true;
    public bool ShowDate { get; set; } = false;

    // Log level colors (hex strings)
    public string ColorInfo   { get; set; } = "#94A3B8";
    public string ColorWarn   { get; set; } = "#FB923C";
    public string ColorError  { get; set; } = "#F87171";
    public string ColorPlayer { get; set; } = "#22C55E";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load(string appDataPath)
    {
        var path = Path.Combine(appDataPath, "settings.json");
        if (!File.Exists(path)) return new AppSettings();

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
    }

    public static async Task<AppSettings> LoadAsync(string appDataPath)
    {
        var path = Path.Combine(appDataPath, "settings.json");
        if (!File.Exists(path)) return new AppSettings();

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions) ?? new AppSettings();
    }

    public async Task SaveAsync(string appDataPath)
    {
        var path = Path.Combine(appDataPath, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, JsonOptions);
    }
}
