using System.Diagnostics;

namespace Prospect.Game.Server;

/// <summary>
/// Slows down guessing a server's password. Wrong passwords are counted per network address: the first few cost
/// nothing, and from then on every further one locks the address out for twice as long as the one before, up to
/// five minutes. A count is forgotten a quarter of an hour after the address's last wrong password.
///
/// Memory is fixed: a table of <see cref="Capacity"/> addresses, allocated once. When every row is in use,
/// addresses that have no row share one more, so a flood from many addresses is slowed as if it were a single
/// address rather than given a fresh allowance each. Nothing here allocates; it is called from the tick.
/// </summary>
public sealed class AttemptLimiter
{
    /// <summary>Addresses remembered at once.</summary>
    public const int Capacity = 256;

    /// <summary>Wrong passwords an address may send before the first lock-out.</summary>
    public const int FreeFailures = 3;

    /// <summary>The longest lock-out, in seconds.</summary>
    public const int MaxBlockSeconds = 300;

    /// <summary>Seconds after its last wrong password that an address is forgotten. Longer than the longest lock-out.</summary>
    public const int ForgetSeconds = 900;

    private struct Row
    {
        public ulong Origin;
        public int Failures;
        public long LastFailure;
        public long BlockedUntil;
    }

    // The last row is the shared one, used only while the others are all taken.
    private readonly Row[] _rows = new Row[Capacity + 1];
    private readonly long _frequency;

    /// <param name="frequency">Timestamp units per second; <see cref="Stopwatch.Frequency"/> unless a test keeps its own clock.</param>
    public AttemptLimiter(long frequency = 0)
    {
        _frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
    }

    /// <summary>
    /// Seconds until an address may try a password again, rounded up; zero when it may try now.
    /// <paramref name="now"/> is a <see cref="Stopwatch.GetTimestamp"/> value.
    /// </summary>
    public int SecondsBlocked(ulong origin, long now)
    {
        var index = Find(origin, now, out _);
        if (index < 0)
            return 0;

        var remaining = _rows[index].BlockedUntil - now;
        return remaining <= 0 ? 0 : (int)Math.Min((remaining + _frequency - 1) / _frequency, MaxBlockSeconds);
    }

    /// <summary>Counts one wrong password from an address and, past the free ones, locks the address out.</summary>
    public void RecordFailure(ulong origin, long now)
    {
        var index = Find(origin, now, out var free);
        if (index < 0)
        {
            index = free;
            _rows[index] = new Row { Origin = origin };
        }

        ref var row = ref _rows[index];
        row.Failures = Math.Min(row.Failures + 1, int.MaxValue - 1);
        row.LastFailure = now;
        if (row.Failures < FreeFailures)
            return;

        // 2, 4, 8, ... seconds, then five minutes for good.
        var doublings = row.Failures - FreeFailures;
        var seconds = doublings >= 8 ? MaxBlockSeconds : Math.Min(2 << doublings, MaxBlockSeconds);
        row.BlockedUntil = now + seconds * _frequency;
    }

    /// <summary>
    /// The row that counts for an address: its own, or the shared one when it has none and no row is free, or
    /// -1 when it has none and one is free, which <paramref name="free"/> then names.
    /// </summary>
    private int Find(ulong origin, long now, out int free)
    {
        free = -1;
        var forget = ForgetSeconds * _frequency;
        for (var i = 0; i < Capacity; i++)
        {
            ref var row = ref _rows[i];
            var live = row.Failures > 0 && now - row.LastFailure < forget;
            if (live && row.Origin == origin)
                return i;
            if (!live && free < 0)
                free = i;
        }

        if (free >= 0)
            return -1;

        // Every row belongs to someone else. The shared row forgets like any other.
        ref var shared = ref _rows[Capacity];
        if (shared.Failures > 0 && now - shared.LastFailure >= forget)
            shared = default;
        free = Capacity;
        return Capacity;
    }
}
