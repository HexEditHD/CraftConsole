namespace CraftConsole.Core.Models;

/// <summary>A record of one file CraftConsole placed on disk via the CurseForge installer, for the Installed list and Remove.</summary>
public class CurseForgeInstall
{
    public Guid ServerId { get; set; }
    public int ModId { get; set; }
    public string ModName { get; set; } = "";
    public int FileId { get; set; }
    public string FileName { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
}
