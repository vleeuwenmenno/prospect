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
            Blocks = Content.Designs[design].FullMask,
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
        var full = Content.Designs[design].FullMask;
        var visuals = new HullVisuals();

        // Late in the charge the hull goes into its opening: find a moment it is partly through.
        var progress = 0.6f;
        for (; progress < 1f; progress += 0.002f)
        {
            Update(visuals, [Hull(1, design, JumpPhase.Charge, progress)]);
            var through = visuals.Hulls[0].PickBlocks;
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
        Assert.Equal(full, beside.PickBlocks);
        Assert.True(beside.Visible);

        // Each block can be picked exactly when its centre is on the shown side of the plane the mesh pass cuts at.
        var slot = visuals.Passages[jumping.Clip];
        var blocks = Content.Designs[design].BlockOffsets;
        var camera = jumping.Position + new Double3(2000, 0, 0);
        var picks = new PickHit[4];
        for (var b = 0; b < blocks.Length; b++)
        {
            var centre = jumping.Position + Vector3.Transform(blocks[b], jumping.Orientation);
            var shown = Vector3.Dot(centre.RelativeTo(slot.Mouth), slot.Axis * slot.Clip) <= 0f;
            Assert.Equal(shown, (jumping.PickBlocks & 1UL << b) != 0);

            // Picked through the same picker the world uses, with nothing in the way: a cut block is not a target.
            var ray = Vector3.Normalize(centre.RelativeTo(camera));
            var hull = new PickHull(1, jumping.Position, jumping.Orientation, blocks, Content.Designs[design].BlockSize, jumping.PickBlocks, flight.Radius);
            var hits = TargetPicking.Pick(ray, camera, ray, 1e6f, [hull], [], picks);
            // Seen from the side, the blocks along the ray are as far along the axis as this one, cut or shown alike.
            Assert.Equal(shown ? 1 : 0, hits);
        }
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
                Assert.False(visual.Inside);
                var drawn = world.DrawnPosition(seen);
                Assert.Equal(visual.Position, drawn);

                // The pointer on where the hull is drawn this frame, then the same frame again with it there.
                if (!world.Project(drawn, out var at))
                    continue;
                devices.PlacePointer(at);
                Step(false);
                if (visual.Visible && visual.PickBlocks == seen.Blocks)
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
}
