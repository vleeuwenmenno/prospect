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

    /// <summary>Kilonewtons. A thrust part pushes the hull the opposite way to the block's facing, and no other.</summary>
    public float Thrust { get; init; }

    /// <summary>The fuel a thrust part burns; without it in the tanks the part gives no thrust. None for an ion part.</summary>
    public FuelKind Fuel { get; init; }

    /// <summary>
    /// True for an ion part: a thrust part that burns no fuel and runs on power alone (<see cref="PowerDraw"/> at
    /// full thrust). Without power it gives no thrust; it never needs a tank.
    /// </summary>
    public bool Ion { get; init; }

    /// <summary>Kilograms of <see cref="Fuel"/> the part burns a second at full thrust, without boost.</summary>
    public float FuelBurn { get; init; }

    /// <summary>The fuel a tank holds, and how many kilograms of it.</summary>
    public FuelKind Tank { get; init; }
    public float FuelCapacity { get; init; }

    /// <summary>The most cells a block of the type may measure along any side; zero for the content's general limit.</summary>
    public int MaxSide { get; init; }

    /// <summary>
    /// Set for a part that sits on the face of a block and fills no grid cell (<see cref="DesignMount"/>); null for
    /// a block. Its figures are those of one unit of the part: a part placed larger (<see cref="MountSpec.Sizes"/>,
    /// <see cref="DesignMount.Size"/>) counts them once for each unit, and nothing else scales them.
    /// </summary>
    public MountSpec? Mount { get; init; }

    /// <summary>Optional automatic cargo tractor, present only on a face mount.</summary>
    public TractorSpec? Tractor { get; init; }

    /// <summary>Set for a drone bay: a box block that keeps drones and lets them out through one hatch face.</summary>
    public BaySpec? Bay { get; init; }

    /// <summary>Optional mining laser, present only on a face mount. Such a part belongs to an action group.</summary>
    public LaserSpec? Laser { get; init; }

    /// <summary>
    /// Kilograms of raw ore an ore bin holds for each cubic metre of its box (<c>"oreKgPerCubicMetre"</c>); zero for
    /// every block that is no bin. A bin holds one ore at a time (<see cref="OreBin"/>).
    /// </summary>
    public float OreCapacity { get; init; }

    /// <summary>
    /// True for an ore hopper (<c>"hopper": true</c>): a box block with one intake face, its facing, through which
    /// ore chunks reach the hull's bins.
    /// </summary>
    public bool Hopper { get; init; }

    /// <summary>
    /// True for a block with one open face that must look away from what it is set against: a drone bay's hatch and an
    /// ore hopper's intake. The face is the block's facing, chosen when it is placed and mirrored with it.
    /// </summary>
    public bool FacesOut => Bay is not null || Hopper;

    /// <summary>
    /// True for a block that is always a box and never a wedge (<c>"boxOnly"</c> in <c>blocks.json</c>): tanks, whose
    /// round vessels a slant would cut, and sensors and scanners. <see cref="BoxDesign.ShapeAllowed"/> is the one rule.
    /// </summary>
    public bool BoxOnly { get; init; }

    /// <summary>
    /// True for a part built for the escape drone's 0.25 m blocks (<c>"drone"</c> in <c>blocks.json</c>); every other
    /// part is built for a ship. <see cref="FitsClass"/> is the one rule the editor's palette and its placements ask.
    /// </summary>
    public bool Drone { get; init; }

    /// <summary>
    /// Whether the part belongs on a craft of <paramref name="craft"/>: drone parts on small craft (the drone is the
    /// one design of class <see cref="HullClass.Fighter"/>), every other part on the rest.
    /// </summary>
    public bool FitsClass(HullClass craft) => Drone == (craft == HullClass.Fighter);

    /// <summary>True for the block a hull cannot exist without; losing it destroys the hull.</summary>
    public bool Core { get; init; }
    public bool Sensor { get; init; }
    public bool Charting { get; init; }

    /// <summary>Optional module identity and evaluated content figures.</summary>
    public ModuleSpec Module { get; init; }

    /// <summary>The faster-than-light drive this block provides, if any.</summary>
    public FtlKind Ftl { get; init; }

    /// <summary>Megawatts the drive can take while charging for a jump.</summary>
    public float FtlPower { get; init; }
    /// <summary>Maximum XYZ jump distance in sectors; hull evaluation converts to metres. Neither block size nor duplicates increase it.</summary>
    public float FtlMaxSectors { get; init; }

    /// <summary>Kilonewton-metres of turning effort.</summary>
    public float Torque { get; init; }

    /// <summary>Megawatts produced, and megawatts drawn when the block is working.</summary>
    public float PowerOutput { get; init; }
    public float PowerDraw { get; init; }

    /// <summary>Megajoules.</summary>
    public float EnergyStorage { get; init; }
    public float Shield { get; init; }
    public float ShieldRecharge { get; init; }

    /// <summary>Cargo capacity in u per ship cell.</summary>
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

    /// <summary>How many times its normal rate thrust burns fuel while boosted, 1 to 100.</summary>
    public float BoostFuelMultiplier { get; init; } = 1f;

    /// <summary>Share of the battery's capacity it must hold before boost will engage, from 0 to 1.</summary>
    public required float BoostEnergyFloor { get; init; }

    /// <summary>
    /// Share of the battery's capacity it must hold again before mining lasers that ran it empty fire once more, from
    /// 0 to 1. Zero without the figure: lasers then wait only for a tick the battery can pay.
    /// </summary>
    public float LaserResumeBattery { get; init; }
    public required float BoostHeatSeconds { get; init; }
    public required float CoolSeconds { get; init; }

    /// <summary>
    /// What a cold gas cloud does to heat, for a hull whose centre is inside one: how many times as fast boost heat
    /// and laser heat build (at most 1) and how many times as fast they are shed (at least 1). Both 1 without the
    /// figures, which leaves a cold cloud with no effect on heat.
    /// </summary>
    public float ColdCloudHeating { get; init; } = 1f;

    public float ColdCloudCooling { get; init; } = 1f;

    /// <summary>
    /// Seconds between two looks at whether a hull is inside a cold cloud. The server keeps the answer with the
    /// hull between looks, so the flight step never asks the gas.
    /// </summary>
    public float ColdCloudCheckSeconds { get; init; } = 0.25f;
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

    /// <summary>Impact energy per shield capacity that produces a full contact flash.</summary>
    public float ShieldImpactEnergyFraction { get; init; } = 0.9f;

    /// <summary>Maximum tree visits per hull pair stride or asteroid query.</summary>
    public int CollisionTreeVisits { get; init; } = 8192;
    /// <summary>Maximum narrow-phase tests per query.</summary>
    public int CollisionBoxTests { get; init; } = 2048;
    /// <summary>Maximum touching pairs retained per query.</summary>
    /// <summary>Minimum fraction of original block HP for a visual dent.</summary>
    public float DentThreshold { get; init; } = 0.02f;
    /// <summary>Damage fraction producing the maximum visual depth.</summary>
    public float DentFullDamage { get; init; } = 0.5f;
    /// <summary>Maximum inward dent as a fraction of the distance to the solid centroid.</summary>
    public float DentMaxDepth { get; init; } = 0.2f;
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
    public float SectorSize { get; init; } = 80000f;
    /// <summary>Unattended loose-container lifetime in ticks; zero means never expire.</summary>
    public long ContainerLifetimeTicks { get; init; } = 60 * 60 * 60;
    /// <summary>Unattended wreck lifetime in ticks; zero means never expire.</summary>
    public long WreckLifetimeTicks { get; init; } = 30 * 60 * 60;
    /// <summary>Maximum wrecks retained in one sector.</summary>
    public int WreckMaxPerSector { get; init; } = 64;
    /// <summary>Cached setting positions for allocation-free persistence of mutable wreck settings.</summary>
    public int WreckLifetimeIndex { get; init; }
    public int WreckMaxIndex { get; init; }

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
            FtlSpeed = catalog.Number(settings, "ftlSpeedKmPerSecond") * 1000,
            FtlChargeMultiplier = catalog.Number(settings, "jumpChargeSpeed"),
            HullStrength = catalog.Number(settings, "hullStrength"),
            SectorSize = catalog.Number(settings, "sectorSize") * 1000,
            ContainerLifetimeTicks = (long)catalog.Number(settings, "containerLifetimeMinutes") * 3600,
            WreckLifetimeTicks = (long)catalog.Number(settings, "wreckLifetimeMinutes") * 3600,
            WreckMaxPerSector = (int)catalog.Number(settings, "wreckMaxPerSector"),
            WreckLifetimeIndex = catalog.Find("wreckLifetimeMinutes")!.Index,
            WreckMaxIndex = catalog.Find("wreckMaxPerSector")!.Index,
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

    // A mount's entry: its unit square and depth in tenths of a cell, the block an old file's part becomes, and, for a
    // part placed in several sizes, the sizes and the one a file that names none means.
    private static MountSpec ReadMount(JsonElement mount)
    {
        var spec = new MountSpec(mount.GetProperty("side"u8).GetInt32(), mount.GetProperty("depth"u8).GetInt32(), mount.GetProperty("legacyBlock"u8).GetString()!,
            mount.TryGetProperty("blockThrough"u8, out var through) ? through.GetInt32() : 4);
        if (!mount.TryGetProperty("sizes"u8, out var listed))
            return mount.TryGetProperty("defaultSize"u8, out var lone) ? spec with { DefaultSize = lone.GetInt32() } : spec;
        var sizes = new int[listed.GetArrayLength()];
        for (var i = 0; i < sizes.Length; i++)
            sizes[i] = listed[i].GetInt32();
        return spec with { Sizes = sizes, DefaultSize = mount.TryGetProperty("defaultSize"u8, out var chosen) ? chosen.GetInt32() : sizes.Length > 0 ? sizes[0] : 1 };
    }

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
            BoostFuelMultiplier = r.GetProperty("boostFuelMultiplier"u8).GetSingle(),
            BoostEnergyFloor = Math.Clamp(r.GetProperty("boostEnergyFloor"u8).GetSingle(), 0f, 1f),
            LaserResumeBattery = r.TryGetProperty("laserResumeBattery"u8, out var laserResume) ? laserResume.GetSingle() : 0f,
            BoostHeatSeconds = r.GetProperty("boostHeatSeconds"u8).GetSingle(),
            CoolSeconds = r.GetProperty("coolSeconds"u8).GetSingle(),
            ColdCloudHeating = r.TryGetProperty("coldCloudHeating"u8, out var coldHeating) ? coldHeating.GetSingle() : 1f,
            ColdCloudCooling = r.TryGetProperty("coldCloudCooling"u8, out var coldCooling) ? coldCooling.GetSingle() : 1f,
            ColdCloudCheckSeconds = r.TryGetProperty("coldCloudCheckSeconds"u8, out var coldCheck) ? coldCheck.GetSingle() : 0.25f,
            ShieldRechargeDelaySeconds = r.GetProperty("shieldRechargeDelaySeconds"u8).GetSingle(),
            ShieldRestartFraction = r.GetProperty("shieldRestartFraction"u8).GetSingle(),
            ShieldRestartPowerMultiplier = r.GetProperty("shieldRestartPowerMultiplier"u8).GetSingle(),
            ShieldImpactEnergyFraction = r.TryGetProperty("shieldImpactEnergyFraction"u8, out var impactFraction) ? impactFraction.GetSingle() : 0.9f,
            ShieldContactSpeed = r.TryGetProperty("shieldContactSpeed"u8, out var contactSpeed) ? contactSpeed.GetSingle() : 0.1f,
            CollisionTreeVisits = r.GetProperty("collisionTreeVisits"u8).GetInt32(),
            CollisionBoxTests = r.GetProperty("collisionBoxTests"u8).GetInt32(),
            DentThreshold = r.GetProperty("dentThreshold"u8).GetSingle(),
            DentFullDamage = r.GetProperty("dentFullDamage"u8).GetSingle(),
            DentMaxDepth = r.GetProperty("dentMaxDepth"u8).GetSingle(),
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
        if (!(rules.ColdCloudHeating > 0f && rules.ColdCloudHeating <= 1f) || !(rules.ColdCloudCooling >= 1f && rules.ColdCloudCooling <= 100f)
            || !(rules.ColdCloudCheckSeconds >= 1f / 60f && rules.ColdCloudCheckSeconds <= 10f))
            throw new InvalidDataException($"{path}: coldCloudHeating is above 0 and at most 1, coldCloudCooling 1 to 100, coldCloudCheckSeconds a sixtieth of a second to 10.");
        if (!(rules.LaserResumeBattery >= 0f && rules.LaserResumeBattery <= 1f))
            throw new InvalidDataException($"{path}: laserResumeBattery is a share of the battery, 0 to 1.");
        if (!float.IsFinite(rules.DentThreshold) || rules.DentThreshold <= 0 || !float.IsFinite(rules.DentFullDamage) || rules.DentFullDamage < rules.DentThreshold
            || !float.IsFinite(rules.DentMaxDepth) || rules.DentMaxDepth <= 0 || rules.DentMaxDepth > 0.5f)
            throw new InvalidDataException("Invalid visual dent limits.");
        if (rules.CollisionTreeVisits is < 1 or > 1000000 || rules.CollisionBoxTests is < 1 or > 1000000 || rules.CollisionContacts is < 1 or > FlightRules.MaxCollisionContacts || rules.CollisionStrides is < 1 or > 1024)
            throw new InvalidDataException($"{path}: invalid collision budgets");
        if (!(rules.BoostFuelMultiplier >= 1 && rules.BoostFuelMultiplier <= 100))
            throw new InvalidDataException($"{path}: boostFuelMultiplier must be from 1 to 100");
        if (!float.IsFinite(rules.ShieldImpactEnergyFraction) || rules.ShieldImpactEnergyFraction <= 0)
            throw new InvalidDataException($"{path}: shieldImpactEnergyFraction must be finite and positive");
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
                Module = ModuleSpec.Read(block, root),
                Tractor = TractorSpec.Read(block, root),
                Laser = LaserSpec.Read(block, root),
                Bay = BaySpec.Read(block, root),
                OreCapacity = Number(block, "oreKgPerCubicMetre"u8),
                Hopper = block.TryGetProperty("hopper"u8, out var hopper) && hopper.GetBoolean(),
                Name = block.GetProperty("name"u8).GetString()!,
                Mass = block.GetProperty("mass"u8).GetSingle(),
                Hull = block.GetProperty("hull"u8).GetSingle(),
                Thrust = Number(block, "thrust"u8),
                Fuel = block.TryGetProperty("fuel"u8, out var fuel) ? Fuels.Parse(fuel.GetString(), path + ": " + entry.Name) : FuelKind.None,
                FuelBurn = Number(block, "fuelBurn"u8),
                Ion = block.TryGetProperty("ion"u8, out var ion) && ion.GetBoolean(),
                Tank = block.TryGetProperty("tank"u8, out var tank) ? Fuels.Parse(tank.GetString(), path + ": " + entry.Name) : FuelKind.None,
                FuelCapacity = Number(block, "fuelCapacity"u8),
                MaxSide = block.TryGetProperty("maxSide"u8, out var maxSide) ? maxSide.GetInt32() : 0,
                Mount = block.TryGetProperty("mount"u8, out var mount) ? ReadMount(mount) : null,
                BoxOnly = block.TryGetProperty("boxOnly"u8, out var boxOnly) && boxOnly.GetBoolean(),
                Drone = block.TryGetProperty("drone"u8, out var drone) && drone.GetBoolean(),
                Sensor = block.TryGetProperty("sensor"u8, out var sensor) && sensor.GetBoolean(),
                Charting = block.TryGetProperty("charting"u8, out var charting) && charting.GetBoolean(),
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
                FtlMaxSectors = Number(block, "ftlMaxSectors"u8),
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

        var limits = DesignLimits.Read(root.GetProperty("limits"u8), path);
        foreach (var type in types.Values)
        {
            if (type.Ftl != FtlKind.None && (!float.IsFinite(type.FtlMaxSectors) || type.FtlMaxSectors <= 0))
                throw new InvalidDataException($"{path}: a drive needs a positive finite ftlMaxSectors");

            // Thrust and fuel go together: a part that pushes names what it burns and how fast, and nothing else does.
            // An ion part is the one exception: it burns no fuel, so it names none and draws its power instead.
            if (!float.IsFinite(type.Thrust) || type.Thrust < 0 || (type.Thrust > 0) != (type.Fuel != FuelKind.None || type.Ion))
                throw new InvalidDataException($"{path}: {type.Id}: a block with thrust names its fuel or is an ion part, and only such a block does");
            if (type.Ion && (type.Fuel != FuelKind.None || !(float.IsFinite(type.PowerDraw) && type.PowerDraw > 0)))
                throw new InvalidDataException($"{path}: {type.Id}: an ion part burns no fuel and needs a positive finite powerDraw");
            if (type.Fuel != FuelKind.None ? !(float.IsFinite(type.FuelBurn) && type.FuelBurn > 0) : type.FuelBurn != 0)
                throw new InvalidDataException($"{path}: {type.Id}: a thrust part needs a positive finite fuelBurn, and only such a part has one");
            if (type.Tank != FuelKind.None ? !(float.IsFinite(type.FuelCapacity) && type.FuelCapacity > 0) : type.FuelCapacity != 0)
                throw new InvalidDataException($"{path}: {type.Id}: a tank needs a positive finite fuelCapacity, and only a tank has one");
            if (type.MaxSide < 0 || type.MaxSide > limits.MaxBoxSide)
                throw new InvalidDataException($"{path}: {type.Id}: maxSide must be from 1 to {limits.MaxBoxSide}, or left out");

            // A mount has no independent block state: its figures go into the sums
            // with its parent block, and it has no hit points, size or identity of its own.
            if (type.Tractor is not null && (type.Mount is null || type.Thrust != 0 || type.PowerDraw != 0))
                throw new InvalidDataException($"{path}: {type.Id}: a tractor requires a non-thrust mount with active-only power");

            // A laser draws its power only while it fires, through the flight model's laser share.
            if (type.Laser is not null && (type.Mount is null || type.Thrust != 0 || type.PowerDraw != 0 || type.Tractor is not null || type.Module.Family != ModuleFamily.None))
                throw new InvalidDataException($"{path}: {type.Id}: a mining laser is a mount with no thrust, tractor, module or standing power draw");

            // A bay is one box with one hatch, its facing: never a mount, a wedge or a part of the drone itself.
            if (type.Bay is not null && (type.Mount is not null || !type.BoxOnly || type.Drone || type.Thrust != 0 || type.Tank != FuelKind.None || type.Cargo != 0 || type.Core))
                throw new InvalidDataException($"{path}: {type.Id}: a drone bay is a box-only ship block and nothing else");

            // A bin and a hopper are plain box blocks of a ship: neither a mount, a tank, a rack, a bay nor each other.
            var plain = type.Mount is null && type.BoxOnly && !type.Drone && type.Thrust == 0 && type.Tank == FuelKind.None && type.Cargo == 0 && !type.Core && type.Bay is null;
            if (!float.IsFinite(type.OreCapacity) || type.OreCapacity < 0 || type.OreCapacity > 1e6f || (type.OreCapacity > 0 && (!plain || type.Hopper)))
                throw new InvalidDataException($"{path}: {type.Id}: an ore bin is a box-only ship block with a positive finite oreKgPerCubicMetre and nothing else");
            if (type.Hopper && !plain)
                throw new InvalidDataException($"{path}: {type.Id}: an ore hopper is a box-only ship block and nothing else");
            if (type.Mount is not { } spec)
                continue;
            if (spec.Side is < 1 or > FaceMounts.MaxSide || spec.Depth is < 1 or > FaceMounts.MaxDepth)
                throw new InvalidDataException($"{path}: {type.Id}: a mount's side is 1 to {FaceMounts.MaxSide} tenths of a cell and its depth 1 to {FaceMounts.MaxDepth}");

            // Sizes: whole units a side, rising, the largest still within a cell; the default is one of them. Only a
            // thrust part is sized: its figures are per unit, and a tractor, a laser or a scanner is one device.
            var sizesValid = spec.Sizes.Count is >= 1 and <= MountSpec.MaxSize && spec.Allows(spec.DefaultSize);
            for (var i = 0; sizesValid && i < spec.Sizes.Count; i++)
                sizesValid = spec.Sizes[i] is >= 1 and <= MountSpec.MaxSize && (i == 0 || spec.Sizes[i] > spec.Sizes[i - 1]);
            if (!sizesValid || spec.LargestSide > FaceMounts.MaxSide)
                throw new InvalidDataException($"{path}: {type.Id}: a mount's sizes are rising whole numbers from 1 to {MountSpec.MaxSize}, the largest no wider than a cell ({FaceMounts.MaxSide} tenths), and defaultSize is one of them");
            if (spec.Sizable && !(type.Thrust > 0))
                throw new InvalidDataException($"{path}: {type.Id}: only a thrust part is placed in more than one size");
            if (!(type.Thrust > 0 || type.Tractor is not null || type.Laser is not null || type.Module.Family != ModuleFamily.None) || type.Core || type.Sensor || type.Charting || type.Ftl != FtlKind.None || type.Tank != FuelKind.None
                || (type.Module.Family != ModuleFamily.None && (type.Thrust != 0 || type.Tractor is not null)) || type.Crew != 0 || type.MaxSide != 0 || type.SizeRules.Count != 0 || type.BoxOnly)
                throw new InvalidDataException($"{path}: {type.Id}: only a thrust part, tractor, mining laser or module scanner mounts on a face, and it has no size rules, maxSide or boxOnly");
            if (spec.LastBlockVersion is < 4 or >= BoxDesign.FormatVersion)
                throw new InvalidDataException($"{path}: {type.Id}: blockThrough must name a design file version from 4 up to the one before {BoxDesign.FormatVersion}");
            if (!types.TryGetValue(spec.LegacyBlock, out var legacy) || legacy.Mount is not null)
                throw new InvalidDataException($"{path}: {type.Id}: legacyBlock must name a block type that is no mount");
        }

        // A dictionary does not promise an order; the file's is kept apart for whoever lists the types.
        var listed = new List<BlockType>(types.Count);
        foreach (var entry in root.GetProperty("blocks"u8).EnumerateObject())
            if (!listed.Contains(types[entry.Name])) listed.Add(types[entry.Name]);
        return new BlockCatalog(types, rules) { Limits = limits, Types = listed.AsReadOnly() };
    }
}

