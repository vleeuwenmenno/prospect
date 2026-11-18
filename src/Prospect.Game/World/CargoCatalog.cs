using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>A container's immutable size, in cargo-grid units, and empty shell strength.</summary>
public readonly record struct ContainerSize(string Name, int Units, GridSize Shape, float Hull);

/// <summary>
/// How a tractor guides a held container home: staging point, final glide, settling and the time-out. Read once from
/// the <c>tractor</c> entry of <c>blocks/cargo.json</c>; every figure is validated there.
/// </summary>
/// <param name="StagingMarginMetres">Gap between the container's near face and the rack's open face at the staging point.</param>
/// <param name="ToleranceMetres">How close counts as arrived, at the staging point and at the slot.</param>
/// <param name="FinalSpeed">Speed cap, in metres per second, on the final leg into the slot.</param>
/// <param name="SettleSpeed">Relative speed under which a container inside its slot cell is stowed.</param>
/// <param name="AlignRate">Per second, how fast the container turns onto the hull's axes.</param>
/// <param name="ProgressMetres">The path left must shrink by this much to count as progress.</param>
/// <param name="StallSeconds">Without progress or without a clear line this long, the claim is released.</param>
/// <param name="RetrySeconds">After such a release no tractor may acquire the container for this long.</param>
/// <param name="ApproachBrakeShare">The share of the braking a body has in hand as it closes on its goal (<see cref="Approach"/>).</param>
/// <param name="CarryAcceleration">
/// Metres per second squared: how hard the holding ship may manoeuvre and still take what its beams hold along
/// (<see cref="Approach.Carry"/>). Beyond it the load lags and the pull has to bring it back.
/// </param>
public readonly record struct TractorGuidance(float StagingMarginMetres, float ToleranceMetres, float FinalSpeed,
    float SettleSpeed, float AlignRate, float ProgressMetres, float StallSeconds, float RetrySeconds, float ApproachBrakeShare,
    float CarryAcceleration)
{
    /// <summary>Ticks of <see cref="StallSeconds"/> at the fixed simulation interval.</summary>
    public int StallTicks => Math.Max(1, (int)MathF.Ceiling(StallSeconds / Prospect.Game.World.HullSim.Dt));

    /// <summary>Reads and validates the <c>tractor</c> entry.</summary>
    internal static TractorGuidance Read(System.Text.Json.JsonElement e)
    {
        var g = new TractorGuidance(
            e.GetProperty("stagingMarginMetres"u8).GetSingle(), e.GetProperty("toleranceMetres"u8).GetSingle(),
            e.GetProperty("finalSpeed"u8).GetSingle(), e.GetProperty("settleSpeed"u8).GetSingle(),
            e.GetProperty("alignRate"u8).GetSingle(), e.GetProperty("progressMetres"u8).GetSingle(),
            e.GetProperty("stallSeconds"u8).GetSingle(), e.GetProperty("retrySeconds"u8).GetSingle(),
            e.GetProperty("approachBrakeShare"u8).GetSingle(), e.GetProperty("carryAcceleration"u8).GetSingle());
        if (!In(g.StagingMarginMetres, 0.01f, 2) || !In(g.ToleranceMetres, 0.005f, 0.5f) || !In(g.FinalSpeed, 0.05f, 10)
            || !In(g.SettleSpeed, 0.01f, 5) || !In(g.AlignRate, 0.1f, 60) || !In(g.ProgressMetres, 0.001f, 1)
            || !In(g.StallSeconds, 0.5f, 120) || !In(g.RetrySeconds, 0, 3600) || g.SettleSpeed > g.FinalSpeed
            || !In(g.ApproachBrakeShare, 0.05f, 1f) || !In(g.CarryAcceleration, 0, 1000))
            throw new InvalidDataException("Invalid tractor guidance tuning.");
        return g;
    }

    private static bool In(float v, float low, float high) => float.IsFinite(v) && v >= low && v <= high;
}

