using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>Which part of a rock's interior a point lies in.</summary>
public enum RockLayer : byte
{
    /// <summary>The weathered outer shell: rock and low-grade ore.</summary>
    Crust,

    /// <summary>The body of the rock below the crust: ore at its normal grade.</summary>
    Mantle,

    /// <summary>A twisting tube of high-grade ore through the mantle.</summary>
    Vein,

    /// <summary>A pocket: a rare mix-in near the core, or an unstable pocket of ice or volatile gas.</summary>
    Pocket,
}

/// <summary>What the body at a point is made of, apart from the ore in it.</summary>
public enum RockMaterial : byte
{
    Rock,
    Ice,

    /// <summary>Volatile gas held under pressure. Unstable.</summary>
    Volatile,
}

/// <summary>What fills a pocket.</summary>
public enum RockPocketKind : byte
{
    /// <summary>The rock's rarer mix-in, rich: the core pocket.</summary>
    Ore,

    /// <summary>Ice. Unstable: it may blow when cracked.</summary>
    Ice,

    /// <summary>Volatile gas. Unstable: it may blow when cracked.</summary>
    Volatile,
}

/// <summary>What is at one point of a rock's interior.</summary>
public struct RockSample
{
    public RockLayer Layer;
    public RockMaterial Material;

    /// <summary>For a point in a pocket, what fills it.</summary>
    public RockPocketKind Pocket;

    /// <summary>Whether the point is inside the rock. A point outside it is given the crust under it.</summary>
    public bool Inside;

    /// <summary>The point's distance from the centre as a fraction of the surface's in that direction: 1 is on it.</summary>
    public float Depth;

    /// <summary>The share of the point that is the rock's main ore, <see cref="Asteroid.Resource"/>.</summary>
    public float MainShare;

    /// <summary>The share that is <see cref="Asteroid.Mix1"/>; zero without one.</summary>
    public float Mix1Share;

    /// <summary>The share that is <see cref="Asteroid.Mix2"/>; zero without one.</summary>
    public float Mix2Share;

    /// <summary>The ore with the largest share at the point, or -1 where there is none.</summary>
    public int Resource;

    /// <summary>The share of <see cref="Resource"/> at the point, from 0 to 1.</summary>
    public float Grade;

    /// <summary>A pocket that can blow when cracked: ice or volatile gas.</summary>
    public readonly bool Unstable => Layer == RockLayer.Pocket && Pocket != RockPocketKind.Ore;

    /// <summary>All ore at the point; the rest is rock (or ice or gas).</summary>
    public readonly float Ore => MainShare + Mix1Share + Mix2Share;
}

/// <summary>What a small volume of a rock holds: the average of a lattice of samples inside it.</summary>
public struct RockVolume
{
    /// <summary>How many lattice points lay inside the rock; the figures below are over these.</summary>
    public int Samples;

    public float Crust;
    public float Mantle;
    public float Vein;
    public float Pocket;

    /// <summary>The share of the samples in an unstable pocket.</summary>
    public float Unstable;

    public float MainShare;
    public float Mix1Share;
    public float Mix2Share;
}

/// <summary>
/// The figures that shape every rock's interior, from <c>content/galaxy/interiors.json</c>. Shared content: both
/// sides must describe the same interior. Lengths are fractions of the rock's own size (the surface's distance
/// from the centre in each direction), except where they say metres.
/// </summary>
public sealed class RockInteriorRules
{
    /// <summary>The most veins a rock can have.</summary>
    public const int MaxVeins = 8;

    /// <summary>The most points along a vein: one more than its segments.</summary>
    public const int MaxVeinPoints = 16;

    /// <summary>The most pockets a rock can have: the core pocket and the unstable ones.</summary>
    public const int MaxPockets = 4;

    /// <summary>The crust's depth as a fraction of the surface's distance from the centre.</summary>
    public float CrustDepth { get; private init; }

    /// <summary>The thickest the crust may be in metres, so a giant does not have a crust hundreds of metres deep.</summary>
    public float CrustMaxMetres { get; private init; }

    /// <summary>How much the crust's depth varies over the rock, as a fraction of it.</summary>
    public float CrustWobble { get; private init; }

    /// <summary>The crust's ore as a multiple of the mantle's: weathered, low grade.</summary>
    public float CrustOre { get; private init; }

    public int VeinsMin { get; private init; }
    public int VeinsMax { get; private init; }
    public float VeinRadiusMin { get; private init; }
    public float VeinRadiusMax { get; private init; }

    /// <summary>How much a vein's radius varies along it, as a fraction of it.</summary>
    public float VeinTaper { get; private init; }

