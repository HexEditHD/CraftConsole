using System.Net;
using System.Net.Sockets;
using CraftConsole.Core.Rcon;

namespace CraftConsole.Tests.Rcon;

/// <summary>
/// A minimal Source RCON server for testing RconClient against, bound to an
/// OS-assigned loopback port. Deliberately not modelled on
/// CraftConsole.FakeServer: that is a child process configured by process-wide
/// environment variables, which is why its tests need DisableParallelization.
/// A TCP listener needs neither — many instances can run in one test process
/// at once, each on its own port.
/// </summary>
public sealed class FakeRconServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _expectedPassword;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    /// <summary>Non-empty command bodies received, in order.</summary>
    public List<string> ReceivedCommands { get; } = [];

    /// <summary>Canned reply for a given command body; commands not listed here get an empty reply.</summary>
    public Dictionary<string, string> Replies { get; } = new();

    /// <summary>When true, the connection is dropped immediately after a successful auth.</summary>
    public bool DropAfterAuth { get; set; }

    public int Port { get; }

    public FakeRconServer(string expectedPassword)
    {
        _expectedPassword = expectedPassword;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                await HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var stream = client.GetStream();

        var authPacket = await RconPacket.ReadAsync(stream, ct);
        if (authPacket is null || authPacket.Type != RconPacketType.Auth) return;

        var authed = authPacket.Body == _expectedPassword;

        // Spec-conformant: an empty RESPONSE_VALUE ack, then the AUTH_RESPONSE
        // (id echoed on success, -1 on failure).
        await WriteAsync(stream, new RconPacket(authPacket.Id, RconPacketType.ResponseValue, ""), ct);
        await WriteAsync(stream, new RconPacket(authed ? authPacket.Id : -1, RconPacketType.AuthResponse, ""), ct);

        if (!authed || DropAfterAuth) return;

        while (!ct.IsCancellationRequested)
        {
            var packet = await RconPacket.ReadAsync(stream, ct);
            if (packet is null) return; // client disconnected

            if (packet.Body.Length > 0)
                lock (ReceivedCommands) ReceivedCommands.Add(packet.Body);

            var reply = packet.Body.Length > 0 && Replies.TryGetValue(packet.Body, out var canned) ? canned : "";

            // Every received packet — including the client's empty dummy/sentinel —
            // gets at least one reply packet echoing its id back. For the dummy
            // that single empty echo IS the "no more coming" marker the real
            // client's ExecuteAsync is watching for; for a long reply, splitting
            // it into several packets here is what exercises reassembly.
            var chunks = ChunkReply(reply).ToList();
            if (chunks.Count == 0) chunks.Add("");
            foreach (var chunk in chunks)
                await WriteAsync(stream, new RconPacket(packet.Id, RconPacketType.ResponseValue, chunk), ct);
        }
    }

    private static IEnumerable<string> ChunkReply(string reply)
    {
        const int chunkSize = 4000;
        for (var i = 0; i < reply.Length; i += chunkSize)
            yield return reply.Substring(i, Math.Min(chunkSize, reply.Length - i));
    }

    private static Task WriteAsync(Stream stream, RconPacket packet, CancellationToken ct)
        => stream.WriteAsync(packet.Encode(), ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _acceptLoop; } catch { /* expected on shutdown */ }
    }
}