/// <summary>Shared container geometry. One ship cell is one metre; drone cells do not redefine cargo units.</summary>
public sealed class CargoCatalog
{
    /// <summary>Loose-container simulation tuning, read once from shared content.</summary>
    public int SpacePerSector { get; private set; }
    public int SpaceContactBudget { get; private set; }
    public float SpaceShellKg { get; private set; }
    public float SpaceRestitution { get; private set; }
    public float JettisonSpeed { get; private set; }
    /// <summary>Delay before any tractor may acquire a released container.</summary>
    public float PickupDelaySeconds { get; private set; }
    public float EjectionSpeed { get; private set; }
    /// <summary>Figures for the tractor's guided path into a rack.</summary>
    public TractorGuidance Tractor { get; private set; }
    private readonly ContainerSize[] _sizes;
    private readonly Dictionary<string, double> _densities;
    /// <summary>Metres per unit edge, relative to the one-metre ship cell.</summary>
    public float UnitSize { get; }
    /// <summary>Sizes in ascending volume order.</summary>
    public ReadOnlySpan<ContainerSize> Sizes => _sizes;
    private CargoCatalog(float unitSize, ContainerSize[] sizes, Dictionary<string, double> densities)
    { UnitSize = unitSize; _sizes = sizes; _densities = densities; }

    /// <summary>Maximum kilograms per cubic metre for a galaxy resource id; zero for an unknown resource.</summary>
    public double Density(string resource) => _densities.GetValueOrDefault(resource);

    /// <summary>Reads and validates doubling sizes, positive strength and exact integer box volumes.</summary>
    public static CargoCatalog Parse(ReadOnlyMemory<byte> bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var unit = root.GetProperty("unitSize"u8).GetSingle();
        var entries = root.GetProperty("sizes"u8);
        if (!float.IsFinite(unit) || unit < 0.0625f || unit > 1 || entries.GetArrayLength() != 8)
            throw new InvalidDataException("Invalid cargo unit size or size count.");
        var sizes = new ContainerSize[8];
        for (var i = 0; i < sizes.Length; i++)
        {
            var e = entries[i];
            var shape = e.GetProperty("shape"u8);
            if (shape.GetArrayLength() != 3) throw new InvalidDataException("Cargo shape needs three sides.");
            var s = new GridSize(shape[0].GetInt32(), shape[1].GetInt32(), shape[2].GetInt32());
            var units = e.GetProperty("units"u8).GetInt32();
            var hull = e.GetProperty("hull"u8).GetSingle();
            var name = e.GetProperty("name"u8).GetString();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 32 || units != (1 << i)
                || s.X < 1 || s.Y < 1 || s.Z < 1 || s.X > units || s.Y > units || s.Z > units
                || (long)s.X * s.Y * s.Z != units || !float.IsFinite(hull) || hull <= 0
                || (i > 0 && hull < sizes[i - 1].Hull))
                throw new InvalidDataException("Invalid cargo size, shape or hull points.");
            for (var j = 0; j < i; j++)
                if (sizes[j].Name == name) throw new InvalidDataException("Duplicate cargo size name.");
            sizes[i] = new ContainerSize(name, units, s, hull);
        }
        var densities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("densityKgPerCubicMetre"u8).EnumerateObject())
        {
            var density = entry.Value.GetDouble();
            if (string.IsNullOrWhiteSpace(entry.Name) || !double.IsFinite(density) || density <= 0 || density > 1e9 || !densities.TryAdd(entry.Name, density))
                throw new InvalidDataException("Invalid or duplicate cargo resource density.");
        }
        var space = root.GetProperty("space"u8);
        var result = new CargoCatalog(unit, sizes, densities)
        {
            PickupDelaySeconds = root.GetProperty("pickupDelaySeconds"u8).GetSingle(),
            SpacePerSector = space.GetProperty("perSector"u8).GetInt32(),
            SpaceContactBudget = space.GetProperty("contactBudget"u8).GetInt32(),
            SpaceShellKg = space.GetProperty("shellKgPerUnit"u8).GetSingle(),
            SpaceRestitution = space.GetProperty("restitution"u8).GetSingle(),
            JettisonSpeed = space.GetProperty("jettisonSpeed"u8).GetSingle(),
            EjectionSpeed = space.GetProperty("ejectionSpeed"u8).GetSingle(),
            Tractor = TractorGuidance.Read(root.GetProperty("tractor"u8))
        };
        if (!float.IsFinite(result.PickupDelaySeconds) || result.PickupDelaySeconds < 0 || result.PickupDelaySeconds > 3600
            || result.SpacePerSector is < 1 or > FreeContainers.Capacity || result.SpaceContactBudget is < 1 or > 1024
            || !float.IsFinite(result.SpaceShellKg) || result.SpaceShellKg <= 0 || result.SpaceShellKg > 1000
            || !float.IsFinite(result.SpaceRestitution) || result.SpaceRestitution < 0 || result.SpaceRestitution > 1
            || !float.IsFinite(result.JettisonSpeed) || result.JettisonSpeed <= 0 || result.JettisonSpeed > 100
            || !float.IsFinite(result.EjectionSpeed) || result.EjectionSpeed <= 0 || result.EjectionSpeed > 100)
            throw new InvalidDataException("Invalid free-container simulation tuning.");
        return result;
    }
}

