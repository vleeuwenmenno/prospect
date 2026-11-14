using System.Numerics;
using Prospect.Game.World;
using IJob = Brutal.Concurrency.Jobs.IJob;

namespace Prospect.Client.Screens;

/// <summary>What a map feature is: a gas cloud's envelope or an asteroid field's ribbon.</summary>
internal enum MapFeatureKind : byte
{
    Cloud,
    Field,
}

/// <summary>
/// One feature's outlines in a <see cref="MapFeatureCache"/> slot: the whole shape (a field's support, a cloud's
/// fringe) and the denser part inside it (a field's core, a cloud's body). Both are closed polygons.
/// </summary>
internal struct MapFeature
{
    public MapFeatureKind Kind;
    public int OuterCount;
    public int InnerCount;
}

/// <summary>
/// The outlines of what a charted sector shows of the natural world, worked out on a worker and kept for the frame
/// to draw: every asteroid field and gas cloud whose volume reaches the sector, at the sector's layer. A chart is of
/// immutable geography, so an entry never goes stale; it is dropped only when its slot is needed for another sector
/// or the galaxy changes. The frame asks for a sector and draws it once it is ready, and never waits; one worker job
/// runs at a time, and nothing here takes memory once the cache is made.
/// </summary>
/// <remarks>
/// The frame owns a slot while it is Ready or Empty; the worker owns it, and only it, while it is Working. Vertices
/// are metres in x and z relative to the centre of the slot's sector.
/// </remarks>
internal sealed class MapFeatureCache : IJob
{
    /// <summary>How many sectors are kept.</summary>
    public const int Slots = 64;

    /// <summary>The most features a sector keeps; a sector over it loses its last fields, never a cloud.</summary>
    public const int MaxFeatures = 48;

    private const int Stride = 2 * MapFootprint.MaxVertices;
    private const int Probes = 4;
    private const int Empty = 0;
    private const int Working = 1;
    private const int Ready = 2;

    private readonly Action<IJob> _schedule;
    private readonly float _coreShare;
    private readonly int[] _state = new int[Slots];
    private readonly SectorCoord[] _sectors = new SectorCoord[Slots];
    private readonly int[] _counts = new int[Slots];
    private readonly MapFeature[] _features = new MapFeature[Slots * MaxFeatures];
    private readonly Vector2[] _vertices = new Vector2[Slots * MaxFeatures * Stride];

    // The worker's own scratch, so a job takes no memory.
    private readonly ChartField[] _fieldBuffer = new ChartField[AsteroidFieldsModule.MaxFieldsInReach];
    private readonly GasCloudPlacement[] _cloudBuffer = new GasCloudPlacement[GasCloudModule.MaxPerSector];

    private Galaxy? _galaxy;
    private Galaxy? _jobGalaxy;
    private SectorCoord _jobSector;
    private int _jobSlot;
    private volatile bool _done;
    private bool _running;
    private int _evict;

    /// <summary>
    /// A cache whose jobs are started by <paramref name="schedule"/> (the job system's, in the game; a direct call in
    /// a test). <paramref name="coreShare"/> is how much of a field's width its core outline keeps.
    /// </summary>
    public MapFeatureCache(Action<IJob> schedule, float coreShare)
    {
        _schedule = schedule;
        _coreShare = coreShare;
    }

    /// <summary>True while a worker job has a sector in hand.</summary>
    public bool Busy => _running;

    /// <summary>
    /// Once a frame, before anything is asked: takes over a finished job and drops every entry when the galaxy is a
    /// different one.
    /// </summary>
    public void Update(Galaxy galaxy)
    {
        if (!ReferenceEquals(galaxy, _galaxy))
        {
            _galaxy = galaxy;
            for (var i = 0; i < Slots; i++)
                _state[i] = _state[i] == Working ? Working : Empty;
        }

        if (!_running || !_done)
            return;
        _running = false;
        _done = false;
        // A job for a galaxy that has been replaced since is of no use.
        _state[_jobSlot] = ReferenceEquals(_jobGalaxy, _galaxy) ? Ready : Empty;
        _jobGalaxy = null;
    }

