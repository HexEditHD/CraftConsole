using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CraftConsole.Core.Models;
using CraftConsole.Core.Process;

namespace CraftConsole.Core.Servers;

public sealed class ServerProcessManager : IMinecraftServer, IAsyncDisposable
{
    private readonly Subject<ConsoleEntry> _consoleSubject = new();
    private readonly Subject<ServerStatus> _statusSubject = new();
    private System.Diagnostics.Process? _process;
    private ServerStatus _status = ServerStatus.Stopped;

    public ServerProfile Profile { get; }
    public ServerStatus Status => _status;

    public IObservable<ConsoleEntry> ConsoleOutput => _consoleSubject.AsObservable();
    public IObservable<ServerStatus> StatusChanged => _statusSubject.AsObservable();

    public ServerProcessManager(ServerProfile profile)
    {
        Profile = profile;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_status is ServerStatus.Running or ServerStatus.Starting)
            return;

        SetStatus(ServerStatus.Starting);

        var startInfo = new ProcessStartInfo
        {
            FileName = Profile.JavaPath,
            Arguments = BuildArguments(),
            WorkingDirectory = Profile.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived  += OnOutput;
        _process.Exited += OnProcessExited;

        _process.Start();
        _process.StandardInput.AutoFlush = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_status is not ServerStatus.Running)
            return;

        SetStatus(ServerStatus.Stopping);
        await SendCommandAsync("stop", ct);

        if (_process is not null)
            await _process.WaitForExitAsync(ct);
    }

    public Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (_process?.HasExited == false)
            _process.StandardInput.WriteLine(command);

        return Task.CompletedTask;
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        var entry = ConsoleOutputParser.Parse(e.Data);
        _consoleSubject.OnNext(entry);

        // Detect server ready from console
        if (_status == ServerStatus.Starting && ServerEventParser.TryParse(entry) is ServerReadyEvent)
            SetStatus(ServerStatus.Running);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        SetStatus(_status == ServerStatus.Stopping ? ServerStatus.Stopped : ServerStatus.Crashed);
        _process?.Dispose();
        _process = null;
    }

    private string BuildArguments()
    {
        var ramArgs = $"-Xms{Profile.MinRamMb}M -Xmx{Profile.MaxRamMb}M";
        var extra = string.IsNullOrWhiteSpace(Profile.JvmArguments) ? "" : $" {Profile.JvmArguments.Trim()}";
        return $"{ramArgs}{extra} -jar \"{Profile.JarPath}\" nogui";
    }

    private void SetStatus(ServerStatus status)
    {
        _status = status;
        _statusSubject.OnNext(status);
    }

    public async ValueTask DisposeAsync()
    {
        if (_status == ServerStatus.Running)
            await StopAsync();

        _consoleSubject.OnCompleted();
        _statusSubject.OnCompleted();
        _process?.Dispose();
    }
}
