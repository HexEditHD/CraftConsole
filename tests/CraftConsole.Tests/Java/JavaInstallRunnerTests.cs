using CraftConsole.Infrastructure.Java;
using Xunit;

namespace CraftConsole.Tests.Java;

/// <summary>
/// Covers the exit-code classifier only — it's pure, so it's fully testable on both OSes even
/// though only Windows calls it in production. Actually running msiexec elevated via a real UAC
/// prompt isn't something CI (or this test class) can drive; that path is manual-only, same as
/// JavaInstallationDetectorTests documents for its own host-dependent cases.
/// </summary>
public class JavaInstallRunnerTests
{
    [Fact]
    public void Exit_code_0_is_success_with_no_detail()
    {
        var result = JavaInstallRunner.ClassifyExitCode(0);

        Assert.Equal(JavaInstallOutcome.Succeeded, result.Outcome);
        Assert.Null(result.Detail);
    }

    [Fact]
    public void Exit_code_3010_is_success_but_flags_a_reboot()
    {
        var result = JavaInstallRunner.ClassifyExitCode(3010);

        Assert.Equal(JavaInstallOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.Detail);
        Assert.Contains("reboot", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1603)] // ERROR_INSTALL_FAILURE — a real, common msiexec failure code
    [InlineData(1618)] // ERROR_INSTALL_ALREADY_RUNNING
    public void Any_other_exit_code_is_a_failure_that_surfaces_the_code(int exitCode)
    {
        var result = JavaInstallRunner.ClassifyExitCode(exitCode);

        Assert.Equal(JavaInstallOutcome.InstallerFailed, result.Outcome);
        Assert.Contains(exitCode.ToString(), result.Detail);
    }
}
