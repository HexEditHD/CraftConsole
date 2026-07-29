using CraftConsole.Core.Models;

namespace CraftConsole.Core.Servers;

public interface IMinecraftServer
{
    ServerProfile Profile { get; }
    ServerStatus Status { get; }
    int? ProcessId { get; }

    /// <summary>Exit code of the most recent run; null while running or if unavailable.</summary>
    int? ExitCode { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SendCommandAsync(string command, CancellationToken ct = default);

    IObservable<ConsoleEntry> ConsoleOutput { get; }
    IObservable<ServerStatus> StatusChanged { get; }
}
