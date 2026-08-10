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
}
