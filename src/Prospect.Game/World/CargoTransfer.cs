namespace Prospect.Game.World;

/// <summary>Retains cargo only in racks with the same grid anchor and type, and repacks before any hull is replaced.</summary>
public static class CargoTransfer
{
    /// <summary>Finds a surviving rack. Moving or replacing an occupied rack requires unloading it first.</summary>
    public static int Rack(in DesignBox source, ReadOnlySpan<DesignBox> target)
    {
        for (var i = 0; i < target.Length; i++)
            if (target[i].Type == source.Type && target[i].At == source.At) return i;
        return -1;
    }

    /// <summary>Plans the whole transfer without mutating either hull; false means Apply must be refused.</summary>
    public static bool TryPlan(GameContent content, HullDesign from, HullDesign to, ReadOnlySpan<HoldContainer> hold,
        Span<HoldContainer> output, Span<byte> occupied)
    {
        if (hold.Length > BlockStateStore.MaxContainers || output.Length < hold.Length) return false;
        for (var i = 0; i < hold.Length; i++)
        {
            if ((uint)hold[i].Rack >= (uint)from.BlockCount) return false;
            var rack = Rack(from.Boxes[hold[i].Rack], to.Boxes);
            if (rack < 0 || to.Types[rack].Cargo <= 0) return false;
            output[i] = hold[i] with { Rack = rack };
        }
        Span<int> sizes = stackalloc int[BlockStateStore.MaxContainers];
        Span<int> indices = stackalloc int[BlockStateStore.MaxContainers];
        Span<CargoPlacement> placements = stackalloc CargoPlacement[BlockStateStore.MaxContainers];
        for (var group = 0; group < hold.Length; group++)
        {
            var rack = output[group].Rack;
            var seen = false;
            for (var i = 0; i < group; i++) if (output[i].Rack == rack) { seen = true; break; }
            if (seen) continue;
            var count = 0;
            for (var size = content.Cargo.Sizes.Length - 1; size >= 0; size--)
                for (var i = 0; i < hold.Length; i++)
                    if (output[i].Rack == rack && output[i].Size == size) { sizes[count] = size; indices[count++] = i; }
            if (!CargoPacking.TryPack(content.Cargo, to.Boxes[rack].Size, to.BlockSize, sizes[..count], occupied, placements)) return false;
            for (var i = 0; i < count; i++) output[indices[i]] = output[indices[i]] with { At = placements[i].At };
        }
        return true;
    }
}
