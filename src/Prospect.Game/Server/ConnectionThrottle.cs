using System.Diagnostics;

namespace Prospect.Game.Server;

/// <summary>
/// Limits how often one network address may open a connection. Every address has an allowance that starts full,
/// costs one per connection and fills up again at a steady rate; an address that has spent it is turned away
/// until the next one is earned. It is asked before a connection is given a slot or anything it sent is read.
///
/// Memory is fixed: a table of <see cref="Capacity"/> addresses, allocated once. An address is forgotten as soon
/// as its allowance is full again, which is the same as never having been seen. When every row is in use,
/// addresses without a row share one more, so a flood from many addresses is held to the rate of a single
/// address rather than given a fresh allowance each; newcomers share that fate until rows come free. Nothing
/// here allocates; it is called from the tick.
/// </summary>
public sealed class ConnectionThrottle
{
    /// <summary>Addresses remembered at once.</summary>
    public const int Capacity = 256;

    private struct Row
    {
        public ulong Origin;

        /// <summary>When the allowance will be full again. A row whose time has come is free.</summary>
        public long FullAt;
    }

    // The last row is the shared one, used only while the others are all taken.
    private readonly Row[] _rows = new Row[Capacity + 1];
    private readonly long _frequency;
    private readonly long _perConnection;
    private readonly long _window;

    /// <param name="burst">Connections an address may open at once when it has not been seen for a while.</param>
    /// <param name="perSecond">How many more it earns each second.</param>
    /// <param name="frequency">Timestamp units per second; <see cref="Stopwatch.Frequency"/> unless a test keeps its own clock.</param>
    public ConnectionThrottle(int burst, double perSecond, long frequency = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        _frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
        _perConnection = Math.Max(1, (long)(_frequency / perSecond));
        _window = _perConnection * burst;
    }

    /// <summary>
    /// Counts one connection from an address. False when the address has none left; <paramref name="retrySeconds"/>
    /// is then how long until it has, rounded up. <paramref name="now"/> is a timestamp in the units of the
    /// frequency given to the constructor.
    /// </summary>
    public bool TryTake(ulong origin, long now, out int retrySeconds)
    {
        // The allowance is kept as the moment it will be full again. Full means that moment has passed; each
        // connection moves it one connection's worth further away; none are left when it is a whole burst away.
        ref var row = ref _rows[Find(origin, now)];
        var fullAt = Math.Max(row.FullAt, now);
        var wait = fullAt + _perConnection - (now + _window);
        if (wait > 0)
        {
            retrySeconds = (int)Math.Min((wait + _frequency - 1) / _frequency, ushort.MaxValue);
            return false;
        }

        row.Origin = origin;
        row.FullAt = fullAt + _perConnection;
        retrySeconds = 0;
        return true;
    }

    /// <summary>The row that counts for an address: its own, a free one to make its own, or the shared one.</summary>
    private int Find(ulong origin, long now)
    {
        var free = -1;
        for (var i = 0; i < Capacity; i++)
        {
            ref var row = ref _rows[i];
            var live = row.FullAt > now;
            if (live && row.Origin == origin)
                return i;
            if (!live && free < 0)
                free = i;
        }

        return free >= 0 ? free : Capacity;
    }
}
