namespace Prospect.Game.World;

/// <summary>A container in a rack. Resource is a galaxy index, or -1 for an empty shell; amount is kilograms.</summary>
public readonly record struct HoldContainer(int Rack, int Size, int Resource, double Amount, float Hull, GridSize At);

public sealed unsafe partial class BlockStateStore
{
    /// <summary>Storage safety bound per hull, independent of the volume its racks provide.</summary>
    public const int MaxContainers = 255;

    /// <summary>Server damage-only ejection hook; generic loss and wreck transfers never invoke it.</summary>
    internal Action<BlockStateHandle, int>? CargoEjection;

    /// <summary>Removes a validated selected container, retaining all other slots and their placement.</summary>
    internal void RemoveCargoAt(BlockStateHandle handle, int index)
    {
        var header = Find(handle);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)header->CargoCount);
        var cargo = CargoOf(header);
        cargo.Slice(index + 1, header->CargoCount - index - 1).CopyTo(cargo[index..]);
        cargo[--header->CargoCount] = default;
        header->CargoRevision++;
    }

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

    /// <summary>Changes whenever hold contents or placement change, including rack loss.</summary>
    public uint CargoRevision(BlockStateHandle handle) => Find(handle)->CargoRevision;

    private static void RemoveCargoOf(Header* header, int rack)
    {
        var containers = CargoOf(header);
        var kept = 0;
        for (var i = 0; i < header->CargoCount; i++)
            if (containers[i].Rack != rack) containers[kept++] = containers[i];
        containers.Slice(kept, header->CargoCount - kept).Clear();
        if (header->CargoCount != kept) header->CargoRevision++;
        header->CargoCount = kept;
    }

    /// <summary>Installs a complete validated hold atomically, without changing block health or fuel.</summary>
    public bool TrySetCargo(BlockStateHandle handle, GameContent content, ReadOnlySpan<HoldContainer> hold)
    {
        var header = Find(handle);
        var design = Design(handle);
        if (hold.Length > header->CargoCapacity) return false;
        for (var i = 0; i < hold.Length; i++)
        {
            var c = hold[i];
            if (!CargoPayloadValid(content, c) || (uint)c.Rack >= (uint)design.BlockCount
                || design.Types[c.Rack].Cargo <= 0 || !BlockSet.Has(AliveOf(header), c.Rack)) return false;
            var grid = CargoPacking.Grid(content.Cargo, design.Boxes[c.Rack].Size, design.BlockSize);
            var shape = content.Cargo.Sizes[c.Size].Shape;
            if (c.At.X < 0 || c.At.Y < 0 || c.At.Z < 0 || c.At.X > grid.X - shape.X
                || c.At.Y > grid.Y - shape.Y || c.At.Z > grid.Z - shape.Z) return false;
            for (var j = 0; j < i; j++)
            {
                var other = hold[j];
                if (c.Rack != other.Rack) continue;
                var size = content.Cargo.Sizes[other.Size].Shape;
                if (c.At.X < other.At.X + size.X && other.At.X < c.At.X + shape.X
                    && c.At.Y < other.At.Y + size.Y && other.At.Y < c.At.Y + shape.Y
                    && c.At.Z < other.At.Z + size.Z && other.At.Z < c.At.Z + shape.Z) return false;
            }
        }
        hold.CopyTo(CargoOf(header));
        header->CargoCount = hold.Length;
        header->CargoRevision++;
        return true;
    }

    /// <summary>Checks resource identity, finite payload capacity and shell health against shared content.</summary>
    public static bool CargoPayloadValid(GameContent content, in HoldContainer c)
    {
        if ((uint)c.Size >= (uint)content.Cargo.Sizes.Length || c.Resource < -1 || c.Resource >= content.Galaxy.Resources.Length) return false;
        var size = content.Cargo.Sizes[c.Size];
        var density = c.Resource < 0 ? 0 : content.Cargo.Density(content.Galaxy.Resources[c.Resource].Id);
        var unit = (double)content.Cargo.UnitSize;
        var capacity = size.Units * unit * unit * unit * density;
        return double.IsFinite(capacity) && (c.Resource < 0 || density > 0)
            && c.Amount >= 0 && c.Amount <= capacity && c.Hull > 0 && c.Hull <= size.Hull;
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
        if (!CargoPayloadValid(content, container)) return false;

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
        header->CargoRevision++;
        for (var i = 0; i < count; i++)
            containers[indices[i]] = containers[indices[i]] with { At = placements[i].At };
        return true;
    }
}
