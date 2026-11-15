using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Prospect.Game.World;

/// <summary>
/// The way a block faces, in the hull's own frame: one of the six grid directions. The numbers are the side numbers
/// <see cref="HullDesign.ExteriorFaces"/> uses, <c>2 * axis + (negative ? 1 : 0)</c>. A hull's nose is its -Z, so
/// <see cref="Front"/> is the way it flies and <see cref="Back"/> the way a main engine's nozzle points. A thrust
/// part pushes the hull the opposite way to its facing; for every other block the facing only says how it sits.
/// </summary>
public enum BlockFacing : byte
{
    /// <summary>+X.</summary>
    Right = 0,

    /// <summary>-X.</summary>
    Left = 1,

    /// <summary>+Y.</summary>
    Up = 2,

    /// <summary>-Y.</summary>
    Down = 3,

    /// <summary>+Z, away from the nose. What a block faces when a design says nothing.</summary>
    Back = 4,

    /// <summary>-Z, the way the nose points.</summary>
    Front = 5,
}

/// <summary>Arithmetic on <see cref="BlockFacing"/>. Allocates nothing.</summary>
public static class Facings
{
    /// <summary>How many facings there are.</summary>
    public const int Count = 6;

    private static readonly string[] Names = ["right", "left", "up", "down", "back", "front"];

    /// <summary>Whether a wire value names a facing.</summary>
    public static bool IsValid(BlockFacing facing) => (byte)facing < Count;

    /// <summary>The name a design file uses.</summary>
    public static string Name(BlockFacing facing) => IsValid(facing) ? Names[(byte)facing] : throw new InvalidDataException("Unknown block facing.");

    /// <summary>Reads the name a design file uses.</summary>
    public static BlockFacing Parse(string? name)
    {
        for (var i = 0; i < Names.Length; i++)
            if (Names[i] == name) return (BlockFacing)i;
        throw new InvalidDataException("Unknown block facing.");
    }

    /// <summary>The facing straight the other way: where a thrust part of this facing pushes the hull.</summary>
    public static BlockFacing Opposite(BlockFacing facing) => (BlockFacing)((byte)facing ^ 1);

    /// <summary>The grid axis the facing lies along: 0 for X, 1 for Y, 2 for Z.</summary>
    public static int Axis(BlockFacing facing) => (byte)facing >> 1;

    /// <summary>1 for a facing along its axis, -1 for one against it.</summary>
    public static int Sign(BlockFacing facing) => ((byte)facing & 1) == 0 ? 1 : -1;

    /// <summary>The facing as a unit vector in the hull's frame.</summary>
    public static Vector3 Vector(BlockFacing facing)
    {
        var vector = Vector3.Zero;
        vector[Axis(facing)] = Sign(facing);
        return vector;
    }

    /// <summary>
    /// The facing after a positive right-handed quarter turn about a grid axis: the same turn
    /// <see cref="BlockShapes.Rotate"/> gives a shape, so a block's shape, size and facing turn together.
    /// </summary>
    public static BlockFacing Rotate(BlockFacing facing, int axis)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)axis, 2u);
        var own = Axis(facing);
        if (own == axis) return facing;
        var a = (axis + 1) % 3;
        var b = (axis + 2) % 3;

        // +a turns to +b and +b to -a.
        return own == a ? (BlockFacing)(2 * b + ((byte)facing & 1)) : (BlockFacing)(2 * a + (((byte)facing & 1) ^ 1));
    }
}

/// <summary>The propellants. A thrust part burns one of them and a tank holds one of them.</summary>
public enum FuelKind : byte
{
    None = 0,

    /// <summary>Compressed nitrogen, puffed by cold gas thrusters.</summary>
    Nitrogen = 1,

    /// <summary>Hydrazine, burnt by hot thrusters.</summary>
    Hydrazine = 2,

    /// <summary>Fuel and oxidiser in one tank, burnt by main engines.</summary>
    Bipropellant = 3,
}

/// <summary>Names and counts for <see cref="FuelKind"/>.</summary>
public static class Fuels
{
    /// <summary>How many fuels there are, <see cref="FuelKind.None"/> not counted.</summary>
    public const int Count = 3;

    /// <summary>Reads the name a content file uses.</summary>
    public static FuelKind Parse(string? name, string source) => name switch
    {
        "nitrogen" => FuelKind.Nitrogen,
        "hydrazine" => FuelKind.Hydrazine,
        "bipropellant" => FuelKind.Bipropellant,
        _ => throw new InvalidDataException($"{source}: unknown fuel \"{name}\""),
    };
}

