using System.Numerics;
using System.Text.Json;
using Prospect.Engine.Time;

namespace Prospect.Game.World;

/// <summary>
/// The figures of mining with a laser, read once from <c>content/rules/mining.json</c>: how a beam turns into
/// scrapes, how the ore it frees adds up into chunks, and how loose chunks behave. Shared content: the server cuts
/// and spawns with them, and a client shows the same limits.
/// </summary>
public sealed class MiningRules
{
    /// <summary>How deep under the surface the beam finds one scrape cuts, in metres.</summary>
    public float BiteMetres { get; private init; }

    /// <summary>Least ticks between two scrapes of one beam.</summary>
    public int EditIntervalTicks { get; private init; }

    /// <summary>A beam whose last scrape is older than this, in ticks, starts a new stroke.</summary>
    public int StrokeGapTicks { get; private init; }

    /// <summary>A beam scrapes again early once its spot has moved this many of its cut radii.</summary>
    public float MoveRadii { get; private init; }

    /// <summary>Lasers one hull fires at once at most, in mount order.</summary>
    public int MaxLasersPerHull { get; private init; }

    /// <summary>Kilograms of one ore a laser frees before a chunk of it breaks off.</summary>
    public float ChunkOreKg { get; private init; }

    /// <summary>Density a chunk's size is worked out from, in kg a cubic metre.</summary>
    public float ChunkDensity { get; private init; }

    /// <summary>Speed a chunk leaves the cut with, along the rock's surface normal, in m/s.</summary>
    public float ChunkSpeed { get; private init; }

    /// <summary>Spin a new chunk is given, in radians a second.</summary>
    public float ChunkSpin { get; private init; }

    /// <summary>Loose chunks one sector holds; the oldest goes first.</summary>
    public int ChunksPerSector { get; private init; }

    /// <summary>Unattended ticks a chunk lasts; zero never ends one.</summary>
    public long ChunkLifetimeTicks { get; private init; }

    /// <summary>How much of a chunk's closing speed survives a bounce.</summary>
    public float ChunkRestitution { get; private init; }

    /// <summary>Contact tests the chunks may take a tick, over all of them.</summary>
    public int ChunkContactBudget { get; private init; }

    /// <summary>Farthest the camera a client aims from may be from its hull, in metres; a farther aim is refused.</summary>
    public float MaxAimOffsetMetres { get; private init; }

    /// <summary>Reads and validates the file. Throws <see cref="InvalidDataException"/> for a figure out of range.</summary>
    public static MiningRules Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var laser = root.GetProperty("laser"u8);
        var chunks = root.GetProperty("chunks"u8);
        float Figure(JsonElement e, ReadOnlySpan<byte> name, float least, float most)
        {
            var value = e.GetProperty(name).GetSingle();
            if (!float.IsFinite(value) || value < least || value > most)
                throw new InvalidDataException($"mining.json: {System.Text.Encoding.UTF8.GetString(name)} must be from {least} to {most}.");
            return value;
        }

