using CraftConsole.Core.Rcon;
using Xunit;

namespace CraftConsole.Tests.Rcon;

/// <summary>
/// Exercises RconClient against a real (if minimal) TCP server rather than
/// mocking the stream — the framing and multi-packet reassembly are exactly
/// the parts most likely to have an off-by-one, and only a real socket
/// round-trip actually proves the byte layout matches on both ends.
/// </summary>
public class RconClientTests
{
    [Fact]
    public async Task Connects_and_authenticates_with_the_correct_password()
    {
        await using var server = new FakeRconServer("hunter2");
        await using var client = new RconClient("127.0.0.1", server.Port);

        await client.ConnectAsync("hunter2");

        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Rejects_the_wrong_password_with_RconAuthException()
    {
        await using var server = new FakeRconServer("hunter2");
        await using var client = new RconClient("127.0.0.1", server.Port);

        await Assert.ThrowsAsync<RconAuthException>(() => client.ConnectAsync("wrong-password"));
    }

    [Fact]
    public async Task Executing_a_command_returns_its_reply()
    {
        await using var server = new FakeRconServer("hunter2");
        server.Replies["say hello"] = "[Server] hello";

        await using var client = new RconClient("127.0.0.1", server.Port);
        await client.ConnectAsync("hunter2");

        var reply = await client.ExecuteAsync("say hello");

        Assert.Equal("[Server] hello", reply);
        Assert.Contains("say hello", server.ReceivedCommands);
    }

    [Fact]
    public async Task An_empty_reply_round_trips_as_an_empty_string_not_an_error()
    {
        await using var server = new FakeRconServer("hunter2");
        // "say hello" with no canned reply configured — the fake server's default.

        await using var client = new RconClient("127.0.0.1", server.Port);
        await client.ConnectAsync("hunter2");

        var reply = await client.ExecuteAsync("say hello");

        Assert.Equal("", reply);
    }

    [Fact]
    public async Task A_reply_over_4096_bytes_is_reassembled_from_multiple_packets()
    {
        // The base protocol has no "final packet" marker; ExecuteAsync's dummy-packet
        // technique is what makes this work at all, so this is the single most
        // important case to prove against a real socket rather than trust in the abstract.
        var longReply = string.Concat(Enumerable.Range(0, 5000).Select(i => (char)('a' + i % 26)));

        await using var server = new FakeRconServer("hunter2");
        server.Replies["dump"] = longReply;

        await using var client = new RconClient("127.0.0.1", server.Port);
        await client.ConnectAsync("hunter2");

        var reply = await client.ExecuteAsync("dump");

        Assert.Equal(longReply.Length, reply.Length);
        Assert.Equal(longReply, reply);
    }

    [Fact]
    public async Task Multiple_commands_on_one_connection_each_get_their_own_reply()
    {
        await using var server = new FakeRconServer("hunter2");
        server.Replies["list"] = "There are 0 of a max of 20 players online: ";
        server.Replies["version"] = "Paper 1.21.4";

        await using var client = new RconClient("127.0.0.1", server.Port);
        await client.ConnectAsync("hunter2");

        Assert.Equal("There are 0 of a max of 20 players online: ", await client.ExecuteAsync("list"));
        Assert.Equal("Paper 1.21.4", await client.ExecuteAsync("version"));
        Assert.Equal("There are 0 of a max of 20 players online: ", await client.ExecuteAsync("list"));
    }

    [Fact]
    public async Task Executing_before_connecting_throws_InvalidOperationException()
    {
        await using var client = new RconClient("127.0.0.1", 25575);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync("list"));
    }

    [Fact]
    public async Task A_command_after_the_server_drops_the_connection_fails_cleanly()
    {
        await using var server = new FakeRconServer("hunter2") { DropAfterAuth = true };
        await using var client = new RconClient("127.0.0.1", server.Port);
        await client.ConnectAsync("hunter2");

        // Any transport failure is acceptable here — the point is that it throws
        // rather than hanging, and doesn't corrupt the client's internal state.
        await Assert.ThrowsAnyAsync<Exception>(() => client.ExecuteAsync("list"));
    }

    [Fact]
    public async Task Connecting_to_a_closed_port_fails_rather_than_hanging()
    {
        // A port nothing is listening on, on loopback, refuses immediately —
        // this proves the failure path itself, not the timeout path.
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var unusedPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var client = new RconClient("127.0.0.1", unusedPort);

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync("hunter2"));
    }
}
