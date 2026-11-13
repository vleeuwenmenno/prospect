using Prospect.Engine.Net;
using Prospect.Game.World;

namespace Prospect.Game.Protocol;

/// <summary>A bounded target-tagged window. Packet admission never changes simulation time.</summary>
public sealed class InputTimeline
{
    public const int Capacity = 32;
    public static readonly FlightInput Fallback = new() { Dampeners = true };
    private readonly InputRecord[] _records = new InputRecord[Capacity];
    private readonly bool[] _occupied = new bool[Capacity];

    /// <summary>Serial arithmetic accepts forward order ids across ushort rollover.</summary>
    public static bool Newer(ushort value, ushort previous) => (short)(value - previous) > 0;

    /// <summary>Clears intent when authority changes the controlled hull.</summary>
    public void Clear() => Array.Clear(_occupied);

    /// <summary>Validates every record before changing any slot; closed intervals are discarded.</summary>
    public bool Accept(ReadOnlySpan<InputRecord> records, long preparing)
    {
        if (preparing < 0) return false;
        for (var i = 0; i < records.Length; i++)
        {
            ref readonly var record = ref records[i];
            if (record.TargetTick < 0) return false;
            if (record.TargetTick < preparing) continue;
            if (record.TargetTick - preparing >= Capacity) return false;
            var at = (int)(record.TargetTick % Capacity);
            if (_occupied[at] && _records[at].TargetTick == record.TargetTick && !Same(_records[at], record))
                return false;
            for (var j = 0; j < i; j++)
                if (records[j].TargetTick == record.TargetTick && !Same(records[j], record)) return false;
        }
        foreach (ref readonly var record in records)
        {
            if (record.TargetTick < preparing) continue;
            var at = (int)(record.TargetTick % Capacity);
            _records[at] = record;
            _occupied[at] = true;
        }
        return true;
    }

    /// <summary>Consumes exactly this interval, never a later or earlier input.</summary>
    public bool Take(long tick, out InputRecord record)
    {
        var at = (int)(tick % Capacity);
        record = _records[at];
        if (!_occupied[at] || record.TargetTick != tick) return false;
        _occupied[at] = false;
        return true;
    }

    private static bool Same(in InputRecord a, in InputRecord b)
    {
        Span<byte> left = stackalloc byte[InputMessage.MaxWireSize];
        Span<byte> right = stackalloc byte[InputMessage.MaxWireSize];
        var x = new NetWriter(left);
        var y = new NetWriter(right);
        InputMessage.Write(ref x, [a], 0);
        InputMessage.Write(ref y, [b], 0);
        return x.Written.SequenceEqual(y.Written);
    }
}
