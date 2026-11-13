using System.Numerics;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Settings;
using Prospect.Client.World;
using Prospect.Engine.Jobs;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Where other hulls are drawn through their jumps, worked out once a frame by <see cref="HullVisuals"/> and shared
/// by the drawing, the picking and the markers: the followed jumps' slots are freed and reused whatever ends a jump,
/// only the jumping hull is cut at its opening, and what can be picked is what is drawn.
/// </summary>
public sealed class HullVisualsTests
{
    private const float Dt = 1f / 60f;
    private static readonly GameContent Content = GameContent.Load();

    private static int DesignWith(FtlKind drive)
    {
        for (var i = 0; i < Content.Designs.Length; i++)
        {
            if (Content.Designs[i].Flight.Ftl == drive)
                return i;
        }

        throw new InvalidOperationException($"no design with a {drive} drive");
    }

    /// <summary>A hull at <paramref name="at"/> facing -z, in the given stage of a jump, that far through it.</summary>
    private static HullView Hull(uint id, int design, JumpPhase phase, float progress, Double3 at = default)
    {
        var flight = Content.Designs[design].Flight;
        var timer = phase switch
        {
            JumpPhase.Charge => flight.FtlEnergy * progress,
            JumpPhase.Tunnel => flight.FtlTunnelSeconds * progress,
            JumpPhase.Arrive => flight.FtlArriveSeconds * progress,
            _ => 0f,
        };
        return new HullView
        {
            Id = id,
            Design = (byte)design,
            Blocks = Content.Designs[design].FullSet.ToArray(),
            Position = at,
            PreviousPosition = at,
            Orientation = Quaternion.Identity,
            PreviousOrientation = Quaternion.Identity,
            Jump = phase,
            JumpTimer = timer,
            JumpCalculation = flight.FtlCalculateSeconds,
        };
    }

    private static void Update(HullVisuals visuals, ReadOnlySpan<HullView> hulls) => visuals.Update(hulls, Content.Designs, 1f, Dt, true, FtlKind.None);

    private static int Followed(HullVisuals visuals)
    {
        var count = 0;
        foreach (var slot in visuals.Passages)
            count += slot.Id != 0 ? 1 : 0;
        return count;
    }

