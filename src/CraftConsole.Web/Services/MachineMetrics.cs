using System.Globalization;

namespace CraftConsole.Web.Services;

/// <summary>Machine-wide CPU and memory. Null means "this platform can't report it".</summary>
public readonly record struct MachineSample(
    double? CpuPercent,
    double? RamUsedGb,
    double? RamTotalGb)
{
    public double? RamPercent =>
        RamUsedGb is { } used && RamTotalGb is { } total && total > 0
            ? Math.Round(used / total * 100, 1)
            : null;

    public static MachineSample Unavailable => new(null, null, null);
}

/// <summary>
/// Reads machine CPU/memory from /proc on Linux. Windows uses PerformanceCounter
/// (see <see cref="MetricsSampler"/>); anything else reports unavailable rather
/// than zero, which the dashboard would otherwise draw as a genuinely idle box.
/// </summary>
public sealed class LinuxMachineMetrics
{
    private const string StatPath = "/proc/stat";
    private const string MemInfoPath = "/proc/meminfo";

    private ulong _lastIdle;
    private ulong _lastTotal;
    private bool _primed;

    public static bool IsSupported => OperatingSystem.IsLinux() && File.Exists(StatPath);

    public MachineSample Sample()
    {
        if (!OperatingSystem.IsLinux()) return MachineSample.Unavailable;

        var (usedGb, totalGb) = ReadMemory();
        return new MachineSample(ReadCpuPercent(), usedGb, totalGb);
    }

    /// <summary>
    /// CPU busy percentage since the previous call. The first call primes the
    /// counters and returns null — there is no earlier sample to diff against.
    /// </summary>
    private double? ReadCpuPercent()
    {
        try
        {
            // "cpu  user nice system idle iowait irq softirq steal guest guest_nice"
            var line = File.ReadLines(StatPath).FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
            if (line is null) return null;

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5) return null;

            ulong total = 0, idle = 0;
            for (var i = 1; i < fields.Length; i++)
            {
                if (!ulong.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    continue;

                total += v;
                // idle (field 4) + iowait (field 5) both count as not-busy
                if (i is 4 or 5) idle += v;
            }

            if (total == 0) return null;

            var previousIdle = _lastIdle;
            var previousTotal = _lastTotal;
            _lastIdle = idle;
            _lastTotal = total;

            if (!_primed)
            {
                _primed = true;
                return null;
            }

            var totalDelta = total - previousTotal;
            var idleDelta = idle - previousIdle;
            if (totalDelta == 0) return null;

            var busy = (1.0 - (double)idleDelta / totalDelta) * 100.0;
            return Math.Round(Math.Clamp(busy, 0, 100), 1);
        }
        catch
        {
            return null;
        }
    }

    private static (double? UsedGb, double? TotalGb) ReadMemory()
    {
        try
        {
            // MemAvailable is the kernel's own estimate of what a new workload could
            // claim; far more honest than MemFree, which excludes reclaimable cache.
            double? totalKb = null, availableKb = null;

            foreach (var line in File.ReadLines(MemInfoPath))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    totalKb = ParseKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    availableKb = ParseKb(line);

                if (totalKb is not null && availableKb is not null) break;
            }

            if (totalKb is not { } total || availableKb is not { } available)
                return (null, null);

            var totalGb = Math.Round(total / 1024 / 1024, 2);
            var usedGb = Math.Round((total - available) / 1024 / 1024, 2);
            return (usedGb, totalGb);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Parses "MemTotal:       16316412 kB" → 16316412.</summary>
    private static double? ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var kb)
            ? kb
            : null;
    }
}
