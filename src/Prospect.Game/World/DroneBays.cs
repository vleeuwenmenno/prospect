using System.Numerics;
using System.Text.Json;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>
/// One drone bay block's figures after its tier: how many drones it keeps, how many leave through its hatch at once,
/// how long it takes to build a lost one and the mass each of its drones carries. Read from a block's <c>"bay"</c>
/// entry and the catalogue's <c>bayTiers</c>, as <see cref="TractorSpec"/> reads tractors: one block type per tier is
/// never needed.
/// </summary>
public sealed record BaySpec(int Capacity, int LaunchRate, float RebuildSeconds, ModuleQuality Quality = ModuleQuality.Common, float CarryKg = 1000f)
{
    /// <summary>Ticks a lost drone takes to rebuild, rounded up.</summary>
    public long RebuildTicks => (long)Math.Ceiling(RebuildSeconds * TickLoop.SimulationHz);

    /// <summary>Reads an optional bay entry; figures outside their bounds refuse the content.</summary>
    public static BaySpec? Read(JsonElement block, JsonElement root)
    {
        if (!block.TryGetProperty("bay"u8, out var value)) return null;
        var name = value.GetProperty("quality"u8).GetString();
        var quality = name switch
        {
            "Common" => ModuleQuality.Common,
            "Uncommon" => ModuleQuality.Uncommon,
            "Rare" => ModuleQuality.Rare,
            "Epic" => ModuleQuality.Epic,
            "Legendary" => ModuleQuality.Legendary,
            "Alien Tech" => ModuleQuality.AlienTech,
            _ => throw new InvalidDataException("Unknown drone bay quality."),
        };
        var tier = root.GetProperty("bayTiers"u8).GetProperty(name!);
        float Figure(string key, string scale)
        {
            var basis = value.GetProperty(key).GetSingle();
            var multiplier = tier.GetProperty(scale).GetSingle();
            var result = basis * multiplier;
            if (!float.IsFinite(basis) || basis <= 0 || !float.IsFinite(multiplier) || multiplier <= 0 || !float.IsFinite(result) || result <= 0)
                throw new InvalidDataException("Drone bay tuning must be finite and positive.");
            return result;
        }
        var capacity = (int)MathF.Round(Figure("capacity", "capacity"));
        var rate = (int)MathF.Round(Figure("launchRate", "launchRate"));
        var rebuild = Figure("rebuildSeconds", "rebuildSeconds");
        var carry = Figure("carryKg", "carry");
        if (capacity is < 1 or > DroneBayRules.MaxCapacity || rate < 1 || rate > capacity || rebuild > 86400f || carry > 1e7f)
            throw new InvalidDataException($"A drone bay keeps 1 to {DroneBayRules.MaxCapacity} drones, launches 1 to its capacity at once, rebuilds one in at most a day and its drones carry at most 10,000 t.");
        return new BaySpec(capacity, rate, rebuild, quality, carry);
    }
}

/// <summary>
/// The figures drones fly and are kept by, from <c>content/blocks/drones.json</c>: how many a player may have out,
/// how they leave a hatch, where they hold, and how they come home. The server decides with them; a client reads
/// them only to show the same limits.
/// </summary>
public sealed record DroneRules
{
    /// <summary>Drones one player may have out at once, across every bay.</summary>
    public required int MaxPerPlayer { get; init; }

    /// <summary>Hull slots below the 255-hull ceiling a launch leaves free for ships, wrecks and escape drones.</summary>
    public required int HullReserve { get; init; }

    /// <summary>Seconds between two launches from one bay; each launches up to its rate.</summary>
    public required float LaunchIntervalSeconds { get; init; }

    /// <summary>Metres a second a drone leaves its hatch with, on top of the hull's own motion there.</summary>
    public required float LaunchSpeed { get; init; }

    /// <summary>How far in front of a hatch must be free of the hull's own blocks and mounts for a drone to pass.</summary>
    public required float HatchClearanceMetres { get; init; }

    /// <summary>Gap between a drone leaving and the hatch's face, and between drones leaving together.</summary>
    public required float HatchMarginMetres { get; init; }

