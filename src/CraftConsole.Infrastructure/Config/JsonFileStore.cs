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

    /// <summary>
    /// Writes to a temp file in the same directory, then atomically replaces the
    /// real one — a crash or power loss mid-write leaves either the old file or
    /// the new one intact, never a truncated half-written file.
    /// </summary>
    public async Task SaveAsync(T value)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = File.Create(tempPath))
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions);

            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* already moved, or never created */ }
        }
    }
}
