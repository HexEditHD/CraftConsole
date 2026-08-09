using CraftConsole.Core.Models;

namespace CraftConsole.Web.Services;

/// <summary>Everything about a server's own process — null fields mean "no process to sample".</summary>
public sealed record ServerMetricsSample(
    double? ServerCpuPercent,
    double? ServerRamMb,
    int? ServerRamMaxMb,
    long? UptimeSeconds,
    ServerStatus Status,
    int PlayerCount);

/// <summary>
/// Samples machine and per-server process metrics every two seconds and
/// publishes them over SSE. Machine counters (Windows PerformanceCounters,
/// degrading gracefully elsewhere) describe the host and are published once
/// per tick, unscoped — every server shares the same host. Each server's own
/// process metrics are sampled and published separately, scoped to its
/// ServerId, for every server the registry knows about (i.e. every one ever
/// started).
///
/// Both are published under the same "metrics" SSE event name: a payload with
/// no serverId is the machine sample, one with a serverId is a server sample.
/// Kept as one event name rather than two so the client's SSE_EVENTS list
/// (bus.js) needs no change for this.
/// </summary>
public sealed class MetricsSampler : BackgroundService
{
    private readonly ServerRegistry _registry;
    private readonly EventBroker _broker;

    private System.Diagnostics.PerformanceCounter? _machineCpu;
    private System.Diagnostics.PerformanceCounter? _machineRam;

    private sealed class CpuTrack
    {
        public int Pid = -1;
        public TimeSpan LastCpuTime;
        public DateTime LastCheck;
    }

    private readonly Dictionary<Guid, CpuTrack> _cpuTracking = [];
    private readonly Dictionary<Guid, ServerMetricsSample> _latestByServer = [];

    /// <summary>Most recent host sample, or null before the first tick.</summary>
    public MachineSample? LatestMachine { get; private set; }

    /// <summary>Most recent sample for a server, or null if it has never been sampled.</summary>
    public ServerMetricsSample? LatestFor(Guid serverId) => _latestByServer.GetValueOrDefault(serverId);

    private readonly LinuxMachineMetrics? _linux;

    public MetricsSampler(ServerRegistry registry, EventBroker broker)
    {
        _registry = registry;
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
                var machine = SampleMachine();
                LatestMachine = machine;
                _broker.Publish("metrics", new
                {
                    machine.CpuPercent,
                    machine.RamUsedGb,
                    machine.RamTotalGb,
                    machine.RamPercent,
                });

                var supervisors = _registry.All();

                // Drop CPU-delta tracking for anything no longer in the registry
                // (its profile was deleted) so this dictionary doesn't grow forever.
                var liveIds = supervisors.Select(s => s.ServerId).ToHashSet();
                foreach (var staleId in _cpuTracking.Keys.Where(id => !liveIds.Contains(id)).ToList())
                    _cpuTracking.Remove(staleId);

                foreach (var supervisor in supervisors)
                {
                    try
                    {
                        var sample = SampleServer(supervisor);
                        _latestByServer[supervisor.ServerId] = sample;
                        _broker.Publish("metrics", supervisor.ServerId, sample);
                    }
                    catch { /* keep sampling the others */ }
                }
            }
            catch { /* keep sampling */ }
        }
    }

    private ServerMetricsSample SampleServer(ServerSupervisor supervisor)
    {
        double? serverCpu = null, serverRamMb = null;
        var pid = supervisor.ProcessId;

        if (!_cpuTracking.TryGetValue(supervisor.ServerId, out var track))
        {
            track = new CpuTrack();
            _cpuTracking[supervisor.ServerId] = track;
        }

        if (pid is { } id)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(id);
                proc.Refresh();
                serverRamMb = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 1);

                var nowCpu = proc.TotalProcessorTime;
                var nowTime = DateTime.UtcNow;
                if (track.Pid == id)
                {
                    var elapsed = (nowTime - track.LastCheck).TotalSeconds;
                    if (elapsed > 0)
                        serverCpu = Math.Round(Math.Clamp(
                            (nowCpu - track.LastCpuTime).TotalSeconds / elapsed / Environment.ProcessorCount * 100,
                            0, 100), 1);
                }
                track.Pid = id;
                track.LastCpuTime = nowCpu;
                track.LastCheck = nowTime;
            }
            catch { track.Pid = -1; }
        }
        else
        {
            track.Pid = -1;
        }

        var startedAt = supervisor.StartedAt;
        return new ServerMetricsSample(
            serverCpu,
            serverRamMb,
            // MaxRamMb is a real, configured JVM heap ceiling for a managed profile;
            // for RCON it's just ServerProfile's unused-field default (2048) — a
            // number that looks real but was never actually a fact about this server.
            supervisor.Capabilities.HasProcessMetrics ? supervisor.ActiveProfile?.MaxRamMb : null,
            startedAt is { } s ? (long?)(DateTimeOffset.UtcNow - s).TotalSeconds : null,
            supervisor.Status,
            supervisor.PlayersSnapshot().Count);
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
