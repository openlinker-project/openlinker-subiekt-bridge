namespace Subiekt.Bridge.Infrastructure.Sql;

/// <summary>Caps caller-supplied row limits to a sane range.</summary>
internal static class LimitGuard
{
    /// <summary>
    /// Returns <paramref name="requested"/> clamped to [1, <paramref name="max"/>],
    /// falling back to <paramref name="fallback"/> when the request is non-positive.
    /// </summary>
    public static int Clamp(int requested, int max, int fallback)
    {
        if (max <= 0)
            max = fallback;

        if (requested <= 0)
            requested = fallback;

        return requested > max ? max : requested;
    }
}
