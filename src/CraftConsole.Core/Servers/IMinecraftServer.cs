using CraftConsole.Core.Models;

namespace CraftConsole.Core.Servers;

public interface IMinecraftServer
{
    ServerProfile Profile { get; }
    ServerStatus Status { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SendCommandAsync(string command, CancellationToken ct = default);

    IObservable<ConsoleEntry> ConsoleOutput { get; }
    IObservable<ServerStatus> StatusChanged { get; }
}
