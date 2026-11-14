using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The gas clouds the player has sampled, worked out from <see cref="ModuleKnowledgeView.Records"/>: one entry per
/// cloud (by its anchor sector), newest first, with the mean of the regions sampled so far. The player's knowledge
/// only: a cloud nobody scanned is not here, and an entry says how many of the cloud's eight regions were sampled and
/// how long ago, never more. Rebuilt only when the records changed; every array is made once, so rebuilding and
/// reading take no memory (the one dictionary grows only when a cloud is learned for the first time).
/// </summary>
internal sealed class GasLedger
{
    /// <summary>The most clouds listed; the oldest are left out past it.</summary>
    public const int MaxClouds = 256;

    private readonly SectorCoord[] _anchor = new SectorCoord[MaxClouds];
    private readonly long[] _newest = new long[MaxClouds];
    private readonly ModuleQuality[] _best = new ModuleQuality[MaxClouds];
    private readonly byte[] _mask = new byte[MaxClouds];
    private readonly float[] _shares = new float[MaxClouds * GasComposition.Capacity];
    private readonly float[] _purity = new float[MaxClouds];
    private readonly float[] _density = new float[MaxClouds];
    private readonly GasCloudKind[] _kind = new GasCloudKind[MaxClouds];
    private readonly int[] _order = new int[MaxClouds];
    // The kind of each cloud looked up so far, in fixed arrays: a Dictionary would grow, and so allocate, in the frame
    // that first learns of a cloud. A lookup beyond the capacity is worked out again each time instead of kept.
    private readonly SectorCoord[] _knownAnchor = new SectorCoord[MaxClouds];
    private readonly GasCloudKind[] _knownKind = new GasCloudKind[MaxClouds];
    private int _knownCount;
    private ulong _signature = ulong.MaxValue;
    private Galaxy? _galaxy;
    private int _count;

    /// <summary>How many clouds are known.</summary>
    public int Count => _count;

    /// <summary>How many times the list was rebuilt, for tests.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Rebuilds the list when the retained records, or the galaxy, are not the ones it was built from.</summary>
    public void Update(ModuleKnowledgeView view, Galaxy? galaxy)
    {
        var signature = 1469598103934665603UL;
        for (var i = 0; i < view.Count; i++)
        {
            ref var record = ref view.Records[i];
            if (record.Family != ModuleFamily.GasScanner)
                continue;
            signature = (signature ^ (ulong)record.RecordedTick) * 1099511628211UL;
            signature = (signature ^ (uint)record.Sector.X ^ ((ulong)(uint)record.Sector.Y << 20) ^ ((ulong)(uint)record.Sector.Z << 40) ^ (ulong)record.Index) * 1099511628211UL;
        }

        if (signature == _signature && ReferenceEquals(galaxy, _galaxy))
            return;
        _signature = signature;
        if (!ReferenceEquals(galaxy, _galaxy))
            _knownCount = 0;
        _galaxy = galaxy;
        Rebuild(view);
    }

    private void Rebuild(ModuleKnowledgeView view)
    {
        Rebuilds++;
        _count = 0;
        Span<int> regions = stackalloc int[MaxClouds];
        for (var i = 0; i < view.Count; i++)
        {
            ref var record = ref view.Records[i];
            if (record.Family != ModuleFamily.GasScanner)
                continue;
            var at = -1;
            for (var c = 0; c < _count && at < 0; c++)
                if (_anchor[c] == record.Sector)
                    at = c;
            if (at < 0)
            {
                if (_count == MaxClouds)
                    continue;
                at = _count++;
                _anchor[at] = record.Sector;
                _newest[at] = 0;
                _best[at] = ModuleQuality.Common;
                _mask[at] = 0;
                regions[at] = 0;
                _purity[at] = 0;
                _density[at] = 0;
                _shares.AsSpan(at * GasComposition.Capacity, GasComposition.Capacity).Clear();
            }

            var bit = (byte)(1 << (record.Index & 7));
            // One record per region, so a region is never counted twice: Find keys on the region.
            if ((_mask[at] & bit) != 0)
                continue;
            _mask[at] |= bit;
            regions[at]++;
            _newest[at] = Math.Max(_newest[at], record.RecordedTick);
            if (record.Quality > _best[at])
                _best[at] = record.Quality;
            _purity[at] += record.Gas.Purity;
            _density[at] += record.Gas.Density;
            for (var g = 0; g < GasComposition.Capacity; g++)
                _shares[at * GasComposition.Capacity + g] += record.Gas.Shares[g];
        }

        for (var c = 0; c < _count; c++)
        {
            var n = Math.Max(regions[c], 1);
            _purity[c] /= n;
            _density[c] /= n;
            for (var g = 0; g < GasComposition.Capacity; g++)
                _shares[c * GasComposition.Capacity + g] /= n;
            _kind[c] = KindOf(_anchor[c]);
            _order[c] = c;
        }

        // Newest first; insertion sort, the list is short and mostly in order already.
        for (var i = 1; i < _count; i++)
        {
            var item = _order[i];
            var j = i - 1;
            while (j >= 0 && _newest[_order[j]] < _newest[item])
            {
                _order[j + 1] = _order[j];
                j--;
            }

            _order[j + 1] = item;
        }
    }

