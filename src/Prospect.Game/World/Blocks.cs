using System.Numerics;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>The faster-than-light drives. Each crosses any distance; they differ in how the jump looks.</summary>
public enum FtlKind : byte
{
    None = 0,

    /// <summary>The stars stretch into lines and the hull is gone.</summary>
    Hyperspace = 1,

    /// <summary>A round gate opens ahead and the hull flies through it.</summary>
    Wormhole = 2,

    /// <summary>A tear opens in space and swallows the hull.</summary>
    Rift = 3,
}

/// <summary>A kind of block a hull can be built from, loaded from <c>content/blocks</c>.</summary>
public sealed class BlockType
{
    /// <summary>Per-stat volume exponents; omitted stats grow linearly with volume.</summary>
    public IReadOnlyDictionary<string, double> SizeRules { get; init; } = new Dictionary<string, double>();

    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Tonnes.</summary>
    public required float Mass { get; init; }
    public required float Hull { get; init; }

    /// <summary>Kilonewtons. Engines push forward only; thrusters push in every direction.</summary>
    public float Thrust { get; init; }

    /// <summary>True for main engines, whose thrust only acts forward.</summary>
    public bool ForwardOnly { get; init; }

    /// <summary>True for the block a hull cannot exist without; losing it destroys the hull.</summary>
    public bool Core { get; init; }

    /// <summary>The faster-than-light drive this block provides, if any.</summary>
    public FtlKind Ftl { get; init; }

    /// <summary>Megawatts the drive can take while charging for a jump.</summary>
    public float FtlPower { get; init; }

    /// <summary>Kilonewton-metres of turning effort.</summary>
    public float Torque { get; init; }

    /// <summary>Megawatts produced, and megawatts drawn when the block is working.</summary>
    public float PowerOutput { get; init; }
    public float PowerDraw { get; init; }

    /// <summary>Megajoules.</summary>
    public float EnergyStorage { get; init; }
    public float Shield { get; init; }
    public float ShieldRecharge { get; init; }

    /// <summary>Cubic metres of cargo space.</summary>
    public float Cargo { get; init; }
    public int Crew { get; init; }
}

/// <summary>
/// Constants that turn a hull's raw figures into how it flies. Data, so they can be tuned without code. The
/// figures in <c>content/blocks</c> are the same for every world; <see cref="Under"/> gives the rules of one
/// world, with its settings applied.
/// </summary>
public sealed record FlightRules
{
    public required float SpeedPerRootAcceleration { get; init; }
    public required float TurnRatePerRootAcceleration { get; init; }
    public required float BoostThrustMultiplier { get; init; }
    public required float BoostSpeedMultiplier { get; init; }
    public required float BoostPowerMultiplier { get; init; }

    /// <summary>Share of the battery's capacity it must hold before boost will engage, from 0 to 1.</summary>
    public required float BoostEnergyFloor { get; init; }
    public required float BoostHeatSeconds { get; init; }
    public required float CoolSeconds { get; init; }
    public required float ShieldRechargeDelaySeconds { get; init; }

    /// <summary>
    /// Share of its capacity a broken shield must charge back to before it comes online again, above 0 and at
    /// most 1. Below it the shield takes no hits, so it cannot flicker on and off at one point of charge.
    /// </summary>
    public float ShieldRestartFraction { get; init; } = 0.25f;

    /// <summary>How many times its normal recharge power a broken shield draws while it charges back, 1 to 10.</summary>
    public float ShieldRestartPowerMultiplier { get; init; } = 2f;
    /// <summary>Minimum closing speed for a contact glint, independent of damage.</summary>
    public float ShieldContactSpeed { get; init; } = 0.1f;

    /// <summary>Maximum tree visits per hull pair stride or asteroid query.</summary>
    public int CollisionTreeVisits { get; init; } = 8192;
    /// <summary>Maximum narrow-phase tests per query.</summary>
    public int CollisionBoxTests { get; init; } = 2048;
    /// <summary>Maximum touching pairs retained per query.</summary>
    public int CollisionContacts { get; init; } = 256;
    /// <summary>Storage ceiling for a query's retained contact pairs.</summary>
    public const int MaxCollisionContacts = 4096;
    /// <summary>Maximum translation samples per hull pair step.</summary>
    public int CollisionStrides { get; init; } = 32;
    public required float CollisionSafeSpeed { get; init; }
    public required float CollisionDamagePerTonneSpeedSquared { get; init; }
    public required float CollisionBounce { get; init; }
    /// <summary>Megajoules a jump takes per tonne of hull; the drive charges this much before it fires.</summary>
    public required float FtlEnergyPerTonne { get; init; }