/// <summary>
/// One figure per fuel: what a hull's tanks of that fuel hold together, what they can hold, a burn rate.
/// Kilograms, or kilograms a second. What each tank holds is kept with the hull's block state (<see cref="TankFuel"/>).
/// </summary>
public struct FuelTanks
{
    public float Nitrogen;
    public float Hydrazine;
    public float Bipropellant;

    /// <summary>The figure for one fuel; zero for <see cref="FuelKind.None"/>.</summary>
    public readonly float this[FuelKind kind] => kind switch
    {
        FuelKind.Nitrogen => Nitrogen,
        FuelKind.Hydrazine => Hydrazine,
        FuelKind.Bipropellant => Bipropellant,
        _ => 0f,
    };

    /// <summary>True when every figure is a finite number.</summary>
    public readonly bool IsFinite => float.IsFinite(Nitrogen) && float.IsFinite(Hydrazine) && float.IsFinite(Bipropellant);
}

/// <summary>
/// One tank of a design: the block it is, the fuel it holds and the most it holds. A hull keeps the kilograms in each
/// of its tanks with its block state, in the order of <see cref="HullDesign.Tanks"/>, so a tank is named by its
/// index there on the wire, in a save and in the interface.
/// </summary>
public readonly record struct DesignTank(int Block, FuelKind Kind, float Capacity);

/// <summary>
/// The arithmetic of fuel kept tank by tank. A hull's fuel is what each of its tanks holds; what it has of a fuel
/// is its tanks of that fuel added up, in tank order, and nothing else. Burning draws from every tank of the fuel
/// at once, each in proportion to what it holds, so tanks that are equally full stay equally full. A tank that is
/// lost holds nothing. Plain arithmetic in a fixed order: the server and a predicting client get the same bits
/// from the same tanks. Allocates nothing.
/// <para>
/// A tank's kilograms are kept in double precision. One tick of one thruster burns a ten-thousandth of a
/// kilogram; taken from a single-precision figure of some hundreds of kilograms that rounds to a few units in
/// the last place, tick after tick the same way, and a large tank at a low throttle would not fall at all.
/// </para>
/// </summary>
public static class TankFuel
{
    /// <summary>
    /// The most tanks a design may have: a snapshot tells the pilot of every one, and its count is a byte. Content
    /// may allow fewer (<c>limits.maxTanks</c> in <c>blocks.json</c>), never more.
    /// </summary>
    public const int MaxTanks = byte.MaxValue;

    /// <summary>What the tanks hold of each fuel, added up in tank order.</summary>
    public static FuelTanks Total(ReadOnlySpan<double> levels, ReadOnlySpan<DesignTank> tanks)
    {
        Span<double> sums = stackalloc double[Fuels.Count + 1];
        Sum(levels, tanks, sums);
        return Fuel(sums);
    }

    /// <summary>
    /// Adds the tanks up into <paramref name="sums"/>, which is indexed by <see cref="FuelKind"/> and so has one
    /// entry more than there are fuels: the figures a step shares its burn out by.
    /// </summary>
    internal static void Sum(ReadOnlySpan<double> levels, ReadOnlySpan<DesignTank> tanks, Span<double> sums)
    {
        sums.Clear();
        for (var i = 0; i < tanks.Length; i++)
            sums[(int)tanks[i].Kind] += levels[i];
    }

    /// <summary>Sums by <see cref="FuelKind"/> as the figures a hull's state and its readout carry.</summary>
    internal static FuelTanks Fuel(ReadOnlySpan<double> sums) => new()
    {
        Nitrogen = (float)sums[(int)FuelKind.Nitrogen],
        Hydrazine = (float)sums[(int)FuelKind.Hydrazine],
        Bipropellant = (float)sums[(int)FuelKind.Bipropellant],
    };

    /// <summary>
    /// Takes <paramref name="burnt"/> kilograms of each fuel out of the tanks that hold it, each tank giving the
    /// share it holds of <paramref name="held"/>, which is what <see cref="Sum"/> made of them before the step.
    /// A fuel burnt down to nothing leaves its tanks at exactly nothing. Returns what the tanks hold afterwards.
    /// </summary>
    internal static FuelTanks Drain(Span<double> levels, ReadOnlySpan<DesignTank> tanks, ReadOnlySpan<double> held, in FuelTanks burnt)
    {
        Span<double> left = stackalloc double[Fuels.Count + 1];
        left.Clear();
        for (var i = 0; i < tanks.Length; i++)
        {
            ref readonly var tank = ref tanks[i];
            var all = held[(int)tank.Kind];
            double taken = burnt[tank.Kind];
            ref var level = ref levels[i];

            // The share is exactly one for a fuel in a single tank, so that tank loses exactly what was burnt.
            level = all > 0.0 && taken < all ? Math.Clamp(level - taken * (level / all), 0.0, tank.Capacity) : 0.0;
            left[(int)tank.Kind] += level;
        }

        return Fuel(left);
    }
}

