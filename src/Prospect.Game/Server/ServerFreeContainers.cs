using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private readonly FreeContainer[] _containerSend = new FreeContainer[FreeContainers.Capacity];

    // Loose cargo has no ownership exception; it shares hulls' sensor and gas coverage policy.
    private bool MaySeeContainer(Connection viewer, in FreeContainer container) => Covered(viewer.PlayerId, container.Position);

    private void SendFreeContainers()
    {
        if (_tick % 6 != 0) return;
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !connection.CatalogueReady) continue;
            var count = 0;
            foreach (var container in _containers.Items)
                if (MaySeeContainer(connection, container)) _containerSend[count++] = container;
            if (count == 0 && !connection.FreeContainersSent) continue;
            var writer = new NetWriter(_sendBuffer);
            FreeContainerMessage.Write(ref writer, _tick, _containerSend.AsSpan(0, count));
            if (connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written)) connection.FreeContainersSent = count != 0;
        }
    }
}
