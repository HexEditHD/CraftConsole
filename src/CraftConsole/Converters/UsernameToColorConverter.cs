using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CraftConsole.Converters;

public class UsernameToColorConverter : IValueConverter
{
    public static readonly UsernameToColorConverter Instance = new();

    private static readonly string[] Palette =
    [
        "#06B6D4", "#8B5CF6", "#10B981", "#F59E0B",
        "#3B82F6", "#EC4899", "#14B8A6", "#A855F7",
    ];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? string.Empty;
        var hash = Math.Abs(name.GetHashCode());
        var hex  = Palette[hash % Palette.Length];
        try { return new SolidColorBrush(Color.Parse(hex)); }
        catch { return new SolidColorBrush(Colors.Gray); }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
