using Avalonia.Controls;
using Avalonia.Input;
using CraftConsole.Core.Players;
using CraftConsole.Modules.Players.ViewModels;

namespace CraftConsole.Modules.Players.Views;

public partial class PlayersView : UserControl
{
    public PlayersView()
    {
        InitializeComponent();
    }

    private void OnOnlineGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.PointerPressedEventArgs.GetCurrentPoint(null).Properties.IsRightButtonPressed
            && DataContext is PlayersViewModel vm)
        {
            vm.SelectedPlayer = e.Row.DataContext as Player;
        }
    }

    private void OnBannedGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.PointerPressedEventArgs.GetCurrentPoint(null).Properties.IsRightButtonPressed
            && DataContext is PlayersViewModel vm)
        {
            vm.SelectedBannedPlayer = e.Row.DataContext as BannedPlayerEntry;
        }
    }

    private void OnBannedIpsGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.PointerPressedEventArgs.GetCurrentPoint(null).Properties.IsRightButtonPressed
            && DataContext is PlayersViewModel vm)
        {
            vm.SelectedBannedIp = e.Row.DataContext as BannedIpEntry;
        }
    }
}