    public int VeinSegments { get; private init; }
    public float VeinStepMin { get; private init; }
    public float VeinStepMax { get; private init; }

    /// <summary>How sharply a vein turns at each point: the length of a random push added to its unit heading.</summary>
    public float VeinTwist { get; private init; }

    /// <summary>A vein's ore as a multiple of the mantle's: high grade.</summary>
    public float VeinOre { get; private init; }

    /// <summary>The chance that a rock with a mix-in has a core pocket of its rarer one.</summary>
    public float PocketChance { get; private init; }
    public float PocketRadiusMin { get; private init; }
    public float PocketRadiusMax { get; private init; }

    /// <summary>How far from the centre the core pocket may lie, as a fraction of the mantle's radius.</summary>
    public float PocketOffset { get; private init; }

    /// <summary>The share of the rarer mix-in's mass that the core pocket holds, if its grade allows.</summary>
    public float PocketHolds { get; private init; }

    /// <summary>The chance of each unstable pocket, tried <see cref="UnstableMax"/> times.</summary>
    public float UnstableChance { get; private init; }
    public int UnstableMax { get; private init; }
    public float UnstableRadiusMin { get; private init; }
    public float UnstableRadiusMax { get; private init; }

    /// <summary>The weight of ice against volatile gas when an unstable pocket is filled.</summary>
    public float UnstableIceWeight { get; private init; }
    public float UnstableVolatileWeight { get; private init; }

    /// <summary>The most ore any point may hold. A rich rock's layers are evened out to stay under it.</summary>
    public float MaxLocalOre { get; private init; }

    /// <summary>
    /// How far, as a share of each, the ore integrated over a rock's interior may stray from its published bulk
    /// composition. The interior's layer volumes are worked out, not counted, so they are close, not exact.
    /// </summary>
    public float BulkTolerance { get; private init; }

    public static RockInteriorRules Load(string path) => Parse(File.ReadAllBytes(path), path);

    /// <summary>Reads and checks the rules. <paramref name="path"/> names where they came from in error messages.</summary>
    public static RockInteriorRules Parse(ReadOnlyMemory<byte> json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var crust = root.GetProperty("crust"u8);
        var veins = root.GetProperty("veins"u8);
        var pocket = root.GetProperty("pocket"u8);
        var unstable = root.GetProperty("unstable"u8);
        static (float, float) Pair(JsonElement e) => (e[0].GetSingle(), e[1].GetSingle());
        var (veinsMin, veinsMax) = Pair(veins.GetProperty("count"u8));
        var (veinRadiusMin, veinRadiusMax) = Pair(veins.GetProperty("radius"u8));
        var (stepMin, stepMax) = Pair(veins.GetProperty("step"u8));
        var (pocketMin, pocketMax) = Pair(pocket.GetProperty("radius"u8));
        var (unstableMin, unstableMax) = Pair(unstable.GetProperty("radius"u8));
        var rules = new RockInteriorRules
        {
            CrustDepth = crust.GetProperty("depth"u8).GetSingle(),
            CrustMaxMetres = crust.GetProperty("maxMetres"u8).GetSingle(),
            CrustWobble = crust.GetProperty("wobble"u8).GetSingle(),
            CrustOre = crust.GetProperty("ore"u8).GetSingle(),
            VeinsMin = (int)veinsMin,
            VeinsMax = (int)veinsMax,
            VeinRadiusMin = veinRadiusMin,
            VeinRadiusMax = veinRadiusMax,
            VeinTaper = veins.GetProperty("taper"u8).GetSingle(),
            VeinSegments = veins.GetProperty("segments"u8).GetInt32(),
            VeinStepMin = stepMin,
            VeinStepMax = stepMax,
            VeinTwist = veins.GetProperty("twist"u8).GetSingle(),
            VeinOre = veins.GetProperty("ore"u8).GetSingle(),
            PocketChance = pocket.GetProperty("chance"u8).GetSingle(),
            PocketRadiusMin = pocketMin,
            PocketRadiusMax = pocketMax,
            PocketOffset = pocket.GetProperty("offset"u8).GetSingle(),
            PocketHolds = pocket.GetProperty("holds"u8).GetSingle(),
            UnstableChance = unstable.GetProperty("chance"u8).GetSingle(),
            UnstableMax = unstable.GetProperty("max"u8).GetInt32(),
            UnstableRadiusMin = unstableMin,
            UnstableRadiusMax = unstableMax,
            UnstableIceWeight = unstable.GetProperty("iceWeight"u8).GetSingle(),
            UnstableVolatileWeight = unstable.GetProperty("volatileWeight"u8).GetSingle(),
            MaxLocalOre = root.GetProperty("maxLocalOre"u8).GetSingle(),
            BulkTolerance = root.GetProperty("bulkTolerance"u8).GetSingle(),
        };

        void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidDataException($"{path}: {what}");
        }

