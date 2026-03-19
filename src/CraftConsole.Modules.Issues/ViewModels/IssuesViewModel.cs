using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftConsole.Core.Models;
using CraftConsole.Core.Process;
using CraftConsole.Core.Servers;

namespace CraftConsole.Modules.Issues.ViewModels;

public partial class IssuesViewModel : ObservableObject
{
    private IDisposable? _subscription;
    private int _nextId = 1;

    public ObservableCollection<IssueEntry> Issues { get; } = [];

    [ObservableProperty] private int _issueCount;

    public void Attach(IMinecraftServer server)
    {
        _subscription?.Dispose();

        _subscription = server.ConsoleOutput.Subscribe(entry =>
        {
            var type = Classify(entry);
            if (type is null) return;

            var issue = new IssueEntry
            {
                Id        = _nextId++,
                Type      = type.Value,
                Timestamp = entry.Timestamp,
                Message   = entry.Message,
            };

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Issues.Add(issue);
                IssueCount = Issues.Count;
            });
        });
    }

    [RelayCommand]
    private void ClearIssues()
    {
        Issues.Clear();
        IssueCount = 0;
        _nextId = 1;
    }

    private static IssueType? Classify(ConsoleEntry entry)
    {
        return entry.Level switch
        {
            ConsoleEntryLevel.Warn  => IssueType.Warning,
            ConsoleEntryLevel.Error => IssueType.Severe,
            ConsoleEntryLevel.Info when entry.Message.Contains("Can't keep up") => IssueType.Warning,
            ConsoleEntryLevel.Info when entry.Message.Contains("overloaded")    => IssueType.Warning,
            ConsoleEntryLevel.Info when entry.Message.Contains("Exception")     => IssueType.Severe,
            _ => null,
        };
    }
}