    /// <summary>How far out from its hatch a drone holds when the player names no point.</summary>
    public required float HoldDistanceMetres { get; init; }

    /// <summary>Spacing of the drones that hold one point.</summary>
    public required float HoldSpacingMetres { get; init; }

    /// <summary>The farthest from the hull a player may ask drones to hold.</summary>
    public required float MaxHoldDistanceMetres { get; init; }

    /// <summary>How far a holding drone may drift from its place before it is flown back.</summary>
    public required float HoldSlackMetres { get; init; }

    /// <summary>How far out from its hatch a returning drone lines up before it flies straight in.</summary>
    public required float StagingMetres { get; init; }

    /// <summary>How near the staging point counts as lined up.</summary>
    public required float StagingRadiusMetres { get; init; }

    /// <summary>How near its docking point (just outside the hatch) a drone is taken in.</summary>
    public required float DockRadiusMetres { get; init; }

    /// <summary>How far behind the hatch a returning drone aims, so the autopilot never stops short of it.</summary>
    public required float DockPushMetres { get; init; }

    /// <summary>The least time between two drone commands of one player.</summary>
    public required float CommandSeconds { get; init; }

    /// <summary>The farthest from the hull a free container may be for drones to be sent to fetch it.</summary>
    public required float FetchRangeMetres { get; init; }

    /// <summary>
    /// Metres a second squared drones give a container whose mass is just what they carry together: more drones than
    /// needed lift it faster, as their carry adds.
    /// </summary>
    public required float CarryAcceleration { get; init; }

    /// <summary>The fastest drones carry a container, relative to the hull they bring it to.</summary>
    public required float CarrySpeed { get; init; }

    /// <summary>
    /// The share of the weakest push of the drones holding a body that its carry may accelerate it at, however light it
    /// is: faster than its drones can follow, a body flies off without them and overshoots the hand-over.
    /// </summary>
    public required float CarryFollowShare { get; init; }

    /// <summary>How far apart drones of one hull keep while they fly to a place, so two never fly side by side touching.</summary>
    public required float SeparationMetres { get; init; }

    /// <summary>The speed, in metres a second, at which two drones in the same spot are pushed apart; less as they part.</summary>
    public required float SeparationSpeed { get; init; }

    /// <summary>How many seconds ahead two drones' courses are looked at for keeping them apart.</summary>
    public required float SeparationSeconds { get; init; }

    /// <summary>
    /// Room kept between a drone, or a body it carries, and the sphere that holds its own hull: a way that would cross
    /// it goes round instead (<see cref="DroneSteering.Around"/>).
    /// </summary>
    public required float ClearanceMetres { get; init; }

    /// <summary>Gap between a carrying drone and the container's side.</summary>
    public required float AttachGapMetres { get; init; }

    /// <summary>How near its place beside the container a drone counts as holding on.</summary>
    public required float TetherMetres { get; init; }

    /// <summary>The most drones one container takes: there are only so many places around it.</summary>
    public required int MaxCarriers { get; init; }

    /// <summary>How far out in front of the tractor beam drones bring a container and hand it over.</summary>
    public required float HandoverMetres { get; init; }

    /// <summary>How near the hand-over point, and how slow against the hull, a container is let go to the beam.</summary>
    public required float HandoverToleranceMetres { get; init; }
    public required float HandoverSpeed { get; init; }

    /// <summary>The longest a fetch may take before its drones give up and come home.</summary>
    public required float FetchSeconds { get; init; }

    /// <summary>
    /// How many loose containers a hull with auto pickup on looks at in one tick, from a rotating start, so every
    /// body of the 256-body pool is seen in at most a few seconds and no tick pays for all of them.
    /// </summary>
    public required int AutoPickupWindow { get; init; }

    /// <summary>
    /// Fetches one hull may have under way at once, containers and ore chunks together, so a hull never sends its whole
    /// bay after dust. Auto pickup starts no fetch beyond it; a manual fetch still may.
    /// </summary>
    public required int AutoPickupMaxFetches { get; init; }

    /// <summary>Seconds a container that a fetch was refused for is left alone by one hull's auto pickup.</summary>
    public required float AutoPickupRetrySeconds { get; init; }

