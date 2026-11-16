namespace Prospect.Game.World;

/// <summary>A container in a rack. Resource is a galaxy index, or -1 for an empty shell; amount is kilograms.</summary>
public readonly record struct HoldContainer(int Rack, int Size, int Resource, double Amount, float Hull, GridSize At);

public sealed unsafe partial class BlockStateStore
{
    /// <summary>Storage safety bound per hull, independent of the volume its racks provide.</summary>
    public const int MaxContainers = 255;

    private static int CargoCapacityFor(HullDesign design) => design.CargoCapacity;

    private static Span<HoldContainer> CargoOf(Header* header)
    {
        var tanks = TanksOf(header);
        return new Span<HoldContainer>((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(tanks))
            + header->Tanks * sizeof(double), header->CargoCapacity);
    }

    /// <summary>The hull's containers in stable insertion order. Valid until the state is released or changed.</summary>
    public ReadOnlySpan<HoldContainer> Cargo(BlockStateHandle handle)
    {
        var header = Find(handle);
        return CargoOf(header)[..header->CargoCount];
    }

    /// <summary>Total cargo kilograms, excluding the rack structure already counted by live blocks.</summary>
    public double CargoKilograms(BlockStateHandle handle)
    {
        var total = 0.0;
        foreach (var container in Cargo(handle)) total += container.Amount;
        return total;
    }

    private static void RemoveCargoOf(Header* header, int rack)
    {
        var containers = CargoOf(header);
        var kept = 0;
        for (var i = 0; i < header->CargoCount; i++)
            if (containers[i].Rack != rack) containers[kept++] = containers[i];
        containers.Slice(kept, header->CargoCount - kept).Clear();
        header->CargoCount = kept;
    }

    /// <summary>
    /// Validates and adds one container, repacking its rack through the shared rule. Refusal changes nothing.
    /// Occupancy is caller-owned scratch with at least the rack's capacity in bytes.
    /// </summary>
    public bool TryAddCargo(BlockStateHandle handle, GameContent content, in HoldContainer container, Span<byte> occupied)
    {
        var header = Find(handle);
        var design = Design(handle);
        if (header->CargoCount == header->CargoCapacity || (uint)container.Rack >= (uint)design.BlockCount
            || !BlockSet.Has(AliveOf(header), container.Rack) || design.Types[container.Rack].Cargo <= 0
            || (uint)container.Size >= (uint)content.Cargo.Sizes.Length
            || container.Resource < -1 || container.Resource >= content.Galaxy.Resources.Length) return false;
        var size = content.Cargo.Sizes[container.Size];
        var density = container.Resource < 0 ? 0 : content.Cargo.Density(content.Galaxy.Resources[container.Resource].Id);
        var unit = (double)content.Cargo.UnitSize;
        var capacity = size.Units * unit * unit * unit * density;
        if (!double.IsFinite(capacity) || (container.Resource >= 0 && density <= 0)
            || !(container.Amount >= 0 && container.Amount <= capacity)
            || !(container.Hull > 0 && container.Hull <= size.Hull)) return false;

        Span<int> sizes = stackalloc int[MaxContainers];
        Span<int> indices = stackalloc int[MaxContainers];
        Span<CargoPlacement> placements = stackalloc CargoPlacement[MaxContainers];
        var containers = CargoOf(header);
        var count = 0;
        // Stable size sorting keeps every resource, amount and health value with its own container.
        for (var s = content.Cargo.Sizes.Length - 1; s >= 0; s--)
        {
            for (var i = 0; i < header->CargoCount; i++)
                if (containers[i].Rack == container.Rack && containers[i].Size == s)
                { sizes[count] = s; indices[count++] = i; }
            if (container.Size == s) { sizes[count] = s; indices[count++] = header->CargoCount; }
        }
        if (!CargoPacking.TryPack(content.Cargo, design.Boxes[container.Rack].Size, design.BlockSize,
            sizes[..count], occupied, placements)) return false;
        containers[header->CargoCount++] = container;
        for (var i = 0; i < count; i++)
            containers[indices[i]] = containers[indices[i]] with { At = placements[i].At };
        return true;
    }
}
