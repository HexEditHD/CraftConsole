using System.Text.Json;
using CraftConsole.Core.Models;

namespace CraftConsole.Infrastructure.Config;

public class ServerProfileStore
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ServerProfileStore(string appDataPath)
    {
        _filePath = Path.Combine(appDataPath, "profiles.json");
    }

    public async Task<List<ServerProfile>> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return [];

        await using var stream = File.OpenRead(_filePath);
        var profiles = await JsonSerializer.DeserializeAsync<List<ServerProfile>>(stream, JsonOptions) ?? [];

        // Fixup: old JSON files have MaxRamMb == 0 (field didn't exist). Scoped to
        // Managed profiles — an Rcon profile legitimately has no memory limit of
        // its own, and this would otherwise paint a fabricated 2 GB on its dashboard.
        foreach (var p in profiles.Where(p => p.Mode == ConnectionMode.Managed && p.MaxRamMb == 0))
        {
            p.MinRamMb = 512;
            p.MaxRamMb = 2048;
        }

        return profiles;
    }

    public async Task SaveAsync(IEnumerable<ServerProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions);
    }
}
