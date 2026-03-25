using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CraftConsole.Core.Models;
using CraftConsole.Core.Players;
using CraftConsole.Core.Process;
using CraftConsole.Core.Servers;

namespace CraftConsole.Modules.Dashboard.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    // --- Machine metrics ---
    [ObservableProperty] private double _machineCpuPercent;
    [ObservableProperty] private double _machineRamUsedGb;
    [ObservableProperty] private double _machineRamTotalGb;
    [ObservableProperty] private double _machineRamPercent;
    [ObservableProperty] private double _machineRamFreeGb;

    // --- App (CraftConsole process) metrics ---
    [ObservableProperty] private double _appCpuPercent;
    [ObservableProperty] private double _appRamMb;

    // --- Server state ---
    [ObservableProperty] private ServerStatus _serverStatus = ServerStatus.Stopped;
    [ObservableProperty] private string _serverStatusLabel = "Stopped";
    [ObservableProperty] private string _serverVersion = "—";
    [ObservableProperty] private TimeSpan _uptime;
    [ObservableProperty] private string _uptimeLabel = "0m";

    // --- Live metrics ---
    [ObservableProperty] private int _playerCount;
    [ObservableProperty] private int _maxPlayers = 20;
    [ObservableProperty] private double _ramUsedMb;
    [ObservableProperty] private double _ramMaxMb;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private double _cpuPercent;

    private IMinecraftServer? _server;
    private System.Diagnostics.Process? _process;
    private IDisposable? _consoleSubscription;
    private IDisposable? _statusSubscription;
    private Timer? _pollTimer;
    private DateTimeOffset _startedAt;

    // For machine CPU delta calculation
#pragma warning disable CA1416
    private System.Diagnostics.PerformanceCounter? _machineCpuCounter;
    private System.Diagnostics.PerformanceCounter? _machineRamCounter;
