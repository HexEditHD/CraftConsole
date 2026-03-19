namespace CraftConsole.Core.Models;

public enum IssueType { Info, Warning, Severe }

public class IssueEntry
{
    public int Id { get; init; }
    public IssueType Type { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Message { get; init; } = string.Empty;
}
