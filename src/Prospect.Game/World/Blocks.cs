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

    public static BlockCatalog Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
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

        return new BlockCatalog(types, rules) { Limits = DesignLimits.Read(root.GetProperty("limits"u8), path) };
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

/// <summary>A hull design loaded from <c>content/ships</c>: its blocks, and everything that follows from them.</summary>
public sealed class HullDesign
{
    private HullDesign(string name, float blockSize, (int X, int Y, int Z)[] cells, BlockType[] types, Vector3 color)
    {
        Name = name;
        BlockSize = blockSize;
        Cells = cells;
        Types = types;
        Color = color;
    }

    public string Name { get; }
    public HullClass Class { get; private init; }
    public float BlockSize { get; }

    /// <summary>Grid cells occupied by a block, and the type of block in each.</summary>
    public (int X, int Y, int Z)[] Cells { get; }
    public BlockType[] Types { get; }
    public Vector3 Color { get; }

    /// <summary>The most blocks a design may have, set by how block state is sent: one bit per block.</summary>
    public const int MaxBlocks = 64;

    /// <summary>The centre of mass of the intact hull in grid units; the hull turns about this point.</summary>
    public Vector3 CenterOfMass { get; private set; }

    /// <summary>Each block's centre relative to the centre of mass, in metres, in the hull's own frame.</summary>
    public Vector3[] BlockOffsets { get; private set; } = [];

    /// <summary>One bit per block, all set: the intact hull.</summary>
    public ulong FullMask { get; private set; }

    /// <summary>
    /// For each block, the faces the intact hull's mesh has: those with no block beside them. Face bit
    /// <c>2 * axis + (negative ? 1 : 0)</c>, so +x, -x, +y, -y, +z, -z, the order <c>MeshBuilder.Hull</c> walks them.
    /// </summary>
    public byte[] ExteriorFaces { get; private set; } = [];

    /// <summary>The bits of the blocks whose loss destroys the hull.</summary>
    public ulong CoreMask { get; private set; }

    /// <summary>The intact hull's data sheet and flight figures.</summary>
    public HullStats Stats { get; private set; }
    public FlightParams Flight { get; private set; }

    private FlightRules _rules = null!;

    /// <summary>Loads a design and works out its figures under a world's rules.</summary>
    public static HullDesign Load(string path, BlockCatalog catalog, FlightRules rules)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        var boxes = BoxDesign.Parse(root, catalog, path);
        if (boxes.Blocks.Length > MaxBlocks || boxes.Blocks.ToArray().Any(b => b.Size != new GridSize(1, 1, 1)))
            throw new InvalidDataException($"{path}: sized boxes or more than 64 blocks need blocks-at-scale stage 2 (storage, wire, collision and drawing)");

        var cells = new List<(int, int, int)>();
        var types = new List<BlockType>();
        foreach (var block in root.GetProperty("blocks"u8).EnumerateArray())
        {
            var at = block.GetProperty("at"u8);
            cells.Add((at[0].GetInt32(), at[1].GetInt32(), at[2].GetInt32()));
            types.Add(catalog.Get(block.GetProperty("type"u8).GetString()!));
        }

        if (cells.Count == 0)
            throw new InvalidDataException($"{path}: a hull needs at least one block");

        var color = root.GetProperty("color"u8);
        var design = new HullDesign(
            root.GetProperty("name"u8).GetString()!,
            root.GetProperty("blockSize"u8).GetSingle(),
            [.. cells],
            [.. types],
            new Vector3(color[0].GetSingle(), color[1].GetSingle(), color[2].GetSingle())
        )
        {
            Class = root.GetProperty("class"u8).GetString() switch
            {
                "ship" => HullClass.Ship,
                "fighter" => HullClass.Fighter,
                "station" => HullClass.Station,
                var other => throw new InvalidDataException($"{path}: unknown class \"{other}\""),
            },
        };
        if (cells.Count > MaxBlocks)
            throw new InvalidDataException($"{path}: {cells.Count} blocks; a design may have at most {MaxBlocks}");

