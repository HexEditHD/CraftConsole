using CommunityToolkit.Mvvm.ComponentModel;

namespace CraftConsole.Modules.Editor.Models;

public partial class EditorTab : ObservableObject
{
    private bool _skipNextChange = true;

    public string FilePath { get; init; } = string.Empty;
    public string Title    { get; init; } = string.Empty;

    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private bool _isDirty;

    public string DisplayTitle => IsDirty ? Title + " *" : Title;

    partial void OnContentChanged(string value)
    {
        if (_skipNextChange) { _skipNextChange = false; return; }
        IsDirty = true;
        OnPropertyChanged(nameof(DisplayTitle));
    }

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayTitle));
    }
}