        static bool Unit(float v) => v >= 0 && v <= 1;
        Check(rules.CrustDepth is >= 0 and <= 0.5f && rules.CrustWobble is >= 0 and <= 1 && rules.CrustDepth * (1 + rules.CrustWobble) <= 0.6f,
            "crust.depth must be 0 to 0.5, wobble 0 to 1, and depth times (1 + wobble) at most 0.6");
        Check(rules.CrustMaxMetres > 0 && float.IsFinite(rules.CrustMaxMetres), "crust.maxMetres must be positive");
        Check(rules.CrustOre is >= 0 and <= 10 && rules.VeinOre is >= 0 and <= 10, "crust.ore and veins.ore must be 0 to 10");
        Check(veinsMin == (int)veinsMin && veinsMax == (int)veinsMax && rules.VeinsMin >= 0 && rules.VeinsMin <= rules.VeinsMax && rules.VeinsMax <= MaxVeins,
            $"veins.count must be whole, rising, at most {MaxVeins}");
        Check(rules.VeinRadiusMin > 0 && rules.VeinRadiusMin <= rules.VeinRadiusMax && rules.VeinRadiusMax <= 0.2f, "veins.radius must be rising, above 0, at most 0.2");
        Check(Unit(rules.VeinTaper) && rules.VeinTaper < 1, "veins.taper must be 0 to below 1");
        Check(rules.VeinSegments >= 1 && rules.VeinSegments < MaxVeinPoints, $"veins.segments must be 1 to {MaxVeinPoints - 1}");
        Check(rules.VeinStepMin > 0 && rules.VeinStepMin <= rules.VeinStepMax && rules.VeinStepMax <= 0.5f, "veins.step must be rising, above 0, at most 0.5");
        Check(rules.VeinTwist is >= 0 and <= 4, "veins.twist must be 0 to 4");
        Check(Unit(rules.PocketChance) && rules.PocketRadiusMin > 0 && rules.PocketRadiusMin <= rules.PocketRadiusMax && rules.PocketRadiusMax <= 0.4f,
            "pocket.chance must be 0 to 1, radius rising, above 0, at most 0.4");
        Check(Unit(rules.PocketOffset) && Unit(rules.PocketHolds), "pocket.offset and pocket.holds must be 0 to 1");
        Check(Unit(rules.UnstableChance) && rules.UnstableMax >= 0 && rules.UnstableMax < MaxPockets, $"unstable.chance must be 0 to 1, max 0 to {MaxPockets - 1}");
        Check(rules.UnstableRadiusMin > 0 && rules.UnstableRadiusMin <= rules.UnstableRadiusMax && rules.UnstableRadiusMax <= 0.25f,
            "unstable.radius must be rising, above 0, at most 0.25");
        Check(rules.UnstableIceWeight >= 0 && rules.UnstableVolatileWeight >= 0 && rules.UnstableIceWeight + rules.UnstableVolatileWeight > 0,
            "unstable weights must not be negative, and not both zero");
        Check(rules.MaxLocalOre is > 0 and <= 1, "maxLocalOre must be above 0, at most 1");
        Check(rules.BulkTolerance is > 0 and < 1, "bulkTolerance must be between 0 and 1");
        return rules;
    }
}

/// <summary>
/// What is inside one asteroid: crust, mantle, veins and pockets, from the world seed and the rock's name, the same
/// for every player. Described once per rock with <see cref="Describe"/>, then asked point by point with
/// <see cref="Sample"/> or for a small volume with <see cref="Average"/>; asking takes no memory.
/// <para>
/// The interior lives in the rock's normalised space: a point's distance from the centre is divided by the
/// surface's distance in that direction (<see cref="RockInteriorSurface"/>), so the rock is a unit ball there. The
/// crust is the outer <c>depth</c> of it, so it follows the surface; veins and pockets are tubes and balls in it.
/// Every share is worked out so that the whole interior adds up to the rock's published composition
/// (<see cref="Asteroid.Grade"/>, <see cref="Asteroid.Mix1Share"/>, <see cref="Asteroid.Mix2Share"/>) within
/// <see cref="RockInteriorRules.BulkTolerance"/>.
/// </para>
/// <para>
/// It draws from streams of its own, keyed by the seed, the sector and the rock's index, never from those the rock
/// was generated from, so it changes no rock. Everything a layer hangs on is plain arithmetic, square roots and
/// <see cref="DetMath"/>: the same bits on every machine.
/// </para>
/// </summary>
public struct RockInterior
{
    private static readonly ulong InteriorStream = SectorRandom.Key("rock-interior");
    private const ulong VeinDraws = 0x6A09E667F3BCC909;
    private const ulong PocketDraws = 0xBB67AE8584CAA73B;
    private const ulong UnstableDraws = 0x3C6EF372FE94F82B;

