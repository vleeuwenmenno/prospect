namespace Prospect.Game.World;

public sealed unsafe partial class BlockStateStore
{
    // The bins follow the cargo records, which are a multiple of eight bytes long, so a bin's double stays aligned.
    private static Span<OreBin> BinsOf(Header* header)
    {
        var cargo = CargoOf(header);
        return new Span<OreBin>((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(cargo))
            + (nint)header->CargoCapacity * sizeof(HoldContainer), header->Bins);
    }

    /// <summary>
    /// What each ore bin of the state's design holds, in the order of <see cref="HullDesign.Bins"/>. A bin that is
    /// lost holds nothing. The span stays good until the state is released.
    /// </summary>
    public ReadOnlySpan<OreBin> Bins(BlockStateHandle handle) => BinsOf(Find(handle));

    /// <summary>Changes whenever what the bins hold changes, a bin's loss included.</summary>
    public uint OreRevision(BlockStateHandle handle) => Find(handle)->OreRevision;

    /// <summary>Everything in the hull's bins, in kilograms.</summary>
    public double OreKilograms(BlockStateHandle handle)
    {
        var total = 0.0;
        foreach (ref readonly var bin in BinsOf(Find(handle)))
            if (!bin.IsEmpty) total += bin.Kg;
        return total;
    }

    /// <summary>Kilograms of an ore the hull's live bins still take (<see cref="OreBins.Room"/>).</summary>
    public double OreRoom(BlockStateHandle handle, int resource)
    {
        var header = Find(handle);
        return OreBins.Room(BinsOf(header), _slots[handle.Index].Design!.Bins, AliveOf(header), resource);
    }

    /// <summary>
    /// Puts ore into the hull's bins by the one rule (<see cref="OreBins.Add"/>) and returns the kilograms taken;
    /// whoever calls refits the hull, since its mass changed.
    /// </summary>
    public double AddOre(BlockStateHandle handle, int resource, double kg, float grade)
    {
        var header = Find(handle);
        var taken = OreBins.Add(BinsOf(header), _slots[handle.Index].Design!.Bins, AliveOf(header), resource, kg, grade);
        if (taken > 0) header->OreRevision++;
        return taken;
    }

    /// <summary>
    /// Installs what every bin holds, whole: what a server sends of the hull a client flies, what a save holds, and
    /// what an edit kept. Nothing is trusted (<see cref="OreBins.Valid"/>); a refusal changes nothing.
    /// </summary>
    public bool TrySetOre(BlockStateHandle handle, ReadOnlySpan<OreBin> bins, int resources)
    {
        var header = Find(handle);
        if (!OreBins.Valid(bins, _slots[handle.Index].Design!, AliveOf(header), resources)) return false;
        bins.CopyTo(BinsOf(header));
        header->OreRevision++;
        return true;
    }

    // A bin that is lost loses what it held.
    private void RemoveOreOf(Header* header, HullDesign design, int block)
    {
        if (header->Bins == 0 || design.BinOf(block) is not (>= 0 and var bin)) return;
        ref var held = ref BinsOf(header)[bin];
        if (!held.IsEmpty) header->OreRevision++;
        held = OreBin.Empty;
    }
}
