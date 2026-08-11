namespace CraftConsole.Core.Servers;

/// <summary>
/// Reads a single key out of a Managed profile's server.properties. Shared by
/// every caller that used to hand-roll its own File.ReadLines loop — the
/// port-conflict check, the whitelist-enabled flag, and the max-players
/// default all want the same "read one line, tolerate anything" behaviour.
/// </summary>
public static class ServerProperties
{
    /// <summary>Null when the directory, the file, or the key itself is missing or unreadable.</summary>
    public static string? Read(string workingDirectory, string key)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return null;
        try
        {
            var path = Path.Combine(workingDirectory, "server.properties");
            if (!File.Exists(path)) return null;

            var prefix = key + "=";
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line[prefix.Length..].Trim();
            }
        }
        catch { /* unreadable — treat as unset */ }
        return null;
    }

    /// <summary>
    /// Replaces one key's value, preserving every other line and their order.
    /// Appends the key if it wasn't already present, and creates the file (and
    /// directory) if this profile has never been launched yet — the same
    /// "works before first run" behaviour Read's 25565 port default assumes.
    /// </summary>
    public static void Write(string workingDirectory, string key, string value)
    {
        Directory.CreateDirectory(workingDirectory);
        var path = Path.Combine(workingDirectory, "server.properties");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];

        var prefix = key + "=";
        var index = lines.FindIndex(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var line = prefix + value;
        if (index >= 0) lines[index] = line;
        else lines.Add(line);

        File.WriteAllLines(path, lines);
    }
}
