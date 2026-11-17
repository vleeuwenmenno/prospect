using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>Where a hull's drone scan stands.</summary>
public enum DroneScanPhase : byte
{
    None = 0,

    /// <summary>Drones are on their way to their places around the rock.</summary>
    Moving = 1,

    /// <summary>Every drone holds its place: the scan waits for the player's order.</summary>
    Ready = 2,

    /// <summary>The scan runs; its result is the server's when it ends.</summary>
    Scanning = 3,

    /// <summary>The scan is over (done or given up); its drones fly around the rock and home.</summary>
    Returning = 4,
}

/// <summary>
/// How drones scan a rock, from the <c>scans</c> entry of <c>content/blocks/drones.json</c> (shared content: the
/// server decides with these figures; a client reads them only to say the same).
/// </summary>
public sealed record DroneScanRules
{
    /// <summary>The farthest the rock's surface may be from the hull for drones to be sent to it.</summary>
    public required float RangeMetres { get; init; }

    /// <summary>How far off the surface a drone holds, as a share of the rock's radius, between the two limits below.</summary>
    public required float StandoffFraction { get; init; }
    public required float MinStandoffMetres { get; init; }
    public required float MaxStandoffMetres { get; init; }

    /// <summary>How near its place a drone counts as holding it.</summary>
    public required float PlaceToleranceMetres { get; init; }

    /// <summary>
    /// The angle a drone moves around the rock's bounding shell at a time on its way to a place on the far side,
    /// so it never cuts through the rock.
    /// </summary>
    public required float TravelStepDegrees { get; init; }

    /// <summary>The longest the drones may take to reach their places before the scan is given up.</summary>
    public required float MoveSeconds { get; init; }

    /// <summary>The longest the drones wait in their places for the player's order before they come home.</summary>
    public required float ReadySeconds { get; init; }

    /// <summary>The step a drone scan resolves a share of the rock's mass to: far finer than any scanner module.</summary>
    public required float Resolution { get; init; }

    /// <summary>The most rock surface one drone's sensors cover, in square metres.</summary>
    public required float CoverageSquareMetres { get; init; }

    /// <summary>How many times over the drones together must see the rock's surface: all of it, or more, to map it.</summary>
    public required float Cover { get; init; }

    /// <summary>The fewest drones a scan takes, however small the rock.</summary>
    public required int MinDrones { get; init; }

    /// <summary>The most drones one scan may take; a rock that needs more is too large for drones.</summary>
    public required int MaxDrones { get; init; }

    /// <summary>How long the scan runs once the player orders it.</summary>
    public required float ScanSeconds { get; init; }

    /// <summary>Ticks the scan runs, at least one.</summary>
    public long ScanTicks => Math.Max(1, (long)Math.Ceiling(ScanSeconds * TickLoop.SimulationHz));

    public long MoveTicks => Math.Max(1, (long)Math.Ceiling(MoveSeconds * TickLoop.SimulationHz));
    public long ReadyTicks => Math.Max(1, (long)Math.Ceiling(ReadySeconds * TickLoop.SimulationHz));

    /// <summary>The travel step in radians.</summary>
    public float TravelStepRadians => TravelStepDegrees * (MathF.PI / 180f);

    /// <summary>Reads the <c>scans</c> entry; a figure out of its range refuses the content.</summary>
    public static DroneScanRules Read(JsonElement scans, int maxPerPlayer)
    {
        static float Number(JsonElement e, string key, float low, float high)
        {
            var value = e.GetProperty(key).GetSingle();
            if (!float.IsFinite(value) || value < low || value > high)
                throw new InvalidDataException($"drones.json: scans.{key} must be from {low} to {high}");
            return value;
        }

        var low = scans.GetProperty("minDrones"u8).GetInt32();
        var high = scans.GetProperty("maxDrones"u8).GetInt32();
        if (low < 1 || high < low || high > DroneScanning.MaxDrones || high > Math.Max(1, maxPerPlayer))
            throw new InvalidDataException($"drones.json: scans takes 1 to {DroneScanning.MaxDrones} drones, no more than a player may have out, and minDrones no more than maxDrones");

        var rules = new DroneScanRules
        {
            RangeMetres = Number(scans, "rangeMetres", 1, 20000),
            StandoffFraction = Number(scans, "standoffFraction", 0, 10),
            MinStandoffMetres = Number(scans, "minStandoffMetres", 1, 1000),
            MaxStandoffMetres = Number(scans, "maxStandoffMetres", 1, 5000),
            PlaceToleranceMetres = Number(scans, "placeToleranceMetres", 0.25f, 100),
            TravelStepDegrees = Number(scans, "travelStepDegrees", 5, 60),
            MoveSeconds = Number(scans, "moveSeconds", 1, 3600),
            ReadySeconds = Number(scans, "readySeconds", 1, 3600),
            Resolution = Number(scans, "resolution", 1e-4f, 0.05f),
            CoverageSquareMetres = Number(scans, "coverageSquareMetres", 1, 1e9f),
            Cover = Number(scans, "cover", 0.05f, 10),
            MinDrones = low,
            MaxDrones = high,
            ScanSeconds = Number(scans, "scanSeconds", 0.1f, 600),
        };
        if (rules.MaxStandoffMetres < rules.MinStandoffMetres)
            throw new InvalidDataException("drones.json: scans.maxStandoffMetres must not be below minStandoffMetres");
        return rules;
    }
}

