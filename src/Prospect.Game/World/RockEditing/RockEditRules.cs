using System.Text.Json;

namespace Prospect.Game.World.RockEditing;

/// <summary>Why a scrape was not made.</summary>
public enum RockEditRefusal : byte
{
    /// <summary>The scrape may be made.</summary>
    None,

    /// <summary>The rock is of a kind that cannot be scraped (an ice shard) or is not a rock of its sector.</summary>
    Rock,

    /// <summary>The radius, the length or a position is outside what the rules allow.</summary>
    Shape,

    /// <summary>The rock already holds as many edits or bricks as one rock may.</summary>
    RockFull,

    /// <summary>The world already holds as many edited rocks, edits or bricks as it may.</summary>
    WorldFull,
}

/// <summary>
/// The figures of rock edits, read once from <c>content/rules/rock_edits.json</c>: the size of a brick's cells, what
/// a scrape may be, how much one rock and one world may hold, and the temporary debug scrape's limits. Shared content:
/// the server and every client build their bricks from the same cell size.
/// </summary>
public sealed class RockEditRules
{
    /// <summary>Cells along a brick's edge.</summary>
    public const int BrickCells = 8;

    /// <summary>Samples along a brick's edge: one more than its cells.</summary>
    public const int BrickSamples = BrickCells + 1;

    /// <summary>Samples in a brick.</summary>
    public const int SampleCount = BrickSamples * BrickSamples * BrickSamples;

    /// <summary>Steps of a sample's value in one cell: a sample is a signed byte of this many steps a cell.</summary>
    public const int StepsPerCell = 32;

    /// <summary>The largest step count a sample holds; farther from a cut than this reads as this.</summary>
    public const int MaxSteps = 127;

    /// <summary>Edge of a cell in metres.</summary>
    public float CellMetres { get; }

    /// <summary>Edge of a cell in position units (<see cref="RockScrape.Units"/> a metre); a whole number.</summary>
    public int CellUnits { get; }

    /// <summary>Edge of a brick in position units.</summary>
    public int BrickUnits => CellUnits * BrickCells;

    /// <summary>Edge of a brick in metres.</summary>
    public float BrickMetres => CellMetres * BrickCells;

    /// <summary>How far from a cut a sample still holds a distance, in position units, rounded up.</summary>
    public int ReachUnits { get; }

    /// <summary>Smallest and largest radius of a scrape, in millimetres.</summary>
    public int MinRadiusMm { get; }

    /// <inheritdoc cref="MinRadiusMm" />
    public int MaxRadiusMm { get; }

    /// <summary>Longest stroke of one scrape, in position units.</summary>
    public int MaxSegmentUnits { get; }

    /// <summary>Most edits one rock holds.</summary>
    public int MaxEditsPerRock { get; }

    /// <summary>Most bricks one rock holds.</summary>
    public int MaxBricksPerRock { get; }

    /// <summary>Most rocks of a world that hold edits.</summary>
    public int MaxEditedRocks { get; }

    /// <summary>Most edits a world holds over all its rocks.</summary>
    public int MaxEdits { get; }

    /// <summary>Most bricks a world holds over all its rocks.</summary>
    public int MaxBricks { get; }

    /// <summary>Debug scrape: how far from the flown hull a scrape may land, in metres.</summary>
    public float DebugRangeMetres { get; }

    /// <summary>Debug scrape: least time between two requests of one player, in ticks.</summary>
    public int DebugIntervalTicks { get; }

    /// <summary>Debug scrape: how deep under the surface it finds one pass cuts, in metres.</summary>
    public float DebugBiteMetres { get; }

    /// <summary>Debug scrape: the radius of its cut, in millimetres.</summary>
    public int DebugRadiusMm { get; }

    /// <summary>Largest coordinate a scrape's point may have, in units, so every brick it touches has a 16-bit index.</summary>
    public int CoordinateLimit { get; }

    private RockEditRules(float cell, int minRadius, int maxRadius, int maxSegment, int editsPerRock, int bricksPerRock, int rocks, int edits, int bricks,
        float debugRange, int debugInterval, float debugBite, int debugRadius)
    {
        DebugRadiusMm = debugRadius;
        CellMetres = cell;
        CellUnits = (int)Math.Round(cell * (double)RockScrape.Units);
        ReachUnits = (MaxSteps * CellUnits + StepsPerCell - 1) / StepsPerCell;
        MinRadiusMm = minRadius;
        MaxRadiusMm = maxRadius;
        MaxSegmentUnits = maxSegment;
        MaxEditsPerRock = editsPerRock;
        MaxBricksPerRock = bricksPerRock;
        MaxEditedRocks = rocks;
        MaxEdits = edits;
        MaxBricks = bricks;
        DebugRangeMetres = debugRange;
        DebugIntervalTicks = debugInterval;
        DebugBiteMetres = debugBite;
        CoordinateLimit = Math.Min(RockScrape.MaxCoordinate, 32000 * BrickUnits) - 2 * BrickUnits - ReachUnits - (maxRadius * RockScrape.Units + 999) / 1000;
    }

