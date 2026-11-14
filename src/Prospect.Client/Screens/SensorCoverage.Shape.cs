using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The shape of coverage: each sensor's reach in every direction on a plane, from the same
/// <see cref="GasSensors.Reach"/> the server tests with, kept until the sensor or its gas changes; which sensors
/// the command view shows and how they link into groups; and the merged outline of each group.
/// </summary>
internal sealed partial class SensorCoverage
{
    /// <summary>How many directions a sensor's boundary is sampled in, evenly round the horizontal plane.</summary>
    public const int ReachSamples = 96;

    /// <summary>The most sensors the command view shows at once: the selection's limit.</summary>
    public const int MaxActive = 64;

    /// <summary>Most outline pieces kept; one more than a ring of segments per sensor has to be cut a few times.</summary>
    public const int MaxOutlineSegments = 16384;

    /// <summary>Sensors that moved only a little are sampled again at most this many a frame; the rest keep their last shape.</summary>
    private const int RefreshBudget = 2;

    /// <summary>A sensor or plane that moved this fraction of the sensor radius has its shape sampled again.</summary>
    private const double MoveTolerance = 0.004;

    /// <summary>How far, in metres between hull centres, two ships' radars link into one picture. Display only.</summary>
    public double LinkDistance;

    private Galaxy? _galaxy;
    private ExplorationRules? _rules;
    private SensorGasCache? _gas;

    private double[] _samples = [];
    private uint[] _entryId = [];
    private Double3[] _entryAt = [];
    private double[] _entryPlane = [];
    private byte[] _entryState = [];
    private uint[] _entryVersion = [];
    private uint _versions;

    private readonly int[] _active = new int[MaxActive];
    private readonly int[] _group = new int[MaxActive];
    private readonly int[] _links = new int[MaxActive * 2];

    /// <summary>How many sensors the command view shows, and how many link lines join them.</summary>
    public int ActiveCount { get; private set; }
    public int LinkCount { get; private set; }

    private OutlineSegment[]? _outline;
    private int _outlineCount;
    private int _builtCount = -1;
    private int[] _builtActive = [];
    private uint[] _builtVersions = [];
    private int[] _builtGroup = [];
    private double[] _ringX = [], _ringZ = [], _ringSamples = [];
    private int[] _ringGroup = [];

    /// <summary>
    /// Points sampling at a galaxy and the content's rules. A change of either forgets every shape. The gas cache is
    /// built once, and only when the content lets gas matter at all, as the server does.
    /// </summary>
    public void Configure(Galaxy? galaxy, ExplorationRules rules)
    {
        if (ReferenceEquals(galaxy, _galaxy) && ReferenceEquals(rules, _rules))
            return;
        _galaxy = galaxy;
        _rules = rules;
        _gas = galaxy?.GasClouds is not null && rules.GasOpticalPenalty != 0 ? new SensorGasCache(galaxy, rules.SensorRadius) : null;
        Array.Clear(_entryState);
        _builtCount = -1;
        Radius = rules.SensorRadius;
    }

    /// <summary>
    /// Chooses the sensors the command view shows: those on the selected ships, or with nothing selected the one on
    /// the ship being flown. A selection holding no ship with a sensor shows none.
    /// </summary>
    public void SelectActive(ReadOnlySpan<uint> selection)
    {
        ActiveCount = 0;
        for (var i = 0; i < Count && ActiveCount < MaxActive; i++)
        {
            if (selection.Length > 0 ? selection.Contains(Ids[i]) : Piloted[i])
                _active[ActiveCount++] = i;
        }
    }

    /// <summary>The sensor index (into <see cref="Positions"/>) of a shown sensor.</summary>
    public int ActiveIndex(int slot) => _active[slot];

    /// <summary>The group a shown sensor belongs to, named by its lowest member's slot.</summary>
    public int GroupOf(int slot) => _group[slot];

    /// <summary>The two sensor indices of link line <paramref name="link"/>.</summary>
    public (int A, int B) Link(int link) => (_links[link * 2], _links[link * 2 + 1]);

