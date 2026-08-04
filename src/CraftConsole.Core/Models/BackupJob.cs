using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CraftConsole.Core.Models;

public partial class BackupJob : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<string> SourcePaths { get; set; } = [];
    public string DestinationPath { get; set; } = string.Empty;
    public CompressionLevel Compression { get; set; } = CompressionLevel.Optimal;
    public bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    private DateTimeOffset? _lastRun;
}
