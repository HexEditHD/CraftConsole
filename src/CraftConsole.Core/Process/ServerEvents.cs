using CraftConsole.Core.Players;

namespace CraftConsole.Core.Process;

public abstract record ServerEvent;

public record PlayerJoinedEvent(Player Player) : ServerEvent;
public record PlayerLeftEvent(string Username) : ServerEvent;
public record PlayerChatEvent(string Username, string Message) : ServerEvent;
public record ServerReadyEvent : ServerEvent;
public record ServerOverloadedEvent(double MsPerTick) : ServerEvent;