    /// <summary>How closely the hull must point at its destination before the drive will charge, in degrees.</summary>
    public required float FtlAlignDegrees { get; init; }
    /// <summary>Seconds of calculation required alongside charging after alignment.</summary>
    public required float FtlCalculateSeconds { get; init; }
    /// <summary>Transit speed in metres per second.</summary>
    public float FtlSpeed { get; init; } = 40000f;
    public required float FtlTunnelSeconds { get; init; }
    public required float FtlArriveSeconds { get; init; }

    /// <summary>
    /// How far, in hull radii, the pulse of a hyperspace arrival reaches for the heaviest hull: lighter ones reach
    /// less, down to the arrival mark's own <see cref="MinArrivalReach"/>. Only how the arrival looks.
    /// </summary>
    public required float FtlArrivalReach { get; init; }

    /// <summary>The mass in tonnes whose arrival pulse reaches halfway to <see cref="FtlArrivalReach"/>.</summary>
    public required float FtlArrivalPulseMass { get; init; }

    /// <summary>The bounds <see cref="FtlArrivalReach"/> must lie in, in hull radii.</summary>
    public const float MinArrivalReach = 2.5f;
    public const float MaxArrivalReach = 16f;

    /// <summary>
    /// Shield points a collision takes for each point of damage the shield absorbs. Zero means the shield does
    /// not stand in the way of a collision at all: the damage goes to the blocks and the shield is left alone.
    /// </summary>
    public float CollisionShieldCost { get; init; } = 1f;

    /// <summary>What a world's settings multiply block hit points, shield recharge and jump drive power by.</summary>
    public float HullStrength { get; init; } = 1f;
    public float ShieldRechargeMultiplier { get; init; } = 1f;
    public float FtlChargeMultiplier { get; init; } = 1f;

    /// <summary>
    /// These rules as one world plays by them. This is the one place a world setting takes effect: to add a
    /// setting, list it in <c>content/rules/settings.json</c> and apply its number here.
    /// </summary>
    public FlightRules Under(WorldSettingCatalog catalog, in WorldSettings settings) =>
        this with
        {
            CollisionDamagePerTonneSpeedSquared = CollisionDamagePerTonneSpeedSquared * catalog.Number(settings, "collisionDamage"),
            CollisionShieldCost = catalog.Number(settings, "collisionShields"),
            ShieldRechargeMultiplier = catalog.Number(settings, "shieldRecharge"),
            FtlChargeMultiplier = catalog.Number(settings, "jumpChargeSpeed"),
            HullStrength = catalog.Number(settings, "hullStrength"),
        };
}

public sealed class BlockCatalog
{
    private readonly Dictionary<string, BlockType> _types;

    private BlockCatalog(Dictionary<string, BlockType> types, FlightRules rules)
    {
        _types = types;
        Rules = rules;
    }

    /// <summary>The rules as the content files give them, before any world's settings.</summary>
    public FlightRules Rules { get; }

    /// <summary>Content limits for versioned box designs.</summary>
    public DesignLimits Limits { get; private init; } = null!;

    public BlockType Get(string id) =>
        _types.TryGetValue(id, out var type) ? type : throw new InvalidDataException($"Unknown block type \"{id}\"");

    /// <summary>Every block type, in the order the file lists them: what a parts palette offers.</summary>
    public IReadOnlyList<BlockType> Types { get; private init; } = [];

    public static BlockCatalog Load(string path) => Parse(File.ReadAllBytes(path), path);

