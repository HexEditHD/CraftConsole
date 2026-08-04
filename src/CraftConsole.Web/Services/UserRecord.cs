namespace CraftConsole.Web.Services;

/// <summary>Ordered so `actual &gt;= required` expresses "at least this privileged".</summary>
public enum Role { Operator, Admin }

public sealed record UserRecord(
    Guid Id,
    string Username,
    string SaltBase64,
    string HashBase64,
    int Iterations,
    Role Role,
    bool Enabled,
    DateTimeOffset CreatedAt);
