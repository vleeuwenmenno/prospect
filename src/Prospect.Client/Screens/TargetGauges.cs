using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>What a shield bar says about its shield, from the figures the snapshot gives for the hull.</summary>
internal enum ShieldReading
{
    /// <summary>No shield capacity: no emitter, or nothing known.</summary>
    None,

    /// <summary>Up, with its charge.</summary>
    Online,

    /// <summary>Broken, and the hull's rules give no restart point to show.</summary>
    Offline,

    /// <summary>Broken and charging back towards its restart point.</summary>
    Restarting,
}

/// <summary>
/// The target panel's shield and hull bars apart from drawing, so they can be tested without fonts: which figures
/// a bar reads, what it says, and where its icon, segments and reading sit. The state and colours are the pilot
/// console's (<see cref="FlightConsole"/>); only the size differs.
/// </summary>
internal static class TargetGauges
{
    /// <summary>
    /// The figures a target's bars read against: the shield capacity it has now, from the blocks it has left, and
    /// the hull strength of its blueprint, as the console reads the own ship's. A hull the snapshot gave no data
    /// sheet for reads against its design's shield.
    /// </summary>
    public static FlightParams Sheet(in HullView target, HullDesign design)
    {
        var flight = design.Flight;
        if (target.Stats.Blocks > 0)
            flight.ShieldCapacity = target.Stats.ShieldCapacity;
        return flight;
    }

    /// <summary>What the shield bar says: nothing without capacity, otherwise up, offline, or restarting.</summary>
    public static ShieldReading Shield(in FlightParams flight, bool offline)
    {
        if (!(flight.ShieldCapacity > 0f) || !float.IsFinite(flight.ShieldCapacity))
            return ShieldReading.None;
        if (!offline)
            return ShieldReading.Online;
        return FlightConsole.RestartMark(flight) > 0f ? ShieldReading.Restarting : ShieldReading.Offline;
    }

    /// <summary>Where one row's parts sit, in screen pixels.</summary>
    /// <param name="Icon">The icon's left edge and top.</param>
    /// <param name="Track">The segments' rectangle.</param>
    /// <param name="ReadingRight">The right edge of the reading, which is right-aligned.</param>
    /// <param name="Height">The row's height, for stepping to the next.</param>
    internal readonly record struct Row(float IconX, float IconY, ConsoleTrack Track, float ReadingRight, float Height);

    /// <summary>
    /// A row whose top-left is (<paramref name="left"/>, <paramref name="top"/>) and which is <paramref name="width"/>
    /// wide, with a reading column <paramref name="readingWidth"/> wide at its right. The bar takes what is left
    /// between icon and reading, and is never negative.
    /// </summary>
    public static Row Place(TargetLook look, float scale, float left, float top, float width, float readingWidth)
    {
        float Px(float design) => MathF.Round(design * scale);

        var height = Px(look.RowHeight);
        var icon = Px(look.IconSize);
        var barHeight = Px(look.BarHeight);
        var barLeft = left + icon + Px(look.IconGap);
        var readingRight = left + width;
        var barRight = Math.Max(barLeft, readingRight - readingWidth - Px(look.ReadingGap));
        var barTop = top + MathF.Floor((height - barHeight) * 0.5f);
        return new Row(left, top + MathF.Floor((height - icon) * 0.5f), new ConsoleTrack(barLeft, barRight, barTop, barTop + barHeight, barLeft), readingRight, height);
    }

    /// <summary>The top of the row after one that starts at <paramref name="top"/>.</summary>
    public static float Next(TargetLook look, float scale, float top) => top + MathF.Round(look.RowHeight * scale) + MathF.Round(look.RowGap * scale);
}