    /// <summary>
    /// Groups the shown sensors: ships whose centres are within <see cref="LinkDistance"/> of each other, directly or
    /// through a chain of such ships, share a picture. Cheap enough for every frame and allocation-free; it also
    /// lists the links that joined them, a spanning forest rather than every pair.
    /// </summary>
    public void LinkActive()
    {
        LinkCount = 0;
        for (var s = 0; s < ActiveCount; s++)
            _group[s] = s;
        if (!(LinkDistance > 0))
            return;
        var limit = LinkDistance * LinkDistance;
        for (var a = 0; a < ActiveCount; a++)
        {
            for (var b = a + 1; b < ActiveCount; b++)
            {
                var ra = Root(a);
                var rb = Root(b);
                if (ra == rb)
                    continue;
                var d = Positions[_active[a]] - Positions[_active[b]];
                if (d.X * d.X + d.Y * d.Y + d.Z * d.Z > limit)
                    continue;
                _group[Math.Max(ra, rb)] = Math.Min(ra, rb);
                _links[LinkCount * 2] = _active[a];
                _links[LinkCount * 2 + 1] = _active[b];
                LinkCount++;
            }
        }

        for (var s = 0; s < ActiveCount; s++)
            _group[s] = Root(s);
    }

    private int Root(int slot)
    {
        while (_group[slot] != slot)
            slot = _group[slot];
        return slot;
    }

    /// <summary>
    /// Brings the sampled reach of the shown (or every) sensor up to date for the horizontal plane at
    /// <paramref name="planeY"/>. A sensor never sampled, replaced, or far from where it was sampled is worked out at
    /// once; one that only moved a little, or whose plane did, waits for a share of the frame's small budget and
    /// draws its last shape meanwhile.
    /// </summary>
    public void Refresh(double planeY, bool activeOnly)
    {
        if (_rules is null)
            return;
        if (_entryState.Length < Positions.Length)
        {
            var capacity = Positions.Length;
            _samples = new double[capacity * ReachSamples];
            _entryId = new uint[capacity];
            _entryAt = new Double3[capacity];
            _entryPlane = new double[capacity];
            _entryState = new byte[capacity];
            _entryVersion = new uint[capacity];
        }

        var radius = _rules.SensorRadius;
        var budget = RefreshBudget;
        var shown = activeOnly ? ActiveCount : Count;
        for (var s = 0; s < shown; s++)
        {
            var i = activeOnly ? _active[s] : s;
            var drift = Positions[i] - _entryAt[i];
            var moved = Math.Max(Math.Max(Math.Abs(drift.X), Math.Abs(drift.Y)), Math.Max(Math.Abs(drift.Z), Math.Abs(planeY - _entryPlane[i])));
            if (_entryState[i] == 0 || _entryId[i] != Ids[i] || moved > radius)
                Compute(i, planeY);
            else if (moved > radius * MoveTolerance && budget > 0)
            {
                Compute(i, planeY);
                budget--;
            }
        }
    }

    /// <summary>
    /// Samples one sensor on the plane. In its own plane each direction is exactly <see cref="GasSensors.Reach"/>. In
    /// another, the boundary is where the ray's reach meets the plane, found by a few steps of: aim at the point
    /// the last reach gave, ask the reach there again. The reach changes smoothly with direction, so it settles at once.
    /// </summary>
    private void Compute(int index, double planeY)
    {
        var rules = _rules!;
        var at = Positions[index];
        var dy = planeY - at.Y;
        var radius = rules.SensorRadius;
        var samples = _samples.AsSpan(index * ReachSamples, ReachSamples);
        var clouds = _gas is { } gas && _galaxy is not null ? gas.At(at) : default;
        var widest = 0.0;
        for (var k = 0; k < ReachSamples; k++)
        {
            var flat = Math.Sqrt(Math.Max(radius * radius - dy * dy, 0));
            var d = flat;
            if (clouds.Length > 0 && flat > 0)
            {
                var unit = SensorOutline.Direction(k);
                for (var step = 0; step < 4; step++)
                {
                    var direction = dy == 0 ? unit : new Double3(unit.X * d, dy, unit.Z * d);
                    var reach = GasSensors.Reach(_galaxy!, clouds, rules, at, direction);
                    var next = Math.Sqrt(Math.Max(reach * reach - dy * dy, 0));
                    var change = Math.Abs(next - d);
                    d = next;
                    if (change < 0.01)
                        break;
                }
            }

            samples[k] = d;
            widest = Math.Max(widest, d);
        }

        _entryId[index] = Ids[index];
        _entryAt[index] = at;
        _entryPlane[index] = planeY;
        _entryState[index] = widest > 0 ? (byte)2 : (byte)1;
        _entryVersion[index] = ++_versions;
    }

