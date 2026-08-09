namespace CraftConsole.Core.Models;

/// <summary>A record of one file CraftConsole placed on disk via the Modrinth installer, for the Installed list and Remove.</summary>
public class ModrinthInstall
{
    public Guid ServerId { get; set; }
    public string ProjectId { get; set; } = "";
    public string ProjectTitle { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
}