    /// <summary>Reads the catalogue from a file's bytes. <paramref name="path"/> names where they came from in error messages.</summary>
    public static BlockCatalog Parse(ReadOnlyMemory<byte> json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var r = root.GetProperty("rules"u8);
        var rules = new FlightRules
        {
            SpeedPerRootAcceleration = r.GetProperty("speedPerRootAcceleration"u8).GetSingle(),
            TurnRatePerRootAcceleration = r.GetProperty("turnRatePerRootAcceleration"u8).GetSingle(),
            BoostThrustMultiplier = r.GetProperty("boostThrustMultiplier"u8).GetSingle(),
            BoostSpeedMultiplier = r.GetProperty("boostSpeedMultiplier"u8).GetSingle(),
            BoostPowerMultiplier = r.GetProperty("boostPowerMultiplier"u8).GetSingle(),
            BoostEnergyFloor = Math.Clamp(r.GetProperty("boostEnergyFloor"u8).GetSingle(), 0f, 1f),
            BoostHeatSeconds = r.GetProperty("boostHeatSeconds"u8).GetSingle(),
            CoolSeconds = r.GetProperty("coolSeconds"u8).GetSingle(),
            ShieldRechargeDelaySeconds = r.GetProperty("shieldRechargeDelaySeconds"u8).GetSingle(),
            ShieldRestartFraction = r.GetProperty("shieldRestartFraction"u8).GetSingle(),
            ShieldRestartPowerMultiplier = r.GetProperty("shieldRestartPowerMultiplier"u8).GetSingle(),
            ShieldContactSpeed = r.TryGetProperty("shieldContactSpeed"u8, out var contactSpeed) ? contactSpeed.GetSingle() : 0.1f,
            CollisionTreeVisits = r.GetProperty("collisionTreeVisits"u8).GetInt32(),
            CollisionBoxTests = r.GetProperty("collisionBoxTests"u8).GetInt32(),
            CollisionContacts = r.GetProperty("collisionContacts"u8).GetInt32(),
            CollisionStrides = r.GetProperty("collisionStrides"u8).GetInt32(),
            CollisionSafeSpeed = r.GetProperty("collisionSafeSpeed"u8).GetSingle(),
            CollisionDamagePerTonneSpeedSquared = r.GetProperty("collisionDamagePerTonneSpeedSquared"u8).GetSingle(),
            CollisionBounce = r.GetProperty("collisionBounce"u8).GetSingle(),
            FtlEnergyPerTonne = r.GetProperty("ftlEnergyPerTonne"u8).GetSingle(),
            FtlAlignDegrees = r.GetProperty("ftlAlignDegrees"u8).GetSingle(),
            FtlCalculateSeconds = r.GetProperty("ftlCalculateSeconds"u8).GetSingle(),
            FtlSpeed = r.GetProperty("ftlSpeed"u8).GetSingle(),
            FtlTunnelSeconds = r.GetProperty("ftlTunnelSeconds"u8).GetSingle(),
            FtlArriveSeconds = r.GetProperty("ftlArriveSeconds"u8).GetSingle(),
            FtlArrivalReach = r.GetProperty("ftlArrivalReach"u8).GetSingle(),
            FtlArrivalPulseMass = r.GetProperty("ftlArrivalPulseMass"u8).GetSingle(),
        };
        if (rules.CollisionTreeVisits is < 1 or > 1000000 || rules.CollisionBoxTests is < 1 or > 1000000 || rules.CollisionContacts is < 1 or > FlightRules.MaxCollisionContacts || rules.CollisionStrides is < 1 or > 1024)
            throw new InvalidDataException($"{path}: invalid collision budgets");
        if (!float.IsFinite(rules.ShieldContactSpeed) || rules.ShieldContactSpeed <= 0)
            throw new InvalidDataException($"{path}: shieldContactSpeed must be finite and positive");
        if (!float.IsFinite(rules.ShieldRechargeDelaySeconds) || rules.ShieldRechargeDelaySeconds < 0)
            throw new InvalidDataException($"{path}: shieldRechargeDelaySeconds must be finite and nonnegative");
        if (!(rules.ShieldRestartFraction > 0 && rules.ShieldRestartFraction <= 1))
            throw new InvalidDataException($"{path}: shieldRestartFraction must be above 0 and at most 1");
        if (!(rules.ShieldRestartPowerMultiplier >= 1 && rules.ShieldRestartPowerMultiplier <= 10))
            throw new InvalidDataException($"{path}: shieldRestartPowerMultiplier must be from 1 to 10");
        if (!float.IsFinite(rules.FtlCalculateSeconds) || rules.FtlCalculateSeconds < 0)
            throw new InvalidDataException($"{path}: ftlCalculateSeconds must be finite and nonnegative");

        if (!float.IsFinite(rules.FtlSpeed) || rules.FtlSpeed <= 0)
            throw new InvalidDataException($"{path}: ftlSpeed must be finite and positive");
        if (!(rules.FtlArrivalReach >= FlightRules.MinArrivalReach && rules.FtlArrivalReach <= FlightRules.MaxArrivalReach))
            throw new InvalidDataException($"{path}: ftlArrivalReach must be from {FlightRules.MinArrivalReach} to {FlightRules.MaxArrivalReach} hull radii");
        if (!float.IsFinite(rules.FtlArrivalPulseMass) || rules.FtlArrivalPulseMass <= 0)
            throw new InvalidDataException($"{path}: ftlArrivalPulseMass must be finite and positive");
        if (!float.IsFinite(rules.FtlTunnelSeconds) || rules.FtlTunnelSeconds < 0 || rules.FtlTunnelSeconds > HullSim.MaxJumpTravelSeconds)
            throw new InvalidDataException($"{path}: ftlTunnelSeconds is outside the supported duration");

        static float Number(JsonElement block, ReadOnlySpan<byte> name) =>
            block.TryGetProperty(name, out var value) ? value.GetSingle() : 0f;

        var types = new Dictionary<string, BlockType>();
        foreach (var entry in root.GetProperty("blocks"u8).EnumerateObject())
        {
            var block = entry.Value;
            types[entry.Name] = new BlockType
            {
                SizeRules = BoxDesign.ReadSizeRules(block, path + ": " + entry.Name),
                Id = entry.Name,
                Name = block.GetProperty("name"u8).GetString()!,
                Mass = block.GetProperty("mass"u8).GetSingle(),
                Hull = block.GetProperty("hull"u8).GetSingle(),
                Thrust = Number(block, "thrust"u8),
                ForwardOnly = block.TryGetProperty("forwardOnly"u8, out var forwardOnly) && forwardOnly.GetBoolean(),
                Core = block.TryGetProperty("core"u8, out var core) && core.GetBoolean(),
                Ftl = block.TryGetProperty("ftl"u8, out var ftl)
                    ? ftl.GetString() switch
                    {
                        "hyperspace" => FtlKind.Hyperspace,
                        "wormhole" => FtlKind.Wormhole,
                        "rift" => FtlKind.Rift,
                        var other => throw new InvalidDataException($"{path}: unknown ftl drive \"{other}\""),
                    }
                    : FtlKind.None,
                FtlPower = Number(block, "ftlPower"u8),
                Torque = Number(block, "torque"u8),
                PowerOutput = Number(block, "powerOutput"u8),
                PowerDraw = Number(block, "powerDraw"u8),
                EnergyStorage = Number(block, "energyStorage"u8),
                Shield = Number(block, "shield"u8),
                ShieldRecharge = Number(block, "shieldRecharge"u8),
                Cargo = Number(block, "cargo"u8),
                Crew = (int)Number(block, "crew"u8),
            };
        }

        // A dictionary does not promise an order; the file's is kept apart for whoever lists the types.
        var listed = new List<BlockType>(types.Count);
        foreach (var entry in root.GetProperty("blocks"u8).EnumerateObject())
            if (!listed.Contains(types[entry.Name])) listed.Add(types[entry.Name]);
        return new BlockCatalog(types, rules) { Limits = DesignLimits.Read(root.GetProperty("limits"u8), path), Types = listed.AsReadOnly() };
    }
}

