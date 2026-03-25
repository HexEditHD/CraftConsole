using CommunityToolkit.Mvvm.ComponentModel;

namespace CraftConsole.Core.Players;

public partial class Player : ObservableObject
{
    public required string Username { get; init; }
    public DateTimeOffset JoinedAt { get; init; } = DateTimeOffset.UtcNow;
    [ObservableProperty] private string? _ipAddress;
    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private DateTimeOffset? _lastSeen;
    [ObservableProperty] private string? _location;
}
