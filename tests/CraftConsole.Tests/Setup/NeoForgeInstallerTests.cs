using CraftConsole.Infrastructure.Http;
using Xunit;

namespace CraftConsole.Tests.Setup;

public class NeoForgeInstallerTests
{
    [Fact]
    public void BuildArguments_quotes_the_jar_path_and_passes_the_version_to_installer()
    {
        var args = NeoForgeInstaller.BuildArguments(@"C:\servers\my-server\server.jar", "26.2.0.45-beta");

        Assert.Equal("-jar \"C:\\servers\\my-server\\server.jar\" --installer 26.2.0.45-beta", args);
    }
}