/// <summary>The figures on a hull's data sheet. All of them follow from its blocks.</summary>
public struct HullStats
{
    public float HullPoints;
    public float ShieldCapacity;
    public float ShieldRecharge;

    /// <summary>Tonnes.</summary>
    public float Mass;

    /// <summary>Cubic metres of hull.</summary>
    public float Volume;

    /// <summary>Width, height and length in metres.</summary>
    public Vector3 Size;
    public int Blocks;
    public float CargoHold;
    public int Crew;
    public float Acceleration;
    public float Deceleration;
    public float LateralAcceleration;
    public float MaxSpeed;
    public float BoostMaxSpeed;

    /// <summary>Pitch, yaw and roll rates in rad/s.</summary>
    public Vector3 TurnRate;

    /// <summary>Megawatts and megajoules.</summary>
    public float PowerRequired;
    public float PowerGenerated;
    public float EnergyStorage;

    /// <summary>The hull's faster-than-light drive, or none.</summary>
    public FtlKind Ftl;

    /// <summary>Megajoules one jump takes, and megawatts the drive draws while charging.</summary>
    public float FtlEnergy;
    public float FtlPower;
}

/// <summary>What kind of thing a hull is. It decides how the hull is shown and, later, what it can do.</summary>
public enum HullClass : byte
{
    /// <summary>A ship: flies between sectors under a pilot or an order.</summary>
    Ship,

