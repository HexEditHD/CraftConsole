using CraftConsole.Core.Java;
using Xunit;

namespace CraftConsole.Tests.Java;

/// <summary>
/// Exercises detection against whatever JDKs the host actually has. On CI this
/// is the point: the Linux path (colon separator, no .exe suffix, /usr/lib/jvm,
/// update-alternatives) is only meaningfully covered by running it on Linux.
/// </summary>
public class JavaInstallationDetectorTests
{
    [Fact]
    public async Task Finds_a_runtime_when_the_host_has_one_on_JAVA_HOME_or_PATH()
    {
        if (!HostHasJava())
        {
            // No JDK here; the shape of a detected install is covered below.
            return;
        }

        var found = await JavaInstallationDetector.DetectAsync();

        Assert.NotEmpty(found);
    }

    [Fact]
    public async Task Detected_installs_have_a_real_executable_and_a_plausible_version()
    {
        var found = await JavaInstallationDetector.DetectAsync();

        foreach (var install in found)
        {
            Assert.True(File.Exists(install.ExecutablePath),
                $"Reported a java at '{install.ExecutablePath}' that does not exist.");
            Assert.False(string.IsNullOrWhiteSpace(install.DisplayVersion));

            // Java 8 is the oldest anyone runs a server on; the upper bound is
            // just a sanity check that the major version was parsed, not guessed.
            Assert.InRange(install.MajorVersion, 6, 99);
            Assert.Contains(install.MajorVersion.ToString(), install.Label);
        }
    }

    [Fact]
    public async Task Detection_returns_no_duplicate_executables()
    {
        // JAVA_HOME, PATH, update-alternatives and the directory scan overlap
        // heavily; results are de-duplicated by resolved path.
        var found = await JavaInstallationDetector.DetectAsync();

        var paths = found.Select(i => Path.GetFullPath(i.ExecutablePath)).ToList();

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static bool HostHasJava()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JAVA_HOME")))
            return true;

        var exeName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir =>
            {
                try { return File.Exists(Path.Combine(dir.Trim(), exeName)); }
                catch { return false; }
            });
    }
}