    [InlineArray(RockInteriorRules.MaxVeins * RockInteriorRules.MaxVeinPoints)]
    private struct VeinPoints
    {
        private Vector4 _first;
    }

    [InlineArray(RockInteriorRules.MaxVeins)]
    private struct VeinBounds
    {
        private Vector4 _first;
    }

    [InlineArray(RockInteriorRules.MaxPockets)]
    private struct Pockets
    {
        private Vector4 _first;
    }

    [InlineArray(RockInteriorRules.MaxPockets)]
    private struct PocketKinds
    {
        private RockPocketKind _first;
    }

    /// <summary>The rock's radius in metres, its shape and its ores, as <see cref="Asteroid"/> has them.</summary>
    public float Radius;
    public int Shape;
    public int Resource;
    public int Mix1;
    public int Mix2;

    /// <summary>The crust's depth as a fraction of the surface's distance, and how much it wobbles.</summary>
    public float CrustDepth;
    public float CrustWobble;

    /// <summary>How many veins, and how many points each has.</summary>
    public int VeinCount;
    public int VeinPointCount;

    /// <summary>How many pockets; the core pocket, if there is one, is the first.</summary>
    public int PocketCount;

    /// <summary>Which of the rock's mix-ins the core pocket holds: 1, 2, or 0 when there is no core pocket.</summary>
    public int PocketMix;

    /// <summary>Shares of main ore, first and second mix-in (x, y, z) in each layer, and in the core pocket.</summary>
    public Vector3 CrustShares;
    public Vector3 MantleShares;
    public Vector3 VeinShares;
    public Vector3 PocketShares;

    /// <summary>The share of the rock's volume each layer takes, as worked out to balance the ore.</summary>
    public float CrustVolume;
    public float VeinVolume;
    public float PocketVolume;
    public float UnstableVolume;

    private VeinPoints _veins;
    private VeinBounds _bounds;
    private Pockets _pockets;
    private PocketKinds _kinds;

    /// <summary>Point <paramref name="point"/> of vein <paramref name="vein"/> in normalised space; W is its radius.</summary>
    public readonly Vector4 VeinPoint(int vein, int point) => _veins[vein * RockInteriorRules.MaxVeinPoints + point];

    /// <summary>Pocket <paramref name="pocket"/>'s centre in normalised space; W is its radius.</summary>
    public readonly Vector4 PocketAt(int pocket) => _pockets[pocket];

    public readonly RockPocketKind PocketKind(int pocket) => _kinds[pocket];

    /// <summary>Whether the rock's body is ice (an ice shard) rather than rock.</summary>
    public readonly bool IceBody => Shape == AsteroidShape.Icicle;

    /// <summary>
    /// Describes the interior of rock <paramref name="rock"/>, whose generated record is <paramref name="asteroid"/>.
    /// Pure: the same seed, content and rock give the same interior anywhere. Takes no memory.
    /// </summary>
    public static void Describe(Galaxy galaxy, RockRef rock, in Asteroid asteroid, out RockInterior interior)
    {
        var rules = galaxy.Interiors ?? throw new InvalidOperationException("The galaxy has no interior rules.");
        var key = new SectorRandom(galaxy.Seed, rock.Sector, InteriorStream).NextUInt64();
        Describe(rules, key, (uint)rock.Index, asteroid, out interior);
    }

