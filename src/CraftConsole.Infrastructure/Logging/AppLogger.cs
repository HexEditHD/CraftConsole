using Serilog;
using Serilog.Events;

namespace CraftConsole.Infrastructure.Logging;

public static class AppLogger
{
    public static ILogger Create(string appDataPath, bool debug = false)
    {
        var logPath = Path.Combine(appDataPath, "logs", "mineui-.log");

        return new LoggerConfiguration()
            .MinimumLevel.Is(debug ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
    }
}
