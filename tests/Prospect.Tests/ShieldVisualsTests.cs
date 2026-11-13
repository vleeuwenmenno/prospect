using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The shield's shell for unit-cell and shipped designs, built by the smooth builder every design shares: wound to
/// face out, closed edge for edge, every block inside it and every triangle's interior clear of the blocks, whole or
/// damaged; round where the old square shell had corners; and losing a block changes the shell without opening it.
/// The square fallback this class once checked is gone: a damaged hull whose own shell would not fit keeps its intact
/// one (see <see cref="SmoothShieldTests"/>).
/// </summary>
public sealed class ShieldShellTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly BlockCatalog Catalog = BlockCatalog.Load(AppPaths.Content("blocks", "blocks.json"));
    private static readonly HullDesign Cube2 = Grid(2);
    private static readonly HullDesign Cube3 = Grid(3);

    /// <summary>A design with blocks at these cells, two metres a block; the first five are the parts a hull needs to fly.</summary>
    private static HullDesign Design(IReadOnlyList<(int X, int Y, int Z)> cells)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"prospect-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var blocks = new List<string>();
            for (var i = 0; i < cells.Count; i++)
            {
                var type = i switch { 0 => "core", 1 => "shield", 2 => "thruster", 3 => "gyro", 4 => "generator", _ => "hull" };
                blocks.Add($"{{ \"at\": [{cells[i].X}, {cells[i].Y}, {cells[i].Z}], \"type\": \"{type}\" }}");
            }

            var path = Path.Combine(dir, "test.json");
            File.WriteAllText(path, $"{{ \"name\": \"T\", \"class\": \"ship\", \"blockSize\": 2.0, \"color\": [0.5, 0.5, 0.5], \"blocks\": [{string.Join(",", blocks)}] }}");
            return HullDesign.Load(path, Catalog, Catalog.Rules);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static HullDesign Grid(int n)
    {
        var cells = new List<(int, int, int)>();
        for (var i = 0; i < n * n * n; i++)
            cells.Add((i % n, i / n % n, i / (n * n)));
        return Design(cells);
    }

    /// <summary>Sixty-four blocks: a 4 x 4 x 4 cube, a line, touching only at edges, or only at corners.</summary>
    private static HullDesign Sixty(string shape)
    {
        var cells = new List<(int, int, int)>();
        for (var i = 0; i < 64; i++)
        {
            cells.Add(shape switch
            {
                "cube" => (i % 4, i / 4 % 4, i / 16),
                "line" => (0, 0, i),
                "staircase" => (i / 2, (i + 1) / 2, 0),
                "diagonal" => (i, i, i),
                // Every other cell of an 8 x 8 x 2 slab.
                _ => (i % 8, i / 8 % 4 * 2 + (i % 8 + i / 32) % 2, i / 32),
            });
        }

        return Design(cells);
    }

    private static ShellVertex[] Shell(HullDesign design, ulong alive)
    {
        var shell = SmoothShieldTests.Triangles(design, [alive], design.ShellShape);
        Assert.True(shell is not null, "the shell does not fit");
        return shell!;
    }

    public static TheoryData<int> Designs()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Content.Designs.Length; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public void EveryDesignWholeOrDamagedGetsAClosedShellClearOfItsBlocks(int index)
    {
        var design = Content.Designs[index];
        Assert.False(design.IntactShell.IsEmpty);
        SmoothShieldTests.Holds(design, design.FullSet, SmoothShieldTests.Intact(design));
        var random = new Random(index * 31 + 7);
        for (var i = 0; i < 6; i++)
        {
            var alive = design.FullMask() & ((ulong)random.NextInt64() | (ulong)random.NextInt64() << 1);
            if (alive != 0)
                SmoothShieldTests.Holds(design, [alive], Shell(design, alive));
        }
    }

    [Fact]
    public void EveryArrangementOfTwoByTwoByTwoBlocksHolds()
    {
        // Blocks touching along an edge or only at a corner, with and without the blocks between.
        for (ulong alive = 1; alive < 256; alive++)
            SmoothShieldTests.Holds(Cube2, [alive], Shell(Cube2, alive));
    }

    [Fact]
    public void RandomArrangementsOfThreeByThreeByThreeBlocksHold()
    {
        var random = new Random(13);
        for (var i = 0; i < 40; i++)
        {
            var alive = Cube3.FullMask() & (ulong)random.NextInt64();
            if (alive != 0)
                SmoothShieldTests.Holds(Cube3, [alive], Shell(Cube3, alive));
        }
    }

    [Theory]
    [InlineData("cube")]
    [InlineData("line")]
    [InlineData("staircase")]
    [InlineData("checker")]
    [InlineData("diagonal")]
    public void SixtyFourBlocksHold(string shape)
    {
        var design = Sixty(shape);
        SmoothShieldTests.Holds(design, design.FullSet, SmoothShieldTests.Intact(design));
    }

    [Fact]
    public void ARoundShellLosesTheCubesCornersAndKeepsItsFaces()
    {
        // One cube: straight out of a face's middle the shell is the margin off, as a square one was; out of a
        // corner it is about the margin off too, where a square shell's corner was √3 times that.
        var design = HullDesign.Of(new BoxDesign("cube", 2f, [new DesignBox(default, new GridSize(1, 1, 1), Catalog.Get("core"))], Catalog.Limits, "cube"), Catalog.Rules);
        var shell = Shell(design, 1UL);
        var half = design.BlockSize * 0.5f;
        var margin = design.BlockSize * GeometryAdmission.Margin;
        // Rays start a hair off the vertices a fan or a run shares, so a hit is never lost on an edge.
        var nudge = new Vector3(0.0013f, 0.0007f, 0.0011f);
        Assert.Equal(margin, Hit(shell, new Vector3(half, 0, 0) + nudge, Vector3.UnitX), 2);
        var corner = Hit(shell, new Vector3(half) + nudge, Vector3.Normalize(Vector3.One));
        Assert.InRange(corner, margin * 0.85f, margin * 1.3f);
        Assert.True(corner < margin * MathF.Sqrt(3) * 0.8f, $"the corner is {corner} out, as a square shell's would be");
        var edge = Hit(shell, new Vector3(half, half, 0) + nudge, Vector3.Normalize(new Vector3(1, 1, 0)));
        Assert.True(edge < margin * MathF.Sqrt(2) * 0.85f, $"the edge is {edge} out, as a square shell's would be");
    }

    /// <summary>How far along a ray from a point the shell is first met.</summary>
    private static float Hit(ReadOnlySpan<ShellVertex> shell, Vector3 from, Vector3 direction)
    {
        var best = float.MaxValue;
        for (var i = 0; i < shell.Length; i += 3)
        {
            var a = shell[i].Position;
            var e1 = shell[i + 1].Position - a;
            var e2 = shell[i + 2].Position - a;
            var p = Vector3.Cross(direction, e2);
            var det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f) continue;
            var t0 = from - a;
            var u = Vector3.Dot(t0, p) / det;
            var q = Vector3.Cross(t0, e1);
            var v = Vector3.Dot(direction, q) / det;
            if (u < 0 || v < 0 || u + v > 1) continue;
            var t = Vector3.Dot(e2, q) / det;
            if (t > 0) best = Math.Min(best, t);
        }
        return best;
    }

    [Fact]
    public void LosingABlockChangesTheShellAndOpensNoHole()
    {
        // Where the lost block was need not be outside: a gap of a block or so is closed over on purpose.
        var design = Content.Pod;
        var emitter = Array.FindIndex(design.Types, t => t.Shield > 0);
        var damaged = design.FullMask() & ~(1UL << emitter);
        var shell = Shell(design, damaged);
        SmoothShieldTests.Holds(design, [damaged], shell);
        var intact = SmoothShieldTests.Intact(design);
        var same = intact.Length == shell.Length;
        for (var i = 0; same && i < shell.Length; i++)
            same = intact[i].Position == shell[i].Position;
        Assert.False(same, "losing the emitter should change the shell");
    }
}

/// <summary>
/// Each hull's shield as drawn: online it glows, a break collapses it once and it is gone until the simulation
/// brings it back, when it fades in; every newly reported hit ripples, and an old one is never shown as new.
/// </summary>
public sealed class ShieldVisualsTests
{
    private const float Dt = 1f / 60f;
    private static readonly GameContent Content = GameContent.Load();
    private static readonly HullDesign Pod = Content.Pod;
    private static readonly int Emitter = Array.FindIndex(Pod.Types, t => t.Shield > 0);
    private static readonly ShieldLook Look = ShieldLook.Load();

    private readonly ShieldVisuals _shields = new(Look);
    private readonly ShieldDraw[] _draws = new ShieldDraw[ShieldVisuals.MaxShields];
    private readonly ShieldInstance[] _instances = new ShieldInstance[ShieldVisuals.MaxShields];
    private readonly MeshVertex[] _vertices = new MeshVertex[ShieldVisuals.MaxVertices];
    private readonly ushort[] _indices = new ushort[ShieldVisuals.MaxIndices];

