namespace CraftConsole.Core.Java;

/// <summary>A detected Java runtime on the local machine.</summary>
public record JavaInstallation(
    string ExecutablePath,
    string DisplayVersion,
    int MajorVersion)
{
    public string Label => $"Java {MajorVersion}  ({DisplayVersion})";
}
