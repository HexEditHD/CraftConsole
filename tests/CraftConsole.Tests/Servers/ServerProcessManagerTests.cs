using System.Diagnostics;
using CraftConsole.Core.Models;
using CraftConsole.Core.Servers;
using Xunit;

namespace CraftConsole.Tests.Servers;

/// <summary>
/// End-to-end coverage of the real process lifecycle, driven against the fake
/// server rather than a JVM. Each test corresponds to a defect fixed in the
/// lifecycle hardening pass.
/// </summary>
[Collection(nameof(FakeServerCollection))]
public class ServerProcessManagerTests
{
    // ── Happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_reaches_Running_once_the_server_reports_ready()
    {
        await using var run = new FakeServerRun();

        await run.Manager.StartAsync();
        await run.WaitForStatusAsync(ServerStatus.Running);

        Assert.Contains(run.Statuses, s => s == ServerStatus.Starting);
        Assert.Contains(run.Messages, m => m.Contains("Done (7.312s)!"));
        Assert.NotNull(run.Manager.ProcessId);
    }

    [Fact]
    public async Task Stop_shuts_the_server_down_gracefully_and_reports_Stopped()
    {
        await using var run = new FakeServerRun();
        await run.Manager.StartAsync();
        await run.WaitForStatusAsync(ServerStatus.Running);

        await run.Manager.StopAsync();

        Assert.Equal(ServerStatus.Stopped, run.Manager.Status);
        Assert.Equal(0, run.Manager.ExitCode);
        Assert.Contains(run.Messages, m => m.Contains("Stopping the server"));
        Assert.Null(run.Manager.ProcessId);
    }

    [Fact]
    public async Task Commands_are_delivered_to_the_server_and_its_replies_are_captured()
    {
        await using var run = new FakeServerRun();
        await run.Manager.StartAsync();
        await run.WaitForStatusAsync(ServerStatus.Running);

        await run.Manager.SendCommandAsync("fake join");
        await run.WaitForConsoleAsync("Steve joined the game");

        await run.Manager.SendCommandAsync("fake chat");
        await run.WaitForConsoleAsync("<Steve> hello world");
    }

    // ── Regression: stop had no timeout or kill fallback ──────────────────

    [Fact]
    public async Task Stop_terminates_a_server_that_ignores_the_stop_command()
    {
        // "hang" never honours stop. Before the fix this waited forever, and because
        // host shutdown blocks on disposal it wedged the whole application.
        await using var run = new FakeServerRun("hang", stopTimeout: TimeSpan.FromSeconds(2));
        await run.Manager.StartAsync();
        await run.WaitForStatusAsync(ServerStatus.Running);

        var stopwatch = Stopwatch.StartNew();
        await run.Manager.StopAsync();
        stopwatch.Stop();

        Assert.Equal(ServerStatus.Stopped, run.Manager.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"Stop should be bounded by the timeout, took {stopwatch.Elapsed}.");
        Assert.Contains(run.Messages, m => m.Contains("terminating the process"));
    }

    // ── Regression: a Starting server was unstoppable ──────────────────────

    [Fact]
    public async Task Stop_works_on_a_server_that_never_finished_starting()
    {
        // A long boot keeps it in Starting. Previously the guard only accepted
        // Running, so this was a silent no-op and the process was orphaned.
        await using var run = new FakeServerRun(stopTimeout: TimeSpan.FromSeconds(2), bootMs: 30_000);
        await run.Manager.StartAsync();
        await run.WaitForStatusAsync(ServerStatus.Starting);
        Assert.Equal(ServerStatus.Starting, run.Manager.Status);

        await run.Manager.StopAsync();

        Assert.Equal(ServerStatus.Stopped, run.Manager.Status);
        Assert.Null(run.Manager.ProcessId);
    }

    // ── Regression: start failure wedged the manager ───────────────────────

    [Fact]
    public async Task Start_with_a_bad_executable_reports_failure_and_leaves_the_manager_usable()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "cc-bad-" + Guid.NewGuid());
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var profile = FakeServer.Profile(workingDirectory);
            profile.JavaPath = Path.Combine(workingDirectory, "definitely-not-here");

            await using var manager = new ServerProcessManager(profile);
            var statuses = new List<ServerStatus>();
            using var _ = manager.StatusChanged.Subscribe(statuses.Add);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync());

            // Previously this stayed at Starting forever, so both start and stop
            // early-returned and the manager could never be used again.
            Assert.Equal(ServerStatus.Crashed, manager.Status);
            Assert.Contains(ServerStatus.Crashed, statuses);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    // ── Regression: exit code was never read ───────────────────────────────

    [Fact]
    public async Task An_unexpected_exit_is_reported_as_Crashed_with_its_exit_code()
    {
        await using var run = new FakeServerRun("crash");

        await run.Manager.StartAsync();
        await run.WaitForStatusAsync(ServerStatus.Crashed);

        Assert.Equal(1, run.Manager.ExitCode);
        Assert.Contains(run.Messages, m => m.Contains("exited unexpectedly") && m.Contains("1"));
    }

    [Fact]
    public async Task A_non_zero_exit_during_a_requested_stop_is_still_surfaced()
    {
        await using var run = new FakeServerRun("exitcode");
        await run.Manager.StartAsync();
        await run.WaitForStatusAsync(ServerStatus.Running);

        await run.Manager.StopAsync();

        Assert.Equal(ServerStatus.Stopped, run.Manager.Status);
        Assert.Equal(3, run.Manager.ExitCode);
        Assert.Contains(run.Messages, m => m.Contains("exited with code 3"));
    }

    // ── Regression: stderr was indistinguishable from stdout ───────────────

    [Fact]
    public async Task Unclassified_stderr_output_is_surfaced_as_an_error()
    {
        await using var run = new FakeServerRun("stderr");

        await run.Manager.StartAsync();
        await run.WaitForConsoleAsync("Unable to access jarfile");

        // Previously this parsed as Unknown and never reached the issues list.
        var entry = run.Console.First(e => e.Message.Contains("Unable to access jarfile"));
        Assert.Equal(ConsoleEntryLevel.Error, entry.Level);
    }

    // ── EULA first run ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_first_run_that_stops_at_the_EULA_prompt_is_captured()
    {
        await using var run = new FakeServerRun("eula");

        await run.Manager.StartAsync();
        await run.WaitForConsoleAsync("agree to the EULA");

        // The server exits on its own here; it never reaches Running.
        await run.WaitUntilAsync(
            () => run.Manager.Status is ServerStatus.Stopped or ServerStatus.Crashed,
            "the server to exit after printing the EULA notice");
    }

    // ── Idempotence ────────────────────────────────────────────────────────

    [Fact]
    public async Task Starting_twice_does_not_launch_a_second_process()
    {
        await using var run = new FakeServerRun();

        await run.Manager.StartAsync();
        await run.WaitForStatusAsync(ServerStatus.Running);
        var firstPid = run.Manager.ProcessId;

        await run.Manager.StartAsync();

        Assert.Equal(firstPid, run.Manager.ProcessId);
    }

    [Fact]
    public async Task Stopping_an_already_stopped_server_is_a_no_op()
    {
        await using var run = new FakeServerRun();

        await run.Manager.StopAsync();

        Assert.Equal(ServerStatus.Stopped, run.Manager.Status);
    }
}

/// <summary>
/// The fake server's behaviour is selected through environment variables, which
/// are process-wide — these tests must not run concurrently with each other.
/// </summary>
[CollectionDefinition(nameof(FakeServerCollection), DisableParallelization = true)]
public class FakeServerCollection;
