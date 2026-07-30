namespace CraftConsole.Core.Servers;

/// <summary>
/// What an <see cref="IMinecraftServer"/> implementation can actually do.
/// <see cref="Models.ServerStatus"/> alone used to drive every affordance in the
/// UI, which left no way for a connection to say "I can't do that" — Start and
/// Restart rendered enabled for a server the panel cannot start, and an empty
/// ban list looked identical to there being no bans. Callers should check the
/// relevant flag before assuming a capability a managed process has always had.
/// </summary>
public sealed record ServerCapabilities(
    bool CanStart,
    bool CanStop,
    bool CanRestart,
    bool HasConsoleStream,
    bool HasProcessMetrics,
    bool HasLocalFiles,
    bool HasUptime,
    bool HasPlayerDetail)
{
    /// <summary>A process the panel launched and owns.</summary>
    public static readonly ServerCapabilities Managed = new(
        CanStart: true, CanStop: true, CanRestart: true,
        HasConsoleStream: true, HasProcessMetrics: true, HasLocalFiles: true,
        HasUptime: true, HasPlayerDetail: true);

    /// <summary>
    /// A server reached over RCON. CanStart stays true even when already
    /// connected-or-attempted — Start doubles as "(re)connect" here, since RCON
    /// has no process to hand back for a plain restart. Stop is available too —
    /// "stop" is just a command — but it is one-way: there is no local process
    /// left to relaunch afterwards, so CanRestart is false even while CanStop is.
    /// </summary>
    public static readonly ServerCapabilities Rcon = new(
        CanStart: true, CanStop: true, CanRestart: false,
        HasConsoleStream: false, HasProcessMetrics: false, HasLocalFiles: false,
        HasUptime: false, HasPlayerDetail: false);
}
