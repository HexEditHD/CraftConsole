using System.Text.Json;

namespace CraftConsole.Infrastructure.Config;

/// <summary>Tiny JSON-file persistence for a single serializable value.</summary>
public class JsonFileStore<T> where T : class
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public JsonFileStore(string directory, string fileName)
    {
        _filePath = Path.Combine(directory, fileName);
    }

    public async Task<T?> LoadAsync()
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
    }
}
