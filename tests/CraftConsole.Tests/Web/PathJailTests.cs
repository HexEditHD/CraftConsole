using CraftConsole.Web.Api;
using Xunit;

namespace CraftConsole.Tests.Web;

/// <summary>
/// The file editor exposes read and write over a client-supplied path. This
/// containment check is the only thing keeping that inside the server directory,
/// so it is tested directly rather than only through the endpoints.
/// </summary>
public class PathJailTests
{
    private static readonly string Root =
        OperatingSystem.IsWindows() ? @"C:\servers\survival" : "/srv/minecraft/survival";

    [Theory]
    [InlineData("server.properties")]
    [InlineData("plugins/config.yml")]
    [InlineData("world/data/scoreboard.dat")]
    [InlineData("./server.properties")]
    [InlineData("plugins/../server.properties")]   // normalises back inside
    public void Accepts_paths_that_stay_inside_the_root(string relative)
    {
        var resolved = WorkspaceApi.ResolveJailedPath(Root, relative);

        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(Root), resolved);
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("plugins/../../../etc/shadow")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_paths_that_climb_out_of_the_root(string relative)
    {
        Assert.Null(WorkspaceApi.ResolveJailedPath(Root, relative));
    }

    [Fact]
    public void Rejects_an_absolute_path()
    {
        // Path.Combine discards the root when the second argument is rooted, so
        // the containment comparison is what has to catch this.
        var absolute = OperatingSystem.IsWindows() ? @"C:\Windows\System32\drivers\etc\hosts" : "/etc/passwd";

        Assert.Null(WorkspaceApi.ResolveJailedPath(Root, absolute));
    }

    [Fact]
    public void Rejects_a_unc_path()
    {
        if (!OperatingSystem.IsWindows()) return; // UNC paths (\\server\share\...) are a Windows concept

        // Path.IsPathRooted treats a UNC path as rooted, so — exactly like
        // Rejects_an_absolute_path above — Path.Combine discards the root and the
        // containment comparison is what actually rejects it.
        Assert.Null(WorkspaceApi.ResolveJailedPath(Root, @"\\attacker-host\share\evil.txt"));
    }

    [Fact]
    public void Rejects_a_sibling_directory_that_merely_shares_a_prefix()
    {
        // "/srv/minecraft/survival-old" starts with the root string but is a
        // different directory — the check appends a separator to prevent this.
        Assert.Null(WorkspaceApi.ResolveJailedPath(Root, "../survival-old/server.properties"));
    }

    [Fact]
    public void Rejects_the_root_itself()
    {
        Assert.Null(WorkspaceApi.ResolveJailedPath(Root, "."));
    }

    // ── Plugin file names ─────────────────────────────────────────────────

    [Theory]
    [InlineData("EssentialsX.jar")]
    [InlineData("worldedit-bukkit-7.2.jar")]
    public void Accepts_plain_plugin_file_names(string fileName)
    {
        Assert.True(WorkspaceApi.IsSafeFileName(fileName));
    }

    [Theory]
    [InlineData("../evil.jar")]
    [InlineData("sub/evil.jar")]
    [InlineData("sub\\evil.jar")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_plugin_file_names_that_address_anything_but_a_file(string fileName)
    {
        Assert.False(WorkspaceApi.IsSafeFileName(fileName));
    }
}
