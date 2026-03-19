using System.Text.RegularExpressions;
using CraftConsole.Core.Models;

namespace CraftConsole.Core.Process;

public static class ConsoleOutputParser
{
    // Matches: [HH:mm:ss] [Thread/LEVEL]: message  (Paper/Spigot/Vanilla format)
    private static readonly Regex LogPattern = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2})\] \[(?<source>[^\]]+)/(?<level>INFO|WARN|ERROR|DEBUG)\]: (?<msg>.+)$",
        RegexOptions.Compiled);

    public static ConsoleEntry Parse(string raw)
    {
        var match = LogPattern.Match(raw);

        if (!match.Success)
            return new ConsoleEntry(DateTimeOffset.Now, raw, raw, ConsoleEntryLevel.Unknown);

        var level = match.Groups["level"].Value switch
        {
            "INFO"  => ConsoleEntryLevel.Info,
            "WARN"  => ConsoleEntryLevel.Warn,
            "ERROR" => ConsoleEntryLevel.Error,
            "DEBUG" => ConsoleEntryLevel.Debug,
            _       => ConsoleEntryLevel.Unknown
        };

        return new ConsoleEntry(
            Timestamp: DateTimeOffset.Now,
            Raw: raw,
            Message: match.Groups["msg"].Value,
            Level: level,
            Source: match.Groups["source"].Value
        );
    }
}
