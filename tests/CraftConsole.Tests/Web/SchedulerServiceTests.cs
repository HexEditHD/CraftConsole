using CraftConsole.Core.Models;
using CraftConsole.Core.Players;
using CraftConsole.Core.Process;
using CraftConsole.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CraftConsole.Tests.Web;

public class SchedulerServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly EventBroker _broker = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ServerSupervisor _supervisor;
    private readonly SchedulerService _scheduler;

    public SchedulerServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-sched-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);

        var settings = new SettingsHolder(_dir);
        var secrets = new RconSecretStore(
            settings,
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(_dir, "dpkeys"))));
        _supervisor = new ServerSupervisor(
            _broker, settings, new HttpClient(), NullLogger<ServerSupervisor>.Instance, secrets);
        _scheduler = new SchedulerService(
            _supervisor, _broker, settings, NullLogger<SchedulerService>.Instance, _time);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ScheduledTask IntervalTask(int seconds = 60, string name = "Autosave") => new()
    {
        Name = name,
        TriggerType = TriggerType.Interval,
        TriggerValue = seconds.ToString(),
        ActionType = TaskActionType.SendCommand,
        ActionValue = "save-all",
        IsEnabled = true,
    };

    /// <summary>Collects "task-ran" events so firing can be observed without a server.</summary>
    private (List<string> Names, IDisposable Subscription) ObserveRuns()
    {
        var names = new List<string>();
        var (reader, subscription) = _broker.Subscribe();

        _ = Task.Run(async () =>
        {
            await foreach (var payload in reader.ReadAllAsync())
            {
                if (payload.Event != "task-ran") continue;
                using var doc = System.Text.Json.JsonDocument.Parse(payload.Json);
                lock (names) names.Add(doc.RootElement.GetProperty("name").GetString()!);
            }
        });

        return (names, subscription);
    }

    private static async Task<List<string>> WaitForRunsAsync(
        List<string> names, int expected, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (names) if (names.Count >= expected) return [.. names];
            await Task.Delay(20);
        }
        lock (names) return [.. names];
    }

    /// <summary>
    /// Starts the loop and waits for its timer to exist. ExecuteAsync awaits the
    /// disk load first, and a clock advance before the timer is created delivers
    /// no tick at all.
    /// </summary>
    private async Task StartLoopAsync()
    {
        await _scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(200);
    }

    /// <summary>
    /// Advances a tick at a time, yielding between each so the loop body — which
    /// is async — actually observes them, rather than collapsing the whole span
    /// into one tick.
    /// </summary>
    private async Task AdvanceAsync(int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            _time.Advance(SchedulerService.TickInterval);
            await Task.Delay(3);
        }
    }

    // ── CRUD and persistence ──────────────────────────────────────────────

    [Fact]
    public async Task Tasks_round_trip_through_disk()
    {
        var task = await _scheduler.AddAsync(IntervalTask());

        var reloaded = new SchedulerService(
            _supervisor, _broker, new SettingsHolder(_dir),
            NullLogger<SchedulerService>.Instance, _time);
        await reloaded.LoadAsync();

        var tasks = reloaded.Snapshot();
        Assert.Single(tasks);
        Assert.Equal(task.Id, tasks[0].Id);
        Assert.Equal("Autosave", tasks[0].Name);
        reloaded.Dispose();
    }

    [Fact]
    public async Task Update_replaces_fields_and_keeps_the_id()
    {
        var task = await _scheduler.AddAsync(IntervalTask());

        var replacement = IntervalTask(120, "Renamed");
        Assert.True(await _scheduler.UpdateAsync(task.Id, replacement));

        var stored = _scheduler.Snapshot().Single();
        Assert.Equal(task.Id, stored.Id);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal("120", stored.TriggerValue);
    }

    [Fact]
    public async Task Update_delete_and_run_report_false_for_an_unknown_id()
    {
        Assert.False(await _scheduler.UpdateAsync(Guid.NewGuid(), IntervalTask()));
        Assert.False(await _scheduler.DeleteAsync(Guid.NewGuid()));
        Assert.False(await _scheduler.RunNowAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Loading_keeps_a_task_added_before_the_loop_started()
    {
        // The service is registered as both a singleton and a hosted service, so an
        // AddAsync can land before ExecuteAsync runs. Replacing the list on load
        // would silently discard it.
        var added = await _scheduler.AddAsync(IntervalTask(60, "AddedEarly"));

        await _scheduler.LoadAsync();

        Assert.Contains(_scheduler.Snapshot(), t => t.Id == added.Id);
        Assert.Single(_scheduler.Snapshot());
    }

    // ── Interval triggers ─────────────────────────────────────────────────

    [Fact]
    public async Task An_interval_task_fires_once_its_interval_has_elapsed()
    {
        var (names, subscription) = ObserveRuns();
        using var _ = subscription;

        await _scheduler.AddAsync(IntervalTask(60));
        await StartLoopAsync();

        // Not yet due.
        await AdvanceAsync(30);
        Assert.Empty(await WaitForRunsAsync(names, 1, 300));

        // Now due.
        await AdvanceAsync(31);
        var fired = await WaitForRunsAsync(names, 1);

        Assert.Contains("Autosave", fired);
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_interval_task_reschedules_and_fires_repeatedly()
    {
        var (names, subscription) = ObserveRuns();
        using var _ = subscription;

        await _scheduler.AddAsync(IntervalTask(60));
        await StartLoopAsync();

        await AdvanceAsync(61);
        await WaitForRunsAsync(names, 1);
        await AdvanceAsync(61);
        var fired = await WaitForRunsAsync(names, 2);

        Assert.True(fired.Count >= 2, $"Expected at least two runs, saw {fired.Count}.");
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_disabled_task_does_not_fire()
    {
        var (names, subscription) = ObserveRuns();
        using var _ = subscription;

        var task = IntervalTask(60);
        task.IsEnabled = false;
        await _scheduler.AddAsync(task);
        await StartLoopAsync();

        await AdvanceAsync(120);

        Assert.Empty(await WaitForRunsAsync(names, 1, 400));
        await _scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_interval_of_zero_or_nonsense_never_becomes_due()
    {
        var (names, subscription) = ObserveRuns();
        using var _ = subscription;

        await _scheduler.AddAsync(IntervalTask(0, "Zero"));
        await _scheduler.AddAsync(new ScheduledTask
        {
            Name = "Nonsense",
            TriggerType = TriggerType.Interval,
            TriggerValue = "not-a-number",
            ActionType = TaskActionType.SendCommand,
            ActionValue = "say hi",
            IsEnabled = true,
        });
        await StartLoopAsync();

        await AdvanceAsync(120);

        Assert.Empty(await WaitForRunsAsync(names, 1, 400));
        await _scheduler.StopAsync(CancellationToken.None);
    }

    // ── Daily triggers ────────────────────────────────────────────────────

    [Fact]
    public async Task A_daily_task_fires_once_within_its_matching_minute()
    {
        var (names, subscription) = ObserveRuns();
        using var _ = subscription;

        // Park the clock just before the target minute.
        _time.SetUtcNow(new DateTimeOffset(2026, 1, 1, 4, 29, 50, TimeSpan.Zero));
        var target = _time.GetLocalNow().AddSeconds(20).ToString("HH:mm");

        await _scheduler.AddAsync(new ScheduledTask
        {
            Name = "NightlyRestart",
            TriggerType = TriggerType.TimeCron,
            TriggerValue = target,
            ActionType = TaskActionType.BroadcastMessage,
            ActionValue = "restarting soon",
            IsEnabled = true,
        });
        await StartLoopAsync();

        // Cross into the target minute and sit there for a while. The task must
        // fire once, not on every tick of that minute.
        await AdvanceAsync(40);

        var fired = await WaitForRunsAsync(names, 1);
        Assert.Equal(1, fired.Count(n => n == "NightlyRestart"));
        await _scheduler.StopAsync(CancellationToken.None);
    }

    // ── Game-event triggers ───────────────────────────────────────────────

    [Fact]
    public async Task A_player_join_trigger_fires_on_a_join_event()
    {
        var (names, subscription) = ObserveRuns();
        using var _ = subscription;

        await _scheduler.AddAsync(new ScheduledTask
        {
            Name = "GreetPlayer",
            TriggerType = TriggerType.PlayerJoin,
            TriggerValue = "",
            ActionType = TaskActionType.BroadcastMessage,
            ActionValue = "welcome",
            IsEnabled = true,
        });

        // Game events reach the scheduler through the supervisor, independently
        // of the tick loop, so no clock movement is needed.
        _supervisor.SimulateOutput("[12:00:15] [Server thread/INFO]: Steve joined the game");

        Assert.Contains("GreetPlayer", await WaitForRunsAsync(names, 1));
    }

    [Fact]
    public async Task A_server_ready_trigger_does_not_fire_on_a_join()
    {
        var (names, subscription) = ObserveRuns();
        using var _ = subscription;

        await _scheduler.AddAsync(new ScheduledTask
        {
            Name = "OnReady",
            TriggerType = TriggerType.ServerReady,
            TriggerValue = "",
            ActionType = TaskActionType.SendCommand,
            ActionValue = "say up",
            IsEnabled = true,
        });

        _supervisor.SimulateOutput("[12:00:15] [Server thread/INFO]: Steve joined the game");

        Assert.Empty(await WaitForRunsAsync(names, 1, 400));
    }

    [Fact]
    public async Task Run_now_executes_a_task_regardless_of_its_trigger()
    {
        var (names, subscription) = ObserveRuns();
        using var _ = subscription;

        var task = await _scheduler.AddAsync(IntervalTask(86_400, "Manual"));

        Assert.True(await _scheduler.RunNowAsync(task.Id));

        Assert.Contains("Manual", await WaitForRunsAsync(names, 1));
    }
}
