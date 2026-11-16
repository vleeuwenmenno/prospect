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

    private readonly Entry[] _entries = new Entry[Slots];
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
        if (!_pending || !jobs.IsComplete(_handle))
            return;
        _pending = false;
        for (var i = 0; i < _build.Count; i++)
        {
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
        _build.Types[at] = type;
        _build.Halves[at] = half;
        _build.Facings[at] = facing;
        _build.Cells[at] = cell;
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

    /// <summary>Builds every model asked for in one frame. Its arrays are read by the frame only once it is complete.</summary>
    private sealed class Build(PropulsionLook look) : IJob
    {
        public int Count;
        public bool CargoPreview;
        public readonly int[] Slots = new int[Batch];
        public readonly BlockType?[] Types = new BlockType?[Batch];
        public readonly Vector3[] Halves = new Vector3[Batch];
        public readonly BlockFacing[] Facings = new BlockFacing[Batch];
        public readonly float[] Cells = new float[Batch];
        public readonly MeshVertex[]?[] Results = new MeshVertex[]?[Batch];

        public void Run()
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();
            for (var i = 0; i < Count; i++)
            {
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