    /// <summary>Seconds auto pickup waits after its owner recalls the drones by hand, so a recall means something.</summary>
    public required float AutoPickupPauseSeconds { get; init; }

    /// <summary>
    /// Whether work drones may haul loose ore chunks (<see cref="OrderTask.FetchOre"/>). Off in the shipped content: drones
    /// on an ore fetch rammed their own ship and made it jitter (docs/roadmap/mining-rework.md). Tractor beams still take
    /// chunks; a saved or sent "pick up ores" flag stays valid and is ignored while this is off.
    /// </summary>
    public required bool OreFetch { get; init; }

    /// <summary>How drones scan rocks: range, standoff, travel and the two scan levels.</summary>
    public required DroneScanRules Scans { get; init; }

    /// <summary>Ticks a refused container is skipped, at least one.</summary>
    public long AutoPickupRetryTicks => Math.Max(1, (long)Math.Ceiling(AutoPickupRetrySeconds * TickLoop.SimulationHz));

    /// <summary>Ticks auto pickup waits after a manual recall.</summary>
    public long AutoPickupPauseTicks => (long)Math.Ceiling(AutoPickupPauseSeconds * TickLoop.SimulationHz);

    /// <summary>Ticks a fetch may take, at least one.</summary>
    public long FetchTicks => Math.Max(1, (long)Math.Ceiling(FetchSeconds * TickLoop.SimulationHz));

    /// <summary>Ticks between two launches from one bay, at least one.</summary>
    public long LaunchIntervalTicks => Math.Max(1, (long)Math.Ceiling(LaunchIntervalSeconds * TickLoop.SimulationHz));

    /// <summary>Ticks between two commands of one player, at least one.</summary>
    public long CommandTicks => Math.Max(1, (long)Math.Ceiling(CommandSeconds * TickLoop.SimulationHz));

    /// <summary>Reads the file's bytes; a figure out of its range refuses the content.</summary>
    public static DroneRules Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        float Number(string key, float low, float high)
        {
            var value = root.GetProperty(key).GetSingle();
            if (!float.IsFinite(value) || value < low || value > high)
                throw new InvalidDataException($"drones.json: {key} must be from {low} to {high}");
            return value;
        }
        int Whole(string key, int low, int high)
        {
            var value = root.GetProperty(key).GetInt32();
            if (value < low || value > high)
                throw new InvalidDataException($"drones.json: {key} must be from {low} to {high}");
            return value;
        }
        var rules = new DroneRules
        {
            MaxPerPlayer = Whole("maxPerPlayer", 0, DroneBayRules.MaxOrders),
            HullReserve = Whole("hullReserve", 0, DroneBayRules.HullCeiling),
            LaunchIntervalSeconds = Number("launchIntervalSeconds", 0, 60),
            LaunchSpeed = Number("launchSpeed", 0, 50),
            HatchClearanceMetres = Number("hatchClearanceMetres", 0, 100),
            HatchMarginMetres = Number("hatchMarginMetres", 0, 10),
            HoldDistanceMetres = Number("holdDistanceMetres", 1, 1000),
            HoldSpacingMetres = Number("holdSpacingMetres", 0.5f, 100),
            MaxHoldDistanceMetres = Number("maxHoldDistanceMetres", 1, 5000),
            HoldSlackMetres = Number("holdSlackMetres", 0.1f, 100),
            StagingMetres = Number("stagingMetres", 1, 100),
            StagingRadiusMetres = Number("stagingRadiusMetres", 0.1f, 100),
            DockRadiusMetres = Number("dockRadiusMetres", 0.05f, 10),
            DockPushMetres = Number("dockPushMetres", 0, 50),
            CommandSeconds = Number("commandSeconds", 0, 10),
            FetchRangeMetres = Number("fetchRangeMetres", 1, 20000),
            CarryAcceleration = Number("carryAcceleration", 0.01f, 50),
            CarrySpeed = Number("carrySpeed", 0.1f, 200),
            CarryFollowShare = Number("carryFollowShare", 0.05f, 1),
            SeparationMetres = Number("separationMetres", 0, 50),
            SeparationSpeed = Number("separationSpeed", 0, 50),
            SeparationSeconds = Number("separationSeconds", 0, 10),
            ClearanceMetres = Number("clearanceMetres", 0, 100),
            AttachGapMetres = Number("attachGapMetres", 0, 10),
            TetherMetres = Number("tetherMetres", 0.1f, 100),
            MaxCarriers = Whole("maxCarriers", 1, DroneFetch.Places),
            HandoverMetres = Number("handoverMetres", 1, 1000),
            HandoverToleranceMetres = Number("handoverToleranceMetres", 0.05f, 50),
            HandoverSpeed = Number("handoverSpeed", 0.01f, 50),
            FetchSeconds = Number("fetchSeconds", 1, 86400),
            AutoPickupWindow = Whole("autoPickupWindow", 1, FreeContainers.Capacity),
            AutoPickupMaxFetches = Whole("autoPickupMaxFetches", 1, DroneBayRules.MaxFetches),
            AutoPickupRetrySeconds = Number("autoPickupRetrySeconds", 1, 3600),
            AutoPickupPauseSeconds = Number("autoPickupPauseSeconds", 0, 3600),
            OreFetch = root.GetProperty("oreFetch"u8).ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException("drones.json: oreFetch must be true or false"),
            },
            Scans = DroneScanRules.Read(root.GetProperty("scans"u8), root.GetProperty("maxPerPlayer"u8).GetInt32()),
        };
        if (rules.HoldDistanceMetres > rules.MaxHoldDistanceMetres)
            throw new InvalidDataException("drones.json: holdDistanceMetres must not exceed maxHoldDistanceMetres");
        return rules;
    }
}

