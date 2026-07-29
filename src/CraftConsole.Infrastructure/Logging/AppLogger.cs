using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CraftConsole.Infrastructure.Logging;

public static class AppLogger
{
    /// <summary>
    /// Builds the application logger: console plus a daily rolling file under
    /// <paramref name="appDataPath"/>/logs, keeping a week of history.
    /// </summary>
    public static Logger Create(string appDataPath, bool debug = false)
    {
        var logPath = Path.Combine(appDataPath, "logs", "craftconsole-.log");

        return new LoggerConfiguration()
            .MinimumLevel.Is(debug ? LogEventLevel.Debug : LogEventLevel.Information)
            // ASP.NET Core logs a pair of lines per request at Information; that would
            // bury everything else in a panel that polls status and streams SSE.
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate:
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
