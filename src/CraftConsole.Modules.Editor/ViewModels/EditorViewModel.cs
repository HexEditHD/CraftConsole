using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftConsole.Core.Servers;
using CraftConsole.Modules.Editor.Models;

namespace CraftConsole.Modules.Editor.ViewModels;

public partial class EditorViewModel : ObservableObject
{
    private static readonly HashSet<string> EditableExtensions =
        [".yml", ".yaml", ".json", ".txt", ".properties", ".log"];

    private string? _rootDirectory;

    public ObservableCollection<FileNode> FileTree { get; } = [];
    public ObservableCollection<EditorTab> Tabs { get; } = [];

    [ObservableProperty] private EditorTab? _selectedTab;

    /// <summary>Set by code-behind to show a confirm-close dialog for dirty tabs.</summary>
    public Func<EditorTab, Task<bool>>? ConfirmCloseAsync { get; set; }

    public void Attach(IMinecraftServer server)
    {
        _rootDirectory = server.Profile.WorkingDirectory;
        LoadFileTree();
    }

    private void LoadFileTree()
    {
        FileTree.Clear();
        if (_rootDirectory is null || !Directory.Exists(_rootDirectory)) return;

        var root = BuildNode(_rootDirectory);
        if (root is not null)
            foreach (var child in root.Children)
                FileTree.Add(child);
    }

    private static FileNode? BuildNode(string path)
    {
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!EditableExtensions.Contains(ext)) return null;
            return new FileNode
            {
                FullPath    = path,
                Name        = Path.GetFileName(path),
                IsDirectory = false,
            };
        }

        if (!Directory.Exists(path)) return null;

        var node = new FileNode
        {
            FullPath    = path,
            Name        = Path.GetFileName(path),
            IsDirectory = true,
        };

        // Files first, then subdirectories
        foreach (var file in Directory.GetFiles(path))
        {
            var child = BuildNode(file);
            if (child is not null) node.Children.Add(child);
        }
        foreach (var dir in Directory.GetDirectories(path))
        {
            var child = BuildNode(dir);
            if (child is not null) node.Children.Add(child);
        }

        // Only include directories that have editable descendants
        return node.Children.Count > 0 ? node : null;
    }

    [RelayCommand]
    private void OpenFile(FileNode node)
    {
        if (node.IsDirectory)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }

        var existing = Tabs.FirstOrDefault(t => t.FilePath == node.FullPath);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var content = File.ReadAllText(node.FullPath);
        var tab = new EditorTab
        {
            FilePath = node.FullPath,
            Title    = node.Name,
            Content  = content,
        };
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private async Task CloseTabAsync(EditorTab tab)
    {
        if (tab.IsDirty && ConfirmCloseAsync is not null)
        {
            var confirmed = await ConfirmCloseAsync(tab);
            if (!confirmed) return;
        }

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (SelectedTab == tab)
        {
            SelectedTab = Tabs.Count > 0
                ? Tabs[Math.Max(0, index - 1)]
                : null;
        }
    }

    [RelayCommand]
    private async Task SaveTabAsync()
    {
        if (SelectedTab is null) return;
        await File.WriteAllTextAsync(SelectedTab.FilePath, SelectedTab.Content);
        SelectedTab.IsDirty = false;
    }
}