    /// <summary>The same, from the rules and the sector's interior key directly.</summary>
    public static void Describe(RockInteriorRules rules, ulong key, uint index, in Asteroid asteroid, out RockInterior interior)
    {
        interior = default;
        interior.Radius = asteroid.Radius;
        interior.Shape = asteroid.Shape;
        interior.Resource = asteroid.Resource;
        interior.Mix1 = asteroid.Resource >= 0 ? asteroid.Mix1 : -1;
        interior.Mix2 = interior.Mix1 >= 0 ? asteroid.Mix2 : -1;
        var depth = rules.CrustDepth;
        if (asteroid.Radius > 0 && depth * asteroid.Radius > rules.CrustMaxMetres)
            depth = rules.CrustMaxMetres / asteroid.Radius;
        interior.CrustDepth = depth;
        interior.CrustWobble = rules.CrustWobble;
        var shape = asteroid.Shape;
        var meanCube = RockInteriorSurface.MeanCube(shape);

        // Everything below lies inside the mantle zone, clear of the crust at its thickest.
        var mantle = 1f - depth * (1f + rules.CrustWobble);

        // The core pocket: the rarer mix-in, near the centre.
        var pockets = SectorRandom.Numbered(key ^ PocketDraws, index);
        var pocketRoll = pockets.NextSingle();
        var pocketSize = pockets.NextSingle();
        var pocketReach = pockets.NextSingle();
        Unit(ref pockets, out var px, out var py, out var pz);
        float pocketVolume = 0;
        if (interior.Mix1 >= 0 && pocketRoll < rules.PocketChance)
        {
            var offset = pocketReach * rules.PocketOffset * mantle;
            var radius = Math.Min(Lerp(rules.PocketRadiusMin, rules.PocketRadiusMax, pocketSize), 0.9f * mantle - offset);
            if (radius > 0)
            {
                interior._pockets[0] = new Vector4(px * offset, py * offset, pz * offset, radius);
                interior._kinds[0] = RockPocketKind.Ore;
                interior.PocketCount = 1;
                interior.PocketMix = interior.Mix2 >= 0 ? 2 : 1;
                // A ball about the centre has the rock's mean cube; one off it, more of the cube where it lies.
                Cube(shape, px, py, pz, out var there);
                var away = Math.Min(1f, offset / radius);
                pocketVolume = radius * radius * radius * ((1f - away) * meanCube + away * there) / meanCube;
            }
        }

        // Unstable pockets of ice or volatile gas anywhere in the mantle.
        var unstable = SectorRandom.Numbered(key ^ UnstableDraws, index);
        float unstableVolume = 0;
        for (var k = 0; k < rules.UnstableMax; k++)
        {
            var roll = unstable.NextSingle();
            var size = unstable.NextSingle();
            var reach = unstable.NextSingle();
            var kind = unstable.NextSingle();
            Unit(ref unstable, out var ux, out var uy, out var uz);
            if (roll >= rules.UnstableChance)
                continue;
            var radius = Lerp(rules.UnstableRadiusMin, rules.UnstableRadiusMax, size);
            var room = 0.9f * mantle - radius;
            if (room <= 0)
                continue;
            var distance = room * (0.3f + 0.7f * reach);
            var p = interior.PocketCount++;
            interior._pockets[p] = new Vector4(ux * distance, uy * distance, uz * distance, radius);
            interior._kinds[p] = kind * (rules.UnstableIceWeight + rules.UnstableVolatileWeight) < rules.UnstableIceWeight ? RockPocketKind.Ice : RockPocketKind.Volatile;
            Cube(shape, ux, uy, uz, out var there);
            unstableVolume += radius * radius * radius * there / meanCube;
        }

        // Veins: only where there is ore to put in them.
        var veins = SectorRandom.Numbered(key ^ VeinDraws, index);
        var count = rules.VeinsMin + veins.NextInt(rules.VeinsMax - rules.VeinsMin + 1);
        float veinVolume = 0;
        if (asteroid.Resource >= 0)
        {
            interior.VeinCount = count;
            interior.VeinPointCount = rules.VeinSegments + 1;
            for (var v = 0; v < count; v++)
                veinVolume += Vein(ref interior, ref veins, rules, v, mantle, shape, meanCube);
        }

        // Normalised volumes to shares of the rock: a ball of radius r is r^3 of the unit ball.
        interior.CrustVolume = RockInteriorSurface.CrustShare(shape, depth, rules.CrustWobble);
        interior.PocketVolume = pocketVolume;
        interior.UnstableVolume = unstableVolume;
        interior.VeinVolume = veinVolume / (4f / 3f * MathF.PI);
        Balance(ref interior, rules, asteroid);
    }

