namespace Prospect.Game.World;

/// <summary>Deterministic passive sensing through smooth gas envelopes, independent of shader noise.</summary>
public static class GasSensors
{
    /// <summary>Tests a center against range spent on distance and gas. Proximity is always clear.</summary>
    public static bool Covers(Galaxy galaxy, ReadOnlySpan<GasCloudPlacement> clouds, ExplorationRules rules,
        Double3 sensor, Double3 target, bool equipped = true)
    {
        var delta = target - sensor;
        var distance = delta.Length;
        if (distance <= rules.ProximityRadius) return true;
        if (!equipped || !(distance <= rules.SensorRadius)) return false;
        return Cost(galaxy, clouds, rules, sensor, delta * (1 / distance), distance) <= rules.SensorRadius;
    }

    /// <summary>Reach in a nonzero direction (normalized here). Caller supplies every cloud in base range.</summary>
    public static double Reach(Galaxy galaxy, ReadOnlySpan<GasCloudPlacement> clouds, ExplorationRules rules,
        Double3 sensor, Double3 direction)
    {
        var length = direction.Length;
        if (!(length > 0) || !double.IsFinite(length)) return 0;
        direction *= 1 / length;
        var low = rules.ProximityRadius;
        var high = rules.SensorRadius;
        if (Cost(galaxy, clouds, rules, sensor, direction, high) <= high) return high;
        // The integral is nonnegative and monotone. Keep the covered side, within 0.3 mm at 5 km.
        for (var i = 0; i < 24; i++)
        {
            var middle = (low + high) * 0.5;
            if (Cost(galaxy, clouds, rules, sensor, direction, middle) <= rules.SensorRadius) low = middle;
            else high = middle;
        }
        return low;
    }

    /// <summary>Samples any caller-owned ring/elevations; outputs metres, with no allocation.</summary>
    public static void SampleReach(Galaxy galaxy, ReadOnlySpan<GasCloudPlacement> clouds, ExplorationRules rules,
        Double3 sensor, ReadOnlySpan<Double3> directions, Span<double> reaches)
    {
        if (reaches.Length < directions.Length) throw new ArgumentException("Reach buffer is too short.", nameof(reaches));
        for (var i = 0; i < directions.Length; i++) reaches[i] = Reach(galaxy, clouds, rules, sensor, directions[i]);
    }

    private static double Cost(Galaxy galaxy, ReadOnlySpan<GasCloudPlacement> clouds, ExplorationRules rules,
        Double3 origin, Double3 direction, double distance)
    {
        if (galaxy.GasClouds is not { } gas || rules.GasOpticalPenalty == 0) return distance;
        double column = 0;
        foreach (var placement in clouds)
        {
            var cloud = placement.Cloud;
            var offset = origin - GasCloudQuery.CenterOf(galaxy, placement);
            // The generated box and content fill define a smooth ellipsoid. No animated/seeded shader noise.
            var rx = cloud.HalfSize.X * (double)gas.Shape.X;
            var ry = cloud.HalfSize.Y * (double)gas.Shape.X;
            var rz = cloud.HalfSize.Z * (double)gas.Shape.X;
            var ox = offset.X / rx; var oy = offset.Y / ry; var oz = offset.Z / rz;
            var dx = direction.X / rx; var dy = direction.Y / ry; var dz = direction.Z / rz;
            var a = dx * dx + dy * dy + dz * dz;
            var b = ox * dx + oy * dy + oz * dz;
            var c = ox * ox + oy * oy + oz * oz;
            var midpoint = -b / a;
            var peak = 1 - c + b * b / a;
            if (!(peak > 0)) continue;
            var half = Math.Sqrt(peak / a);
            var start = Math.Max(0, midpoint - half);
            var end = Math.Min(distance, midpoint + half);
            if (end <= start) continue;
            // Exact integral of max(0, 1 - ellipsoidRadiusSquared), using the chord midpoint for stability.
            var u = start - midpoint; var v = end - midpoint;
            var integral = (end - start) * (peak - a * (u * u + u * v + v * v) / 3);
            column += Math.Max(0, integral) * gas.ExtinctionOf(cloud, (float)galaxy.SectorSize);
        }
        return distance + rules.SensorRadius * rules.GasOpticalPenalty * column;
    }
}

/// <summary>Bounded, allocation-free sector cache. Own one per reader; not thread safe.</summary>
public sealed class SensorGasCache
{
    private const int Entries = 256;
    private readonly Galaxy _galaxy;
    private readonly int _shell, _capacity;
    private readonly GasCloudPlacement[] _clouds;
    private readonly SectorCoord[] _sectors = new SectorCoord[Entries];
    private readonly int[] _counts = new int[Entries];
    private readonly bool[] _valid = new bool[Entries];

    public SensorGasCache(Galaxy galaxy, double range)
    {
        if (!double.IsFinite(range) || range <= 0 || range > galaxy.SectorSize * 8)
            throw new ArgumentOutOfRangeException(nameof(range), "Sensor range must fit within eight sectors.");
        _galaxy = galaxy;
        _shell = (int)Math.Ceiling(range / galaxy.SectorSize) + 1;
        var side = 2 * _shell + 1;
        _capacity = side * side * side;
        _clouds = new GasCloudPlacement[Entries * _capacity];
    }

    /// <summary>All clouds that can affect any sensor in this sector, including across walls.</summary>
    public ReadOnlySpan<GasCloudPlacement> At(Double3 position)
    {
        var sector = _galaxy.SectorOf(position);
        var slot = (int)(unchecked((uint)sector.X * 73856093 ^ (uint)sector.Y * 19349663 ^ (uint)sector.Z * 83492791) % Entries);
        var buffer = _clouds.AsSpan(slot * _capacity, _capacity);
        if (!_valid[slot] || _sectors[slot] != sector)
        {
            _counts[slot] = GasCloudQuery.Collect(_galaxy, sector, _shell, buffer);
            _sectors[slot] = sector;
            _valid[slot] = true;
        }
        return buffer[.._counts[slot]];
    }
}
