using Brutal.Numerics;

namespace Prospect.Client.World;

/// <summary>What a box drag does to the blocks already selected.</summary>
public enum BoxSelectMode : byte
{
    /// <summary>The blocks inside become the whole selection.</summary>
    Replace,

    /// <summary>The blocks inside are added to the selection.</summary>
    Add,

    /// <summary>The blocks inside are taken out of the selection.</summary>
    Remove,
}

/// <summary>
/// The screen rectangle of the editor's box select, without any drawing or state: which corner is which, which
/// modifier means what and whether a projected point is inside.
/// </summary>
public static class BoxSelect
{
    /// <summary>The mode the held modifiers mean: Alt removes, otherwise Shift adds, otherwise the drag replaces.</summary>
    public static BoxSelectMode ModeOf(bool shift, bool alt) => alt ? BoxSelectMode.Remove : shift ? BoxSelectMode.Add : BoxSelectMode.Replace;

    /// <summary>The rectangle between two corners given in any order.</summary>
    public static void Normalize(float2 a, float2 b, out float2 min, out float2 max)
    {
        min = new float2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        max = new float2(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    /// <summary>Whether a point lies inside the rectangle, its edges included.</summary>
    public static bool Contains(float2 min, float2 max, float2 point) => point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;

    /// <summary>Whether a press and a release are far enough apart to be a drag rather than a click.</summary>
    public static bool IsDrag(float2 start, float2 end, float slop) => MathF.Abs(end.X - start.X) + MathF.Abs(end.Y - start.Y) > slop;
}