    /// <summary>
    /// Shares per layer that add up to the rock's composition. The core pocket takes what <see cref="RockInteriorRules.PocketHolds"/>
    /// says of its mix-in, as rich as the local limit allows; the rest is spread over crust, mantle and veins in the
    /// proportions the rules give, evened out as far as needed to keep every point under <see cref="RockInteriorRules.MaxLocalOre"/>.
    /// </summary>
    private static void Balance(ref RockInterior interior, RockInteriorRules rules, in Asteroid asteroid)
    {
        if (interior.Resource < 0)
            return;
        var bulk = new Vector3(asteroid.Grade, interior.Mix1 >= 0 ? asteroid.Mix1Share : 0, interior.Mix2 >= 0 ? asteroid.Mix2Share : 0);
        var limit = rules.MaxLocalOre;
        var fp = interior.PocketVolume;
        if (interior.PocketMix != 0 && fp > 0)
        {
            var held = interior.PocketMix == 2 ? bulk.Z : bulk.Y;
            var share = Math.Min(rules.PocketHolds * held / fp, limit);
            if (interior.PocketMix == 2)
            {
                interior.PocketShares = new Vector3(0, 0, share);
                bulk.Z = held - share * fp;
            }
            else
            {
                interior.PocketShares = new Vector3(0, share, 0);
                bulk.Y = held - share * fp;
            }
        }

        var fc = interior.CrustVolume;
        var fv = interior.VeinVolume;
        var fm = 1f - fc - fv - fp - interior.UnstableVolume;
        var matrix = fc + fv + fm;
        var total = bulk.X + bulk.Y + bulk.Z;
        if (matrix <= 0 || total <= 0)
            return;

        // Contrast t from 0 (every layer the same) to 1 (the rules' factors): the largest t whose richest layer
        // stays under the limit. Both sides are linear in t.
        var kc = rules.CrustOre;
        var kv = rules.VeinOre;
        var top = Math.Max(1f, Math.Max(kc, kv));
        var spread = fc * kc + fm + fv * kv;
        var need = total * (top - 1f) - limit * (spread - matrix);
        var room = limit * matrix - total;
        var t = room <= 0 ? 0f : need <= room ? 1f : room / need;
        var crust = 1f + t * (kc - 1f);
        var vein = 1f + t * (kv - 1f);
        var mantle = bulk / (fc * crust + fm + fv * vein);
        interior.MantleShares = mantle;
        interior.CrustShares = mantle * crust;
        interior.VeinShares = mantle * vein;
    }

    /// <summary>Lays out one vein and returns its volume in normalised space, weighted by the rock's cube there.</summary>
    private static float Vein(ref RockInterior interior, ref SectorRandom draws, RockInteriorRules rules, int v, float mantle, int shape, float meanCube)
    {
        var points = interior.VeinPointCount;
        var radius = Lerp(rules.VeinRadiusMin, rules.VeinRadiusMax, draws.NextSingle());
        var step = Lerp(rules.VeinStepMin, rules.VeinStepMax, draws.NextSingle());
        var start = draws.NextSingle();
        var keep = mantle - radius * (1f + rules.VeinTaper);
        Unit(ref draws, out var x, out var y, out var z);
        Unit(ref draws, out var hx, out var hy, out var hz);
        var at = Math.Max(0f, keep) * (0.2f + 0.6f * start);
        x *= at;
        y *= at;
        z *= at;
        float lowX = x, lowY = y, lowZ = z, highX = x, highY = y, highZ = z, widest = 0, volume = 0;
        var baseIndex = v * RockInteriorRules.MaxVeinPoints;
        for (var p = 0; p < points; p++)
        {
            var r = radius * (1f + rules.VeinTaper * draws.NextSigned());
            if (p > 0)
            {
                // Turn by a random push, then step; a step that would leave the mantle zone turns back first.
                Unit(ref draws, out var tx, out var ty, out var tz);
                hx += tx * rules.VeinTwist;
                hy += ty * rules.VeinTwist;
                hz += tz * rules.VeinTwist;
                Normalise(ref hx, ref hy, ref hz);
                var nx = x + hx * step;
                var ny = y + hy * step;
                var nz = z + hz * step;
                var reach = nx * nx + ny * ny + nz * nz;
                if (reach > keep * keep)
                {
                    var length = MathF.Sqrt(x * x + y * y + z * z);
                    if (length > 0)
                    {
                        var outward = (hx * x + hy * y + hz * z) / length;
                        if (outward > 0)
                        {
                            hx -= 2f * outward * x / length;
                            hy -= 2f * outward * y / length;
                            hz -= 2f * outward * z / length;
                        }
                    }

                    nx = x + hx * step;
                    ny = y + hy * step;
                    nz = z + hz * step;
                    reach = nx * nx + ny * ny + nz * nz;
                    if (reach > keep * keep)
                    {
                        var pull = keep / MathF.Sqrt(reach);
                        nx *= pull;
                        ny *= pull;
                        nz *= pull;
                    }
                }

                // The frustum from the last point to this one, weighted by the rock's cube where it lies.
                var previous = interior._veins[baseIndex + p - 1];
                var dx = nx - previous.X;
                var dy = ny - previous.Y;
                var dz = nz - previous.Z;
                var segment = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                var mx = (nx + previous.X) * 0.5f;
                var my = (ny + previous.Y) * 0.5f;
                var mz = (nz + previous.Z) * 0.5f;
                var mid = MathF.Sqrt(mx * mx + my * my + mz * mz);
                var cube = meanCube;
                if (mid > 1e-6f)
                    Cube(shape, mx / mid, my / mid, mz / mid, out cube);
                volume += MathF.PI * segment * (previous.W * previous.W + previous.W * r + r * r) / 3f * cube / meanCube;
                x = nx;
                y = ny;
                z = nz;
            }

            interior._veins[baseIndex + p] = new Vector4(x, y, z, r);
            lowX = Math.Min(lowX, x);
            lowY = Math.Min(lowY, y);
            lowZ = Math.Min(lowZ, z);
            highX = Math.Max(highX, x);
            highY = Math.Max(highY, y);
            highZ = Math.Max(highZ, z);
            widest = Math.Max(widest, r);
        }

        var cx = (lowX + highX) * 0.5f;
        var cy = (lowY + highY) * 0.5f;
        var cz = (lowZ + highZ) * 0.5f;
        var ex = (highX - lowX) * 0.5f;
        var ey = (highY - lowY) * 0.5f;
        var ez = (highZ - lowZ) * 0.5f;
        interior._bounds[v] = new Vector4(cx, cy, cz, MathF.Sqrt(ex * ex + ey * ey + ez * ez) + widest);
        return volume;
    }