/// <summary>
/// The shared rules of a drone scan: how many drones a rock takes, where each holds, and the path a drone takes around
/// the rock to get there. Points are in the rock's own frame (undo <see cref="Asteroid.Rotation"/>), in metres from its
/// centre. The server decides with these; the figures a client is shown come from the server. Takes no memory.
/// </summary>
public static class DroneScanning
{
    /// <summary>The most drones one scan can take: one per place the wire and the bay view count in a byte.</summary>
    public const int MaxDrones = 32;

    /// <summary>Directions the heat map has, spread evenly over the rock.</summary>
    public const int HeatCells = 64;

    /// <summary>Samples along each heat map direction, from the surface to the centre.</summary>
    public const int HeatDepthSamples = 20;

    /// <summary>
    /// The <paramref name="index"/>-th of <paramref name="count"/> directions spread evenly over the sphere (a golden
    /// spiral). Only where drones hold and which way a heat cell looks hang on it; neither is a discrete choice a
    /// client works out for itself, so plain float arithmetic with <see cref="DetMath"/> is enough.
    /// </summary>
    public static Vector3 Direction(int index, int count)
    {
        if (count <= 1) return Vector3.UnitY;
        var y = 1f - 2f * (index + 0.5f) / count;
        var ring = MathF.Sqrt(Math.Max(0f, 1f - y * y));
        var angle = index * 2.39996323f;
        return new Vector3(ring * DetMath.Cos(angle), y, ring * DetMath.Sin(angle));
    }

    /// <summary>
    /// The rock's extent from the surface table (<see cref="RockInteriorSurface"/>) over the heat map's directions:
    /// the farthest and the nearest surface and their mean, as fractions of the radius.
    /// </summary>
    public static void Extent(int shape, out float farthest, out float mean, out float nearest)
    {
        farthest = 0; nearest = float.MaxValue; var sum = 0f;
        for (var i = 0; i < HeatCells; i++)
        {
            var d = Direction(i, HeatCells);
            RockInteriorSurface.Lookup(shape, d.X, d.Y, d.Z, out var surface, out _);
            farthest = Math.Max(farthest, surface);
            nearest = Math.Min(nearest, surface);
            sum += surface;
        }
        mean = sum / HeatCells;
    }

    /// <summary>How far off the surface drones hold for a rock of <paramref name="radius"/> metres.</summary>
    public static float Standoff(DroneScanRules rules, float radius) =>
        Math.Clamp(radius * rules.StandoffFraction, rules.MinStandoffMetres, rules.MaxStandoffMetres);

    /// <summary>
    /// The radius of the shell drones travel on around a rock: its farthest surface and the standoff. Nothing of the
    /// rock reaches past it.
    /// </summary>
    public static float Shell(DroneScanRules rules, float radius, int shape)
    {
        Extent(shape, out var farthest, out _, out _);
        return Math.Max(farthest, 1f) * radius + Standoff(rules, radius);
    }

