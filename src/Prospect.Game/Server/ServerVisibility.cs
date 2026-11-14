using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Server;

public sealed partial class ServerHost
{
    private readonly Double3[] _sensorPositions = new Double3[HullStore.MaxHulls];
    private readonly uint[] _sensorOwners = new uint[HullStore.MaxHulls];
    private int _sensorCount;

    private void GatherSensors()
    {
        _sensorCount = 0;
        for (var i = 0; i < _hulls.Slots; i++)
        {
            if (!_hulls.IsAliveAt(i) || !Equipped(i, false)) continue;
            _sensorPositions[_sensorCount] = _hulls.State(i).Position;
            _sensorOwners[_sensorCount++] = _hulls.Info(i).Owner;
        }
    }

    internal bool Covered(uint owner, Double3 position)
    {
        for (var i = 0; i < _sensorCount; i++)
        {
            if (_sensorOwners[i] != owner) continue;
            if (_content.Exploration.Covers(_sensorPositions[i], position)) return true;
        }
        return false;
    }

    private void UpdateVisibility(Connection connection, long tick)
    {
        var changed = !connection.VisibilitySent;
        var words = BlockSet.WordCount(_hulls.Slots);
        for (var word = 0; word < words; word++)
        {
            ulong bits = 0;
            for (var b = 0; b < 64; b++)
            {
                var slot = word * 64 + b;
                if (slot >= _hulls.Slots) break;
                if (_hulls.IsAliveAt(slot) && (_hulls.Info(slot).Owner == connection.PlayerId
                    || Covered(connection.PlayerId, _hulls.State(slot).Position))) bits |= 1UL << b;
            }
            changed |= connection.Visible[word] != bits;
            connection.Visible[word] = bits;
        }
        if (!changed) return;
        while (words > 0 && connection.Visible[words - 1] == 0) words--;
        var writer = new NetWriter(_sendBuffer);
        writer.WriteByte((byte)MessageId.Visibility);
        writer.WriteInt64(tick);
        writer.WriteUInt16((ushort)words);
        foreach (var word in connection.Visible.AsSpan(0, words)) writer.WriteUInt64(word);
        connection.VisibilitySent = connection.Transport.Send(connection.Peer, NetChannel.Reliable, writer.Written);
    }
}
