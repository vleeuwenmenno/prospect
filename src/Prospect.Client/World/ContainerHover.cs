using System.Numerics;
using Brutal.Numerics;
using Prospect.Engine.Time;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>Where a hovered container is.</summary>
internal enum HoverKind : byte
{
    /// <summary>Nothing is hovered.</summary>
    None,
    /// <summary>A container in one of the player's own racks.</summary>
    Stowed,
    /// <summary>A container loose in space.</summary>
    Free,
}

/// <summary>Who may take a loose container with a tractor.</summary>
internal enum ClaimState : byte
{
    /// <summary>Not a loose container.</summary>
    None,
    /// <summary>Anyone may take it.</summary>
    Unclaimed,
    /// <summary>Only the player may take it.</summary>
    Yours,
    /// <summary>Only another player may take it.</summary>
    Others,
}

/// <summary>What the hover tooltip says about a container. Plain data, drawn by the screens.</summary>
internal readonly record struct ContainerInfo(HoverKind Kind, int Size, int Resource, double Amount, float Fill, float Hull, float MaxHull, int Units,
    ClaimState Claim, float PickupSeconds)
{
    /// <summary>True when something is hovered.</summary>
    public bool Any => Kind != HoverKind.None;
}

/// <summary>
/// The figures a container tooltip shows, worked out from the snapshot's own records, never a forecast. Font-free
/// and allocation-free, so the rules are tested without a window.
/// </summary>
internal static class ContainerHover
{
    /// <summary>Fill fraction 0 to 1 of a container: its kilograms against what its size holds of its resource.</summary>
    public static float Fill(CargoCatalog cargo, ReadOnlySpan<Resource> resources, int size, int resource, double amount)
    {
        if ((uint)size >= (uint)cargo.Sizes.Length || (uint)resource >= (uint)resources.Length) return 0;
        var unit = cargo.UnitSize;
        var capacity = cargo.Sizes[size].Units * unit * unit * unit * cargo.Density(resources[resource].Id);
        return capacity > 0 && double.IsFinite(amount) ? (float)Math.Clamp(amount / capacity, 0, 1) : 0;
    }

    /// <summary>The tooltip of a container held in a rack.</summary>
    public static ContainerInfo Stowed(CargoCatalog cargo, ReadOnlySpan<Resource> resources, in HoldContainer held)
    {
        var sizes = cargo.Sizes;
        if ((uint)held.Size >= (uint)sizes.Length) return default;
        return new ContainerInfo(HoverKind.Stowed, held.Size, held.Resource, held.Amount, Fill(cargo, resources, held.Size, held.Resource, held.Amount),
            held.Hull, sizes[held.Size].Hull, sizes[held.Size].Units, ClaimState.None, 0);
    }

    /// <summary>The tooltip of a loose container, at the snapshot's free-container tick.</summary>
    public static ContainerInfo Free(CargoCatalog cargo, ReadOnlySpan<Resource> resources, in FreeContainer item, uint player, long tick)
    {
        var sizes = cargo.Sizes;
        if ((uint)item.Size >= (uint)sizes.Length) return default;
        return new ContainerInfo(HoverKind.Free, item.Size, item.Resource, item.Amount, Fill(cargo, resources, item.Size, item.Resource, item.Amount),
            item.Hull, sizes[item.Size].Hull, sizes[item.Size].Units, ClaimOf(item.ClaimOwner, player), PickupSeconds(item.PickupReadyTick, tick));
    }

    /// <summary>The claim a loose container carries as seen by <paramref name="player"/>.</summary>
    public static ClaimState ClaimOf(uint owner, uint player) => owner == 0 ? ClaimState.Unclaimed : owner == player ? ClaimState.Yours : ClaimState.Others;

    /// <summary>Seconds until tractors may take a container released at <paramref name="readyTick"/>; zero once they may.</summary>
    public static float PickupSeconds(long readyTick, long tick) =>
        readyTick > tick ? (float)((readyTick - tick) / (double)TickLoop.SimulationHz) : 0;

    /// <summary>
    /// A stowed container as an oriented box in the world: the hull's pose and the slot's place in its rack, as the
    /// container is drawn. The result is only a pose for <see cref="TargetPicking.Container"/>; no state is kept.
    /// </summary>
    public static FreeContainer StowedPose(HullDesign design, Double3 position, Quaternion orientation, float stretch, in HoldContainer container, CargoCatalog cargo)
    {
        var shape = cargo.Sizes[container.Size].Shape;
        var offset = design.BlockOffsets[container.Rack] - design.BlockHalfExtents[container.Rack]
            + new Vector3(container.At.X + shape.X * .5f, container.At.Y + shape.Y * .5f, container.At.Z + shape.Z * .5f) * cargo.UnitSize;
        offset.Z *= stretch;
        return new FreeContainer { Position = position + Vector3.Transform(offset, orientation), Orientation = orientation, Size = container.Size };
    }
}
