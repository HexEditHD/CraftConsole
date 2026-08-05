using CraftConsole.Core.Java;
using CraftConsole.Core.Models;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>Profiles, Java detection/downloads, server JAR downloads, and app settings.</summary>
public static class SetupApi
{
    public record ServerDownloadRequest(ServerType Type, string? Version, string Directory);
    public record JavaDownloadRequest(int Major);
    public record SetRconPasswordRequest(string Password);
    public record SettingsDto(
        bool ShowTimestamp, bool ShowDate, bool AutoScrollConsole, int MaxConsoleLines,
        string ColorInfo, string ColorWarn, string ColorError, string ColorPlayer);
    public record BrowseEntryDto(string Name, string Path);

    public static void MapSetupApi(this IEndpointRouteBuilder app)
    {
        // ── Profiles ──────────────────────────────────────────────────────
        app.MapGet("/api/profiles", async (ProfilesService profiles, RconSecretStore secrets, SettingsHolder settings) =>
        {
            var list = await profiles.ListAsync();
            var withFlags = new List<object>(list.Count);
            foreach (var p in list)
            {
                // ServerProfile itself never carries the password (see RconSecretStore) —
                // this flag is computed per-request so the UI can say "set" vs "unset"
                // without the value ever existing anywhere the client can read it.
                var hasPassword = p.Mode == ConnectionMode.Rcon && await secrets.HasAsync(p.Id);
                withFlags.Add(new
                {
                    p.Id, p.Name, p.Mode,
                    p.JarPath, p.WorkingDirectory, p.JavaPath, p.MinRamMb, p.MaxRamMb,
                    p.MinecraftVersion, p.JvmArguments, p.Type,
                    p.RconHost, p.RconPort,
                    HasRconPassword = hasPassword,
                });
            }
            return Results.Json(new { Profiles = withFlags, ActiveProfileId = settings.Current.ActiveProfileId }, Json.Options);
        }).RequireRole(Role.Admin);

        app.MapPost("/api/profiles", async (ServerProfile profile, ProfilesService profiles) =>
        {
            try { return Results.Json(await profiles.AddAsync(profile), Json.Options); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { ex.Message }); }
        }).RequireRole(Role.Admin);

