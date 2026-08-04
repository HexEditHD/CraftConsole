namespace CraftConsole.Web.Services;

/// <summary>Reads the identity the auth gate in Program.cs stashed on the request.</summary>
public static class HttpContextAuthExtensions
{
    public const string SessionInfoKey = "cc-session";

    /// <summary>The signed-in user for this request, or null on a public/unauthenticated path.</summary>
    public static SessionInfo? GetSession(this HttpContext ctx)
        => ctx.Items.TryGetValue(SessionInfoKey, out var value) ? value as SessionInfo : null;
}