    /// <summary>
    /// What is at a point given in metres from the rock's centre, in the rock's own frame (undo
    /// <see cref="Asteroid.Rotation"/> first: <see cref="ToLocal"/>). Takes no memory.
    /// </summary>
    public readonly void Sample(Vector3 local, out RockSample sample)
    {
        sample = default;
        var scale = Radius > 0 ? 1f / Radius : 0f;
        var qx = local.X * scale;
        var qy = local.Y * scale;
        var qz = local.Z * scale;
        var length = MathF.Sqrt(qx * qx + qy * qy + qz * qz);
        float dx = 0, dy = 1, dz = 0;
        if (length > 0)
        {
            dx = qx / length;
            dy = qy / length;
            dz = qz / length;
        }

        RockInteriorSurface.Lookup(Shape, dx, dy, dz, out var surface, out var wobble);
        var depth = length / surface;
        sample.Depth = depth;
        sample.Inside = depth <= 1f;
        sample.Material = IceBody ? RockMaterial.Ice : RockMaterial.Rock;
        sample.Resource = -1;
        Vector3 shares;
        if (depth > 1f - CrustDepth * (1f + CrustWobble * wobble))
        {
            sample.Layer = RockLayer.Crust;
            shares = CrustShares;
        }
        else
        {
            var nx = dx * depth;
            var ny = dy * depth;
            var nz = dz * depth;
            sample.Layer = RockLayer.Mantle;
            shares = MantleShares;
            var pocket = -1;
            for (var p = 0; p < PocketCount; p++)
            {
                var c = _pockets[p];
                var ox = nx - c.X;
                var oy = ny - c.Y;
                var oz = nz - c.Z;
                if (ox * ox + oy * oy + oz * oz <= c.W * c.W)
                {
                    pocket = p;
                    break;
                }
            }

            if (pocket >= 0)
            {
                sample.Layer = RockLayer.Pocket;
                sample.Pocket = _kinds[pocket];
                if (sample.Pocket == RockPocketKind.Ore)
                    shares = PocketShares;
                else
                {
                    shares = default;
                    sample.Material = sample.Pocket == RockPocketKind.Ice ? RockMaterial.Ice : RockMaterial.Volatile;
                }
            }
            else if (InVein(nx, ny, nz))
            {
                sample.Layer = RockLayer.Vein;
                shares = VeinShares;
            }
        }

        sample.MainShare = shares.X;
        sample.Mix1Share = shares.Y;
        sample.Mix2Share = shares.Z;
        if (shares.X > 0 && Resource >= 0)
        {
            sample.Resource = Resource;
            sample.Grade = shares.X;
        }

        if (shares.Y > sample.Grade && Mix1 >= 0)
        {
            sample.Resource = Mix1;
            sample.Grade = shares.Y;
        }

        if (shares.Z > sample.Grade && Mix2 >= 0)
        {
            sample.Resource = Mix2;
            sample.Grade = shares.Z;
        }
    }

