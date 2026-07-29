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

    public MetricsSampler(ServerSupervisor supervisor, EventBroker broker)
    {
        _supervisor = supervisor;
        _broker = broker;

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
        double machineCpu = 0, availMb = 0;
        if (OperatingSystem.IsWindows())
        {
            try { machineCpu = Math.Round(_machineCpu?.NextValue() ?? 0, 1); } catch { }
            try { availMb = _machineRam?.NextValue() ?? 0; } catch { }
        }

        var totalGb = Math.Round((double)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024 / 1024, 2);
        var usedGb = availMb > 0 ? Math.Round(totalGb - availMb / 1024.0, 2) : 0;
        var machineRamPct = totalGb > 0 && usedGb > 0 ? Math.Round(usedGb / totalGb * 100, 1) : 0;

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
            MachineCpuPercent = machineCpu,
            MachineRamUsedGb = usedGb,
            MachineRamTotalGb = totalGb,
            MachineRamPercent = machineRamPct,
            ServerCpuPercent = serverCpu,
            ServerRamMb = serverRamMb,
            ServerRamMaxMb = _supervisor.ActiveProfile?.MaxRamMb ?? 0,
            UptimeSeconds = startedAt is { } s ? (long)(DateTimeOffset.UtcNow - s).TotalSeconds : 0,
            Status = _supervisor.Status,
            PlayerCount = _supervisor.PlayersSnapshot().Count,
        };
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
