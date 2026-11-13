using System.Numerics;
using Brutal.Concurrency.Jobs;
using Brutal.Numerics;
using Prospect.Engine.Jobs;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>A bounded triangle stream with matching original positions for distance fading.</summary>
internal sealed class DamageMesh
{
    public MeshVertex[] Original = [];
    public MeshVertex[] Dented = [];
    private int[] _changed = [];

    /// <summary>Builds whole live hulls, or refuses the whole hull before allocating output.</summary>
    public static DamageMesh? Build(HullDesign design, ReadOnlySpan<ulong> alive, in VisualDamageState state, int capacity, float maxDepth)
    {
        // Six quad faces, each at most two triangles, plus two extra triangles for each dent.
        if ((long)design.BlockCount * 36 + state.Count * 6 > capacity) return null;
        var original = new List<MeshVertex>();
        var dented = new List<MeshVertex>();
        Span<int> corners = stackalloc int[4];
        Span<Vector3> vertices = stackalloc Vector3[8];
        var intact = BlockSet.Equal(alive, design.FullSet);
        for (var b = 0; b < design.BlockCount; b++)
        {
            if (!BlockSet.Has(alive, b)) continue;
            var shape = design.Shapes[b]; var half = design.BlockHalfExtents[b]; var offset = design.BlockOffsets[b];
            var nv = BlockShapes.Vertices(shape, half, vertices);
            var centroid = Vector3.Zero;
            for (var v = 0; v < nv; v++) centroid += vertices[v];
            centroid = offset + centroid / nv;
            for (var face = 0; face < 6; face++)
            {
                if (intact && (design.ExteriorFaces[b] & (1 << face)) == 0) continue;
                var n = BlockShapes.Face(shape, face, corners);
                if (n == 0) continue;
                var normal = BlockShapes.FaceNormal(shape, half, face);
                var profile = default(DentProfile);
                for (var p = 0; p < state.Count; p++)
                    if (state.Dents[p].Block == b && state.Dents[p].Face == face) profile = state.Dents[p];
                var a = offset + BlockShapes.Corner(corners[0], half);
                if (profile.Depth == 0)
                {
                    for (var c = 1; c + 1 < n; c++)
                        Triangle(a, offset + BlockShapes.Corner(corners[c], half), offset + BlockShapes.Corner(corners[c + 1], half), a, normal);
                    continue;
                }
                var middle = Vector3.Zero;
                for (var c = 0; c < n; c++) middle += offset + BlockShapes.Corner(corners[c], half);
                // Keep edge hits strictly inside the face so the fan never contains a zero-area triangle.
                var point = Vector3.Lerp(profile.Point(design), middle / n, 0.02f);
                var inward = Vector3.Lerp(point, centroid, profile.Depth / 255f * maxDepth);
                for (var c = 0; c < n; c++)
                    Triangle(point, offset + BlockShapes.Corner(corners[c], half), offset + BlockShapes.Corner(corners[(c + 1) % n], half), inward, normal);
            }
        }
        var changes = new List<int>();
        for (var i = 0; i < original.Count; i++)
            if (!original[i].Position.Equals(dented[i].Position) || !original[i].Normal.Equals(dented[i].Normal)) changes.Add(i);
        return new DamageMesh { Original = original.ToArray(), Dented = dented.ToArray(), _changed = changes.ToArray() };

        void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 moved, Vector3 normal)
        {
            var slope = Vector3.Normalize(Vector3.Cross(b - moved, c - moved));
            original.Add(Vertex(a, normal)); original.Add(Vertex(b, normal)); original.Add(Vertex(c, normal));
            dented.Add(Vertex(moved, slope)); dented.Add(Vertex(b, slope)); dented.Add(Vertex(c, slope));
        }
    }

    /// <summary>Copies the faded triangle stream into a frame-owned destination without allocating.</summary>
    public void CopyTo(Span<MeshVertex> destination, float fade)
    {
        if (fade >= 1) { Dented.AsSpan().CopyTo(destination); return; }
        Original.AsSpan().CopyTo(destination);
        if (fade <= 0) return;
        foreach (var i in _changed)
        {
            destination[i].Position = Original[i].Position * (1 - fade) + Dented[i].Position * fade;
            destination[i].Normal = Original[i].Normal * (1 - fade) + Dented[i].Normal * fade;
        }
    }

    private static MeshVertex Vertex(Vector3 p, Vector3 n) => new() { Position = new float3(p.X, p.Y, p.Z), Normal = new float3(n.X, n.Y, n.Z) };
}

