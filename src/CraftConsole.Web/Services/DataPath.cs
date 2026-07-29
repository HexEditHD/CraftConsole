namespace CraftConsole.Web.Services;

/// <summary>
/// Resolves where CraftConsole keeps its data: settings, profiles, scheduled
/// tasks, backup jobs, the auth record, and logs.
///
/// Precedence: <c>--data-dir</c> argument, then the <c>CRAFTCONSOLE_DATA</c>
/// environment variable, then a per-user OS default. The Debian package sets
/// the environment variable so the systemd service writes to
/// /var/lib/craftconsole instead of the service account's home directory.
/// </summary>
public static class DataPath
{
    public const string EnvironmentVariable = "CRAFTCONSOLE_DATA";
    public const string CommandLineSwitch = "--data-dir";

    private const string FolderName = "CraftConsole";

    /// <param name="args">Process command-line arguments.</param>
    /// <param name="environment">
    /// Environment-variable lookup; defaults to the real environment. Injectable for tests.
    /// </param>
    /// <param name="fallbackRoot">
    /// Used only when the OS reports no per-user application-data directory — which happens
    /// to daemon accounts that have no HOME. Defaults to the directory holding the binary.
    /// </param>
    public static string Resolve(
        IReadOnlyList<string> args,
        Func<string, string?>? environment = null,
        string? fallbackRoot = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        if (TryReadSwitch(args, out var fromArgs))
            return Path.GetFullPath(fromArgs);

        if (environment(EnvironmentVariable) is { } fromEnv && !string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);

        return DefaultPath(fallbackRoot);
    }

    /// <summary>Accepts both <c>--data-dir VALUE</c> and <c>--data-dir=VALUE</c>.</summary>
    private static bool TryReadSwitch(IReadOnlyList<string> args, out string value)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg.StartsWith(CommandLineSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg[(CommandLineSwitch.Length + 1)..].Trim('"');
                if (!string.IsNullOrWhiteSpace(value)) return true;
            }

            if (arg.Equals(CommandLineSwitch, StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Count
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                value = args[i + 1].Trim('"');
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string DefaultPath(string? fallbackRoot)
    {
        // Windows: %APPDATA%\CraftConsole. Unix: $XDG_CONFIG_HOME/CraftConsole,
        // or ~/.config/CraftConsole.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // A daemon account with no HOME gets an empty string here. Path.Combine would then
        // hand back the bare relative name "CraftConsole", quietly rooting the data directory
        // at whatever the current working directory happens to be.
        if (string.IsNullOrWhiteSpace(appData))
            return Path.Combine(fallbackRoot ?? AppContext.BaseDirectory, "data");

        return Path.Combine(appData, FolderName);
    }
}
