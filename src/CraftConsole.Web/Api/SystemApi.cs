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
                // The two Default*Root values above are suggestions and often do
                // not exist yet, so the folder picker cannot open on them without
                // landing wherever the nearest real ancestor happens to be — the
                // drive root on Windows. This one always exists, so it gives the
                // picker somewhere sensible to start and to jump back to.
                // SpecialFolder.UserProfile resolves per-OS: %USERPROFILE% on
                // Windows, $HOME on Linux and macOS.
                HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            }, Json.Options);
        }).RequireRole(Role.Admin);
    }
}
