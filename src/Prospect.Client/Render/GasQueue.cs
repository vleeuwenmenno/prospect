using Brutal.Numerics;

namespace Prospect.Client.Render;

/// <summary>
/// One frame's gas draws in the order they are drawn. A cloud is queued once per level of detail it shows at
/// (two while it crosses from one level to the next), each with its weight.
///
/// Whole clouds are laid over each other, farthest centre first; gas of two overlapping clouds is not interleaved
/// along a ray. The primary draw is the nearest one at the full level and always comes last: it is the one cloud
/// that composition with jump openings and shields can take over, and it keeps its place in the order whether it
/// is drawn in the scene or composed, so nothing changes when a shield or an opening appears.
/// </summary>
internal sealed class GasQueue
{
    /// <summary>The most draws a frame can hold besides the primary.</summary>
    public const int Capacity = 64;

    private readonly GasConstants[] _draws = new GasConstants[Capacity];
    private readonly float4[] _bounds = new float4[Capacity];
    private readonly float[] _distances = new float[Capacity];
    private float _primaryDistance;
    private int _limit = Capacity;

    /// <summary>How many draws are queued, not counting the primary.</summary>
    public int Count { get; private set; }

    /// <summary>Whether a cloud at the full level is queued; <see cref="Primary"/> is the nearest of them.</summary>
    public bool HasPrimary { get; private set; }

    /// <summary>The draw composition may take over.</summary>
    public GasConstants Primary;

    /// <summary>The primary's bounds on screen, as for <see cref="Bounds"/>.</summary>
    public float4 PrimaryBounds;

    /// <summary>Empties the queue; <paramref name="limit"/> is the most draws it then holds besides the primary.</summary>
    public void Begin(int limit = Capacity)
    {
        Count = 0;
        HasPrimary = false;
        Primary = default;
        _limit = Math.Clamp(limit, 1, Capacity);
    }

    /// <summary>Draw <paramref name="index"/>, farthest first.</summary>
    public ref readonly GasConstants this[int index] => ref _draws[index];

    /// <summary>
    /// Where on screen draw <paramref name="index"/> can show: left, top, right and bottom as shares of the view,
    /// which may reach past its edges.
    /// </summary>
    public ref readonly float4 Bounds(int index) => ref _bounds[index];

    /// <summary>The distance draw <paramref name="index"/> was queued at, which orders it.</summary>
    public float Distance(int index) => _distances[index];

    /// <summary>
    /// How many draws at the start of the order are at the far level. They are behind every other cloud, so they
    /// can be drawn into the sky before anything solid without changing the order.
    /// </summary>
    public int SkyCount
    {
        get
        {
            var count = 0;
            while (count < Count && _draws[count].Budget.Y == GasConstants.Far) count++;
            return count;
        }
    }

    /// <summary>
    /// Queues a draw. Its <paramref name="distance"/> places it; of two at the same distance the one queued first is
    /// drawn first. When the queue is full the farthest draw makes room. An overlapping pair is one draw, so it is
    /// kept or left out whole.
    /// </summary>
    public void Add(in GasConstants gas, in float4 bounds, float distance)
    {
        if (gas.Budget.Y != GasConstants.Full)
        {
            Insert(gas, bounds, distance);
            return;
        }

        if (!HasPrimary)
        {
            HasPrimary = true;
            Primary = gas;
            PrimaryBounds = bounds;
            _primaryDistance = distance;
            return;
        }

        if (distance < _primaryDistance)
        {
            var passed = Primary;
            var passedBounds = PrimaryBounds;
            var passedDistance = _primaryDistance;
            Primary = gas;
            PrimaryBounds = bounds;
            _primaryDistance = distance;
            Insert(passed, passedBounds, passedDistance);
            return;
        }

        Insert(gas, bounds, distance);
    }

    private void Insert(in GasConstants gas, in float4 bounds, float distance)
    {
        int index;
        if (Count < _limit)
        {
            index = Count++;
        }
        else
        {
            if (distance >= _distances[0]) return;
            for (var i = 1; i < Count; i++)
            {
                _draws[i - 1] = _draws[i];
                _bounds[i - 1] = _bounds[i];
                _distances[i - 1] = _distances[i];
            }
            index = Count - 1;
        }

        while (index > 0 && _distances[index - 1] < distance)
        {
            _draws[index] = _draws[index - 1];
            _bounds[index] = _bounds[index - 1];
            _distances[index] = _distances[index - 1];
            index--;
        }
        _draws[index] = gas;
        _bounds[index] = bounds;
        _distances[index] = distance;
    }
}
