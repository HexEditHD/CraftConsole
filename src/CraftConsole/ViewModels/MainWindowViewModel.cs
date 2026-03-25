using CommunityToolkit.Mvvm.ComponentModel;
using CraftConsole.Core.Models;
using CraftConsole.Infrastructure.Config;
using CraftConsole.Infrastructure.Http;
using CraftConsole.Modules.Backup.ViewModels;
using CraftConsole.Modules.Console.ViewModels;
using CraftConsole.Modules.Dashboard.ViewModels;
using CraftConsole.Modules.Editor.ViewModels;
using CraftConsole.Modules.Issues.ViewModels;
using CraftConsole.Modules.Players.ViewModels;
using CraftConsole.Modules.Plugins.ViewModels;
using CraftConsole.Modules.Scheduler.ViewModels;
using CraftConsole.Modules.Server.ViewModels;
using CraftConsole.Modules.Settings.ViewModels;
using CraftConsole.Views;

namespace CraftConsole.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] private ObservableObject _currentPage;
    [ObservableProperty] private NavItem _selectedNavItem;
    [ObservableProperty] private string _serverStatusLabel = "No server";
    [ObservableProperty] private bool _serverRunning;

    public List<NavItem> NavItems { get; }
    private readonly ServerViewModel _serverVm;

    public MainWindowViewModel(AppSettings settings, string appDataPath)
    {
        var http           = new HttpClient();
        var downloader     = new DownloadService(http);
        var serverDownload = new ServerDownloadService(http, downloader);
        var javaDownload   = new JavaDownloadService(http, downloader);

        var dashboardVm  = new DashboardViewModel();
        var consoleVm    = new ConsoleViewModel();
        var playersVm    = new PlayersViewModel();
        var issuesVm     = new IssuesViewModel();
        var serverVm     = new ServerViewModel(serverDownload, javaDownload);
        var pluginsVm    = new PluginsViewModel();
        var editorVm     = new EditorViewModel();
        var backupVm     = new BackupViewModel(appDataPath);
        var schedulerVm  = new SchedulerViewModel(appDataPath);
        var settingsVm   = new SettingsViewModel(settings, appDataPath);

        _serverVm = serverVm;

        // Pass server quick-action commands to console sidebar
        consoleVm.StartServerCommand = serverVm.StartServerCommand;
        consoleVm.StopServerCommand  = serverVm.StopServerCommand;

        NavItems =
        [
            new NavItem("Dashboard", "\uE950", dashboardVm),
            new NavItem("Console",   "\uE756", consoleVm),
            new NavItem("Players",   "\uE716", playersVm),
            new NavItem("Issues",    "\uE7BA", issuesVm),
            new NavItem("Server",    "\uE774", serverVm),
            new NavItem("Plugins",   "\uE74C", pluginsVm),
            new NavItem("Editor",    "\uE70F", editorVm),
            new NavItem("Backup",    "\uE74E", backupVm),
            new NavItem("Scheduler", "\uE787", schedulerVm),
            new NavItem("Settings",  "\uE713", settingsVm),
        ];

        // Wire server start → attach all consumers
        var consoleNavItem = NavItems[1];
        serverVm.ServerStarted = server =>
        {
            dashboardVm.Attach(server);
            dashboardVm.SetPlayerSource(playersVm.Players);
            consoleVm.Attach(server);
            playersVm.Attach(server);
            issuesVm.Attach(server);
            pluginsVm.Attach(server);
            editorVm.Attach(server);
            schedulerVm.Attach(server);

            // Update header status chip
            server.StatusChanged.Subscribe(s =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ServerStatusLabel = s.ToString();
                    ServerRunning     = s == ServerStatus.Running;
                }));
        };

        // Wire reason dialog for Players (must be done via the View — see PlayersView.axaml.cs)
        // playersVm.ShowReasonDialogAsync is wired when the PlayersView attaches to the visual tree

        serverVm.NavigateToConsoleRequested = () =>
        {
            SelectedNavItem = consoleNavItem;
        };

        _selectedNavItem = NavItems[0];
        _currentPage = _selectedNavItem.ViewModel;
    }

    partial void OnSelectedNavItemChanged(NavItem value) => CurrentPage = value.ViewModel;

    public Task ShutdownAsync() => _serverVm.ShutdownAsync();
}

public record NavItem(string Label, string Icon, ObservableObject ViewModel);
