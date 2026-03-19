namespace CraftConsole.Core.Models;

public enum TriggerType { Interval, TimeCron, PlayerJoin, ServerReady }
public enum TaskActionType { SendCommand, BroadcastMessage, RestartServer }

public class ScheduledTask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public TriggerType TriggerType { get; set; } = TriggerType.Interval;
    public string TriggerValue { get; set; } = string.Empty;
    public TaskActionType ActionType { get; set; } = TaskActionType.SendCommand;
    public string ActionValue { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}
