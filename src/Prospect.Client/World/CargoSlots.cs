using System.Numerics;
using Prospect.Game.World;

namespace Prospect.Client.World;

/// <summary>
/// Where a stowed container sits in its rack, in the hull's frame. The one rule the renderer draws containers by and
/// the Cargo window's callout outlines them by: the packed cell the server's packing gave the container, never a
/// second packing rule.
/// </summary>
internal static class CargoSlots
{
    /// <summary>The container's box centre in the hull's frame, in metres, before any jump stretch.</summary>
    public static Vector3 Centre(HullDesign design, CargoCatalog catalog, in HoldContainer container)
    {
        var shape = catalog.Sizes[container.Size].Shape;
        return design.BlockOffsets[container.Rack] - design.BlockHalfExtents[container.Rack]
            + new Vector3(container.At.X + shape.X * .5f, container.At.Y + shape.Y * .5f, container.At.Z + shape.Z * .5f) * catalog.UnitSize;
    }

    /// <summary>The container's half extents in metres: its packed shape in whole units.</summary>
    public static Vector3 Half(CargoCatalog catalog, in HoldContainer container)
    {
        var shape = catalog.Sizes[container.Size].Shape;
        return new Vector3(shape.X, shape.Y, shape.Z) * (catalog.UnitSize * .5f);
    }

    /// <summary>True when the container names a live rack of the design and a size class of the catalogue.</summary>
    public static bool Valid(HullDesign design, CargoCatalog catalog, in HoldContainer container) =>
        (uint)container.Rack < (uint)design.BlockOffsets.Length && (uint)container.Size < (uint)catalog.Sizes.Length;
}
