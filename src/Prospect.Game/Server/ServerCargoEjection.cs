using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private void EjectDestroyedRack(BlockStateHandle state, int rack)
    {
        for (var h = 0; h < _hulls.Slots; h++)
        {
            if (!_hulls.IsAliveAt(h) || _hulls.Info(h).BlockState != state) continue;
            var design = _content.Designs[_hulls.Info(h).Design];
            if (design.Types[rack].Cargo <= 0) return;
            var hold = _hulls.BlockStates.Cargo(state);
            for (var i = 0; i < hold.Length; i++)
            {
                if (hold[i].Rack != rack) continue;
                if (!HasRoom(0, 0)) return;
                var seed = unchecked((uint)_hulls.Info(h).Entity * 16777619u ^ (uint)_tick ^ (uint)i);
                var free = CargoEjection.Make(_content.Cargo, design, hold[i], _hulls.State(h), _hulls.Flight(h).CenterOfMass, _tick, seed, true);
                _containers.Add(_content, free, out _);
            }
            return;
        }
    }

    private void JettisonContainer(Connection connection, in EditorRequestMessage request)
    {
        if (connection.EditPhase != EditorPhase.Open || request.Token != connection.EditToken)
        { SendEditorState(connection); return; }
        var invalid = EditInvalid(connection);
        if (invalid != EditorReason.None) { EndEdit(connection, invalid); return; }
        if (connection.EditHull != connection.Hull || request.Hull != connection.Hull.Packed
            || _hulls.Info(connection.Hull.Index).Owner != connection.PlayerId)
        { ReplyEdit(connection, EditorReason.FillRefused); return; }
        if (_tick < connection.EditNextFill) { ReplyEdit(connection, EditorReason.TooSoon, 1); return; }
        connection.EditNextFill = _tick + FillIntervalTicks;
        var state = _hulls.Info(connection.Hull.Index).BlockState;
        var hold = _hulls.BlockStates.Cargo(state);
        var design = _content.Designs[_hulls.Info(connection.Hull.Index).Design];
        if (request.JettisonMode is not (JettisonMode.Abandon or JettisonMode.KeepClaim)
            || request.CargoTick != connection.CargoTick || _hulls.BlockStates.CargoRevision(state) != connection.CargoRevision
            || (uint)request.CargoIndex >= (uint)hold.Length || hold[request.CargoIndex].Rack != request.CargoRack
            || CargoEjection.OpenSide(design, request.CargoRack) < 0 || !HasRoom(0, 0))
        { ReplyEdit(connection, EditorReason.FillRefused); return; }
        var free = CargoEjection.Make(_content.Cargo, design, hold[request.CargoIndex], _hulls.State(connection.Hull.Index),
            _hulls.Flight(connection.Hull.Index).CenterOfMass, _tick, 0, false);
        free.ClaimOwner = request.JettisonMode == JettisonMode.KeepClaim ? connection.PlayerId : 0;
        if (!_containers.Add(_content, free, out _)) { ReplyEdit(connection, EditorReason.FillRefused); return; }
        _hulls.BlockStates.RemoveCargoAt(state, request.CargoIndex);
        _field.Refit(connection.Hull.Index);
        ReplyEdit(connection, EditorReason.None);
    }
}
