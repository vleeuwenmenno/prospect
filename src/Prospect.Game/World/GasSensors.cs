namespace Prospect.Game.World;

/// <summary>Authoritative instantaneous environment at the hull centre; derived, never saved.</summary>
[Flags]
public enum HullEnvironment : byte { None = 0, InColdCloud = 1, InLightningCloud = 2, SensorsDisrupted = 4 }

/// <summary>Deterministic passive sensing through smooth gas envelopes, independent of shader noise.</summary>
public static class GasSensors
{
    /// <summary>Tests a center against range spent on distance and gas. Proximity is always clear.</summary>
    public static bool Covers(Galaxy galaxy, ReadOnlySpan<GasCloudPlacement> clouds, ExplorationRules rules,
        Double3 sensor, Double3 target, bool equipped = true, long tick = 0)
    {
        var delta = target - sensor;
        var distance = delta.Length;
        if (distance <= rules.ProximityRadius) return true;
        var budget = Budget(galaxy, clouds, rules, sensor, tick);
        if (!equipped || !(distance <= budget)) return false;
        return Cost(galaxy, clouds, rules, sensor, delta * (1 / distance), distance) <= budget;
    }

    /// <summary>Reach in a nonzero direction (normalized here). Caller supplies every cloud in base range.</summary>
    public static double Reach(Galaxy galaxy, ReadOnlySpan<GasCloudPlacement> clouds, ExplorationRules rules,
        Double3 sensor, Double3 direction, long tick = 0)
    {
        var length = direction.Length;
        if (!(length > 0) || !double.IsFinite(length)) return 0;
        direction *= 1 / length;
        var low = rules.ProximityRadius;
        var budget = Budget(galaxy, clouds, rules, sensor, tick);
        var high = budget;
        if (Cost(galaxy, clouds, rules, sensor, direction, high) <= high) return high;
        // The integral is nonnegative and monotone. Keep the covered side, within 0.3 mm at 5 km.
        for (var i = 0; i < 24; i++)
        {
            var middle = (low + high) * 0.5;
            if (Cost(galaxy, clouds, rules, sensor, direction, middle) <= budget) low = middle;
            else high = middle;
        }
        return low;
    }

    /// <summary>Samples any caller-owned ring/elevations; outputs metres, with no allocation.</summary>
    public static void SampleReach(Galaxy galaxy, ReadOnlySpan<GasCloudPlacement> clouds, ExplorationRules rules,
        Double3 sensor, ReadOnlySpan<Double3> directions, Span<double> reaches, long tick = 0)
    {
        if (reaches.Length < directions.Length) throw new ArgumentException("Reach buffer is too short.", nameof(reaches));
        for (var i = 0; i < directions.Length; i++) reaches[i] = Reach(galaxy, clouds, rules, sensor, directions[i], tick);
    }

    /// <summary>Membership in the same smooth ellipsoid used by sensor attenuation.</summary>
    public static bool Contains(Galaxy galaxy, in GasCloudPlacement placement, Double3 point)
    {
        if (galaxy.GasClouds is not { } gas) return false;
        var offset = point - GasCloudQuery.CenterOf(galaxy, placement);
        var cloud = placement.Cloud;
        var x = offset.X / (cloud.HalfSize.X * (double)gas.Shape.X);
        var y = offset.Y / (cloud.HalfSize.Y * (double)gas.Shape.X);
        var z = offset.Z / (cloud.HalfSize.Z * (double)gas.Shape.X);
        return x * x + y * y + z * z <= 1;
    }

    /// <summary>Seeded burst phase on the saved server clock; no random draws or mutable state.</summary>
    public static bool Disrupted(in GasCloud cloud, ExplorationRules rules, long tick)
    {
        var phase = (long)(cloud.Seed / 128.0 * rules.LightningPeriodTicks);
        return ((tick % rules.LightningPeriodTicks + phase) % rules.LightningPeriodTicks + rules.LightningPeriodTicks)
            % rules.LightningPeriodTicks < rules.LightningBurstTicks;
    }

    /// <summary>
    /// Which lightning cycle a server tick falls in for this cloud, and how many ticks into that cycle it is. The
    /// burst is the first <see cref="ExplorationRules.LightningBurstTicks"/> of a cycle, so
    /// <c>into &lt; LightningBurstTicks</c> exactly when <see cref="Disrupted"/> is true. The renderer times its
    /// flashes by this, so what a player sees and what their sensors lose happen together; the cycle number seeds
    /// where the strokes fall.
    /// </summary>
    public static long LightningCycle(in GasCloud cloud, ExplorationRules rules, long tick, out long into)
    {
        var period = (long)rules.LightningPeriodTicks;
        var phase = (long)(cloud.Seed / 128.0 * period);
        into = ((tick % period + phase) % period + period) % period;
        // tick + phase - into is a whole number of periods, so the division is exact for any sign.
        return (tick + phase - into) / period;
    }

    /// <summary>Flags at a hull centre, using server snapshot time, without allocation.</summary>
    public static HullEnvironment EnvironmentAt(Galaxy galaxy, ReadOnlySpan<GasCloudPlacement> clouds,
        ExplorationRules rules, Double3 point, long tick)
    {
        var status = HullEnvironment.None;
        foreach (var placement in clouds)
        {
            if (placement.Cloud.Kind == GasCloudKind.Normal || !Contains(galaxy, placement, point)) continue;
            if (placement.Cloud.Kind == GasCloudKind.Cold) status |= HullEnvironment.InColdCloud;
            if (placement.Cloud.Kind != GasCloudKind.Lightning) continue;
            status |= HullEnvironment.InLightningCloud;
            if (Disrupted(placement.Cloud, rules, tick)) status |= HullEnvironment.SensorsDisrupted;
        }
        return status;
    }

    private static double Budget(Galaxy galaxy, ReadOnlySpan<GasCloudPlacement> clouds, ExplorationRules rules, Double3 point, long tick) =>
        (EnvironmentAt(galaxy, clouds, rules, point, tick) & HullEnvironment.SensorsDisrupted) != 0
            ? Math.Max(rules.ProximityRadius, rules.SensorRadius * rules.LightningReachShare) : rules.SensorRadius;

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
