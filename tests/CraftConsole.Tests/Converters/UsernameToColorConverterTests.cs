using System.Globalization;
using Avalonia.Media;
using CraftConsole.Converters;
using Xunit;

namespace CraftConsole.Tests.Converters;

public class UsernameToColorConverterTests
{
    private static Color Convert(string username)
    {
        var brush = Assert.IsType<SolidColorBrush>(
            UsernameToColorConverter.Instance.Convert(username, typeof(object), null, CultureInfo.InvariantCulture));
        return brush.Color;
    }

    // Expected colors were derived from the converter's deterministic hash for
    // these exact usernames; if this ever fails, either the hash algorithm or
    // the Palette array changed.
    [Theory]
    [InlineData("Steve", "#3B82F6")]
    [InlineData("Alex", "#EC4899")]
    public void Convert_maps_known_username_to_expected_palette_color(string username, string expectedHex)
    {
        Assert.Equal(Color.Parse(expectedHex), Convert(username));
    }

    [Theory]
    [InlineData("Steve")]
    [InlineData("Notch")]
    [InlineData("herobrine")]
    public void Convert_returns_the_same_color_across_repeated_calls(string username)
    {
        var first = Convert(username);
        var second = Convert(username);
        var third = Convert(username);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void Convert_is_case_insensitive_for_the_same_username()
    {
        Assert.Equal(Convert("steve"), Convert("STEVE"));
        Assert.Equal(Convert("steve"), Convert("Steve"));
    }
}