    private GasCloudKind KindOf(SectorCoord anchor)
    {
        for (var i = 0; i < _knownCount; i++)
            if (_knownAnchor[i] == anchor)
                return _knownKind[i];
        var kind = GasCloudKind.Normal;
        if (_galaxy?.GasClouds is { } gas && gas.TryGetCloud(_galaxy, anchor, out var cloud))
            kind = cloud.Kind;
        if (_knownCount < MaxClouds)
        {
            _knownAnchor[_knownCount] = anchor;
            _knownKind[_knownCount++] = kind;
        }

        return kind;
    }

    /// <summary>The entry listed in a row, newest first.</summary>
    public int At(int row) => _order[row];

    /// <summary>The entry for the cloud anchored at <paramref name="anchor"/>, or -1.</summary>
    public int Find(SectorCoord anchor)
    {
        for (var c = 0; c < _count; c++)
            if (_anchor[c] == anchor)
                return c;
        return -1;
    }

    public SectorCoord Anchor(int entry) => _anchor[entry];

    /// <summary>The tick of the newest sample.</summary>
    public long Newest(int entry) => _newest[entry];

    /// <summary>The best scanner quality that sampled the cloud.</summary>
    public ModuleQuality Best(int entry) => _best[entry];

    /// <summary>How many of the cloud's eight regions were sampled.</summary>
    public int Regions(int entry) => System.Numerics.BitOperations.PopCount(_mask[entry]);

    public GasCloudKind Kind(int entry) => _kind[entry];

    /// <summary>The mean purity and density over the sampled regions.</summary>
    public float Purity(int entry) => _purity[entry];

    public float Density(int entry) => _density[entry];

    /// <summary>The mean share of gas <paramref name="gas"/> over the sampled regions.</summary>
    public float Share(int entry, int gas) => _shares[entry * GasComposition.Capacity + gas];

    /// <summary>
    /// The part of the mixture the scanner could not resolve: what is left when the resolved shares are added up. Never
    /// spread over the named gases, which would claim a precision the scanner did not have.
    /// </summary>
    public float Unresolved(int entry)
    {
        var sum = 0f;
        for (var g = 0; g < GasComposition.Capacity; g++)
            sum += Share(entry, g);
        return Math.Clamp(1f - sum, 0f, 1f);
    }
}

/// <summary>How the interface says how long ago something was observed.</summary>
internal static class ModuleAge
{
    /// <summary>Seconds between a record's tick and the server's now.</summary>
    public static float Seconds(long recordedTick, long serverTick) => Math.Max(0, serverTick - recordedTick) / (float)TickLoop.SimulationHz;

    /// <summary>The unit an age reads in: seconds below a minute and a half, minutes below ninety, else hours.</summary>
    public static string Unit(float seconds) => seconds < 90f ? "S" : seconds < 5400f ? "MIN" : "H";

    /// <summary>The age in that unit.</summary>
    public static float Value(float seconds) => seconds < 90f ? seconds : seconds < 5400f ? seconds / 60f : seconds / 3600f;
}

