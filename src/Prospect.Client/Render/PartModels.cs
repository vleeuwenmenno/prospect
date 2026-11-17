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
        public float Small;
        public MeshVertex[]? Yoke;
        public MeshVertex[]? Head;
        public MeshVertex[]? Bars;
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
    /// The clamp jaws of a container of <paramref name="size"/> held in a rack whose deck is on <paramref name="deck"/>;
    /// false while the worker has not finished. Allocates nothing.
    /// </summary>
    public bool TryGetClamp(int size, BlockFacing deck, out ReadOnlySpan<MeshVertex> vertices)
    {
        vertices = default;
        if ((uint)size >= 8 || (uint)deck >= 6 || !_containersReady) return false;
        vertices = _containers.Clamps[size * 6 + (int)deck];
        return vertices.Length > 0;
    }

    private readonly OreBuild _ore = new();
    private JobHandle _oreHandle;
    private bool _oreStarted, _oreReady;

    /// <summary>
    /// The lump of an ore chunk, variant <paramref name="variant"/> (any number; it wraps), as a triangle list; false
    /// while the worker has not finished. Allocates nothing.
    /// </summary>
    public bool TryGetOreChunk(int variant, out ReadOnlySpan<MeshVertex> vertices)
    {
        vertices = default;
        if (!_oreReady) return false;
        vertices = _ore.Results[(int)((uint)variant % OreChunkModels.Variants)];
        return true;
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
        public readonly MeshVertex[][] Results = new MeshVertex[OreChunkModels.Variants][];
        public void Run()
        {
            for (var i = 0; i < Results.Length; i++) Results[i] = OreChunkModels.Build(i);
        }
    }

    private sealed class ContainerBuild(PropulsionLook look) : IJob
    {
        public readonly MeshVertex[][] Clamps = new MeshVertex[8 * 6][];
        public readonly int ResourceSlots = 1 + (look.Rack?.Galaxy.Length ?? 0);
        public readonly MeshVertex[][] Results = new MeshVertex[8 * (1 + (look.Rack?.Galaxy.Length ?? 0))][];
        public void Run()
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
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
                    vertices.Clear(); indices.Clear();
                    if (look.Rack is { } clampLook)
                        PropulsionModels.ClampModel(shape, (BlockFacing)face, clampLook, look.Cargo, vertices, indices);
                    var clamp = new MeshVertex[indices.Count];
                    for (var k = 0; k < clamp.Length; k++) clamp[k] = vertices[(int)indices[k]];
                    Clamps[i * 6 + face] = clamp;
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
        if (_drone.Design is not null && !_droneStarted) { _droneHandle = jobs.Schedule(_drone, 2); _droneStarted = true; }
        if (_droneStarted && !_droneReady && jobs.IsComplete(_droneHandle)) _droneReady = true;
        if (!_pending || !jobs.IsComplete(_handle))
            return;
        _pending = false;
        for (var i = 0; i < _build.Count; i++)
        {
            if (_build.IsTurret[i])
            {
                ref var tEntry = ref _turrets[_build.Slots[i]];
                tEntry.Queued = false;
                if (tEntry.Small == _build.Cells[i])
                {
                    tEntry.Yoke = _build.Results[i];
                    tEntry.Head = _build.HeadResults[i];
                    tEntry.Bars = _build.BarResults[i];
                    Built++;
                }
                _build.Results[i] = null;
                _build.HeadResults[i] = null;
                _build.BarResults[i] = null;
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
        _build.IsTurret[at] = false;
        _build.Types[at] = type;
        _build.Halves[at] = half;
        _build.Facings[at] = facing;
        _build.Cells[at] = cell;
        return false;
    }

    /// <summary>
    /// Returns the worker-built yoke, emitter head and emitter bars of a tractor turret of half-width
    /// <paramref name="small"/>; false while queued. Allocates nothing.
    /// </summary>
    public bool TryGetTractorTurret(float small, out ReadOnlySpan<MeshVertex> yoke, out ReadOnlySpan<MeshVertex> head, out ReadOnlySpan<MeshVertex> bars)
    {
        yoke = default;
        head = default;
        bars = default;
        if (small <= 0 || look.Tractor is null || !look.Models.ContainsKey("tractorBeam"))
            return false;
        var free = -1;
        var oldest = long.MaxValue;
        for (var i = 0; i < _turrets.Length; i++)
        {
            ref var entry = ref _turrets[i];
            if (entry.Small == small)
            {
                entry.Used = _frame;
                if (entry.Yoke is null || entry.Head is null || entry.Bars is null)
                    return false;
                yoke = entry.Yoke;
                head = entry.Head;
                bars = entry.Bars;
                return true;
            }
            var age = entry.Small <= 0 ? long.MinValue : entry.Used;
            if (!entry.Queued && entry.Used < _frame && age < oldest)
            {
                oldest = age;
                free = i;
            }
        }
        if (free < 0 || _pending || _build.Count == Batch)
            return false;
        _turrets[free] = new TurretEntry { Small = small, Queued = true, Used = _frame };
        var at = _build.Count++;
        _build.Slots[at] = free;
        _build.IsTurret[at] = true;
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
        public readonly bool[] IsTurret = new bool[Batch];
        public readonly BlockType?[] Types = new BlockType?[Batch];
        public readonly Vector3[] Halves = new Vector3[Batch];
        public readonly BlockFacing[] Facings = new BlockFacing[Batch];
        public readonly float[] Cells = new float[Batch];
        public readonly MeshVertex[]?[] Results = new MeshVertex[]?[Batch];
        public readonly MeshVertex[]?[] HeadResults = new MeshVertex[]?[Batch];
        public readonly MeshVertex[]?[] BarResults = new MeshVertex[]?[Batch];

        public void Run()
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            for (var i = 0; i < Count; i++)
            {
                if (IsTurret[i])
                {
                    var tractor = look.Tractor!;
                    vertices.Clear();
                    indices.Clear();
                    PropulsionModels.BuildTractorYoke(Cells[i], tractor, vertices, indices);
                    Results[i] = Soup(vertices, indices);

                    vertices.Clear();
                    indices.Clear();
                    PropulsionModels.BuildTractorHead(Cells[i], tractor, vertices, indices);
                    HeadResults[i] = Soup(vertices, indices);

                    vertices.Clear();
                    indices.Clear();
                    PropulsionModels.BuildTractorBars(Cells[i], tractor, vertices, indices);
                    BarResults[i] = Soup(vertices, indices);
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
