using CraftConsole.Core.Players;
using Xunit;

namespace CraftConsole.Tests.Players;

public class UsernameColorTests
{
    // Expected colors were derived from the deterministic hash for these exact
    // usernames; if this ever fails, either the hash algorithm or the Palette
    // array changed — both would shuffle every player's color.
    [Theory]
    [InlineData("Steve", "#3B82F6")]
    [InlineData("Alex", "#EC4899")]
    public void GetHex_maps_known_username_to_expected_palette_color(string username, string expectedHex)
    {
        Assert.Equal(expectedHex, UsernameColor.GetHex(username));
    }

    [Theory]
    [InlineData("Steve")]
    [InlineData("Notch")]
    [InlineData("herobrine")]
    public void GetHex_returns_the_same_color_across_repeated_calls(string username)
    {
        var first = UsernameColor.GetHex(username);

        Assert.Equal(first, UsernameColor.GetHex(username));
        Assert.Equal(first, UsernameColor.GetHex(username));
    }

    [Fact]
    public void GetHex_is_case_insensitive_for_the_same_username()
    {
        Assert.Equal(UsernameColor.GetHex("steve"), UsernameColor.GetHex("STEVE"));
        Assert.Equal(UsernameColor.GetHex("steve"), UsernameColor.GetHex("Steve"));
    }

    [Fact]
    public void GetHex_always_returns_a_palette_entry()
    {
        foreach (var name in new[] { "", "x", "a_very_long_minecraft_username", "1234", "ÿ" })
            Assert.Contains(UsernameColor.GetHex(name), UsernameColor.Palette);
    }
}
