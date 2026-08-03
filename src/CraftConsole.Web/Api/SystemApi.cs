using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>Host OS info the frontend uses to render OS-appropriate paths, placeholders, and copy.</summary>
public static class SystemApi
{
    public static void MapSystemApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/info", (SettingsHolder settings) =>
        {
            var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other";
            return Results.Json(new
            {
                Platform = platform,
                PathSeparator = Path.DirectorySeparatorChar.ToString(),
                DataDirectory = settings.AppDataPath,
                DefaultServerRoot = PlatformPaths.DefaultServerRoot(),
                DefaultBackupRoot = PlatformPaths.DefaultBackupRoot(),
            }, Json.Options);
        });
    }
}
