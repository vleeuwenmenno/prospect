using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

internal sealed partial class ClientWorld
{
    private readonly OreBin[] _ore = new OreBin[OreBins.MaxBins];
    private uint _oreHull;
    private byte _oreDesign;
    private long _oreTick, _appliedOreTick;
    private int _oreCount;
    private uint _oreSerial, _appliedOreSerial;

    /// <summary>Keeps a reliable bins update until a matching hull pose and catalogue are available, as the hold's is kept.</summary>
    public void OnOreBins(ref NetReader reader)
    {
        Span<OreBin> incoming = stackalloc OreBin[OreBins.MaxBins];
        if (!OreBinsMessage.TryRead(ref reader, incoming, out var hull, out var design, out var tick, out var count)
            || design >= _content.Designs.Length) return;
        incoming[..count].CopyTo(_ore);
        _oreHull = hull; _oreDesign = design; _oreTick = tick; _oreCount = count; _oreSerial++;
        ApplyOre();
    }

    // The bins are part of what the hull weighs: once they are taken, the hull flies on the new figures, as the
    // server's does.
    private void ApplyOre()
    {
        if (_appliedOreSerial == _oreSerial || !_ownBlocks.IsValid || _oreHull != _ownHullId
            || _oreDesign != _ownDesign || _snapshotTick < _oreTick) return;
        if (!_blocks.TrySetOre(_ownBlocks, _ore.AsSpan(0, _oreCount), _content.Galaxy.Resources.Length)) return;
        _appliedOreSerial = _oreSerial;
        _appliedOreTick = _oreTick;
        OwnDesign.Figures(_blocks.Sums(_ownBlocks), out _flight, out _stats, _blocks.Cargo(_ownBlocks), _blocks.Bins(_ownBlocks));
    }

    /// <summary>Copies the own hull's bins into snapshot-owned storage; nothing is told of another hull's.</summary>
    public void PublishOre(SessionSnapshot snapshot)
    {
        if (snapshot.OwnHull < 0 || !_ownBlocks.IsValid) return;
        var bins = _blocks.Bins(_ownBlocks);
        bins.CopyTo(snapshot.OwnOre);
        snapshot.OwnOreTick = _appliedOreTick;
        snapshot.Hulls[snapshot.OwnHull].Ore = snapshot.OwnOre.AsMemory(0, bins.Length);
    }

    // What the ore chunks the own hull's beams hold ask of its power, by the server's own figure, so the predicted
    // battery agrees with the one the server keeps.
    private float OwnChunkDemand()
    {
        var demand = 0f;
        for (var c = 0; c < _oreChunkCount; c++)
        {
            ref readonly var item = ref _oreChunks[c];
            if (item.TractorHull != _ownHullId || (uint)item.TractorMount >= (uint)OwnDesign.Mounts.Length
                || OwnDesign.Mounts[item.TractorMount].Type.Tractor is not { } spec
                || !BlockSet.Has(_near.Blocks(0), OwnDesign.Mounts[item.TractorMount].Parent)) continue;
            demand += spec.Power(item.Mass);
        }

        return demand;
    }
}