    /// <summary>A small craft: a drone or fighter, short-ranged and fragile.</summary>
    Fighter,

    /// <summary>A station: built to stay where it is.</summary>
    Station,
}

/// <summary>
/// A hull design loaded from <c>content/ships</c>: its blocks as boxes, and everything that follows from them.
/// A design never changes and is shared by every hull built to it; a hull adds only which blocks it still has.
/// </summary>
public sealed class HullDesign
{
    private readonly BoxDesign _boxes;
    private FlightRules _rules = null!;
    internal BoxCollision Collision { get; }
    internal FlightRules CollisionRules => _rules;
    private float[] _hitPoints = [];
    private readonly ulong[] _fullSet;
    private readonly ulong[] _coreSet;

    private HullDesign(BoxDesign boxes, FlightRules rules)
    {
        _boxes = boxes;
        var blocks = boxes.Blocks;
        Cells = new (int, int, int)[blocks.Length];
        Types = new BlockType[blocks.Length];
        BlockOffsets = new Vector3[blocks.Length];
        BlockHalfExtents = new Vector3[blocks.Length];
        ExteriorFaces = new byte[blocks.Length];
        Shapes = new BlockShape[blocks.Length];
        _fullSet = new ulong[BlockSet.WordCount(blocks.Length)];
        _coreSet = new ulong[_fullSet.Length];
        BlockSet.Fill(_fullSet, blocks.Length);

        for (var i = 0; i < blocks.Length; i++)
        {
            ref readonly var box = ref blocks[i];
            Cells[i] = (box.At.X, box.At.Y, box.At.Z);
            Types[i] = box.Type;
            BlockOffsets[i] = boxes.Offset(box);
            BlockHalfExtents[i] = boxes.HalfExtents(box);
            Shapes[i] = box.Shape;
            if (box.Type.Core)
                BlockSet.Add(_coreSet, i);
        }

        UsesRoundedUnitShell = true;
        foreach (var box in blocks) UsesRoundedUnitShell &= box.Shape == BlockShape.Box && box.Size == new GridSize(1, 1, 1);

        Collision = new BoxCollision(BlockOffsets, BlockHalfExtents);
        // Partial adjacency retains a whole face, shared with picking. Adjacent boxes never overlap in area.
        Span<long> covered = stackalloc long[6];
        for (var i = 0; i < blocks.Length; i++)
        {
            covered.Clear();
            var box = blocks[i];
            var lo = new Vector3(box.At.X, box.At.Y, box.At.Z);
            var hi = lo + new Vector3(box.Size.X, box.Size.Y, box.Size.Z);
            var cursor = 0;
            var visits = int.MaxValue;
            var padding = new Vector3(0.001f);
            while (Collision.Next(ref cursor, BlockOffsets[i] - BlockHalfExtents[i] - padding,
                BlockOffsets[i] + BlockHalfExtents[i] + padding, _fullSet, ref visits, out var j))
            {
                if (i == j) continue;
                var other = blocks[j];
                if (other.Shape != BlockShape.Box) continue;
                var otherLo = new Vector3(other.At.X, other.At.Y, other.At.Z);
                var otherHi = otherLo + new Vector3(other.Size.X, other.Size.Y, other.Size.Z);
                for (var axis = 0; axis < 3; axis++)
                {
                    var u = (axis + 1) % 3;
                    var v = (axis + 2) % 3;
                    var area = (long)Math.Max(0, Math.Min(hi[u], otherHi[u]) - Math.Max(lo[u], otherLo[u]))
                        * (long)Math.Max(0, Math.Min(hi[v], otherHi[v]) - Math.Max(lo[v], otherLo[v]));
                    if (hi[axis] == otherLo[axis]) covered[2 * axis] += area;
                    if (lo[axis] == otherHi[axis]) covered[2 * axis + 1] += area;
                }
            }
            var faces = 0x3F;
            for (var side = 0; side < 6; side++)
            {
                var u = (side / 2 + 1) % 3;
                var v = (side / 2 + 2) % 3;
                if (covered[side] >= (long)(hi[u] - lo[u]) * (long)(hi[v] - lo[v]))
                    faces &= ~(1 << side);
            }
            ExteriorFaces[i] = box.Shape == BlockShape.Box ? (byte)faces : (byte)0x3F;
        }

        Sums = new BoxHullSums(boxes);
        Apply(rules);
    }

