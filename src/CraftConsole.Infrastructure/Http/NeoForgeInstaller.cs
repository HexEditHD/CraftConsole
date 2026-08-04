using System.Diagnostics;

namespace CraftConsole.Infrastructure.Http;

/// <summary>
/// Runs ServerStarterJar's --installer step to turn a downloaded server.jar into an actually
/// runnable NeoForge server. ServerStarterJar (github.com/neoforged/ServerStarterJar) wraps the
/// official NeoForge installer so the result behaves like any other single-jar server, matching
/// the -jar &lt;path&gt; assumption ServerProcessManager makes for every server type — unlike
/// running the NeoForge installer directly, which produces a run script and libraries folder.
/// </summary>
public static class NeoForgeInstaller
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs synchronously to completion (it's already awaited from a background download task).
    /// Throws <see cref="InvalidOperationException"/> with the installer's own output on failure.
    /// </summary>
    public static async Task RunAsync(string javaPath, string jarPath, string version, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(jarPath)
            ?? throw new InvalidOperationException($"\"{jarPath}\" has no containing directory.");

        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            Arguments = BuildArguments(jarPath, version),
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The NeoForge installer did not start.");

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(InstallTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException("The NeoForge installer did not finish in time.");
        }

        // ServerStarterJar's own exit code isn't a reliable success signal: after a successful
        // install it also attempts to launch the server once as a smoke test, which exits
        // non-zero simply because the EULA hasn't been accepted yet — expected, and unrelated to
        // whether the install itself worked. Its own marker for "already installed" (see its
        // "Failed to find run file at run.bat" message) is the presence of a run script, so that's
        // the actual success signal checked here instead of the process exit code.
        if (File.Exists(Path.Combine(directory, RunScriptName)))
            return;

        var stderr = (await stderrTask).Trim();
        var stdout = (await stdoutTask).Trim();
        var detail = string.IsNullOrEmpty(stderr) ? stdout : stderr;
        throw new InvalidOperationException(
            $"The NeoForge installer did not produce a runnable server (exit code {process.ExitCode})"
            + (string.IsNullOrEmpty(detail) ? "." : $": {detail}"));
    }

    private static string RunScriptName => OperatingSystem.IsWindows() ? "run.bat" : "run.sh";

    /// <summary>Pure, so the exact invocation is cheaply testable without launching a real JVM.</summary>
    internal static string BuildArguments(string jarPath, string version)
        => $"-jar \"{jarPath}\" --installer {version}";
}
