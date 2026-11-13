using System.Diagnostics;
using System.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.Jobs;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class DamageRenderTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static HullDesign Design(int count = 1, BlockShape shape = BlockShape.Box) => HullDesign.Of(new BoxDesign("Render fixture", 1,
        Enumerable.Range(0, count).Select(i => new DesignBox(new(i % 35 * 3, i / 35 * 3, 0), new(2, 2, 2), new BlockType
        { Id = "dent", Name = "Hull", Core = i == 0, Mass = 10, Hull = 100 }, shape)).ToArray(), Content.Blocks.Limits, "dent"), Content.Rules);
    private static VisualDamageState Dents(int count, int face = 0) { var v = new VisualDamageState { Count = (byte)count, Version = 1 }; for (var i = 0; i < count; i++) v.Dents[i] = new DentProfile { Block = i, Face = (byte)face, Depth = 255, U = 0, V = 0 }; return v; }
    private static Vector3 V(Brutal.Numerics.float3 p) => new(p.X, p.Y, p.Z);

    [Fact]
    public void EveryBoxAndWedgeFaceRetainsBoundaryAndMovesOnlyInteriorInward()
    {
        Span<int> corners = stackalloc int[4];
        for (var shape = 0; shape <= 12; shape++)
        {
            var design = Design(shape: (BlockShape)shape);
            for (var face = 0; face < 6; face++)
            {
                var count = BlockShapes.Face((BlockShape)shape, face, corners);
                if (count == 0) continue;
                var mesh = DamageMesh.Build(design, design.FullSet, Dents(1, face), 40000, Content.Rules.DentMaxDepth)!;
                var moved = 0;
                for (var i = 0; i < mesh.Dented.Length; i += 3)
                {
                    var a = V(mesh.Dented[i].Position); var b = V(mesh.Dented[i + 1].Position); var c = V(mesh.Dented[i + 2].Position);
                    var cross = Vector3.Cross(b - a, c - a);
                    Assert.True(float.IsFinite(cross.LengthSquared()) && cross.LengthSquared() > 1e-9f);
                    Assert.True(Vector3.Dot(Vector3.Normalize(cross), V(mesh.Dented[i].Normal)) > 0.999f);
                    Assert.Equal(mesh.Original[i + 1].Position, mesh.Dented[i + 1].Position);
                    Assert.Equal(mesh.Original[i + 2].Position, mesh.Dented[i + 2].Position);
                    if (mesh.Original[i].Position.Equals(mesh.Dented[i].Position)) continue;
                    moved++;
                    Assert.True(Vector3.Dot(a - V(mesh.Original[i].Position), BlockShapes.FaceNormal((BlockShape)shape, Vector3.One, face)) < 0);
                    var local = a - design.BlockOffsets[0];
                    Assert.True(Vector3.Distance(local, BlockShapes.Closest((BlockShape)shape, Vector3.One, local)) < 1e-5f);
                }
                Assert.Equal(count, moved);
                var faded = new MeshVertex[mesh.Dented.Length]; mesh.CopyTo(faded, 0);
                Assert.Equal(mesh.Original, faded);
            }
        }
    }

    [Fact]
    public void LossFiltersLaggingDentsAndOverflowRefusesWholeHull()
    {
        var design = Design(2); var alive = design.FullSet.ToArray(); BlockSet.Remove(alive, 0);
        var mesh = DamageMesh.Build(design, alive, Dents(1), 40000, 0.4f)!;
        Assert.Equal(36, mesh.Dented.Length); Assert.Equal(mesh.Original, mesh.Dented);
        Assert.Null(DamageMesh.Build(design, alive, Dents(1), 20, 0.4f));
    }

    [Fact]
    public void FrozenWorkerRejectsStaleLossRepairAndSessionAndReusesSlots()
    {
        using var jobs = new JobSystem(1, "Dents"); var cache = new DamageMeshes(jobs); var design = Design(2);
        var alive = design.FullSet.ToArray(); var state = Dents(1); HullView[] hulls = [new() { Id = 1 }];
        Assert.Null(cache.Get(1, 0, design, alive, state, 0.4f));
        BlockSet.Remove(alive, 0); jobs.CompleteAll(); cache.Begin(hulls);
        Assert.Null(cache.Get(1, 1, design, alive, state, 0.4f));
        jobs.CompleteAll(); cache.Begin(hulls);
        var mesh = cache.Get(1, 1, design, alive, state, 0.4f)!; Assert.Equal(36, mesh.Dented.Length);
        cache.Clear(); Assert.Null(cache.Get(1, 1, design, alive, state, 0.4f)); cache.Clear(); jobs.CompleteAll(); cache.Begin(hulls);
        Assert.Null(cache.Get(1, 1, design, alive, state, 0.4f)); jobs.CompleteAll(); cache.Begin(hulls);
        for (uint id = 2; id < 80; id++) { hulls[0].Id = id; cache.Begin(hulls); Assert.Null(cache.Get(id, 0, design, design.FullSet, state, 0.4f)); jobs.CompleteAll(); cache.Begin(hulls); Assert.NotNull(cache.Get(id, 0, design, design.FullSet, state, 0.4f)); }
    }

    [Fact]
    public void SparksBaselineDeduplicateExpireAndNeverReplayFarEvents()
    {
        var sparks = new CollisionSparks(); var tuning = new CollisionDetail(); var design = Design();
        var hull = new HullView { Id = 1, Visual = Dents(1) };
        hull.Visual.Spark = new CollisionSpark { Sequence = 1, Tick = 100, Contact = hull.Visual.Dents[0] };
        void Observe(bool visible, double tick) => sparks.Observe(hull, design, new Double3(0, 0, 20), Quaternion.Identity, visible, tick, tuning);
        int Expand(double tick) => sparks.Expand(Double3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 1, 1, 1000, tick, tuning).Length;
        Observe(true, 100); Assert.Equal(0, Expand(100));
        hull.Visual.Spark.Sequence++; Observe(true, 100); Assert.Equal(36, Expand(100));
        Observe(true, 100); Assert.Equal(36, Expand(100)); Assert.Equal(0, Expand(140));
        hull.Visual.Spark.Sequence++; hull.Visual.Spark.Tick = 140; Observe(false, 140); Observe(true, 140); Assert.Equal(0, Expand(140));
        hull.Visual.Spark.Sequence++; Observe(true, 200); Assert.Equal(0, Expand(200));
        tuning.SparkCapacity = 2;
        for (var i = 0; i < 4; i++) { hull.Visual.Spark.Sequence++; hull.Visual.Spark.Tick = 201; Observe(true, 201); }
        Assert.Equal(72, Expand(201));
    }

    [Fact]
    public void WarmCacheAndFullSparkPoolAllocateNothing()
    {
        var design = Design(); var state = Dents(1); var tuning = new CollisionDetail();
        var sparks = new CollisionSparks(); HullView[] hulls = [new() { Id = 1, Visual = state }];
        using var jobs = new JobSystem(1, "WarmDents"); var cache = new DamageMeshes(jobs);
        cache.Get(1, 0, design, design.FullSet, state, 0.4f); jobs.CompleteAll(); cache.Begin(hulls);
        sparks.Observe(hulls[0], design, new Double3(0, 0, 20), Quaternion.Identity, true, 100, tuning);
        for (uint i = 1; i <= 140; i++)
        {
            hulls[0].Visual.Spark = new CollisionSpark { Sequence = i, Tick = 100, Contact = state.Dents[0] };
            sparks.Observe(hulls[0], design, new Double3(0, 0, 20), Quaternion.Identity, true, 100, tuning);
        }
        int Prepare()
        {
            cache.Begin(hulls); cache.Get(1, 0, design, design.FullSet, state, 0.4f); sparks.Begin(hulls);
            sparks.Observe(hulls[0], design, new Double3(0, 0, 20), Quaternion.Identity, true, 100, tuning);
            return sparks.Expand(Double3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 1, 1, 1000, 100, tuning).Length;
        }
        for (var i = 0; i < 20; i++) Assert.Equal(128 * 6 * 6, Prepare());
        var timer = Stopwatch.StartNew(); var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) Prepare();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before; timer.Stop(); Assert.Equal(0, allocated);
        File.WriteAllText("/tmp/prospect-visual-damage-sparks-performance.txt", $"Debug CPU; 128 bursts, 6 streaks each, 4608 vertices, 110592 upload bytes, 1 draw; 20 warmups, 100 samples; {timer.Elapsed.TotalMilliseconds / 100:F4} ms/preparation; {allocated} managed bytes. No GPU measurement.\n");
    }

    [Fact]
    public void CpuLoadReportAndWarmPreparationAllocations()
    {
        var design = Design(910); var tuning = new CollisionDetail(); var report = new List<string> { "Debug CPU only; 910 separated boxes/hull; 3 warmups, 20 timed render preparations; worker build average of 3; no GPU/FPS claim.", "hulls,dents,distance,worker_ms,prep_ms,thread_bytes,draws,instances,triangles,upload_bytes" };
        var destination = new MeshVertex[2000000];
        foreach (var hulls in new[] { 30, 50 }) foreach (var dents in new[] { 0, 4, 16 }) foreach (var far in new[] { false, true })
                {
                    var state = Dents(dents); var timer = Stopwatch.StartNew(); DamageMesh? mesh = null;
                    for (var i = 0; i < 3; i++) mesh = DamageMesh.Build(design, design.FullSet, state, DamageMeshes.VertexCapacity, Content.Rules.DentMaxDepth);
                    var worker = timer.Elapsed.TotalMilliseconds / 3;
                    var position = new Vector3(0, 0, far ? 5000 : 200);
                    void Prepare() { for (var h = 0; h < hulls; h++) { var fade = tuning.Fade(position, design.Flight.Radius, 100); if (fade > 0 && dents > 0) mesh!.CopyTo(destination.AsSpan(h * mesh.Dented.Length), fade); } }
                    for (var i = 0; i < 3; i++) Prepare();
                    timer.Restart(); var before = GC.GetAllocatedBytesForCurrentThread(); for (var i = 0; i < 20; i++) Prepare(); var allocated = GC.GetAllocatedBytesForCurrentThread() - before; timer.Stop();
                    Assert.Equal(0, allocated); var detailed = !far && dents > 0;
                    report.Add($"{hulls},{dents},{(far ? "far" : "near")},{worker:F4},{timer.Elapsed.TotalMilliseconds / 20:F4},{allocated},{(detailed ? hulls : 1)},{hulls},{(detailed ? mesh!.Dented.Length / 3 : 910 * 12) * hulls},{(detailed ? mesh!.Dented.Length * 24L * hulls : 0)}");
                }
        File.WriteAllLines("/tmp/prospect-visual-damage-render-performance.csv", report);
        Assert.Equal(0, tuning.Fade(new Vector3(0, 0, 5000), 100, 100));
        Assert.True(tuning.Fade(new Vector3(0, 0, 1500), 1200, 100) > 0);
    }
}
