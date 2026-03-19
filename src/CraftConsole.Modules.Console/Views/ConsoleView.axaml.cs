using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CraftConsole.Modules.Console.ViewModels;

namespace CraftConsole.Modules.Console.Views;

public partial class ConsoleView : UserControl
{
    public ConsoleView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is ConsoleViewModel vm)
        {
            vm.Entries.CollectionChanged += OnEntriesChanged;
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Scroll to end every time this tab becomes active
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => ConsoleScroll.ScrollToEnd(),
            Avalonia.Threading.DispatcherPriority.Loaded);

        this.GetObservable(IsVisibleProperty).Subscribe(isVisible =>
        {
            if (isVisible)
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => ConsoleScroll.ScrollToEnd(),
                    Avalonia.Threading.DispatcherPriority.Loaded);
        });
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ConsoleScroll.ScrollToEnd();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ConsoleViewModel vm) return;

        switch (e.Key)
        {
            case Key.Up:
                vm.HistoryUp();
                e.Handled = true;
                // Move caret to end after history navigation
                CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
                break;

            case Key.Down:
                vm.HistoryDown();
                e.Handled = true;
                CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
                break;

            case Key.Tab:
                if (vm.ShowSuggestions && vm.Suggestions.Count > 0)
                {
                    vm.AcceptSuggestion(vm.Suggestions[0]);
                    CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
                    e.Handled = true;
                }
                break;

            case Key.Escape:
                vm.DismissSuggestions();
                e.Handled = true;
                break;

            case Key.Enter:
                vm.DismissSuggestions();
                if (vm.SendCommandCommand.CanExecute(null))
                    vm.SendCommandCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ConsoleViewModel vm) return;
        if (SuggestionList.SelectedItem is string suggestion)
        {
            vm.AcceptSuggestion(suggestion);
            SuggestionList.SelectedItem = null;
            CommandBox.Focus();
            CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
        }
    }
}
