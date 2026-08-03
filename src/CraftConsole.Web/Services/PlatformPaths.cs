namespace CraftConsole.Web.Services;

/// <summary>
/// Sensible per-OS defaults for where a user would put server and backup data —
/// distinct from <see cref="DataPath"/>, which resolves where the panel keeps
/// its own settings/profiles/logs. Never used to create directories at startup;
/// only as prefilled suggestions the user can overwrite.
/// </summary>
public static class PlatformPaths
{
    /// <param name="environment">
    /// Environment-variable lookup; defaults to the real environment. Injectable for tests.
    /// </param>
    public static string DefaultServerRoot(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        if (OperatingSystem.IsWindows())
            return Path.Combine(SystemDrive(environment), "MinecraftServers");

        // /srv is the FHS location for data served by this host, whether the
        // panel was installed from the .deb (which provisions and owns it) or
        // is just being run manually.
        if (OperatingSystem.IsLinux())
            return "/srv/minecraft";

        if (environment("HOME") is { Length: > 0 } home)
            return Path.Combine(home, "minecraft-servers");

        return "/srv/minecraft";
    }

    public static string DefaultBackupRoot(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        if (OperatingSystem.IsWindows())
            return Path.Combine(SystemDrive(environment), "MinecraftServers", "Backups");

        if (OperatingSystem.IsLinux())
            return "/srv/minecraft-backups";

        if (environment("HOME") is { Length: > 0 } home)
            return Path.Combine(home, "minecraft-servers", "backups");

        return "/srv/minecraft-backups";
    }

    private static string SystemDrive(Func<string, string?> environment)
        => environment("SystemDrive") is { Length: > 0 } drive ? drive : "C:";
}
