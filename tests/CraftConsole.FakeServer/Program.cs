// A stand-in for a Paper/Vanilla Minecraft server.
//
// ServerProcessManager launches it exactly as it would launch java — the
// java-style arguments it builds (-Xms/-Xmx/-jar/nogui) are simply ignored
// here. That lets the tests exercise the real launch, capture, parse, event
// and shutdown paths without a JVM, a server jar, or the Minecraft EULA.
//
// Behaviour is selected with CRAFTCONSOLE_FAKE_MODE:
//
//   normal      boot, report ready, then serve until told to stop  (default)
//   eula        print the EULA notice and exit 0, as a first run does
//   crash       fail during boot and exit non-zero
//   hang        ignore "stop" entirely — exercises the kill fallback
//   exitcode    report ready, then exit non-zero when asked to stop
//   stderr      write an unprefixed failure to stderr and exit non-zero
//
// CRAFTCONSOLE_FAKE_BOOT_MS overrides the boot delay (default 60ms).

using System.Globalization;

var mode = Environment.GetEnvironmentVariable("CRAFTCONSOLE_FAKE_MODE") ?? "normal";
var bootMs = int.TryParse(
    Environment.GetEnvironmentVariable("CRAFTCONSOLE_FAKE_BOOT_MS"),
    NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
    ? parsed
    : 60;

void Log(string level, string message) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Server thread/{level}]: {message}");

// ── Boot ─────────────────────────────────────────────────────────────────
Log("INFO", "Starting minecraft server version 1.21.4");
Log("INFO", "Loading properties");
Log("INFO", "Default game type: SURVIVAL");

if (mode == "eula")
{
    // Vanilla and Paper both print this and exit before binding a port.
    Log("WARN", "Failed to load eula.txt");
    Log("INFO", "You need to agree to the EULA in order to run the server. Go to eula.txt for more info.");
    return 0;
}

if (mode == "stderr")
{
    // No level prefix, so the parser cannot classify it — this is the shape of
    // a real JVM failure such as "Error: Unable to access jarfile server.jar".
    Console.Error.WriteLine("Error: Unable to access jarfile server.jar");
    return 1;
}

if (mode == "crash")
{
    Log("ERROR", "Encountered an unexpected exception");
    Console.Error.WriteLine("java.lang.OutOfMemoryError: Java heap space");
    return 1;
}

await Task.Delay(bootMs);

Log("INFO", "Preparing level \"world\"");
Log("INFO", "Preparing start region for dimension minecraft:overworld");
Log("INFO", "Done (7.312s)! For help, type \"help\"");

// ── Command loop ─────────────────────────────────────────────────────────
string? line;
while ((line = Console.ReadLine()) is not null)
{
    var command = line.Trim();
    if (command.Length == 0) continue;

    switch (command)
    {
        case "stop":
            if (mode == "hang")
            {
                Log("INFO", "Ignoring stop (hang mode)");
                // Keep reading so stdin stays open; only a kill ends this.
                continue;
            }

            Log("INFO", "Stopping the server");
            Log("INFO", "Saving worlds");
            Log("INFO", "ThreadedAnvilChunkStorage: All dimensions are saved");
            return mode == "exitcode" ? 3 : 0;

        case "list":
            Log("INFO", "There are 0 of a max of 20 players online:");
            break;

        // Test hooks — emit realistic events on demand.
        case "fake join":
            Log("INFO", "Steve[/192.168.1.50:51234] logged in with entity id 261");
            Log("INFO", "Steve joined the game");
            break;

        case "fake leave":
            Log("INFO", "Steve left the game");
            break;

        case "fake chat":
            Log("INFO", "<Steve> hello world");
            break;

        case "fake warn":
            Log("WARN", "Can't keep up! Is the server overloaded? Running 2043ms or 40 ticks behind");
            break;

        case "fake error":
            Log("ERROR", "Exception ticking world");
            break;

        default:
            Log("INFO", $"Unknown or incomplete command, see below for error{Environment.NewLine}{command}<--[HERE]");
            break;
    }
}

// stdin closed without a stop command.
return 0;