/// <summary>The locator overlay's strengths over time. Pure arithmetic, so the tests can drive it.</summary>
internal static class LocatorField
{
    /// <summary>
    /// How strong a marker is. A marker from the pulse just fired (<paramref name="fresh"/>) shows at full strength
    /// for the vision time, then fades; in the command view it fades down to the retained strength and stays, in the
    /// flight view it fades out. An older marker shows only in the command view, at the retained strength.
    /// </summary>
    public static float Alpha(bool fresh, bool command, float pulseAge, ModuleLook.LocatorLook look)
    {
        var retained = command ? look.RetainedAlpha : 0f;
        if (!fresh || pulseAge >= look.VisionSeconds + look.FadeSeconds)
            return retained;
        if (pulseAge <= look.VisionSeconds)
            return look.VisionAlpha;
        var t = look.FadeSeconds <= 0f ? 1f : (pulseAge - look.VisionSeconds) / look.FadeSeconds;
        return look.VisionAlpha + (retained - look.VisionAlpha) * Math.Clamp(t, 0f, 1f);
    }

    /// <summary>
    /// Whether the pulse's first wave has reached a marker: it travels from the pulse's origin out to its range in
    /// <see cref="ModuleLook.LocatorLook.WaveSeconds"/>, so the nearest asteroids appear first. Returns the reveal
    /// strength, 0 to 1, over a short ramp behind the wave front.
    /// </summary>
    public static float Reveal(double distance, float range, float pulseAge, ModuleLook.LocatorLook look)
    {
        if (look.WaveSeconds <= 0f || range <= 0f)
            return 1f;
        var front = pulseAge / look.WaveSeconds * range;
        var ramp = Math.Max(range * 0.08, 1);
        return (float)Math.Clamp((front - distance) / ramp, 0.0, 1.0);
    }
}

/// <summary>
/// Which gas cloud the gas scanner would sample, and how far the ship is from it. Worked out again only when the
/// ship has moved, or a little time has passed: the question asks every cloud near the ship, which a frame should not.
/// </summary>
internal sealed class GasTargeting
{
    private const double MoveMeters = 25;
    private const float RefreshSeconds = 0.5f;
    private readonly GasCloudPlacement[] _scratch = new GasCloudPlacement[16];
    private Double3 _at;
    private double _range = -1;
    private float _time = float.MinValue;
    private Galaxy? _galaxy;

    /// <summary>The nearest cloud within reach, if any.</summary>
    public bool Has { get; private set; }

    /// <summary>The cloud's anchor sector, the name a gas scan request gives.</summary>
    public SectorCoord Anchor { get; private set; }

    /// <summary>The distance from the ship to the point the scan would sample, in metres.</summary>
    public double Distance { get; private set; }

    /// <summary>The kind of the cloud found.</summary>
    public GasCloudKind Kind { get; private set; }

    /// <summary>How many times the question was asked, for tests.</summary>
    public int Queries { get; private set; }

    /// <summary>Forgets the answer, for a new game or hull.</summary>
    public void Reset()
    {
        _time = float.MinValue;
        Has = false;
    }

    /// <summary>Looks again if the ship moved, the range changed or enough time passed.</summary>
    public void Update(Galaxy? galaxy, Double3 position, double range, float time)
    {
        if (galaxy is null || !(range > 0) || galaxy.GasClouds is not { } gas)
        {
            Has = false;
            _time = float.MinValue;
            return;
        }

        var moved = (position - _at).Length;
        if (ReferenceEquals(galaxy, _galaxy) && range == _range && moved < MoveMeters && time - _time < RefreshSeconds && time >= _time)
            return;
        _galaxy = galaxy;
        _range = range;
        _at = position;
        _time = time;
        Queries++;
        var found = gas.Intersecting(galaxy, position, range, _scratch);
        Has = false;
        Distance = double.MaxValue;
        for (var i = 0; i < found; i++)
        {
            var point = GasCloudQuery.SamplePoint(galaxy, _scratch[i], position);
            var distance = (point - position).Length;
            if (distance >= Distance)
                continue;
            Distance = distance;
            Anchor = _scratch[i].Anchor;
            Kind = _scratch[i].Cloud.Kind;
            Has = true;
        }
    }

    /// <summary>True when the sample point is within the module's reach: what the server checks on every tick.</summary>
    public bool InRange(double range) => Has && Distance <= range;
}
