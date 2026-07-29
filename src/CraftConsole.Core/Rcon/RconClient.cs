using System.Net.Sockets;
using System.Text;

namespace CraftConsole.Core.Rcon;

/// <summary>Thrown when the server rejects the RCON password, distinct from a transport failure.</summary>
public sealed class RconAuthException(string message) : Exception(message);

/// <summary>
/// Source RCON client: connect, authenticate, execute commands, get replies.
///
/// RCON is a single request/response channel per connection — only one command
/// can be in flight at a time, enforced here with a gate. This class does not
/// reconnect on its own; a dropped connection surfaces as an exception from
/// <see cref="ExecuteAsync"/>, and reconnect-with-backoff is the caller's job
/// (see RconMinecraftServer), which is also where "are we connected" status
/// naturally lives.
/// </summary>
public sealed class RconClient : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _nextId = 1;
    private bool _disposed;

    public bool IsConnected => _tcp?.Connected == true;

    public RconClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>
    /// Connects and authenticates. Throws <see cref="RconAuthException"/> for a
    /// wrong password (a real, expected outcome to show the user), or an
    /// <see cref="IOException"/>/<see cref="SocketException"/> for anything else.
    /// </summary>
    public async Task ConnectAsync(string password, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DisconnectCore();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ConnectTimeout);

            var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(_host, _port, timeoutCts.Token);
                var stream = tcp.GetStream();

                var authId = NextId();
                await WritePacketAsync(stream, new RconPacket(authId, RconPacketType.Auth, password), timeoutCts.Token);

                // The spec has the server send an empty RESPONSE_VALUE ack before the
                // AUTH_RESPONSE; some implementations skip straight to AUTH_RESPONSE.
                // Read until that type arrives rather than assuming a fixed count.
                RconPacket? authResponse = null;
                for (var i = 0; i < 4 && authResponse is null; i++)
                {
                    var packet = await RconPacket.ReadAsync(stream, timeoutCts.Token)
                        ?? throw new IOException("Connection closed during authentication.");
                    if (packet.Type == RconPacketType.AuthResponse)
                        authResponse = packet;
                }

                if (authResponse is null)
                    throw new IOException("The server never sent an authentication response.");

                if (authResponse.Id == -1)
                    throw new RconAuthException("The RCON server rejected the password.");

                _tcp = tcp;
                _stream = stream;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new IOException($"Timed out connecting to {_host}:{_port}.");
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Sends a command and returns its full reply, reassembled across packets if needed.</summary>
    public async Task<string> ExecuteAsync(string command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream is null) throw new InvalidOperationException("Not connected.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CommandTimeout);

            try
            {
                var commandId = NextId();
                var sentinelId = NextId();

                await WritePacketAsync(_stream, new RconPacket(commandId, RconPacketType.ExecCommand, command), timeoutCts.Token);
                // A reply over ~4096 bytes arrives split with no marker for "that was
                // the last one" — the base protocol has none. This dummy packet's
                // echoed id is the marker: everything with commandId that arrived
                // before it is the complete reply. `list` on a full server routinely
                // exceeds 4096 bytes, so this is not an edge case to skip.
                await WritePacketAsync(_stream, new RconPacket(sentinelId, RconPacketType.ExecCommand, ""), timeoutCts.Token);

                var reply = new StringBuilder();
                while (true)
                {
                    var packet = await RconPacket.ReadAsync(_stream, timeoutCts.Token)
                        ?? throw new IOException("Connection closed while waiting for a reply.");

                    if (packet.Id == sentinelId) break;
                    if (packet.Id == commandId) reply.Append(packet.Body);
                    // Anything else — a stray id from a desynced or buggy server — is
                    // dropped rather than risking corruption of the reassembled reply.
                }

                return reply.ToString();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // However many packets are still coming for this command are now
                // unaccounted for; reusing the connection risks attributing them to
                // the next request. Close it so ConnectAsync starts clean.
                DisconnectCore();
                throw new IOException($"Timed out waiting for a reply to \"{command}\".");
            }
        }
        finally { _gate.Release(); }
    }

    private int NextId()
    {
        var id = _nextId;
        _nextId = _nextId == int.MaxValue ? 1 : _nextId + 1;
        return id;
    }

    private static Task WritePacketAsync(Stream stream, RconPacket packet, CancellationToken ct)
        => stream.WriteAsync(packet.Encode(), ct).AsTask();

    private void DisconnectCore()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            DisconnectCore();
        }
        finally { _gate.Release(); }
    }
}