/// <summary>One frozen worker request; cache keys include session, identity, design, loss and cosmetic revision.</summary>
internal sealed class DamageMeshes(JobSystem jobs, int vertexCapacity = DamageMeshes.VertexCapacity)
{
    public const int Slots = 64;
    public const int VertexCapacity = 40000;
    private struct Entry { public uint Id, Revision, Version; public HullDesign? Design; public DamageMesh? Mesh; public bool Ready; }
    private readonly Entry[] _entries = new Entry[Slots];
    private readonly Build _build = new();
    private JobHandle _handle;
    private bool _pending;
    private int _generation;

    public void Clear() { _generation++; Array.Clear(_entries); }

    /// <summary>Polls without uploading; only an exact request match can be used by Get.</summary>
    public void Begin(ReadOnlySpan<Prospect.Game.Client.HullView> hulls)
    {
        for (var i = 0; i < Slots; i++)
        {
            var found = false;
            foreach (ref readonly var hull in hulls) if (hull.Id == _entries[i].Id) { found = true; break; }
            if (!found) _entries[i] = default;
        }
        if (!_pending || !jobs.IsComplete(_handle)) return;
        _pending = false;
        if (_build.Generation != _generation) return;
        ref var entry = ref _entries[_build.Slot];
        if (entry.Id != _build.Id || entry.Design != _build.Design) return;
        entry.Revision = _build.Revision; entry.Version = _build.Visual.Version;
        entry.Mesh = _build.Result; entry.Ready = true;
    }

    /// <summary>Called only after distance and projection culling. No allocation on this thread.</summary>
    public DamageMesh? Get(uint id, uint revision, HullDesign design, ReadOnlySpan<ulong> alive, in VisualDamageState visual, float maxDepth)
    {
        var slot = -1;
        for (var i = 0; i < Slots; i++) if (_entries[i].Id == id) { slot = i; break; }
        if (slot < 0)
            for (var i = 0; i < Slots; i++) if (_entries[i].Id == 0) { slot = i; _entries[i] = new Entry { Id = id, Design = design }; break; }
        if (slot < 0) return null;
        ref var entry = ref _entries[slot];
        if (entry.Ready && entry.Design == design && entry.Revision == revision && entry.Version == visual.Version) return entry.Mesh;
        if (!_pending && alive.Length <= _build.Alive.Length)
        {
            _build.Generation = _generation; _build.Slot = slot; _build.Id = id; _build.Revision = revision;
            _build.Capacity = vertexCapacity; _build.MaxDepth = maxDepth; _build.Design = design; _build.Visual = visual; _build.Words = alive.Length; alive.CopyTo(_build.Alive);
            entry.Design = design;
            _pending = true; _handle = jobs.Schedule(_build, 2);
        }
        return null;
    }

    private sealed class Build : IJob
    {
        public int Generation, Slot, Words, Capacity;
        public float MaxDepth;
        public uint Id, Revision;
        public HullDesign Design = null!;
        public VisualDamageState Visual;
        public readonly ulong[] Alive = new ulong[64];
        public DamageMesh? Result;
        public void Run() => Result = DamageMesh.Build(Design, Alive.AsSpan(0, Words), Visual, Capacity, MaxDepth);
    }
}
