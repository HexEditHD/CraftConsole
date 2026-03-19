using Avalonia.Controls;
using Avalonia.Interactivity;
using CraftConsole.Modules.Editor.Models;
using CraftConsole.Modules.Editor.ViewModels;

namespace CraftConsole.Modules.Editor.Views;

public partial class EditorView : UserControl
{
    public EditorView()
    {
        InitializeComponent();
    }

    private void OnTreeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel vm) return;
        if (FileTreeView.SelectedItem is FileNode node)
            vm.OpenFileCommand.Execute(node);
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EditorTab tab } &&
            DataContext is EditorViewModel vm)
        {
            vm.SelectedTab = tab;
        }
    }
}
