using CraftConsole.Core.Models;

namespace CraftConsole.Core.Servers;

public interface IMinecraftServer : IAsyncDisposable
{
    ServerProfile Profile { get; }
    ServerStatus Status { get; }
    int? ProcessId { get; }
    ServerCapabilities Capabilities { get; }

    /// <summary>
    /// The real player cap where the transport can learn it directly (RCON's
    /// `list` reply) rather than needing server.properties; null when it can't
    /// (a managed process — ServerSupervisor already has that from the file).
    /// </summary>
    int? MaxPlayers { get; }

    /// <summary>Exit code of the most recent run; null while running or if unavailable.</summary>
    int? ExitCode { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends a command. Returns its reply where the transport has one (RCON's
    /// response packet); null where output arrives later and asynchronously via
    /// <see cref="ConsoleOutput"/> instead, as a managed process's stdout does.
    /// </summary>
    Task<string?> SendCommandAsync(string command, CancellationToken ct = default);

    IObservable<ConsoleEntry> ConsoleOutput { get; }
    IObservable<ServerStatus> StatusChanged { get; }
}
