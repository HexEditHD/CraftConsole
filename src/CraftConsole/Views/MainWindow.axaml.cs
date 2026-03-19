using Avalonia.Controls;
using CraftConsole.Modules.Players.ViewModels;
using CraftConsole.ViewModels;

namespace CraftConsole.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
        Loaded  += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Wire reason dialog for PlayersViewModel — must happen here so we have a Window ref
        var playersNav = vm.NavItems.FirstOrDefault(n => n.Label == "Players");
        if (playersNav?.ViewModel is PlayersViewModel playersVm)
        {
            playersVm.ShowReasonDialogAsync = prompt =>
                ReasonDialog.ShowAsync(this, prompt);
        }
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            // Prevent the window from closing while we stop the server
            e.Cancel = true;
            await vm.ShutdownAsync();
            Closing -= OnWindowClosing;
            Close();
        }
    }
}
