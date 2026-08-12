namespace CraftConsole.Core.Models;

public enum TriggerType { Interval, TimeCron, PlayerJoin, ServerReady }
public enum TaskActionType { SendCommand, BroadcastMessage, RestartServer, RunBackup }

public class ScheduledTask
{
    public Guid Id { get; init; } = Guid.NewGuid();

    // Null on a task saved before multi-server existed, or one created by a
    // caller that hasn't set it — SchedulerService resolves either case to
    // whatever profile is currently active, both at load time (migrating the
    // stored value) and defensively again at execution time.
    public Guid? ServerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public TriggerType TriggerType { get; set; } = TriggerType.Interval;
    public string TriggerValue { get; set; } = string.Empty;
    public TaskActionType ActionType { get; set; } = TaskActionType.SendCommand;
    public string ActionValue { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}
