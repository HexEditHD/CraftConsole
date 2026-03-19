using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using CraftConsole.Core.Models;
using CraftConsole.Core.Process;
using CraftConsole.Core.Servers;
using SkiaSharp;

namespace CraftConsole.Modules.Dashboard.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    // --- Machine metrics ---
    [ObservableProperty] private double _machineCpuPercent;
    [ObservableProperty] private double _machineRamUsedGb;
    [ObservableProperty] private double _machineRamTotalGb;
    [ObservableProperty] private double _machineRamPercent;

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
    [ObservableProperty] private double _tps = 20.0;
    [ObservableProperty] private double _ramUsedMb;
    [ObservableProperty] private double _ramMaxMb;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private double _cpuPercent;

    // --- TPS sparkline (last 60 samples) ---
    private readonly ObservableCollection<ObservableValue> _tpsSamples = [];
    public ISeries[] TpsSeries { get; }
    public Axis[] TpsXAxes { get; } =
    [
        new Axis { IsVisible = false }
    ];
    public Axis[] TpsYAxes { get; } =
    [
        new Axis
        {
            MinLimit = 0, MaxLimit = 20,
            IsVisible = false
        }
    ];

    // --- RAM pie ---
    private readonly ObservableValue _ramUsed = new(0);
    private readonly ObservableValue _ramFree = new(1);
    public ISeries[] RamSeries { get; }

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

    // Parses: [HH:mm:ss] [Server thread/INFO]: TPS from last 1m, 5m, 15m: 19.95, 19.97, 19.98
    private static readonly Regex TpsPattern = new(
        @"TPS from last 1m, 5m, 15m: (?<t1>[\d.]+),", RegexOptions.Compiled);

    // Parses server version line: Starting minecraft server version 1.21.4
    private static readonly Regex VersionPattern = new(
        @"Starting minecraft server version (?<ver>[\d.]+)", RegexOptions.Compiled);

    public DashboardViewModel()
    {
        for (int i = 0; i < 60; i++)
            _tpsSamples.Add(new ObservableValue(20));

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

        TpsSeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = _tpsSamples,
                Fill = new SolidColorPaint(new SKColor(12, 45, 56, 200)),
                Stroke = new SolidColorPaint(new SKColor(34, 211, 238), 2),
                GeometrySize = 0,
                LineSmoothness = 0.5,
            }
        ];

        RamSeries =
        [
            new PieSeries<ObservableValue>
            {
                Values = [_ramUsed],
                Fill = new SolidColorPaint(new SKColor(34, 211, 238)),
                Name = "Used",
                InnerRadius = 40,
            },
            new PieSeries<ObservableValue>
            {
                Values = [_ramFree],
                Fill = new SolidColorPaint(new SKColor(22, 32, 53)),
                Name = "Free",
                InnerRadius = 40,
            }
        ];
    }

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
        // TPS (via /tps command response)
        if (TpsPattern.Match(entry.Message) is { Success: true } tpsMatch
            && double.TryParse(tpsMatch.Groups["t1"].Value, out var tps))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PushTps(tps));
        }

        // Version
        if (VersionPattern.Match(entry.Message) is { Success: true } vMatch)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ServerVersion = vMatch.Groups["ver"].Value);
        }

        // Player count from events
        var evt = ServerEventParser.TryParse(entry);
        if (evt is PlayerJoinedEvent) Avalonia.Threading.Dispatcher.UIThread.Post(() => PlayerCount++);
        if (evt is PlayerLeftEvent)   Avalonia.Threading.Dispatcher.UIThread.Post(() => PlayerCount = Math.Max(0, PlayerCount - 1));
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

                _ramUsed.Value = ramMb;
                _ramFree.Value = Math.Max(0, maxMb - ramMb);
            });
        }
        catch { /* process may have exited mid-read */ }
    }

    private void PushTps(double tps)
    {
        Tps = Math.Round(tps, 2);
        _tpsSamples.RemoveAt(0);
        _tpsSamples.Add(new ObservableValue(tps));
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
