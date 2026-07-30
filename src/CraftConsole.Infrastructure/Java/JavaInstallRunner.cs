using System.ComponentModel;
using System.Diagnostics;

namespace CraftConsole.Infrastructure.Java;

public enum JavaInstallOutcome { Succeeded, UserCancelledElevation, InstallerFailed }

public readonly record struct JavaInstallResult(JavaInstallOutcome Outcome, string? Detail);

/// <summary>
/// Runs the downloaded Adoptium installer instead of just telling the user to. Windows only —
/// on Linux the app stays fully unprivileged (see SetupService), so there's nothing to run here.
/// </summary>
public static class JavaInstallRunner
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs the given MSI silently, elevated via the normal UAC consent prompt — the OS's own
    /// elevation gate, not a privilege the app itself holds. Declining the prompt is reported as
    /// <see cref="JavaInstallOutcome.UserCancelledElevation"/>, not a crash.
    /// </summary>
    public static async Task<JavaInstallResult> InstallWindowsAsync(string msiPath, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec",
            Arguments = $"/i \"{msiPath}\" /qn /norestart",
            UseShellExecute = true, // required for the "runas" verb to trigger UAC
            Verb = "runas",
        };

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("msiexec did not start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return new JavaInstallResult(JavaInstallOutcome.UserCancelledElevation, null);
        }

        using (process)
        {
            if (!await WaitForExitAsync(process, InstallTimeout, ct))
                return new JavaInstallResult(JavaInstallOutcome.InstallerFailed, "The installer did not finish in time.");

            return ClassifyExitCode(process.ExitCode);
        }
    }

    /// <summary>Pure, so the exit-code mapping is cheaply testable without launching a real installer.</summary>
    internal static JavaInstallResult ClassifyExitCode(int exitCode) => exitCode switch
    {
        0 => new JavaInstallResult(JavaInstallOutcome.Succeeded, null),
        3010 => new JavaInstallResult(JavaInstallOutcome.Succeeded, "A reboot is recommended to finish the install."),
        _ => new JavaInstallResult(JavaInstallOutcome.InstallerFailed, $"Installer exited with code {exitCode}."),
    };

    // Same CancelAfter/WaitForExitAsync idiom as ServerProcessManager.WaitForExitAsync.
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
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
    }
}
