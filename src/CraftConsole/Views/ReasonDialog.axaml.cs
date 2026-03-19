using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CraftConsole.Views;

public partial class ReasonDialog : Window
{
    private string? _result;

    public static async Task<string?> ShowAsync(Window owner, string prompt)
    {
        var dialog = new ReasonDialog(prompt);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    public ReasonDialog(string prompt)
    {
        InitializeComponent();
        PromptLabel.Text = prompt;
        Opened += (_, _) => ReasonBox.Focus();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _result = ReasonBox.Text ?? string.Empty;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }
}
