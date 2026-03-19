using System.Collections.ObjectModel;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CraftConsole.Core.Models;
using CraftConsole.Core.Servers;

namespace CraftConsole.Modules.Plugins.ViewModels;

public partial class PluginsViewModel : ObservableObject
{
    private string? _pluginsFolder;

    public ObservableCollection<PluginInfo> Plugins { get; } = [];

    [ObservableProperty] private bool _isScanning;

    public void Attach(IMinecraftServer server)
    {
        _pluginsFolder = Path.Combine(server.Profile.WorkingDirectory, "plugins");
        _ = ScanPluginsAsync();
    }

    [RelayCommand]
    private async Task ScanPluginsAsync()
    {
        if (_pluginsFolder is null || !Directory.Exists(_pluginsFolder)) return;

        IsScanning = true;
        Plugins.Clear();

        var jars = await Task.Run(() =>
            Directory.GetFiles(_pluginsFolder, "*.jar", SearchOption.TopDirectoryOnly));

        foreach (var jar in jars)
        {
            var info = await Task.Run(() => TryReadPluginYaml(jar));
            Plugins.Add(info);
        }

        IsScanning = false;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_pluginsFolder is null) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _pluginsFolder,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void RemovePlugin(PluginInfo plugin)
    {
        if (_pluginsFolder is null) return;
        var source = Path.Combine(_pluginsFolder, plugin.FileName);
        var disabledDir = Path.Combine(_pluginsFolder, "disabled");
        Directory.CreateDirectory(disabledDir);
        var dest = Path.Combine(disabledDir, plugin.FileName);
        try
        {
            File.Move(source, dest, overwrite: true);
            Plugins.Remove(plugin);
        }
        catch { /* ignore file errors */ }
    }

    private static PluginInfo TryReadPluginYaml(string jarPath)
    {
        var fileName = Path.GetFileName(jarPath);
        string name = Path.GetFileNameWithoutExtension(jarPath);
        string description = string.Empty;
        string author = string.Empty;
        string version = string.Empty;

        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("plugin.yml") ?? zip.GetEntry("plugin.yaml");
            if (entry is not null)
            {
                using var reader = new StreamReader(entry.Open());
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.StartsWith("name:"))        name        = line[5..].Trim().Trim('\'', '"');
                    else if (line.StartsWith("version:")) version     = line[8..].Trim().Trim('\'', '"');
                    else if (line.StartsWith("description:")) description = line[12..].Trim().Trim('\'', '"');
                    else if (line.StartsWith("author:")) author      = line[7..].Trim().Trim('\'', '"');
                }
            }
        }
        catch { /* bad zip or no plugin.yml */ }

        return new PluginInfo
        {
            FileName    = fileName,
            Name        = name,
            Version     = version,
            Description = description,
            Author      = author,
        };
    }
}
