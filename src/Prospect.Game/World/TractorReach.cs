using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Shared tractor visibility and cargo fit rules. Coordinates are grid-origin metres.</summary>
public static class TractorReach
{
    /// <summary>The emitting point on the outside of the mount's box.</summary>
    public static Vector3 Emitter(ReadOnlySpan<DesignBox> boxes, in DesignMount mount, float cell)
    {
        Span<int> low = stackalloc int[3]; Span<int> high = stackalloc int[3];
        FaceMounts.Box(boxes[mount.Parent], mount, low, high);
        var p = new Vector3(low[0] + high[0], low[1] + high[1], low[2] + high[2]) * (cell / 20);
        var axis = Facings.Axis(mount.Face);
        p[axis] = (Facings.Sign(mount.Face) > 0 ? high[axis] : low[axis]) * (cell / 10);
        return p;
    }

    /// <summary>
    /// A rack is in view when a straight line from the emitter reaches one of its faces. A face counts when the emitter
    /// is on its outer side, it carries no mount, and its part in the mount's outward half-space (the plane of the face
    /// of the block the mount sits on, and everything beyond it) is within range and has a clear line. The aim is the middle of that
    /// visible part, else its point nearest the emitter (<paramref name="nearest"/>). Of the faces in view the nearest
    /// wins. Only the rack's own frame is ignored on the way: it is open, so it never blocks its own loading. Every
    /// other live block blocks, the block the mount sits on included, which is why nothing behind the mount's face
    /// plane is in view.
    /// </summary>
    public static bool Rack(ReadOnlySpan<DesignBox> boxes, ReadOnlySpan<DesignMount> mounts, ReadOnlySpan<ulong> alive,
        float cell, int tractor, int rack, out Vector3 point, out BlockFacing side, out bool nearest)
    {
        point = default; side = default; nearest = false;
        if ((uint)tractor >= (uint)mounts.Length || (uint)rack >= (uint)boxes.Length
            || mounts[tractor].Type.Tractor is not { } spec || boxes[rack].Type.Cargo <= 0
            || !Live(alive, rack) || !Live(alive, mounts[tractor].Parent)) return false;
        var start = Emitter(boxes, mounts[tractor], cell);
        var range = spec.Range * spec.Range;
        var best = float.PositiveInfinity;
        for (var f = 0; f < 6; f++)
        {
            var facing = (BlockFacing)f;
            if (!Visible(boxes, mounts[tractor], cell, start, rack, facing, out var low, out var high)
                || Mounted(mounts, rack, facing)) continue;
            for (var k = 0; k < 2; k++)
            {
                var aim = k == 0 ? (low + high) * .5f : Vector3.Clamp(start, low, high);
                var distance = Vector3.DistanceSquared(start, aim);
                if (distance >= best || distance > range || !Clear(boxes, alive, cell, start, aim, rack)) continue;
                best = distance; point = aim; side = facing; nearest = k == 1;
                break;
            }
        }
        return best < float.PositiveInfinity;
    }

    /// <summary>The same as the full overload, without the aim kind.</summary>
    public static bool Rack(ReadOnlySpan<DesignBox> boxes, ReadOnlySpan<DesignMount> mounts, ReadOnlySpan<ulong> alive,
        float cell, int tractor, int rack, out Vector3 point, out BlockFacing side) =>
        Rack(boxes, mounts, alive, cell, tractor, rack, out point, out side, out _);

    /// <summary>The aim point <c>Rack</c> chose, worked out again from its face and aim kind (the editor draws to it).</summary>
    public static Vector3 Aim(ReadOnlySpan<DesignBox> boxes, in DesignMount mount, float cell, int rack, BlockFacing side, bool nearest)
    {
        var start = Emitter(boxes, mount, cell);
        if (!Visible(boxes, mount, cell, start, rack, side, out var low, out var high)) return Middle(boxes[rack], cell);
        return nearest ? Vector3.Clamp(start, low, high) : (low + high) * .5f;
    }

    /// <summary>
    /// The part of one rack face the emitter can face: a box in grid metres, flat along the face's axis. False when the
    /// emitter is behind that face or the whole face lies behind the mount's face plane.
    /// </summary>
    private static bool Visible(ReadOnlySpan<DesignBox> boxes, in DesignMount mount, float cell, Vector3 start,
        int rack, BlockFacing facing, out Vector3 low, out Vector3 high)
    {
        var center = Middle(boxes[rack], cell); var half = Half(boxes[rack], cell);
        var axis = Facings.Axis(facing); var sign = Facings.Sign(facing);
        var mountAxis = Facings.Axis(mount.Face);
        var plane = center[axis] + sign * half[axis];
        low = center - half; high = center + half;
        low[axis] = high[axis] = plane;
        if ((start[axis] - plane) * sign < -Epsilon) return false;
        // The half-space starts at the face of the block the mount sits on, not at the emitter above it.
        var parent = boxes[mount.Parent];
        var edge = Middle(parent, cell)[mountAxis] + Facings.Sign(mount.Face) * Half(parent, cell)[mountAxis];
        if (Facings.Sign(mount.Face) > 0) low[mountAxis] = Math.Max(low[mountAxis], edge);
        else high[mountAxis] = Math.Min(high[mountAxis], edge);
        return low[mountAxis] <= high[mountAxis] + Epsilon;
    }

