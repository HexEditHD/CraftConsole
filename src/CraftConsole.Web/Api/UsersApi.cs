using CraftConsole.Web.Services;

namespace CraftConsole.Web.Api;

/// <summary>User management — Admin only. See AuthService for the last-admin protections.</summary>
public static class UsersApi
{
    public record CreateUserRequest(string Username, string Password, Role Role);
    public record SetEnabledRequest(bool Enabled);
    public record SetRoleRequest(Role Role);
    public record SetPasswordRequest(string NewPassword);

    public static void MapUsersApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/users", (AuthService auth) =>
            Results.Json(new { Users = auth.ListUsers().Select(Redact) }, Json.Options))
            .RequireRole(Role.Admin);

        app.MapPost("/api/users", async (CreateUserRequest req, AuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username))
                return Results.BadRequest(new { Message = "A username is required." });
            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
                return Results.BadRequest(new { Message = "Password must be at least 8 characters." });

            var (result, user) = await auth.CreateUserAsync(req.Username.Trim(), req.Password, req.Role);
            return result switch
            {
                UserMutationResult.UsernameTaken =>
                    Results.Conflict(new { Message = $"“{req.Username}” is already taken." }),
                UserMutationResult.Success => Results.Json(Redact(user!), Json.Options),
                _ => Results.Problem("Unexpected error creating the user."),
            };
        }).RequireRole(Role.Admin);

        app.MapPut("/api/users/{id:guid}/enabled", async (Guid id, SetEnabledRequest req, AuthService auth) =>
            ToResult(await auth.SetEnabledAsync(id, req.Enabled), "disable the last remaining admin"))
            .RequireRole(Role.Admin);

        app.MapPut("/api/users/{id:guid}/role", async (Guid id, SetRoleRequest req, AuthService auth) =>
            ToResult(await auth.SetRoleAsync(id, req.Role), "demote the last remaining admin"))
            .RequireRole(Role.Admin);

        app.MapPut("/api/users/{id:guid}/password", async (Guid id, SetPasswordRequest req, AuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
                return Results.BadRequest(new { Message = "Password must be at least 8 characters." });

            return ToResult(await auth.SetPasswordAsync(id, req.NewPassword), "reset that password");
        }).RequireRole(Role.Admin);

        app.MapDelete("/api/users/{id:guid}", async (Guid id, AuthService auth) =>
            ToResult(await auth.DeleteUserAsync(id), "delete the last remaining admin"))
            .RequireRole(Role.Admin);
    }

    private static IResult ToResult(UserMutationResult result, string blockedAction) => result switch
    {
        UserMutationResult.Success => Results.NoContent(),
        UserMutationResult.NotFound => Results.NotFound(),
        UserMutationResult.LastAdminProtected =>
            Results.BadRequest(new { Message = $"Can't {blockedAction} — at least one enabled admin is required." }),
        _ => Results.Problem("Unexpected error."),
    };

    private static object Redact(UserRecord u) => new { u.Id, u.Username, u.Role, u.Enabled, u.CreatedAt };
}
