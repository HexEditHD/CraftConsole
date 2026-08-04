namespace CraftConsole.Web.Services;

/// <summary>Endpoint metadata read by the auth gate in Program.cs.</summary>
public sealed class RequiredRole(Role role)
{
    public Role Role { get; } = role;
}

public static class RequiredRoleExtensions
{
    /// <summary>
    /// Marks an endpoint as requiring at least <paramref name="role"/>. Every
    /// endpoint behind the auth gate should carry one explicitly — see
    /// EveryEndpointDeclaresARequiredRoleTests for the guard that enforces it.
    /// </summary>
    public static TBuilder RequireRole<TBuilder>(this TBuilder builder, Role role)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequiredRole(role));
        return builder;
    }
}