        design.Initialize(rules, path);
        return design;
    }

    /// <summary>
    /// The same design in a world with other rules: the same blocks in the same places, with the figures that
    /// follow from them worked out again. The original is left as it was, so two worlds in one process, or a
    /// server and a client that disagree, each keep their own.
    /// </summary>
    public HullDesign WithRules(FlightRules rules)
    {
        var design = (HullDesign)MemberwiseClone();
        design._rules = rules;
        design.Evaluate(FullMask, out var flight, out var stats);
        design.Flight = flight;
        design.Stats = stats;
        return design;
    }

    /// <summary>The hit points a block of this design has when intact, in the world the design was evaluated for.</summary>
    public float BlockHitPoints(int block) => Types[block].Hull * _rules.HullStrength;

    private void Initialize(FlightRules rules, string path)
    {
        _rules = rules;
        var mass = 0f;
        var weighted = Vector3.Zero;
        for (var i = 0; i < Cells.Length; i++)
        {
            mass += Types[i].Mass;
            weighted += new Vector3(Cells[i].X, Cells[i].Y, Cells[i].Z) * Types[i].Mass;
            if (Types[i].Core)
                CoreMask |= 1UL << i;
        }

        CenterOfMass = weighted / mass;
        FullMask = Cells.Length == 64 ? ulong.MaxValue : (1UL << Cells.Length) - 1;
        BlockOffsets = new Vector3[Cells.Length];
        for (var i = 0; i < Cells.Length; i++)
            BlockOffsets[i] = (new Vector3(Cells[i].X, Cells[i].Y, Cells[i].Z) - CenterOfMass) * BlockSize;
        ExteriorFaces = new byte[Cells.Length];
        for (var i = 0; i < Cells.Length; i++)
        {
            var faces = 0x3F;
            foreach (var other in Cells)
            {
                var (x, y, z) = (other.X - Cells[i].X, other.Y - Cells[i].Y, other.Z - Cells[i].Z);
                if (Math.Abs(x) + Math.Abs(y) + Math.Abs(z) == 1)
                    faces &= ~(1 << (2 * (x != 0 ? 0 : y != 0 ? 1 : 2) + (x + y + z < 0 ? 1 : 0)));
            }

            ExteriorFaces[i] = (byte)faces;
        }

        Evaluate(FullMask, out var flight, out var stats);
        Flight = flight;
        Stats = stats;
        if (flight.Acceleration.X <= 0 || flight.TurnRate.X <= 0 || flight.PowerGenerated <= 0 || CoreMask == 0)
            throw new InvalidDataException($"{path}: a hull needs a core, a thruster, a gyro and a generator to fly");
    }

    /// <summary>
    /// Works out how the hull flies from the blocks it still has: mass and moments of inertia from where they
    /// sit, acceleration from thrust over mass, turning from gyro torque over inertia, and the power budget.
    /// A hull that has lost blocks is lighter and may have lost thrust, power or shields with them.
    /// </summary>
    public void Evaluate(ulong alive, out FlightParams flight, out HullStats stats)
    {
        var rules = _rules;
        var size = BlockSize;
        var mass = 0f;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var inertia = Vector3.Zero;
        var radius = 0f;
        float hull = 0, shield = 0, recharge = 0, engineThrust = 0, thrusterThrust = 0, torque = 0;
        float generated = 0, idlePower = 0, thrustPower = 0, shieldPower = 0, storage = 0, cargo = 0;
        var crew = 0;
        var blocks = 0;
        var ftl = FtlKind.None;
        var ftlPower = 0f;

        for (var i = 0; i < Cells.Length; i++)
        {
            // The collision sphere stays that of the intact hull; what is left still turns about the same point.
            var r = BlockOffsets[i];
            radius = Math.Max(radius, r.Length() + size * 0.87f);
            if ((alive & 1UL << i) == 0)
                continue;

            var type = Types[i];
            var cell = new Vector3(Cells[i].X, Cells[i].Y, Cells[i].Z);
            blocks++;
            mass += type.Mass;
            min = Vector3.Min(min, cell);
            max = Vector3.Max(max, cell);
            hull += type.Hull * rules.HullStrength;
            shield += type.Shield;
            recharge += type.ShieldRecharge;
            torque += type.Torque;
            generated += type.PowerOutput;
            storage += type.EnergyStorage;
            cargo += type.Cargo;
            crew += type.Crew;
            if (ftl == FtlKind.None)
                ftl = type.Ftl;
            if (type.Ftl == ftl)
                ftlPower += type.FtlPower;

            if (type.ForwardOnly)
                engineThrust += type.Thrust;
            else
                thrusterThrust += type.Thrust;

            if (type.Thrust > 0)
                thrustPower += type.PowerDraw;
            else if (type.Shield > 0)
                shieldPower += type.PowerDraw;
            else
                idlePower += type.PowerDraw;

            // Each block is a solid cube turning about the hull's centre of mass.
            var own = type.Mass * size * size / 6f;
            inertia += new Vector3(type.Mass * (r.Y * r.Y + r.Z * r.Z) + own, type.Mass * (r.X * r.X + r.Z * r.Z) + own, type.Mass * (r.X * r.X + r.Y * r.Y) + own);
        }

        mass = Math.Max(mass, 0.01f);
        inertia = Vector3.Max(inertia, new Vector3(0.001f));
        recharge *= rules.ShieldRechargeMultiplier;

        // A drive that takes power faster charges sooner; what a jump costs in all is the same.
        ftlPower *= rules.FtlChargeMultiplier;
        var lateral = thrusterThrust / mass;
        var forward = (engineThrust + thrusterThrust) / mass;
        var turnAcceleration = new Vector3(torque) / inertia;
        var turnRate = new Vector3(MathF.Sqrt(turnAcceleration.X), MathF.Sqrt(turnAcceleration.Y), MathF.Sqrt(turnAcceleration.Z)) * rules.TurnRatePerRootAcceleration;
        var maxSpeed = rules.SpeedPerRootAcceleration * MathF.Sqrt(forward);

        flight = new FlightParams
        {
            Acceleration = new Vector3(lateral, lateral, forward),
            ReverseAcceleration = lateral,
            BoostMultiplier = rules.BoostThrustMultiplier,
            MaxSpeed = maxSpeed,
            BoostMaxSpeed = maxSpeed * rules.BoostSpeedMultiplier,
            TurnRate = turnRate,
            TurnAcceleration = turnAcceleration,
            HeatRate = 1f / rules.BoostHeatSeconds,
            CoolRate = 1f / rules.CoolSeconds,
            Mass = mass,
            Inertia = inertia,
            Radius = radius,
            HullPoints = hull,
            ShieldCapacity = shield,
            ShieldRecharge = recharge,
            ShieldDelay = rules.ShieldRechargeDelaySeconds,
            PowerGenerated = generated,
            IdlePower = idlePower,
            ThrustPower = thrustPower,
            ShieldPower = shieldPower,
            BoostPowerMultiplier = rules.BoostPowerMultiplier,
            EnergyStorage = storage,
            BoostEnergyFloor = rules.BoostEnergyFloor * storage,
            CollisionSafeSpeed = rules.CollisionSafeSpeed,
            CollisionDamagePerTonneSpeedSquared = rules.CollisionDamagePerTonneSpeedSquared,
            CollisionBounce = rules.CollisionBounce,
            CollisionShieldCost = rules.CollisionShieldCost,
            Ftl = ftl,
            FtlPower = ftlPower,
            FtlEnergy = rules.FtlEnergyPerTonne * mass,
            FtlAlignCosine = MathF.Cos(rules.FtlAlignDegrees * MathF.PI / 180f),
            FtlCalculateSeconds = rules.FtlCalculateSeconds,
            FtlTunnelSeconds = rules.FtlTunnelSeconds,
            FtlSpeed = rules.FtlSpeed,
            FtlArriveSeconds = rules.FtlArriveSeconds,
        };

        stats = new HullStats
        {
            HullPoints = hull,
            ShieldCapacity = shield,
            ShieldRecharge = recharge,
            Mass = mass,
            Volume = blocks * size * size * size,
            Size = blocks > 0 ? (max - min + Vector3.One) * size : Vector3.Zero,
            Blocks = blocks,
            CargoHold = cargo,
            Crew = crew,
            Acceleration = forward,
            Deceleration = lateral,
            LateralAcceleration = lateral,
            MaxSpeed = maxSpeed,
            BoostMaxSpeed = maxSpeed * rules.BoostSpeedMultiplier,
            TurnRate = turnRate,
            PowerRequired = flight.PowerRequired,
            PowerGenerated = generated,
            EnergyStorage = storage,
            Ftl = ftl,
            FtlEnergy = rules.FtlEnergyPerTonne * mass,
            FtlPower = ftlPower,
        };
    }
}
