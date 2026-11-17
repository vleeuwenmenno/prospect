using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// The guided path of one held container into its rack, in grid-origin metres (the frame of <see cref="TractorReach"/>).
/// </summary>
/// <param name="Slot">Centre of the packed slot the container will take.</param>
/// <param name="Staging">Outside the rack's open face, in line with the slot along the face normal.</param>
/// <param name="Exit">Just outside the emitter: where a container with no clear straight line to the staging point goes first.</param>
/// <param name="Half">The container's half extents; it flies aligned with the hull's axes, as the slot is.</param>
/// <param name="Slack">Extra range allowed once held, since the slot lies deeper in the rack than the face the range was measured to.</param>
public readonly record struct TractorGuide(Vector3 Slot, Vector3 Staging, Vector3 Exit, Vector3 Half, float Slack);

/// <summary>
/// Where a tractor leads a container: to a staging point outside the rack's face, then straight in along the face
/// normal to the slot packing gives it. The server's pull and its tests read this one rule; the pull never aims at
/// the emitter once a rack is chosen, because the blocks between the emitter and the slot are what jammed it.
/// </summary>
public static class TractorPath
{
    /// <summary>
    /// Works the path out for a container of <paramref name="size"/> that <paramref name="mount"/> is loading into
    /// <paramref name="rack"/>. The slot is the one <see cref="BlockStateStore.TryAddCargo"/> will give it: the
    /// rack's stowed containers first, largest to smallest, the new one after its own size. False when the rack
    /// is not in view of the mount or the packing has no room.
    /// </summary>
    public static bool TryGuide(CargoCatalog cargo, HullDesign design, ReadOnlySpan<ulong> alive, int mount, int rack, int size,
        ReadOnlySpan<HoldContainer> stowed, Span<byte> occupied, out TractorGuide guide)
    {
        guide = default;
        var boxes = design.Boxes; var cell = design.BlockSize;
        if ((uint)size >= (uint)cargo.Sizes.Length || stowed.Length >= BlockStateStore.MaxContainers
            || !TractorReach.Rack(boxes, design.Mounts, alive, cell, mount, rack, out _, out var side)) return false;
        Span<int> sizes = stackalloc int[BlockStateStore.MaxContainers];
        Span<CargoPlacement> placements = stackalloc CargoPlacement[BlockStateStore.MaxContainers];
        var count = 0; var mine = -1;
        for (var s = cargo.Sizes.Length - 1; s >= 0; s--)
        {
            foreach (var c in stowed) if (c.Rack == rack && c.Size == s) sizes[count++] = s;
            if (size == s) { mine = count; sizes[count++] = s; }
        }
        if (!CargoPacking.TryPack(cargo, boxes[rack].Size, cell, sizes[..count], occupied, placements)) return false;

        var box = boxes[rack]; var shape = cargo.Sizes[size].Shape; var at = placements[mine].At;
        var corner = new Vector3(box.At.X, box.At.Y, box.At.Z) * cell;
        var slot = corner + new Vector3(at.X + shape.X * .5f, at.Y + shape.Y * .5f, at.Z + shape.Z * .5f) * cargo.UnitSize;
        var half = FreeContainers.Half(cargo, size);
        var axis = Facings.Axis(side); var sign = Facings.Sign(side);
        var plane = TractorReach.Middle(box, cell)[axis] + sign * TractorReach.Half(box, cell)[axis];
        var staging = slot;
        staging[axis] = plane + sign * (half[axis] + cargo.Tractor.StagingMarginMetres);
        var exit = TractorReach.Emitter(boxes, design.Mounts[mount], cell)
            + Facings.Vector(design.Mounts[mount].Face) * (half.Length() + cargo.UnitSize * .1f);
        guide = new TractorGuide(slot, staging, exit, half, TractorReach.Half(box, cell).Length() * 2);
        return true;
    }

    /// <summary>
    /// The path of an ore chunk of half edge <paramref name="half"/> that <paramref name="mount"/> leads into the
    /// intake of <paramref name="hopper"/>: to a point outside the intake face, in line with its middle along the face
    /// normal and clear of it by the chunk's half edge and <paramref name="margin"/>, then straight in until the
    /// chunk's centre is a half edge (at most half the hopper's depth) inside the face, where the hopper takes it.
    /// False when the mount does not see the intake.
    /// </summary>
    public static bool TryGuideIntake(HullDesign design, ReadOnlySpan<ulong> alive, int mount, int hopper, float half, float margin, out TractorGuide guide)
    {
        guide = default;
        var boxes = design.Boxes; var cell = design.BlockSize;
        if (!TractorReach.Intake(boxes, design.Mounts, alive, cell, mount, hopper, out _)) return false;
        var box = boxes[hopper];
        var side = box.Facing;
        var axis = Facings.Axis(side); var sign = Facings.Sign(side);
        var middle = TractorReach.Middle(box, cell); var extent = TractorReach.Half(box, cell);
        var plane = middle[axis] + sign * extent[axis];
        var slot = middle;
        slot[axis] = plane - sign * Math.Min(half, extent[axis]);
        var staging = middle;
        staging[axis] = plane + sign * (half + margin);
        var exit = TractorReach.Emitter(boxes, design.Mounts[mount], cell)
            + Facings.Vector(design.Mounts[mount].Face) * (half * 1.75f + margin);
        guide = new TractorGuide(slot, staging, exit, new Vector3(half), extent.Length() * 2);
        return true;
    }
}
