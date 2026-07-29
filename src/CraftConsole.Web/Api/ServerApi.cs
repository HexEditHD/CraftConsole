using System.Text;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

public static class ServerApi
{
    public record CommandRequest(string Command);
    public record StartRequest(Guid? ProfileId);

    public static void MapServerApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/status", (ServerSupervisor sup) => Results.Json(sup.StatusSnapshot(), Json.Options));

        app.MapGet("/api/metrics", (MetricsSampler metrics) =>
            Results.Json(metrics.Latest ?? new { }, Json.Options));

        app.MapGet("/api/console", (ServerSupervisor sup) => Results.Json(sup.ConsoleSnapshot(), Json.Options));

        app.MapDelete("/api/console", (ServerSupervisor sup) =>
        {
            sup.ClearConsole();
            return Results.NoContent();
        });

        app.MapPost("/api/server/start", async (StartRequest? req, ServerSupervisor sup, ProfilesService profiles) =>
        {
            var profile = req?.ProfileId is { } id
                ? await profiles.GetAsync(id)
                : await profiles.GetActiveAsync();

            if (profile is null)
                return Results.BadRequest(new { Message = "No server profile found. Create one in Server → Profiles first." });

            if (req?.ProfileId is { } selected)
                await profiles.SetActiveAsync(selected);

            try
            {
                await sup.StartAsync(profile);
                return Results.Ok(sup.StatusSnapshot());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        });

        app.MapPost("/api/server/stop", async (ServerSupervisor sup) =>
        {
            await sup.StopAsync();
            return Results.Ok(sup.StatusSnapshot());
        });

        app.MapPost("/api/server/restart", async (ServerSupervisor sup) =>
        {
            try
            {
                await sup.RestartAsync();
                return Results.Ok(sup.StatusSnapshot());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        });

        app.MapPost("/api/server/command", async (CommandRequest req, ServerSupervisor sup) =>
        {
            await sup.SendCommandAsync(req.Command);
            return Results.NoContent();
        });

        app.MapPost("/api/server/eula/accept", async (ServerSupervisor sup) =>
        {
            try
            {
                await sup.AcceptEulaAsync();
                return Results.Ok(sup.StatusSnapshot());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { ex.Message });
            }
        });

        // ── Live event stream (SSE) ──────────────────────────────────────
        app.MapGet("/api/events", async (HttpContext ctx, EventBroker broker, ServerSupervisor sup) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var (reader, subscription) = broker.Subscribe();
            using var _ = subscription;

            await ctx.Response.WriteAsync("retry: 3000\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            // Sync the client immediately on (re)connect
            broker.Publish("status", sup.StatusSnapshot());

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
        });
    }

    public record SimulateRequest(string[] Lines);

    /// <summary>Development-only: feed fake console lines through the real pipeline.</summary>
    public static void MapDevApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/dev/simulate", (SimulateRequest req, ServerSupervisor sup) =>
        {
            foreach (var line in req.Lines)
                sup.SimulateOutput(line);
            return Results.NoContent();
        });
    }
}
