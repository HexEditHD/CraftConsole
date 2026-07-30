using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CraftConsole.Core.Models;
using CraftConsole.Core.Process;

namespace CraftConsole.Core.Servers;

public sealed class ServerProcessManager : IMinecraftServer
{
    /// <summary>How long a graceful "stop" is given before the process is terminated.</summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan KillGracePeriod = TimeSpan.FromSeconds(10);

    private readonly Subject<ConsoleEntry> _consoleSubject = new();
    private readonly Subject<ServerStatus> _statusSubject = new();
    private readonly TimeSpan _stopTimeout;

    // Guards _process, _status and _exitCode. Subjects are always published to
    // outside the lock — a subscriber calling back in would otherwise deadlock.
    private readonly object _gate = new();

    private System.Diagnostics.Process? _process;
    private ServerStatus _status = ServerStatus.Stopped;
    private int? _exitCode;
    private bool _disposed;

    public ServerProfile Profile { get; }

    public ServerStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Exit code of the most recent run; null while running or if it could not be read.</summary>
    public int? ExitCode
    {
        get { lock (_gate) return _exitCode; }
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                try { return _process?.HasExited == false ? _process.Id : null; }
                catch { return null; }
            }
        }
    }

    public ServerCapabilities Capabilities => ServerCapabilities.Managed;

    // ServerSupervisor already tracks this for a managed process by reading
    // server.properties directly; nothing here to add to that.
    public int? MaxPlayers => null;

    public IObservable<ConsoleEntry> ConsoleOutput => _consoleSubject.AsObservable();
    public IObservable<ServerStatus> StatusChanged => _statusSubject.AsObservable();

    public ServerProcessManager(ServerProfile profile, TimeSpan? stopTimeout = null)
    {
        Profile = profile;
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
    }

    // ── Start ─────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_status is ServerStatus.Running or ServerStatus.Starting or ServerStatus.Stopping)
                return Task.CompletedTask;

            // The previous run's handle is kept alive past its Exited event so a
            // concurrent StopAsync can still await it; release it now instead.
            _process?.Dispose();
            _process = null;
            _exitCode = null;
        }

        PublishStatus(ServerStatus.Starting);

        var process = new System.Diagnostics.Process
        {
            StartInfo = BuildStartInfo(),
            EnableRaisingEvents = true,
        };
        process.OutputDataReceived += OnStandardOutput;
        process.ErrorDataReceived += OnStandardError;
        process.Exited += OnProcessExited;

        // Assigned before Start so a process that dies immediately still matches
        // the identity check in OnProcessExited.
        lock (_gate) _process = process;

        try
        {
            process.Start();
            process.StandardInput.AutoFlush = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            // Without this the status stayed at Starting forever, wedging the
            // manager: both StartAsync and StopAsync would then early-return.
            lock (_gate) _process = null;
            process.Dispose();

            Emit($"Failed to launch \"{Profile.JavaPath}\": {ex.Message}", ConsoleEntryLevel.Error);
            PublishStatus(ServerStatus.Crashed);

            throw new InvalidOperationException(
                $"Could not start the server process using \"{Profile.JavaPath}\": {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    // ── Stop ──────────────────────────────────────────────────────────────

    public async Task StopAsync(CancellationToken ct = default)
    {
        System.Diagnostics.Process? process;

        lock (_gate)
        {
            // Starting is included deliberately: a server that never reached Running
            // — still generating a world, or halted at the EULA prompt — was
            // previously unstoppable, and the orphaned JVM outlived the panel.
            if (_status is not (ServerStatus.Running or ServerStatus.Starting))
                return;

            process = _process;
        }

        PublishStatus(ServerStatus.Stopping);

        if (process is null) return;
        try { if (process.HasExited) return; }
        catch { return; }

        await SendCommandAsync("stop", ct);

        if (await WaitForExitAsync(process, _stopTimeout, ct))
            return;

        Emit($"Server did not exit within {_stopTimeout.TotalSeconds:0}s — terminating the process.",
            ConsoleEntryLevel.Warn);

        KillTree(process);
        await WaitForExitAsync(process, KillGracePeriod, ct);
    }

    public Task<string?> SendCommandAsync(string command, CancellationToken ct = default)
    {
        System.Diagnostics.Process? process;
        lock (_gate) process = _process;

        try
        {
            // HasExited is a point-in-time check — the process can still exit
            // between here and the write, so the write itself is guarded too.
            if (process?.HasExited == false)
                process.StandardInput.WriteLine(command);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Process went away mid-write; the Exited handler reports the real story.
        }

        // A managed process's reply, if any, arrives later on stdout via
        // ConsoleOutput — there is no synchronous response to return here.
        return Task.FromResult<string?>(null);
    }

    // ── Process output ────────────────────────────────────────────────────

    private void OnStandardOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        Publish(ConsoleOutputParser.Parse(e.Data));
    }

    private void OnStandardError(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;

        var entry = ConsoleOutputParser.Parse(e.Data);

        // stderr used to be merged into stdout, so an unprefixed JVM failure such as
        // "Error: Unable to access jarfile ..." parsed as Unknown and never surfaced
        // as an error. Only unclassified lines are promoted — servers that log
        // properly levelled output to stderr keep their own levels.
        if (entry.Level is ConsoleEntryLevel.Unknown)
            entry = entry with { Level = ConsoleEntryLevel.Error };

        Publish(entry);
    }

    private void Publish(ConsoleEntry entry)
    {
        _consoleSubject.OnNext(entry);

        bool promote;
        lock (_gate) promote = _status == ServerStatus.Starting;

        if (promote && ServerEventParser.TryParse(entry) is ServerReadyEvent)
            PublishStatus(ServerStatus.Running);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var process = sender as System.Diagnostics.Process;

        int? code = null;
        try { code = process?.ExitCode; }
        catch { /* handle already released */ }

        ServerStatus next;
        lock (_gate)
        {
            // A handler left over from an earlier run must not clobber current state.
            if (!ReferenceEquals(process, _process)) return;

            _exitCode = code;
            next = _status == ServerStatus.Stopping ? ServerStatus.Stopped : ServerStatus.Crashed;
        }

        // The Process object is intentionally not disposed here. Doing so raced a
        // concurrent StopAsync still awaiting WaitForExitAsync on it; the next
        // StartAsync (or DisposeAsync) releases it instead.

        if (next == ServerStatus.Crashed)
            Emit($"Server process exited unexpectedly (exit code {Describe(code)}).", ConsoleEntryLevel.Error);
        else if (code is not (null or 0))
            Emit($"Server process exited with code {code}.", ConsoleEntryLevel.Warn);

        PublishStatus(next);
    }

    private static string Describe(int? code) => code?.ToString() ?? "unknown";

    // ── Helpers ───────────────────────────────────────────────────────────

    private ProcessStartInfo BuildStartInfo() => new()
    {
        FileName = Profile.JavaPath,
        Arguments = BuildArguments(),
        WorkingDirectory = Profile.WorkingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private string BuildArguments()
    {
        var ramArgs = $"-Xms{Profile.MinRamMb}M -Xmx{Profile.MaxRamMb}M";
        var extra = string.IsNullOrWhiteSpace(Profile.JvmArguments) ? "" : $" {Profile.JvmArguments.Trim()}";
        return $"{ramArgs}{extra} -jar \"{Profile.JarPath}\" nogui";
    }

    /// <returns>true if the process exited within the timeout.</returns>
    private static async Task<bool> WaitForExitAsync(
        System.Diagnostics.Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // our timeout, not the caller's cancellation
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return true; // handle gone — nothing left to wait for
        }
    }

    private void KillTree(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Already gone between the check and the kill.
        }
        catch (Exception ex)
        {
            Emit($"Could not terminate the server process: {ex.Message}", ConsoleEntryLevel.Error);
        }
    }

    /// <summary>Emits a panel-generated line into the console stream.</summary>
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

    // ── Disposal ──────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try
        {
            // StopAsync is bounded by _stopTimeout plus the kill grace period, so
            // host shutdown can no longer block indefinitely on a wedged JVM.
            await StopAsync();
        }
        catch { /* best-effort on shutdown */ }

        _consoleSubject.OnCompleted();
        _statusSubject.OnCompleted();
        _consoleSubject.Dispose();
        _statusSubject.Dispose();

        System.Diagnostics.Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        process?.Dispose();
    }
}
