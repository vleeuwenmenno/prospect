using System.Numerics;
using Prospect.Client.Render;
using Prospect.Engine.Jobs;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>What the editor's backend needs of the shared world and the renderer's catalogue, without a session.</summary>
public sealed class EditorWorldTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static HullDesign Box(int width, float shield) => HullDesign.Of(new BoxDesign("Damage tick", 1,
        [new(new(0, 0, 0), new(width, 2, 2), new BlockType
        {
            Id = "test", Name = "Core", Core = true, Mass = 1, Hull = 10000, Shield = shield
        })], Content.Blocks.Limits, "damage tick"), Content.Rules);

    [Fact]
    public void TheQuietTimeIsCountedFromTheLastDamageAndNeverSubtractsFromTheSentinel()
    {
        var rules = Content.Editor;
        Assert.Equal(30, rules.QuietSeconds);
        var quiet = rules.QuietTicks;
        Assert.Equal(1800, quiet);
        foreach (var tick in new[] { 0L, 1, quiet - 1, quiet, 1_000_000, long.MaxValue })
        {
            // Never damaged: editable at once, at any tick, without overflow.
            Assert.Equal(0, rules.QuietTicksLeft(tick, long.MinValue));
            Assert.Equal(0, rules.QuietTicksLeft(tick, long.MinValue + 1));
        }
        Assert.Equal(quiet, rules.QuietTicksLeft(5000, 5000));
        Assert.Equal(quiet - 1, rules.QuietTicksLeft(5001, 5000));
        Assert.Equal(1, rules.QuietTicksLeft(5000 + quiet - 1, 5000));
        Assert.Equal(0, rules.QuietTicksLeft(5000 + quiet, 5000));
        Assert.Equal(0, rules.QuietTicksLeft(long.MaxValue, 5000));
        // A stamp ahead of now counts as now.
        Assert.Equal(quiet, rules.QuietTicksLeft(5000, 6000));
        Assert.Equal(quiet, rules.QuietTicksLeft(0, long.MaxValue));
        // The first seconds of a server's life: tick minus the quiet time is negative, and still right.
        Assert.Equal(quiet - 10, rules.QuietTicksLeft(10, 0));
    }

    [Theory]
    [InlineData(2f, 10000f, false)]
    [InlineData(20f, 10000f, true)]
    [InlineData(20f, 0f, true)]
    public void OnlyRealDamageStampsBothHullsWhetherTheShieldPaysOrTheBlocks(float speed, float shield, bool damaged)
    {
        var design = Box(4, shield);
        using var store = new HullStore(2);
        var a = store.Create(1, 0, design, new Double3(3.95, 0, 0));
        var b = store.Create(2, 0, design, Double3.Zero);
        store.State(a.Index).Velocity = new Vector3(-speed, 0, 0);
        Assert.Equal(long.MinValue, store.Info(a.Index).DamageTick);
        var field = new HullField(store, [design]);
        field.BeginRound();
        field.Step(a.Index, default, tick: 77);
        Assert.Equal(77, store.Info(a.Index).ContactTick);
        Assert.Equal(77, store.Info(b.Index).ContactTick);
        foreach (var index in new[] { a.Index, b.Index })
        {
            Assert.Equal(damaged ? 77 : long.MinValue, store.Info(index).DamageTick);
            if (shield > 0)
            {
                // The contact shows on the shield either way; only one that costs charge is damage.
                Assert.True(store.State(index).ShieldHit.Strength > 0);
                Assert.Equal(damaged, store.State(index).Shield < design.Flight.ShieldCapacity);
                Assert.Equal(design.Flight.HullPoints, store.State(index).Hull);
            }
            else
            {
                Assert.True(store.State(index).Hull < design.Flight.HullPoints);
            }
        }
    }

    [Fact]
    public void AFieldTakesLargerDesignsBetweenStepsAndNoMemoryForOnesItReserved()
    {
        var small = Box(1, 0);
        var boxes = new DesignBox[200];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = new DesignBox(new GridSize(i, 0, 0), new GridSize(1, 1, 1), Content.Blocks.Get(i == 0 ? "core" : "hull"));
        var large = HullDesign.Of(new BoxDesign("Long", 1, boxes, Content.Blocks.Limits, "long"), Content.Rules);
        HullDesign[] grown = [small, large];

        foreach (var reserve in new[] { 0, GeometryAdmission.MaxBlocks })
        {
            using var store = new HullStore(4);
            var field = new HullField(store, [small], reserveBlocks: reserve);
            var first = store.Create(1, 0, small, Double3.Zero);
            field.BeginRound();
            field.Step(first.Index, default);

            var before = GC.GetAllocatedBytesForCurrentThread();
            field.SetDesigns(grown);
            var taken = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(reserve == 0, taken > 0);
            Assert.Same(grown, field.Designs);

            // Two of the large design strike each other at their far ends, past the first words of a block set.
            var a = store.Create(1, 1, large, new Double3(0, 0, 500));
            var b = store.Create(2, 1, large, new Double3(0, 0.9, 500));
            store.State(b.Index).Velocity = new Vector3(0, -30, 0);
            field.BeginRound();
            field.Step(b.Index, default, tick: 9);
            Assert.Equal(9, store.Info(a.Index).DamageTick);
            Assert.Equal(9, store.Info(b.Index).DamageTick);
            Assert.True(store.Revision(a.Index) + store.Revision(b.Index) > 0 || store.State(a.Index).Hull < large.Flight.HullPoints);
        }
    }

    [Fact]
    public void AGrownCatalogueKeepsItsInstalledMeshesWhileTheNewOnesAreBuiltAndAFailedBuildChangesNothing()
    {
        using var jobs = new JobSystem(1, "Meshes");
        var preparation = new CatalogueMeshes(jobs);
        var designs = Content.Designs;
        Assert.Equal(0, preparation.Installed(designs));
        preparation.Prepare(designs);
        jobs.CompleteAll();
        var meshes = Assert.IsType<MeshData[]>(preparation.Prepare(designs));
        preparation.Accept(designs, meshes);
        Assert.Equal(designs.Length, preparation.Installed(designs));

        // The same designs with one more: what is installed stays installed while the worker builds.
        var grown = Content.WithDesign(Box(3, 0)).Designs;
        Assert.Equal(designs.Length, preparation.Installed(grown));
        Assert.False(preparation.IsReady(grown));
        Assert.Null(preparation.Prepare(grown));
        Assert.Equal(designs.Length, preparation.Installed(grown));
        jobs.CompleteAll();
        var more = Assert.IsType<MeshData[]>(preparation.Prepare(grown));
        Assert.Equal(grown.Length, more.Length);
        for (var i = 0; i < designs.Length; i++) Assert.Same(meshes[i], more[i]);
        Assert.Equal(MeshBuilder.Hull(grown[^1]).Vertices, more[^1].Vertices);
        preparation.Accept(grown, more);
        Assert.Equal(grown.Length, preparation.Installed(grown));

        // A design that cannot be built: an error for that catalogue, and the installed one untouched.
        HullDesign[] broken = [.. grown, null!];
        preparation.Prepare(broken);
        jobs.CompleteAll();
        Assert.Null(preparation.Prepare(broken));
        Assert.NotNull(preparation.Error);
        Assert.Equal(grown.Length, preparation.Installed(broken));
        Assert.True(preparation.IsReady(grown));

        // A catalogue of other designs has nothing installed, and a shorter one neither.
        HullDesign[] other = [Box(2, 0), .. designs];
        Assert.Equal(0, preparation.Installed(other));
        Assert.Equal(0, preparation.Installed(designs[..2]));
        Assert.Equal(0, preparation.Installed(null));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) preparation.Installed(broken);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void TheBlockCatalogueListsEveryTypeOnceInFileOrder()
    {
        var types = Content.Blocks.Types;
        Assert.Equal("hull", types[0].Id);
        Assert.Equal(types.Count, types.Select(type => type.Id).Distinct().Count());
        foreach (var type in types) Assert.Same(type, Content.Blocks.Get(type.Id));
        Assert.Contains(types, type => type.Core);
    }
}
