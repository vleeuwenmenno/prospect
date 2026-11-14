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

/// <summary>One figure per fuel: an amount in a hull's tanks, a capacity, a burn rate. Kilograms, or kilograms a second.</summary>
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

    /// <summary>Each figure brought down to the one in <paramref name="limit"/> where it is above it.</summary>
    public readonly FuelTanks Clamped(in FuelTanks limit) => new()
    {
        Nitrogen = Math.Min(Nitrogen, limit.Nitrogen),
        Hydrazine = Math.Min(Hydrazine, limit.Hydrazine),
        Bipropellant = Math.Min(Bipropellant, limit.Bipropellant),
    };

    /// <summary>True when every figure is a number, is not negative and is at most the one in <paramref name="limit"/>.</summary>
    public readonly bool Within(in FuelTanks limit) =>
        Nitrogen >= 0f && Nitrogen <= limit.Nitrogen && Hydrazine >= 0f && Hydrazine <= limit.Hydrazine && Bipropellant >= 0f && Bipropellant <= limit.Bipropellant;

    /// <summary>True when every figure is a finite number.</summary>
    public readonly bool IsFinite => float.IsFinite(Nitrogen) && float.IsFinite(Hydrazine) && float.IsFinite(Bipropellant);
}

/// <summary>
/// One figure for each direction a hull can be pushed and each fuel that can push it: eighteen numbers. The direction
/// is the way the hull is pushed, which is the opposite of the facing of the parts that do it.
/// </summary>
[InlineArray(Facings.Count * Fuels.Count)]
public struct ThrustTable : IEquatable<ThrustTable>
{
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
    public static int Index(BlockFacing push, FuelKind fuel) => (byte)push * Fuels.Count + (byte)fuel - 1;

    /// <summary>The figures of every fuel in one direction, added.</summary>
    public readonly float Sum(BlockFacing push)
    {
        var at = (byte)push * Fuels.Count;
        return this[at] + this[at + 1] + this[at + 2];
    }

    /// <summary>The figures in one direction of the fuels <paramref name="fuel"/> still has any of, added.</summary>
    public readonly float Sum(BlockFacing push, in FuelTanks fuel)
    {
        var at = (byte)push * Fuels.Count;
        return (fuel.Nitrogen > 0f ? this[at] : 0f) + (fuel.Hydrazine > 0f ? this[at + 1] : 0f) + (fuel.Bipropellant > 0f ? this[at + 2] : 0f);
    }

    /// <summary>True when every figure is a finite number.</summary>
    public readonly bool IsFinite
    {
        get
        {
            for (var i = 0; i < Facings.Count * Fuels.Count; i++)
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

    /// <summary>
    /// The throttle along the hull's right, up and back axes, from -127 to 127: positive pushes the hull toward
    /// +X, +Y or +Z. It is the share of that direction's thrust in use, after any cut for power; boost is not in it.
    /// </summary>
    public sbyte X;
    public sbyte Y;
    public sbyte Z;

    /// <summary>Bit <c>kind - 1</c> is set while parts burning that <see cref="FuelKind"/> push; bit 3 while boost runs.</summary>
    public byte Flags;

    /// <summary>Whether the bits are ones a step can write.</summary>
    public readonly bool IsValid => Flags < 16 && X != sbyte.MinValue && Y != sbyte.MinValue && Z != sbyte.MinValue;

    /// <summary>True while thrust is boosted.</summary>
    public readonly bool Boosted => (Flags & BoostBit) != 0;

    /// <summary>True while any part burning <paramref name="fuel"/> pushes.</summary>
    public readonly bool Burning(FuelKind fuel) => fuel != FuelKind.None && (Flags & 1 << ((byte)fuel - 1)) != 0;

    /// <summary>The throttle, from 0 to 1, of the parts that push the hull toward <paramref name="push"/>.</summary>
    public readonly float Throttle(BlockFacing push)
    {
        var along = Facings.Axis(push) switch { 0 => X, 1 => Y, _ => Z } * Facings.Sign(push);
        return along > 0 ? along / 127f : 0f;
    }

    /// <summary>
    /// The throttle, from 0 to 1, of a part that burns <paramref name="fuel"/> and faces <paramref name="facing"/>:
    /// zero while it is idle or its fuel is gone. A part pushes the hull the opposite way to its facing.
    /// </summary>
    public readonly float Firing(FuelKind fuel, BlockFacing facing) => Burning(fuel) ? Throttle(Facings.Opposite(facing)) : 0f;

    internal static ThrustState Of(float x, float y, float z, bool nitrogen, bool hydrazine, bool bipropellant, bool boost) => new()
    {
        X = Quantize(x),
        Y = Quantize(y),
        Z = Quantize(z),
        Flags = (byte)((nitrogen ? 1 : 0) | (hydrazine ? 2 : 0) | (bipropellant ? 4 : 0) | (boost ? BoostBit : 0)),
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
/// state, and they live in <see cref="HullState.Fuel"/>; this is never sent or saved.
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