    [Fact]
    public void ANewSessionWithReusedHullIdsStartsWithoutOldPassages()
    {
        var design = DesignWith(FtlKind.Rift);
        var devices = new InputDevices();
        var actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        using var jobs = new JobSystem(1, "TestJob");
        var world = new WorldView(devices, actions, new ClientSettings(), jobs, null!, Content.Designs);
        var screen = new float2(1280, 720);
        SessionSnapshot Session(JumpPhase phase)
        {
            // ClientSession creates a fresh galaxy on every Welcome, even for the same seed.
            var snapshot = new SessionSnapshot(Content)
            {
                Galaxy = Content.Galaxy.WithSeed(17),
                State = SessionState.Active,
                PlayerId = 1,
                OwnHull = 0,
                HullCount = 2,
                Flight = Content.Designs[design].Flight,
            };
            snapshot.Hulls[0] = Hull(1, design, phase, 0.8f);
            snapshot.Hulls[0].IsOwn = true;
            snapshot.Hulls[0].Owner = 1;
            snapshot.Hulls[1] = Hull(2, design, phase, 0.8f, new Double3(100, 0, 0));
            return snapshot;
        }

        try
        {
            var old = Session(JumpPhase.Charge);
            world.Update(old, new PlayerInputBridge(), Dt, false, screen, screen, false);
            Assert.True(Followed(world.Visuals) > 0);
            Assert.True(world.OwnDrawnPosition.RelativeTo(world.OwnPosition).Length() > 1f);

            var current = Session(JumpPhase.None);
            world.Update(current, new PlayerInputBridge(), Dt, false, screen, screen, false);
            Assert.Equal(0, Followed(world.Visuals));
            Assert.Equal(world.OwnPosition, world.OwnDrawnPosition);
            for (var i = 0; i < current.HullCount; i++)
            {
                Assert.Equal(current.Hulls[i].Position, world.DrawnPosition(current.Hulls[i]));
                Assert.Equal(-1, world.Visuals.Hulls[i].Clip);
            }
        }
        finally
        {
            jobs.CompleteAll();
        }
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    [InlineData(FtlKind.Hyperspace)]
    public void HullsThatLeaveMidJumpFreeTheirSlotsForTheNextOnes(FtlKind drive)
    {
        // Every slot taken by hulls drawn well along their noses, which then leave the snapshot (a disconnect) with
        // their offsets still large: their passages ease out and the slots are free for the next round of jumpers.
        var design = DesignWith(drive);
        var visuals = new HullVisuals();
        var hulls = new HullView[HullVisuals.MaxPassages];
        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < hulls.Length; i++)
                hulls[i] = Hull((uint)(round * 100 + i + 1), design, JumpPhase.Charge, 0.95f, new Double3(i * 500, 0, 0));
            Update(visuals, hulls);
            for (var i = 0; i < hulls.Length; i++)
            {
                var visual = visuals.Hulls[i];
                Assert.True(visual.Position.RelativeTo(visual.Anchor).Length() > 1f, $"round {round}: hull {i} has no slot to be drawn along its jump");
            }

            Assert.Equal(HullVisuals.MaxPassages, Followed(visuals));
            var frames = 0;
            while (Followed(visuals) > 0 && frames++ < 240)
                Update(visuals, []);
            Assert.True(frames < 240, $"round {round}: {Followed(visuals)} passages of departed hulls never ended");
        }
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Hyperspace)]
    public void ACancelledJumpEasesBackAndItsSlotIsReusedFresh(FtlKind drive)
    {
        var design = DesignWith(drive);
        var visuals = new HullVisuals();
        Update(visuals, [Hull(1, design, JumpPhase.Charge, 0.97f)]);
        var drawnAt = visuals.Hulls[0].Position.RelativeTo(visuals.Hulls[0].Anchor).Length();
        Assert.True(drawnAt > 1f);

        // Cancelled: the hull backs out over several frames rather than snapping to where the simulation has it.
        Update(visuals, [Hull(1, design, JumpPhase.None, 0f)]);
        var first = visuals.Hulls[0].Position.RelativeTo(visuals.Hulls[0].Anchor).Length();
        Assert.True(first > 0.5f * drawnAt && first < drawnAt, $"{first} after {drawnAt}");
        Assert.True(visuals.Hulls[0].Visible);
        var frames = 0;
        while (Followed(visuals) > 0 && frames++ < 240)
            Update(visuals, [Hull(1, design, JumpPhase.None, 0f)]);
        Assert.Equal(0, Followed(visuals));
        Update(visuals, [Hull(1, design, JumpPhase.None, 0f)]);
        Assert.Equal(-1, visuals.Hulls[0].Clip);
        Assert.Equal(1f, visuals.Hulls[0].Stretch);
        Assert.True(visuals.Hulls[0].Position.RelativeTo(visuals.Hulls[0].Anchor).Length() < 0.01f);

        // The freed slot starts the next hull's jump from nothing: no stretch, no light left from the last one.
        Update(visuals, [Hull(2, design, JumpPhase.Charge, 0.1f)]);
        var slot = visuals.Passages[0];
        Assert.Equal(2u, slot.Id);
        Assert.True(MathF.Abs(slot.Offset) < 1e-4f && slot.Stretch == 1f && slot.Flash <= 0f);
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    public void OnlyTheJumpingHullIsCutAndOnlyWhatIsShownCanBePicked(FtlKind drive)
    {
        var design = DesignWith(drive);
        var flight = Content.Designs[design].Flight;
        var full = Content.Designs[design].FullMask();
        var visuals = new HullVisuals();

        // Late in the charge the hull goes into its opening: find a moment it is partly through.
        var progress = 0.6f;
        for (; progress < 1f; progress += 0.002f)
        {
            Update(visuals, [Hull(1, design, JumpPhase.Charge, progress)]);
            var through = BlockMasks.Word(visuals.Hulls[0].PickBlocks.Span);
            if (through != full && through != 0)
                break;
        }

        Assert.True(progress < 1f, "the hull is never partly through its opening");

        // A formation neighbour sits past the opening, where the jumping hull is cut away.
        var mouth = visuals.Passages[0].Mouth;
        var neighbour = Hull(2, design, JumpPhase.None, 0f, mouth + new Double3(0, 0, -flight.Radius));
        Update(visuals, [Hull(1, design, JumpPhase.Charge, progress), neighbour]);
        var jumping = visuals.Hulls[0];
        var beside = visuals.Hulls[1];
        Assert.True(jumping.Clip >= 0);
        Assert.Equal(-1, beside.Clip);
        Assert.Equal(full, BlockMasks.Word(beside.PickBlocks.Span));
        Assert.True(beside.Visible);

        // A block stays pickable while any of it is on the shown side of the plane the mesh pass cuts at.
        var slot = visuals.Passages[jumping.Clip];
        var blocks = Content.Designs[design].BlockOffsets;
        var half = Content.Designs[design].BlockSize * 0.5f;
        var camera = jumping.Position + new Double3(2000, 0, 0);
        var picks = new PickHit[4];
        var hull = visuals.Pick(1, jumping, jumping.Position, Content.Designs[design]);
        for (var b = 0; b < blocks.Length; b++)
        {
            var centre = jumping.Position + Vector3.Transform(blocks[b], jumping.Orientation);
            var past = Vector3.Dot(centre.RelativeTo(slot.Mouth), slot.Axis * slot.Clip);
            Assert.Equal(past - half <= 0f, (BlockMasks.Word(jumping.PickBlocks.Span) & 1UL << b) != 0);

            // Picked through the same picker the world uses, with nothing in the way: the cut part of a block is
            // not a target, even when the rest of it is. Seen from the side, the blocks along the ray through its
            // centre are as far along the axis as this one, cut or shown alike.
            var ray = Vector3.Normalize(centre.RelativeTo(camera));
            Assert.Equal(past <= 0f ? 1 : 0, TargetPicking.Pick(ray, camera, ray, 1e6f, [hull], [], picks));
        }
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    public void AHullWithEveryBlockCentrePastItsOpeningIsPickedByWhatIsStillShown(FtlKind drive)
    {
        var design = DesignWith(drive);
        var blocks = Content.Designs[design].BlockOffsets;
        var half = Content.Designs[design].BlockSize * 0.5f;
        var visuals = new HullVisuals();

        // Find a frame where the hull is still drawn and every block's centre is past the opening, but not all of
        // every block: the aft blocks still show their back slabs.
        HullVisuals.HullVisual visual = default;
        var found = false;
        for (var progress = 0.6f; progress < 1f && !found; progress += 0.0005f)
        {
            Update(visuals, [Hull(1, design, JumpPhase.Charge, progress)]);
            visual = visuals.Hulls[0];
            if (!visual.Visible || visual.Clip < 0)
                continue;
            var slot = visuals.Passages[visual.Clip];
            var centres = true;
            foreach (var block in blocks)
                centres &= Vector3.Dot((visual.Position + block).RelativeTo(slot.Mouth), slot.Axis * slot.Clip) > 0f;
            found = centres && BlockMasks.Word(visual.PickBlocks.Span) != 0;
        }

        Assert.True(found, "the hull is never drawn with every block centre past its opening");
        var mouth = visuals.Passages[visual.Clip].Mouth;
        var hull = visuals.Pick(1, visual, visual.Position, Content.Designs[design]);

        // The hull faces -z and goes in nose first, so the aft block shows from the opening back to its aft face.
        var aft = 0;
        for (var b = 1; b < blocks.Length; b++)
            aft = blocks[b].Z > blocks[aft].Z ? b : aft;
        var centre = visual.Position + blocks[aft];
        var opening = (float)(mouth.Z - centre.Z);
        Assert.InRange(opening, 0f, half);
        var camera = centre + new Double3(2000, 0, 0);
        var picks = new PickHit[1];
        var shown = Vector3.Normalize((centre + new Double3(0, 0, (opening + half) * 0.5f)).RelativeTo(camera));
        Assert.Equal(1, TargetPicking.Pick(shown, camera, shown, 1e6f, [hull], [], picks));
        Assert.InRange(picks[0].Distance, 2000f - half - 0.01f, 2000f - half + 0.01f);

        // The part past the opening is not, though the block is kept.
        var cut = Vector3.Normalize((centre + new Double3(0, 0, (opening - half) * 0.5f)).RelativeTo(camera));
        Assert.Equal(0, TargetPicking.Pick(cut, camera, cut, 1e6f, [hull], [], picks));

        // Seen nose on from past the opening, the kept slabs show nothing: each face towards the camera is past the
        // opening, the others face away, and nothing is drawn across the cut. The intact hull is one mesh.
        Assert.Same(Content.Designs[design].ExteriorFaces, hull.Faces);
        var ahead = centre + new Double3(0, 0, -2000);
        var into = Vector3.Normalize(centre.RelativeTo(ahead));
        Assert.Equal(0, TargetPicking.Pick(into, ahead, into, 1e6f, [hull], [], picks));

        // Missing a block, it is drawn a cube per block, with every face of each.
        var damaged = Hull(1, design, JumpPhase.Charge, 0.5f) with { Blocks = BlockMasks.Set(Content.Designs[design].FullMask() & ~1UL) };
        Update(visuals, [damaged]);
        Assert.Null(visuals.Pick(1, visuals.Hulls[0], visuals.Hulls[0].Position, Content.Designs[design]).Faces);
    }

    [Fact]
    public void AHullDrawnOutByHyperspaceIsPickedAsFarAsItIsDrawn()
    {
        var design = DesignWith(FtlKind.Hyperspace);
        var blocks = Content.Designs[design].BlockOffsets;
        var half = Content.Designs[design].BlockSize * 0.5f;
        var visuals = new HullVisuals();
        Update(visuals, [Hull(1, design, JumpPhase.Charge, 0.99f)]);
        var visual = visuals.Hulls[0];
        Assert.True(visual.Visible && visual.Stretch > 4f, $"stretch {visual.Stretch}");
        var hull = visuals.Pick(1, visual, visual.Position, Content.Designs[design]);
        var plain = hull with { Stretch = 1f };

        // The block furthest along the nose (-z), drawn out the way the mesh pass draws it.
        var nose = 0;
        for (var b = 1; b < blocks.Length; b++)
            nose = blocks[b].Z < blocks[nose].Z ? b : nose;
        var tip = blocks[nose] with { Z = (blocks[nose].Z - half) * visual.Stretch };
        var plainTip = blocks[nose].Z - half;

        // Seen side on, a point inside the drawn-out nose but well ahead of the blocks unstretched is the hull.
        var inside = visual.Position + (tip with { Z = tip.Z + half });
        Assert.True(inside.Z < visual.Position.Z + plainTip - 1f, $"{inside.Z} against {visual.Position.Z + plainTip}");
        var camera = inside + new Double3(2000, 0, 0);
        var ray = Vector3.Normalize(inside.RelativeTo(camera));
        var picks = new PickHit[1];
        Assert.Equal(1, TargetPicking.Pick(ray, camera, ray, 1e6f, [hull], [], picks));
        Assert.Equal(0, TargetPicking.Pick(ray, camera, ray, 1e6f, [plain], [], picks));

        // Just past the drawn-out tip, within the twelve-pixel grab, it is still the hull; unstretched it is not.
        const float Focal = 1000f;
        const float Distance = 200f;
        var grab = TargetPicking.GrabPixels / Focal * Distance;
        var near = visual.Position + (tip with { Z = tip.Z - grab * 0.5f });
        Assert.True(near.Z < visual.Position.Z + plainTip - grab * 2f);
        camera = near + new Double3(Distance, 0, 0);
        var forward = -Vector3.UnitX;
        ray = Vector3.Normalize(near.RelativeTo(camera));
        PickHull[] hulls = [hull];
        Assert.Equal(1, TargetPicking.Pick(ray, camera, forward, Focal, hulls, [], picks));
        Assert.Equal(0, TargetPicking.Pick(ray, camera, forward, Focal, [plain], [], picks));

        // The grab around the drawn-out silhouette allocates nothing.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
            TargetPicking.Pick(ray, camera, forward, Focal, hulls, [], picks);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void AnotherHullsHyperspaceJumpLeavesAndArrivesInsteadOfVanishing()
    {
        var design = DesignWith(FtlKind.Hyperspace);
        var flight = Content.Designs[design].Flight;
        var visuals = new HullVisuals();

        // Through the charge it stays in view and, at the end, lunges along its nose, drawn out.
        var lastOffset = 0f;
        for (var p = 0f; p <= 1f; p += 0.01f)
        {
            Update(visuals, [Hull(1, design, JumpPhase.Charge, p)]);
            var visual = visuals.Hulls[0];
            var offset = Vector3.Dot(visual.Position.RelativeTo(visual.Anchor), -Vector3.UnitZ);
            Assert.True(visual.Visible);
            Assert.True(offset >= lastOffset - 1e-4f);
            lastOffset = offset;
        }

        Assert.True(lastOffset > flight.Radius * 2f && visuals.Hulls[0].Stretch > 4f, $"{lastOffset} m, stretch {visuals.Hulls[0].Stretch}");

        // The drive fires: the hull is gone, and the light it flew into flares and fades where it was.
        var flashes = 0;
        var arrivedSeen = false;
        for (var t = 0f; t < flight.FtlTunnelSeconds; t += Dt)
        {
            Update(visuals, [Hull(1, design, JumpPhase.Tunnel, t / flight.FtlTunnelSeconds)]);
            var flash = visuals.Passages[0].Flash;
            flashes += flash > 0f && flash < 1f ? 1 : 0;
            if (t < flight.FtlTunnelSeconds - 0.6f)
                Assert.False(visuals.Hulls[0].Visible);
            arrivedSeen |= visuals.Hulls[0].Visible;
        }

        Assert.True(flashes > 10, $"the light at the start showed for {flashes} frames");
        Assert.True(arrivedSeen, "the hull is not streaking in before the transit is over");

        // It streaks in from behind and settles where the simulation has it.
        Update(visuals, [Hull(1, design, JumpPhase.Arrive, 0f)]);
        var arriving = visuals.Hulls[0];
        Assert.True(arriving.Stretch > 1f && Vector3.Dot(arriving.Position.RelativeTo(arriving.Anchor), -Vector3.UnitZ) < 0f);
        Update(visuals, [Hull(1, design, JumpPhase.Arrive, 1f)]);
        Assert.True(visuals.Hulls[0].Position.RelativeTo(visuals.Hulls[0].Anchor).Length() < 1e-3f && visuals.Hulls[0].Stretch == 1f);

        // One who first sees it in transit sees no light left at a start they never saw.
        var late = new HullVisuals();
        Update(late, [Hull(1, design, JumpPhase.Tunnel, 0.05f)]);
        Assert.True(late.Passages[0].Flash <= 0f);
    }

    [Fact]
    public void WorkingOutTheHullsAllocatesNothingOnceWarm()
    {
        var hulls = new HullView[ClientWorld.MaxViews];
        FtlKind[] drives = [FtlKind.Rift, FtlKind.Wormhole, FtlKind.Hyperspace, FtlKind.None];
        var visuals = new HullVisuals();
        var allocated = 0L;
        for (var frame = 0; frame < 400; frame++)
        {
            for (var i = 0; i < hulls.Length; i++)
            {
                var drive = drives[i % drives.Length];
                var design = drive == FtlKind.None ? 0 : DesignWith(drive);
                var phase = (JumpPhase)((frame / 50 + i) % 5);
                hulls[i] = Hull((uint)i + 1, design, phase, frame % 50 / 50f, new Double3(i * 300, 0, 0));
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            Update(visuals, hulls);
            if (frame >= 50)
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0L, allocated);
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    [InlineData(FtlKind.Hyperspace)]
    public void InTheCommandViewAJumpingHullIsPickedAndMarkedWhereItIsDrawn(FtlKind drawnAs)
    {
        // A real server and client joined in process, the real world view in the command view, where the pilot's
        // own hull's jump is seen from outside as any other hull's is. Each frame the pointer is put on the hull
        // where it is drawn, and on where the simulation has it, and the world picks under it.
        using var link = new LoopbackLink();
        using var client = new ClientSession(link.Client, Content, "Watcher");
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, Content, [link.Server]);
        using var jobs = new JobSystem(1, "TestJob");
        var devices = new InputDevices();
        var actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        var world = new WorldView(devices, actions, new ClientSettings(), jobs, null!, Content.Designs) { FtlPreview = drawnAs };
        var screen = new float2(1280, 720);
        long tick = 0;
        SessionSnapshot Step(bool advance)
        {
            if (advance)
            {
                ((ITickHandler)server).Tick(tick);
                ((ITickHandler)client).Tick(tick++);
            }

            var snapshot = client.AcquireSnapshot();
            devices.BeginFrame();
            if (snapshot.Galaxy is not null && snapshot.OwnHull >= 0)
                world.Update(snapshot, client.Input, Dt, true, screen, screen, false);
            return snapshot;
        }

        try
        {
            for (var i = 0; i < 10; i++)
                Step(true);
            world.SetMode(ViewMode.Command);
            for (var i = 0; i < 60; i++)
                Step(true);
            var target = client.AcquireSnapshot().Galaxy!.CenterOf(new SectorCoord(1, 0, 0));
            client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = target });

            var shownPicked = 0;
            var movedPicked = 0;
            var hiddenChecked = 0;
            var started = false;
            for (var frame = 0; frame < TickLoop.SimulationHz * 60; frame++)
            {
                var snapshot = Step(true);
                var index = snapshot.OwnHull;
                var seen = snapshot.Hulls[index];
                started |= seen.Jump != JumpPhase.None;
                if (started && seen.Jump == JumpPhase.None)
                    break;
                var visual = world.Visuals.Hulls[index];

                // Both sets are this frame's memory, which the next frame fills again: keep them as numbers.
                var pickable = BlockMasks.Word(visual.PickBlocks.Span);
                var whole = seen.Mask();
                Assert.False(visual.Inside);
                var drawn = world.DrawnPosition(seen);
                Assert.Equal(visual.Position, drawn);

                // The pointer on where the hull is drawn this frame, then the same frame again with it there.
                if (!world.Project(drawn, out var at))
                    continue;
                devices.PlacePointer(at);
                Step(false);
                if (visual.Visible && pickable == whole)
                {
                    Assert.True(seen.Id == world.HoveredHull, $"{seen.Jump} {WorldView.JumpProgress(seen, Content.Designs[seen.Design].Flight)}: hovered {world.HoveredHull}");
                    shownPicked++;
                    movedPicked += drawn.RelativeTo(world.HullPosition(seen)).Length() > 1f ? 1 : 0;
                }
                else if (!visual.Visible)
                {
                    // Between its ends it can be picked neither where it would be drawn nor where the simulation has it.
                    Assert.NotEqual(seen.Id, world.HoveredHull);
                    if (world.Project(world.HullPosition(seen), out var raw))
                    {
                        devices.PlacePointer(raw);
                        Step(false);
                        Assert.NotEqual(seen.Id, world.HoveredHull);
                    }

                    hiddenChecked++;
                }
            }

            Assert.True(started, "the hull never jumped");
            Assert.True(shownPicked > 30 && movedPicked > 5 && hiddenChecked > 30, $"picked {shownPicked} ({movedPicked} moved along the jump), hidden {hiddenChecked}");
        }
        finally
        {
            jobs.CompleteAll();
        }
    }

    [Fact]
    public void AHullsTransitIsItsOwnLatchedDurationOrTheDrivesBaseOne()
    {
        var flight = Content.Designs[DesignWith(FtlKind.Rift)].Flight;
        Assert.Equal(flight.FtlTunnelSeconds, HullVisuals.TunnelSeconds(new HullView(), flight));
        Assert.Equal(25f, HullVisuals.TunnelSeconds(new HullView { JumpTunnelSeconds = 25f }, flight));
        Assert.Equal(flight.FtlTunnelSeconds, HullVisuals.TunnelSeconds(new HullView { JumpTunnelSeconds = float.NaN }, flight));
        Assert.Equal(0.5f, WorldView.JumpProgress(new HullView { Jump = JumpPhase.Tunnel, JumpTimer = 20f, JumpTunnelSeconds = 40f }, flight));
    }

    [Theory]
    [InlineData(FtlKind.Rift, 12f)]
    [InlineData(FtlKind.Rift, 600f)]
    [InlineData(FtlKind.Wormhole, 12f)]
    [InlineData(FtlKind.Wormhole, 600f)]
    public void ALongTransitWaitsBetweenTheOpeningsAndComesOutAsAShortOne(FtlKind drive, float tunnel)
    {
        // However far the jump, the far opening opens and the hull comes out of it over the same last seconds as
        // the drive's shortest transit, and the arrival is the same; before that nothing shows at the far end.
        var flight = Content.Designs[DesignWith(drive)].Flight;
        var basis = flight.FtlTunnelSeconds;
        Assert.True(tunnel > basis);
        for (var since = 0f; since < tunnel; since += tunnel / 400f)
        {
            var shape = HullVisuals.Passage(JumpPhase.Tunnel, since / tunnel, flight, tunnel);
            var left = tunnel - since;
            if (left > basis)
            {
                Assert.False(shape.Visible, $"seen {left} s before the end");
                Assert.Equal(0f, shape.Open);
                continue;
            }

            var short_ = HullVisuals.Passage(JumpPhase.Tunnel, (basis - left) / basis, flight);
            Assert.Equal(short_.Visible, shape.Visible);
            Assert.Equal(short_.Open, shape.Open, 3);
            Assert.Equal(short_.Offset, shape.Offset, 2);
            Assert.Equal(short_.Mouth, shape.Mouth, 3);
        }

        for (var progress = 0f; progress <= 1f; progress += 0.05f)
        {
            var short_ = HullVisuals.Passage(JumpPhase.Arrive, progress, flight);
            var shape = HullVisuals.Passage(JumpPhase.Arrive, progress, flight, tunnel);
            Assert.Equal(short_.Visible, shape.Visible);
            Assert.Equal(short_.Open, shape.Open, 3);
            Assert.Equal(short_.Offset, shape.Offset, 2);
        }
    }

    [Theory]
    [InlineData(12f)]
    [InlineData(600f)]
    public void ALongHyperspaceTransitStreaksInOverTheSameLastMoment(float tunnel)
    {
        var flight = Content.Designs[DesignWith(FtlKind.Hyperspace)].Flight;
        var basis = flight.FtlTunnelSeconds;
        for (var since = 0f; since < tunnel; since += tunnel / 400f)
        {
            var shape = HullVisuals.Hyperspace(JumpPhase.Tunnel, since / tunnel, flight, tunnel);
            var left = tunnel - since;
            if (left > basis)
            {
                Assert.False(shape.Visible, $"seen {left} s before the end");
                continue;
            }

            var short_ = HullVisuals.Hyperspace(JumpPhase.Tunnel, (basis - left) / basis, flight);
            Assert.Equal(short_.Visible, shape.Visible);
            Assert.Equal(short_.Offset, shape.Offset, 2);
            Assert.Equal(short_.Stretch, shape.Stretch, 3);
        }

        for (var progress = 0f; progress <= 1f; progress += 0.05f)
        {
            var short_ = HullVisuals.Hyperspace(JumpPhase.Arrive, progress, flight);
            var shape = HullVisuals.Hyperspace(JumpPhase.Arrive, progress, flight, tunnel);
            Assert.Equal(short_.Visible, shape.Visible);
            Assert.Equal(short_.Offset, shape.Offset, 2);
            Assert.Equal(short_.Stretch, shape.Stretch, 3);
        }
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(33554432.0)]
    [InlineData(134217728.0)]
    [InlineData(1099511627776.0)]
    public void TheLastSecondsOfAnyTransitStayExact(double tunnel)
    {
        // The simulation keeps the elapsed seconds in double; the drawing takes the seconds left from them, so the
        // last second and the arrival are the same for a transit of any supported length as for the shortest.
        var flight = Content.Designs[DesignWith(FtlKind.Rift)].Flight;
        var basis = flight.FtlTunnelSeconds;
        foreach (var left in (ReadOnlySpan<double>)[2.5, 1.2, 0.4, 0.2])
        {
            var hull = new HullView { Jump = JumpPhase.Tunnel, JumpTunnelSeconds = (float)tunnel, JumpTunnelElapsed = (float)tunnel - left };
            var clock = HullVisuals.TransitClock(hull, flight);
            Assert.Equal(left, clock.Left, 4);
            var shape = HullVisuals.Passage(JumpPhase.Tunnel, WorldView.JumpProgress(hull, flight), flight, HullVisuals.TunnelSeconds(hull, flight), clock.Left);
            var short_ = HullVisuals.Passage(JumpPhase.Tunnel, (float)((basis - left) / basis), flight);
            Assert.True(float.IsFinite(shape.Offset) && float.IsFinite(shape.Open));
            Assert.Equal(short_.Visible, shape.Visible);
            // At 2^40 s a double still holds the elapsed seconds to a quarter of a millisecond.
            Assert.True(MathF.Abs(short_.Offset - shape.Offset) < 0.05f, $"{left} s left: {shape.Offset}, {short_.Offset} for a short transit");
            Assert.True(MathF.Abs(short_.Open - shape.Open) < 1e-3f);
            var light = HullVisuals.Hyperspace(JumpPhase.Tunnel, 0f, flight, HullVisuals.TunnelSeconds(hull, flight), clock.Left);
            var shortLight = HullVisuals.Hyperspace(JumpPhase.Tunnel, (float)((basis - left) / basis), flight);
            Assert.True(MathF.Abs(shortLight.Offset - light.Offset) < 0.05f, $"{left} s left: {light.Offset}, {shortLight.Offset} for a short transit");
            Assert.Equal(shortLight.Visible, light.Visible);
        }

        // Through the arrival the transit's length no longer matters at all.
        var arriving = new HullView { Jump = JumpPhase.Arrive, JumpTimer = 0.5f, JumpTunnelSeconds = (float)tunnel };
        var arrival = HullVisuals.TransitClock(arriving, flight);
        Assert.Equal(-0.5f, arrival.Left);
        Assert.Equal(HullVisuals.Passage(JumpPhase.Arrive, 0.5f / flight.FtlArriveSeconds, flight).Offset,
            HullVisuals.Passage(JumpPhase.Arrive, 0.5f / flight.FtlArriveSeconds, flight, (float)tunnel, arrival.Left).Offset, 3);
    }
}
