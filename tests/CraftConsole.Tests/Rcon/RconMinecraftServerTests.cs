using CraftConsole.Core.Models;
using CraftConsole.Core.Servers;
using Xunit;

namespace CraftConsole.Tests.Rcon;

/// <summary>
/// End-to-end coverage of RconMinecraftServer against a real (fake) RCON
/// server, mirroring how ServerProcessManagerTests exercises the managed
/// implementation — each test proves one piece of the IMinecraftServer
/// contract, not just that the wire protocol works (RconClientTests covers that).
/// </summary>
public class RconMinecraftServerTests
{
    private static ServerProfile Profile(int port) => new()
    {
        Name = "Remote",
        Mode = ConnectionMode.Rcon,
        RconHost = "127.0.0.1",
        RconPort = port,
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Timed out waiting for: {because}.");
    }

    [Fact]
    public async Task StartAsync_connects_and_reports_Running()
    {
        await using var fake = new FakeRconServer("hunter2");
        await using var server = new RconMinecraftServer(Profile(fake.Port), "hunter2");

        await server.StartAsync();

        Assert.Equal(ServerStatus.Running, server.Status);
        Assert.Null(server.ProcessId); // no local process, ever
    }

    [Fact]
    public async Task StartAsync_with_the_wrong_password_throws_and_reports_Crashed()
    {
        await using var fake = new FakeRconServer("hunter2");
        await using var server = new RconMinecraftServer(Profile(fake.Port), "wrong-password");

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync());

        Assert.Equal(ServerStatus.Crashed, server.Status);
    }

    [Fact]
    public async Task Polling_list_surfaces_players_as_synthetic_join_lines_and_learns_the_real_max()
    {
        await using var fake = new FakeRconServer("hunter2");
        fake.Replies["list"] = "There are 2 of a max of 30 players online: Steve, Alex";

        var entries = new List<ConsoleEntry>();
        await using var server = new RconMinecraftServer(Profile(fake.Port), "hunter2");
        using var _ = server.ConsoleOutput.Subscribe(entries.Add);

        await server.StartAsync();

        await WaitUntilAsync(() => server.MaxPlayers == 30, "MaxPlayers to be learned from list");
        await WaitUntilAsync(
            () => entries.Any(e => e.Message.Contains("Steve joined the game"))
                && entries.Any(e => e.Message.Contains("Alex joined the game")),
            "synthetic join lines for both players");

        // list carries no IP — the synthetic line must look exactly like a real
        // server's IP-less "joined the game" line, not the IP-bearing "logged in" one.
        Assert.DoesNotContain(entries, e => e.Message.Contains("logged in"));
    }

    [Fact]
    public async Task A_player_leaving_produces_a_synthetic_leave_line_on_the_next_poll()
    {
        await using var fake = new FakeRconServer("hunter2");
        fake.Replies["list"] = "There are 1 of a max of 20 players online: Steve";

        var entries = new List<ConsoleEntry>();
        await using var server = new RconMinecraftServer(Profile(fake.Port), "hunter2");
        using var _ = server.ConsoleOutput.Subscribe(entries.Add);

        await server.StartAsync();
        await WaitUntilAsync(() => entries.Any(e => e.Message.Contains("Steve joined the game")), "Steve to join");

        fake.Replies["list"] = "There are 0 of a max of 20 players online: ";
        await WaitUntilAsync(() => entries.Any(e => e.Message.Contains("Steve left the game")), "Steve to leave", 8000);
    }

    [Fact]
    public async Task SendCommandAsync_returns_the_reply_directly()
    {
        await using var fake = new FakeRconServer("hunter2");
        fake.Replies["version"] = "This server is running Paper 1.21.4";

        await using var server = new RconMinecraftServer(Profile(fake.Port), "hunter2");
        await server.StartAsync();

        var reply = await server.SendCommandAsync("version");

        Assert.Equal("This server is running Paper 1.21.4", reply);
    }

    [Fact]
    public async Task StopAsync_sends_stop_and_settles_on_Stopped()
    {
        await using var fake = new FakeRconServer("hunter2");
        await using var server = new RconMinecraftServer(Profile(fake.Port), "hunter2");
        await server.StartAsync();

        await server.StopAsync();

        Assert.Equal(ServerStatus.Stopped, server.Status);
        Assert.Contains("stop", fake.ReceivedCommands);
    }

    [Fact]
    public async Task DisposeAsync_never_sends_stop()
    {
        // The panel does not own this server's lifecycle — disposing the connection
        // (switching profiles, panel shutdown) must never end the remote process.
        await using var fake = new FakeRconServer("hunter2");
        var server = new RconMinecraftServer(Profile(fake.Port), "hunter2");
        await server.StartAsync();

        await server.DisposeAsync();

        Assert.DoesNotContain("stop", fake.ReceivedCommands);
    }

    [Fact]
    public async Task Capabilities_reflect_what_RCON_can_and_cannot_do()
    {
        await using var fake = new FakeRconServer("hunter2");
        await using var server = new RconMinecraftServer(Profile(fake.Port), "hunter2");

        Assert.False(server.Capabilities.CanRestart);
        Assert.False(server.Capabilities.HasConsoleStream);
        Assert.False(server.Capabilities.HasLocalFiles);
        Assert.True(server.Capabilities.CanStop);
    }
}
