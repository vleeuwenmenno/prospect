namespace Prospect.Game.World;

/// <summary>A gas cloud together with the sector that generated it.</summary>
public readonly record struct PlacedGasCloud(SectorCoord Sector, GasCloud Cloud)
{
    /// <summary>
    /// The cloud's centre in the galaxy. This is the one place that turns a descriptor's centre into a galactic
    /// position, so a change to how a cloud is placed in its sector is made here and nowhere else.
    /// </summary>
    public Double3 CenterIn(Galaxy galaxy) => galaxy.CenterOf(Sector) + Cloud.Center;
}

/// <summary>
/// Read-only questions about the gas a seed generates, answered without generating anything else in a sector.
/// It only calls the gas module's own two steps, so it always agrees with <see cref="Galaxy.Generate"/>.
/// </summary>
public static class GasCloudQuery
{
    /// <summary>
    /// Writes the clouds of every sector at most <paramref name="radius"/> sectors from <paramref name="center"/>
    /// on each axis, nearest shell of sectors first, and returns how many. When <paramref name="into"/> is full the
    /// farther shells are left out, so the clouds kept are always the nearest. Takes no memory:
    /// <paramref name="scratch"/> is the caller's, and only its summary and gas descriptor are written.
    /// </summary>
    public static int Collect(Galaxy galaxy, SectorCoord center, int radius, SectorContent scratch, Span<PlacedGasCloud> into)
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
                        var coord = new SectorCoord(center.X + dx, center.Y + dy, center.Z + dz);
                        if (TryGet(galaxy, gas, coord, scratch, out var cloud)) into[count++] = new PlacedGasCloud(coord, cloud);
                    }
                }
        return count;
    }

    /// <summary>The cloud a sector generates, if it has one.</summary>
    public static bool TryGet(Galaxy galaxy, SectorCoord coord, SectorContent scratch, out GasCloud cloud)
    {
        cloud = default;
        return galaxy.GasClouds is { } gas && TryGet(galaxy, gas, coord, scratch, out cloud);
    }

    private static bool TryGet(Galaxy galaxy, GasCloudModule gas, SectorCoord coord, SectorContent scratch, out GasCloud cloud)
    {
        // The module is handed the same sector and the same matter density as in a full generation. Only its own
        // summary figures are filled in first: a module draws from its own streams and reads no other module's.
        var sector = new SectorContext(galaxy, coord, galaxy.Shape.Density(coord.X, coord.Z) * galaxy.Shape.LayerFalloff(coord.Y));
        var summary = new SectorSummary { Coord = coord, Density = sector.Density };
        gas.Summarize(sector, ref summary);
        scratch.Summary = summary;
        scratch.GasCloud = default;
        if (summary.GasCloudCount != 0) gas.Generate(sector, scratch);
        cloud = scratch.GasCloud;
        return summary.GasCloudCount != 0;
    }
}