/// <summary>
/// The shared geometry of drone bays: where a hatch is, whether it faces open space, where drones leave, line up
/// and dock, and where each drone of a group holds. Points are in the hull's own frame, in metres from its pivot
/// (<see cref="HullDesign.BlockOffsets"/>), unless named otherwise. Takes no memory.
/// </summary>
public static class DroneBayRules
{
    /// <summary>The most bays a design may have: the wire tells the pilot of every one.</summary>
    public const int MaxBays = 16;

    /// <summary>The most drones one bay keeps, whatever its tier.</summary>
    public const int MaxCapacity = 32;

    /// <summary>Hulls a world holds before nothing more is launched or wrecked: a snapshot names at most this many.</summary>
    public const int HullCeiling = 255;

    /// <summary>Orders the server can hold at once: one per hull below the ceiling.</summary>
    public const int MaxOrders = 256;

    /// <summary>Fetches one hull may have under way at once, each with its own launches: the server's fixed storage.</summary>
    public const int MaxFetches = 8;

    /// <summary>The way a bay's hatch faces: the block's facing, chosen when it was placed.</summary>
    public static BlockFacing Hatch(HullDesign design, int block) => design.Boxes[block].Facing;

    /// <summary>The unit vector out of a face, in the hull's frame.</summary>
    public static Vector3 Normal(BlockFacing face) => Facings.Vector(face);

    /// <summary>The middle of a bay's hatch, on the block's face.</summary>
    public static Vector3 HatchPoint(HullDesign design, int block)
    {
        var face = Hatch(design, block);
        var axis = Facings.Axis(face);
        var point = design.BlockOffsets[block];
        point[axis] += Facings.Sign(face) * design.BlockHalfExtents[block][axis];
        return point;
    }

