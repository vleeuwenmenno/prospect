using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private readonly SensorGasCache? _sensorGas;
    private readonly int[] _sensorNext = new int[HullStore.MaxHulls];
    private readonly double[] _sensorRadius = new double[HullStore.MaxHulls];

    private readonly Dictionary<uint, Connection> _sensorConnections = new();

    private void GatherSensors()
    {
        _sensorConnections.Clear();
        foreach (var connection in _connections)
        {
            connection.SensorHead = -1;
            if (connection.State == ConnectionState.Active) _sensorConnections.Add(connection.PlayerId, connection);
        }
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (!_hulls.IsAliveAt(i) || !_sensorConnections.TryGetValue(_hulls.Info(i).Owner, out var connection)) continue;
            _sensorRadius[i] = Equipped(i, false) ? _content.Exploration.SensorRadius : _content.Exploration.ProximityRadius;
            _sensorNext[i] = connection.SensorHead;
            connection.SensorHead = i;
        }
    }

    /// <summary>
    /// Looks, for a share of the hulls each tick, whether the hull's centre is inside a cold gas cloud, and keeps the
    /// answer with the hull (<see cref="HullInfo.Cold"/>) for its flight steps: heat builds slower and sheds faster
    /// there. Every hull is looked at once in the content's <c>coldCloudCheckSeconds</c>, staggered by slot, so the
    /// step never asks the gas and a tick asks it for few hulls. The same smooth envelope as a hull's replicated
    /// environment (<see cref="GasSensors.EnvironmentAt"/>), which is what a predicting client goes by.
    /// </summary>
    private void TickColdClouds()
    {
        if (_sensorGas is null) return;
        var cadence = Math.Max(1, (int)MathF.Round(_content.Rules.ColdCloudCheckSeconds * Prospect.Engine.Time.TickLoop.SimulationHz));
        for (var i = (int)((cadence - _tick % cadence) % cadence); i < _hulls.Slots; i += cadence)
        {
            if (!_hulls.IsAliveAt(i)) continue;
            var position = _hulls.State(i).Position;
            _hulls.Info(i).Cold = (GasSensors.EnvironmentAt(Galaxy, _sensorGas.At(position), _content.Exploration, position, _tick) & HullEnvironment.InColdCloud) != 0;
        }
    }

    internal bool Covered(uint owner, Double3 position)
    {
        if (!_sensorConnections.TryGetValue(owner, out var connection) || connection.State != ConnectionState.Active
            || connection.PlayerId != owner) return false;
        for (var i = connection.SensorHead; i >= 0; i = _sensorNext[i])
        {
            if (!_hulls.IsAliveAt(i) || _hulls.Info(i).Owner != owner) continue;
            var d = position - _hulls.State(i).Position;
            var distanceSquared = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
            if (!(distanceSquared <= _sensorRadius[i] * _sensorRadius[i])) continue;
            if (_sensorGas is null || distanceSquared <= _content.Exploration.ProximityRadius * _content.Exploration.ProximityRadius) return true;
            if (GasSensors.Covers(Galaxy, _sensorGas.At(_hulls.State(i).Position), _content.Exploration,
                _hulls.State(i).Position, position, _sensorRadius[i] > _content.Exploration.ProximityRadius, _tick)) return true;
        }
        return false;
    }

    private void RememberContact(int first, int second, long tick)
    {
        foreach (var c in _connections)
        {
            if (c.State != ConnectionState.Active) continue;
            if (_hulls.Info(first).Owner == c.PlayerId)
            { c.ContactIds[second] = _hulls.HandleAt(second).Packed; c.ContactUntil[second] = tick + RemoteHullInterval; }
            if (_hulls.Info(second).Owner == c.PlayerId)
            { c.ContactIds[first] = _hulls.HandleAt(first).Packed; c.ContactUntil[first] = tick + RemoteHullInterval; }
        }
    }

    // All ordinary and FTL hull disclosure goes through this policy before replication.
    private bool MaySeeHull(Connection viewer, int slot, long tick) =>
        _hulls.IsAliveAt(slot) && (_hulls.Info(slot).Owner == viewer.PlayerId
            || Covered(viewer.PlayerId, _hulls.State(slot).Position)
            || (_hulls.State(slot).Jump != JumpPhase.Tunnel
                && viewer.ContactIds[slot] == _hulls.HandleAt(slot).Packed && viewer.ContactUntil[slot] >= tick));

    private void UpdateVisibility(Connection connection, long tick)
    {
        var changed = !connection.VisibilitySent;
        var destroyed = connection.Destroyed;
        for (var slot = 0; slot < _hulls.Slots; slot++)
        {
            var id = connection.VisibleIds[slot];
            if (id != 0 && (!_hulls.IsAliveAt(slot) || _hulls.HandleAt(slot).Packed != id))
                destroyed[slot] = id;
        }
        for (var i = 0; i < _hulls.Slots; i++) changed |= destroyed[i] != 0;
        var words = BlockSet.WordCount(_hulls.Slots);
        for (var word = 0; word < words; word++)
        {
            ulong bits = 0;
            for (var b = 0; b < 64; b++)
            {
                var slot = word * 64 + b;
                if (slot >= _hulls.Slots) break;
                if (MaySeeHull(connection, slot, tick)) bits |= 1UL << b;
            }
            changed |= connection.Visible[word] != bits;
            connection.Visible[word] = bits;
            for (var b = 0; b < 64 && word * 64 + b < _hulls.Slots; b++)
            {
                var slot = word * 64 + b;
                connection.VisibleIds[slot] = (bits & (1UL << b)) != 0 ? _hulls.HandleAt(slot).Packed : 0;
            }
        }
        if (!changed) return;
        while (words > 0 && connection.Visible[words - 1] == 0) words--;
        var writer = new NetWriter(_sendBuffer);
        writer.WriteByte((byte)MessageId.Visibility);
        writer.WriteInt64(tick);
        writer.WriteUInt16((ushort)words);
        foreach (var word in connection.Visible.AsSpan(0, words)) writer.WriteUInt64(word);
        var count = 0;
        for (var i = 0; i < _hulls.Slots && count < ExplorationMessage.MaxDestroyed; i++) if (destroyed[i] != 0) count++;
        writer.WriteUInt16((ushort)count);
        var left = count;
        for (var i = 0; i < _hulls.Slots && left > 0; i++)
            if (destroyed[i] != 0) { writer.WriteUInt32(destroyed[i]); left--; }
        connection.VisibilitySent = connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);
        if (connection.VisibilitySent)
            for (var i = 0; i < _hulls.Slots && count > 0; i++)
                if (destroyed[i] != 0) { destroyed[i] = 0; count--; }
    }
}
