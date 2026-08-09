using System.Reflection;
using System.Text;
using CraftConsole.Core.Models;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

public static class ServerApi
{
    public record CommandRequest(string Command);
    public record StartRequest(Guid? ProfileId);

    /// <summary>
    /// Panel version, from the assembly's informational version (set by the
    /// release workflow from the git tag). Falls back to "dev" for local builds.
    /// </summary>
    public static string PanelVersion { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var informational = typeof(ServerApi).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return "dev";

        // The SDK appends "+<commit sha>" when SourceLink is active.
        var plus = informational.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;

        return version is "1.0.0" ? "dev" : version;
    }

    public static void MapServerApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/version", () => Results.Json(new
        {
            Version = PanelVersion,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        }, Json.Options)).RequireRole(Role.Operator);

        // ── The switcher's list — every profile, with live status merged in ──
        app.MapGet("/api/servers", async (ProfilesService profiles, ServerRegistry registry, SettingsHolder settings) =>
        {
            var list = await profiles.ListAsync();

            // Two Managed profiles sharing a server-port will have the second
            // fail to bind — which presents as a confusing crash rather than an
            // obvious config mistake. Surfacing the collision here lets the
            // switcher warn before it happens rather than after.
            var ports = list
                .Where(p => p.Mode == ConnectionMode.Managed)
                .Select(p => (Profile: p, Port: ReadServerPort(p.WorkingDirectory)))
                .Where(t => t.Port is not null)
                .ToList();
            var conflicting = ports
                .GroupBy(t => t.Port)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            var result = list.Select(p =>
            {
                var sup = registry.TryGet(p.Id);
                var port = ports.FirstOrDefault(t => t.Profile.Id == p.Id).Port;
                return new
                {
                    p.Id,
                    p.Name,
                    p.Mode,
                    Status = sup?.Status ?? ServerStatus.Stopped,
                    PlayerCount = sup?.PlayersSnapshot().Count ?? 0,
                    PortConflict = port is not null && conflicting.Contains(port),
                };
            }).ToList();

            // "Active" (ProfilesService's notion — a UI preference now, see
            // ServerScope) is a distinct thing from ActiveProfile on a
            // supervisor (only set once StartAsync has actually run). The
            // switcher's own idea of "current" has to be driven by this one:
            // StatusSnapshot().Profile is null for any profile never started,
            // which is the ordinary case for most entries in this list.
            return Results.Json(new { Servers = result, ActiveProfileId = settings.Current.ActiveProfileId }, Json.Options);
        }).RequireRole(Role.Operator);

        // ── Status ────────────────────────────────────────────────────────
        app.MapGet("/api/servers/{id:guid}/status", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(sup.StatusSnapshot(), Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Operator);

        app.MapGet("/api/status", async (ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return Results.Json(sup?.StatusSnapshot() ?? EmptyStatus, Json.Options);
        }).RequireRole(Role.Operator);

        // ── Metrics ───────────────────────────────────────────────────────
        app.MapGet("/api/servers/{id:guid}/metrics", async (Guid id, ProfilesService profiles, ServerRegistry registry, MetricsSampler metrics) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json((object?)metrics.LatestFor(sup.ServerId) ?? new { }, Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Operator);

        // Reconstructs the pre-multi-server flat shape — machine and server
        // gauges together in one object — for whichever profile is active.
        app.MapGet("/api/metrics", async (MetricsSampler metrics, ProfilesService profiles, ServerRegistry registry) =>
        {
            var machine = metrics.LatestMachine;
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            var server = sup is not null ? metrics.LatestFor(sup.ServerId) : null;

            return Results.Json(new
            {
                MachineCpuPercent = machine?.CpuPercent,
                MachineRamUsedGb = machine?.RamUsedGb,
                MachineRamTotalGb = machine?.RamTotalGb,
                MachineRamPercent = machine?.RamPercent,
                ServerCpuPercent = server?.ServerCpuPercent,
                ServerRamMb = server?.ServerRamMb,
                ServerRamMaxMb = server?.ServerRamMaxMb,
                UptimeSeconds = server?.UptimeSeconds,
                Status = server?.Status ?? ServerStatus.Stopped,
                PlayerCount = server?.PlayerCount ?? 0,
            }, Json.Options);
        }).RequireRole(Role.Operator);

        // ── Console ───────────────────────────────────────────────────────
        app.MapGet("/api/servers/{id:guid}/console", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
            await ServerScope.ResolveAsync(id, profiles, registry) is { } sup
                ? Results.Json(sup.ConsoleSnapshot(), Json.Options)
                : Results.NotFound())
            .RequireRole(Role.Operator);

        app.MapGet("/api/console", async (ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            return Results.Json(sup?.ConsoleSnapshot() ?? [], Json.Options);
        }).RequireRole(Role.Operator);

        app.MapDelete("/api/servers/{id:guid}/console", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
        {
            if (await ServerScope.ResolveAsync(id, profiles, registry) is not { } sup) return Results.NotFound();
            sup.ClearConsole();
            return Results.NoContent();
        }).RequireRole(Role.Operator);

        app.MapDelete("/api/console", async (ProfilesService profiles, ServerRegistry registry) =>
        {
            (await ServerScope.ResolveActiveAsync(profiles, registry))?.ClearConsole();
            return Results.NoContent();
        }).RequireRole(Role.Operator);

        // ── Lifecycle ─────────────────────────────────────────────────────
        app.MapPost("/api/servers/{id:guid}/start", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
        {
            if (await profiles.GetAsync(id) is not { } profile) return Results.NotFound();

            var sup = registry.GetOrCreate(id);
            try
            {
                await sup.StartAsync(profile);
                return Results.Ok(sup.StatusSnapshot());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        }).RequireRole(Role.Operator);

        // ProfileId in the body picks which profile; omitted falls back to
        // whichever was already active — the exact behaviour this route has
        // always had.
        app.MapPost("/api/server/start", async (StartRequest? req, ProfilesService profiles, ServerRegistry registry) =>
        {
            var profile = req?.ProfileId is { } id
                ? await profiles.GetAsync(id)
                : await profiles.GetActiveAsync();

            if (profile is null)
                return Results.BadRequest(new { Message = "No server profile found. Create one in Server → Profiles first." });

            if (req?.ProfileId is { } selected)
                await profiles.SetActiveAsync(selected);

            var sup = registry.GetOrCreate(profile.Id);
            try
            {
                await sup.StartAsync(profile);
                return Results.Ok(sup.StatusSnapshot());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        }).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/stop", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
        {
            if (await ServerScope.ResolveAsync(id, profiles, registry) is not { } sup) return Results.NotFound();
            await sup.StopAsync();
            return Results.Ok(sup.StatusSnapshot());
        }).RequireRole(Role.Operator);

        app.MapPost("/api/server/stop", async (ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            if (sup is null) return Results.Ok(EmptyStatus);
            await sup.StopAsync();
            return Results.Ok(sup.StatusSnapshot());
        }).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/restart", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
        {
            if (await ServerScope.ResolveAsync(id, profiles, registry) is not { } sup) return Results.NotFound();
            try
            {
                await sup.RestartAsync();
                return Results.Ok(sup.StatusSnapshot());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        }).RequireRole(Role.Operator);

        app.MapPost("/api/server/restart", async (ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            if (sup is null) return Results.Ok(EmptyStatus);
            try
            {
                await sup.RestartAsync();
                return Results.Ok(sup.StatusSnapshot());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        }).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/command", async (Guid id, CommandRequest req, ProfilesService profiles, ServerRegistry registry) =>
        {
            if (await ServerScope.ResolveAsync(id, profiles, registry) is not { } sup) return Results.NotFound();
            await sup.SendCommandAsync(req.Command);
            return Results.NoContent();
        }).RequireRole(Role.Operator);

        app.MapPost("/api/server/command", async (CommandRequest req, ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            if (sup is not null) await sup.SendCommandAsync(req.Command);
            return Results.NoContent();
        }).RequireRole(Role.Operator);

        app.MapPost("/api/servers/{id:guid}/eula/accept", async (Guid id, ProfilesService profiles, ServerRegistry registry) =>
        {
            if (await ServerScope.ResolveAsync(id, profiles, registry) is not { } sup) return Results.NotFound();
            try
            {
                await sup.AcceptEulaAsync();
                return Results.Ok(sup.StatusSnapshot());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        }).RequireRole(Role.Operator);

        app.MapPost("/api/server/eula/accept", async (ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            if (sup is null) return Results.BadRequest(new { Message = "No server has been started yet." });
            try
            {
                await sup.AcceptEulaAsync();
                return Results.Ok(sup.StatusSnapshot());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        }).RequireRole(Role.Operator);

        // ── Live event stream (SSE) ──────────────────────────────────────
        // One connection, every server multiplexed onto it: each payload's own
        // "serverId" field (added by EventBroker's scoped Publish overload)
        // says which server it is about, or is absent for a genuinely global
        // event. The client filters; there is no per-server SSE endpoint.
        app.MapGet("/api/events", async (HttpContext ctx, EventBroker broker, ProfilesService profiles, ServerRegistry registry) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var (reader, subscription) = broker.Subscribe();
            using var _ = subscription;

            await ctx.Response.WriteAsync("retry: 3000\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            // Sync every known server's status immediately on (re)connect —
            // not just the active one, so the switcher's dots are correct as
            // soon as the client connects rather than waiting on the next
            // change for servers it isn't currently viewing.
            foreach (var sup in registry.All())
                broker.Publish("status", sup.ServerId, sup.StatusSnapshot());

            try
            {
                await foreach (var payload in reader.ReadAllAsync(ctx.RequestAborted))
                {
                    var sb = new StringBuilder()
                        .Append("event: ").Append(payload.Event).Append('\n')
                        .Append("data: ").Append(payload.Json).Append("\n\n");
                    await ctx.Response.WriteAsync(sb.ToString(), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
        }).RequireRole(Role.Operator);
    }

    /// <summary>
    /// What StatusSnapshot() looks like for a server that has never been
    /// started and has no supervisor yet — used by the legacy routes when
    /// there is no active profile at all (no profiles exist, or the active
    /// one was deleted), the one case ServerScope.ResolveActiveAsync can't
    /// hand back a real supervisor for. Matches ServerSupervisor.StatusSnapshot()
    /// field-for-field for a freshly constructed instance.
    /// </summary>
    private static readonly object EmptyStatus = new
    {
        Status = ServerStatus.Stopped,
        Version = "",
        StartedAt = (DateTimeOffset?)null,
        UptimeSeconds = (long?)null,
        PlayerCount = 0,
        MaxPlayers = 20,
        EulaRequired = false,
        Profile = (ServerProfile?)null,
        Capabilities = Core.Servers.ServerCapabilities.Managed,
    };

    /// <summary>Reads server-port from a Managed profile's server.properties, for cross-profile conflict detection.</summary>
    private static int? ReadServerPort(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return null;
        try
        {
            var path = Path.Combine(workingDirectory, "server.properties");
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("server-port=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["server-port=".Length..].Trim(), out var port))
                    return port;
            }
        }
        catch { /* unreadable — skip conflict detection for this profile */ }
        return null;
    }

    public record SimulateRequest(string[] Lines);

    /// <summary>Development-only: feed fake console lines through the real pipeline of whichever server is active.</summary>
    public static void MapDevApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/dev/simulate", async (SimulateRequest req, ProfilesService profiles, ServerRegistry registry) =>
        {
            var sup = await ServerScope.ResolveActiveAsync(profiles, registry);
            if (sup is null) return Results.BadRequest(new { Message = ServerScope.NoServerStarted });
            foreach (var line in req.Lines)
                sup.SimulateOutput(line);
            return Results.NoContent();
        }).RequireRole(Role.Admin);
    }
}