#pragma warning restore CA1416

    // For app CPU calculation
    private TimeSpan _lastAppCpuTime;
    private DateTime _lastAppCpuCheck;

    // Parses server version line: Starting minecraft server version 1.21.4
    private static readonly Regex VersionPattern = new(
        @"Starting minecraft server version (?<ver>[\d.]+)", RegexOptions.Compiled);

    // Player source (set by MainWindowViewModel after server starts)
    private ObservableCollection<Player>? _playerSource;

    public DashboardViewModel()
    {
        // Init machine performance counters (Windows only; graceful fallback)
        try
        {
#pragma warning disable CA1416
            _machineCpuCounter = new System.Diagnostics.PerformanceCounter(
                "Processor", "% Processor Time", "_Total");
            _machineRamCounter = new System.Diagnostics.PerformanceCounter(
                "Memory", "Available MBytes");
            // First call to NextValue is always 0; do it eagerly so second call is accurate
            _machineCpuCounter.NextValue();
#pragma warning restore CA1416
        }
        catch
        {
            _machineCpuCounter = null;
            _machineRamCounter = null;
        }

        // Init app CPU baseline
        var self = System.Diagnostics.Process.GetCurrentProcess();
        _lastAppCpuTime  = self.TotalProcessorTime;
        _lastAppCpuCheck = DateTime.UtcNow;

        // Start a machine/app metrics timer immediately (no server needed)
        _ = new Timer(PollMachineMetrics, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    /// <summary>Called by MainWindowViewModel after a server starts to get an accurate player count.</summary>
    public void SetPlayerSource(ObservableCollection<Player> players)
    {
        if (_playerSource is not null)
            _playerSource.CollectionChanged -= OnPlayerSourceChanged;

        _playerSource = players;
        _playerSource.CollectionChanged += OnPlayerSourceChanged;
        PlayerCount = _playerSource.Count;
    }

    private void OnPlayerSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => PlayerCount = _playerSource?.Count ?? 0);

    public void Attach(IMinecraftServer server)
    {
        Detach();
        _server = server;

        _statusSubscription = server.StatusChanged.Subscribe(OnStatusChanged);
        _consoleSubscription = server.ConsoleOutput.Subscribe(OnConsoleEntry);

        _pollTimer = new Timer(PollProcessMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public void Detach()
    {
        _consoleSubscription?.Dispose();
        _statusSubscription?.Dispose();
        _pollTimer?.Dispose();
        _server = null;
        _process = null;

#pragma warning disable CA1416
        _machineCpuCounter?.Dispose();
        _machineRamCounter?.Dispose();
#pragma warning restore CA1416
        _machineCpuCounter = null;
        _machineRamCounter = null;
    }

    private void OnStatusChanged(ServerStatus status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ServerStatus = status;
            ServerStatusLabel = status.ToString();

            if (status == ServerStatus.Running)
                _startedAt = DateTimeOffset.UtcNow;
        });
    }

    private void OnConsoleEntry(ConsoleEntry entry)
    {
        // Version
        if (VersionPattern.Match(entry.Message) is { Success: true } vMatch)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ServerVersion = vMatch.Groups["ver"].Value);
        }
    }

    private void PollProcessMetrics(object? _)
    {
        try
        {
            // Grab the java process if we haven't yet
            if (_process is null || _process.HasExited)
            {
                _process = System.Diagnostics.Process.GetProcessesByName("java").FirstOrDefault();
                if (_process is null) return;
            }

            _process.Refresh();

            var ramMb  = _process.WorkingSet64 / 1024.0 / 1024.0;
            var maxMb  = _process.PeakWorkingSet64 / 1024.0 / 1024.0;
            var ramPct = maxMb > 0 ? ramMb / maxMb * 100 : 0;

            var uptime = ServerStatus == ServerStatus.Running
                ? DateTimeOffset.UtcNow - _startedAt
                : TimeSpan.Zero;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RamUsedMb  = Math.Round(ramMb, 1);
                RamMaxMb   = Math.Round(maxMb, 1);
                RamPercent = Math.Round(ramPct, 1);
                Uptime     = uptime;
                UptimeLabel = FormatUptime(uptime);
            });
        }
        catch { /* process may have exited mid-read */ }
    }

    private void PollMachineMetrics(object? _)
    {
        try
        {
            // Machine CPU
            double cpuPct = 0;
#pragma warning disable CA1416
            if (_machineCpuCounter is not null)
                cpuPct = Math.Round(_machineCpuCounter.NextValue(), 1);
#pragma warning restore CA1416

            // Machine RAM
            double totalGb = Math.Round(
                (double)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024 / 1024, 2);
            double availMb = 0;
#pragma warning disable CA1416
            if (_machineRamCounter is not null)
                availMb = _machineRamCounter.NextValue();
#pragma warning restore CA1416
            double usedGb  = Math.Round(totalGb - availMb / 1024.0, 2);
            double freeGb  = Math.Round(totalGb - usedGb, 2);
            double ramPct  = totalGb > 0 ? Math.Round(usedGb / totalGb * 100, 1) : 0;

            // App CPU
            var self    = System.Diagnostics.Process.GetCurrentProcess();
            self.Refresh();
            var nowCpu   = self.TotalProcessorTime;
            var nowTime  = DateTime.UtcNow;
            var elapsed  = (nowTime - _lastAppCpuCheck).TotalSeconds;
            double appCpu = 0;
            if (elapsed > 0)
                appCpu = Math.Round(
                    (nowCpu - _lastAppCpuTime).TotalSeconds / elapsed / Environment.ProcessorCount * 100, 1);
            _lastAppCpuTime  = nowCpu;
            _lastAppCpuCheck = nowTime;

            // App RAM
            double appRam = Math.Round(Environment.WorkingSet / 1024.0 / 1024.0, 1);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                MachineCpuPercent  = cpuPct;
                MachineRamUsedGb   = usedGb;
                MachineRamTotalGb  = totalGb;
                MachineRamPercent  = ramPct;
                MachineRamFreeGb   = freeGb;
                AppCpuPercent      = appCpu;
                AppRamMb           = appRam;
            });
        }
        catch { /* ignore */ }
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return $"{t.Seconds}s";
    }
}