        static int Ticks(float seconds) => Math.Max(1, (int)MathF.Ceiling(seconds * TickLoop.SimulationHz));
        var perHull = (int)Figure(laser, "maxPerHull"u8, 1, 64);
        var perSector = (int)Figure(chunks, "perSector"u8, 1, OreChunks.Capacity);
        var budget = (int)Figure(chunks, "contactBudget"u8, 1, 4096);
        var lifetime = Figure(chunks, "lifetimeMinutes"u8, 0, 24 * 60);
        return new MiningRules
        {
            BiteMetres = Figure(laser, "biteMetres"u8, 0.05f, 4),
            EditIntervalTicks = Ticks(Figure(laser, "editSeconds"u8, 1f / TickLoop.SimulationHz, 10)),
            StrokeGapTicks = Ticks(Figure(laser, "strokeGapSeconds"u8, 1f / TickLoop.SimulationHz, 10)),
            MoveRadii = Figure(laser, "moveRadii"u8, 0.1f, 8),
            MaxLasersPerHull = perHull,
            MaxAimOffsetMetres = Figure(laser, "maxAimOffsetMetres"u8, 1, 10_000),
            ChunkOreKg = Figure(chunks, "oreKg"u8, 1, 1_000_000),
            ChunkDensity = Figure(chunks, "densityKgPerCubicMetre"u8, 100, 30_000),
            ChunkSpeed = Figure(chunks, "speed"u8, 0, 50),
            ChunkSpin = Figure(chunks, "spin"u8, 0, 20),
            ChunksPerSector = perSector,
            ChunkLifetimeTicks = (long)Math.Round(lifetime * 60.0 * TickLoop.SimulationHz),
            ChunkRestitution = Figure(chunks, "restitution"u8, 0, 1),
            ChunkContactBudget = budget,
        };
    }

    /// <summary>
    /// Kilograms of rock one scrape of radius <paramref name="radius"/> takes out when its spot moved
    /// <paramref name="length"/> metres since the last: the capsule's cross-section times the bite. An estimate,
    /// worked out on the server alone; nothing discrete in a client hangs on it.
    /// </summary>
    public double ScrapeKg(float radius, float length, float rockDensityTonnes) =>
        (Math.PI * radius * radius + 2.0 * radius * length) * BiteMetres * rockDensityTonnes * 1000.0;
}

/// <summary>
/// A loose chunk of ore a mining laser broke off a rock. Server-owned, in a fixed pool of its own
/// (<see cref="OreChunks"/>), replicated and saved: its resource, grade and mass. A tractor beam takes it to an ore
/// hopper's intake, or drones bring it to the beam; the claim of either is transient and starts over on a load.
/// </summary>
public struct OreChunk
{
    /// <summary>Durable identity, never reused within a world.</summary>
    public uint Id;
    public Double3 Position;
    public Vector3 Velocity, AngularVelocity;
    public Quaternion Orientation;

    /// <summary>The galaxy's index of the ore it holds.</summary>
    public int Resource;

    /// <summary>The share of <see cref="Mass"/> that is that ore, above 0 and at most 1; the rest is rock.</summary>
    public float Grade;

    /// <summary>The whole chunk's mass in kilograms.</summary>
    public float Mass;

    /// <summary>Tick it broke off and unattended ticks since, as free containers keep them.</summary>
    public long Born, Age;

    /// <summary>
    /// Transient exclusive tractor claim: the hull (packed handle, zero for none), its tractor mount and the hopper
    /// block the chunk is led to. Sent, so a client predicts the beam's power and draws it; a load starts over.
    /// </summary>
    public uint TractorHull;
    public int TractorMount, TractorHopper;

    /// <summary>0 while travelling to the point outside the intake, 1 on the glide into it.</summary>
    public byte TractorStage;
    public bool TractorPowered;

    /// <summary>Server-only, never sent or saved: the least path left so far (0 = not set) and the ticks without gaining on it.</summary>
    public float TractorBest;
    public int TractorIdle;

    /// <summary>
    /// Server-only, never sent or saved: the leg the beam last pulled the chunk along (0 none yet, 1 to the point
    /// outside the intake, 2 by way of the emitter's exit, 3 the glide in). A change of leg sets
    /// <see cref="TractorBest"/> to the new leg's length; nothing else may raise it, so a chunk that only jitters on one
    /// leg still reaches the stall.
    /// </summary>
    public byte TractorRoute;

    /// <summary>Server-only, never sent or saved: the first tick a tractor may take the chunk again after one let it go.</summary>
    public long RetryTick;

    /// <summary>
    /// Server-only, never sent or saved: the hull whose drones are fetching the chunk (packed handle, zero for none)
    /// and the tractor mount they hand it to. While set, no tractor acquires it and no other drones are sent for it.
    /// </summary>
    public uint FetchHull;
    public int FetchMount;

