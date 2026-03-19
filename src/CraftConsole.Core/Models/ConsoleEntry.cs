namespace CraftConsole.Core.Models;

public enum ConsoleEntryLevel { Info, Warn, Error, Debug, Unknown, Input }

public record ConsoleEntry(
    DateTimeOffset Timestamp,
    string Raw,
    string Message,
    ConsoleEntryLevel Level,
    string? Source = null
);