    /// <summary>
    /// Fills <paramref name="reach"/> with how far sensor <paramref name="index"/> sees in each direction on the
    /// plane last given to <see cref="Refresh"/>, direction k being angle k / n of a turn from +x towards +z. False
    /// when the plane misses the sensor or it has not been sampled.
    /// </summary>
    public bool SampleReach(int index, double planeY, Span<double> reach)
    {
        if (!Ring(index, out _, out _, out _, out var samples))
            return false;
        samples.CopyTo(reach);
        return true;
    }

    /// <summary>The sampled ring of a sensor: its centre on the plane, the plain sphere's radius there and the reach per direction.</summary>
    public bool Ring(int index, out double x, out double z, out double nominal, out ReadOnlySpan<double> samples)
    {
        x = z = nominal = 0;
        samples = default;
        if (index >= _entryState.Length || _entryState[index] != 2)
            return false;
        var dy = _entryAt[index].Y - _entryPlane[index];
        x = _entryAt[index].X;
        z = _entryAt[index].Z;
        nominal = Math.Sqrt(Math.Max(Radius * Radius - dy * dy, 0));
        samples = _samples.AsSpan(index * ReachSamples, ReachSamples);
        return true;
    }

    /// <summary>
    /// The merged outline of the shown sensors, each group's regions as one boundary. Built again only when the
    /// shown sensors, their groups or any shape changed since the last build; between those it is the cached one.
    /// </summary>
    public ReadOnlySpan<OutlineSegment> Outline()
    {
        if (_outline is null)
        {
            _outline = new OutlineSegment[MaxOutlineSegments];
            _builtActive = new int[MaxActive];
            _builtVersions = new uint[MaxActive];
            _builtGroup = new int[MaxActive];
            _ringX = new double[MaxActive];
            _ringZ = new double[MaxActive];
            _ringGroup = new int[MaxActive];
            _ringSamples = new double[MaxActive * ReachSamples];
        }

        var current = _builtCount == ActiveCount;
        for (var s = 0; s < ActiveCount && current; s++)
        {
            var i = _active[s];
            current = _builtActive[s] == i && _builtGroup[s] == _group[s] && _builtVersions[s] == (_entryState[i] == 2 ? _entryVersion[i] : 0);
        }

        if (!current)
        {
            var rings = 0;
            for (var s = 0; s < ActiveCount; s++)
            {
                var i = _active[s];
                _builtActive[s] = i;
                _builtGroup[s] = _group[s];
                _builtVersions[s] = _entryState[i] == 2 ? _entryVersion[i] : 0;
                if (!Ring(i, out var x, out var z, out _, out var samples))
                    continue;
                _ringX[rings] = x;
                _ringZ[rings] = z;
                _ringGroup[rings] = _group[s];
                samples.CopyTo(_ringSamples.AsSpan(rings * ReachSamples, ReachSamples));
                rings++;
            }

            _builtCount = ActiveCount;
            _outlineCount = SensorOutline.Build(_ringX, _ringZ, _ringSamples, _ringGroup, rings, _outline);
        }

        return _outline.AsSpan(0, _outlineCount);
    }

    /// <summary>
    /// True when a point on the plane lies inside the sampled region of any shown sensor (or, with
    /// <paramref name="activeOnly"/> false, any sensor) other than <paramref name="except"/>, a sensor index or -1.
    /// </summary>
    public bool InsideShown(double x, double z, int except = -1, bool activeOnly = true)
    {
        var shown = activeOnly ? ActiveCount : Count;
        for (var s = 0; s < shown; s++)
        {
            var i = activeOnly ? _active[s] : s;
            if (i != except && Ring(i, out var cx, out var cz, out _, out var samples) && SensorOutline.Inside(cx, cz, samples, x, z, 0))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when a point lies inside the plain sphere's circle of a sensor with a lower index than
    /// <paramref name="index"/>, so a tint of range lost to gas, which is drawn where no sensor sees, is drawn once
    /// by the lowest sensor reaching a point and not again by each.
    /// </summary>
    public bool InsideEarlierDisc(double x, double z, int index, bool activeOnly = true)
    {
        var shown = activeOnly ? ActiveCount : Count;
        for (var s = 0; s < shown; s++)
        {
            var i = activeOnly ? _active[s] : s;
            if (i >= index || !Ring(i, out var cx, out var cz, out var nominal, out _))
                continue;
            if ((x - cx) * (x - cx) + (z - cz) * (z - cz) <= nominal * nominal)
                return true;
        }

        return false;
    }
}
