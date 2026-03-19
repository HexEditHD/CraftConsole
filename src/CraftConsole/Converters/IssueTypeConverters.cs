using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CraftConsole.Core.Models;

namespace CraftConsole.Converters;

public class IssueTypeToBgConverter : IValueConverter
{
    public static readonly IssueTypeToBgConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is IssueType type
            ? type switch
            {
                IssueType.Warning => new SolidColorBrush(Color.Parse("#2D1F0A")),
                IssueType.Severe  => new SolidColorBrush(Color.Parse("#2D1515")),
                _                 => new SolidColorBrush(Color.Parse("#0F1E38")),
            }
            : new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class IssueTypeToFgConverter : IValueConverter
{
    public static readonly IssueTypeToFgConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is IssueType type
            ? type switch
            {
                IssueType.Warning => new SolidColorBrush(Color.Parse("#FB923C")),
                IssueType.Severe  => new SolidColorBrush(Color.Parse("#F87171")),
                _                 => new SolidColorBrush(Color.Parse("#60A5FA")),
            }
            : new SolidColorBrush(Colors.White);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