    /// <summary>
    /// Whether the sector's outlines are ready; asks the worker for them when they are not and it has no other job.
    /// The caller has checked that the player holds a chart of the sector.
    /// </summary>
    public bool TryGet(Galaxy galaxy, SectorCoord sector, out int slot)
    {
        var home = HomeOf(sector);
        slot = home;
        if (!ReferenceEquals(_galaxy, galaxy))
            return false;

        // A sector may sit in any of a few slots from its home on, so two charted sectors that hash alike do not
        // push each other out every frame.
        var free = -1;
        for (var probe = 0; probe < Probes; probe++)
        {
            var at = (home + probe) % Slots;
            if (_state[at] != Empty && _sectors[at] == sector)
            {
                slot = at;
                return _state[at] == Ready;
            }

            if (free < 0 && _state[at] == Empty)
                free = at;
        }

        if (_running)
            return false;
        if (free < 0)
        {
            // All taken: the oldest of the probe run makes room. A slot being worked on is never taken.
            for (var probe = 0; probe < Probes && free < 0; probe++)
            {
                var at = (home + (_evict + probe) % Probes) % Slots;
                if (_state[at] == Ready)
                    free = at;
            }

            _evict++;
            if (free < 0)
                return false;
        }

        slot = free;
        _state[free] = Working;
        _sectors[free] = sector;
        _jobSlot = free;
        _jobSector = sector;
        _jobGalaxy = galaxy;
        _done = false;
        _running = true;
        _schedule(this);
        return false;
    }

    /// <summary>The features of a ready slot.</summary>
    public ReadOnlySpan<MapFeature> Features(int slot) => _features.AsSpan(slot * MaxFeatures, _counts[slot]);

    /// <summary>The whole outline of feature <paramref name="index"/> of a ready slot.</summary>
    public ReadOnlySpan<Vector2> Outer(int slot, int index) =>
        _vertices.AsSpan((slot * MaxFeatures + index) * Stride, _features[slot * MaxFeatures + index].OuterCount);

    /// <summary>The denser inner outline of feature <paramref name="index"/> of a ready slot.</summary>
    public ReadOnlySpan<Vector2> Inner(int slot, int index) =>
        _vertices.AsSpan((slot * MaxFeatures + index) * Stride + MapFootprint.MaxVertices, _features[slot * MaxFeatures + index].InnerCount);

    /// <summary>The worker's part: fills the job's slot, which the frame does not touch until this has finished.</summary>
    public void Run()
    {
        Build(_jobGalaxy!, _jobSector, _jobSlot);
        _done = true;
    }

    /// <summary>Works a sector's outlines out into a slot. Public to tests through <see cref="BuildNow"/>.</summary>
    private void Build(Galaxy galaxy, SectorCoord sector, int slot)
    {
        var count = 0;
        var features = _features.AsSpan(slot * MaxFeatures, MaxFeatures);
        var origin = galaxy.CenterOf(sector);
        var half = galaxy.SectorSize * 0.5;

        if (galaxy.GasClouds is { } gas)
        {
            var found = gas.Intersecting(galaxy, sector, _cloudBuffer);
            var fill = gas.Shape.X;
            var lumpiness = gas.Shape.Y;
            for (var i = 0; i < found && count < MaxFeatures; i++)
            {
                ref readonly var placement = ref _cloudBuffer[i];
                var anchor = galaxy.CenterOf(placement.Anchor);
                var vertices = _vertices.AsSpan((slot * MaxFeatures + count) * Stride, Stride);
                var outer = MapFootprint.Cloud(placement.Cloud, anchor, MapFootprint.FringeShare(fill), lumpiness, origin.Y - half, origin.Y + half, origin, vertices[..MapFootprint.MaxVertices]);
                var inner = MapFootprint.Cloud(placement.Cloud, anchor, fill, lumpiness, origin.Y - half, origin.Y + half, origin, vertices[MapFootprint.MaxVertices..]);
                if (outer == 0)
                    continue;
                features[count++] = new MapFeature { Kind = MapFeatureKind.Cloud, OuterCount = outer, InnerCount = inner };
            }
        }

        if (galaxy.AsteroidFields is { } asteroidFields)
        {
            var found = asteroidFields.ChartFields(galaxy, sector, _fieldBuffer);
            for (var i = 0; i < found && count < MaxFeatures; i++)
            {
                var vertices = _vertices.AsSpan((slot * MaxFeatures + count) * Stride, Stride);
                var outer = MapFootprint.Field(_fieldBuffer[i], 1f, origin, vertices[..MapFootprint.MaxVertices]);
                var inner = MapFootprint.Field(_fieldBuffer[i], _coreShare, origin, vertices[MapFootprint.MaxVertices..]);
                features[count++] = new MapFeature { Kind = MapFeatureKind.Field, OuterCount = outer, InnerCount = inner };
            }
        }

        _counts[slot] = count;
    }

    /// <summary>Works a sector out on the calling thread and has it ready; for tests, which need no worker.</summary>
    public int BuildNow(Galaxy galaxy, SectorCoord sector)
    {
        Update(galaxy);
        var slot = HomeOf(sector);
        _state[slot] = Ready;
        _sectors[slot] = sector;
        Build(galaxy, sector, slot);
        return slot;
    }

    private static int HomeOf(SectorCoord sector) =>
        (int)((uint)(sector.X * 73856093 ^ sector.Y * 19349663 ^ sector.Z * 83492791) % Slots);
}