    /// <summary>Kilograms of ore in it.</summary>
    public readonly float OreKg => Mass * Grade;
}

/// <summary>
/// Fixed storage for loose ore chunks, apart from the containers' pool: chunks carry no cargo payload and no
/// player's claim, and go to an ore hopper, never a rack. Stable ids are never reused within a world. Takes no memory
/// after construction.
/// </summary>
public sealed class OreChunks
{
    /// <summary>Storage and replication ceiling.</summary>
    public const int Capacity = 256;

    private readonly OreChunk[] _items = new OreChunk[Capacity];
    private int _count;
    private uint _next = 1;

    /// <summary>The occupied prefix, in insertion order.</summary>
    public ReadOnlySpan<OreChunk> Items => _items.AsSpan(0, _count);

    /// <summary>The next durable identity; zero means identity space is exhausted.</summary>
    public uint NextId => _next;

    /// <summary>Mutable access for the authoritative simulation only.</summary>
    public ref OreChunk At(int index) => ref _items.AsSpan(0, _count)[index];

    /// <summary>Half the edge of a chunk's cube, from its mass and the content's density.</summary>
    public static float Half(MiningRules rules, float mass) => 0.5f * MathF.Cbrt(Math.Max(mass, 0.001f) / rules.ChunkDensity);

    /// <summary>Validates, then evicts the oldest chunk of a full sector or a full pool, then adds.</summary>
    public bool Add(GameContent content, in OreChunk item, out uint id)
    {
        id = 0;
        if (_next == 0 || !Valid(content, item) || item.Born < 0 || item.Age < 0) return false;
        var sector = content.Galaxy.SectorOf(item.Position);
        var count = 0;
        var oldest = -1;
        for (var i = 0; i < _count; i++)
            if (content.Galaxy.SectorOf(_items[i].Position) == sector)
            {
                count++;
                if (oldest < 0 || Older(_items[i], _items[oldest])) oldest = i;
            }
        if (count >= content.Mining.ChunksPerSector) Remove(oldest);
        if (_count == Capacity)
        {
            oldest = 0;
            for (var i = 1; i < _count; i++) if (Older(_items[i], _items[oldest])) oldest = i;
            Remove(oldest);
        }
        _items[_count] = item;
        id = _items[_count++].Id = _next++;
        return true;
    }

    /// <summary>Restores a validated snapshot, keeping identities and the counter.</summary>
    public void Restore(ReadOnlySpan<OreChunk> items, uint next)
    {
        if (items.Length > Capacity) throw new InvalidDataException("Too many ore chunks.");
        items.CopyTo(_items);
        _items.AsSpan(items.Length, _count > items.Length ? _count - items.Length : 0).Clear();
        _count = items.Length;
        _next = next;
        for (var i = 0; i < _count; i++)
        {
            ClearTractor(ref _items[i]);
            ClearFetch(ref _items[i]);
            _items[i].RetryTick = 0;
        }
    }

    /// <summary>Drops a chunk's tractor claim: on a load, when the beam lets go, or for a client that cannot see the pulling hull.</summary>
    public static void ClearTractor(ref OreChunk item)
    { item.TractorHull = 0; item.TractorMount = 0; item.TractorHopper = 0; item.TractorStage = 0; item.TractorPowered = false; item.TractorBest = 0; item.TractorIdle = 0; item.TractorRoute = 0; }

    /// <summary>Lets go of a chunk drones were fetching: it stays where it is, with the motion it has.</summary>
    public static void ClearFetch(ref OreChunk item) { item.FetchHull = 0; item.FetchMount = 0; }

