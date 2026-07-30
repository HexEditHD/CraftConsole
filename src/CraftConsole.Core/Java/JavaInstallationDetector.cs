using System.Text.RegularExpressions;

namespace CraftConsole.Core.Java;

public static class JavaInstallationDetector
{
    private static readonly string[] WindowsScanDirectories =
    [
        @"C:\Program Files\Java",
        @"C:\Program Files\Eclipse Adoptium",
        @"C:\Program Files\Microsoft",
        @"C:\Program Files\Semeru",
        @"C:\Program Files\BellSoft",
        @"C:\Program Files\Amazon Corretto",
    ];

    // Debian/Ubuntu/RHEL-family package layout; update-alternatives (below) covers
    // most distros' actual java registrations, this is a fallback for direct installs.
    private static readonly string[] LinuxScanDirectories =
    [
        "/usr/lib/jvm",
        "/opt/java",
    ];

    private static string JavaExecutableName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    public static async Task<List<JavaInstallation>> DetectAsync(
        CancellationToken ct = default)
    {
        var candidates = new List<string>();

        // 1. JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            candidates.Add(Path.Combine(javaHome, "bin", JavaExecutableName));

        // 2. PATH entries (Path.PathSeparator is ';' on Windows, ':' on Unix)
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), JavaExecutableName);
            if (File.Exists(candidate))
                candidates.Add(candidate);
        }

        // 3. update-alternatives — lists every java registered with the system,
        // not just the one currently active on PATH (Debian/Ubuntu/RHEL-family)
        if (!OperatingSystem.IsWindows())
            candidates.AddRange(await ListUpdateAlternativesAsync(ct));

        // 4. Common install directories — scan one level deep
        foreach (var root in OperatingSystem.IsWindows() ? WindowsScanDirectories : LinuxScanDirectories)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var subDir in Directory.EnumerateDirectories(root))
            {
                var candidate = Path.Combine(subDir, "bin", JavaExecutableName);
                if (File.Exists(candidate))
                    candidates.Add(candidate);
            }
        }

        // Deduplicate by resolved (canonical) path, probe each
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<JavaInstallation>();

        foreach (var path in candidates)
        {
            try
            {
                var resolved = Path.GetFullPath(path);
                if (!seen.Add(resolved) || !File.Exists(resolved)) continue;

                var install = await ProbeAsync(resolved, ct);
                if (install is not null)
                    results.Add(install);
            }
            catch { /* skip bad path */ }
        }

        return results;
    }

    private static async Task<List<string>> ListUpdateAlternativesAsync(CancellationToken ct)
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "update-alternatives",
                Arguments = "--list java",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        catch
        {
            return []; // not installed / not on this distro — other scans still run
        }
    }

    private static async Task<JavaInstallation?> ProbeAsync(
        string executablePath, CancellationToken ct)
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "-version",
                RedirectStandardError = true,   // java -version writes to stderr
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();
            var output = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return ParseVersion(executablePath, output);
        }
        catch
        {
            return null;
        }
    }

    private static readonly Regex VersionRegex =
        new(@"version ""(?<ver>[^""]+)""", RegexOptions.Compiled);

    private static JavaInstallation? ParseVersion(string path, string output)
    {
        var match = VersionRegex.Match(output);
        if (!match.Success) return null;

        var ver = match.Groups["ver"].Value;
        var parts = ver.Split('.');

        // Legacy Java 8: "1.8.0_401" → major 8
        // Modern Java: "21.0.3" → major 21
        int major = parts[0] == "1" && parts.Length > 1
            ? int.TryParse(parts[1], out var m8) ? m8 : 0
            : int.TryParse(parts[0], out var m) ? m : 0;

        return new JavaInstallation(path, ver, major);
    }
}