    /// <summary>
    /// Whether a bay's hatch faces open space: nothing of the hull it still has stands in the box that runs
    /// <paramref name="clearance"/> metres out from the hatch's face, as wide as the face, and no part is mounted on
    /// the hatch itself. Wedges count by their bounding boxes. Asks the design's collision tree, so a large hull
    /// costs little; false once <paramref name="visits"/> tree nodes have been looked at without an answer.
    /// </summary>
    public static bool HatchClear(HullDesign design, ReadOnlySpan<ulong> alive, int block, float clearance, int visits)
    {
        var face = Hatch(design, block);
        design.MountsOf(block, out var start, out var end);
        var mounts = design.Mounts;
        for (var i = start; i < end; i++)
            if (mounts[i].Face == face) return false;
        if (clearance <= 0) return true;
        var axis = Facings.Axis(face);
        var sign = Facings.Sign(face);
        var centre = design.BlockOffsets[block];
        var half = design.BlockHalfExtents[block];
        const float inset = 1e-3f;
        var low = centre - half + new Vector3(inset);
        var high = centre + half - new Vector3(inset);
        var plane = centre[axis] + sign * half[axis];
        if (sign > 0) { low[axis] = plane + inset; high[axis] = plane + clearance; }
        else { low[axis] = plane - clearance; high[axis] = plane - inset; }
        var cursor = 0;
        while (design.Collision.Next(ref cursor, low, high, alive, ref visits, out var other))
        {
            if (other == block) continue;
            var c = design.BlockOffsets[other];
            var h = design.BlockHalfExtents[other];
            if (c.X - h.X < high.X && c.X + h.X > low.X && c.Y - h.Y < high.Y && c.Y + h.Y > low.Y && c.Z - h.Z < high.Z && c.Z + h.Z > low.Z)
                return false;
        }
        if (visits <= 0) return false;

        // A part mounted on another block's face can stand out into the way, as a block can.
        for (var i = 0; i < mounts.Length; i++)
        {
            if (!BlockSet.Has(alive, mounts[i].Parent)) continue;
            var c = design.MountOffsets[i];
            var h = design.MountHalfExtents[i];
            if (c.X - h.X < high.X && c.X + h.X > low.X && c.Y - h.Y < high.Y && c.Y + h.Y > low.Y && c.Z - h.Z < high.Z && c.Z + h.Z > low.Z)
                return false;
        }
        return true;
    }

    /// <summary>Where the <paramref name="order"/>-th drone of one launch appears: in front of the hatch, one behind another.</summary>
    public static Vector3 LaunchPoint(HullDesign design, int block, float droneRadius, float margin, int order) =>
        HatchPoint(design, block) + Normal(Hatch(design, block)) * (droneRadius + margin + order * (2 * droneRadius + margin));

    /// <summary>Where a returning drone lines up: straight out from the hatch.</summary>
    public static Vector3 StagingPoint(HullDesign design, int block, float distance) =>
        HatchPoint(design, block) + Normal(Hatch(design, block)) * distance;

    /// <summary>Where a drone is taken in: just outside the hatch, by its own radius.</summary>
    public static Vector3 DockPoint(HullDesign design, int block, float droneRadius) =>
        HatchPoint(design, block) + Normal(Hatch(design, block)) * droneRadius;

    /// <summary>Where a bay's drones hold when the player names no point: straight out from its hatch.</summary>
    public static Vector3 DefaultHold(HullDesign design, int block, float distance) => StagingPoint(design, block, distance);

    // A group's places around its point, nearest first: a five by five grid in the plane across the hatch, then the
    // same again a layer out. Index n of a group takes entry n modulo the table's length, a layer per lap.
    private static ReadOnlySpan<sbyte> Grid =>
    [
        0, 0, 1, 0, -1, 0, 0, 1, 0, -1, 1, 1, -1, -1, 1, -1, -1, 1,
        2, 0, -2, 0, 0, 2, 0, -2, 2, 1, -2, -1, 1, 2, -1, -2, 2, -1, -2, 1, -1, 2, 1, -2,
        2, 2, -2, -2, 2, -2, -2, 2,
    ];

    /// <summary>
    /// The place of the <paramref name="slot"/>-th drone of a group holding <paramref name="point"/>, spread
    /// <paramref name="spacing"/> apart in the plane across <paramref name="face"/>, a layer out per 25 drones.
    /// </summary>
    public static Vector3 HoldSlot(Vector3 point, BlockFacing face, int slot, float spacing)
    {
        var count = Grid.Length / 2;
        var entry = slot % count;
        var layer = slot / count;
        var axis = Facings.Axis(face);
        var u = Vector3.Zero; var v = Vector3.Zero;
        u[(axis + 1) % 3] = 1; v[(axis + 2) % 3] = 1;
        return point + (u * Grid[2 * entry] + v * Grid[2 * entry + 1]) * spacing + Normal(face) * (layer * spacing);
    }
}
