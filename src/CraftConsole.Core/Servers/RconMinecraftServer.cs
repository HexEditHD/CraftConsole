using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using CraftConsole.Core.Models;
using CraftConsole.Core.Rcon;

namespace CraftConsole.Core.Servers;

/// <summary>
/// Manages a Minecraft server the panel connects to over RCON rather than
/// launches — see <see cref="ServerCapabilities.Rcon"/> for exactly what that
/// gives up.
///
/// RCON has no log stream, only command replies, so <see cref="ConsoleOutput"/>
/// here is a transcript of what was sent and what came back — never a live feed
/// the way a managed process's stdout is. Players are discovered by polling
/// `list` on a timer rather than by parsing join/leave lines, and that same
/// poll is also how the real player cap is recovered (there is no
/// server.properties to read remotely, so ServerSupervisor's file-based
/// default of 20 would otherwise stick forever).
///
/// A dropped connection is NOT the same as the server going down — it is
/// reconnected with backoff automatically. Sending "stop", by contrast, really
/// does end the remote server's process, which is why disposal here never does
/// that on its own: the panel does not own this server's lifecycle the way it
/// owns a launched one, and closing the panel — or switching to a different
/// profile — must not shut down someone else's running server as a side effect.
/// </summary>
public sealed partial class RconMinecraftServer : IMinecraftServer
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(60);

    private readonly Subject<ConsoleEntry> _consoleSubject = new();
    private readonly Subject<ServerStatus> _statusSubject = new();
    private readonly string _password;

    // Guards _client, _status, _maxPlayers. _lastKnownPlayers is touched only
    // from inside the single connection-loop iteration and needs no lock.
    private readonly object _gate = new();
    private RconClient? _client;
    private ServerStatus _status = ServerStatus.Stopped;
    private int? _maxPlayers;
    private bool _disposed;

    private HashSet<string> _lastKnownPlayers = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _lifetimeCts;
    private Task? _connectionLoop;

    public ServerProfile Profile { get; }
    public ServerCapabilities Capabilities => ServerCapabilities.Rcon;

    public int? ProcessId => null; // no local process for MetricsSampler to sample
    public int? ExitCode => null;  // meaningless for a connection, not a process

    public ServerStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public int? MaxPlayers
    {
        get { lock (_gate) return _maxPlayers; }
    }

    public IObservable<ConsoleEntry> ConsoleOutput => _consoleSubject.AsObservable();
    public IObservable<ServerStatus> StatusChanged => _statusSubject.AsObservable();

    public RconMinecraftServer(ServerProfile profile, string password)
    {
        Profile = profile;
        _password = password;
    }

    // ── Start / stop ─────────────────────────────────────────────────────

    /// <summary>
    /// Connects and authenticates. The first attempt is awaited here, so a bad
    /// host/port or password fails fast and clearly rather than retrying
    /// silently in the background; a background loop then keeps the connection
    /// alive (polling players, reconnecting with backoff) until StopAsync or
    /// DisposeAsync.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_status is ServerStatus.Running or ServerStatus.Starting) return;
        }

        PublishStatus(ServerStatus.Starting);

        var client = new RconClient(Profile.RconHost, Profile.RconPort);
        try
        {
            await client.ConnectAsync(_password, ct);
        }
        catch (Exception ex)
        {
            await client.DisposeAsync();
            Emit($"Could not connect to {Profile.RconHost}:{Profile.RconPort}: {ex.Message}", ConsoleEntryLevel.Error);
            PublishStatus(ServerStatus.Crashed);
            throw new InvalidOperationException(
                $"Could not connect to {Profile.RconHost}:{Profile.RconPort} — {ex.Message}", ex);
        }

        lock (_gate) _client = client;
        Emit($"Connected to {Profile.RconHost}:{Profile.RconPort}.", ConsoleEntryLevel.Info);
        PublishStatus(ServerStatus.Running);

        _lifetimeCts?.Dispose();
        _lifetimeCts = new CancellationTokenSource();
        _connectionLoop = RunConnectionLoopAsync(_lifetimeCts.Token);
    }

    /// <summary>
    /// Sends "stop" — a real, one-way action: it ends the remote server's
    /// process, and the panel has no way to bring it back afterwards.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        RconClient? client;
        Task? loopTask;
        lock (_gate)
        {
            if (_status is not (ServerStatus.Running or ServerStatus.Starting)) return;
            client = _client;
            loopTask = _connectionLoop;
        }

        PublishStatus(ServerStatus.Stopping);

        // Stop polling before sending "stop" so exactly one thing is ever in
        // flight on the connection at a time.
        _lifetimeCts?.Cancel();
        if (loopTask is not null)
        {
            try { await loopTask; }
            catch { /* the loop only ever exits via its own OperationCanceledException */ }
        }

        if (client is not null)
        {
            // Best-effort: the remote process exiting drops the connection either
            // way, so a failure here doesn't change the outcome.
            try { await client.ExecuteAsync("stop", ct); }
            catch { }
        }

        lock (_gate) _client = null;
        if (client is not null) await client.DisposeAsync();

        PublishStatus(ServerStatus.Stopped);
    }

    public async Task<string?> SendCommandAsync(string command, CancellationToken ct = default)
    {
        RconClient? client;
        lock (_gate) client = _client;

        if (client is null) return "Not connected.";

        try
        {
            return await client.ExecuteAsync(command, ct);
        }
        catch (Exception ex)
        {
            // Mirrors ServerProcessManager.SendCommandAsync, which never throws
            // either — the connection loop's own reconnect handles the drop; this
            // just reports what happened to the specific command that was sent.
            return $"Command failed: {ex.Message}";
        }
    }

    // ── Connection supervision ──────────────────────────────────────────────

    private async Task RunConnectionLoopAsync(CancellationToken ct)
    {
        var backoff = InitialReconnectDelay;

        while (!ct.IsCancellationRequested)
        {
            RconClient? client;
            lock (_gate) client = _client;

            if (client is null || !client.IsConnected)
            {
                try
                {
                    var fresh = new RconClient(Profile.RconHost, Profile.RconPort);
                    await fresh.ConnectAsync(_password, ct);
                    lock (_gate) _client = fresh;
                    client = fresh;
                    Emit("Reconnected.", ConsoleEntryLevel.Info);
                    PublishStatus(ServerStatus.Running);
                    backoff = InitialReconnectDelay;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Emit($"Reconnect failed: {ex.Message}. Retrying in {backoff.TotalSeconds:0}s…", ConsoleEntryLevel.Warn);
                    PublishStatus(ServerStatus.Starting);
                    try { await Task.Delay(backoff, ct); }
                    catch (OperationCanceledException) { return; }
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxReconnectDelay.TotalSeconds));
                    continue;
                }
            }

            try
            {
                var reply = await client!.ExecuteAsync("list", ct);
                ApplyListReply(reply);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var dead = client;
                lock (_gate) _client = null;
                await dead!.DisposeAsync();

                Emit($"Lost the RCON connection: {ex.Message}.", ConsoleEntryLevel.Warn);
                PublishStatus(ServerStatus.Starting);
                continue; // reconnect attempt happens at the top of the next iteration
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    [GeneratedRegex(@"There are (?<count>\d+) of a max(?:imum)? of (?<max>\d+) players online(?::\s*(?<names>.*))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ListReplyPattern();

    private void ApplyListReply(string reply)
    {
        var match = ListReplyPattern().Match(reply);
        if (!match.Success) return; // an unrecognised reply shape — leave state as it was

        if (int.TryParse(match.Groups["max"].Value, out var max))
            lock (_gate) _maxPlayers = max;

        var namesRaw = match.Groups["names"].Success ? match.Groups["names"].Value.Trim() : "";
        var current = namesRaw.Length == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                namesRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

        // Synthesise the same join/leave lines a managed server's own console
        // would produce, so ServerSupervisor's player tracking — built entirely
        // around parsing those — needs no RCON-specific branch at all. `list`
        // carries no IP, which is exactly how a real "joined the game" line
        // (as opposed to "logged in") behaves too, so this stays honest about
        // what RCON does and doesn't know.
        foreach (var name in current.Except(_lastKnownPlayers))
            Emit($"{name} joined the game", ConsoleEntryLevel.Info);
        foreach (var name in _lastKnownPlayers.Except(current))
            Emit($"{name} left the game", ConsoleEntryLevel.Info);

        _lastKnownPlayers = current;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Emit(string message, ConsoleEntryLevel level)
        => _consoleSubject.OnNext(new ConsoleEntry(DateTimeOffset.Now, message, message, level));

    private void PublishStatus(ServerStatus status)
    {
        lock (_gate)
        {
            if (_status == status) return;
            _status = status;
        }

        _statusSubject.OnNext(status);
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Deliberately does not send "stop" — see the type-level remarks. This
        // only ever tears down the connection, whatever the reason (profile
        // switch, panel shutdown, or an explicit StopAsync already having run).
        _lifetimeCts?.Cancel();
        if (_connectionLoop is not null)
        {
            try { await _connectionLoop; }
            catch { /* expected: the loop's own OperationCanceledException */ }
        }
        _lifetimeCts?.Dispose();

        RconClient? client;
        lock (_gate) { client = _client; _client = null; }
        if (client is not null) await client.DisposeAsync();

        _consoleSubject.OnCompleted();
        _statusSubject.OnCompleted();
        _consoleSubject.Dispose();
        _statusSubject.Dispose();
    }
}