        app.MapPut("/api/profiles/{id:guid}", async (Guid id, ServerProfile profile, ProfilesService profiles) =>
        {
            try { return await profiles.UpdateAsync(id, profile) ? Results.NoContent() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { ex.Message }); }
        }).RequireRole(Role.Admin);

        app.MapDelete("/api/profiles/{id:guid}", async (Guid id, ProfilesService profiles) =>
            await profiles.DeleteAsync(id) ? Results.NoContent() : Results.NotFound())
            .RequireRole(Role.Admin);

        app.MapPost("/api/profiles/{id:guid}/activate", async (Guid id, ProfilesService profiles) =>
        {
            if (await profiles.GetAsync(id) is null) return Results.NotFound();
            await profiles.SetActiveAsync(id);
            return Results.NoContent();
        }).RequireRole(Role.Admin);

        // Write-only: sets or replaces the password, never returns it. GET
        // /api/profiles exposes only whether one is set (HasRconPassword above).
        app.MapPut("/api/profiles/{id:guid}/rcon-password",
            async (Guid id, SetRconPasswordRequest req, ProfilesService profiles, RconSecretStore secrets) =>
        {
            if (await profiles.GetAsync(id) is not { } profile) return Results.NotFound();
            if (profile.Mode != ConnectionMode.Rcon)
                return Results.BadRequest(new { Message = "Only RCON profiles have a password." });
            if (string.IsNullOrEmpty(req.Password))
                return Results.BadRequest(new { Message = "A password is required." });

            await secrets.SetAsync(id, req.Password);
            return Results.NoContent();
        }).RequireRole(Role.Admin);

        // ── Java ──────────────────────────────────────────────────────────
        app.MapGet("/api/setup/java/detect", async (CancellationToken ct) =>
        {
            var found = await JavaInstallationDetector.DetectAsync(ct);
            return Results.Json(found
                .OrderByDescending(j => j.MajorVersion)
                .Select(j => new { j.ExecutablePath, j.DisplayVersion, j.MajorVersion, j.Label }),
                Json.Options);
        }).RequireRole(Role.Admin);

        app.MapGet("/api/setup/java/versions", async (SetupService setup, CancellationToken ct) =>
            Results.Json(new { Versions = await setup.FetchJavaVersionsAsync(ct) }, Json.Options))
            .RequireRole(Role.Admin);

        app.MapPost("/api/setup/java/download", (JavaDownloadRequest req, SetupService setup) =>
            setup.StartJavaDownload(req.Major)
                ? Results.Accepted()
                : Results.Conflict(new { Message = "A Java download is already in progress." }))
            .RequireRole(Role.Admin);

        // ── Server JARs ───────────────────────────────────────────────────
        app.MapGet("/api/setup/server/types", () =>
            Results.Json(SetupService.AllServerTypes, Json.Options)).RequireRole(Role.Admin);

        app.MapGet("/api/setup/server/versions", async (ServerType type, SetupService setup, ILogger<SetupService> log, CancellationToken ct) =>
        {
            try { return Results.Json(await setup.FetchServerVersionsAsync(type, ct), Json.Options); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not fetch {Type} versions", type);
                return Results.Json(new { Message = $"Could not fetch versions from the {type} API: {ex.Message}" },
                    Json.Options, statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireRole(Role.Admin);

        app.MapPost("/api/setup/server/download", (ServerDownloadRequest req, SetupService setup) =>
        {
            if (string.IsNullOrWhiteSpace(req.Directory))
                return Results.BadRequest(new { Message = "A destination directory is required." });

            return setup.StartServerDownload(req.Type, req.Version, req.Directory)
                ? Results.Accepted()
                : Results.Conflict(new { Message = "A server download is already in progress." });
        }).RequireRole(Role.Admin);

        app.MapPost("/api/setup/cancel/{kind}", (string kind, SetupService setup) =>
        {
            setup.Cancel(kind);
            return Results.NoContent();
        }).RequireRole(Role.Admin);

        // ── Folder picker ─────────────────────────────────────────────────
        // Backs the destination-folder picker on server/backup setup forms.
        // Not jailed to a profile directory — Admins can already point those
        // forms at any path on disk by typing it, so this is a UX convenience
        // over an already-trusted capability, not a new one. Read-only:
        // it only lists directory names, never file contents.
        app.MapGet("/api/setup/browse", (string? path) =>
        {
            IResult DriveList()
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => { try { return d.IsReady; } catch { return false; } })
                    .Select(d => new BrowseEntryDto(d.Name.TrimEnd('\\'), d.Name))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return Results.Json(new { Path = "", Parent = (string?)null, Directories = drives }, Json.Options);
            }

            // No path means: show the drive list on Windows, or "/" on Unix
            // (which has no separate drive-list concept).
            if (string.IsNullOrWhiteSpace(path))
            {
                if (OperatingSystem.IsWindows()) return DriveList();
                path = "/";
            }

            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Results.BadRequest(new { Message = "Invalid path." });
            }

            // The requested path may not exist yet — it can be a suggested
            // default that was never created, or a destination the user is
            // about to create. Walk up to the nearest real ancestor instead
            // of dead-ending; fall back to the drive list / "/" if nothing
            // on that branch exists at all.
            while (!Directory.Exists(fullPath))
            {
                var up = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(up) || up == fullPath)
                    return OperatingSystem.IsWindows() ? DriveList() : Results.BadRequest(new { Message = "That folder doesn't exist." });
                fullPath = up;
            }

            List<BrowseEntryDto> directories;
            try
            {
                directories = Directory.GetDirectories(fullPath)
                    .Select(d => new BrowseEntryDto(Path.GetFileName(d), d))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return Results.Json(new { Message = "Access denied to this folder." }, Json.Options, statusCode: StatusCodes.Status403Forbidden);
            }

            var atRoot = string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
            // At a drive root on Windows, "up" goes to the drive list (empty
            // path); at "/" on Linux there's nowhere further up to go.
            string? parent = atRoot
                ? (OperatingSystem.IsWindows() ? "" : null)
                : Path.GetDirectoryName(fullPath);

            return Results.Json(new { Path = fullPath, Parent = parent, Directories = directories }, Json.Options);
        }).RequireRole(Role.Admin);

        // ── Settings ──────────────────────────────────────────────────────
        app.MapGet("/api/settings", (SettingsHolder settings) =>
            Results.Json(settings.Current, Json.Options)).RequireRole(Role.Admin);

        app.MapPut("/api/settings", async (SettingsDto dto, SettingsHolder settings) =>
        {
            await settings.UpdateAsync(s =>
            {
                s.ShowTimestamp = dto.ShowTimestamp;
                s.ShowDate = dto.ShowDate;
                s.AutoScrollConsole = dto.AutoScrollConsole;
                s.MaxConsoleLines = Math.Clamp(dto.MaxConsoleLines, 100, 20000);
                s.ColorInfo = dto.ColorInfo;
                s.ColorWarn = dto.ColorWarn;
                s.ColorError = dto.ColorError;
                s.ColorPlayer = dto.ColorPlayer;
            });
            return Results.Json(settings.Current, Json.Options);
        }).RequireRole(Role.Admin);
    }
}
