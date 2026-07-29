using CraftConsole.Core.Models;
using CraftConsole.Core.Process;
using CraftConsole.Infrastructure.Config;

namespace CraftConsole.Web.Services;

/// <summary>
/// Executes scheduled tasks: interval timers, daily HH:mm triggers, and
/// game-event triggers (player join / server ready). Tasks persist to
/// tasks.json in the app data folder — same file the desktop app used.
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly ServerSupervisor _supervisor;
    private readonly EventBroker _broker;
    private readonly ILogger<SchedulerService> _log;
    private readonly JsonFileStore<List<ScheduledTask>> _store;

    private readonly object _lock = new();
    private List<ScheduledTask> _tasks = [];
    private readonly Dictionary<Guid, DateTimeOffset> _nextDue = [];
    private string _lastCronMinute = "";

    public SchedulerService(
        ServerSupervisor supervisor, EventBroker broker, SettingsHolder settings,
        ILogger<SchedulerService> log)
    {
        _supervisor = supervisor;
        _broker = broker;
        _log = log;
        _store = new JsonFileStore<List<ScheduledTask>>(settings.AppDataPath, "tasks.json");

        _supervisor.GameEvent += OnGameEvent;
    }

    public List<ScheduledTask> Snapshot()
    {
        lock (_lock) return [.. _tasks];
    }

    public async Task<ScheduledTask> AddAsync(ScheduledTask task)
    {
        lock (_lock)
        {
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
        _tasks = await _store.LoadAsync() ?? [];
        lock (_lock)
        {
            foreach (var task in _tasks) ScheduleNextDue(task);
        }
        _broker.Publish("tasks", new { Tasks = Snapshot() });

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            List<ScheduledTask> due = [];
            var now = DateTimeOffset.UtcNow;
            var minute = DateTime.Now.ToString("HH:mm");

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

    private void ScheduleNextDue(ScheduledTask task)
    {
        if (task.TriggerType == TriggerType.Interval
            && task.IsEnabled
            && int.TryParse(task.TriggerValue, out var secs) && secs > 0)
        {
            _nextDue[task.Id] = DateTimeOffset.UtcNow.AddSeconds(secs);
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

    private async Task ExecuteTaskAsync(ScheduledTask task)
    {
        try
        {
            switch (task.ActionType)
            {
                case TaskActionType.SendCommand:
                    await _supervisor.SendCommandAsync(task.ActionValue);
                    break;
                case TaskActionType.BroadcastMessage:
                    await _supervisor.SendCommandAsync($"say {task.ActionValue}");
                    break;
                case TaskActionType.RestartServer:
                    await _supervisor.RestartAsync();
                    break;
            }
            _broker.Publish("task-ran", new { task.Id, task.Name, At = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Scheduled task {Name} failed", task.Name);
        }
    }

    private async Task SaveAndPublishAsync()
    {
        await _store.SaveAsync(Snapshot());
        _broker.Publish("tasks", new { Tasks = Snapshot() });
    }

    public override void Dispose()
    {
        _supervisor.GameEvent -= OnGameEvent;
        base.Dispose();
    }
}
