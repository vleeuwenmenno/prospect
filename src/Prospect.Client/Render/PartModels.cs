using System.Numerics;
using Brutal.Concurrency.Jobs;
using Prospect.Engine.Jobs;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// Models of single thrust parts and tanks for what no design mesh holds: the editor's draft and its placement ghost, and far
/// hulls that have lost blocks and are drawn block by block. Each model is a triangle list (no indices) centred on its
/// box, keyed by part type, half extents, facing and cell size, built on a worker by <see cref="PropulsionModels"/>
/// and drawn through <see cref="SceneRenderer.AddTriangles"/>. A fixed table: looking a model up takes no memory, and a
/// model not built yet is asked for and drawn as its plain block until it is there, a frame or two later.
/// </summary>
internal sealed class PartModels(JobSystem jobs, PropulsionLook look)
{
    /// <summary>Models kept at once; the one used longest ago makes room.</summary>
    public const int Slots = 96;

    /// <summary>Models one worker run builds.</summary>
    private const int Batch = 16;

    private struct Entry
    {
        public BlockType? Type;
        public Vector3 Half;
        public BlockFacing Facing;
        public float Cell;
        public MeshVertex[]? Vertices;
        public bool Queued;
        public long Used;
    }

    private struct TurretEntry
    {
        public TurretKind Kind;
        public float Small;
        public MeshVertex[]? Yoke;
        public MeshVertex[]? Head;
        public MeshVertex[]? Light;
        public bool Queued;
        public long Used;
    }

    private readonly ContainerBuild _containers = new(look);
    private JobHandle _containerHandle;
    private bool _containersStarted, _containersReady;
    public bool TryGetContainer(int size, out ReadOnlySpan<MeshVertex> vertices) =>
        TryGetContainer(size, -1, out vertices);

    public bool TryGetContainer(int size, int resource, out ReadOnlySpan<MeshVertex> vertices)
    {
        vertices = default;
        if ((uint)size >= 8 || !_containersReady) return false;
        var slot = (uint)resource < (uint)(_containers.ResourceSlots - 1) ? resource + 1 : 0;
        vertices = _containers.Results[size * _containers.ResourceSlots + slot];
        return true;
    }

    /// <summary>
    /// The mag-lock jaws of a container of <paramref name="size"/> held in a rack whose back (the face opposite its
    /// opening) is <paramref name="back"/>, and their lights, drawn apart in the colour of the container's stage;
    /// false while the worker has not finished. Allocates nothing.
    /// </summary>
    public bool TryGetClamp(int size, BlockFacing back, out ReadOnlySpan<MeshVertex> vertices, out ReadOnlySpan<MeshVertex> lights)
    {
        vertices = lights = default;
        if ((uint)size >= 8 || (uint)back >= 6 || !_containersReady) return false;
        vertices = _containers.Clamps[size * 6 + (int)back];
        lights = _containers.ClampLights[size * 6 + (int)back];
        return vertices.Length > 0;
    }

    private readonly OreBuild _ore = new();
    private JobHandle _oreHandle;
    private bool _oreStarted, _oreReady;

    /// <summary>
    /// The lump of an ore chunk, variant <paramref name="variant"/> (any number; it wraps) with the crystals of grade band
    /// <paramref name="band"/> (<see cref="OreChunkModelLook.Band"/>), as a triangle list; false while the worker has not
    /// finished. Allocates nothing.
    /// </summary>
    public bool TryGetOreChunk(int variant, int band, out ReadOnlySpan<MeshVertex> vertices)
    {
        vertices = default;
        if (!_oreReady) return false;
        var look = _ore.Look;
        vertices = _ore.Results[(int)((uint)variant % look.Variants) * _ore.Bands + Math.Clamp(band, 0, _ore.Bands - 1)];
        return true;
    }

    private readonly GlowBuild _glow = new(look);
    private JobHandle _glowHandle;
    private bool _glowStarted, _glowReady;

