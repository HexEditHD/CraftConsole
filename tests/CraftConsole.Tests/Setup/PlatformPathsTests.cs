using CraftConsole.Web.Services;
using Xunit;

namespace CraftConsole.Tests.Setup;

public class PlatformPathsTests
{
    private static Func<string, string?> NoEnvironment => _ => null;
    private static Func<string, string?> Env(string value) => _ => value;

    [Fact]
    public void DefaultServerRoot_and_DefaultBackupRoot_differ_and_are_rooted()
    {
        var serverRoot = PlatformPaths.DefaultServerRoot(NoEnvironment);
        var backupRoot = PlatformPaths.DefaultBackupRoot(NoEnvironment);

        Assert.NotEqual(serverRoot, backupRoot);
        Assert.True(Path.IsPathRooted(serverRoot));
        Assert.True(Path.IsPathRooted(backupRoot));
    }

    [Fact]
    public void DefaultServerRoot_uses_srv_minecraft_on_linux()
    {
        if (!OperatingSystem.IsLinux()) return;

        Assert.Equal("/srv/minecraft", PlatformPaths.DefaultServerRoot(NoEnvironment));
        Assert.Equal("/srv/minecraft-backups", PlatformPaths.DefaultBackupRoot(NoEnvironment));

        // Linux ignores SystemDrive — that's a Windows-only concept.
        Assert.Equal("/srv/minecraft", PlatformPaths.DefaultServerRoot(Env("D:")));
    }

    [Fact]
    public void DefaultServerRoot_uses_the_system_drive_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(@"D:\MinecraftServers", PlatformPaths.DefaultServerRoot(Env("D:")));
        Assert.Equal(@"D:\MinecraftServers\Backups", PlatformPaths.DefaultBackupRoot(Env("D:")));
    }

    [Fact]
    public void DefaultServerRoot_falls_back_to_C_drive_on_windows_when_SystemDrive_is_blank()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(@"C:\MinecraftServers", PlatformPaths.DefaultServerRoot(NoEnvironment));
        Assert.Equal(@"C:\MinecraftServers\Backups", PlatformPaths.DefaultBackupRoot(NoEnvironment));
    }
}
