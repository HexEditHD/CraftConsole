using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CraftConsole.Modules.Editor.Models;

public partial class FileNode : ObservableObject
{
    public string FullPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public ObservableCollection<FileNode> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;
}
