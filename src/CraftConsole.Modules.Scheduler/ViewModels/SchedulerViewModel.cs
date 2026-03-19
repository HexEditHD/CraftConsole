using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftConsole.Core.Models;
using CraftConsole.Core.Process;
using CraftConsole.Core.Servers;

namespace CraftConsole.Modules.Scheduler.ViewModels;

public partial class SchedulerViewModel : ObservableObject
{
    private readonly string _appDataPath;
    private string _tasksFile => Path.Combine(_appDataPath, "tasks.json");

    private IMinecraftServer? _server;
    private IDisposable? _subscription;
    private readonly List<DispatcherTimer> _timers = [];

    public ObservableCollection<ScheduledTask> Tasks { get; } = [];

    // ── Add/Edit form ────────────────────────────────────────────────────
    [ObservableProperty] private bool _isAddingNew;
    [ObservableProperty] private string _formName = string.Empty;
    [ObservableProperty] private TriggerType _formTriggerType = TriggerType.Interval;
    [ObservableProperty] private string _formTriggerValue = string.Empty;
    [ObservableProperty] private TaskActionType _formActionType = TaskActionType.SendCommand;
    [ObservableProperty] private string _formActionValue = string.Empty;

    private ScheduledTask? _editingTask;

    public static TriggerType[]    AllTriggerTypes => Enum.GetValues<TriggerType>();
    public static TaskActionType[] AllActionTypes  => Enum.GetValues<TaskActionType>();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SchedulerViewModel(string appDataPath)
    {
        _appDataPath = appDataPath;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (!File.Exists(_tasksFile)) return;
        try
        {
            await using var stream = File.OpenRead(_tasksFile);
            var list = await JsonSerializer.DeserializeAsync<List<ScheduledTask>>(stream, JsonOptions);
            if (list is null) return;
            foreach (var task in list) Tasks.Add(task);
        }
        catch { /* ignore */ }
    }

    private async Task SaveAsync()
    {
        Directory.CreateDirectory(_appDataPath);
        await using var stream = File.Create(_tasksFile);
        await JsonSerializer.SerializeAsync(stream, Tasks.ToList(), JsonOptions);
    }

    public void Attach(IMinecraftServer server)
    {
        _server = server;
        StopAllTimers();

        _subscription = server.ConsoleOutput.Subscribe(entry =>
        {
            var evt = ServerEventParser.TryParse(entry);
            if (evt is null) return;

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var task in Tasks.Where(t => t.IsEnabled))
                {
                    switch (task.TriggerType)
                    {
                        case TriggerType.PlayerJoin when evt is PlayerJoinedEvent:
                            _ = ExecuteTaskAsync(task);
                            break;
                        case TriggerType.ServerReady when evt is ServerReadyEvent:
                            _ = ExecuteTaskAsync(task);
                            break;
                    }
                }
            });
        });

        StartTimers();
    }

    private void StartTimers()
    {
        foreach (var task in Tasks.Where(t => t.IsEnabled))
            AttachTimer(task);
    }

    private void AttachTimer(ScheduledTask task)
    {
        switch (task.TriggerType)
        {
            case TriggerType.Interval:
                if (int.TryParse(task.TriggerValue, out var secs) && secs > 0)
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
                    timer.Tick += (_, _) => { if (task.IsEnabled) _ = ExecuteTaskAsync(task); };
                    timer.Start();
                    _timers.Add(timer);
                }
                break;

            case TriggerType.TimeCron:
                // Check once per minute
                if (!_timers.Any(t => t.Tag is "cron"))
                {
                    var cronTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30), Tag = "cron" };
                    cronTimer.Tick += (_, _) => CheckCronTasks();
                    cronTimer.Start();
                    _timers.Add(cronTimer);
                }
                break;
        }
    }

    private void CheckCronTasks()
    {
        var now = DateTime.Now.ToString("HH:mm");
        foreach (var task in Tasks.Where(t => t.IsEnabled && t.TriggerType == TriggerType.TimeCron))
        {
            if (task.TriggerValue.Trim() == now)
                _ = ExecuteTaskAsync(task);
        }
    }

    private void StopAllTimers()
    {
        foreach (var t in _timers) t.Stop();
        _timers.Clear();
        _subscription?.Dispose();
    }

    private async Task ExecuteTaskAsync(ScheduledTask task)
    {
        if (_server is null) return;
        switch (task.ActionType)
        {
            case TaskActionType.SendCommand:
                await _server.SendCommandAsync(task.ActionValue);
                break;
            case TaskActionType.BroadcastMessage:
                await _server.SendCommandAsync($"say {task.ActionValue}");
                break;
            case TaskActionType.RestartServer:
                await _server.StopAsync();
                await Task.Delay(3000);
                await _server.StartAsync();
                break;
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void StartAdd()
    {
        _editingTask = null;
        FormName = string.Empty;
        FormTriggerType = TriggerType.Interval;
        FormTriggerValue = string.Empty;
        FormActionType = TaskActionType.SendCommand;
        FormActionValue = string.Empty;
        IsAddingNew = true;
    }

    [RelayCommand]
    private void EditTask(ScheduledTask task)
    {
        _editingTask = task;
        FormName = task.Name;
        FormTriggerType = task.TriggerType;
        FormTriggerValue = task.TriggerValue;
        FormActionType = task.ActionType;
        FormActionValue = task.ActionValue;
        IsAddingNew = true;
    }

    [RelayCommand]
    private void CancelAdd() => IsAddingNew = false;

    [RelayCommand]
    private async Task SaveTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(FormName)) return;

        if (_editingTask is not null)
        {
            _editingTask.Name         = FormName;
            _editingTask.TriggerType  = FormTriggerType;
            _editingTask.TriggerValue = FormTriggerValue;
            _editingTask.ActionType   = FormActionType;
            _editingTask.ActionValue  = FormActionValue;
        }
        else
        {
            var task = new ScheduledTask
            {
                Name         = FormName,
                TriggerType  = FormTriggerType,
                TriggerValue = FormTriggerValue,
                ActionType   = FormActionType,
                ActionValue  = FormActionValue,
            };
            Tasks.Add(task);
            AttachTimer(task);
        }

        IsAddingNew = false;
        await SaveAsync();
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(ScheduledTask task)
    {
        Tasks.Remove(task);
        await SaveAsync();
    }

    [RelayCommand]
    private async Task TestTaskAsync(ScheduledTask task) => await ExecuteTaskAsync(task);
}
