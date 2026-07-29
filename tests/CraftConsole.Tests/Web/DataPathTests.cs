using CraftConsole.Web.Services;
using Xunit;

namespace CraftConsole.Tests.Web;

public class DataPathTests
{
    private static Func<string, string?> NoEnvironment => _ => null;
    private static Func<string, string?> Env(string value) => _ => value;

    [Fact]
    public void Resolve_prefers_the_command_line_switch_over_the_environment_variable()
    {
        var path = DataPath.Resolve(
            [DataPath.CommandLineSwitch, Path.Combine(Path.GetTempPath(), "from-args")],
            Env(Path.Combine(Path.GetTempPath(), "from-env")));

        Assert.EndsWith("from-args", path);
    }

    [Fact]
    public void Resolve_accepts_the_equals_form_of_the_switch()
    {
        var expected = Path.Combine(Path.GetTempPath(), "equals-form");

        var path = DataPath.Resolve([$"{DataPath.CommandLineSwitch}={expected}"], NoEnvironment);

        Assert.Equal(Path.GetFullPath(expected), path);
    }

    [Fact]
    public void Resolve_uses_the_environment_variable_when_no_switch_is_present()
    {
        var expected = Path.Combine(Path.GetTempPath(), "from-env");

        var path = DataPath.Resolve([], Env(expected));

        Assert.Equal(Path.GetFullPath(expected), path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_ignores_a_blank_environment_variable(string blank)
    {
        var path = DataPath.Resolve([], Env(blank));

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void Resolve_ignores_the_switch_when_it_has_no_value()
    {
        var expected = Path.Combine(Path.GetTempPath(), "from-env");

        // "--data-dir" as the final argument has nothing following it.
        var path = DataPath.Resolve([DataPath.CommandLineSwitch], Env(expected));

        Assert.Equal(Path.GetFullPath(expected), path);
    }

    [Fact]
    public void Resolve_returns_an_absolute_path_for_a_relative_input()
    {
        var path = DataPath.Resolve([$"{DataPath.CommandLineSwitch}=relative-dir"], NoEnvironment);

        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith("relative-dir", path);
    }

    [Fact]
    public void Resolve_falls_back_to_a_rooted_path_when_the_os_reports_no_app_data()
    {
        // A daemon account with no HOME: SpecialFolder.ApplicationData comes back empty.
        // Path.Combine would otherwise yield the bare relative name "CraftConsole",
        // silently rooting data at the current working directory.
        var fallbackRoot = Path.Combine(Path.GetTempPath(), "cc-fallback-" + Guid.NewGuid());

        var path = DataPath.Resolve([], NoEnvironment, fallbackRoot);

        Assert.True(Path.IsPathRooted(path));
        if (string.IsNullOrWhiteSpace(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)))
            Assert.StartsWith(fallbackRoot, path);
    }

    [Fact]
    public void Resolve_defaults_to_a_CraftConsole_folder_under_the_os_app_data_directory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            return; // environment has no app-data dir; covered by the fallback test above

        var path = DataPath.Resolve([], NoEnvironment);

        Assert.Equal(Path.Combine(appData, "CraftConsole"), path);
    }
}
