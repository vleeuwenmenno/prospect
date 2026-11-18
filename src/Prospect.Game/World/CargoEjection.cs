using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// Shared placement and deterministic release motion for containers leaving a rack. A rack is a closed box with one open
/// face, its facing (<see cref="OpenFaces"/>): a released container leaves through it, in line with its slot.
/// </summary>
public static class CargoEjection
{
    /// <summary>
    /// The rack's open face, or -1 when it is closed: a part is mounted on it, or the rack is not a box of the cargo
    /// type. A neighbour against part of the face is asked per container (<see cref="MayLeave"/>).
    /// </summary>
    public static int OpenSide(HullDesign design, int rack)
    {
        if ((uint)rack >= (uint)design.Types.Length || design.Types[rack].Cargo <= 0) return -1;
        var face = design.Boxes[rack].Facing;
        foreach (var mount in design.Mounts) if (mount.Parent == rack && mount.Face == face) return -1;
        return (int)face;
    }

    /// <summary>
    /// True when <paramref name="held"/> can leave through its rack's open face: the face is open
    /// (<see cref="OpenSide"/>) and no live box of <paramref name="alive"/> (all of them when empty) lies against the
    /// face over the container's own footprint, so the body never appears inside a block.
    /// </summary>
    public static bool MayLeave(CargoCatalog cargo, HullDesign design, ReadOnlySpan<ulong> alive, in HoldContainer held)
    {
        var face = OpenSide(design, held.Rack);
        if (face < 0 || (uint)held.Size >= (uint)cargo.Sizes.Length) return false;
        var boxes = design.Boxes;
        var rack = boxes[held.Rack];
        var axis = face >> 1; var positive = (face & 1) == 0;
        var plane = positive ? rack.High4(axis) : rack.Low4(axis);
        // The container's footprint on the face, in quarter cells of the grid, as doubles: a unit need not be a quarter cell.
        var shape = cargo.Sizes[held.Size].Shape;
        var perUnit = cargo.UnitSize / design.BlockSize * GridPoint.PerCell;
        Span<double> low = stackalloc double[3]; Span<double> high = stackalloc double[3];
        for (var a = 0; a < 3; a++)
        {
            low[a] = rack.Low4(a) + FaceMounts.Of(held.At, a) * perUnit;
            high[a] = low[a] + FaceMounts.Of(shape, a) * perUnit;
        }
        for (var j = 0; j < boxes.Length; j++)
        {
            if (j == held.Rack || !alive.IsEmpty && !BlockSet.Has(alive, j)) continue;
            var other = boxes[j];
            if ((positive ? other.Low4(axis) : other.High4(axis)) != plane) continue;
            var over = true;
            for (var a = 0; a < 3 && over; a++)
                if (a != axis) over = Math.Min(high[a], other.High4(a)) - Math.Max(low[a], other.Low4(a)) > 1e-6;
            if (over) return false;
        }
        return true;
    }

    /// <summary>
    /// Releases a container with the hull's point velocity. A jettisoned one starts just outside the rack's open face, in
    /// line with its slot, and is pushed out along the face's normal. One thrown out by a destroyed rack
    /// (<paramref name="destroyed"/>) starts in its slot, whose walls went with the rack, and gets a seeded kick out of
    /// the open face and to the sides.
    /// </summary>
    public static FreeContainer Make(CargoCatalog cargo, HullDesign design, in HoldContainer held,
        in HullState hull, Vector3 centerOfMass, long tick, uint seed, bool destroyed)
    {
        var face = (int)design.Boxes[held.Rack].Facing;
        var axis = face / 2;
        var sign = face % 2 == 0 ? 1 : -1;
        var normal = Vector3.Zero; normal[axis] = sign;
        var shape = cargo.Sizes[held.Size].Shape;
        var half = FreeContainers.Half(cargo, held.Size);
        var offset = design.BlockOffsets[held.Rack] - design.BlockHalfExtents[held.Rack]
            + new Vector3(held.At.X + shape.X * .5f, held.At.Y + shape.Y * .5f, held.At.Z + shape.Z * .5f) * cargo.UnitSize;
        var kick = normal * cargo.JettisonSpeed;
        if (destroyed)
        {
            var random = seed == 0 ? 1u : seed;
            kick = normal * cargo.EjectionSpeed;
            kick[(axis + 1) % 3] = Signed(ref random) * cargo.EjectionSpeed;
            kick[(axis + 2) % 3] = Signed(ref random) * cargo.EjectionSpeed;
        }
        else
            offset[axis] = Exit(design, held.Rack, half, cargo);
        return new FreeContainer
        {
            Position = hull.Position + HullRotation.Rotate(offset, hull.Orientation),
            Orientation = hull.Orientation,
            Velocity = hull.Velocity + HullRotation.Rotate(Vector3.Cross(hull.AngularVelocity, offset - centerOfMass) + kick, hull.Orientation),
            AngularVelocity = hull.AngularVelocity,
            Size = held.Size,
            Resource = held.Resource,
            Amount = held.Amount,
            Hull = held.Hull,
            Born = tick,
            PickupReadyTick = tick + (long)Math.Ceiling(cargo.PickupDelaySeconds * Prospect.Engine.Time.TickLoop.SimulationHz)
        };
    }

    /// <summary>
    /// Where along the open face's axis a released container's centre starts, in the hull's frame: its half extent and a
    /// tenth of a unit outside the face, so its back clears the opening.
    /// </summary>
    public static float Exit(HullDesign design, int rack, Vector3 half, CargoCatalog cargo)
    {
        var face = (int)design.Boxes[rack].Facing;
        var axis = face / 2;
        var sign = face % 2 == 0 ? 1 : -1;
        return design.BlockOffsets[rack][axis] + sign * (design.BlockHalfExtents[rack][axis] + half[axis] + cargo.UnitSize * .1f);
    }

    private static float Signed(ref uint state)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return (state & 65535) / 32767.5f - 1;
    }
}
