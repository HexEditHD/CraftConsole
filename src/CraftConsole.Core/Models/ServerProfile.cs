namespace CraftConsole.Core.Models;

public class ServerProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string JarPath { get; set; }
    public required string WorkingDirectory { get; set; }
    public string JavaPath { get; set; } = "java";
    public int MinRamMb { get; set; } = 512;
    public int MaxRamMb { get; set; } = 2048;
    public string MinecraftVersion { get; set; } = string.Empty;
    public string JvmArguments { get; set; } = string.Empty;
    public ServerType Type { get; set; } = ServerType.Paper;
}
