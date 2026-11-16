using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Shared placement and deterministic release motion for containers leaving a rack.</summary>
public static class CargoEjection
{
    /// <summary>First unplated, unmounted face, or -1 for an enclosed rack.</summary>
    public static int OpenSide(HullDesign design, int rack)
    {
        var closed = (int)design.TouchedFaces[rack];
        foreach (var mount in design.Mounts) if (mount.Parent == rack) closed |= 1 << (int)mount.Face;
        for (var face = 0; face < 6; face++) if ((closed & 1 << face) == 0) return face;
        return -1;
    }

    /// <summary>Releases beyond the rack's open face with COM point velocity and an optional seeded kick.</summary>
    public static FreeContainer Make(CargoCatalog cargo, HullDesign design, in HoldContainer held,
        in HullState hull, Vector3 centerOfMass, long tick, uint seed, bool destroyed)
    {
        var face = OpenSide(design, held.Rack);
        if (face < 0) face = 2;
        var axis = face / 2;
        var sign = face % 2 == 0 ? 1 : -1;
        var normal = Vector3.Zero; normal[axis] = sign;
        var shape = cargo.Sizes[held.Size].Shape;
        var half = FreeContainers.Half(cargo, held.Size);
        var offset = design.BlockOffsets[held.Rack] - design.BlockHalfExtents[held.Rack]
            + new Vector3(held.At.X + shape.X * .5f, held.At.Y + shape.Y * .5f, held.At.Z + shape.Z * .5f) * cargo.UnitSize;
        offset[axis] = design.BlockOffsets[held.Rack][axis] + sign * (design.BlockHalfExtents[held.Rack][axis] + half[axis] + cargo.UnitSize * .1f);
        var kick = normal * cargo.JettisonSpeed;
        if (destroyed)
        {
            var random = seed == 0 ? 1u : seed;
            kick = normal * cargo.EjectionSpeed;
            kick[(axis + 1) % 3] = Signed(ref random) * cargo.EjectionSpeed;
            kick[(axis + 2) % 3] = Signed(ref random) * cargo.EjectionSpeed;
        }
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
            PickupReadyTick = tick + (long)Math.Ceiling(cargo.PickupDelaySeconds * HullSim.TickRate)
        };
    }

    private static float Signed(ref uint state)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return (state & 65535) / 32767.5f - 1;
    }
}
