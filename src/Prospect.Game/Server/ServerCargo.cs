using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private readonly byte[] CargoScratch;

    private void SendCargo()
    {
        foreach (var c in _connections)
        {
            if (c.State != ConnectionState.Active || !c.CatalogueReady || !_hulls.IsAlive(c.Hull)) continue;
            var info = _hulls.Info(c.Hull.Index);
            if (!BlockSet.Has(c.ReadyDesigns, info.Design)) continue;
            var revision = _hulls.BlockStates.CargoRevision(info.BlockState);
            if (c.CargoHull == c.Hull.Packed && c.CargoRevision == revision) continue;
            var writer = new NetWriter(_sendBuffer);
            CargoMessage.Write(ref writer, c.Hull.Packed, info.Design, _tick, _hulls.BlockStates.Cargo(info.BlockState));
            if (c.Transport.Send(c.Peer, NetChannel.Reliable, writer.Written))
            { c.CargoHull = c.Hull.Packed; c.CargoRevision = revision; }
        }
    }
}
