using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CraftConsole.Core.Models;
using CraftConsole.Infrastructure.Config;

namespace CraftConsole.Converters;

public class LevelToColorConverter : IValueConverter
{
    public static readonly LevelToColorConverter Instance = new();

    /// <summary>Set by App.axaml.cs after loading AppSettings. New log lines read live colors.</summary>
    public static AppSettings? CurrentSettings { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ConsoleEntryLevel level)
            return new SolidColorBrush(Color.Parse("#F1F5F9"));

        var s = CurrentSettings;
        return level switch
        {
            ConsoleEntryLevel.Warn  => Brush(s?.ColorWarn  ?? "#FB923C"),
            ConsoleEntryLevel.Error => Brush(s?.ColorError ?? "#F87171"),
            ConsoleEntryLevel.Debug => Brush("#475569"),
            ConsoleEntryLevel.Info  => Brush(s?.ColorInfo  ?? "#94A3B8"),
            ConsoleEntryLevel.Input => Brush("#22D3EE"),
            _                       => Brush("#64748B"),
        };
    }

    private static SolidColorBrush Brush(string hex)
    {
        try { return new SolidColorBrush(Color.Parse(hex)); }
        catch { return new SolidColorBrush(Color.Parse("#94A3B8")); }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
