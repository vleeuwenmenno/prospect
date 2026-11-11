namespace Prospect.Listing;

/// <summary>Which per-address allowance a request draws on.</summary>
public enum RequestKind
{
    /// <summary>Announcements, refreshes and removals (<see cref="ListingOptions.PostsPerMinute"/>).</summary>
    Post,

    /// <summary>List requests (<see cref="ListingOptions.ListsPerMinute"/>).</summary>
    List,
}

/// <summary>What the limiter decided about one request.</summary>
/// <param name="Allowed">Whether the request may go on.</param>
/// <param name="Global">When refused, whether the whole service's limit refused it rather than the address's own.</param>
/// <param name="RetryAfter">When refused, how long until the same request would be allowed.</param>
public readonly record struct LimitDecision(bool Allowed, bool Global, TimeSpan RetryAfter);

/// <summary>
/// The request limits: one bucket for the whole service, and per address group (an IPv4 address or an IPv6 prefix) one
/// bucket for posts and one for lists.
/// </summary>
/// <remarks>
/// <para>
/// Each bucket is a generic cell rate algorithm: one timestamp per bucket, a burst of the configured size, then exactly
/// the configured rate. The interval between requests is rounded up, so the sustained rate never exceeds the setting.
/// </para>
/// <para>
/// Per-address state lives in a fixed number of slots reserved at start, so memory and the cost of housekeeping depend
/// on <see cref="ListingOptions.RateLimitAddresses"/>, never on how many addresses are seen. A slot is freed only once
/// both its buckets have refilled completely, when its state is the same as a new address's: dropping it lets no one
/// escape a limit. When every slot is in use, a new address is limited by one of a few shared overflow buckets chosen by
/// hash, while every address that already holds a slot keeps being limited exactly as before.
/// </para>
/// <para>
/// The service-wide bucket is checked before any per-address state is looked at or created, so when it is empty a flood
/// touches nothing else. It is only spent by requests the address's own limit lets through, so one address hammering
/// the service is stopped by its own limit and cannot use up everyone else's allowance.
/// </para>
/// </remarks>
public sealed class RequestLimiter
{
    private readonly Lock gate = new();
    private readonly TimeProvider time;
    private readonly Cell post;
    private readonly Cell list;
    private readonly Cell global;
    private readonly long sweepSpacing;
    private readonly Dictionary<string, int> slotOf;
    private readonly Slot[] slots;
    private readonly int[] free;
    private readonly Slot[] overflow;
    private int freeCount;
    private long globalTat = long.MinValue;
    private long lastSweep = long.MinValue;

    /// <summary>Reserves the slots and works out each bucket's interval from <paramref name="options"/>.</summary>
    public RequestLimiter(ListingOptions options, TimeProvider time)
    {
        this.time = time;
        var frequency = time.TimestampFrequency;
        post = Cell.Of(options.PostsPerMinute, 60 * frequency);
        list = Cell.Of(options.ListsPerMinute, 60 * frequency);
        global = Cell.Of(options.GlobalRequestsPerSecond, frequency);

        // A full sweep walks every slot, so it runs at most once a second, and only when a new address finds no slot.
        sweepSpacing = frequency;

        var capacity = options.RateLimitAddresses;
        slotOf = new Dictionary<string, int>(capacity, StringComparer.Ordinal);
        slots = new Slot[capacity];
        free = new int[capacity];
        for (var i = 0; i < capacity; i++)
        {
            slots[i] = Slot.Fresh;
            free[i] = capacity - 1 - i;
        }

        freeCount = capacity;
        overflow = new Slot[options.RateLimitOverflowBuckets];
        Array.Fill(overflow, Slot.Fresh);
    }

    /// <summary>Address groups that hold a slot of their own right now.</summary>
    public int TrackedCount
    {
        get
        {
            lock (gate)
            {
                return slotOf.Count;
            }
        }
    }

    /// <summary>How many groups the slot index can hold without growing. It is reserved at start and never changes.</summary>
    public int IndexCapacity
    {
        get
        {
            lock (gate)
            {
                return slotOf.EnsureCapacity(0);
            }
        }
    }

    /// <summary>
    /// Decides one request from <paramref name="group"/> (see <see cref="AddressRules.GroupKey"/>) and, when it is
    /// allowed, spends its share of the address's and the service's allowance.
    /// </summary>
    public LimitDecision TryAcquire(string group, RequestKind kind)
    {
        var now = time.GetTimestamp();
        var cell = kind == RequestKind.Post ? post : list;
        lock (gate)
        {
            if (!global.Admits(globalTat, now, out var globalWait))
            {
                return new LimitDecision(false, true, ToTimeSpan(globalWait));
            }

            ref var slot = ref Find(group, now);
            ref var tat = ref kind == RequestKind.Post ? ref slot.Post : ref slot.List;
            if (!cell.Admits(tat, now, out var wait))
            {
                return new LimitDecision(false, false, ToTimeSpan(wait));
            }

            cell.Spend(ref tat, now);
            global.Spend(ref globalTat, now);
            return new LimitDecision(true, false, TimeSpan.Zero);
        }
    }

    private ref Slot Find(string group, long now)
    {
        if (slotOf.TryGetValue(group, out var index))
        {
            return ref slots[index];
        }

        if (freeCount == 0 && now - sweepSpacing >= lastSweep)
        {
            Sweep(now);
        }

        if (freeCount == 0)
        {
            // string.GetHashCode is seeded per process, so which addresses share an overflow bucket cannot be chosen.
            return ref overflow[(int)((uint)group.GetHashCode() % (uint)overflow.Length)];
        }

        index = free[--freeCount];
        slots[index] = Slot.Fresh with { Group = group };
        slotOf.Add(group, index);
        return ref slots[index];
    }

    private void Sweep(long now)
    {
        lastSweep = now;
        for (var i = 0; i < slots.Length; i++)
        {
            ref var slot = ref slots[i];
            if (slot.Group is { } group && slot.Post <= now && slot.List <= now)
            {
                slotOf.Remove(group);
                slot = Slot.Fresh;
                free[freeCount++] = i;
            }
        }
    }

    private TimeSpan ToTimeSpan(long ticks) =>
        TimeSpan.FromSeconds((double)ticks / time.TimestampFrequency);

    /// <summary>One address group's state: when each of its buckets is next back to empty use ("theoretical arrival time").</summary>
    private struct Slot
    {
        public static readonly Slot Fresh = new() { Post = long.MinValue, List = long.MinValue };

        public string? Group;
        public long Post;
        public long List;
    }

    /// <summary>One bucket's shape: the interval between requests at the sustained rate, and how far ahead a burst may run.</summary>
    private readonly record struct Cell(long Interval, long Tolerance)
    {
        // The interval is rounded up so the sustained rate is never above the configured one.
        public static Cell Of(int perPeriod, long period)
        {
            var interval = (period + perPeriod - 1) / perPeriod;
            return new Cell(interval, (perPeriod - 1) * interval);
        }

        public bool Admits(long tat, long now, out long wait)
        {
            var ahead = Math.Max(tat, now) - now;
            wait = ahead - Tolerance;
            return wait <= 0;
        }

        public void Spend(ref long tat, long now) => tat = Math.Max(tat, now) + Interval;
    }
}
