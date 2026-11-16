using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

internal sealed partial class ClientWorld
{
    private readonly HoldContainer[] _hold = new HoldContainer[BlockStateStore.MaxContainers];
    private uint _holdHull;
    private byte _holdDesign;
    private long _holdTick;
    private int _holdCount;
    private uint _holdSerial, _appliedHoldSerial;

    /// <summary>Keeps reliable hold updates until a matching hull pose and catalogue are available.</summary>
    public void OnCargo(ref NetReader reader)
    {
        Span<HoldContainer> incoming = stackalloc HoldContainer[BlockStateStore.MaxContainers];
        if (!CargoMessage.TryRead(ref reader, incoming, out var hull, out var design, out var tick, out var count)
            || design >= _content.Designs.Length) return;
        incoming[..count].CopyTo(_hold);
        _holdHull = hull; _holdDesign = design; _holdTick = tick; _holdCount = count; _holdSerial++;
        ApplyCargo();
    }

    private void ApplyCargo()
    {
        if (_appliedHoldSerial == _holdSerial || !_ownBlocks.IsValid || _holdHull != _ownHullId
            || _holdDesign != _ownDesign || _snapshotTick < _holdTick) return;
        if (!_blocks.TrySetCargo(_ownBlocks, _content, _hold.AsSpan(0, _holdCount))) return;
        _appliedHoldSerial = _holdSerial;
        OwnDesign.Figures(_blocks.Sums(_ownBlocks), out _flight, out _stats, _blocks.Cargo(_ownBlocks));
    }

    /// <summary>Copies own cargo into snapshot-owned storage; nothing is exposed for remote hulls.</summary>
    public void PublishCargo(SessionSnapshot snapshot)
    {
        if (snapshot.OwnHull < 0 || !_ownBlocks.IsValid) return;
        var cargo = _blocks.Cargo(_ownBlocks);
        cargo.CopyTo(snapshot.OwnCargo);
        snapshot.Hulls[snapshot.OwnHull].Cargo = snapshot.OwnCargo.AsMemory(0, cargo.Length);
    }
}
