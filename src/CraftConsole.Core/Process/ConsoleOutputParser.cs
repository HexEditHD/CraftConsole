using System.Text.RegularExpressions;
using CraftConsole.Core.Models;

namespace CraftConsole.Core.Process;

public static class ConsoleOutputParser
{
    // Vanilla / old Spigot: [HH:mm:ss] [Server thread/INFO]: message
    private static readonly Regex LogPatternFull = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2})\] \[(?<source>[^\]]+)/(?<level>INFO|WARN|ERROR|DEBUG)\]: (?<msg>.+)$",
        RegexOptions.Compiled);

    // Paper 1.18+ / Log4j2 pattern "[%d{HH:mm:ss} %level]: %msg":
    //   [HH:mm:ss INFO]: message
    private static readonly Regex LogPatternSimple = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2}) (?<level>INFO|WARN|ERROR|DEBUG)\]: (?<msg>.+)$",
        RegexOptions.Compiled);

    // Strips ANSI escape sequences (colour codes output by Paper/Spigot)
    private static readonly Regex AnsiStrip = new(
        @"\x1B\[[0-9;]*[mGKHF]", RegexOptions.Compiled);

    public static ConsoleEntry Parse(string raw)
    {
        var clean = AnsiStrip.Replace(raw, string.Empty);

        var match = LogPatternFull.Match(clean);
        if (!match.Success)
            match = LogPatternSimple.Match(clean);

        if (!match.Success)
            return new ConsoleEntry(DateTimeOffset.Now, clean, clean, ConsoleEntryLevel.Unknown);

        var level = match.Groups["level"].Value switch
        {
            "INFO"  => ConsoleEntryLevel.Info,
            "WARN"  => ConsoleEntryLevel.Warn,
            "ERROR" => ConsoleEntryLevel.Error,
            "DEBUG" => ConsoleEntryLevel.Debug,
            _       => ConsoleEntryLevel.Unknown
        };

        var source = match.Groups["source"].Success ? match.Groups["source"].Value : string.Empty;

        return new ConsoleEntry(
            Timestamp: DateTimeOffset.Now,
            Raw: clean,
            Message: match.Groups["msg"].Value,
            Level: level,
            Source: source
        );
    }
}