    /// <summary>Removes one chunk without changing any surviving identity.</summary>
    public void Remove(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count);
        _items.AsSpan(index + 1, _count - index - 1).CopyTo(_items.AsSpan(index));
        _items[--_count] = default;
    }

    /// <summary>Finds a stable identity, or -1 when it is absent.</summary>
    public int Find(uint id)
    {
        for (var i = 0; i < _count; i++) if (_items[i].Id == id) return i;
        return -1;
    }

    /// <summary>Checks physical state and payload before admission, decoding or restoration.</summary>
    public static bool Valid(GameContent content, in OreChunk item) =>
        double.IsFinite(item.Position.X) && double.IsFinite(item.Position.Y) && double.IsFinite(item.Position.Z)
        && Finite(item.Velocity) && Finite(item.AngularVelocity)
        && float.IsFinite(item.Orientation.LengthSquared()) && Math.Abs(item.Orientation.LengthSquared() - 1) < .001f
        && (uint)item.Resource < (uint)content.Galaxy.Resources.Length
        && float.IsFinite(item.Grade) && item.Grade > 0 && item.Grade <= 1
        && float.IsFinite(item.Mass) && item.Mass > 0 && item.Mass <= 1e7f
        && item.Born >= 0 && item.Age >= 0;

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.LengthSquared());

    private static bool Older(in OreChunk a, in OreChunk b) => a.Born < b.Born || a.Born == b.Born && a.Id < b.Id;

    /// <summary>Advances every chunk by one fixed interval.</summary>
    public void Drift()
    {
        for (var i = 0; i < _count; i++)
        {
            ref var c = ref _items[i];
            c.Position += c.Velocity * HullSim.Dt;
            var speed = c.AngularVelocity.Length();
            if (speed > 0)
                c.Orientation = Quaternion.Normalize(c.Orientation * Quaternion.CreateFromAxisAngle(c.AngularVelocity / speed, speed * HullSim.Dt));
        }
    }

    /// <summary>Enforces sector limits after drift across a boundary, oldest first.</summary>
    public void EnforceCaps(GameContent content)
    {
        for (var i = _count - 1; i >= 0; i--)
        {
            var sector = content.Galaxy.SectorOf(_items[i].Position);
            var newer = 0;
            for (var j = 0; j < _count; j++)
                if (content.Galaxy.SectorOf(_items[j].Position) == sector && Older(_items[i], _items[j])) newer++;
            if (newer >= content.Mining.ChunksPerSector) Remove(i);
        }
    }

    /// <summary>Accumulates unattended ticks; a player near pauses a chunk's lifetime, as for free containers.</summary>
    public void Cleanup(long lifetime, ReadOnlySpan<Double3> players, double radius)
    {
        for (var i = _count - 1; i >= 0; i--)
        {
            ref var c = ref _items[i];
            var near = false;
            foreach (var player in players)
            {
                var d = c.Position - player;
                if (d.X * d.X + d.Y * d.Y + d.Z * d.Z <= radius * radius) { near = true; break; }
            }
            if (!near) c.Age++;
            if (lifetime > 0 && c.Age >= lifetime) Remove(i);
        }
    }
}

/// <summary>
/// Bounded, allocation-free contacts of loose ore chunks with hull blocks and rocks, on the shared box and rock
/// contact code (<see cref="BoxCollision"/>). A chunk bounces and pushes what it hits by its share of the mass; it
/// never takes or deals damage.
/// </summary>
public sealed class OreChunkSimulation
{
    private readonly SectorContent _sector = new();
    private bool _hasSector;
    private SectorCoord _coord;
    private int _first;

    /// <summary>Reserves generation buffers before the server starts ticking.</summary>
    public OreChunkSimulation(Galaxy galaxy) => galaxy.Reserve(_sector);