    /// <summary>
    /// The smooth shield shell of the intact hull, kept by admission (<see cref="GeometryAdmission.Measure"/>), in
    /// the hull's frame and metres; empty for a design that was never admitted. A damaged hull whose own shell would
    /// not fit, or is still being built, is drawn with this one: what is left of a hull is inside it.
    /// </summary>
    public ReadOnlyMemory<ShellVertex> IntactShell => _intact.Vertices;

    /// <summary>The triangles of <see cref="IntactShell"/>, three indices each, kept with it.</summary>
    public ReadOnlyMemory<ushort> IntactIndices => _intact.Indices;

    /// <summary>The shape <see cref="IntactShell"/> was built with, and every shell of this design is built with.</summary>
    public ShellShape ShellShape => _intact.Shape;

    // One object, swapped whole, so a reader on another thread never sees vertices of one build with indices of another.
    private IntactShellData _intact = new([], [], ShellShape.Default);

    private sealed record IntactShellData(ShellVertex[] Vertices, ushort[] Indices, ShellShape Shape);

    internal void Admit(ShellVertex[] vertices, ushort[] indices, ShellShape shape) => _intact = new IntactShellData(vertices, indices, shape);

    /// <summary>Only box-only unit designs may use the rounded cell shell.</summary>
    public bool UsesRoundedUnitShell { get; }

    public string Name => _boxes.Name;
    public HullClass Class => _boxes.Class;

    /// <summary>Metres per grid cell.</summary>
    public float BlockSize => _boxes.BlockSize;
    public Vector3 Color => _boxes.Color;

    /// <summary>The blocks as the file has them: each a box of whole cells and a type. A block's id is its index here.</summary>
    public ReadOnlySpan<DesignBox> Boxes => _boxes.Blocks;

    /// <summary>
    /// The lowest cell of each block, and each block's type. The rounded unit-cube shield uses these; sized geometry reads the full boxes.
    /// </summary>
    public (int X, int Y, int Z)[] Cells { get; }
    public BlockType[] Types { get; }

    /// <summary>How many blocks the design has. A block's id is its index in <see cref="Boxes"/>.</summary>
    public int BlockCount => Cells.Length;

    /// <summary>
    /// The point the hull turns about, in grid units: the intact design's centre of mass. It never moves, whatever
    /// the hull loses.
    /// </summary>
    public Double3 Pivot => _boxes.Center;

    /// <summary><see cref="Pivot"/> in single precision, for building a mesh about it.</summary>
    public Vector3 CenterOfMass => new((float)_boxes.Center.X, (float)_boxes.Center.Y, (float)_boxes.Center.Z);

    /// <summary>Each block's centre relative to <see cref="Pivot"/>, in metres, in the hull's own frame.</summary>
    public Vector3[] BlockOffsets { get; }

    /// <summary>Half of each block's width, height and length, in metres.</summary>
    public Vector3[] BlockHalfExtents { get; }

    /// <summary>
    /// Each block's shape, in block order, for callers that keep arrays rather than the design: a wedge fills half of
    /// the box <see cref="BlockOffsets"/> and <see cref="BlockHalfExtents"/> describe.
    /// </summary>
    public BlockShape[] Shapes { get; }

    /// <summary>The <see cref="BlockSet"/> of the intact hull: every block of the design. Never changes.</summary>
    public ReadOnlySpan<ulong> FullSet => _fullSet;

    /// <summary>
    /// For each block, the faces not wholly covered by adjacent boxes. Face bit <c>2 * axis + (negative ? 1 : 0)</c>, so +x, -x, +y, -y, +z, -z, the order
    /// <c>MeshBuilder.Hull</c> walks them. Partial adjacency retains the whole face.
    /// </summary>
    public byte[] ExteriorFaces { get; }

