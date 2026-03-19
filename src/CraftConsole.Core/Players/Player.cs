namespace CraftConsole.Core.Players;

public class Player
{
    public required string Username { get; init; }
    public DateTimeOffset JoinedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? IpAddress { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public string? Location { get; set; }
}
