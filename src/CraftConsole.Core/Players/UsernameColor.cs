namespace CraftConsole.Core.Players;

/// <summary>Deterministic username → color mapping shared by every UI surface.</summary>
public static class UsernameColor
{
    public static readonly string[] Palette =
    [
        "#06B6D4", "#8B5CF6", "#10B981", "#F59E0B",
        "#3B82F6", "#EC4899", "#14B8A6", "#A855F7",
    ];

    public static string GetHex(string name)
        => Palette[(int)(GetStableHash(name) % (uint)Palette.Length)];

    // string.GetHashCode() is randomized per-process (hash randomization), so it
    // can't be used for a color that should stay stable across app restarts.
    // FNV-1a over the invariant-uppercase name gives a deterministic, unsigned
    // hash (no Math.Abs overflow on int.MinValue) that's also case-insensitive.
    public static uint GetStableHash(string name)
    {
        const uint fnvOffsetBasis = 2166136261;
        const uint fnvPrime = 16777619;

        var hash = fnvOffsetBasis;
        foreach (var c in name.ToUpperInvariant())
        {
            hash ^= c;
            hash *= fnvPrime;
        }

        return hash;
    }
}