    /// <summary>
    /// The average of a lattice of <paramref name="perAxis"/> cubed points (at most 16) within
    /// <paramref name="radius"/> metres of <paramref name="centre"/>, a rock-local point. Only points inside the rock
    /// count. Takes no memory.
    /// </summary>
    public readonly void Average(Vector3 centre, float radius, int perAxis, out RockVolume volume)
    {
        volume = default;
        perAxis = Math.Clamp(perAxis, 1, 16);
        var step = perAxis == 1 ? 0f : 2f * radius / (perAxis - 1);
        var reach = radius * radius * 1.0001f;
        for (var k = 0; k < perAxis; k++)
        {
            for (var j = 0; j < perAxis; j++)
            {
                for (var i = 0; i < perAxis; i++)
                {
                    var ox = perAxis == 1 ? 0f : -radius + step * i;
                    var oy = perAxis == 1 ? 0f : -radius + step * j;
                    var oz = perAxis == 1 ? 0f : -radius + step * k;
                    if (ox * ox + oy * oy + oz * oz > reach)
                        continue;
                    Sample(new Vector3(centre.X + ox, centre.Y + oy, centre.Z + oz), out var sample);
                    if (!sample.Inside)
                        continue;
                    volume.Samples++;
                    switch (sample.Layer)
                    {
                        case RockLayer.Crust: volume.Crust++; break;
                        case RockLayer.Mantle: volume.Mantle++; break;
                        case RockLayer.Vein: volume.Vein++; break;
                        default: volume.Pocket++; break;
                    }

                    if (sample.Unstable)
                        volume.Unstable++;
                    volume.MainShare += sample.MainShare;
                    volume.Mix1Share += sample.Mix1Share;
                    volume.Mix2Share += sample.Mix2Share;
                }
            }
        }

        if (volume.Samples == 0)
            return;
        var each = 1f / volume.Samples;
        volume.Crust *= each;
        volume.Mantle *= each;
        volume.Vein *= each;
        volume.Pocket *= each;
        volume.Unstable *= each;
        volume.MainShare *= each;
        volume.Mix1Share *= each;
        volume.Mix2Share *= each;
    }

    /// <summary>A point relative to the rock's centre, turned into the rock's own frame.</summary>
    public static Vector3 ToLocal(in Asteroid rock, Vector3 offset) => Vector3.Transform(offset, Quaternion.Conjugate(rock.Rotation));

    private readonly bool InVein(float x, float y, float z)
    {
        for (var v = 0; v < VeinCount; v++)
        {
            var b = _bounds[v];
            var bx = x - b.X;
            var by = y - b.Y;
            var bz = z - b.Z;
            if (bx * bx + by * by + bz * bz > b.W * b.W)
                continue;
            var baseIndex = v * RockInteriorRules.MaxVeinPoints;
            var a = _veins[baseIndex];
            for (var p = 1; p < VeinPointCount; p++)
            {
                var c = _veins[baseIndex + p];
                var sx = c.X - a.X;
                var sy = c.Y - a.Y;
                var sz = c.Z - a.Z;
                var px = x - a.X;
                var py = y - a.Y;
                var pz = z - a.Z;
                var along = sx * sx + sy * sy + sz * sz;
                var t = along > 0 ? Math.Clamp((px * sx + py * sy + pz * sz) / along, 0f, 1f) : 0f;
                var ox = px - sx * t;
                var oy = py - sy * t;
                var oz = pz - sz * t;
                var r = a.W + (c.W - a.W) * t;
                if (ox * ox + oy * oy + oz * oz <= r * r)
                    return true;
                a = c;
            }
        }

        return false;
    }

    /// <summary>The surface fraction cubed along a unit direction: how much rock lies that way.</summary>
    private static void Cube(int shape, float x, float y, float z, out float cube)
    {
        RockInteriorSurface.Lookup(shape, x, y, z, out var surface, out _);
        cube = surface * surface * surface;
    }

    /// <summary>A unit vector, even over the sphere, from two draws.</summary>
    private static void Unit(ref SectorRandom draws, out float x, out float y, out float z)
    {
        var a = draws.NextSingle();
        var b = draws.NextSingle();
        z = 2f * a - 1f;
        var ring = MathF.Sqrt(Math.Max(0f, 1f - z * z));
        var angle = 2f * MathF.PI * b;
        x = ring * DetMath.Cos(angle);
        y = ring * DetMath.Sin(angle);
    }

    private static void Normalise(ref float x, ref float y, ref float z)
    {
        var length = MathF.Sqrt(x * x + y * y + z * z);
        if (length > 0)
        {
            x /= length;
            y /= length;
            z /= length;
        }
        else
            y = 1f;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
