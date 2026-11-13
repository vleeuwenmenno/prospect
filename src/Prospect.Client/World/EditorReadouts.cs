using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// What one block of a type adds at a size, by the same size rules the server's figures use
/// (<see cref="BoxDesign.Scale"/>), with the world's hull and recharge multipliers. For the part cards and the
/// merge preview; worked out when the size or the selection changes, never in a frame.
/// </summary>
internal struct PartFigures
{
    public float Mass, Hull, Thrust, Torque, PowerOutput, PowerDraw, Energy, Shield, Recharge, Cargo, Crew, FtlPower;

    /// <summary>
    /// The figures of one block of <paramref name="type"/> whose box holds <paramref name="cells"/> cells. A wedge
    /// fills half of its box and is worked out as the design is (<see cref="BoxDesign.ScaleHalf"/>), crew rounded
    /// down to whole people the same way.
    /// </summary>
    public static PartFigures Of(BlockType type, long cells, FlightRules rules, BlockShape shape = BlockShape.Box)
    {
        var half = shape != BlockShape.Box;
        float Scaled(string stat, float value) => value == 0f ? 0f
            : (float)(half ? BoxDesign.ScaleHalf(type, stat, value, cells) : BoxDesign.Scale(type, stat, value, cells));
        return new PartFigures
        {
            Mass = Scaled("mass", type.Mass),
            Hull = Scaled("hull", type.Hull) * rules.HullStrength,
            Thrust = Scaled("thrust", type.Thrust),
            Torque = Scaled("torque", type.Torque),
            PowerOutput = Scaled("powerOutput", type.PowerOutput),
            PowerDraw = Scaled("powerDraw", type.PowerDraw),
            Energy = Scaled("energyStorage", type.EnergyStorage),
            Shield = Scaled("shield", type.Shield),
            Recharge = Scaled("shieldRecharge", type.ShieldRecharge) * rules.ShieldRechargeMultiplier,
            Cargo = Scaled("cargo", type.Cargo),
            Crew = type.Crew == 0 ? 0f : WholeCrew(type, cells, half),
            FtlPower = Scaled("ftlPower", type.FtlPower),
        };
    }

    private static float WholeCrew(BlockType type, long cells, bool half)
    {
        if (!half)
            return (float)BoxDesign.WholeCrew(type, cells);
        var scaled = BoxDesign.ScaleHalf(type, "crew", type.Crew, cells);
        return (float)Math.Floor(scaled + Math.Abs(scaled) * 1e-9);
    }

    /// <summary>The sum of two sets of figures.</summary>
    public static PartFigures operator +(PartFigures a, PartFigures b) => new()
    {
        Mass = a.Mass + b.Mass,
        Hull = a.Hull + b.Hull,
        Thrust = a.Thrust + b.Thrust,
        Torque = a.Torque + b.Torque,
        PowerOutput = a.PowerOutput + b.PowerOutput,
        PowerDraw = a.PowerDraw + b.PowerDraw,
        Energy = a.Energy + b.Energy,
        Shield = a.Shield + b.Shield,
        Recharge = a.Recharge + b.Recharge,
        Cargo = a.Cargo + b.Cargo,
        Crew = a.Crew + b.Crew,
        FtlPower = a.FtlPower + b.FtlPower,
    };

    /// <summary>The figure a part of a role is chosen for, which the merge preview compares.</summary>
    public readonly float Main(PartRole role) => role switch
    {
        PartRole.Engine or PartRole.Thruster => Thrust,
        PartRole.Gyro => Torque,
        PartRole.Power => PowerOutput,
        PartRole.Storage => Energy,
        PartRole.Shield => Shield,
        PartRole.Cargo => Cargo,
        PartRole.Crew => Crew,
        PartRole.Drive => FtlPower,
        _ => Mass,
    };
}

/// <summary>A warning the editor gives about a design it accepts: it can be applied, but flies or powers poorly.</summary>
internal enum DesignWarning : byte
{
    /// <summary>No generator: nothing makes power, so the battery only drains.</summary>
    NoPower,

    /// <summary>The generators cannot even run what is always on.</summary>
    IdleOverload,

    /// <summary>Full thrust needs more than the generators give after what is always on: thrust runs off the battery.</summary>
    ThrustOverload,

    /// <summary>A recharging shield needs more than is left after what is always on: it drains the battery.</summary>
    ShieldOverload,

    /// <summary>A shield without a battery: under the rules a shield only recharges from stored energy, so it never does.</summary>
    NoBattery,

    /// <summary>No forward thrust at all.</summary>
    NoForward,

    /// <summary>Forward acceleration below the editor's threshold.</summary>
    SlowForward,

    /// <summary>Top speed below the editor's threshold.</summary>
    SlowTop,

    /// <summary>No turning at all on some axis.</summary>
    NoTurn,

    /// <summary>The slowest turn rate below the editor's threshold.</summary>
    SlowTurn,
}

/// <summary>Works out the warnings for a checked design's figures. Allocates nothing.</summary>
internal static class DesignWarnings
{
    public const int Count = 10;

    /// <summary>
    /// Writes the warnings that apply to <paramref name="flight"/> into <paramref name="warnings"/> and returns how
    /// many. A warning never stops a design from being applied; it says what the design will do.
    /// </summary>
    public static int Of(in FlightParams flight, EditorTuning tuning, Span<DesignWarning> warnings)
    {
        var count = 0;
        void Add(Span<DesignWarning> list, DesignWarning warning) => list[count++] = warning;
        var spare = flight.PowerGenerated - flight.IdlePower;
        if (flight.PowerGenerated <= 0f)
            Add(warnings, DesignWarning.NoPower);
        else if (spare < 0f)
            Add(warnings, DesignWarning.IdleOverload);
        if (flight.PowerGenerated > 0f && flight.ThrustPower > 0f && flight.ThrustPower > spare)
            Add(warnings, DesignWarning.ThrustOverload);
        if (flight.ShieldCapacity > 0f)
        {
            if (flight.EnergyStorage <= 0f)
                Add(warnings, DesignWarning.NoBattery);
            else if (flight.PowerGenerated > 0f && flight.ShieldPower > spare)
                Add(warnings, DesignWarning.ShieldOverload);
        }

        if (!(flight.Acceleration.Z > 0f))
            Add(warnings, DesignWarning.NoForward);
        else if (flight.Acceleration.Z < tuning.LowForwardAcceleration)
            Add(warnings, DesignWarning.SlowForward);
        if (flight.Acceleration.Z > 0f && flight.MaxSpeed < tuning.LowTopSpeed)
            Add(warnings, DesignWarning.SlowTop);
        var slowest = MathF.Min(flight.TurnRate.X, MathF.Min(flight.TurnRate.Y, flight.TurnRate.Z));
        if (!(slowest > 0f))
            Add(warnings, DesignWarning.NoTurn);
        else if (slowest * (180f / MathF.PI) < tuning.LowTurnDegrees)
            Add(warnings, DesignWarning.SlowTurn);
        return count;
    }
}
