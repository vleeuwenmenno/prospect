using System.Numerics;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The shield's shell, built from whatever blocks a hull has: closed, a margin off the blocks all round, wound to
/// face out, and within its vertex budget for every design a live hull can have.
/// </summary>
public sealed class ShieldShellTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static Vector3 P(in MeshVertex v) => new(v.Position.X, v.Position.Y, v.Position.Z);

    private static Vector3 N(in MeshVertex v) => new(v.Normal.X, v.Normal.Y, v.Normal.Z);

    private static MeshVertex[] Shell(HullDesign design, ulong alive)
    {
        var vertices = new MeshVertex[ShieldShell.MaxVertices];
        var count = ShieldShell.Build(design, alive, vertices);
        Assert.Equal(0, count % 3);
        return vertices[..count];
    }

    /// <summary>How far a point is outside a block's cube; zero inside it.</summary>
    private static float OutsideBlock(HullDesign design, int block, Vector3 point)
    {
        var d = Vector3.Abs(point - design.BlockOffsets[block]) - new Vector3(design.BlockSize * 0.5f);
        return Vector3.Max(d, Vector3.Zero).Length();
    }

    /// <summary>How many triangles a ray from <paramref name="origin"/> along <paramref name="direction"/> crosses.</summary>
    private static int Crossings(ReadOnlySpan<MeshVertex> shell, Vector3 origin, Vector3 direction)
    {
        var crossings = 0;
        for (var i = 0; i < shell.Length; i += 3)
        {
            var a = P(shell[i]);
            var e1 = P(shell[i + 1]) - a;
            var e2 = P(shell[i + 2]) - a;
            var p = Vector3.Cross(direction, e2);
            var det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f)
                continue;
            var t0 = origin - a;
            var u = Vector3.Dot(t0, p) / det;
            if (u < 0 || u > 1)
                continue;
            var q = Vector3.Cross(t0, e1);
            var v = Vector3.Dot(direction, q) / det;
            if (v < 0 || u + v > 1)
                continue;
            if (Vector3.Dot(e2, q) / det > 0)
                crossings++;
        }

        return crossings;
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
    public void EveryDesignWholeOrDamagedGetsAClosedShellAMarginOffItsBlocks(int index)
    {
        var design = Content.Designs[index];
        var random = new Random(index * 31 + 7);
        var masks = new List<ulong> { design.FullMask };
        for (var i = 0; i < 6; i++)
            masks.Add(design.FullMask & ((ulong)random.NextInt64() | (ulong)random.NextInt64() << 1));

        foreach (var alive in masks)
        {
            if (alive == 0)
                continue;
            var shell = Shell(design, alive);
            var margin = design.BlockSize * ShieldShell.Margin;
            for (var i = 0; i < shell.Length; i += 3)
            {
                // Wound counter-clockwise from outside, like the hull's own mesh, so back faces are culled.
                var a = P(shell[i]);
                var winding = Vector3.Cross(P(shell[i + 1]) - a, P(shell[i + 2]) - a);
                Assert.True(Vector3.Dot(winding, N(shell[i])) > 0f);

                // Every corner stands at least the margin off every block it has, and close to the nearest.
                for (var k = 0; k < 3; k++)
                {
                    var nearest = float.MaxValue;
                    for (var b = 0; b < design.BlockOffsets.Length; b++)
                    {
                        if ((alive & 1UL << b) == 0)
                            continue;
                        var outside = OutsideBlock(design, b, P(shell[i + k]));
                        Assert.True(outside >= margin - 1e-3f, $"a shell corner is {outside} m off a block {b} at {P(shell[i + k])} normal {N(shell[i])} block {design.BlockOffsets[b]} size {design.BlockSize} mask {alive:X}");
                        nearest = Math.Min(nearest, outside);
                    }

                    Assert.True(nearest <= margin * MathF.Sqrt(3f) + 1e-3f, $"a shell corner is {nearest} m from the hull");
                }
            }

            // Closed: a ray out of any block it has crosses the shell an odd number of times, whichever way.
            for (var b = 0; b < design.BlockOffsets.Length; b++)
            {
                if ((alive & 1UL << b) == 0)
                    continue;
                for (var r = 0; r < 24; r++)
                {
                    var direction = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
                    var crossings = Crossings(shell, design.BlockOffsets[b], direction);
                    Assert.True(crossings % 2 == 1, $"{crossings} crossings from block {b} {design.Cells[b]} along {direction} with blocks {alive:X}");
                }
            }
        }
    }

    [Fact]
    public void OneBlockIsACubeTheMarginBiggerAllRound()
    {
        var design = Content.Pod;
        var shell = Shell(design, 1UL);
        Assert.Equal(36, shell.Length);
        var reach = design.BlockSize * (0.5f + ShieldShell.Margin);
        foreach (var vertex in shell)
        {
            var local = Vector3.Abs(P(vertex) - design.BlockOffsets[0]);
            Assert.Equal(reach, Math.Max(local.X, Math.Max(local.Y, local.Z)), 4);
            Assert.Equal(reach, local.X, 4);
            Assert.Equal(reach, local.Y, 4);
            Assert.Equal(reach, local.Z, 4);
        }
    }

    [Fact]
    public void LosingABlockOpensNoHoleAndShrinksTheShell()
    {
        var design = Content.Pod;
        var whole = Shell(design, design.FullMask);
        var emitter = Array.FindIndex(design.Types, t => t.Shield > 0);
        var damaged = Shell(design, design.FullMask & ~(1UL << emitter));
        Assert.True(damaged.Length < whole.Length);

        // Where the lost block was is now outside the shell: a ray from there crosses it an even number of times.
        var direction = Vector3.Normalize(new Vector3(1f, 0.013f, 0.007f));
        Assert.Equal(0, Crossings(damaged, design.BlockOffsets[emitter], direction) % 2);
        Assert.Equal(1, Crossings(whole, design.BlockOffsets[emitter], direction) % 2);
    }

    [Theory]
    [InlineData("cube")]
    [InlineData("line")]
    [InlineData("checker")]
    public void SixtyFourBlocksFitTheBudget(string shape)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"prospect-shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var blocks = new List<string>();
            for (var i = 0; i < HullDesign.MaxBlocks; i++)
            {
                var at = shape switch
                {
                    "cube" => (i % 4, i / 4 % 4, i / 16),
                    "line" => (0, 0, i),
                    // Blocks touching only at edges: every other cell of a 8 x 8 x 2 slab.
                    _ => (i % 8, i / 8 % 4 * 2 + (i % 8 + i / 32) % 2, i / 32),
                };
                blocks.Add($"{{ \"at\": [{at.Item1}, {at.Item2}, {at.Item3}], \"type\": \"{i switch { 0 => "core", 1 => "shield", 2 => "thruster", 3 => "gyro", 4 => "generator", _ => "hull" }}\" }}");
            }

            var path = Path.Combine(dir, "test.json");
            File.WriteAllText(path, $"{{ \"name\": \"T\", \"class\": \"ship\", \"blockSize\": 2.0, \"color\": [0.5, 0.5, 0.5], \"blocks\": [{string.Join(",", blocks)}] }}");
            var catalog = BlockCatalog.Load(AppPaths.Content("blocks", "blocks.json"));
            var design = HullDesign.Load(path, catalog, catalog.Rules);
            var shell = Shell(design, design.FullMask);
            Assert.InRange(shell.Length, 36, ShieldShell.MaxVertices);
            var random = new Random(5);
            for (var r = 0; r < 16; r++)
            {
                var direction = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
                Assert.Equal(1, Crossings(shell, design.BlockOffsets[r * 3], direction) % 2);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
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

    private static HullView Hull(uint id, float shield = 1500f, bool offline = false, ulong? blocks = null, float changeAge = 0f) =>
        new()
        {
            Id = id,
            Design = (byte)Content.PodDesign,
            Blocks = blocks ?? Pod.FullMask,
            Orientation = Quaternion.Identity,
            Shield = shield,
            ShieldOffline = offline,
            ShieldChangeAge = changeAge,
        };

    /// <summary>Runs one frame and returns the drawn shell of the hull, if any.</summary>
    private ShieldInstance? Frame(params HullView[] hulls)
    {
        _shields.Update(hulls, Content.Designs, Dt);
        _shields.Begin(_draws);
        foreach (var hull in hulls)
            _shields.Add(hull.Id, new Vector3(1, 2, 3), Quaternion.Identity, 1f, 1f, _instances, _vertices);
        return _shields.Draws.Length > 0 ? _instances[0] : null;
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
            _shields.Add(hull.Id, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices);
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
        bare.Update([Hull(2, 0f, blocks: Pod.FullMask & ~(1UL << Emitter))], Content.Designs, Dt);
        bare.Begin(_draws);
        bare.Add(2, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices);
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
        while (Frame(Hull(1, frames * 2f, offline: true)) is { } still)
        {
            Assert.True(still.Params.Z > 0f);
            frames++;
            Assert.True(frames < 200);
        }

        Assert.InRange(frames * Dt, Look.CollapseSeconds - 2 * Dt, Look.CollapseSeconds + 2 * Dt);
        for (var i = 0; i < 120; i++)
            Assert.Null(Frame(Hull(1, 300f, offline: true)));

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
        var lost = Pod.FullMask & ~(1UL << Emitter);
        Assert.True(Frame(Hull(1, 0f, blocks: lost))!.Value.Params.Z > 0f);
        for (var i = 0; i < 60; i++)
            Frame(Hull(1, 0f, blocks: lost));
        Assert.Null(Frame(Hull(1, 0f, blocks: lost)));
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
            drawn = Frame(fresh)!.Value;
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
        _shields.Update([Hull(1)], Content.Designs, Dt);
        _shields.Begin(_draws);
        var material = 1f + 4f * (5 + 1);
        _shields.Add(1, Vector3.Zero, Quaternion.Identity, 3f, material, _instances, _vertices);
        var drawn = _instances[0];
        Assert.Equal(material, drawn.Params.W);
        Assert.Equal(3f, drawn.Row2.Z, 4);
        Assert.Equal(1f, drawn.Row0.X, 4);

        // Its vertices are the shell of the blocks it has.
        var shell = new MeshVertex[ShieldShell.MaxVertices];
        var count = ShieldShell.Build(Pod, Pod.FullMask, shell);
        Assert.Equal(count, _shields.Draws[0].VertexCount);
        Assert.Equal(shell[5].Position.Y, _vertices[_shields.Draws[0].FirstVertex + 5].Position.Y);

        // A hull not in the snapshot this frame adds nothing.
        _shields.Add(99, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices);
        Assert.Equal(1, _shields.Draws.Length);
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
    public void AFullFrameOfShieldsAllocatesNothingOnceWarm()
    {
        var hulls = new HullView[ShieldVisuals.MaxShields];
        for (var i = 0; i < hulls.Length; i++)
            hulls[i] = Hull((uint)i + 1, 700f, blocks: Pod.FullMask & ~(1UL << (i % Pod.Cells.Length)));
        for (var warm = 0; warm < 3; warm++)
            Frame(hulls);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 30; frame++)
        {
            hulls[frame % hulls.Length].ShieldHit.Serial++;
            hulls[frame % hulls.Length].ShieldHit.Strength = 50;
            Frame(hulls);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(ShieldVisuals.MaxShields - CountUnshielded(hulls), _shields.Draws.Length);
    }

    private static int CountUnshielded(HullView[] hulls)
    {
        var count = 0;
        foreach (var hull in hulls)
        {
            Pod.Evaluate(hull.Blocks, out var flight, out _);
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
        var lost = Pod.FullMask & ~(1UL << Emitter);
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
        var own = new ShieldVisuals(Look);
        own.Update([Hull(3)], Content.Designs, Dt);
        own.Update([Hull(3, 0f, offline: true)], Content.Designs, Dt);
        own.Update([Hull(3, 1500f, changeAge: 1.9f)], Content.Designs, Dt);
        own.Update([Hull(3, 1500f, changeAge: 1.9f)], Content.Designs, Dt);
        own.Begin(_draws);
        own.Add(3, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices);
        Assert.Equal(1, own.Draws.Length);
        Assert.InRange(_instances[0].Params.X, 1e-4f, 2 * Dt / Look.FadeInSeconds);
        Assert.Equal(0f, _instances[0].Params.Z);
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

    /// <summary>Runs one frame of <paramref name="dt"/> seconds with one hull and returns its shell, if drawn.</summary>
    private ShieldInstance? Frame(float dt, HullView hull)
    {
        _shields.Update([hull], Content.Designs, dt);
        _shields.Begin(_draws);
        _shields.Add(hull.Id, Vector3.Zero, Quaternion.Identity, 1f, 1f, _instances, _vertices);
        return _shields.Draws.Length > 0 ? _instances[0] : null;
    }

    [Fact]
    public void AStallPastAPendingBreakTakesTheNewPublishedAgeAndDoesNotReplayIt()
    {
        Frame(Hull(1));
        Assert.Equal(1f, Frame(Dt, Hull(1, 0f, offline: true, changeAge: -0.05f))!.Value.Params.X);

        // A second without frames; the next publication says the break is 0.95 s old, past the 0.7 s collapse.
        Assert.Null(Frame(1f, Hull(1, 0f, offline: true, changeAge: 0.95f)));
        Assert.Null(Frame(Dt, Hull(1, 0f, offline: true, changeAge: 0.95f + Dt)));
    }

    [Fact]
    public void ARunningCollapseFollowsTheRenderedClockForwardAndBack()
    {
        Frame(Hull(1));
        Frame(Dt, Hull(1, 0f, offline: true, changeAge: 0.1f));
        var forward = Frame(Dt, Hull(1, 0f, offline: true, changeAge: 0.5f))!.Value;
        Assert.Equal(0.5f / Look.CollapseSeconds, forward.Params.Z, 3);
        var back = Frame(Dt, Hull(1, 0f, offline: true, changeAge: 0.2f))!.Value;
        Assert.Equal(0.2f / Look.CollapseSeconds, back.Params.Z, 3);

        // Corrected back past the break itself: the drawn moment is before it again, so the shield is up.
        var before = Frame(Dt, Hull(1, 0f, offline: true, changeAge: -0.1f))!.Value;
        Assert.Equal(1f, before.Params.X);
        Assert.Equal(0f, before.Params.Z);

        // And a pending break pulled forward starts where the clock says.
        var pulled = Frame(Dt, Hull(1, 0f, offline: true, changeAge: 0.05f))!.Value;
        Assert.Equal(0.05f / Look.CollapseSeconds, pulled.Params.Z, 3);
    }

    [Fact]
    public void RepeatedPublicationsMoveOnByTheLocalClock()
    {
        Frame(Hull(1));
        var frames = 0;
        while (Frame(Dt, Hull(1, 0f, offline: true, changeAge: -0.05f))!.Value.Params.Z == 0f)
        {
            frames++;
            Assert.True(frames < 10);
        }

        Assert.InRange(frames, 2, 4);

        // The same publication again and again: the collapse still runs to its end.
        for (var i = 0; i < 60 && Frame(Dt, Hull(1, 0f, offline: true, changeAge: -0.05f)) is not null; i++)
            Assert.True(i < 50);
    }

    [Fact]
    public void ARippleIsPutBackOnThePublishedClockAfterAStall()
    {
        var hull = Hull(1);
        Frame(hull);
        hull.ShieldHit = new ShieldHit { Serial = 1, Strength = 200, X = 50 };
        hull.ShieldHitAge = -0.05f;
        var first = Frame(Dt, hull)!.Value;
        Assert.InRange((first.Strengths.X > 0 ? first.Ripple0 : first.Strengths.Y > 0 ? first.Ripple1 : first.Strengths.Z > 0 ? first.Ripple2 : first.Ripple3).W, -0.05f, -0.04f);

        hull.ShieldHitAge = 0.6f;
        var drawn = Frame(1f, hull)!.Value;
        var live = drawn.Strengths.X > 0 ? drawn.Ripple0 : drawn.Strengths.Y > 0 ? drawn.Ripple1 : drawn.Strengths.Z > 0 ? drawn.Ripple2 : drawn.Ripple3;
        Assert.Equal(0.6f, live.W, 3);

        // Past its life on the published clock, it is gone.
        hull.ShieldHitAge = Look.RippleSeconds + 0.1f;
        drawn = Frame(Dt, hull)!.Value;
        Assert.Equal(0f, drawn.Strengths.X + drawn.Strengths.Y + drawn.Strengths.Z + drawn.Strengths.W);
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
    [InlineData("\"rippleSeconds\": 1.2", "\"rippleSeconds\": 0")]
    [InlineData("\"unstableFull\": 0.08", "\"unstableFull\": 0.5")]
    [InlineData("\"calmColor\": [0.16, 0.34, 0.42]", "\"calmColor\": [0.16, 0.34]")]
    [InlineData("\"refractionPixels\": 2.2", "\"refractionPixels\": 9")]
    [InlineData("\"rippleWidth\": 1.1", "\"rippleWidth\": -1")]
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

        // Two vertex attributes, then one location per four floats of the instance.
        var locations = System.Runtime.CompilerServices.Unsafe.SizeOf<ShieldInstance>() / 16;
        Assert.Equal(9, locations);
        Assert.Contains($"layout(location = {2 + locations - 1}) in vec4 strengths;", shield);
    }
}
