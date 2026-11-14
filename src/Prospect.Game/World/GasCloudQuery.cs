namespace Prospect.Game.World;

/// <summary>
/// Read-only questions about the gas a seed generates that the gas module does not answer itself. Which clouds
/// exist is always <see cref="GasCloudModule.TryGetCloud"/>'s word; nothing here repeats a generation rule.
/// </summary>
public static class GasCloudQuery
{
    /// <summary>
    /// A cloud's centre in the galaxy. The one place outside the module that turns a placement into a galactic
    /// position.
    /// </summary>
    public static Double3 CenterOf(Galaxy galaxy, in GasCloudPlacement placement) => galaxy.CenterOf(placement.Anchor) + placement.Cloud.Center;

    /// <summary>
    /// Writes the clouds anchored in every sector at most <paramref name="radius"/> sectors from
    /// <paramref name="center"/> on each axis, nearest shell of sectors first, and returns how many. A cloud of any
    /// size is listed by its anchor, so one that reaches across sector walls is found like any other. When
    /// <paramref name="into"/> is full the farther shells are left out, so the clouds kept are always the nearest.
    /// Pure, allocates nothing. Unlike <see cref="GasCloudModule.Intersecting(Galaxy, Double3, double, Span{GasCloudPlacement})"/>
    /// it is ordered by distance and has no limit on the radius: it is what a far view needs.
    /// </summary>
    public static int Collect(Galaxy galaxy, SectorCoord center, int radius, Span<GasCloudPlacement> into)
    {
        if (galaxy.GasClouds is not { } gas) return 0;
        var count = 0;
        for (var shell = 0; shell <= radius; shell++)
            for (var dx = -shell; dx <= shell; dx++)
                for (var dy = -shell; dy <= shell; dy++)
                {
                    // Off the shell's faces only the two ends of the z run are on the shell.
                    var face = Math.Abs(dx) == shell || Math.Abs(dy) == shell;
                    for (var dz = -shell; dz <= shell; dz += face ? 1 : Math.Max(2 * shell, 1))
                    {
                        if (count == into.Length) return count;
                        var anchor = center.Offset(dx, dy, dz);
                        if (gas.TryGetCloud(galaxy, anchor, out var cloud)) into[count++] = new GasCloudPlacement(anchor, cloud);
                    }
                }
        return count;
    }
}
