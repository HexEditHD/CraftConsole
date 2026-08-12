using CraftConsole.Core.Models;
using CraftConsole.Core.Process;
using CraftConsole.Infrastructure.Config;

namespace CraftConsole.Web.Services;

/// <summary>
/// Executes scheduled tasks: interval timers, daily HH:mm triggers, and
/// game-event triggers (player join / server ready). Tasks persist to
/// tasks.json in the app data folder — same file the desktop app used.
///
/// Each task targets one server via ServerId. Game-event triggers (PlayerJoin,
/// ServerReady) are evaluated against every server: this service subscribes to
/// GameEvent on every supervisor the registry already knows about, and on every
/// one created afterwards, so a server started after this service starts is
/// still covered.
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly ServerRegistry _registry;
    private readonly BackupService _backups;
    private readonly EventBroker _broker;
    private readonly SettingsHolder _settings;
    private readonly ILogger<SchedulerService> _log;
    private readonly JsonFileStore<List<ScheduledTask>> _store;

    // Every clock read and the tick timer go through this, so tests can drive
    // interval and daily triggers instantly instead of waiting on wall time.
    private readonly TimeProvider _time;

    private readonly object _lock = new();
    private readonly List<ScheduledTask> _tasks = [];
    private readonly Dictionary<Guid, DateTimeOffset> _nextDue = [];
    private string _lastCronMinute = "";

    // Tracks which servers' GameEvent this service has already hooked, so
    // SupervisorCreated firing for one already subscribed via All() at
    // construction can't double-subscribe it.
    private readonly HashSet<Guid> _subscribedServers = [];
    private readonly object _subscribeLock = new();

    /// <summary>How often trigger conditions are evaluated.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    public SchedulerService(
        ServerRegistry registry, BackupService backups, EventBroker broker, SettingsHolder settings,
        ILogger<SchedulerService> log, TimeProvider? timeProvider = null)
    {
        _registry = registry;
        _backups = backups;
        _broker = broker;
        _settings = settings;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
        _store = new JsonFileStore<List<ScheduledTask>>(settings.AppDataPath, "tasks.json");

        _registry.SupervisorCreated += SubscribeTo;
        foreach (var supervisor in _registry.All())
            SubscribeTo(supervisor);
    }

    private void SubscribeTo(ServerSupervisor supervisor)
    {
        lock (_subscribeLock)
        {
            if (!_subscribedServers.Add(supervisor.ServerId)) return;
        }
        supervisor.GameEvent += OnGameEvent;
    }

    public List<ScheduledTask> Snapshot()
    {
        lock (_lock) return [.. _tasks];
    }

    public async Task<ScheduledTask> AddAsync(ScheduledTask task)
    {
        lock (_lock)
        {
            task.ServerId ??= CurrentServerId();
            _tasks.Add(task);
            ScheduleNextDue(task);
        }
        await SaveAndPublishAsync();
        return task;
    }

    public async Task<bool> UpdateAsync(Guid id, ScheduledTask updated)
    {
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task is null) return false;
            task.ServerId = updated.ServerId ?? CurrentServerId();
            task.Name = updated.Name;
            task.TriggerType = updated.TriggerType;
            task.TriggerValue = updated.TriggerValue;
            task.ActionType = updated.ActionType;
            task.ActionValue = updated.ActionValue;
            task.IsEnabled = updated.IsEnabled;
            ScheduleNextDue(task);
        }
        await SaveAndPublishAsync();
        return true;
    }

    public async Task<bool> SetEnabledAsync(Guid id, bool enabled)
    {
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task is null) return false;
            task.IsEnabled = enabled;
            ScheduleNextDue(task);
        }
        await SaveAndPublishAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task is null) return false;
            _tasks.Remove(task);
            _nextDue.Remove(id);
        }
        await SaveAndPublishAsync();
        return true;
    }

    public Task<bool> RunNowAsync(Guid id)
    {
        ScheduledTask? task;
        lock (_lock) task = _tasks.FirstOrDefault(t => t.Id == id);
        if (task is null) return Task.FromResult(false);
        _ = ExecuteTaskAsync(task);
        return Task.FromResult(true);
    }

    // ── Execution loop ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await LoadAsync();
        _broker.Publish("tasks", new { Tasks = Snapshot() });

        using var timer = new PeriodicTimer(TickInterval, _time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            List<ScheduledTask> due = [];
            var now = _time.GetUtcNow();
            var minute = _time.GetLocalNow().ToString("HH:mm");

            lock (_lock)
            {
                foreach (var task in _tasks.Where(t => t.IsEnabled))
                {
                    switch (task.TriggerType)
                    {
                        case TriggerType.Interval
                            when _nextDue.TryGetValue(task.Id, out var at) && now >= at:
                            due.Add(task);
                            ScheduleNextDue(task);
                            break;

                        case TriggerType.TimeCron
                            when minute != _lastCronMinute && task.TriggerValue.Trim() == minute:
                            due.Add(task);
                            break;
                    }
                }
                _lastCronMinute = minute;
            }

            foreach (var task in due)
                _ = ExecuteTaskAsync(task);
        }
    }

    /// <summary>
    /// Reads tasks.json into the live list. Merges rather than replaces: the
    /// service is a singleton as well as a hosted service, so an AddAsync can
    /// land between construction and the loop starting, and replacing the list
    /// outright would drop it. A task saved before multi-server existed has no
    /// ServerId — migrated here to whatever was "the" server at the time.
    /// </summary>
    internal async Task LoadAsync()
    {
        var loaded = await _store.LoadAsync() ?? [];
        var fallback = CurrentServerId();

        lock (_lock)
        {
            foreach (var task in loaded)
            {
                if (_tasks.Any(t => t.Id == task.Id)) continue;
                task.ServerId ??= fallback;
                _tasks.Add(task);
            }

            foreach (var task in _tasks) ScheduleNextDue(task);
        }
    }

    private Guid? CurrentServerId()
        => Guid.TryParse(_settings.Current.ActiveProfileId, out var id) ? id : null;

    private void ScheduleNextDue(ScheduledTask task)
    {
        if (task.TriggerType == TriggerType.Interval
            && task.IsEnabled
            && int.TryParse(task.TriggerValue, out var secs) && secs > 0)
        {
            _nextDue[task.Id] = _time.GetUtcNow().AddSeconds(secs);
        }
        else
        {
            _nextDue.Remove(task.Id);
        }
    }

    private void OnGameEvent(ServerEvent evt)
    {
        List<ScheduledTask> triggered;
        lock (_lock)
        {
            triggered = _tasks.Where(t => t.IsEnabled && t.TriggerType switch
            {
                TriggerType.PlayerJoin => evt is PlayerJoinedEvent,
                TriggerType.ServerReady => evt is ServerReadyEvent,
                _ => false,
            }).ToList();
        }

        foreach (var task in triggered)
            _ = ExecuteTaskAsync(task);
    }

    /// <summary>
    /// Resolves the server a task targets, falling back to whichever profile is
    /// currently active if the task predates having one of its own (defensive —
    /// LoadAsync already migrates stored tasks; this covers one added via
    /// AddAsync/UpdateAsync by a caller that didn't set ServerId either).
    /// GetOrCreate rather than TryGet: a task targeting a server that has never
    /// been started should still resolve to a real (idle) supervisor, so the
    /// action below reports its own "not running" error rather than this
    /// method reporting a misleading "no such server".
    /// </summary>
    private ServerSupervisor? ResolveTarget(ScheduledTask task)
    {
        var id = task.ServerId ?? CurrentServerId();
        return id is { } serverId ? _registry.GetOrCreate(serverId) : null;
    }

    private async Task ExecuteTaskAsync(ScheduledTask task)
    {
        try
        {
            var supervisor = ResolveTarget(task)
                ?? throw new InvalidOperationException(
                    "This task has no server to target — set one on the task, or make a server active.");

            switch (task.ActionType)
            {
                case TaskActionType.SendCommand:
                    await supervisor.SendCommandAsync(task.ActionValue);
                    break;
                case TaskActionType.BroadcastMessage:
                    await supervisor.SendCommandAsync($"say {task.ActionValue}");
                    break;
                case TaskActionType.RestartServer:
                    await supervisor.RestartAsync();
                    break;
                case TaskActionType.RunBackup:
                    if (!Guid.TryParse(task.ActionValue, out var jobId))
                        throw new InvalidOperationException("No backup job is configured for this task.");
                    if (!await _backups.RunAsync(jobId))
                        throw new InvalidOperationException("The configured backup job no longer exists.");
                    break;
            }
            _broker.Publish("task-ran", new { task.Id, task.Name, At = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            // A task that fails (e.g. RestartServer against an RCON connection,
            // which can't relaunch a process it never started — see
            // ServerSupervisor.RestartAsync) used to sit there looking enabled and
            // simply never run, with nothing but a log line no one was watching.
            _log.LogWarning(ex, "Scheduled task {Name} failed", task.Name);
            _broker.Publish("task-failed", new { task.Id, task.Name, Message = ex.Message });
        }
    }

    private async Task SaveAndPublishAsync()
    {
        await _store.SaveAsync(Snapshot());
        _broker.Publish("tasks", new { Tasks = Snapshot() });
    }

    public override void Dispose()
    {
        _registry.SupervisorCreated -= SubscribeTo;
        foreach (var supervisor in _registry.All())
            supervisor.GameEvent -= OnGameEvent;
        base.Dispose();
    }
}