    /// <summary>Reads and validates the file. Throws <see cref="InvalidDataException"/> for a figure out of range.</summary>
    public static RockEditRules Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var cell = root.GetProperty("cellMetres"u8).GetSingle();
        var cellUnits = cell * (double)RockScrape.Units;
        if (!float.IsFinite(cell) || cellUnits < 4 || cellUnits > 128 || cellUnits != Math.Floor(cellUnits))
            throw new InvalidDataException("rock_edits.json: cellMetres must be a whole number of 1/64 m, from 1/16 m to 2 m.");
        var minRadius = Metres(root, "minRadiusMetres"u8, cell, 60) * 1000.0;
        var maxRadius = Metres(root, "maxRadiusMetres"u8, cell, 60) * 1000.0;
        if (minRadius > maxRadius)
            throw new InvalidDataException("rock_edits.json: minRadiusMetres is above maxRadiusMetres.");
        var segment = Metres(root, "maxSegmentMetres"u8, cell, 100) * RockScrape.Units;
        var editsPerRock = Count(root, "maxEditsPerRock"u8, 1, ushort.MaxValue);
        var bricksPerRock = Count(root, "maxBricksPerRock"u8, 64, 1 << 16);
        var rocks = Count(root, "maxEditedRocks"u8, 1, 4096);
        var edits = Count(root, "maxEdits"u8, editsPerRock, 1 << 20);
        var bricks = Count(root, "maxBricks"u8, bricksPerRock, 1 << 18);
        var debug = root.GetProperty("debug"u8);
        var range = debug.GetProperty("rangeMetres"u8).GetSingle();
        var interval = debug.GetProperty("intervalSeconds"u8).GetSingle();
        var bite = debug.GetProperty("biteMetres"u8).GetSingle();
        var debugRadius = debug.GetProperty("radiusMetres"u8).GetSingle() * 1000.0;
        if (!float.IsFinite(range) || range < 1 || range > 10_000 || !float.IsFinite(interval) || interval < 0.02f || interval > 10
            || !float.IsFinite(bite) || bite <= 0 || bite * 1000.0 > maxRadius * 2 || !(debugRadius >= minRadius && debugRadius <= maxRadius))
            throw new InvalidDataException("rock_edits.json: a debug figure is out of range.");
        return new RockEditRules(cell, (int)Math.Round(minRadius), (int)Math.Round(maxRadius), (int)Math.Round(segment), editsPerRock, bricksPerRock, rocks, edits, bricks,
            range, Math.Max(1, (int)MathF.Ceiling(interval / HullSim.Dt)), bite, (int)Math.Round(debugRadius));
    }

    /// <summary>Whether a rock of this shape can be scraped. The ice shard cannot yet: its eight faces are not facets of the shared mesh.</summary>
    public static bool Scrapable(int shape) => (uint)shape < Galaxy.RockShapes;

    /// <summary>
    /// Whether a scrape is one the rules allow on a rock of the given radius: radius and length within limits, both
    /// points no farther from the centre than the rock reaches plus the cut. Whole-number arithmetic only.
    /// </summary>
    public bool Valid(in RockScrape edit, float rockRadius)
    {
        if (edit.RadiusMm < MinRadiusMm || edit.RadiusMm > MaxRadiusMm) return false;
        long dx = edit.Bx - (long)edit.Ax, dy = edit.By - (long)edit.Ay, dz = edit.Bz - (long)edit.Az;
        if (dx * dx + dy * dy + dz * dz > (long)MaxSegmentUnits * MaxSegmentUnits) return false;
        // The shape's furthest point sits at the nominal radius; two per cent covers the sampling of that maximum.
        var reach = Math.Min(Math.Ceiling(rockRadius * 1.02 * RockScrape.Units) + (edit.RadiusMm * RockScrape.Units + 999) / 1000, CoordinateLimit);
        var limit = (long)reach;
        return Within(edit.Ax, edit.Ay, edit.Az, limit) && Within(edit.Bx, edit.By, edit.Bz, limit);
    }

    private static bool Within(long x, long y, long z, long limit) =>
        Math.Abs(x) <= limit && Math.Abs(y) <= limit && Math.Abs(z) <= limit && x * x + y * y + z * z <= limit * limit;

    private static double Metres(JsonElement root, ReadOnlySpan<byte> name, float least, float most)
    {
        var value = root.GetProperty(name).GetSingle();
        if (!float.IsFinite(value) || value < least || value > most)
            throw new InvalidDataException($"rock_edits.json: {System.Text.Encoding.UTF8.GetString(name)} must be from {least} to {most}.");
        return value;
    }

    private static int Count(JsonElement root, ReadOnlySpan<byte> name, int least, int most)
    {
        var value = root.GetProperty(name).GetInt32();
        if (value < least || value > most)
            throw new InvalidDataException($"rock_edits.json: {System.Text.Encoding.UTF8.GetString(name)} must be from {least} to {most}.");
        return value;
    }
}
