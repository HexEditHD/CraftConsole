using System.Net;
using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

public static class AuthApi
{
    public const string CookieName = "cc_session";

    public record SetupRequest(string Password);
    public record LoginRequest(string Username, string Password);
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    public static void MapAuthApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/status", (AuthService auth) =>
            Results.Json(new { Configured = auth.IsConfigured }, Json.Options));

        app.MapPost("/api/auth/setup", async (SetupRequest req, AuthService auth, HttpContext ctx) =>
        {
            if (auth.IsConfigured)
                return Results.Conflict(new { Message = "A password is already set." });

            // Closes the race where a remote client claims the first password before the
            // owner does, if the panel was bound wider than localhost before setup ran.
            if (!IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? IPAddress.None))
                return Results.Json(
                    new { Message = "Initial setup must be completed from this machine." },
                    statusCode: StatusCodes.Status403Forbidden);

            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
                return Results.BadRequest(new { Message = "Password must be at least 8 characters." });

            await auth.SetupAdminAsync(req.Password);
            var admin = auth.ListUsers()[0];
            IssueSessionCookie(ctx, auth, admin.Id);
            return Results.NoContent();
        });

        app.MapPost("/api/auth/login", (LoginRequest req, AuthService auth, HttpContext ctx) =>
        {
            var ip = ClientIp(ctx);
            if (auth.IsLockedOut(ip))
                return Results.Json(
                    new { Message = "Too many attempts. Try again in a few minutes." },
                    statusCode: StatusCodes.Status429TooManyRequests);

            var user = auth.VerifyCredentials(req.Username ?? "", req.Password ?? "");
            if (user is null)
            {
                auth.RegisterFailure(ip);
                return Results.Json(new { Message = "Incorrect username or password." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            auth.ClearFailures(ip);
            IssueSessionCookie(ctx, auth, user.Id);
            return Results.NoContent();
        });

        app.MapPost("/api/auth/logout", (AuthService auth, HttpContext ctx) =>
        {
            auth.RevokeSession(ctx.Request.Cookies[CookieName]);
            ctx.Response.Cookies.Delete(CookieName);
            return Results.NoContent();
        }).RequireRole(Role.Operator);

        app.MapGet("/api/auth/me", (HttpContext ctx) =>
        {
            var session = ctx.GetSession()!; // gate guarantees a session for a role-annotated endpoint
            return Results.Json(new { session.Username, Role = session.Role.ToString() }, Json.Options);
        }).RequireRole(Role.Operator);

        app.MapPost("/api/auth/change-password", async (ChangePasswordRequest req, AuthService auth, HttpContext ctx) =>
        {
            var session = ctx.GetSession()!;
            if (auth.VerifyCredentials(session.Username, req.CurrentPassword ?? "") is null)
                return Results.BadRequest(new { Message = "Current password is incorrect." });
            if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
                return Results.BadRequest(new { Message = "New password must be at least 8 characters." });

            await auth.SetPasswordAsync(session.UserId, req.NewPassword);
            auth.RevokeAllSessionsForUser(session.UserId); // force re-login everywhere, including this tab, on the fresh cookie below
            IssueSessionCookie(ctx, auth, session.UserId);
            return Results.NoContent();
        }).RequireRole(Role.Operator);
    }

    private static void IssueSessionCookie(HttpContext ctx, AuthService auth, Guid userId)
    {
        var token = auth.CreateSession(userId);
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = ctx.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
            Path = "/",
        });
    }

    private static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