/// <summary>
/// One figure for each direction a hull can be pushed and each kind of thrust part that can push it: three fuels and
/// the ion parts, which burn no fuel and run on power alone (<see cref="BlockType.Ion"/>). Twenty-four numbers. The
/// direction is the way the hull is pushed, which is the opposite of the facing of the parts that do it. A table of
/// fuel burnt leaves the ion column at nothing.
/// </summary>
[InlineArray(Facings.Count * Kinds)]
public struct ThrustTable : IEquatable<ThrustTable>
{
    /// <summary>The kinds of thrust part a direction has a figure for: each fuel, then the ion parts.</summary>
    public const int Kinds = Fuels.Count + 1;

    private float _first;

    /// <summary>True when every figure of the two tables has the same bits.</summary>
    public readonly bool Equals(ThrustTable other) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<ThrustTable>(in this)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<ThrustTable>(in other)));

    public override readonly bool Equals(object? other) => other is ThrustTable table && Equals(table);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(MemoryMarshal.AsBytes(new ReadOnlySpan<ThrustTable>(in this)));
        return hash.ToHashCode();
    }

    /// <summary>Where the figure of a push direction and a fuel sits in the table.</summary>
    public static int Index(BlockFacing push, FuelKind fuel) => (byte)push * Kinds + (byte)fuel - 1;

    /// <summary>Where the figure of the ion parts that push toward <paramref name="push"/> sits in the table.</summary>
    public static int IonIndex(BlockFacing push) => (byte)push * Kinds + Fuels.Count;

    /// <summary>The figures of every kind of part in one direction, added.</summary>
    public readonly float Sum(BlockFacing push)
    {
        var at = (byte)push * Kinds;
        return this[at] + this[at + 1] + this[at + 2] + this[at + 3];
    }

    /// <summary>
    /// The figures in one direction of the fuels <paramref name="fuel"/> still has any of, and of the ion parts, which
    /// need no fuel, added.
    /// </summary>
    public readonly float Sum(BlockFacing push, in FuelTanks fuel)
    {
        var at = (byte)push * Kinds;
        return (fuel.Nitrogen > 0f ? this[at] : 0f) + (fuel.Hydrazine > 0f ? this[at + 1] : 0f) + (fuel.Bipropellant > 0f ? this[at + 2] : 0f) + this[at + 3];
    }

    /// <summary>True when every figure is a finite number.</summary>
    public readonly bool IsFinite
    {
        get
        {
            for (var i = 0; i < Facings.Count * Kinds; i++)
                if (!float.IsFinite(this[i])) return false;
            return true;
        }
    }
}

/// <summary>
/// One figure for each of the six directions a hull can be pushed, indexed by <see cref="BlockFacing"/>: what the ion
/// parts pushing that way draw at full thrust, in megawatts.
/// </summary>
[InlineArray(Facings.Count)]
public struct DirectionFigures : IEquatable<DirectionFigures>
{
    private float _first;

    /// <summary>True when every figure of the two have the same bits.</summary>
    public readonly bool Equals(DirectionFigures other) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<DirectionFigures>(in this)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<DirectionFigures>(in other)));

    public override readonly bool Equals(object? other) => other is DirectionFigures figures && Equals(figures);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(MemoryMarshal.AsBytes(new ReadOnlySpan<DirectionFigures>(in this)));
        return hash.ToHashCode();
    }

    /// <summary>The figure of one direction.</summary>
    public readonly float this[BlockFacing push] => this[(int)push];

    /// <summary>The most the six figures give at once: the larger of each axis's two directions, added.</summary>
    public readonly float AtOnce =>
        Math.Max(this[0], this[1]) + Math.Max(this[2], this[3]) + Math.Max(this[4], this[5]);

    /// <summary>True when every figure is a finite number.</summary>
    public readonly bool IsFinite
    {
        get
        {
            for (var i = 0; i < Facings.Count; i++)
                if (!float.IsFinite(this[i])) return false;
            return true;
        }
    }
}

/// <summary>
/// What a hull's thrust parts did in its last step, small enough to ride in every snapshot record: which way the
/// hull was pushed and how hard, and which kinds of part fired. Simulation state like the rest of the hull: the
/// server and the predicting client write it with the same code (<see cref="FlightModel.Step"/>), dampener braking
/// included, so a viewer can light an engine when thrust starts on any hull it sees, not only its own.
/// </summary>
public struct ThrustState
{
    private const byte BoostBit = 1 << 3;
    private const byte IonBit = 1 << 4;

