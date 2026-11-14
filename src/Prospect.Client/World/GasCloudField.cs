using Prospect.Engine.Jobs;
using Prospect.Game.World;
using IJob = Brutal.Concurrency.Jobs.IJob;

namespace Prospect.Client.World;

/// <summary>
/// Which gas clouds the viewer may know of around a sector. Today that is every cloud the seed generates
/// (<see cref="GeneratedGasClouds"/>); a source that knows only charted sectors takes its place without the
/// renderer or the levels of detail changing.
/// </summary>
internal interface IGasCloudSource
{
    /// <summary>
    /// Writes the known clouds anchored in the sectors at most <paramref name="radius"/> sectors from
    /// <paramref name="center"/> on each axis, nearest first, and returns how many. A cloud is named by its anchor
    /// whatever its size, so one that lies across sector walls is listed once. Called on a worker, one call at
    /// a time, and must take no memory once warm.
    /// </summary>
    int Collect(Galaxy galaxy, SectorCoord center, int radius, Span<GasCloudPlacement> into);
}

/// <summary>
/// Every cloud the seed generates, as the generation module names them, by the sector that anchors each; no
/// asteroid is generated.
/// </summary>
internal sealed class GeneratedGasClouds : IGasCloudSource
{
    public int Collect(Galaxy galaxy, SectorCoord center, int radius, Span<GasCloudPlacement> into) =>
        GasCloudQuery.Collect(galaxy, center, radius, into);
}

/// <summary>
/// The clouds around the camera's sector, kept current by one worker job at a time. The frame reads the list it
/// last took over and never waits: until a new list is ready the old one stays, and it reaches far enough past the
/// view range that a cloud entering the range is already in it.
/// </summary>
internal sealed class GasCloudField : IJob
{
    /// <summary>The most clouds kept; the nearest sectors' are kept first.</summary>
    public const int Capacity = 512;

    private const int Idle = 0;
    private const int Working = 1;
    private const int Finished = 2;

    private readonly JobSystem _jobs;
    private readonly IGasCloudSource _source;
    private readonly int _radius;

    // The frame owns the front list always, and the back list except while a job is working on it.
    private GasCloudPlacement[] _front = new GasCloudPlacement[Capacity];
    private GasCloudPlacement[] _back = new GasCloudPlacement[Capacity];
    private int[] _frontPartners = new int[Capacity];
    private int[] _backPartners = new int[Capacity];
    private int _frontCount;
    private int _backCount;
    private Galaxy? _galaxy;
    private SectorCoord _center;
    private Galaxy? _jobGalaxy;
    private SectorCoord _jobCenter;
    private volatile int _state;

    /// <summary>
    /// A field for clouds seen up to <paramref name="viewRangeSectors"/> sector edges away, measured to a cloud's
    /// centre. The list is made two sectors wider than that: one for the camera's and the centre's places within
    /// their sectors (a centre lies in its anchor), one for the list being a sector behind while its successor is
    /// worked out.
    /// </summary>
    public GasCloudField(JobSystem jobs, IGasCloudSource source, float viewRangeSectors)
    {
        _jobs = jobs;
        _source = source;
        _radius = (int)MathF.Ceiling(viewRangeSectors) + 2;
    }

    /// <summary>How many sectors from the camera's own, on each axis, the list is made from.</summary>
    public int Radius => _radius;

    /// <summary>The clouds known around the camera, nearest sectors first; empty until the first list is ready.</summary>
    public ReadOnlySpan<GasCloudPlacement> Clouds(Galaxy galaxy) =>
        ReferenceEquals(galaxy, _galaxy) ? _front.AsSpan(0, _frontCount) : default;

    /// <summary>
    /// For each cloud of <see cref="Clouds"/>, the index of the cloud it is drawn together with, or -1. Only a cold
    /// and a normal cloud can share space; where their boxes overlap, the two are integrated jointly along each ray
    /// instead of laid one over the other. Each cloud has at most one partner: the first in the list's order.
    /// </summary>
    public ReadOnlySpan<int> Partners(Galaxy galaxy) =>
        ReferenceEquals(galaxy, _galaxy) ? _frontPartners.AsSpan(0, _frontCount) : default;

    /// <summary>Called once a frame: takes over a finished list and asks for a new one when the camera changed sector.</summary>
    public void Update(Galaxy galaxy, SectorCoord center)
    {
        if (_state == Finished)
        {
            (_front, _back) = (_back, _front);
            (_frontPartners, _backPartners) = (_backPartners, _frontPartners);
            _frontCount = _backCount;
            _galaxy = _jobGalaxy;
            _center = _jobCenter;
            _jobGalaxy = null;
            _state = Idle;
        }

        if (_state != Idle || (ReferenceEquals(galaxy, _galaxy) && center == _center))
            return;
        _jobGalaxy = galaxy;
        _jobCenter = center;
        _state = Working;
        _jobs.Schedule(this);
    }

    /// <summary>The worker's part: fills the back list, which the frame does not touch until this has finished.</summary>
    public void Run()
    {
        _backCount = _source.Collect(_jobGalaxy!, _jobCenter, _radius, _back);
        Pair(_jobGalaxy!, _back.AsSpan(0, _backCount), _backPartners);
        _state = Finished;
    }

    /// <summary>
    /// Pairs every cold cloud with the first normal cloud whose box overlaps its own, and the other way round; a cloud
    /// already paired is skipped. Generation keeps every other pair of kinds apart, so these are the only volumes
    /// that can share a point. Takes no memory.
    /// </summary>
    internal static void Pair(Galaxy galaxy, ReadOnlySpan<GasCloudPlacement> clouds, Span<int> partners)
    {
        partners[..clouds.Length].Fill(-1);
        for (var i = 0; i < clouds.Length; i++)
        {
            if (partners[i] >= 0 || clouds[i].Cloud.Kind == GasCloudKind.Lightning) continue;
            var center = GasCloudQuery.CenterOf(galaxy, clouds[i]);
            for (var j = i + 1; j < clouds.Length; j++)
            {
                if (partners[j] >= 0 || !GasCloudModule.MayOverlap(clouds[i].Cloud.Kind, clouds[j].Cloud.Kind)) continue;
                var apart = GasCloudQuery.CenterOf(galaxy, clouds[j]) - center;
                var reach = clouds[i].Cloud.HalfSize + clouds[j].Cloud.HalfSize;
                if (Math.Abs(apart.X) >= reach.X || Math.Abs(apart.Y) >= reach.Y || Math.Abs(apart.Z) >= reach.Z) continue;
                partners[i] = j;
                partners[j] = i;
                break;
            }
        }
    }
}
