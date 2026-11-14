using Brutal.Numerics;

namespace Prospect.Client.Settings;

/// <summary>
/// How a client remembers where the player dragged panels: one saved shift per panel name, in unscaled pixels from
/// the place the game gives the panel. The tips and the galaxy map's panels keep theirs in separate lists of
/// <see cref="ClientSettings"/> but through this one set of rules, so a hand-edited file cannot grow either without
/// limit or throw a panel off the screen, and a lookup or a move never allocates after a panel's first move.
/// </summary>
internal static class PanelOffsets
{
    /// <summary>The longest key kept.</summary>
    public const int MaxKeyLength = ClientSettings.MaxTipPositionKeyLength;

    /// <summary>The largest shift kept on either axis, in unscaled pixels.</summary>
    public const float MaxShift = ClientSettings.MaxTipShift;

    /// <summary>The shift saved under <paramref name="key"/>, zero when there is none.</summary>
    public static float2 Get(Dictionary<string, PanelOffset> positions, string key) =>
        positions.TryGetValue(key, out var offset) ? new float2(offset.X, offset.Y) : default;

    /// <summary>
    /// Saves <paramref name="shift"/> under <paramref name="key"/>. True when the saved figure changed; false when
    /// it was already that, or when a new key would pass <paramref name="limit"/> entries.
    /// </summary>
    public static bool Set(Dictionary<string, PanelOffset> positions, string key, float2 shift, int limit)
    {
        if (!positions.TryGetValue(key, out var offset))
        {
            if (positions.Count >= limit)
                return false;
            positions[key] = offset = new PanelOffset();
        }

        if (offset.X == shift.X && offset.Y == shift.Y)
            return false;
        offset.X = shift.X;
        offset.Y = shift.Y;
        return true;
    }

    /// <summary>A list as a file may have made it: only sane keys and finite shifts, at most <paramref name="limit"/>, clamped.</summary>
    public static Dictionary<string, PanelOffset> Clean(Dictionary<string, PanelOffset>? positions, int limit)
    {
        positions ??= [];
        var kept = new Dictionary<string, PanelOffset>(Math.Min(positions.Count, limit));
        foreach (var (key, offset) in positions)
        {
            if (offset is null || string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength || kept.Count >= limit)
                continue;
            if (float.IsFinite(offset.X) && float.IsFinite(offset.Y))
                kept[key] = new PanelOffset { X = Math.Clamp(offset.X, -MaxShift, MaxShift), Y = Math.Clamp(offset.Y, -MaxShift, MaxShift) };
        }

        return kept;
    }
}