/// <summary>The figures on a hull's data sheet. All of them follow from its blocks.</summary>
public struct HullStats
{
    /// <summary>The selected surviving equipment for each family; duplicates provide redundancy, not stacking.</summary>
    public ModuleEquipment GasScanner, AsteroidScanner, AsteroidLocator;

    /// <summary>Reads one family from the shared block evaluator.</summary>
    public readonly ModuleEquipment Module(ModuleFamily family) => family switch
    {
        ModuleFamily.GasScanner => GasScanner,
        ModuleFamily.AsteroidScanner => AsteroidScanner,
        ModuleFamily.AsteroidLocator => AsteroidLocator,
        _ => default
    };

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

    /// <summary>What the tanks the hull still has can hold of each fuel, in kilograms.</summary>
    public FuelTanks FuelCapacity;
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

    /// <summary>The longest jump the selected surviving drive block allows, in metres; zero without a working drive.</summary>
    public float FtlMaxDistance;
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
    internal int CargoCapacity { get; private set; }
    internal readonly int[] SensorBlocks;
    internal readonly int[] ChartingBlocks;
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
        var mounts = boxes.Mounts;
        var tractors = new List<int>();
        for (var i = 0; i < mounts.Length; i++) if (mounts[i].Type.Tractor is not null) tractors.Add(i);
        Tractors = tractors.ToArray();
        var lasers = new List<int>();
        for (var i = 0; i < mounts.Length; i++) if (mounts[i].Type.Laser is not null) lasers.Add(i);
        Lasers = lasers.ToArray();
        MountOffsets = new Vector3[mounts.Length];
        MountHalfExtents = new Vector3[mounts.Length];
        for (var i = 0; i < mounts.Length; i++)
        {
            MountOffsets[i] = boxes.MountOffset(mounts[i]);
            MountHalfExtents[i] = BoxDesign.MountSize(mounts[i]) * (boxes.BlockSize * 0.5f);
        }