    private static HullView Hull(uint id, float shield = 1500f, bool offline = false, ulong? blocks = null, float changeAge = 0f) =>
        new()
        {
            Id = id,
            Design = (byte)Content.PodDesign,
            Blocks = BlockMasks.Set(blocks ?? Pod.FullMask()),
            Stats = Pod.StatsOf([blocks ?? Pod.FullMask()]),
            Orientation = Quaternion.Identity,
            Shield = shield,
            ShieldOffline = offline,
            ShieldChangeAge = changeAge,
        };

    // The publication the frames draw, named as SessionSnapshot.PublishedAt names it, and the tick it shows its
    // hulls at. Every test says which frames draw a new publication and which draw the last one again.
    private long _publication;
    private double _shownTick;

    /// <summary>
    /// Draws a new publication, one tick of simulation after the last, in a frame one tick long. Returns the
    /// first shell drawn, if any.
    /// </summary>
    private ShieldInstance? Frame(params HullView[] hulls) => Publish(Dt, Dt, hulls);

    /// <summary>
    /// Draws a new publication that shows its hulls <paramref name="simulated"/> seconds after the last one did,
    /// in a frame <paramref name="dt"/> long: the two differ after a stall, where the application caps the
    /// frame's length.
    /// </summary>
    private ShieldInstance? Publish(float simulated, float dt, params HullView[] hulls)
    {
        _publication += 977;
        _shownTick += simulated / HullSim.Dt;
        return Drawn(dt, hulls);
    }

    /// <summary>Draws the last publication again, in a frame <paramref name="dt"/> long.</summary>
    private ShieldInstance? Repeat(float dt, params HullView[] hulls) => Drawn(dt, hulls);

