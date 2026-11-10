using System.Numerics;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>A kind of block a hull can be built from, loaded from <c>content/blocks</c>.</summary>
public sealed class BlockType
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Tonnes.</summary>
    public required float Mass { get; init; }
    public required float Hull { get; init; }

    /// <summary>Kilonewtons. Engines push forward only; thrusters push in every direction.</summary>
    public float Thrust { get; init; }

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

/// <summary>Constants that turn a hull's raw figures into how it flies. Data, so they can be tuned without code.</summary>
public sealed class FlightRules
{
    public required float SpeedPerRootAcceleration { get; init; }
    public required float TurnRatePerRootAcceleration { get; init; }
    public required float BoostThrustMultiplier { get; init; }
    public required float BoostSpeedMultiplier { get; init; }
    public required float BoostPowerMultiplier { get; init; }
    public required float BoostHeatSeconds { get; init; }
    public required float CoolSeconds { get; init; }
    public required float ShieldRechargeDelaySeconds { get; init; }
    public required float CollisionSafeSpeed { get; init; }
    public required float CollisionDamagePerTonneSpeed { get; init; }
    public required float CollisionBounce { get; init; }
}

public sealed class BlockCatalog
{
    private readonly Dictionary<string, BlockType> _types;

    private BlockCatalog(Dictionary<string, BlockType> types, FlightRules rules)
    {
        _types = types;
        Rules = rules;
    }

    public FlightRules Rules { get; }

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
            BoostHeatSeconds = r.GetProperty("boostHeatSeconds"u8).GetSingle(),
            CoolSeconds = r.GetProperty("coolSeconds"u8).GetSingle(),
            ShieldRechargeDelaySeconds = r.GetProperty("shieldRechargeDelaySeconds"u8).GetSingle(),
            CollisionSafeSpeed = r.GetProperty("collisionSafeSpeed"u8).GetSingle(),
            CollisionDamagePerTonneSpeed = r.GetProperty("collisionDamagePerTonneSpeed"u8).GetSingle(),
            CollisionBounce = r.GetProperty("collisionBounce"u8).GetSingle(),
        };

        static float Number(JsonElement block, ReadOnlySpan<byte> name) =>
            block.TryGetProperty(name, out var value) ? value.GetSingle() : 0f;

        var types = new Dictionary<string, BlockType>();
        foreach (var entry in root.GetProperty("blocks"u8).EnumerateObject())
        {
            var block = entry.Value;
            types[entry.Name] = new BlockType
            {
                Id = entry.Name,
                Name = block.GetProperty("name"u8).GetString()!,
                Mass = block.GetProperty("mass"u8).GetSingle(),
                Hull = block.GetProperty("hull"u8).GetSingle(),
                Thrust = Number(block, "thrust"u8),
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

        return new BlockCatalog(types, rules);
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
    public float BlockSize { get; }

    /// <summary>Grid cells occupied by a block, and the type of block in each.</summary>
    public (int X, int Y, int Z)[] Cells { get; }
    public BlockType[] Types { get; }
    public Vector3 Color { get; }

    /// <summary>The centre of mass in grid units; the hull turns about this point and meshes are centred on it.</summary>
    public Vector3 CenterOfMass { get; private set; }
    public HullStats Stats { get; private set; }
    public FlightParams Flight { get; private set; }

    public static HullDesign Load(string path, BlockCatalog catalog)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

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
        );
        design.Derive(catalog.Rules, path);
        return design;
    }

    /// <summary>
    /// Works out how the hull flies from what it is made of: mass and moments of inertia from where the blocks
    /// sit, acceleration from thrust over mass, turning from gyro torque over inertia, and the power budget.
    /// </summary>
    private void Derive(FlightRules rules, string path)
    {
        var size = BlockSize;
        var mass = 0f;
        var weighted = Vector3.Zero;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        float hull = 0, shield = 0, recharge = 0, engineThrust = 0, thrusterThrust = 0, torque = 0;
        float generated = 0, idlePower = 0, thrustPower = 0, shieldPower = 0, storage = 0, cargo = 0;
        var crew = 0;

        for (var i = 0; i < Cells.Length; i++)
        {
            var type = Types[i];
            var cell = new Vector3(Cells[i].X, Cells[i].Y, Cells[i].Z);
            mass += type.Mass;
            weighted += cell * type.Mass;
            min = Vector3.Min(min, cell);
            max = Vector3.Max(max, cell);
            hull += type.Hull;
            shield += type.Shield;
            recharge += type.ShieldRecharge;
            torque += type.Torque;
            generated += type.PowerOutput;
            storage += type.EnergyStorage;
            cargo += type.Cargo;
            crew += type.Crew;

            // An engine is a thrust block that only pushes forward; the catalogue tells them apart by id.
            if (type.Id == "engine")
                engineThrust += type.Thrust;
            else
                thrusterThrust += type.Thrust;

            if (type.Thrust > 0)
                thrustPower += type.PowerDraw;
            else if (type.Shield > 0)
                shieldPower += type.PowerDraw;
            else
                idlePower += type.PowerDraw;
        }

        if (thrusterThrust <= 0 || torque <= 0 || generated <= 0)
            throw new InvalidDataException($"{path}: a hull needs at least one thruster, one gyro and one generator to fly");

        CenterOfMass = weighted / mass;

        // Moments of inertia about the centre of mass, each block a solid cube.
        var inertia = Vector3.Zero;
        var radius = 0f;
        for (var i = 0; i < Cells.Length; i++)
        {
            var r = (new Vector3(Cells[i].X, Cells[i].Y, Cells[i].Z) - CenterOfMass) * size;
            var m = Types[i].Mass;
            var own = m * size * size / 6f;
            inertia += new Vector3(m * (r.Y * r.Y + r.Z * r.Z) + own, m * (r.X * r.X + r.Z * r.Z) + own, m * (r.X * r.X + r.Y * r.Y) + own);
            radius = Math.Max(radius, r.Length() + size * 0.87f);
        }

        var lateral = thrusterThrust / mass;
        var forward = (engineThrust + thrusterThrust) / mass;
        var turnAcceleration = new Vector3(torque) / inertia;
        var turnRate = new Vector3(MathF.Sqrt(turnAcceleration.X), MathF.Sqrt(turnAcceleration.Y), MathF.Sqrt(turnAcceleration.Z)) * rules.TurnRatePerRootAcceleration;
        var maxSpeed = rules.SpeedPerRootAcceleration * MathF.Sqrt(forward);

        Flight = new FlightParams
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
            CollisionSafeSpeed = rules.CollisionSafeSpeed,
            CollisionDamagePerTonneSpeed = rules.CollisionDamagePerTonneSpeed,
            CollisionBounce = rules.CollisionBounce,
        };

        Stats = new HullStats
        {
            HullPoints = hull,
            ShieldCapacity = shield,
            ShieldRecharge = recharge,
            Mass = mass,
            Volume = Cells.Length * size * size * size,
            Size = (max - min + Vector3.One) * size,
            Blocks = Cells.Length,
            CargoHold = cargo,
            Crew = crew,
            Acceleration = forward,
            Deceleration = lateral,
            LateralAcceleration = lateral,
            MaxSpeed = maxSpeed,
            BoostMaxSpeed = maxSpeed * rules.BoostSpeedMultiplier,
            TurnRate = turnRate,
            PowerRequired = Flight.PowerRequired,
            PowerGenerated = generated,
            EnergyStorage = storage,
        };
    }
}
