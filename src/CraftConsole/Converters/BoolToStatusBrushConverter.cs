using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CraftConsole.Converters;

/// <summary>Returns AccentBrush (green) when true, TextMutedBrush (gray) when false.</summary>
public class BoolToStatusBrushConverter : IValueConverter
{
    public static readonly BoolToStatusBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
        {
            // Try to get the accent brush from resources, fall back to hardcoded green
            if (Application.Current?.TryGetResource("AccentBrush", null, out var accent) == true && accent is IBrush accentBrush)
                return accentBrush;
            return new SolidColorBrush(Color.Parse("#22C55E"));
        }

        if (Application.Current?.TryGetResource("TextMutedBrush", null, out var muted) == true && muted is IBrush mutedBrush)
            return mutedBrush;
        return new SolidColorBrush(Color.Parse("#64748B"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
