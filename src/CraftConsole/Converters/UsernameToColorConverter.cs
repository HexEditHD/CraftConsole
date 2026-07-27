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
        var hash = GetStableHash(name);
        var hex  = Palette[(int)(hash % (uint)Palette.Length)];
        try { return new SolidColorBrush(Color.Parse(hex)); }
        catch { return new SolidColorBrush(Colors.Gray); }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    // string.GetHashCode() is randomized per-process (hash randomization), so it
    // can't be used for a color that should stay stable across app restarts.
    // FNV-1a over the invariant-uppercase name gives a deterministic, unsigned
    // hash (no Math.Abs overflow on int.MinValue) that's also case-insensitive.
    internal static uint GetStableHash(string name)
    {
        const uint fnvOffsetBasis = 2166136261;
        const uint fnvPrime = 16777619;

        var hash = fnvOffsetBasis;
        foreach (var c in name.ToUpperInvariant())
        {
            hash ^= c;
            hash *= fnvPrime;
        }

        return hash;
    }
}