    /// <summary>Range and line of sight to a free container from the emitting point.</summary>
    public static bool Container(ReadOnlySpan<DesignBox> boxes, ReadOnlySpan<ulong> alive, float cell,
        in DesignMount mount, Vector3 point)
    {
        if (mount.Type.Tractor is not { } spec || !Live(alive, mount.Parent)) return false;
        var start = Emitter(boxes, mount, cell);
        return Vector3.DistanceSquared(start, point) <= spec.Range * spec.Range && Clear(boxes, alive, cell, start, point);
    }

    /// <summary>Tests the open segment against live box and wedge solids; touching a face alone is not a blockage.</summary>
    public static bool Clear(ReadOnlySpan<DesignBox> boxes, ReadOnlySpan<ulong> alive, float cell,
        Vector3 start, Vector3 end, int except = -1)
    {
        for (var i = 0; i < boxes.Length; i++)
        {
            if (i == except || !Live(alive, i)) continue;
            var box = boxes[i]; var half = Half(box, cell);
            var origin = start - Middle(box, cell); var direction = end - start;
            var near = 0f; var far = 1f; var hit = true;
            for (var axis = 0; axis < 3 && hit; axis++)
            {
                if (Math.Abs(direction[axis]) < 1e-9f)
                { if (Math.Abs(origin[axis]) >= half[axis] - 1e-5f) hit = false; }
                else
                {
                    var a = (-half[axis] - origin[axis]) / direction[axis];
                    var b = (half[axis] - origin[axis]) / direction[axis];
                    near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
                    if (near >= far) hit = false;
                }
            }
            if (hit && box.Shape != BlockShape.Box)
            {
                var normal = BlockShapes.Slant(box.Shape, half);
                var o = Vector3.Dot(normal, origin); var d = Vector3.Dot(normal, direction);
                if (Math.Abs(d) < 1e-9f) { if (o >= 0) hit = false; }
                else if (d < 0) near = Math.Max(near, -o / d);
                else far = Math.Min(far, -o / d);
            }
            if (hit && near < far - 1e-6f && far > 1e-6f && near < 1 - 1e-6f) return false;
        }
        return true;
    }

    /// <summary>Chooses the first reachable rack that can repack existing and reserved cargo with the new size.</summary>
    public static int ChooseRack(CargoCatalog cargo, ReadOnlySpan<DesignBox> boxes, ReadOnlySpan<DesignMount> mounts,
        ReadOnlySpan<ulong> alive, float cell, int tractor, ReadOnlySpan<HoldContainer> held,
        ReadOnlySpan<HoldContainer> reserved, int size, Span<byte> occupied)
    {
        if ((uint)size >= (uint)cargo.Sizes.Length || held.Length + reserved.Length >= BlockStateStore.MaxContainers) return -1;
        Span<int> sizes = stackalloc int[BlockStateStore.MaxContainers];
        Span<CargoPlacement> placements = stackalloc CargoPlacement[BlockStateStore.MaxContainers];
        for (var rack = 0; rack < boxes.Length; rack++)
        {
            if (!Rack(boxes, mounts, alive, cell, tractor, rack, out _, out _)) continue;
            var count = 0;
            foreach (var c in held) if (c.Rack == rack) sizes[count++] = c.Size;
            foreach (var c in reserved) if (c.Rack == rack) sizes[count++] = c.Size;
            sizes[count++] = size;
            if (CargoPacking.TryPack(cargo, boxes[rack].Size, cell, sizes[..count], occupied, placements)) return rack;
        }
        return -1;
    }

    private const float Epsilon = 1e-5f;

    private static bool Mounted(ReadOnlySpan<DesignMount> mounts, int rack, BlockFacing face)
    {
        foreach (var mount in mounts) if (mount.Parent == rack && mount.Face == face) return true;
        return false;
    }

    private static bool Live(ReadOnlySpan<ulong> alive, int block) => alive.IsEmpty || BlockSet.Has(alive, block);
    private static Vector3 Middle(in DesignBox box, float cell) =>
        new Vector3(box.At.X + box.Size.X * .5f, box.At.Y + box.Size.Y * .5f, box.At.Z + box.Size.Z * .5f) * cell;
    private static Vector3 Half(in DesignBox box, float cell) => new Vector3(box.Size.X, box.Size.Y, box.Size.Z) * (cell * .5f);
}
