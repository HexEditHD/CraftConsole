using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CraftConsole.Converters;

public class StringToBrushConverter : IValueConverter
{
    public static readonly StringToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex) return new SolidColorBrush(Colors.Transparent);
        try { return new SolidColorBrush(Color.Parse(hex)); }
        catch { return new SolidColorBrush(Colors.Transparent); }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
