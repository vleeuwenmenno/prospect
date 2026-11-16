using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private readonly byte[] CargoScratch;

    private void AddContainer(Connection connection, in EditorRequestMessage request)
    {
        if (!_allowDebugFill || !connection.Transport.IsInProcess)
        { ReplyEdit(connection, EditorReason.FillOff); return; }
        if (connection.EditPhase != EditorPhase.Open || request.Token != connection.EditToken)
        { SendEditorState(connection); return; }
        var invalid = EditInvalid(connection);
        if (invalid != EditorReason.None) { EndEdit(connection, invalid); return; }
        if (connection.EditHull != connection.Hull || _hulls.Info(connection.EditHull.Index).Owner != connection.PlayerId)
        { ReplyEdit(connection, EditorReason.FillRefused); return; }
        if (_tick < connection.EditNextFill) { ReplyEdit(connection, EditorReason.TooSoon, 1); return; }
        connection.EditNextFill = _tick + FillIntervalTicks;
        if (!HasRoom(0, 0)) { ReplyEdit(connection, EditorReason.HullLimit); return; }
        if ((uint)request.CargoSize >= (uint)_content.Cargo.Sizes.Length || request.CargoResource < -1
            || request.CargoResource >= _content.Galaxy.Resources.Length)
        { ReplyEdit(connection, EditorReason.FillRefused); return; }
        var size = _content.Cargo.Sizes[request.CargoSize];
        var unit = (double)_content.Cargo.UnitSize;
        var kg = request.CargoResource < 0 ? 0 : size.Units * unit * unit * unit * _content.Cargo.Density(_content.Galaxy.Resources[request.CargoResource].Id);
        var container = new HoldContainer(request.CargoRack, request.CargoSize, request.CargoResource, kg, size.Hull, default);
        var state = _hulls.Info(connection.EditHull.Index).BlockState;
        if (!_hulls.BlockStates.TryAddCargo(state, _content, container, CargoScratch))
        { ReplyEdit(connection, EditorReason.CargoFull); return; }
        _field.Refit(connection.EditHull.Index);
        ReplyEdit(connection, EditorReason.None);
    }

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
