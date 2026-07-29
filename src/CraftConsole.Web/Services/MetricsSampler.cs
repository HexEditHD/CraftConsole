using CraftConsole.Core.Models;

namespace CraftConsole.Web.Services;

/// <summary>
/// Samples machine and server-process metrics every two seconds and publishes
/// them over SSE. Machine counters use Windows PerformanceCounters when
/// available and degrade gracefully elsewhere.
/// </summary>
public sealed class MetricsSampler : BackgroundService
{
    private readonly ServerSupervisor _supervisor;
    private readonly EventBroker _broker;

    private System.Diagnostics.PerformanceCounter? _machineCpu;
    private System.Diagnostics.PerformanceCounter? _machineRam;

    private int _lastPid = -1;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuCheck;

    public object? Latest { get; private set; }

    private readonly LinuxMachineMetrics? _linux;

    public MetricsSampler(ServerSupervisor supervisor, EventBroker broker)
    {
        _supervisor = supervisor;
        _broker = broker;

        if (LinuxMachineMetrics.IsSupported)
            _linux = new LinuxMachineMetrics();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                _machineCpu = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
                _machineRam = new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes");
                _machineCpu.NextValue(); // first sample is always 0 — prime it
            }
            catch
            {
                _machineCpu = null;
                _machineRam = null;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var snapshot = Sample();
                Latest = snapshot;
                _broker.Publish("metrics", snapshot);
            }
            catch { /* keep sampling */ }
        }
    }

    private object Sample()
    {
        // ── Machine ──
        // Null (not zero) where the platform can't report a figure, so the dashboard
        // can say "unavailable" instead of drawing a permanently idle machine.
        var machine = SampleMachine();

        // ── Server process ──
        double serverCpu = 0, serverRamMb = 0;
        var pid = _supervisor.ProcessId;
        if (pid is { } id)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(id);
                proc.Refresh();
                serverRamMb = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1);

                var nowCpu = proc.TotalProcessorTime;
                var nowTime = DateTime.UtcNow;
                if (_lastPid == id)
                {
                    var elapsed = (nowTime - _lastCpuCheck).TotalSeconds;
                    if (elapsed > 0)
                        serverCpu = Math.Round(Math.Clamp(
                            (nowCpu - _lastCpuTime).TotalSeconds / elapsed / Environment.ProcessorCount * 100,
                            0, 100), 1);
                }
                _lastPid = id;
                _lastCpuTime = nowCpu;
                _lastCpuCheck = nowTime;
            }
            catch { _lastPid = -1; }
        }
        else
        {
            _lastPid = -1;
        }

        var startedAt = _supervisor.StartedAt;
        return new
        {
            MachineCpuPercent = machine.CpuPercent,
            MachineRamUsedGb = machine.RamUsedGb,
            MachineRamTotalGb = machine.RamTotalGb,
            MachineRamPercent = machine.RamPercent,
            ServerCpuPercent = serverCpu,
            ServerRamMb = serverRamMb,
            ServerRamMaxMb = _supervisor.ActiveProfile?.MaxRamMb ?? 0,
            UptimeSeconds = startedAt is { } s ? (long)(DateTimeOffset.UtcNow - s).TotalSeconds : 0,
            Status = _supervisor.Status,
            PlayerCount = _supervisor.PlayersSnapshot().Count,
        };
    }

    private MachineSample SampleMachine()
    {
        if (OperatingSystem.IsWindows())
        {
            double? cpu = null, availMb = null;
            try { if (_machineCpu is not null) cpu = Math.Round(_machineCpu.NextValue(), 1); } catch { }
            try { if (_machineRam is not null) availMb = _machineRam.NextValue(); } catch { }

            // TotalAvailableMemoryBytes is the GC's view, which honours container and
            // heap-limit configuration — close enough to physical RAM for a gauge.
            var totalGb = Math.Round(
                (double)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024 / 1024, 2);

            double? usedGb = availMb is { } avail && totalGb > 0
                ? Math.Round(totalGb - avail / 1024.0, 2)
                : null;

            return new MachineSample(cpu, usedGb, usedGb is null ? null : totalGb);
        }

        return _linux?.Sample() ?? MachineSample.Unavailable;
    }

    public override void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            _machineCpu?.Dispose();
            _machineRam?.Dispose();
        }
        base.Dispose();
    }
}