/// <summary>A packed container, indexed by size, at its minimum unit-grid corner.</summary>
public readonly record struct CargoPlacement(int Size, GridSize At);

/// <summary>Largest first, fixed orientation, first fit with X fastest, then Z, then Y. No allocation or rotation.</summary>
public static class CargoPacking
{
    /// <summary>Usable whole unit-grid dimensions; any fractional strip remains unused.</summary>
    public static GridSize Grid(CargoCatalog cargo, GridSize cells, float blockSize)
    {
        if (!float.IsFinite(blockSize) || blockSize <= 0 || cells.X <= 0 || cells.Y <= 0 || cells.Z <= 0)
            return default;
        var x = Math.Floor(cells.X * (double)blockSize / cargo.UnitSize);
        var y = Math.Floor(cells.Y * (double)blockSize / cargo.UnitSize);
        var z = Math.Floor(cells.Z * (double)blockSize / cargo.UnitSize);
        if (x > int.MaxValue || y > int.MaxValue || z > int.MaxValue || x * y * z > int.MaxValue) return default;
        return new((int)x, (int)y, (int)z);
    }

    /// <summary>Capacity in u, including only whole unit slots.</summary>
    public static int Capacity(CargoCatalog cargo, GridSize cells, float blockSize)
    {
        var g = Grid(cargo, cells, blockSize);
        return g.X * g.Y * g.Z;
    }

    /// <summary>Largest size index fitting the fixed orientation, or -1.</summary>
    public static int Largest(CargoCatalog cargo, GridSize cells, float blockSize)
    {
        var g = Grid(cargo, cells, blockSize);
        for (var i = cargo.Sizes.Length - 1; i >= 0; i--)
        {
            var s = cargo.Sizes[i].Shape;
            if (s.X <= g.X && s.Y <= g.Y && s.Z <= g.Z) return i;
        }
        return -1;
    }

    /// <summary>
    /// Packs size indices into caller storage, in canonical descending size order. Occupancy needs Capacity bytes.
    /// False means invalid input, insufficient buffers or no fit; output then has no usable placements.
    /// </summary>
    public static bool TryPack(CargoCatalog cargo, GridSize cells, float blockSize, ReadOnlySpan<int> sizes,
        Span<byte> occupied, Span<CargoPlacement> output)
    {
        var g = Grid(cargo, cells, blockSize);
        var capacity = Capacity(cargo, cells, blockSize);
        if (capacity == 0 || occupied.Length < capacity || output.Length < sizes.Length) return false;
        long total = 0;
        foreach (var i in sizes)
        {
            if ((uint)i >= (uint)cargo.Sizes.Length) return false;
            total += cargo.Sizes[i].Units;
        }
        if (total > capacity) return false;
        occupied[..capacity].Clear();
        var count = 0;
        for (var size = cargo.Sizes.Length - 1; size >= 0; size--)
        {
            var s = cargo.Sizes[size].Shape;
            foreach (var requested in sizes)
            {
                if (requested != size) continue;
                var found = false;
                for (var y = 0; y <= g.Y - s.Y && !found; y++)
                    for (var z = 0; z <= g.Z - s.Z && !found; z++)
                        for (var x = 0; x <= g.X - s.X && !found; x++)
                        {
                            var free = true;
                            for (var dy = 0; dy < s.Y && free; dy++)
                                for (var dz = 0; dz < s.Z && free; dz++)
                                    for (var dx = 0; dx < s.X; dx++)
                                        if (occupied[x + dx + g.X * (z + dz + g.Z * (y + dy))] != 0) { free = false; break; }
                            if (!free) continue;
                            for (var dy = 0; dy < s.Y; dy++)
                                for (var dz = 0; dz < s.Z; dz++)
                                    occupied.Slice(x + g.X * (z + dz + g.Z * (y + dy)), s.X).Fill(1);
                            output[count++] = new CargoPlacement(size, new GridSize(x, y, z));
                            found = true;
                        }
                if (!found) { output[..count].Clear(); return false; }
            }
        }
        return true;
    }
}
