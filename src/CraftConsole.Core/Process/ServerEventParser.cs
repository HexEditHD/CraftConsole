using System.Text.RegularExpressions;
using CraftConsole.Core.Models;
using CraftConsole.Core.Players;

namespace CraftConsole.Core.Process;

/// <summary>
/// Extracts typed server events from parsed console entries.
/// </summary>
public static class ServerEventParser
{
    // Paper/Spigot: "Steve[/192.168.1.5:51234] logged in with entity id …"
    private static readonly Regex PlayerLogin = new(
        @"^(?<name>\w+)\[/(?<ip>[\d.]+):\d+\] logged in", RegexOptions.Compiled);
    private static readonly Regex PlayerJoin  = new(@"^(?<name>\w+) joined the game$", RegexOptions.Compiled);
    private static readonly Regex PlayerLeave = new(@"^(?<name>\w+) left the game$", RegexOptions.Compiled);
    private static readonly Regex PlayerChat  = new(@"^<(?<name>\w+)> (?<msg>.+)$", RegexOptions.Compiled);
    private static readonly Regex ServerReady = new(@"Done \(\d+\.\d+s\)!", RegexOptions.Compiled);
    private static readonly Regex Overloaded  = new(@"Can't keep up! Is the server overloaded\? Running (?<ms>[\d.]+)ms", RegexOptions.Compiled);

    public static ServerEvent? TryParse(ConsoleEntry entry)
    {
        var msg = entry.Message;

        if (PlayerLogin.Match(msg) is { Success: true } login)
            return new PlayerJoinedEvent(new Player
            {
                Username  = login.Groups["name"].Value,
                IpAddress = login.Groups["ip"].Value,
            });

        if (PlayerJoin.Match(msg) is { Success: true } join)
            return new PlayerJoinedEvent(new Player { Username = join.Groups["name"].Value });

        if (PlayerLeave.Match(msg) is { Success: true } leave)
            return new PlayerLeftEvent(leave.Groups["name"].Value);

        if (PlayerChat.Match(msg) is { Success: true } chat)
            return new PlayerChatEvent(chat.Groups["name"].Value, chat.Groups["msg"].Value);

        if (ServerReady.IsMatch(msg))
            return new ServerReadyEvent();

        if (Overloaded.Match(msg) is { Success: true } ov && double.TryParse(ov.Groups["ms"].Value, out var ms))
            return new ServerOverloadedEvent(ms);

        return null;
    }
}