    /// <summary>Advances the chunks and settles a capped set of contacts, in rotating order.</summary>
    public void Step(OreChunks chunks, GameContent content, HullField field, RockField rocks, long tick)
    {
        if (chunks.Items.IsEmpty) return;
        chunks.Drift();
        var rules = content.Mining;
        var budget = rules.ChunkContactBudget;
        var count = chunks.Items.Length;
        var first = _first % count;
        _first = (first + 1) % count;
        var sector = rocks.Galaxy.SectorOf(chunks.Items[first].Position);
        if (!_hasSector || sector != _coord)
        {
            rocks.Galaxy.Generate(sector, _sector);
            _coord = sector;
            _hasSector = true;
        }

        for (var n = 0; n < count && budget > 0; n++)
        {
            ref var c = ref chunks.At((first + n) % count);
            var half = new Vector3(OreChunks.Half(rules, c.Mass));
            for (var h = 0; h < field.Hulls.Slots && budget > 0; h++)
            {
                if (!field.Hulls.IsAliveAt(h)) continue;
                // A chunk the hull's own tractor is leading into a hopper passes the hull's blocks, as a held
                // container does; drones carrying one never knock it away. Rocks and other hulls still hit it.
                if (c.TractorPowered && c.TractorHull == field.Hulls.HandleAt(h).Packed) continue;
                if (c.FetchHull != 0 && field.Hulls.Info(h).WorkDrone) continue;
                ref var state = ref field.Hulls.State(h);
                if (state.Jump == JumpPhase.Tunnel) continue;
                var design = field.Designs[field.Hulls.Info(h).Design];
                var inverse = Quaternion.Conjugate(state.Orientation);
                var local = HullRotation.Rotate(c.Position.RelativeTo(state.Position), inverse);
                var extent = BoxCollision.Bounds(half, inverse * c.Orientation);
                var cursor = 0;
                while (design.Collision.Next(ref cursor, local - extent, local + extent, field.Hulls.Blocks(h), ref budget, out var b))
                {
                    var position = state.Position + HullRotation.Rotate(design.BlockOffsets[b], state.Orientation);
                    var between = HullRotation.Rotate(c.Position.RelativeTo(position), inverse);
                    if (!BoxCollision.Contact(between, half, inverse * c.Orientation, design.BlockHalfExtents[b],
                        BlockShape.Box, design.Boxes[b].Shape, out var normalLocal, out var depth)) continue;
                    var normal = HullRotation.Rotate(normalLocal, state.Orientation);
                    c.Position += normal * depth;
                    var closing = -Vector3.Dot(c.Velocity - state.Velocity, normal);
                    if (closing > 0)
                    {
                        ref var flight = ref field.Hulls.Flight(h);
                        var tonnes = c.Mass / 1000f;
                        var fraction = flight.Mass / (flight.Mass + tonnes);
                        var change = normal * (closing * (1 + rules.ChunkRestitution) * fraction);
                        c.Velocity += change;
                        state.Velocity -= change * (tonnes / flight.Mass);
                    }
                    break;
                }
            }

            if (rocks.Galaxy.SectorOf(c.Position) != _coord) continue;
            var origin = rocks.Galaxy.CenterOf(_coord);
            for (var r = 0; r < _sector.AsteroidCount && budget > 0; r++)
            {
                ref var rock = ref _sector.Asteroids[r];
                rocks.Displacement(new(_coord, r), tick, out var offset, out var velocity);
                var position = origin + rock.LocalPosition + offset;
                var reach = rock.Radius + half.X * 1.75f;
                if (c.Position.RelativeTo(position).LengthSquared() > reach * reach) continue;
                budget--;
                if (!BoxCollision.Rock(c.Position.RelativeTo(position), half, c.Orientation, rock, out var normal, out var depth, out _,
                    BlockShape.Box, rocks.Edits is { } edits ? edits.CutOf(new RockRef(_coord, r)) : default)) continue;
                c.Position += normal * depth;
                var closing = -Vector3.Dot(c.Velocity - velocity, normal);
                if (closing > 0) c.Velocity += normal * (closing * (1 + rules.ChunkRestitution));
            }
        }

        chunks.EnforceCaps(content);
    }
}