        ExteriorFaces = new byte[blocks.Length];
        TouchedFaces = new byte[blocks.Length];
        Shapes = new BlockShape[blocks.Length];
        _fullSet = new ulong[BlockSet.WordCount(blocks.Length)];
        _coreSet = new ulong[_fullSet.Length];
        BlockSet.Fill(_fullSet, blocks.Length);

        for (var i = 0; i < blocks.Length; i++)
        {
            ref readonly var box = ref blocks[i];
            Cells[i] = (box.At.X, box.At.Y, box.At.Z);
            Types[i] = box.Type;
            if (box.Type.Cargo > 0) CargoCapacity = BlockStateStore.MaxContainers;
            BlockOffsets[i] = boxes.Offset(box);
            BlockHalfExtents[i] = boxes.HalfExtents(box);
            Shapes[i] = box.Shape;
            if (box.Type.Core)
                BlockSet.Add(_coreSet, i);
        }

        SensorBlocks = Enumerable.Range(0, Types.Length).Where(i => Types[i].Sensor).ToArray();
        ChartingBlocks = Enumerable.Range(0, Types.Length).Where(i => Types[i].Charting).ToArray();

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
            var touched = 0;
            for (var side = 0; side < 6; side++)
            {
                if (covered[side] > 0)
                    touched |= 1 << side;
                var u = (side / 2 + 1) % 3;
                var v = (side / 2 + 2) % 3;
                if (covered[side] >= (long)(hi[u] - lo[u]) * (long)(hi[v] - lo[v]))
                    faces &= ~(1 << side);
            }
            ExteriorFaces[i] = box.Shape == BlockShape.Box ? (byte)faces : (byte)0x3F;
            TouchedFaces[i] = (byte)touched;
        }

        Sums = new BoxHullSums(boxes);

        // The tanks, in block order: a tank's index here names it wherever its kilograms are kept or sent.
        var tanks = new List<DesignTank>();
        for (var i = 0; i < blocks.Length; i++)
            if (blocks[i].Type.Tank != FuelKind.None)
                tanks.Add(new DesignTank(i, blocks[i].Type.Tank, (float)Sums.TankCapacity(i, blocks[i].Type.Tank)));
        _tanks = [.. tanks];

        // The bays, in block order: a bay's index here names it in the server's records, on the wire and in a save.
        var bays = new List<int>();
        for (var i = 0; i < blocks.Length; i++)
            if (blocks[i].Type.Bay is not null) bays.Add(i);
        Bays = [.. bays];

        // The ore bins, in block order: a bin's index here names it wherever what it holds is kept, sent or saved.
        // What a bin holds when full follows from its volume. The hoppers, in block order, are found the same way.
        var bins = new List<DesignBin>();
        var hoppers = new List<int>();
        var cubic = (double)boxes.BlockSize * boxes.BlockSize * boxes.BlockSize;
        for (var i = 0; i < blocks.Length; i++)
        {
            if (blocks[i].Type.OreCapacity > 0)
                bins.Add(new DesignBin(i, blocks[i].Type.OreCapacity * cubic * BoxDesign.Volume(blocks[i])));
            if (blocks[i].Type.Hopper) hoppers.Add(i);
        }
        _bins = [.. bins];
        Hoppers = [.. hoppers];
        Apply(rules);
    }

    private readonly DesignBin[] _bins;

    /// <summary>
    /// The design's ore bins in block order: which block each is and the kilograms it holds when full. A hull keeps
    /// what each holds with its block state, in this order. At most <see cref="OreBins.MaxBins"/>.
    /// </summary>
    public ReadOnlySpan<DesignBin> Bins => _bins;

    /// <summary>The index in <see cref="Bins"/> of the bin a block is, or -1 for a block that is no bin.</summary>
    public int BinOf(int block)
    {
        for (var i = 0; i < _bins.Length; i++)
            if (_bins[i].Block == block) return i;
        return -1;
    }

    /// <summary>The blocks that are ore hoppers, in block order. At most <see cref="OreBins.MaxHoppers"/>.</summary>
    public int[] Hoppers { get; }

    /// <summary>The blocks that are drone bays, in block order. At most <see cref="DroneBayRules.MaxBays"/>.</summary>
    public int[] Bays { get; }

    private readonly DesignTank[] _tanks;

    /// <summary>
    /// The design's tanks in block order: which block each is, its fuel and what it holds when full. A hull keeps
    /// the kilograms in each with its block state, in this order. At most <see cref="TankFuel.MaxTanks"/>.
    /// </summary>
    public ReadOnlySpan<DesignTank> Tanks => _tanks;

    /// <summary>The index in <see cref="Tanks"/> of the tank a block is, or -1 for a block that is no tank.</summary>
    public int TankOf(int block)
    {
        var low = 0;
        var high = _tanks.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            var at = _tanks[middle].Block;
            if (at == block) return middle;
            if (at < block) low = middle + 1;
            else high = middle - 1;
        }

        return -1;
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
    /// The parts mounted on block faces, ordered by parent block. A hull has a mount exactly while it has the
    /// mount's parent: there is no other state for one, on the wire or in a saved world.
    /// </summary>
    public ReadOnlySpan<DesignMount> Mounts => _boxes.Mounts;

    /// <summary>The mounts of one block, as a range of <see cref="Mounts"/>.</summary>
    public void MountsOf(int block, out int start, out int end) => _boxes.MountsOf(block, out start, out end);

    /// <summary>Each mount's centre relative to <see cref="Pivot"/>, in metres, in the hull's own frame.</summary>
    public Vector3[] MountOffsets { get; }

    /// <summary>Half of each mount's width, height and length, in metres. It stands out along its face's axis.</summary>
    public Vector3[] MountHalfExtents { get; }

    /// <summary>
    /// The lowest cell of each block, and each block's type. The rounded unit-cube shield uses these; sized geometry reads the full boxes.
    /// </summary>
    public (int X, int Y, int Z)[] Cells { get; }
    public BlockType[] Types { get; }

    /// <summary>How many blocks the design has. A block's id is its index in <see cref="Boxes"/>.</summary>
    public int BlockCount => Cells.Length;

    /// <summary>Tractor mount indices, prepared with the immutable design off the tick.</summary>
    public int[] Tractors { get; }

    /// <summary>Mining laser mount indices, in mount order, prepared with the immutable design off the tick.</summary>
    public int[] Lasers { get; }

    /// <summary>The immutable design pivot in grid units.</summary>
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

    /// <summary>
    /// For each block, the faces an adjacent box touches over any area, bits as in <see cref="ExteriorFaces"/>. A part
    /// drawn as an open frame (a tank) closes these, since a neighbour leaves out a face the part wholly covers.
    /// Wedges are not counted: a wedge draws all its faces.
    /// </summary>
    public byte[] TouchedFaces { get; }

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
        if (design.Flight.StrongestPush <= 0 || design.Flight.TurnRate.X <= 0 || design.Flight.PowerGenerated <= 0 || !BlockSet.Any(design._coreSet))
            throw new InvalidDataException($"{path}: a hull needs a core, a thruster, a gyro and a generator to fly");
        return design;
    }

    /// <summary>
    /// The first fuel that parts of a hull burn and none of its tanks holds, or <see cref="FuelKind.None"/> when
    /// every thrust part can be fed. Such parts never fire: the editor warns of it, and no shipped design has it.
    /// </summary>
    public static FuelKind MissingTank(in FlightParams flight)
    {
        for (var kind = FuelKind.Nitrogen; kind <= FuelKind.Bipropellant; kind++)
            if (flight.Burns(kind) && !(flight.FuelCapacity[kind] > 0f))
                return kind;
        return FuelKind.None;
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
    public bool NeedsRepair(uint revision, float hullPoints, int dentCount = 0) => revision > 0 || hullPoints < Flight.HullPoints || dentCount > 0;

    /// <summary>The hit points a block of this design has when intact, in the world the design was evaluated for.</summary>
    public float BlockHitPoints(int block) => _hitPoints[block];

    /// <summary>Every block's hit points when intact, in block order, and what they add up to.</summary>
    internal ReadOnlySpan<float> IntactHitPoints => _hitPoints;
    internal double IntactHitTotal { get; private set; }

    /// <summary>
    /// A hull's flight figures and data sheet read off its sums, in the world the design was evaluated for.
    /// Constant time: this is what a hull is refitted with when it loses blocks.
    /// </summary>
    public void Figures(ReadOnlySpan<HullSum> sums, out FlightParams flight, out HullStats stats, ReadOnlySpan<HoldContainer> cargo = default, ReadOnlySpan<OreBin> ore = default) =>
        Sums.Evaluate(sums, _rules, out flight, out stats, cargo, ore);

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