    private ShieldInstance? Drawn(float dt, HullView[] hulls)
    {
        for (var i = 0; i < hulls.Length; i++)
            hulls[i].ShownTick = _shownTick;
        _shields.Update(hulls, Content.Designs, _publication, dt);
        _shields.Begin(_draws);
        foreach (var hull in hulls)
            _shields.Add(hull.Id, new Vector3(1, 2, 3), Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        return _shields.Draws.Length > 0 ? _instances[0] : null;
    }

    private static int RippleCount(in ShieldInstance drawn) =>
        (drawn.Strengths.X > 0f ? 1 : 0) + (drawn.Strengths.Y > 0f ? 1 : 0) + (drawn.Strengths.Z > 0f ? 1 : 0) + (drawn.Strengths.W > 0f ? 1 : 0);

    /// <summary>The age of the one ripple spreading from the hit at <paramref name="x"/>, as <see cref="ShieldHit.X"/> has it.</summary>
    private static float RippleAge(in ShieldInstance drawn, sbyte x)
    {
        var at = x * Pod.Flight.Radius / 127f;
        float? age = null;
        Span<float4> ripples = [drawn.Ripple0, drawn.Ripple1, drawn.Ripple2, drawn.Ripple3];
        Span<float> strengths = [drawn.Strengths.X, drawn.Strengths.Y, drawn.Strengths.Z, drawn.Strengths.W];
        for (var i = 0; i < ripples.Length; i++)
        {
            if (strengths[i] <= 0f || MathF.Abs(ripples[i].X - at) > 1e-3f)
                continue;
            Assert.Null(age);
            age = ripples[i].W;
        }

        Assert.True(age.HasValue, $"no ripple spreads from {x}");
        return age!.Value;
    }

    [Fact]
    public void TheShellsFitEveryHullASnapshotCanShow() => Assert.Equal(ClientWorld.MaxViews, ShieldVisuals.MaxShields);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public void ReplacingHullsInAFullSnapshotKeepsEverySurvivingAndNewShield(int replaced)
    {
        var hulls = new HullView[ShieldVisuals.MaxShields];
        for (var i = 0; i < hulls.Length; i++)
            hulls[i] = Hull((uint)i + 1);
        Frame(hulls);
        for (var i = hulls.Length - replaced; i < hulls.Length; i++)
            hulls[i] = Hull((uint)(i + hulls.Length + 1));
        Frame(hulls);
        Assert.Equal(hulls.Length, _shields.Draws.Length);
        foreach (var hull in hulls)
        {
            _shields.Begin(_draws);
            _shields.Add(hull.Id, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
            Assert.Equal(1, _shields.Draws.Length);
        }
    }

    [Fact]
    public void AShieldOnlineAtFirstSightGlowsAtOnceAndAHullWithoutOneHasNoShell()
    {
        var drawn = Frame(Hull(1))!.Value;
        Assert.Equal(1f, drawn.Params.X);
        Assert.Equal(0f, drawn.Params.Z);
        Assert.Equal(new Vector3(1, 2, 3), new Vector3(drawn.Row0.W, drawn.Row1.W, drawn.Row2.W));

        // The emitter is gone: no shield, no shell, and nothing pretends to shut down.
        var bare = new ShieldVisuals(Look);
        bare.Update([Hull(2, 0f, blocks: Pod.FullMask() & ~(1UL << Emitter))], Content.Designs, 1, Dt);
        bare.Begin(_draws);
        bare.Add(2, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        Assert.Equal(0, bare.Draws.Length);
    }

    [Fact]
    public void ABreakCollapsesOnceThenNothingIsDrawnUntilTheShieldIsBackOnline()
    {
        Frame(Hull(1));
        var collapse = Frame(Hull(1, 0f, offline: true))!.Value;
        Assert.True(collapse.Params.Z > 0f);
        Assert.Equal(0f, collapse.Params.X);

        // The collapse runs its course, then the offline shield is not drawn at all, while its charge builds.
        var frames = 1;
        while (Frame(Hull(1, frames * 2f, offline: true, changeAge: frames * Dt)) is { } still)
        {
            Assert.True(still.Params.Z > 0f);
            frames++;
            Assert.True(frames < 200);
        }

        Assert.InRange(frames * Dt, Look.CollapseSeconds - 2 * Dt, Look.CollapseSeconds + 2 * Dt);
        for (var i = 1; i <= 120; i++)
            Assert.Null(Frame(Hull(1, 300f, offline: true, changeAge: (frames + i) * Dt)));

        // Back online: it fades in over a second, from nearly nothing.
        var first = Frame(Hull(1, 380f, changeAge: Dt))!.Value;
        Assert.InRange(first.Params.X, 0f, 2 * Dt / Look.FadeInSeconds);
        ShieldInstance last = first;
        for (var i = 0; i < 80; i++)
        {
            var next = Frame(Hull(1, 380f, changeAge: Dt * (i + 2)))!.Value;
            Assert.True(next.Params.X >= last.Params.X);
            last = next;
        }

        Assert.Equal(1f, last.Params.X);
    }

    [Fact]
    public void ARejoinOrFirstSightOfABrokenShieldShowsNoCollapse()
    {
        Assert.Null(Frame(Hull(7, 100f, offline: true)));
        _shields.Clear();
        Assert.Null(Frame(Hull(7, 100f, offline: true)));
    }

    [Fact]
    public void LosingTheLastEmitterWhileOnlineCollapsesTheShieldOnce()
    {
        Frame(Hull(1));
        var lost = Pod.FullMask() & ~(1UL << Emitter);
        Assert.True(Frame(Hull(1, 0f, blocks: lost))!.Value.Params.Z > 0f);
        for (var i = 1; i <= 60; i++)
            Frame(Hull(1, 0f, blocks: lost, changeAge: i * Dt));
        Assert.Null(Frame(Hull(1, 0f, blocks: lost, changeAge: 61 * Dt)));
    }

    [Fact]
    public void ANewHitRipplesFromWhereItLandedAndAnOldOneNeverDoes()
    {
        var radius = Pod.Flight.Radius;
        var hit = new ShieldHit { Serial = 3, Strength = 255, X = 127, Y = 0, Z = 0 };

        // First sight of a hit two seconds old: nothing to show.
        var old = Hull(1);
        old.ShieldHit = hit;
        old.ShieldHitAge = 2f;
        Assert.Equal(0f, Frame(old)!.Value.Strengths.X + Frame(old)!.Value.Strengths.Y + Frame(old)!.Value.Strengths.Z + Frame(old)!.Value.Strengths.W);

        // A new hit, reported a little ahead of where the hull is drawn: it waits, then spreads, then ends.
        var fresh = old;
        fresh.ShieldHit.Serial = 4;
        fresh.ShieldHitAge = -0.05f;
        var drawn = Frame(fresh)!.Value;
        var slot = drawn.Strengths.X > 0 ? drawn.Ripple0 : drawn.Strengths.Y > 0 ? drawn.Ripple1 : drawn.Strengths.Z > 0 ? drawn.Ripple2 : drawn.Ripple3;
        Assert.Equal(radius, slot.X, 3);
        Assert.InRange(slot.W, -0.05f, 0f);
        for (var i = 0; i < (int)(Look.RippleSeconds / Dt) + 5; i++)
        {
            fresh.ShieldHitAge += Dt;
            drawn = Frame(fresh)!.Value;
        }

        Assert.Equal(0f, drawn.Strengths.X + drawn.Strengths.Y + drawn.Strengths.Z + drawn.Strengths.W);
    }

    [Fact]
    public void ManyHitsKeepTheNewestFourRipples()
    {
        var hull = Hull(1);
        for (byte serial = 1; serial <= 9; serial++)
        {
            hull.ShieldHit = new ShieldHit { Serial = serial, Strength = 100, X = (sbyte)(serial * 10) };
            hull.ShieldHitAge = 0f;
            Frame(hull);
        }

        var drawn = Frame(hull)!.Value;
        Span<float> xs = [drawn.Ripple0.X, drawn.Ripple1.X, drawn.Ripple2.X, drawn.Ripple3.X];
        var radius = Pod.Flight.Radius;
        for (byte serial = 6; serial <= 9; serial++)
            Assert.Contains(xs.ToArray(), x => MathF.Abs(x - serial * 10 * radius / 127f) < 1e-3f);
    }

    [Fact]
    public void TheShellIsDrawnOutAndCutWithItsHull()
    {
        _shields.Update([Hull(1)], Content.Designs, 1, Dt);
        _shields.Begin(_draws);
        var material = 1f + 4f * (5 + 1);
        _shields.Add(1, Vector3.Zero, Quaternion.Identity, 3f, material, _instances, _vertices, _indices);
        var drawn = _instances[0];
        Assert.Equal(material, drawn.Params.W);
        Assert.Equal(3f, drawn.Row2.Z, 4);
        Assert.Equal(1f, drawn.Row0.X, 4);

        // Its vertices are the intact shell its design was admitted with.
        var shell = Pod.IntactShell.Span;
        Assert.True(shell.Length > 0);
        Assert.Equal(shell.Length, _shields.Draws[0].VertexCount);
        Assert.True(Pod.IntactIndices.Span.SequenceEqual(_indices.AsSpan(_shields.Draws[0].FirstIndex, _shields.Draws[0].IndexCount)));
        Assert.Equal(shell[5].Position.Y, _vertices[_shields.Draws[0].FirstVertex + 5].Position.Y);

        // A hull not in the snapshot this frame adds nothing.
        _shields.Add(99, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        Assert.Equal(1, _shields.Draws.Length);
    }

    [Fact]
    public void HullsThatLostAsManyBlocksButNotTheSameOnesEachGetTheirOwnShell()
    {
        // Two blocks whose loss leaves shells of different sizes, neither of them the emitter.
        int Size(ulong mask) => SmoothShieldTests.Count(Pod, [mask], Pod.ShellShape, out _);
        var first = -1;
        var second = -1;
        for (var b = 1; b < Pod.Cells.Length && second < 0; b++)
        {
            if (b == Emitter)
                continue;
            if (first < 0)
                first = b;
            else if (Size(Pod.FullMask() & ~(1UL << b)) != Size(Pod.FullMask() & ~(1UL << first)))
                second = b;
        }

        Assert.True(second > 0);
        var lostFirst = Pod.FullMask() & ~(1UL << first);
        var lostSecond = Pod.FullMask() & ~(1UL << second);

        // The same revision, one block lost each: the blocks themselves tell the shells apart.
        var a = Hull(1, blocks: lostFirst) with { Revision = 1 };
        var b2 = Hull(2, blocks: lostSecond) with { Revision = 1 };
        Frame(a, b2);
        Assert.Equal(Size(lostFirst), _shields.Draws[0].VertexCount);
        Assert.Equal(Size(lostSecond), _shields.Draws[1].VertexCount);

        // A view's blocks live in its snapshot's memory, which is used again: the shell keeps its own copy, so
        // memory that now reads other blocks is not taken for the old ones.
        var words = new[] { lostFirst };
        Frame(Hull(3, blocks: lostFirst) with { Revision = 1, Blocks = words });
        Assert.Equal(Size(lostFirst), _shields.Draws[0].VertexCount);
        words[0] = lostSecond;
        Frame(Hull(4, blocks: lostSecond) with { Revision = 1, Blocks = words }, Hull(5, blocks: lostFirst) with { Revision = 1 });
        Assert.Equal(Size(lostSecond), _shields.Draws[0].VertexCount);
        Assert.Equal(Size(lostFirst), _shields.Draws[1].VertexCount);
    }

    [Fact]
    public void LowChargeMakesTheShellUnstableSmoothly()
    {
        var full = Frame(Hull(1, 1500f))!.Value.Params.Y;
        var low = Frame(Hull(1, 30f))!.Value.Params.Y;
        Assert.Equal(0f, full);
        Assert.Equal(1f, low);
        var previous = 1f;
        for (var shield = 30f; shield <= 600f; shield += 15f)
        {
            var value = Frame(Hull(1, shield))!.Value.Params.Y;
            Assert.True(value <= previous && previous - value < 0.15f);
            previous = value;
        }
    }

    [Fact]
    public void HalfChargeAlreadyShowsStrainAndLightContactsHaveAVisibleFloor()
    {
        var capacity = Pod.Flight.ShieldCapacity;
        Assert.Equal(0f, Frame(Hull(1, capacity))!.Value.Params.Y);
        var early = Frame(Hull(1, capacity * 0.7f))!.Value.Params.Y;
        var half = Frame(Hull(1, capacity * 0.5f))!.Value.Params.Y;
        var low = Frame(Hull(1, capacity * 0.2f))!.Value.Params.Y;
        Assert.InRange(early, 0.01f, 0.2f);
        Assert.InRange(half, 0.3f, 0.7f);
        Assert.True(low > half);
        var hit = Hull(2);
        hit.ShieldHit = new ShieldHit { Serial = 1, Strength = 1 };
        var drawn = Frame(hit)!.Value;
        Assert.InRange(drawn.Strengths.X, 0.6f, 1f);
        Assert.Equal(Look.ImpactRadius, Look.Constants.Impact.X);
        Assert.Equal(Look.ImpactSeconds, Look.Constants.Impact.Y);
        Assert.Equal(Look.ImpactGlow, Look.Constants.Impact.Z);
        Assert.Equal(Look.RippleGlow, Look.Constants.Ripple.W);
    }

    [Fact]
    public void AShieldChargingUpShiversAndSettlesOnceFull()
    {
        var capacity = Pod.Flight.ShieldCapacity;
        Assert.Equal(0f, Frame(Hull(1, capacity * 0.5f))!.Value.Recovery.Y);

        // One rise is not yet a recharge; a second running is.
        Assert.Equal(0f, Frame(Hull(1, capacity * 0.51f))!.Value.Recovery.Y);
        var charging = Frame(Hull(1, capacity * 0.52f))!.Value;
        Assert.Equal(1f, charging.Recovery.Y);
        Assert.Equal(Look.RecoveryHz, charging.Recovery.Z);
        var strain = Look.RecoveryWobble * Pod.BlockSize / Pod.Flight.Radius;
        Assert.Equal(strain, charging.Recovery.X, 5);

        // As a remote hull's charge reads, new only every third publication: it keeps shivering between.
        var charge = capacity * 0.52f;
        for (var i = 0; i < 30; i++)
        {
            if (i % 3 == 0)
                charge += capacity * 0.01f;
            Assert.True(Frame(Hull(1, charge))!.Value.Recovery.Y > 0.9f);
        }

        // Full: it settles over the settle time, smoothly, and stays settled.
        var previous = 1f;
        var frames = 0;
        float level;
        do
        {
            level = Frame(Hull(1, capacity))!.Value.Recovery.Y;
            Assert.True(level <= previous && previous - level < 0.1f, $"{previous} to {level}");
            previous = level;
            frames++;
        }
        while (level > 0f);
        Assert.InRange(frames * Dt, Look.RecoverySettleSeconds - 2 * Dt, Look.RecoverySettleSeconds + 3 * Dt);
        for (var i = 0; i < 30; i++)
            Assert.Equal(0f, Frame(Hull(1, capacity))!.Value.Recovery.X);
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("hit")]
    [InlineData("repeated")]
    public void AChargeThatDoesNotRiseNeverShivers(string why)
    {
        var capacity = Pod.Flight.ShieldCapacity;
        var charge = capacity * 0.4f;
        for (var i = 0; i < 40; i++)
        {
            ShieldInstance drawn;
            switch (why)
            {
                case "waiting":
                    // Below full but waiting out its delay, or with no energy to charge with: the charge holds.
                    drawn = Frame(Hull(1, charge))!.Value;
                    break;
                case "hit":
                    // Each hit takes some away, and each rise after one is the first of its run.
                    charge += i % 2 == 0 ? -capacity * 0.02f : capacity * 0.01f;
                    drawn = Frame(Hull(1, charge))!.Value;
                    break;
                default:
                    // One publication drawn again and again, however long.
                    drawn = i == 0 ? Frame(Hull(1, charge))!.Value : Repeat(Dt, Hull(1, charge * (1f + i)))!.Value;
                    break;
            }

            Assert.Equal(0f, drawn.Recovery.Y);
            Assert.Equal(0f, drawn.Recovery.X);
        }
    }

    [Fact]
    public void ARestartShiversAsItFadesInAndOnWhileItChargesWhileOfflineNothingShows()
    {
        var capacity = Pod.Flight.ShieldCapacity;
        Frame(Hull(1));
        Frame(Hull(1, 0f, offline: true));

        // Broken and charging back: once the collapse is over nothing is drawn, shivering or not.
        var frames = 1;
        while (Frame(Hull(1, frames * 4f, offline: true, changeAge: frames * Dt)) is not null)
            frames++;
        for (var i = 0; i < 20; i++, frames++)
            Assert.Null(Frame(Hull(1, frames * 4f, offline: true, changeAge: frames * Dt)));

        // Back online at the restart threshold: it fades in shivering, and goes on while the charge rises.
        var charge = capacity * 0.25f;
        for (var i = 1; i < 90; i++)
        {
            charge += 4f;
            var drawn = Frame(Hull(1, charge, changeAge: i * Dt))!.Value;
            Assert.True(drawn.Params.X > 0f);
            Assert.Equal(1f, drawn.Recovery.Y);
        }
    }

    [Fact]
    public void CorrectionsNewOwnersAndCapacityChangesAreNotRecharges()
    {
        var capacity = Pod.Flight.ShieldCapacity;
        var hull = Hull(1, capacity * 0.5f);
        Frame(hull);
        Frame(hull);

        // A publication that goes back on the hull's timeline and reads more is a correction.
        hull.Shield = capacity * 0.6f;
        _shownTick -= 5;
        Assert.Equal(0f, Frame(hull)!.Value.Recovery.Y);
        hull.Shield = capacity * 0.61f;
        Assert.Equal(0f, Frame(hull)!.Value.Recovery.Y);

        // So is one that changes hands, onto the pilot's own timeline.
        Frame(hull);
        hull.IsOwn = true;
        hull.Shield = capacity * 0.7f;
        Assert.Equal(0f, Frame(hull)!.Value.Recovery.Y);
        hull.Shield = capacity * 0.71f;
        Assert.Equal(0f, Frame(hull)!.Value.Recovery.Y);

        // And one at another capacity: a lost block that held no emitter keeps it, one that held an emitter not.
        Frame(hull);
        var other = Array.FindIndex(Pod.Types, t => t.Shield <= 0f && t != Pod.Types[0]);
        hull.Blocks = BlockMasks.Set(Pod.FullMask() & ~(1UL << other));
        hull.Shield = capacity * 0.72f;
        Assert.Equal(1f, Frame(hull)!.Value.Recovery.Y);

        // First sight while charging: two rises seen before it shivers.
        var seen = Hull(2, capacity * 0.3f);
        Assert.Equal(0f, Frame(seen)!.Value.Recovery.Y);
        seen.Shield = capacity * 0.31f;
        Assert.Equal(0f, Frame(seen)!.Value.Recovery.Y);
        seen.Shield = capacity * 0.32f;
        Assert.Equal(1f, Frame(seen)!.Value.Recovery.Y);
    }

    [Fact]
    public void ACapacityChangeIsNotARecharge()
    {
        // Two emitters: losing one halves the capacity, and the charge it reads is no rise.
        var design = TwoEmitters.Value;
        HullView Two(float shield, ulong blocks) =>
            new() { Id = 7, Design = 0, Blocks = BlockMasks.Set(blocks), Stats = design.StatsOf([blocks]), Orientation = Quaternion.Identity, Shield = shield };
        HullDesign[] designs = [design];
        var emitter = Array.FindIndex(design.Types, t => t.Shield > 0f);
        var capacity = design.Flight.ShieldCapacity;
        ShieldInstance Draw(HullView hull)
        {
            _publication += 977;
            _shownTick += 1;
            hull.ShownTick = _shownTick;
            _shields.Update([hull], designs, _publication, Dt);
            _shields.Begin(_draws);
            _shields.Add(hull.Id, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
            return _instances[0];
        }

        Draw(Two(capacity * 0.3f, design.FullMask()));
        Draw(Two(capacity * 0.31f, design.FullMask()));
        var damaged = design.FullMask() & ~(1UL << emitter);
        Assert.Equal(0f, Draw(Two(capacity * 0.4f, damaged)).Recovery.Y);
        Assert.Equal(0f, Draw(Two(capacity * 0.41f, damaged)).Recovery.Y);
        Assert.Equal(1f, Draw(Two(capacity * 0.42f, damaged)).Recovery.Y);
    }

    /// <summary>Charges the hull up until it shivers fully, and returns the charge it reached.</summary>
    private float Shivering(ref HullView hull)
    {
        var capacity = Pod.Flight.ShieldCapacity;
        for (var i = 0; i < 3; i++)
        {
            hull.Shield += capacity * 0.01f;
            Frame(hull);
        }

        hull.Shield += capacity * 0.01f;
        Assert.Equal(1f, Frame(hull)!.Value.Recovery.Y);
        return hull.Shield;
    }

    [Fact]
    public void ACorrectionBackWhileShiveringTakesBackDrawnTimeOnlyAndItStillSettles()
    {
        var hull = Hull(1, Pod.Flight.ShieldCapacity * 0.4f);
        Frame(hull);
        Shivering(ref hull);

        // The charge stops rising: it settles frame by frame.
        var levels = new List<float>();
        for (var i = 0; i < 12; i++)
            levels.Add(Frame(hull)!.Value.Recovery.Y);
        Assert.True(levels[^1] < levels[0] && levels[^1] > 0f);

        // A correction takes six ticks back: the shiver is as it was six ticks ago, never more than full, and the
        // charge it reads, higher or not, is no rise of its own.
        _shownTick -= 7;
        hull.Shield += 100f;
        var back = Frame(hull)!.Value.Recovery.Y;
        Assert.Equal(levels[^7], back, 3);
        Assert.InRange(back, 0f, 1f);

        // Drawn again and again, then published on: it settles within the settle time and stays settled.
        var previous = back;
        for (var i = 0; i < 4; i++)
        {
            var level = Repeat(Dt, hull)!.Value.Recovery.Y;
            Assert.True(level <= previous);
            previous = level;
        }

        var frames = 0;
        while (Frame(hull)!.Value.Recovery.Y > 0f)
        {
            frames++;
            Assert.True(frames * Dt <= Look.RecoverySettleSeconds, "it never settles");
        }

        for (var i = 0; i < 20; i++)
            Assert.Equal(0f, Frame(hull)!.Value.Recovery.Y);
    }

    [Fact]
    public void ANewOwnerOrCapacityWhileShiveringDoesNotRestartIt()
    {
        var capacity = Pod.Flight.ShieldCapacity;
        var hull = Hull(1, capacity * 0.4f);
        Frame(hull);
        Shivering(ref hull);
        var settling = Frame(hull)!.Value.Recovery.Y;

        // Taken over, onto the pilot's own timeline, reading more: it goes on settling, not shivering afresh.
        hull.IsOwn = true;
        hull.Shield += capacity * 0.05f;
        var taken = Frame(hull)!.Value.Recovery.Y;
        Assert.True(taken < settling);

        // One rise after is the first of a run; a second is charging again, which a forward correction would also
        // read as, so it shivers.
        hull.Shield += capacity * 0.01f;
        Assert.True(Frame(hull)!.Value.Recovery.Y < taken);
        hull.Shield += capacity * 0.01f;
        Assert.Equal(1f, Frame(hull)!.Value.Recovery.Y);

        // A lost block that held no emitter keeps the capacity and the run: losing the emitter is another matter
        // (ACapacityChangeIsNotARecharge). Here the capacity holds and the next rise goes on shivering.
        var other = Array.FindIndex(Pod.Types, t => t.Shield <= 0f && t != Pod.Types[0]);
        hull.Blocks = BlockMasks.Set(Pod.FullMask() & ~(1UL << other));
        hull.Shield += capacity * 0.01f;
        Assert.Equal(1f, Frame(hull)!.Value.Recovery.Y);
    }

    [Fact]
    public void ACapacityChangeWhileShiveringDoesNotRestartIt()
    {
        var design = TwoEmitters.Value;
        HullDesign[] designs = [design];
        var emitter = Array.FindIndex(design.Types, t => t.Shield > 0f);
        var capacity = design.Flight.ShieldCapacity;
        float Draw(float shield, ulong blocks)
        {
            _publication += 977;
            _shownTick += 1;
            var hull = new HullView { Id = 7, Design = 0, Blocks = BlockMasks.Set(blocks), Stats = design.StatsOf([blocks]), Orientation = Quaternion.Identity, Shield = shield, ShownTick = _shownTick };
            _shields.Update([hull], designs, _publication, Dt);
            _shields.Begin(_draws);
            _shields.Add(hull.Id, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
            return _instances[0].Recovery.Y;
        }

        for (var i = 0; i < 4; i++)
            Draw(capacity * (0.3f + 0.01f * i), design.FullMask());
        Assert.Equal(1f, Draw(capacity * 0.35f, design.FullMask()));
        var settling = Draw(capacity * 0.35f, design.FullMask());
        var damaged = design.FullMask() & ~(1UL << emitter);
        var changed = Draw(capacity * 0.45f, damaged);
        Assert.True(changed < settling);
        Assert.True(Draw(capacity * 0.46f, damaged) < changed);
    }

    private static readonly Lazy<HullDesign> TwoEmitters = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), $"prospect-two-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "two.json");
            string[] types = ["core", "shield", "shield", "thruster", "gyro", "generator"];
            var blocks = string.Join(",", types.Select((t, i) => $"{{ \"at\": [0, 0, {i}], \"type\": \"{t}\" }}"));
            File.WriteAllText(path, $"{{ \"name\": \"Two\", \"class\": \"ship\", \"blockSize\": 2.0, \"color\": [0.5, 0.5, 0.5], \"blocks\": [{blocks}] }}");
            var catalog = BlockCatalog.Load(AppPaths.Content("blocks", "blocks.json"));
            return HullDesign.Load(path, catalog, catalog.Rules);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    });

    [Fact]
    public void EveryShellOfAFullSnapshotIsDrawnWhateverItsShape()
    {
        // Sixty-four hulls whose blocks touch only at their edges: each shell falls back to fit its room, none is
        // left out and none is cut short.
        var cells = new List<string>();
        for (var i = 0; i < 64; i++)
        {
            var type = i switch { 0 => "core", 1 => "shield", 2 => "thruster", 3 => "gyro", 4 => "generator", _ => "hull" };
            cells.Add($"{{ \"at\": [{i % 8}, {i / 8 % 4 * 2 + (i % 8 + i / 32) % 2}, {i / 32}], \"type\": \"{type}\" }}");
        }

        var dir = Path.Combine(Path.GetTempPath(), $"prospect-checker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        HullDesign checker;
        try
        {
            var path = Path.Combine(dir, "checker.json");
            File.WriteAllText(path, $"{{ \"name\": \"C\", \"class\": \"ship\", \"blockSize\": 2.0, \"color\": [0.5, 0.5, 0.5], \"blocks\": [{string.Join(",", cells)}] }}");
            var catalog = BlockCatalog.Load(AppPaths.Content("blocks", "blocks.json"));
            checker = HullDesign.Load(path, catalog, catalog.Rules);
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        var hulls = new HullView[ShieldVisuals.MaxShields];
        for (var i = 0; i < hulls.Length; i++)
            hulls[i] = new HullView { Id = (uint)i + 1, Design = 0, Blocks = BlockMasks.Set(checker.FullMask() & ~(1UL << (5 + i % 50))), Stats = checker.StatsOf([checker.FullMask() & ~(1UL << (5 + i % 50))]), Orientation = Quaternion.Identity, Shield = 1f, ShownTick = 1 };
        _shields.Update(hulls, [checker], 1, Dt);
        _shields.Begin(_draws);
        foreach (var hull in hulls)
            _shields.Add(hull.Id, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        Assert.Equal(hulls.Length, _shields.Draws.Length);
        foreach (var draw in _shields.Draws)
            Assert.InRange(draw.VertexCount, 36, ShieldVisuals.ShellVertices);
    }

    [Fact]
    public void AFullFrameOfShieldsAllocatesNothingOnceWarm()
    {
        var hulls = new HullView[ShieldVisuals.MaxShields];
        for (var i = 0; i < hulls.Length; i++)
            hulls[i] = Hull((uint)i + 1, 700f, blocks: Pod.FullMask() & ~(1UL << (i % Pod.Cells.Length)));
        for (var warm = 0; warm < 3; warm++)
            Frame(hulls);

        var allocation = new AllocationProbe();
        for (var frame = 0; frame < 30; frame++)
        {
            // Every attempt is a frame with a new hit.
            while (allocation.Measuring())
            {
                hulls[frame % hulls.Length].ShieldHit.Serial++;
                hulls[frame % hulls.Length].ShieldHit.Strength = 50;
                Frame(hulls);
            }
        }

        Assert.Equal(0, allocation.Bytes);
        Assert.Equal(ShieldVisuals.MaxShields - CountUnshielded(hulls), _shields.Draws.Length);
    }

    private static int CountUnshielded(HullView[] hulls)
    {
        var count = 0;
        foreach (var hull in hulls)
        {
            Pod.Evaluate(hull.Blocks.Span, out var flight, out _);
            if (flight.ShieldCapacity <= 0f)
                count++;
        }

        return count;
    }

    [Fact]
    public void ABreakAheadOfTheDrawnMomentWaitsForIt()
    {
        Frame(Hull(1));

        // The newest snapshot says the shield broke a tenth of a second after the moment this remote hull is drawn.
        var waits = 0;
        var wait = -0.1f;
        ShieldInstance drawn;
        while ((drawn = Frame(Hull(1, 0f, offline: true, changeAge: wait))!.Value).Params.Z == 0f)
        {
            Assert.Equal(1f, drawn.Params.X);
            wait += Dt;
            waits++;
            Assert.True(waits < 20);
        }

        Assert.InRange(waits, 5, 7);
        Assert.InRange(drawn.Params.Z, 1e-4f, 2 * Dt / Look.CollapseSeconds);
    }

    [Fact]
    public void AnOldChangeIsDrawnAsItsEndAndOneMidWayStartsMidWay()
    {
        // A packet gap: by the time the break is heard of, its collapse is long over.
        Frame(Hull(1));
        Assert.Null(Frame(Hull(1, 0f, offline: true, changeAge: 1.9f)));

        // The same snapshot again changes nothing.
        Assert.Null(Frame(Hull(1, 0f, offline: true, changeAge: 1.9f)));

        // Half-way through a collapse when first heard of.
        Frame(Hull(2));
        var half = Look.CollapseSeconds * 0.5f;
        var drawn = Frame(Hull(1, 0f, offline: true, changeAge: 1.9f), Hull(2, 0f, offline: true, changeAge: half));
        Assert.Equal(0.5f, _instances[0].Params.Z, 2);

        // A restart heard of late shows fully on; one heard of half-way fades on from half-way.
        Frame(Hull(1, 0f, offline: true, changeAge: 1.9f), Hull(2, 0f, offline: true, changeAge: 1.9f));
        Frame(Hull(1, 400f, changeAge: 1.9f), Hull(2, 400f, changeAge: Look.FadeInSeconds * 0.5f));
        Assert.Equal(1f, _instances[0].Params.X);
        Assert.Equal(0.5f, _instances[1].Params.X, 2);
    }

    [Fact]
    public void LosingTheLastEmitterIsTimedLikeABreak()
    {
        Frame(Hull(1));
        var lost = Pod.FullMask() & ~(1UL << Emitter);
        Assert.Equal(0f, Frame(Hull(1, 0f, blocks: lost, changeAge: -0.05f))!.Value.Params.Z);
        for (var i = 0; i < 3; i++)
            Frame(Hull(1, 0f, blocks: lost, changeAge: -0.05f + (i + 1) * Dt));
        Assert.True(Frame(Hull(1, 0f, blocks: lost, changeAge: 4 * Dt))!.Value.Params.Z > 0f);

        // Heard of too late to show, nothing collapses.
        Frame(Hull(2));
        Frame(Hull(2, 0f, blocks: lost, changeAge: 1.9f));
        Assert.Equal(0, _shields.Draws.Length);
    }

    [Fact]
    public void ARestartAheadOfTheDrawnMomentWaitsAndACorrectedBreakFadesBack()
    {
        Frame(Hull(1, 0f, offline: true));
        for (var i = 0; i < 3; i++)
            Assert.Null(Frame(Hull(1, 400f, changeAge: -0.1f + i * Dt)));
        for (var i = 0; i < 6; i++)
            Frame(Hull(1, 400f, changeAge: -0.05f + i * Dt));
        Assert.True(Frame(Hull(1, 400f, changeAge: 0.2f))!.Value.Params.X > 0f);

        // The prediction broke the shield, then the server said it held, with its last change long ago: the
        // collapse gives way to a fade back in, not a snap.
        Frame(Hull(3));
        Assert.True(Frame(Hull(3, 0f, offline: true))!.Value.Params.Z > 0f);
        Frame(Hull(3, 1500f, changeAge: 1.9f));
        var back = Frame(Hull(3, 1500f, changeAge: 1.9f))!.Value;
        Assert.InRange(back.Params.X, 1e-4f, 2 * Dt / Look.FadeInSeconds);
        Assert.Equal(0f, back.Params.Z);

        // The fade runs over drawn simulation time: through publications that all read one saturated age, through
        // frames that draw one of them again, and through a stall, whose frame is cut short but whose time is not.
        var saturated = Hull(3, 1500f, changeAge: ShieldHit.MaxAgeTicks * HullSim.Dt);
        for (var i = 0; i < 10; i++)
            back = Frame(saturated)!.Value;
        Assert.Equal(11 * Dt / Look.FadeInSeconds, back.Params.X, 3);
        for (var i = 0; i < 2; i++)
            back = Repeat(Dt * 0.25f, saturated)!.Value;
        Assert.Equal(11.5f * Dt / Look.FadeInSeconds, back.Params.X, 3);
        back = Publish(0.4f, 0.1f, saturated)!.Value;
        Assert.Equal((11 * Dt + 0.4f) / Look.FadeInSeconds, back.Params.X, 3);
        Assert.Equal(0f, back.Params.Z);
        Assert.Equal(1f, Publish(1f, 0.1f, saturated)!.Value.Params.X);
    }

    [Fact]
    public void AChangeTakenBackBeforeTheDrawnMomentReachesItNeverShows()
    {
        Frame(Hull(1));
        Frame(Hull(1, 0f, offline: true, changeAge: -0.2f));
        for (var i = 0; i < 30; i++)
        {
            var drawn = Frame(Hull(1, 1500f, changeAge: 1.9f))!.Value;
            Assert.Equal(1f, drawn.Params.X);
            Assert.Equal(0f, drawn.Params.Z);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void ACorrectionFadeTakesBackLocalTimeOnTheNextPublication(int repeatedFrames)
    {
        Frame(Hull(1));
        Frame(Hull(1, 0f, offline: true));
        var corrected = Hull(1, changeAge: 1.9f);
        Assert.Null(Frame(corrected));

        // Render ahead of the publication, including far enough to finish the fade locally.
        for (var i = 0; i < repeatedFrames; i++)
            Repeat(0.1f, corrected);
        var drawn = Frame(corrected)!.Value;
        Assert.Equal(Dt / Look.FadeInSeconds, drawn.Params.X, 4);
        Assert.Equal(0f, drawn.Params.Z);
        Assert.Equal(1f, Publish(Look.FadeInSeconds, 0.1f, corrected)!.Value.Params.X);
    }

    [Fact]
    public void AStallPastAPendingBreakTakesTheNewPublishedAgeAndDoesNotReplayIt()
    {
        Frame(Hull(1));
        Assert.Equal(1f, Frame(Hull(1, 0f, offline: true, changeAge: -0.05f))!.Value.Params.X);

        // A second without frames; the next publication says the break is 0.95 s old, past the 0.7 s collapse.
        Assert.Null(Publish(1f, 0.1f, Hull(1, 0f, offline: true, changeAge: 0.95f)));
        Assert.Null(Frame(Hull(1, 0f, offline: true, changeAge: 0.95f + Dt)));
    }

    [Fact]
    public void ARunningCollapseFollowsTheRenderedClockForwardAndBack()
    {
        Frame(Hull(1));
        Frame(Hull(1, 0f, offline: true, changeAge: 0.1f));
        var forward = Frame(Hull(1, 0f, offline: true, changeAge: 0.5f))!.Value;
        Assert.Equal(0.5f / Look.CollapseSeconds, forward.Params.Z, 3);
        var back = Frame(Hull(1, 0f, offline: true, changeAge: 0.2f))!.Value;
        Assert.Equal(0.2f / Look.CollapseSeconds, back.Params.Z, 3);

        // Corrected back past the break itself: the drawn moment is before it again, so the shield is up.
        var before = Frame(Hull(1, 0f, offline: true, changeAge: -0.1f))!.Value;
        Assert.Equal(1f, before.Params.X);
        Assert.Equal(0f, before.Params.Z);

        // And a pending break pulled forward starts where the clock says.
        var pulled = Frame(Hull(1, 0f, offline: true, changeAge: 0.05f))!.Value;
        Assert.Equal(0.05f / Look.CollapseSeconds, pulled.Params.Z, 3);
    }

    [Fact]
    public void RepeatedPublicationsMoveOnByTheLocalClock()
    {
        Frame(Hull(1));
        var broken = Hull(1, 0f, offline: true, changeAge: -0.05f);
        Assert.Equal(0f, Frame(broken)!.Value.Params.Z);
        var frames = 1;
        while (Repeat(Dt, broken)!.Value.Params.Z == 0f)
        {
            frames++;
            Assert.True(frames < 10);
        }

        Assert.InRange(frames, 2, 4);

        // The same publication again and again: the collapse still runs to its end.
        for (var i = 0; i < 60 && Repeat(Dt, broken) is not null; i++)
            Assert.True(i < 50);
    }

    [Fact]
    public void ANewPublicationSetsTheAgeEvenWhenItReadsTheSameAndOnlyARepeatedOneMovesOn()
    {
        Frame(Hull(1));
        var broken = Hull(1, 0f, offline: true, changeAge: 0.1f);
        Assert.Equal(0.1f / Look.CollapseSeconds, Frame(broken)!.Value.Params.Z, 3);

        // The same publication, drawn six more times: the frames move the collapse on by themselves.
        var drawn = default(ShieldInstance);
        for (var i = 0; i < 6; i++)
            drawn = Repeat(Dt, broken)!.Value;
        Assert.Equal((0.1f + 6 * Dt) / Look.CollapseSeconds, drawn.Params.Z, 3);

        // New publications that read the very same age, as a clock corrected back by what had passed does: the
        // collapse is where each one says, not where the frames had taken it.
        for (var i = 0; i < 6; i++)
            Assert.Equal(0.1f / Look.CollapseSeconds, Frame(broken)!.Value.Params.Z, 3);

        // The same for a hit: its ripple stays at the age every new publication repeats.
        var hull = Hull(2);
        Frame(hull);
        hull.ShieldHit = new ShieldHit { Serial = 1, Strength = 200, X = 50 };
        hull.ShieldHitAge = 0.2f;
        Assert.Equal(0.2f, RippleAge(Frame(hull)!.Value, 50), 3);
        for (var i = 0; i < 3; i++)
            drawn = Repeat(Dt, hull)!.Value;
        Assert.Equal(0.2f + 3 * Dt, RippleAge(drawn, 50), 3);
        for (var i = 0; i < 6; i++)
            Assert.Equal(0.2f, RippleAge(Frame(hull)!.Value, 50), 3);
    }

    [Fact]
    public void ABreakPublishedAtTheAgeARestartWasPublishedAtStartsAtThatAge()
    {
        Assert.Null(Frame(Hull(1, 0f, offline: true)));
        var restart = Hull(1, 400f, changeAge: 0.1f);
        Assert.Equal(0.1f / Look.FadeInSeconds, Frame(restart)!.Value.Params.X, 3);
        var drawn = default(ShieldInstance);
        for (var i = 0; i < 12; i++)
            drawn = Repeat(Dt, restart)!.Value;
        Assert.Equal(0.3f / Look.FadeInSeconds, drawn.Params.X, 3);

        // The break's own age happens to read as the restart's did. It is a new publication all the same.
        var broke = Frame(Hull(1, 0f, offline: true, changeAge: 0.1f))!.Value;
        Assert.Equal(0.1f / Look.CollapseSeconds, broke.Params.Z, 3);
        Assert.Equal(0f, broke.Params.X);
    }

    [Fact]
    public void AnAgeCorrectedBackAcrossTheEndOfACollapseShowsItPartWayAgain()
    {
        Frame(Hull(1));
        Assert.Equal(0.69f / Look.CollapseSeconds, Frame(Hull(1, 0f, offline: true, changeAge: 0.69f))!.Value.Params.Z, 3);
        Assert.Null(Frame(Hull(1, 0f, offline: true, changeAge: 0.71f)));
        var back = Frame(Hull(1, 0f, offline: true, changeAge: 0.68f))!.Value;
        Assert.Equal(0.68f / Look.CollapseSeconds, back.Params.Z, 3);
        Assert.Equal(0f, back.Params.X);

        // On over its end again, back before the break itself, and on once more.
        Assert.Null(Frame(Hull(1, 0f, offline: true, changeAge: 0.72f)));
        var before = Frame(Hull(1, 0f, offline: true, changeAge: -0.05f))!.Value;
        Assert.Equal(1f, before.Params.X);
        Assert.Equal(0f, before.Params.Z);
        Assert.Null(Frame(Hull(1, 0f, offline: true, changeAge: 1.9f)));
    }

    [Fact]
    public void AnAgeCorrectedBackAcrossTheEndOfARestartShowsItPartWayAgain()
    {
        Assert.Null(Frame(Hull(1, 0f, offline: true)));
        var fade = Look.FadeInSeconds;
        Assert.Equal((fade - 0.01f) / fade, Frame(Hull(1, 400f, changeAge: fade - 0.01f))!.Value.Params.X, 3);
        Assert.Equal(1f, Frame(Hull(1, 400f, changeAge: fade + 0.01f))!.Value.Params.X);
        Assert.Equal((fade - 0.1f) / fade, Frame(Hull(1, 400f, changeAge: fade - 0.1f))!.Value.Params.X, 3);

        // Back before the restart: not there yet. Then long after it: fully on.
        Assert.Null(Frame(Hull(1, 400f, changeAge: -0.05f)));
        Assert.Equal(1f, Frame(Hull(1, 400f, changeAge: 1.9f))!.Value.Params.X);
    }

    [Fact]
    public void ThePredictedAgeSteppingBackTwoTicksIsFollowedAcrossTheEndOfAChange()
    {
        // The pilot's own age is whole ticks, and a correction can take it from one past an effect's end to one
        // before it.
        var collapse = (int)MathF.Ceiling(Look.CollapseSeconds / Dt);
        Frame(Hull(1));
        Assert.Null(Frame(Hull(1, 0f, offline: true, changeAge: (collapse + 1) * Dt)));
        Assert.Equal((collapse - 1) * Dt / Look.CollapseSeconds, Frame(Hull(1, 0f, offline: true, changeAge: (collapse - 1) * Dt))!.Value.Params.Z, 3);
        Assert.Null(Frame(Hull(1, 0f, offline: true, changeAge: (collapse + 60) * Dt)));

        var fade = (int)MathF.Ceiling(Look.FadeInSeconds / Dt);
        Assert.Equal(1f, Frame(Hull(1, 400f, changeAge: (fade + 1) * Dt))!.Value.Params.X);
        Assert.Equal((fade - 1) * Dt / Look.FadeInSeconds, Frame(Hull(1, 400f, changeAge: (fade - 1) * Dt))!.Value.Params.X, 3);
    }

    [Fact]
    public void WhatWasNeverSeenChangingShowsNoChangeWhateverTheClockDoes()
    {
        // First sight of a shield already down, its age corrected back below a collapse's length: no collapse.
        Assert.Null(Frame(Hull(7, 0f, offline: true, changeAge: 0.71f)));
        Assert.Null(Frame(Hull(7, 0f, offline: true, changeAge: 0.3f)));
        _shields.Clear();
        Assert.Null(Frame(Hull(7, 0f, offline: true, changeAge: 0.3f)));
        Assert.Null(Frame(Hull(7, 0f, offline: true, changeAge: 0.2f)));

        // First sight of one already up: no fade.
        Assert.Equal(1f, Frame(Hull(8, changeAge: 1.2f))!.Value.Params.X);
        Assert.Equal(1f, Frame(Hull(8, changeAge: 0.3f))!.Value.Params.X);
        Assert.Equal(1f, Frame(Hull(8, changeAge: -0.1f))!.Value.Params.X);
    }

    [Fact]
    public void AWaitingBreakFollowsTheClockAcrossItsEndAndOneTakenBackIsGoneForGood()
    {
        Frame(Hull(1));
        var waiting = Frame(Hull(1, 0f, offline: true, changeAge: -0.1f))!.Value;
        Assert.Equal(1f, waiting.Params.X);
        Assert.Equal(0f, waiting.Params.Z);

        // The clock jumps past the whole collapse, and back to before the break: the shield is drawn up again.
        Assert.Null(Frame(Hull(1, 0f, offline: true, changeAge: 0.9f)));
        waiting = Frame(Hull(1, 0f, offline: true, changeAge: -0.05f))!.Value;
        Assert.Equal(1f, waiting.Params.X);
        Assert.Equal(0f, waiting.Params.Z);

        // Taken back while it waits: it never happened, and no age afterwards makes it a change.
        foreach (var age in (float[])[1.9f, 0.3f, -0.1f, 0.71f, 0.69f])
        {
            var drawn = Frame(Hull(1, 1500f, changeAge: age))!.Value;
            Assert.Equal(1f, drawn.Params.X);
            Assert.Equal(0f, drawn.Params.Z);
        }
    }

    [Fact]
    public void ARippleIsPutBackOnThePublishedClockAfterAStall()
    {
        var hull = Hull(1);
        Frame(hull);
        hull.ShieldHit = new ShieldHit { Serial = 1, Strength = 200, X = 50 };
        hull.ShieldHitAge = -0.05f;
        Assert.InRange(RippleAge(Frame(hull)!.Value, 50), -0.05f, -0.04f);

        hull.ShieldHitAge = 0.6f;
        Assert.Equal(0.6f, RippleAge(Publish(1f, 0.1f, hull)!.Value, 50), 3);

        // Past its life on the published clock, it is gone.
        hull.ShieldHitAge = Look.RippleSeconds + 0.1f;
        Assert.Equal(0, RippleCount(Frame(hull)!.Value));
    }

    [Fact]
    public void AStallEndsAnOlderRippleEvenWhenADifferentHitIsTheNewest()
    {
        var hull = Hull(1);
        Frame(hull);
        hull.ShieldHit = new ShieldHit { Serial = 1, Strength = 200, X = 50 };
        hull.ShieldHitAge = 0.2f;
        Assert.Equal(0.2f, RippleAge(Frame(hull)!.Value, 50), 3);

        // Two seconds without frames. The next publication tells of another hit, so nothing in it says how old
        // the first one is: the two seconds of simulation it skipped do. The frame itself is capped at 0.1 s.
        hull.ShieldHit = new ShieldHit { Serial = 2, Strength = 200, X = -50 };
        hull.ShieldHitAge = 0.05f;
        var drawn = Publish(2f, 0.1f, hull)!.Value;
        Assert.Equal(1, RippleCount(drawn));
        Assert.Equal(0.05f, RippleAge(drawn, -50), 3);
    }

    [Fact]
    public void ALiveRippleTakesItsCorrectedAgeBeforeItCanExpireInTheSameUpdate()
    {
        var hull = Hull(1);
        hull.IsOwn = true;
        Frame(hull);
        hull.ShieldHit = new ShieldHit { Serial = 1, Strength = 200, X = 50 };
        hull.ShieldHitAge = Look.RippleSeconds - Dt;
        Assert.Equal(hull.ShieldHitAge, RippleAge(Frame(hull)!.Value, 50), 4);

        // Prediction advances one input, but reconciliation makes the still-live hit younger.
        hull.ShieldHitAge -= Dt;
        var drawn = Frame(hull)!.Value;
        Assert.Equal(1, RippleCount(drawn));
        Assert.Equal(hull.ShieldHitAge, RippleAge(drawn, 50), 4);

        // A genuinely expired publication still removes it; later corrections do not resurrect that slot.
        hull.ShieldHitAge = Look.RippleSeconds;
        Assert.Equal(0, RippleCount(Frame(hull)!.Value));
        hull.ShieldHitAge -= Dt;
        Assert.Equal(0, RippleCount(Frame(hull)!.Value));
    }

    [Fact]
    public void AnOlderRippleAgesByTheSimulationTimeDrawnAndNotByTheFrameOrTheNewestHit()
    {
        var hull = Hull(1);
        Frame(hull);
        hull.ShieldHit = new ShieldHit { Serial = 1, Strength = 200, X = 50 };
        hull.ShieldHitAge = 0.2f;
        Frame(hull);

        // Half a second passes in one capped frame, and the newest hit is another: the older ripple is half a
        // second on, neither a tenth nor whatever the new hit's age would suggest.
        hull.ShieldHit = new ShieldHit { Serial = 2, Strength = 200, X = -50 };
        hull.ShieldHitAge = 0.05f;
        var drawn = Publish(0.5f, 0.1f, hull)!.Value;
        Assert.Equal(2, RippleCount(drawn));
        Assert.Equal(0.7f, RippleAge(drawn, 50), 3);
        Assert.Equal(0.05f, RippleAge(drawn, -50), 3);

        // Frames that draw this publication again run ahead of it; the next publication, with a third hit, is one
        // tick on, and that is all the older ripples have aged since the last one.
        for (var i = 0; i < 3; i++)
            drawn = Repeat(Dt, hull)!.Value;
        Assert.Equal(0.7f + 3 * Dt, RippleAge(drawn, 50), 3);
        hull.ShieldHit = new ShieldHit { Serial = 3, Strength = 200, X = 20 };
        hull.ShieldHitAge = 0f;
        drawn = Frame(hull)!.Value;
        Assert.Equal(3, RippleCount(drawn));
        Assert.Equal(0.7f + Dt, RippleAge(drawn, 50), 3);
        Assert.Equal(0.05f + Dt, RippleAge(drawn, -50), 3);
        Assert.Equal(0f, RippleAge(drawn, 20), 3);
    }

    [Fact]
    public void ThePredictedHitAgeSteppingBackMovesOnlyThatHitsRipple()
    {
        var hull = Hull(1);
        Frame(hull);
        hull.ShieldHit = new ShieldHit { Serial = 6, Strength = 200, X = 50 };
        hull.ShieldHitAge = 0.1f;
        Frame(hull);
        hull.ShieldHit = new ShieldHit { Serial = 7, Strength = 200, X = -50 };
        hull.ShieldHitAge = 14 * Dt;
        var drawn = Frame(hull)!.Value;
        Assert.Equal(14 * Dt, RippleAge(drawn, -50), 3);
        Assert.Equal(0.1f + Dt, RippleAge(drawn, 50), 3);

        // The prediction is corrected: one tick on, the newest hit is twelve ticks old, not fifteen. The older
        // ripple is no part of that correction; it is one tick older.
        hull.ShieldHitAge = 12 * Dt;
        drawn = Frame(hull)!.Value;
        Assert.Equal(12 * Dt, RippleAge(drawn, -50), 3);
        Assert.Equal(0.1f + 2 * Dt, RippleAge(drawn, 50), 3);
    }

    [Fact]
    public void EveryHullAgesItsRipplesOnItsOwnTimeline()
    {
        // The pilot's hull counts the inputs predicted; a remote hull is drawn at a delayed server tick, which a
        // clock correction moves by itself. One publication, two different spans of drawn time.
        var own = Hull(1);
        var remote = Hull(2);
        own.ShownTick = 500;
        remote.ShownTick = 90000.25;
        _shields.Update([own, remote], Content.Designs, 1, Dt);
        own.ShieldHit = remote.ShieldHit = new ShieldHit { Serial = 1, Strength = 200, X = 50 };
        own.ShieldHitAge = remote.ShieldHitAge = 0.2f;
        own.ShownTick += 1;
        remote.ShownTick += 1;
        _shields.Update([own, remote], Content.Designs, 2, Dt);

        own.ShieldHit = remote.ShieldHit = new ShieldHit { Serial = 2, Strength = 200, X = -50 };
        own.ShieldHitAge = remote.ShieldHitAge = 0f;
        own.ShownTick += 1;
        remote.ShownTick += 30;
        _shields.Update([own, remote], Content.Designs, 3, Dt);
        _shields.Begin(_draws);
        _shields.Add(1, Vector3.Zero, Quaternion.Identity, 1f, 2f, _instances, _vertices, _indices);
        _shields.Add(2, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        Assert.Equal(0.2f + Dt, RippleAge(_instances[0], 50), 3);
        Assert.Equal(0.2f + 30 * Dt, RippleAge(_instances[1], 50), 3);

        // The remote clock is corrected back: its ripples go back with it, the pilot's do not.
        own.ShownTick += 1;
        remote.ShownTick -= 12;
        own.ShieldHitAge = Dt;
        remote.ShieldHitAge = -12 * Dt;
        _shields.Update([own, remote], Content.Designs, 4, Dt);
        _shields.Begin(_draws);
        _shields.Add(1, Vector3.Zero, Quaternion.Identity, 1f, 2f, _instances, _vertices, _indices);
        _shields.Add(2, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        Assert.Equal(0.2f + 2 * Dt, RippleAge(_instances[0], 50), 3);
        Assert.Equal(Dt, RippleAge(_instances[0], -50), 3);
        Assert.Equal(0.2f + 18 * Dt, RippleAge(_instances[1], 50), 3);
        Assert.Equal(-12 * Dt, RippleAge(_instances[1], -50), 3);
    }

    [Fact]
    public void AHullThatChangesHandsKeepsItsRipplesAcrossTheChangeOfTimeline()
    {
        // Watched, the hull is shown at a delayed server tick; flown, at the pilot's count of inputs. The two
        // numbers are far apart and mean nothing to each other.
        var hull = Hull(1);
        hull.ShownTick = 90000.25;
        _shields.Update([hull], Content.Designs, 1, Dt);
        hull.ShieldHit = new ShieldHit { Serial = 1, Strength = 200, X = 50 };
        hull.ShieldHitAge = 0.2f;
        hull.ShownTick += 1;
        _shields.Update([hull], Content.Designs, 2, Dt);

        // The pilot takes it, and it is hit again in the same publication: the older ripple moves on by the frame.
        hull.IsOwn = true;
        hull.ShownTick = 500;
        hull.ShieldHit = new ShieldHit { Serial = 2, Strength = 200, X = -50 };
        hull.ShieldHitAge = 0f;
        _shields.Update([hull], Content.Designs, 3, Dt);
        _shields.Begin(_draws);
        _shields.Add(1, Vector3.Zero, Quaternion.Identity, 1f, 2f, _instances, _vertices, _indices);
        Assert.Equal(0.2f + Dt, RippleAge(_instances[0], 50), 3);
        Assert.Equal(0f, RippleAge(_instances[0], -50), 3);

        // From there it follows the new timeline, and the same on the way back.
        hull.ShownTick += 3;
        hull.ShieldHitAge = 3 * Dt;
        _shields.Update([hull], Content.Designs, 4, Dt);
        hull.IsOwn = false;
        hull.ShownTick = 90010;
        hull.ShieldHit = new ShieldHit { Serial = 3, Strength = 200, X = 20 };
        hull.ShieldHitAge = -0.05f;
        _shields.Update([hull], Content.Designs, 5, Dt);
        _shields.Begin(_draws);
        _shields.Add(1, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices, _indices);
        Assert.Equal(0.2f + 5 * Dt, RippleAge(_instances[0], 50), 3);
        Assert.Equal(4 * Dt, RippleAge(_instances[0], -50), 3);
        Assert.Equal(-0.05f, RippleAge(_instances[0], 20), 3);
    }

    [Fact]
    public void AgesThatStopCountingHoldEveryEffectAtItsEnd()
    {
        // A hit on one hull and a break on another, then a publication every tick: the ages count up to where
        // the simulation stops counting and stay there, one new publication after another reading the same.
        var struck = Hull(1);
        Frame(struck, Hull(2));
        struck.ShieldHit = new ShieldHit { Serial = 1, Strength = 200, X = 50 };
        for (var tick = 0; tick <= ShieldHit.MaxAgeTicks + 40; tick++)
        {
            var age = Math.Min(tick, (int)ShieldHit.MaxAgeTicks) * HullSim.Dt;
            struck.ShieldHitAge = age;
            var drawn = Frame(struck, Hull(2, 0f, offline: true, changeAge: age))!.Value;
            Assert.Equal(1f, drawn.Params.X);
            if (age < Look.RippleSeconds)
                Assert.Equal(age, RippleAge(drawn, 50), 4);
            else
                Assert.Equal(0, RippleCount(drawn));

            Assert.Equal(age < Look.CollapseSeconds ? 2 : 1, _shields.Draws.Length);
            if (age < Look.CollapseSeconds)
                Assert.Equal(Math.Max(age / Look.CollapseSeconds, 1e-3f), _instances[1].Params.Z, 4);
        }
    }

    [Fact]
    public void EffectsEndBeforeTheSimulationStopsCountingTheirAge()
    {
        // A remote hull is drawn six ticks in the past; an event at the saturated age must still read as over.
        var oldest = ShieldHit.MaxAgeTicks * HullSim.Dt - 6 * HullSim.Dt;
        Assert.True(ShieldLook.MaxEffectSeconds < oldest - 0.25f);
        Assert.True(Look.CollapseSeconds <= ShieldLook.MaxEffectSeconds);
        Assert.True(Look.FadeInSeconds <= ShieldLook.MaxEffectSeconds);
        Assert.True(Look.RippleSeconds <= ShieldLook.MaxEffectSeconds);
    }

    [Theory]
    [InlineData("\"collapseSeconds\": 0.7", "\"collapseSeconds\": 1.6")]
    [InlineData("\"rippleSeconds\": 0.85", "\"rippleSeconds\": 0")]
    [InlineData("\"unstableFull\": 0.08", "\"unstableFull\": 0.9")]
    [InlineData("\"calmColor\": [0.32, 0.36, 0.35]", "\"calmColor\": [0.16, 0.34]")]
    [InlineData("\"refractionPixels\": 1.2", "\"refractionPixels\": 9")]
    [InlineData("\"rippleWidth\": 0.45", "\"rippleWidth\": -1")]
    [InlineData("\"impactRadius\": 1.2", "\"impactRadius\": 0")]
    [InlineData("\"impactSeconds\": 0.45", "\"impactSeconds\": 1.2")]
    [InlineData("\"contactStrength\": 0.65", "\"contactStrength\": 2")]
    [InlineData("\"recoveryWobble\": 0.008", "\"recoveryWobble\": 0.2")]
    [InlineData("\"recoveryHz\": 2.5", "\"recoveryHz\": 0")]
    [InlineData("\"recoveryShimmer\": 0.3", "\"recoveryShimmer\": 1.5")]
    [InlineData("\"recoveryShimmer\": 0.3", "\"recoveryShimmer\": -0.1")]
    [InlineData("\"recoverySettleSeconds\": 0.6", "\"recoverySettleSeconds\": 2")]
    public void TheLookRefusesValuesOutOfRange(string from, string to)
    {
        var source = File.ReadAllText(AppPaths.Content("visuals", "shields.json"));
        Assert.Contains(from, source);
        var path = Path.Combine(Path.GetTempPath(), $"prospect-shields-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, source.Replace(from, to));
        try
        {
            Assert.Throws<InvalidDataException>(() => ShieldLook.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// The seams between the shield's C# side and its shaders, which no test runs on a GPU: the shell decodes its hull's
/// material exactly as the hull's mesh does and asks the shared cut, and its instance fills the attribute
/// locations its vertex shader reads.
/// </summary>
public sealed class ShieldShaderSeamTests
{
    private static string Shader(string name) => File.ReadAllText(AppPaths.Asset(Path.Combine("shaders", name)));

    [Fact]
    public void TheShellIsCutAsItsHullIsAndItsInstanceMatchesItsShader()
    {
        var mesh = Shader("mesh.vert");
        var shield = Shader("shield.vert");
        Assert.Contains("int slot = int((tint.a + 0.5) * 0.25);", mesh);
        Assert.Contains("int slot = int((params.w + 0.5) * 0.25);", shield);
        Assert.Contains("cut = slot - 1;", mesh);
        Assert.Contains("cut = slot - 1;", shield);
        Assert.Contains("#include \"hull_clip.glsl\"", Shader("shield.frag"));
        Assert.Contains("if (hullCut(cut, own != 0, worldPosition))", Shader("shield.frag"));

        // The recovery bands' strength rides in the collapse colour's spare lane, and the shader scales them by it.
        var look = ShieldLook.Load();
        Assert.Equal(look.RecoveryShimmer, look.Constants.Collapse.W);
        Assert.Contains("float shiver = recovering.y * bands * shieldLook.collapse.a;", Shader("shield.frag"));

        // Two vertex attributes, then one location per four floats of the instance.
        var locations = System.Runtime.CompilerServices.Unsafe.SizeOf<ShieldInstance>() / 16;
        Assert.Equal(10, locations);
        Assert.Contains("layout(location = 10) in vec4 strengths;", shield);
        Assert.Contains($"layout(location = {2 + locations - 1}) in vec4 recovery;", shield);
    }
}
