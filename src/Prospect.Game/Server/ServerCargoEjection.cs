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
        if (connection.EditHull != connection.Hull)
        { ReplyEdit(connection, EditorReason.FillRefused); return; }
        var reason = ReleaseCargo(connection, request.Hull, request.CargoRack, request.CargoIndex, request.CargoTick, request.JettisonMode);
        ReplyEdit(connection, reason, reason == EditorReason.TooSoon ? 1 : 0);
    }

    private EditorReason ReleaseCargo(Connection connection, uint hull, int rack, int index, long tick, JettisonMode mode)
    {
        if (!_hulls.IsAlive(connection.Hull) || hull != connection.Hull.Packed
            || _hulls.Info(connection.Hull.Index).Owner != connection.PlayerId
            || _hulls.State(connection.Hull.Index).Jump == JumpPhase.Tunnel)
            return EditorReason.FillRefused;
        if (_tick < connection.EditNextFill) return EditorReason.TooSoon;
        connection.EditNextFill = _tick + FillIntervalTicks;
        var state = _hulls.Info(connection.Hull.Index).BlockState;
        var hold = _hulls.BlockStates.Cargo(state);
        var design = _content.Designs[_hulls.Info(connection.Hull.Index).Design];
        if (mode is not (JettisonMode.Abandon or JettisonMode.KeepClaim)
            || connection.CargoHull != hull || tick != connection.CargoTick
            || _hulls.BlockStates.CargoRevision(state) != connection.CargoRevision
            || (uint)index >= (uint)hold.Length || hold[index].Rack != rack
            || (uint)rack >= (uint)design.Types.Length || design.Types[rack].Cargo <= 0
            || !BlockSet.Has(_hulls.Blocks(connection.Hull.Index), rack)
            || CargoEjection.OpenSide(design, rack) < 0 || !HasRoom(0, 0))
            return EditorReason.FillRefused;
        var free = CargoEjection.Make(_content.Cargo, design, hold[index], _hulls.State(connection.Hull.Index),
            _hulls.Flight(connection.Hull.Index).CenterOfMass, _tick, 0, false);
        free.ClaimOwner = mode == JettisonMode.KeepClaim ? connection.PlayerId : 0;
        if (!_containers.Add(_content, free, out _)) return EditorReason.FillRefused;
        _hulls.BlockStates.RemoveCargoAt(state, index);
        _field.Refit(connection.Hull.Index);
        return EditorReason.None;
    }
}
