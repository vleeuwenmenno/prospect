using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

/// <summary>
/// Which drones carry what, told to every client that may see both the drone and the body it pulls
/// (<see cref="DroneCarryMessage"/>). Truth only: the carrier set is the drones <see cref="CarryGroup"/> counts as
/// holding on (a fetch order past its flight to the body), so a drone parked beside a loose container carries nothing
/// and is told as carrying nothing. Takes no memory in a tick.
/// </summary>
public sealed partial class ServerHost
{
    private readonly DroneCarry[] _carrySend = new DroneCarry[DroneCarryMessage.MaxCarries];

    /// <summary>
    /// Sends each client the carries it may see, with the cadence of loose cargo; a client that held carries and now
    /// has none is sent an empty set, once. A drone or body the client cannot show is left out, as a tractor's pull is.
    /// </summary>
    private void SendDroneCarries()
    {
        if (_tick % 6 != 0) return;
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !connection.CatalogueReady) continue;
            var count = GatherCarries(connection);
            if (count == 0 && !connection.CarriesSent) continue;
            var writer = new NetWriter(_sendBuffer);
            DroneCarryMessage.Write(ref writer, _tick, _carrySend.AsSpan(0, count));
            if (connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written)) connection.CarriesSent = count != 0;
        }
    }

    /// <summary>
    /// Fills <see cref="_carrySend"/> with the carries <paramref name="connection"/> may see: every body one of the
    /// hulls it can see is fetching, whose record its own coverage rule would show it (<see cref="MaySeeContainer"/>,
    /// and <see cref="Covered"/> for a chunk).
    /// </summary>
    private int GatherCarries(Connection connection)
    {
        var count = 0;
        foreach (ref readonly var item in _containers.Items)
            if (item.FetchHull != 0 && MaySeeContainer(connection, item))
                count = AddCarriers(connection, item.Id, DroneCarryKind.Container, item.FetchHull, OrderTask.Fetch, count);
        foreach (ref readonly var item in _chunks.Items)
            if (item.FetchHull != 0 && Covered(connection.PlayerId, item.Position))
                count = AddCarriers(connection, item.Id, DroneCarryKind.Ore, item.FetchHull, OrderTask.FetchOre, count);
        return count;
    }

    /// <summary>
    /// Adds the drones of <paramref name="hull"/> holding on to body <paramref name="body"/> that
    /// <paramref name="connection"/> may see, from <paramref name="count"/> on, and answers the new count. A drone still
    /// flying to its place beside the body pulls nothing yet, so it is not added.
    /// </summary>
    private int AddCarriers(Connection connection, uint body, DroneCarryKind kind, uint hull, OrderTask task, int count)
    {
        for (var i = 0; i < _orderCount && count < _carrySend.Length; i++)
        {
            ref readonly var order = ref _orders[i];
            if (order.Task != task || order.Subject != body || order.Base.Packed != hull || order.Phase != 1) continue;
            if (!_hulls.IsAlive(order.Actor) || !MaySeeHull(connection, order.Actor.Index, _tick)
                || !BlockSet.Has(connection.ReadyDesigns, _hulls.Info(order.Actor.Index).Design)) continue;
            _carrySend[count++] = new DroneCarry { Drone = order.Actor.Packed, Body = body, Kind = kind };
        }
        return count;
    }
}
