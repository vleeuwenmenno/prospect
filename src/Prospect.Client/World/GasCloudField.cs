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
    /// Writes the known clouds of the sectors at most <paramref name="radius"/> sectors from
    /// <paramref name="center"/> on each axis, nearest first, and returns how many. Called on a worker, one call at
    /// a time, and must take no memory once warm.
    /// </summary>
    int Collect(Galaxy galaxy, SectorCoord center, int radius, Span<PlacedGasCloud> into);
}

/// <summary>Every cloud the seed generates, worked out by the shared generation code; no asteroid is generated.</summary>
internal sealed class GeneratedGasClouds : IGasCloudSource
{
    private readonly SectorContent _scratch = new();

    public int Collect(Galaxy galaxy, SectorCoord center, int radius, Span<PlacedGasCloud> into) =>
        GasCloudQuery.Collect(galaxy, center, radius, _scratch, into);
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
    private PlacedGasCloud[] _front = new PlacedGasCloud[Capacity];
    private PlacedGasCloud[] _back = new PlacedGasCloud[Capacity];
    private int _frontCount;
    private int _backCount;
    private Galaxy? _galaxy;
    private SectorCoord _center;
    private Galaxy? _jobGalaxy;
    private SectorCoord _jobCenter;
    private volatile int _state;

    /// <summary>
    /// A field for clouds seen up to <paramref name="viewRangeSectors"/> sector edges away. The list is made two
    /// sectors wider than that: one for the camera's and the cloud's places within their sectors, one for the list
    /// being a sector behind while its successor is worked out.
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
    public ReadOnlySpan<PlacedGasCloud> Clouds(Galaxy galaxy) =>
        ReferenceEquals(galaxy, _galaxy) ? _front.AsSpan(0, _frontCount) : default;

    /// <summary>Called once a frame: takes over a finished list and asks for a new one when the camera changed sector.</summary>
    public void Update(Galaxy galaxy, SectorCoord center)
    {
        if (_state == Finished)
        {
            (_front, _back) = (_back, _front);
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
        _state = Finished;
    }
}
