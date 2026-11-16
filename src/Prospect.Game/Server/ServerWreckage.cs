using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private WreckSettingsMessage _wreckSettings;
    private long _wreckSettingsVersion = 1;
    private readonly HullConnectivity _wreckPieces = new(GeometryAdmission.MaxBlocks);

    private void TickWreckage()
    {
        foreach (var connection in _connections)
        {
            if (connection.State != ConnectionState.Active || !connection.CatalogueReady || connection.WreckSettingsVersion == _wreckSettingsVersion) continue;
            var writer = new NetWriter(_sendBuffer);
            _wreckSettings.Write(ref writer);
            if (connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written)) connection.WreckSettingsVersion = _wreckSettingsVersion;
        }
        var slots = _hulls.Slots;
        for (var i = 0; i < slots; i++)
        {
            if (!_hulls.IsAliveAt(i)) continue;
            var revision = _hulls.Revision(i);
            if (_hulls.Info(i).ConnectivityRevision == revision) continue;
            SplitWreckage(i);
        }
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (!_hulls.IsAliveAt(i) || !_hulls.Info(i).Wreck) continue;
            ref var info = ref _hulls.Info(i);
            var position = _hulls.State(i).Position;
            var near = false;
            foreach (var connection in _connections)
            {
                if (connection.State != ConnectionState.Active || !_hulls.IsAlive(connection.Hull)) continue;
                var delta = position.RelativeTo(_hulls.State(connection.Hull.Index).Position);
                if (delta.LengthSquared() <= _content.Exploration.SensorRadius * _content.Exploration.SensorRadius)
                { near = true; break; }
            }
            if (!near) info.WreckAge++;
            if (((long)_wreckSettings.Minutes * 3600) > 0 && info.WreckAge >= ((long)_wreckSettings.Minutes * 3600))
            { _hulls.Destroy(_hulls.HandleAt(i)); continue; }
            var sector = Galaxy.SectorOf(position);
            var newer = 0;
            for (var j = 0; j < _hulls.Slots; j++)
                if (_hulls.IsAliveAt(j) && _hulls.Info(j).Wreck && Galaxy.SectorOf(_hulls.State(j).Position) == sector
                    && (_hulls.Info(j).WreckBorn > info.WreckBorn || (_hulls.Info(j).WreckBorn == info.WreckBorn && _hulls.Info(j).Entity > info.Entity))) newer++;
            if (newer >= _wreckSettings.PerSector) _hulls.Destroy(_hulls.HandleAt(i));
        }
    }

    private void SplitWreckage(int index)
    {
        var source = _hulls.Info(index).BlockState;
        var designIndex = _hulls.Info(index).Design;
        var design = _content.Designs[designIndex];
        _wreckPieces.Find(design, _hulls.Blocks(index));
        if (_wreckPieces.Count == 0)
        {
            var pilot = PilotOf(_hulls.HandleAt(index));
            if (pilot is not null) LoseHull(pilot); else _hulls.Destroy(_hulls.HandleAt(index));
            return;
        }
        _field.Refit(index);
        var parent = _hulls.State(index);
        var parentCenter = _hulls.Flight(index).CenterOfMass;
        for (var piece = 0; piece < _wreckPieces.Count; piece++)
        {
            if (piece == _wreckPieces.Kept) continue;
            // Keep the existing one-byte snapshot record budget and pre-sized hull storage bounded.
            while (_hulls.Count >= 255 || !HasRoom(1, design.BlockCount))
            {
                var oldest = -1;
                for (var candidate = 0; candidate < _hulls.Slots; candidate++)
                    if (candidate != index && _hulls.IsAliveAt(candidate) && _hulls.Info(candidate).Wreck
                        && (oldest < 0 || _hulls.Info(candidate).Entity < _hulls.Info(oldest).Entity)) oldest = candidate;
                if (oldest < 0) break;
                _hulls.Destroy(_hulls.HandleAt(oldest));
            }
            var canSpawn = _hulls.Count < 255 && HasRoom(1, design.BlockCount);
            if (canSpawn)
            {
                var wreck = CreateHull(0, designIndex, parent.Position, fullTanks: false);
                ref var info = ref _hulls.Info(wreck.Index);
                info.Wreck = true;
                info.WreckBorn = _tick;
                var target = info.BlockState;
                _hulls.BlockStates.CopyFrom(target, _hulls.BlockStates, source);
                for (var b = 0; b < design.BlockCount; b++)
                    if (_wreckPieces.Pieces[b] != piece) _hulls.BlockStates.Lose(target, b);
                _hulls.State(wreck.Index) = parent;
                _hulls.State(wreck.Index).Energy = 0;
                _hulls.State(wreck.Index).Shield = 0;
                _hulls.State(wreck.Index).Jump = JumpPhase.None;
                _hulls.State(wreck.Index).Thrust = default;
                _field.RestoreFigures(wreck.Index);
                _hulls.State(wreck.Index).Hull = (float)_hulls.BlockStates.HitTotal(target);
                _hulls.State(wreck.Index).Velocity = parent.Velocity + HullRotation.Rotate(
                    Vector3.Cross(parent.AngularVelocity, _hulls.Flight(wreck.Index).CenterOfMass - parentCenter), parent.Orientation);
                info.ConnectivityRevision = _hulls.Revision(wreck.Index);
            }
            for (var b = 0; b < design.BlockCount; b++)
                if (_wreckPieces.Pieces[b] == piece) _hulls.BlockStates.Lose(source, b);
        }
        _field.Refit(index);
        _hulls.State(index).Hull = (float)_hulls.BlockStates.HitTotal(source);
        _hulls.Info(index).ConnectivityRevision = _hulls.Revision(index);
    }
}