    /// <summary>
    /// How many drones a scan takes for a rock, before the limit: from its size
    /// (surface area), its shape (an elongated rock has more surface than its mean radius says) and line of sight
    /// (a drone at the standoff sees only a cap of it). Each drone covers the smaller of what it can see and its
    /// sensors' coverage; together they must cover the rock <see cref="DroneScanRules.Cover"/> times. Never less
    /// than <see cref="DroneScanRules.MinDrones"/>; can be more than <see cref="DroneScanRules.MaxDrones"/>, which
    /// means the rock is too large for drones.
    /// </summary>
    public static int Needed(DroneScanRules rules, float radius, int shape)
    {
        Extent(shape, out var farthest, out var mean, out var nearest);
        var reach = Math.Max(farthest, 1e-3f) * radius;
        var standoff = Standoff(rules, radius);
        // Line of sight: the cap of a sphere of the farthest radius seen from the standoff above it.
        var sight = 0.5f * (1f - reach / (reach + standoff));
        // Shape: the surface of a sphere of the mean radius, stretched by how much longer the rock is than wide.
        var stretch = nearest > 0 ? Math.Clamp(farthest / nearest, 1f, 8f) : 1f;
        var area = 4f * MathF.PI * (mean * radius) * (mean * radius) * MathF.Sqrt(stretch);
        var each = Math.Min(sight, area > 0 ? rules.CoverageSquareMetres / area : 1f);
        if (each <= 0) return int.MaxValue;
        var needed = (int)Math.Ceiling(rules.Cover / each - 1e-4f);
        return Math.Max(rules.MinDrones, needed);
    }

    /// <summary>
    /// The <paramref name="slot"/>-th of <paramref name="count"/> drones' place around a rock, in its own frame: spread
    /// evenly over the sphere of directions, the standoff off the surface that way.
    /// </summary>
    public static Vector3 Place(int slot, int count, float radius, int shape, float standoff)
    {
        var d = Direction(slot, count);
        RockInteriorSurface.Lookup(shape, d.X, d.Y, d.Z, out var surface, out _);
        return d * (surface * radius + standoff);
    }

