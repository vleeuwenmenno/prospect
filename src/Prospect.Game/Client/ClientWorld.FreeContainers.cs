using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Client;

internal sealed partial class ClientWorld
{
    private readonly FreeContainer[] _freeContainers = new FreeContainer[FreeContainers.Capacity];
    private long _freeContainerTick = -1;
    private int _freeContainerCount;

    internal void OnFreeContainers(ref NetReader reader)
    {
        Span<FreeContainer> incoming = stackalloc FreeContainer[FreeContainers.Capacity];
        if (!FreeContainerMessage.TryRead(ref reader, _content, incoming, out var tick, out var count) || tick <= _freeContainerTick) return;
        incoming[..count].CopyTo(_freeContainers);
        _freeContainerTick = tick; _freeContainerCount = count;
    }

    internal void PublishFreeContainers(SessionSnapshot snapshot)
    {
        snapshot.FreeContainerCount = _freeContainerCount;
        snapshot.FreeContainerTick = _freeContainerTick;
        _freeContainers.AsSpan(0, _freeContainerCount).CopyTo(snapshot.FreeContainers);
    }
}
