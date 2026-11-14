using System.Numerics;
using System.Security.Cryptography;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private EditorReason CanCreateShip(Connection connection, ReadOnlySpan<char> name)
    {
        if (!_hulls.IsAlive(connection.Hull) || !_hulls.Info(connection.Hull.Index).EscapeDrone)
            return EditorReason.NotPiloting;
        if (_hulls.State(connection.Hull.Index).Jump != JumpPhase.None || connection.Order.Kind == OrderKind.Jump)
            return EditorReason.Jumping;
        if (name.IsEmpty) return EditorReason.BadName;
        var characters = 0;
        foreach (var rune in name.EnumerateRunes()) characters++;
        if (characters > _content.Editor.NewShipNameChars) return EditorReason.BadName;
        var count = 0;
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (!_hulls.IsAliveAt(i) || _hulls.Info(i).Owner != connection.PlayerId || _hulls.Info(i).EscapeDrone) continue;
            count++;
            if (name.Equals(_content.Designs[_hulls.Info(i).Design].Name, StringComparison.OrdinalIgnoreCase))
                return EditorReason.DuplicateName;
        }
        if (count >= _content.Editor.MaxOwnedShips) return EditorReason.ShipLimit;
        if (_hulls.Count >= HullStore.MaxHulls || !HasRoom(1, 1)) return EditorReason.HullLimit;
        return EditorReason.None;
    }

    private void BeginNewShip(Connection connection, ShipName name)
    {
        if (connection.EditPhase != EditorPhase.Closed)
        {
            // Do not replace or cancel another in-flight edit, even for a raw protocol request.
            connection.EditReply.NewShip = NewShipStatus.Refused;
            connection.EditReply.Reason = EditorReason.Busy;
            SendEditorState(connection);
            return;
        }
        var reason = CanCreateShip(connection, name.Text[..name.Length]);
        if (reason != EditorReason.None) { RefuseNewShip(connection, reason); return; }
        if (!_catalogue.Allows(1)) { RefuseNewShip(connection, EditorReason.CatalogueFull); return; }
        if (_tick < connection.EditNextCommit) { RefuseNewShip(connection, EditorReason.TooSoon); return; }
        EditUpload? free = null;
        foreach (var upload in _uploads) if (!upload.Held) { free = upload; break; }
        if (free is null) { RefuseNewShip(connection, EditorReason.Busy); return; }

        // Match existing bounded editor workers; the immutable input is copied, never read from live state.
        free.Held = true;
        free.Worker = CheckNewShip(name);
        connection.EditUpload = free;
        if (++_nextEditToken == 0) _nextEditToken = 1;
        connection.EditToken = _nextEditToken;
        connection.CreatingShip = true;
        connection.RemoteEdit = false;
        connection.EditHull = connection.Hull;
        connection.EditStartTick = _tick;
        connection.EditPhase = EditorPhase.Checking;
        connection.EditNextCommit = _tick + (long)_content.Editor.CommitSeconds * Prospect.Engine.Time.TickLoop.SimulationHz;
        ReplyEdit(connection, EditorReason.None);
    }

    private Task<EditChecked?> CheckNewShip(ShipName name)
    {
        var content = _content;
        var cancel = _editCancel.Token;
        return Task.Run(() =>
        {
            if (cancel.IsCancellationRequested) return null;
            try
            {
                var template = content.Pod;
                var design = HullDesign.Of(new BoxDesign(new string(name.Text[..name.Length]), template.BlockSize,
                    [new DesignBox(default, new GridSize(1, 1, 1), content.Blocks.Get(content.Editor.NewShipRoot))],
                    content.Blocks.Limits, "new ship", template.Class, template.Color), content.Rules);
                var bytes = DesignBytes.Encode(design);
                var hash = SHA256.HashData(bytes);
                return new EditChecked(DesignBytes.Decode(bytes, hash, content), bytes, hash);
            }
            catch (Exception) { return null; }
        });
    }

    private void RefuseNewShip(Connection connection, EditorReason reason)
    {
        connection.EditReply = new EditorStateMessage { Phase = connection.EditPhase, NewShip = NewShipStatus.Refused, Reason = reason };
        SendEditorState(connection);
    }

    private void DecideNewShip(Connection connection)
    {
        var worker = connection.EditUpload!.Worker!;
        var result = worker.IsCompletedSuccessfully ? worker.Result : null;
        ReleaseUpload(connection);
        if (result is null) { EndEdit(connection, EditorReason.Invalid); return; }
        var reason = CanCreateShip(connection, result.Design.Name);
        if (reason != EditorReason.None) { EndEdit(connection, reason); return; }
        if (!_catalogue.Allows(result.Bytes.Length)) { EndEdit(connection, EditorReason.CatalogueFull); return; }
        if (!NewShipPosition(connection, result.Design, out _)) { EndEdit(connection, EditorReason.NoClearSpot); return; }
        var id = _catalogue.Find(result.Hash, result.Bytes.Length);
        if (id < 0)
        {
            id = _catalogue.Append(result.Bytes, result.Hash);
            _content = _content.WithDesign(result.Design);
            _field.SetDesigns(_content.Designs);
        }
        Publish(connection, (byte)id);
    }

    private void CreateNewShip(Connection connection, byte design)
    {
        var shape = _content.Designs[design];
        var reason = CanCreateShip(connection, shape.Name);
        if (reason != EditorReason.None) { EndEdit(connection, reason); return; }
        // Recheck after DesignReady: both the drone and obstacles may have moved while the client loaded.
        if (!NewShipPosition(connection, shape, out var position)) { EndEdit(connection, EditorReason.NoClearSpot); return; }
        var next = CreateHull(connection.PlayerId, design, position);
        _hulls.State(next.Index).Orientation = _hulls.State(connection.Hull.Index).Orientation;
        connection.CreatingShip = false;
        connection.RemoteEdit = true;
        connection.EditPilot = connection.Hull;
        connection.EditHull = next;
        connection.EditStartTick = _tick;
        connection.EditPhase = EditorPhase.Open;
        if (++_nextEditToken == 0) _nextEditToken = 1;
        connection.EditToken = _nextEditToken;
        ReplyEdit(connection, EditorReason.None);
        connection.EditReply.NewShip = NewShipStatus.Created;
        SendEditorState(connection);
    }

    private bool NewShipPosition(Connection connection, HullDesign design, out Double3 position)
    {
        var drone = _hulls.State(connection.Hull.Index);
        position = drone.Position + Vector3.Transform(-Vector3.UnitZ, drone.Orientation) * _content.Editor.NewShipDistance;
        var radius = design.Flight.Radius + _content.Editor.NewShipClearance;
        // One candidate, wholly within the current sector. This avoids generating neighbours on the tick.
        var sector = Galaxy.SectorOf(drone.Position);
        var center = Galaxy.CenterOf(sector);
        var local = position.RelativeTo(center);
        if (!Galaxy.InSameSector(drone.Position, position) || Math.Abs(local.X) + radius >= Galaxy.SectorSize / 2
            || Math.Abs(local.Y) + radius >= Galaxy.SectorSize / 2 || Math.Abs(local.Z) + radius >= Galaxy.SectorSize / 2) return false;
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (!_hulls.IsAliveAt(i)) continue;
            var reach = radius + _content.Designs[_hulls.Info(i).Design].Flight.Radius;
            if (_hulls.State(i).Position.RelativeTo(position).LengthSquared() <= reach * reach) return false;
        }
        var rocks = _rocks.Sector(sector);
        for (var i = 0; i < rocks.AsteroidCount; i++)
        {
            var reach = radius + rocks.Asteroids[i].Radius;
            _rocks.Displacement(new RockRef(sector, i), _tick, out var offset, out _);
            if ((rocks.Asteroids[i].LocalPosition + offset - local).LengthSquared() <= reach * reach) return false;
        }
        // Displaced rocks may have crossed a sector wall; their generated sector does not change.
        for (var i = 0; i < _rocks.Deltas.Length; i++)
        {
            var reach = radius + _rocks.ReachAt(i);
            if (_rocks.PositionOf(_rocks.Deltas[i].Rock, _tick).RelativeTo(position).LengthSquared() <= reach * reach) return false;
        }
        return true;
    }
}