    /// <summary>The <see cref="BlockSet"/> of the blocks whose loss destroys the hull. Never changes.</summary>
    public ReadOnlySpan<ulong> CoreSet => _coreSet;

    /// <summary>
    /// What each block adds to the hull's figures and the sums of the intact hull. A hull's own sums, kept with
    /// its block state, are laid out by this and start as a copy of <see cref="BoxHullSums.Intact"/>.
    /// </summary>
    public BoxHullSums Sums { get; }

    /// <summary>The intact hull's data sheet and flight figures.</summary>
    public HullStats Stats { get; private set; }
    public FlightParams Flight { get; private set; }

    /// <summary>Loads a design and works out its figures under a world's rules.</summary>
    public static HullDesign Load(string path, BlockCatalog catalog, FlightRules rules) => Parse(File.ReadAllBytes(path), path, catalog, rules);

    /// <summary>
    /// Reads a design from a file's bytes and works out its figures under a world's rules.
    /// <paramref name="path"/> names where the bytes came from in error messages.
    /// </summary>
    public static HullDesign Parse(ReadOnlyMemory<byte> json, string path, BlockCatalog catalog, FlightRules rules)
    {
        using var document = JsonDocument.Parse(json);
        var boxes = BoxDesign.Parse(document.RootElement, catalog, path);
        GeometryAdmission.Count(boxes.Blocks.Length, catalog.Limits);

        var design = new HullDesign(boxes, rules);
        GeometryAdmission.Validate(design, catalog.Limits);
        if (design.Flight.Acceleration.X <= 0 || design.Flight.TurnRate.X <= 0 || design.Flight.PowerGenerated <= 0 || !BlockSet.Any(design._coreSet))
            throw new InvalidDataException($"{path}: a hull needs a core, a thruster, a gyro and a generator to fly");
        return design;
    }

    /// <summary>
    /// The same design in a world with other rules: the same blocks in the same places, with the figures that
    /// follow from them worked out again. The original is left as it was, so two worlds in one process, or a
    /// server and a client that disagree, each keep their own. The sums are shared: nothing in them depends on
    /// a world's rules.
    /// </summary>
    public HullDesign WithRules(FlightRules rules)
    {
        var design = (HullDesign)MemberwiseClone();
        design.Apply(rules);
        return design;
    }

    /// <summary>
    /// Builds a design straight from validated boxes, of any size and number, with no check that it can fly:
    /// used by isolated storage and arithmetic tests. Public loading also checks runtime resource budgets.
    /// </summary>
    internal static HullDesign Of(BoxDesign boxes, FlightRules rules) => new(boxes, rules);

    /// <summary>
    /// Whether a hull of this design has anything to repair: it has lost a block, which its
    /// <paramref name="revision"/> counts, or has fewer <paramref name="hullPoints"/> than the design as built.
    /// A shield that is down is no damage to the hull. The server decides a repair by this, and a client asks
    /// the same of the hull it was sent to know whether a repair would be taken.
    /// </summary>
    public bool NeedsRepair(uint revision, float hullPoints) => revision > 0 || hullPoints < Flight.HullPoints;

    /// <summary>The hit points a block of this design has when intact, in the world the design was evaluated for.</summary>
    public float BlockHitPoints(int block) => _hitPoints[block];

    /// <summary>Every block's hit points when intact, in block order, and what they add up to.</summary>
    internal ReadOnlySpan<float> IntactHitPoints => _hitPoints;
    internal double IntactHitTotal { get; private set; }

    /// <summary>
    /// A hull's flight figures and data sheet read off its sums, in the world the design was evaluated for.
    /// Constant time: this is what a hull is refitted with when it loses blocks.
    /// </summary>
    public void Figures(ReadOnlySpan<HullSum> sums, out FlightParams flight, out HullStats stats) => Sums.Evaluate(sums, _rules, out flight, out stats);

    private void Apply(FlightRules rules)
    {
        _rules = rules;
        Sums.Evaluate(Sums.Intact, rules, out var flight, out var stats);
        Flight = flight;
        Stats = stats;

        // A block's hit points follow from its type and size, times the world's hull strength.
        var points = new float[BlockCount];
        var total = 0.0;
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = (float)Sums.HullPoints(i) * rules.HullStrength;
            total += points[i];
        }

        _hitPoints = points;
        IntactHitTotal = total;
    }
}
