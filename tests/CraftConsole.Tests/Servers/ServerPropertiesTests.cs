using CraftConsole.Core.Servers;
using Xunit;

namespace CraftConsole.Tests.Servers;

public class ServerPropertiesTests
{
    [Fact]
    public void Reads_a_key_that_is_present()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "server.properties"), "server-port=25566\nmax-players=10\n");

        Assert.Equal("25566", ServerProperties.Read(dir, "server-port"));
        Assert.Equal("10", ServerProperties.Read(dir, "max-players"));
    }

    [Fact]
    public void Returns_null_when_the_key_is_absent()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "server.properties"), "max-players=10\n");

        Assert.Null(ServerProperties.Read(dir, "server-port"));
    }

    [Fact]
    public void Returns_null_when_the_file_does_not_exist_yet()
    {
        // The normal state before a profile's first launch — Minecraft only
        // writes server.properties on its own first start.
        Assert.Null(ServerProperties.Read(NewDir(), "server-port"));
    }

    [Fact]
    public void Returns_null_for_an_empty_or_missing_directory()
    {
        Assert.Null(ServerProperties.Read("", "server-port"));
        Assert.Null(ServerProperties.Read(Path.Combine(Path.GetTempPath(), "cc-does-not-exist-" + Guid.NewGuid()), "server-port"));
    }

    [Fact]
    public void Key_lookup_is_case_insensitive()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "server.properties"), "Server-Port=25566\n");

        Assert.Equal("25566", ServerProperties.Read(dir, "server-port"));
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-server-properties-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