    /// <summary>
    /// The throttle along the hull's right, up and back axes, from -127 to 127: positive pushes the hull toward
    /// +X, +Y or +Z. It is the share of that direction's thrust in use, after any cut for power; boost is not in it.
    /// </summary>
    public sbyte X;
    public sbyte Y;
    public sbyte Z;

    /// <summary>
    /// Bit <c>kind - 1</c> is set while parts burning that <see cref="FuelKind"/> push; bit 3 while boost runs; bit 4
    /// while ion parts push.
    /// </summary>
    public byte Flags;

    /// <summary>Whether the bits are ones a step can write.</summary>
    public readonly bool IsValid => Flags < 32 && X != sbyte.MinValue && Y != sbyte.MinValue && Z != sbyte.MinValue;

    /// <summary>True while thrust is boosted.</summary>
    public readonly bool Boosted => (Flags & BoostBit) != 0;

    /// <summary>True while ion parts push: they had the power to.</summary>
    public readonly bool Ion => (Flags & IonBit) != 0;

    /// <summary>
    /// True while any part burning <paramref name="fuel"/> pushes. <see cref="FuelKind.None"/> names the ion parts,
    /// the only thrust parts that burn no fuel: it answers <see cref="Ion"/>.
    /// </summary>
    public readonly bool Burning(FuelKind fuel) => fuel == FuelKind.None ? Ion : (Flags & 1 << ((byte)fuel - 1)) != 0;

    /// <summary>The throttle, from 0 to 1, of the parts that push the hull toward <paramref name="push"/>.</summary>
    public readonly float Throttle(BlockFacing push)
    {
        var along = Facings.Axis(push) switch { 0 => X, 1 => Y, _ => Z } * Facings.Sign(push);
        return along > 0 ? along / 127f : 0f;
    }

    /// <summary>
    /// The throttle, from 0 to 1, of a part that burns <paramref name="fuel"/> and faces <paramref name="facing"/>:
    /// zero while it is idle or its fuel is gone. A part pushes the hull the opposite way to its facing. An ion part,
    /// which burns no fuel, is asked for with <see cref="FuelKind.None"/>; it is zero while it has no power.
    /// </summary>
    public readonly float Firing(FuelKind fuel, BlockFacing facing) => Burning(fuel) ? Throttle(Facings.Opposite(facing)) : 0f;

    internal static ThrustState Of(float x, float y, float z, bool nitrogen, bool hydrazine, bool bipropellant, bool ion, bool boost) => new()
    {
        X = Quantize(x),
        Y = Quantize(y),
        Z = Quantize(z),
        Flags = (byte)((nitrogen ? 1 : 0) | (hydrazine ? 2 : 0) | (bipropellant ? 4 : 0) | (boost ? BoostBit : 0) | (ion ? IonBit : 0)),
    };

    // A throttle too small for the scale still counts as firing.
    private static sbyte Quantize(float value)
    {
        var scaled = (int)MathF.Round(Math.Clamp(value, -1f, 1f) * 127f);
        return (sbyte)(scaled != 0 ? scaled : value > 0f ? 1 : value < 0f ? -1 : 0);
    }
}

/// <summary>
/// A hull's fuel in one tick: what its tanks hold, what they can hold and what its parts burnt, per fuel.
/// <see cref="FlightModel.Step"/> fills it from the same arithmetic that empties the tanks, so the pilot's readout
/// and the simulation cannot disagree. The burn is gross, in kilograms a second. Only the amounts are simulation
/// state, and they live tank by tank with the hull's block state (<see cref="HullState.Fuel"/> is their sum);
/// this is never sent or saved.
/// </summary>
public struct FuelFlow
{
    /// <summary>What the tanks hold after the step.</summary>
    public FuelTanks Amount;

    /// <summary>What the tanks the hull still has can hold.</summary>
    public FuelTanks Capacity;

    /// <summary>What the step burnt, as a rate.</summary>
    public FuelTanks Burn;

    /// <summary>Takes the amounts and capacities of <paramref name="sample"/> and moves each burn a share of the way to its, so a readout does not flicker.</summary>
    public void Blend(in FuelFlow sample, float share)
    {
        Amount = sample.Amount;
        Capacity = sample.Capacity;
        Burn.Nitrogen += (sample.Burn.Nitrogen - Burn.Nitrogen) * share;
        Burn.Hydrazine += (sample.Burn.Hydrazine - Burn.Hydrazine) * share;
        Burn.Bipropellant += (sample.Burn.Bipropellant - Burn.Bipropellant) * share;
    }
}
