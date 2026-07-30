using System.Globalization;
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
    // ip group uses .+ to handle both IPv4 (127.0.0.1) and IPv6 (::1 / 0:0:0:0:0:0:0:1)
    private static readonly Regex PlayerLogin = new(
        @"^(?<name>\w+)\[/(?<ip>.+):\d+\] logged in", RegexOptions.Compiled);
    // \s*$ instead of $ so trailing \r or spaces don't break the match
    private static readonly Regex PlayerJoin  = new(@"^(?<name>\w+) joined the game\s*$", RegexOptions.Compiled);
    private static readonly Regex PlayerLeave = new(@"^(?<name>\w+) left the game\s*$",  RegexOptions.Compiled);
    private static readonly Regex PlayerChat  = new(@"^<(?<name>\w+)> (?<msg>.+)$", RegexOptions.Compiled);
    // Anchored. Unanchored, a player chatting "Done (1.0s)!" flipped a Starting
    // server to Running; the same trick faked overload warnings.
    private static readonly Regex ServerReady = new(@"^Done \(\d+[.,]\d+s\)!", RegexOptions.Compiled);
    private static readonly Regex Overloaded  = new(@"^Can't keep up! Is the server overloaded\? Running (?<ms>[\d.,]+)ms", RegexOptions.Compiled);

    public static ServerEvent? TryParse(ConsoleEntry entry)
    {
        var msg = entry.Message.Trim();

        // If ConsoleOutputParser couldn't extract the message (Unknown level = full raw line
        // stored as Message), pull the message out manually from after the last ']: '.
        // This handles Forge, Fabric, and any other server that adds extra bracket groups.
        if (entry.Level == ConsoleEntryLevel.Unknown)
        {
            var idx = msg.LastIndexOf("]: ");
            if (idx >= 0)
                msg = msg[(idx + 3)..].Trim();
        }

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

        // Checked before the server-state patterns below so a chat line can never be
        // mistaken for server output.
        if (PlayerChat.Match(msg) is { Success: true } chat)
            return new PlayerChatEvent(chat.Groups["name"].Value, chat.Groups["msg"].Value);

        if (ServerReady.IsMatch(msg))
            return new ServerReadyEvent();

        // InvariantCulture: a server logging "2043,5ms" under a comma-decimal locale
        // silently failed to parse, dropping the event entirely.
        if (Overloaded.Match(msg) is { Success: true } ov
            && double.TryParse(
                ov.Groups["ms"].Value.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var ms))
            return new ServerOverloadedEvent(ms);

        return null;
    }
}