    /// <summary>
    /// Where a drone at <paramref name="from"/> flies next on its way to <paramref name="place"/> (both in the rock's
    /// frame, metres from its centre) without cutting through the rock: along the great circle between the two on the
    /// bounding shell <paramref name="shell"/>, at most <paramref name="step"/> radians ahead of where it is, and
    /// straight to the place once within that angle of it. A straight line from outside the shell to a point on it
    /// less than a right angle away stays outside it, so no leg passes through the rock.
    /// </summary>
    public static Vector3 Travel(Vector3 from, Vector3 place, float shell, float step)
    {
        var a = from.Length();
        var b = place.Length();
        if (a < 1e-4f || b < 1e-4f) return place;
        var u = from / a;
        var v = place / b;
        var cos = Math.Clamp(Vector3.Dot(u, v), -1f, 1f);
        var angle = MathF.Acos(cos);
        if (angle <= step) return place;
        // Turn u toward v by the step; directly opposite, any axis across will do.
        var axis = Vector3.Cross(u, v);
        if (axis.LengthSquared() < 1e-10f)
        {
            axis = Vector3.Cross(u, Math.Abs(u.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        }
        axis = Vector3.Normalize(axis);
        var turned = u * MathF.Cos(step) + Vector3.Cross(axis, u) * MathF.Sin(step) + axis * (Vector3.Dot(axis, u) * (1f - MathF.Cos(step)));
        return Vector3.Normalize(turned) * shell;
    }
}

/// <summary>A point of a vein in a scan: in the rock's frame as a share of its radius (32767 is one radius), and the vein's radius there.</summary>
public struct DroneScanPoint
{
    public short X, Y, Z;

    /// <summary>The vein's radius as a share of the rock's radius, 65535 for one radius.</summary>
    public ushort R;
}

/// <summary>One vein of a scan: how many points it has, its ore and that ore's share of the vein.</summary>
public struct DroneScanVein
{
    public byte Points;
    public sbyte Resource;

    /// <summary>The ore's share of the vein's mass, 255 for all of it.</summary>
    public byte Grade;
}

/// <summary>A pocket of a scan: its centre and radius as for <see cref="DroneScanPoint"/>, what fills it and its ore.</summary>
public struct DroneScanPocket
{
    public short X, Y, Z;
    public ushort R;
    public RockPocketKind Kind;
    public sbyte Resource;
    public byte Grade;

    /// <summary>A pocket of ice or volatile gas: it may blow when cracked.</summary>
    public readonly bool Unstable => Kind != RockPocketKind.Ore;
}

[InlineArray(RockInteriorRules.MaxVeins)]
public struct DroneScanVeins { private DroneScanVein _first; }

[InlineArray(RockInteriorRules.MaxVeins * RockInteriorRules.MaxVeinPoints)]
public struct DroneScanPoints { private DroneScanPoint _first; }

[InlineArray(RockInteriorRules.MaxPockets)]
public struct DroneScanPockets { private DroneScanPocket _first; }

[InlineArray(DroneScanning.HeatCells)]
public struct DroneScanCells { private byte _first; }

/// <summary>
/// The map of a scan: veins as polylines with a radius at each point, pockets as balls, and a heat map over
/// the rock's surface. Vein <c>v</c>'s points are <see cref="Points"/>[v * <see cref="RockInteriorRules.MaxVeinPoints"/> + k].
/// </summary>
public struct DroneScanMap
{
    public byte VeinCount, PocketCount;
    public DroneScanVeins Veins;
    public DroneScanPoints Points;
    public DroneScanPockets Pockets;

    /// <summary>
    /// Per heat map direction (<see cref="DroneScanning.Direction"/> of <see cref="DroneScanning.HeatCells"/>): bit 7
    /// set when an unstable pocket lies under that part of the surface; bits 0 to 6 how near the surface ore richer
    /// than the mantle lies there (127 just under it, 0 none).
    /// </summary>
    public DroneScanCells Heat;

    /// <summary>The surface's distance that way, as a share of the rock's radius (255 for one radius).</summary>
    public DroneScanCells Surface;

    /// <summary>Whether a heat cell has an unstable pocket under it.</summary>
    public readonly bool Unstable(int cell) => (Heat[cell] & 0x80) != 0;

    /// <summary>How easy a heat cell's ore is to reach, from 0 (none found) to 1 (just under the surface).</summary>
    public readonly float Ease(int cell) => (Heat[cell] & 0x7F) / 127f;
}

/// <summary>
/// What one player's drones found in one rock: kept per owner on the server, saved, and sent to that owner only. The
/// composition is the rock's true bulk composition at the drones' resolution, and the map shows its veins and pockets. A client
/// shows this and nothing generated.
/// </summary>
public struct DroneScanRecord
{
    public uint Owner;
    public SectorCoord Sector;
    public int Index;
    public long RecordedTick;

    /// <summary>The rock's radius in metres: the scale of every point of the map.</summary>
    public float Radius;

    /// <summary>The main ore (-1 for bare rock) and the mix-ins (-1 for none) with their shares of the rock's mass, as <see cref="ModuleObservation"/> has them.</summary>
    public int Resource, Mix1, Mix2;
    public float Grade, Mix1Share, Mix2Share;

    /// <summary>The veins, pockets and heat map the drones found.</summary>
    public DroneScanMap Map;

    public readonly RockRef Rock => new(Sector, Index);

    /// <summary>The composition as a module observation has it, so the panels read both the same way. Takes no memory.</summary>
    public readonly ModuleObservation AsObservation(ModuleQuality quality = ModuleQuality.AlienTech) => new()
    {
        Owner = Owner,
        Family = ModuleFamily.AsteroidScanner,
        Quality = quality,
        Sector = Sector,
        Index = Index,
        RecordedTick = RecordedTick,
        Radius = Radius,
        Resource = Resource,
        Grade = Grade,
        Mix1 = Mix1,
        Mix1Share = Mix1Share,
        Mix2 = Mix2,
        Mix2Share = Mix2Share,
    };

    /// <summary>A vein's point in the rock's frame, in metres from its centre, and the vein's radius there in metres.</summary>
    public readonly Vector4 VeinPoint(int vein, int point)
    {
        var p = Map.Points[vein * RockInteriorRules.MaxVeinPoints + point];
        var scale = Radius / 32767f;
        return new Vector4(p.X * scale, p.Y * scale, p.Z * scale, p.R * (Radius / 65535f));
    }

    /// <summary>A pocket's centre in the rock's frame, in metres from its centre, and its radius in metres.</summary>
    public readonly Vector4 PocketAt(int pocket)
    {
        var p = Map.Pockets[pocket];
        var scale = Radius / 32767f;
        return new Vector4(p.X * scale, p.Y * scale, p.Z * scale, p.R * (Radius / 65535f));
    }

    /// <summary>A heat cell's point on the rock's surface, in its frame, in metres from its centre.</summary>
    public readonly Vector3 HeatPoint(int cell) => DroneScanning.Direction(cell, DroneScanning.HeatCells) * (Map.Surface[cell] / 255f * Radius);
}

/// <summary>
/// Works a drone scan's result out of a rock's interior (<see cref="RockInterior"/>): the server's only way to make a
/// <see cref="DroneScanRecord"/>. Pure and allocation-free.
/// </summary>
public static class DroneScanResults
{
    /// <summary>
    /// Fills <paramref name="record"/> for a scan of <paramref name="asteroid"/>, whose
    /// interior is <paramref name="interior"/>. The owner, rock and tick are the caller's.
    /// </summary>
    public static void Build(in Asteroid asteroid, in RockInterior interior, float resolution, ref DroneScanRecord record)
    {
        record.Radius = asteroid.Radius;
        record.Map = default;
        record.Resource = asteroid.Resource;
        record.Mix1 = asteroid.Resource >= 0 ? asteroid.Mix1 : -1;
        record.Mix2 = record.Mix1 >= 0 ? asteroid.Mix2 : -1;
        record.Grade = record.Resource >= 0 ? Resolved(asteroid.Grade, resolution) : 0;
        record.Mix1Share = record.Mix1 >= 0 ? Math.Min(record.Grade, Resolved(asteroid.Mix1Share, resolution)) : 0;
        record.Mix2Share = record.Mix2 >= 0 ? Math.Min(record.Mix1Share, Resolved(asteroid.Mix2Share, resolution)) : 0;
        Map(asteroid, interior, ref record);
    }

    /// <summary>A share as the drones resolve it: whole steps, never more than is there.</summary>
    public static float Resolved(float share, float step) =>
        step <= 0 ? share : (float)Math.Clamp(Math.Floor(share / (double)step + 1e-4) * step, 0, 1);

    private static void Map(in Asteroid asteroid, in RockInterior interior, ref DroneScanRecord record)
    {
        ref var map = ref record.Map;
        var radius = asteroid.Radius;
        var shape = asteroid.Shape;
        map.VeinCount = (byte)Math.Min(interior.VeinCount, RockInteriorRules.MaxVeins);
        var points = Math.Min(interior.VeinPointCount, RockInteriorRules.MaxVeinPoints);
        for (var v = 0; v < map.VeinCount; v++)
        {
            map.Veins[v] = new DroneScanVein { Points = (byte)points, Resource = (sbyte)interior.Resource, Grade = Unit(interior.VeinShares.X) };
            for (var k = 0; k < points; k++)
            {
                var n = interior.VeinPoint(v, k);
                var at = Metres(shape, new Vector3(n.X, n.Y, n.Z), out var surface);
                map.Points[v * RockInteriorRules.MaxVeinPoints + k] = new DroneScanPoint
                {
                    X = Short(at.X),
                    Y = Short(at.Y),
                    Z = Short(at.Z),
                    R = (ushort)Math.Clamp(MathF.Round(n.W * surface * 65535f), 0, 65535),
                };
            }
        }

        map.PocketCount = (byte)Math.Min(interior.PocketCount, RockInteriorRules.MaxPockets);
        for (var p = 0; p < map.PocketCount; p++)
        {
            var c = interior.PocketAt(p);
            var at = Metres(shape, new Vector3(c.X, c.Y, c.Z), out var surface);
            var kind = interior.PocketKind(p);
            var resource = -1;
            var grade = 0f;
            if (kind == RockPocketKind.Ore)
            {
                resource = interior.PocketMix == 2 ? interior.Mix2 : interior.Mix1;
                grade = interior.PocketMix == 2 ? interior.PocketShares.Z : interior.PocketShares.Y;
            }
            map.Pockets[p] = new DroneScanPocket
            {
                X = Short(at.X),
                Y = Short(at.Y),
                Z = Short(at.Z),
                R = (ushort)Math.Clamp(MathF.Round(c.W * surface * 65535f), 0, 65535),
                Kind = kind,
                Resource = (sbyte)resource,
                Grade = Unit(grade),
            };
        }

        // The heat map: down from the surface along each direction, how soon ore richer than the mantle is met, and
        // whether an unstable pocket lies on the way.
        var mantle = interior.MantleShares.X + interior.MantleShares.Y + interior.MantleShares.Z;
        for (var cell = 0; cell < DroneScanning.HeatCells; cell++)
        {
            var d = DroneScanning.Direction(cell, DroneScanning.HeatCells);
            RockInteriorSurface.Lookup(shape, d.X, d.Y, d.Z, out var surface, out _);
            map.Surface[cell] = (byte)Math.Clamp(MathF.Round(surface * 255f), 0, 255);
            var ease = 0;
            var unstable = false;
            for (var k = 1; k <= DroneScanning.HeatDepthSamples; k++)
            {
                var depth = 1f - k / (DroneScanning.HeatDepthSamples + 1f);
                interior.Sample(d * (surface * radius * depth), out var sample);
                if (sample.Unstable) unstable = true;
                if (ease == 0 && (sample.Layer == RockLayer.Vein || sample.Layer == RockLayer.Pocket && sample.Pocket == RockPocketKind.Ore)
                    && sample.Ore > mantle)
                    ease = (int)MathF.Round(127f * (DroneScanning.HeatDepthSamples + 1 - k) / DroneScanning.HeatDepthSamples);
            }
            map.Heat[cell] = (byte)((unstable ? 0x80 : 0) | Math.Clamp(ease, 0, 127));
        }
    }

    /// <summary>A point of normalised space as a share of the rock's radius: the point times the surface's distance its way.</summary>
    private static Vector3 Metres(int shape, Vector3 n, out float surface)
    {
        var length = n.Length();
        var d = length > 1e-6f ? n / length : Vector3.UnitY;
        RockInteriorSurface.Lookup(shape, d.X, d.Y, d.Z, out surface, out _);
        return n * surface;
    }

    private static short Short(float share) => (short)Math.Clamp(MathF.Round(share * 32767f), -32767, 32767);
    private static byte Unit(float share) => (byte)Math.Clamp(MathF.Round(share * 255f), 0, 255);
}

/// <summary>Shared validation of a drone scan record, for the wire and the save alike, independent of a galaxy.</summary>
public static class DroneScanRecordRules
{
    /// <summary>The most drone scans the server keeps for one player; the oldest gives way to a new rock.</summary>
    public const int KnowledgePerOwner = 32;

    /// <summary>Structure only: the composition's shape and the map's counts. Resources are checked against the galaxy by the reader.</summary>
    public static bool Valid(in DroneScanRecord record)
    {
        if (record.Owner == 0 || record.Index < 0
            || record.Index > ushort.MaxValue || record.RecordedTick < 0 || !float.IsFinite(record.Radius) || record.Radius <= 0
            || record.Radius > 1e6f) return false;
        if (record.Resource is < -1 or >= 32 || record.Mix1 is < -1 or >= 32 || record.Mix2 is < -1 or >= 32
            || !Unit(record.Grade) || !Unit(record.Mix1Share) || !Unit(record.Mix2Share)) return false;
        if (record.Resource < 0 && (record.Grade != 0 || record.Mix1 >= 0)) return false;
        if (record.Mix1 < 0 && (record.Mix2 >= 0 || record.Mix1Share != 0)) return false;
        if (record.Mix2 < 0 && record.Mix2Share != 0) return false;
        if (record.Mix1 >= 0 && (record.Mix1 == record.Resource || record.Mix1Share > record.Grade)) return false;
        if (record.Mix2 >= 0 && (record.Mix2 == record.Resource || record.Mix2 == record.Mix1 || record.Mix2Share > record.Mix1Share)) return false;
        if (record.Grade + record.Mix1Share + record.Mix2Share > 1.00001f) return false;
        ref readonly var map = ref record.Map;
        if (map.VeinCount > RockInteriorRules.MaxVeins || map.PocketCount > RockInteriorRules.MaxPockets) return false;
        for (var v = 0; v < map.VeinCount; v++)
        {
            var vein = map.Veins[v];
            if (vein.Points is < 2 or > RockInteriorRules.MaxVeinPoints || vein.Resource < -1 || vein.Resource >= 32) return false;
            for (var k = 0; k < vein.Points; k++) if (!Point(map.Points[v * RockInteriorRules.MaxVeinPoints + k])) return false;
        }
        for (var p = 0; p < map.PocketCount; p++)
        {
            var pocket = map.Pockets[p];
            if (pocket.Kind > RockPocketKind.Volatile || pocket.Resource < -1 || pocket.Resource >= 32 || pocket.X == short.MinValue
                || pocket.Y == short.MinValue || pocket.Z == short.MinValue || (pocket.Unstable && (pocket.Resource != -1 || pocket.Grade != 0)))
                return false;
        }
        return true;
    }

    private static bool Point(in DroneScanPoint p) => p.X != short.MinValue && p.Y != short.MinValue && p.Z != short.MinValue;
    private static bool Unit(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
}