    /// <summary>
    /// The light of a bell's throat under thrust, for the look at <paramref name="slot"/> of
    /// <see cref="PropulsionLook.GlowModels"/> (<see cref="ThrustNozzle.Glow"/>): one model a look, for a nozzle of exit
    /// radius 1 and length 1 (<see cref="PropulsionModels.BuildEngineGlow"/>). False while the worker has not
    /// finished. Allocates nothing.
    /// </summary>
    public bool TryGetNozzleGlow(int slot, out ReadOnlySpan<MeshVertex> vertices)
    {
        vertices = default;
        if (!_glowReady || (uint)slot >= (uint)_glow.Results.Length)
            return false;
        vertices = _glow.Results[slot];
        return vertices.Length > 0;
    }

    private sealed class GlowBuild(PropulsionLook look) : IJob
    {
        public readonly MeshVertex[][] Results = new MeshVertex[look.GlowModels.Length][];

        public void Run()
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            for (var i = 0; i < Results.Length; i++)
            {
                vertices.Clear();
                indices.Clear();
                PropulsionModels.BuildEngineGlow(look.GlowModels[i], look.GlowModels[i].Bell!, vertices, indices);
                Results[i] = Soup(vertices, indices);
            }
        }
    }

    private readonly DroneBuild _drone = new(look);
    private JobHandle _droneHandle;
    private bool _droneStarted, _droneReady;

    /// <summary>
    /// Says which design the work drone's lit parts are built for, so the worker builds them when a session starts and
    /// not when the first drone comes into view. One design at a time, as a session has one. Allocates nothing.
    /// </summary>
    public void PrepareDrone(HullDesign design)
    {
        // The worker reads the design it was started with: another one waits until that run is over.
        if (look.WorkDrone is null || ReferenceEquals(_drone.Design, design) || (_droneStarted && !_droneReady))
            return;
        _drone.Design = design;
        _droneStarted = _droneReady = false;
    }

    /// <summary>
    /// The lit parts of a work drone of <paramref name="design"/>, in the hull's frame: the tractor array's segments
    /// and the pods' strips (<see cref="PropulsionModels.BuildWorkDroneGlow"/>). False while the worker has not
    /// finished, and without a work drone look. Allocates nothing.
    /// </summary>
    public bool TryGetDroneGlow(HullDesign design, out ReadOnlySpan<MeshVertex> array, out ReadOnlySpan<MeshVertex> strips)
    {
        array = default;
        strips = default;
        PrepareDrone(design);
        if (!_droneReady || !ReferenceEquals(_drone.Design, design))
            return false;
        array = _drone.Array;
        strips = _drone.Strips;
        return true;
    }

    private sealed class DroneBuild(PropulsionLook look) : IJob
    {
        public HullDesign? Design;
        public MeshVertex[] Array = [], Strips = [];

        public void Run()
        {
            var array = new List<MeshVertex>();
            var arrayIndices = new List<uint>();
            var strips = new List<MeshVertex>();
            var stripIndices = new List<uint>();
            PropulsionModels.BuildWorkDroneGlow(Design!, look.WorkDrone!, array, arrayIndices, strips, stripIndices);
            Array = Soup(array, arrayIndices);
            Strips = Soup(strips, stripIndices);
        }
    }

    private sealed class OreBuild : IJob
    {
        public readonly OreChunkModelLook Look = OreChunkModelLook.Current;
        public readonly int Bands;
        public readonly MeshVertex[][] Results;

        public OreBuild()
        {
            Bands = Look.CrystalCounts.Length;
            Results = new MeshVertex[Look.Variants * Bands][];
        }

        public void Run()
        {
            for (var v = 0; v < Look.Variants; v++)
                for (var b = 0; b < Bands; b++)
                    Results[v * Bands + b] = OreChunkModels.Build(Look, v, Look.CrystalCounts[b]);
        }
    }

    private sealed class ContainerBuild(PropulsionLook look) : IJob
    {
        public readonly MeshVertex[][] Clamps = new MeshVertex[8 * 6][];
        public readonly MeshVertex[][] ClampLights = new MeshVertex[8 * 6][];
        public readonly int ResourceSlots = 1 + (look.Rack?.Galaxy.Length ?? 0);
        public readonly MeshVertex[][] Results = new MeshVertex[8 * (1 + (look.Rack?.Galaxy.Length ?? 0))][];
        public void Run()
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            var lightVertices = new List<MeshVertex>();
            var lightIndices = new List<uint>();
            for (var i = 0; i < 8; i++)
            {
                var shape = look.Cargo.Sizes[i].Shape;
                var half = new Vector3(shape.X, shape.Y, shape.Z) * (look.Cargo.UnitSize * .5f);
                var box = new PartBox(Vector3.Zero, half, BlockFacing.Front, 1);
                for (var r = 0; r < ResourceSlots; r++)
                {
                    vertices.Clear(); indices.Clear();
                    if (look.Rack is { } rack)
                        PropulsionModels.ContainerModel(box, i, r - 1, rack, vertices, indices);
                    var soup = new MeshVertex[indices.Count];
                    for (var k = 0; k < soup.Length; k++) soup[k] = vertices[(int)indices[k]];
                    Results[i * ResourceSlots + r] = soup;
                }
                for (var face = 0; face < 6; face++)
                {
                    vertices.Clear(); indices.Clear(); lightVertices.Clear(); lightIndices.Clear();
                    if (look.Rack is { } clampLook)
                        PropulsionModels.ClampModel(shape, (BlockFacing)face, clampLook, look.Cargo, vertices, indices, lightVertices, lightIndices);
                    Clamps[i * 6 + face] = Soup(vertices, indices);
                    ClampLights[i * 6 + face] = Soup(lightVertices, lightIndices);
                }
            }
        }
    }

    private readonly Entry[] _entries = new Entry[Slots];
    private readonly TurretEntry[] _turrets = new TurretEntry[16];
    private readonly Build _build = new(look);

    /// <summary>Only scripted editor captures request sample containers.</summary>
    public void PreviewCargo() => _cargoPreview = true;
    private bool _cargoPreview;
    private JobHandle _handle;
    private bool _pending;
    private long _frame = 1;

    /// <summary>Models built so far, for tests.</summary>
    public int Built { get; private set; }

    /// <summary>Takes what the worker finished; call once a frame before any <see cref="TryGet"/>.</summary>
    public void Begin()
    {
        _frame++;
        if (!_containersStarted) { _containerHandle = jobs.Schedule(_containers, 2); _containersStarted = true; }
        if (!_containersReady && jobs.IsComplete(_containerHandle)) _containersReady = true;
        if (!_oreStarted) { _oreHandle = jobs.Schedule(_ore, 2); _oreStarted = true; }
        if (!_oreReady && jobs.IsComplete(_oreHandle)) _oreReady = true;
        if (!_glowStarted) { _glowHandle = jobs.Schedule(_glow, 2); _glowStarted = true; }
        if (!_glowReady && jobs.IsComplete(_glowHandle)) _glowReady = true;
        if (_drone.Design is not null && !_droneStarted) { _droneHandle = jobs.Schedule(_drone, 2); _droneStarted = true; }
        if (_droneStarted && !_droneReady && jobs.IsComplete(_droneHandle)) _droneReady = true;
        if (!_pending || !jobs.IsComplete(_handle))
            return;
        _pending = false;
        for (var i = 0; i < _build.Count; i++)
        {
            if (_build.Turrets[i] != TurretKind.None)
            {
                ref var tEntry = ref _turrets[_build.Slots[i]];
                tEntry.Queued = false;
                if (tEntry.Kind == _build.Turrets[i] && tEntry.Small == _build.Cells[i])
                {
                    tEntry.Yoke = _build.Results[i];
                    tEntry.Head = _build.HeadResults[i];
                    tEntry.Light = _build.LightResults[i];
                    Built++;
                }
                _build.Results[i] = null;
                _build.HeadResults[i] = null;
                _build.LightResults[i] = null;
                continue;
            }
            ref var entry = ref _entries[_build.Slots[i]];
            entry.Queued = false;
            // A slot handed to another part meanwhile keeps waiting for its own model.
            if (entry.Type == _build.Types[i] && entry.Half == _build.Halves[i] && entry.Facing == _build.Facings[i] && entry.Cell == _build.Cells[i])
            {
                entry.Vertices = _build.Results[i];
                Built++;
            }
            _build.Results[i] = null;
        }
        _build.Count = 0;
    }

    /// <summary>
    /// The model of a part of <paramref name="type"/> filling a box of <paramref name="half"/> extents and facing
    /// <paramref name="facing"/>, as a triangle list centred on the box; false, after asking for it, while it is not
    /// built, and for a type with no model. Allocates nothing.
    /// </summary>
    public bool TryGet(BlockType type, BlockShape shape, Vector3 half, BlockFacing facing, float cell, out ReadOnlySpan<MeshVertex> vertices)
    {
        vertices = default;
        if (!PropulsionModels.HasModel(type, shape, look))
            return false;
        var free = -1;
        var oldest = long.MaxValue;
        for (var i = 0; i < Slots; i++)
        {
            ref var entry = ref _entries[i];
            if (entry.Type == type && entry.Half == half && entry.Facing == facing && entry.Cell == cell)
            {
                entry.Used = _frame;
                if (entry.Vertices is null)
                    return false;
                vertices = entry.Vertices;
                return true;
            }
            // Room comes from an empty slot first, then the model used longest ago; never one in use this frame or
            // waiting for the worker.
            var age = entry.Type is null ? long.MinValue : entry.Used;
            if (!entry.Queued && entry.Used < _frame && age < oldest)
            {
                oldest = age;
                free = i;
            }
        }
        if (free < 0 || _pending || _build.Count == Batch)
            return false;
        _entries[free] = new Entry { Type = type, Half = half, Facing = facing, Cell = cell, Queued = true, Used = _frame };
        var at = _build.Count++;
        _build.Slots[at] = free;
        _build.Turrets[at] = TurretKind.None;
        _build.Types[at] = type;
        _build.Halves[at] = half;
        _build.Facings[at] = facing;
        _build.Cells[at] = cell;
        return false;
    }

    /// <summary>
    /// Returns the worker-built parts of a posed turret of <paramref name="kind"/> on a mount of half-width
    /// <paramref name="small"/>: its yoke, its head and the part of the head that is drawn as light (the tractor's
    /// emitter bars, the laser's lens). False, after asking for them, while they are not built, and for a kind the
    /// look has no turret for. Allocates nothing.
    /// </summary>
    public bool TryGetTurret(TurretKind kind, float small, out ReadOnlySpan<MeshVertex> yoke, out ReadOnlySpan<MeshVertex> head, out ReadOnlySpan<MeshVertex> light)
    {
        yoke = default;
        head = default;
        light = default;
        var has = kind switch
        {
            TurretKind.Tractor => look.Tractor is not null && look.Models.ContainsKey("tractorBeam"),
            TurretKind.Laser => look.Laser is not null,
            _ => false,
        };
        if (small <= 0 || !has)
            return false;
        var free = -1;
        var oldest = long.MaxValue;
        for (var i = 0; i < _turrets.Length; i++)
        {
            ref var entry = ref _turrets[i];
            if (entry.Kind == kind && entry.Small == small)
            {
                entry.Used = _frame;
                if (entry.Yoke is null || entry.Head is null || entry.Light is null)
                    return false;
                yoke = entry.Yoke;
                head = entry.Head;
                light = entry.Light;
                return true;
            }
            var age = entry.Kind == TurretKind.None ? long.MinValue : entry.Used;
            if (!entry.Queued && entry.Used < _frame && age < oldest)
            {
                oldest = age;
                free = i;
            }
        }
        if (free < 0 || _pending || _build.Count == Batch)
            return false;
        _turrets[free] = new TurretEntry { Kind = kind, Small = small, Queued = true, Used = _frame };
        var at = _build.Count++;
        _build.Slots[at] = free;
        _build.Turrets[at] = kind;
        _build.Cells[at] = small;
        return false;
    }

    /// <summary>Hands this frame's requests to a worker; call once a frame after the last <see cref="TryGet"/>.</summary>
    public void Flush()
    {
        if (_pending || _build.Count == 0)
            return;
        _pending = true;
        _build.CargoPreview = _cargoPreview;
        _handle = jobs.Schedule(_build, 2);
    }

    /// <summary>An indexed mesh as the triangle list <see cref="SceneRenderer.AddTriangles"/> draws.</summary>
    internal static MeshVertex[] Soup(List<MeshVertex> vertices, List<uint> indices)
    {
        var soup = new MeshVertex[indices.Count];
        for (var k = 0; k < soup.Length; k++)
            soup[k] = vertices[(int)indices[k]];
        return soup;
    }

    /// <summary>Builds every model asked for in one frame. Its arrays are read by the frame only once it is complete.</summary>
    private sealed class Build(PropulsionLook look) : IJob
    {
        public int Count;
        public bool CargoPreview;
        public readonly int[] Slots = new int[Batch];
        public readonly TurretKind[] Turrets = new TurretKind[Batch];
        public readonly BlockType?[] Types = new BlockType?[Batch];
        public readonly Vector3[] Halves = new Vector3[Batch];
        public readonly BlockFacing[] Facings = new BlockFacing[Batch];
        public readonly float[] Cells = new float[Batch];
        public readonly MeshVertex[]?[] Results = new MeshVertex[]?[Batch];
        public readonly MeshVertex[]?[] HeadResults = new MeshVertex[]?[Batch];
        public readonly MeshVertex[]?[] LightResults = new MeshVertex[]?[Batch];

        public void Run()
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            for (var i = 0; i < Count; i++)
            {
                if (Turrets[i] != TurretKind.None)
                {
                    // Three parts a turret: the yoke, the head and the head's light, each in its own space.
                    for (var part = 0; part < 3; part++)
                    {
                        vertices.Clear();
                        indices.Clear();
                        if (Turrets[i] == TurretKind.Tractor)
                        {
                            var tractor = look.Tractor!;
                            if (part == 0) PropulsionModels.BuildTractorYoke(Cells[i], tractor, vertices, indices);
                            else if (part == 1) PropulsionModels.BuildTractorHead(Cells[i], tractor, vertices, indices);
                            else PropulsionModels.BuildTractorBars(Cells[i], tractor, vertices, indices);
                        }
                        else
                        {
                            var laser = look.Laser!;
                            if (part == 0) PropulsionModels.BuildTurretYoke(Cells[i], laser.Turret, vertices, indices);
                            else if (part == 1) PropulsionModels.BuildLaserHead(Cells[i], laser, vertices, indices);
                            else PropulsionModels.BuildLaserLens(Cells[i], laser, vertices, indices);
                        }
                        (part == 0 ? Results : part == 1 ? HeadResults : LightResults)[i] = Soup(vertices, indices);
                    }
                    continue;
                }
                var type = Types[i]!;
                vertices.Clear();
                indices.Clear();
                PropulsionModels.BuildPart(type, new PartBox(Vector3.Zero, Halves[i], Facings[i], Cells[i]), look, vertices, indices);
                if (CargoPreview && type.Cargo > 0 && look.Rack is { } rack)
                    PropulsionModels.RackPreview(new PartBox(Vector3.Zero, Halves[i], Facings[i], Cells[i]), rack, look.Cargo, vertices, indices);
                var soup = new MeshVertex[indices.Count];
                for (var k = 0; k < soup.Length; k++)
                    soup[k] = vertices[(int)indices[k]];
                Results[i] = soup;
            }
        }
    }
}
