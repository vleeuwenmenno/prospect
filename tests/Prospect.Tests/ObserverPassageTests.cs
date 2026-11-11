using System.Diagnostics;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Another hull's jump through a rift or a wormhole as an observer draws it: from the stage and progress the server
/// sends, the hull flies into the opening ahead of its nose, which closes behind it before the drive fires, and at
/// the far end it comes out of an opening behind it and slows to where the server has it.
/// </summary>
public sealed class ObserverPassageTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static FlightParams Flight(FtlKind drive)
    {
        foreach (var hull in Content.Designs)
        {
            if (hull.Flight.Ftl == drive)
                return hull.Flight;
        }

        throw new InvalidOperationException($"no design with a {drive} drive");
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    public void AHullFliesIntoItsOpeningAndIsGoneBeforeTheDriveFires(FtlKind drive)
    {
        var flight = Flight(drive);
        var radius = flight.Radius;
        var previous = HullVisuals.Passage(JumpPhase.Align, 0f, flight);
        Assert.Equal(0f, previous.Offset);
        Assert.True(previous.Visible && previous.Clip > 0f);
        for (var i = 0; i <= 100; i++)
        {
            var shape = HullVisuals.Passage(JumpPhase.Charge, i / 100f, flight);
            // The opening stays where it opened; the hull only moves forward, toward it and through it.
            Assert.Equal(previous.Mouth, shape.Mouth);
            Assert.True(shape.Offset >= previous.Offset);
            if (i <= 60)
                Assert.Equal(0f, shape.Offset);
            // Drawn only until its tail is through, and the opening stays open while it passes.
            Assert.Equal(shape.Offset - radius * 1.5f < shape.Mouth, shape.Visible);
            if (shape.Visible && shape.Offset + radius > shape.Mouth)
                Assert.True(shape.Open > 0.5f, $"open {shape.Open} with the hull passing at {shape.Offset}");
            previous = shape;
        }

        // Fully through and closed when the drive fires: nothing is left to vanish in one frame.
        Assert.False(previous.Visible);
        Assert.True(previous.Open < 0.01f, $"open {previous.Open} when the drive fires");
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    public void AHullComesOutOfAnOpeningBehindItAndStopsWhereTheServerHasIt(FtlKind drive)
    {
        var flight = Flight(drive);
        var radius = flight.Radius;
        var first = true;
        var previous = HullVisuals.Passage(JumpPhase.Tunnel, 0f, flight);
        Assert.False(previous.Visible);
        Assert.True(previous.Clip < 0f);
        foreach (var phase in new[] { JumpPhase.Tunnel, JumpPhase.Arrive })
        {
            for (var i = 0; i <= 100; i++)
            {
                var shape = HullVisuals.Passage(phase, i / 100f, flight);
                Assert.True(shape.Mouth < 0f, "the opening is behind the hull");
                Assert.True(shape.Offset >= previous.Offset);
                if (shape.Visible && first)
                {
                    // It appears wholly inside the space the opening hides, so it comes out of it rather than popping.
                    first = false;
                    Assert.True(shape.Offset + radius <= shape.Mouth, $"appears at {shape.Offset}, opening at {shape.Mouth}");
                    Assert.True(shape.Open > 0.5f);
                }

                previous = shape;
            }
        }

        Assert.False(first);
        Assert.Equal(0f, previous.Offset, 3);
        Assert.True(previous.Open < 0.01f);
    }

    [Fact]
    public void AHullIsOnlyThroughItsOpeningOnceItsDriveHasBothChargedAndCalculated()
    {
        var flight = Flight(FtlKind.Rift);
        Assert.True(flight.FtlCalculateSeconds > 0f);

        // Charged but half calculated: the drive is half ready, and the hull has not gone through.
        var hull = new HullView { Jump = JumpPhase.Charge, JumpTimer = flight.FtlEnergy, JumpCalculation = flight.FtlCalculateSeconds * 0.5f };
        Assert.Equal(1f, WorldView.ChargeFraction(hull, flight));
        Assert.Equal(0.5f, WorldView.JumpProgress(hull, flight), 3);
        var shape = HullVisuals.Passage(hull.Jump, WorldView.JumpProgress(hull, flight), flight);
        Assert.True(shape.Visible && shape.Offset == 0f);

        // The other way round, the same.
        hull = new HullView { Jump = JumpPhase.Charge, JumpTimer = flight.FtlEnergy * 0.5f, JumpCalculation = flight.FtlCalculateSeconds };
        Assert.Equal(1f, WorldView.CalculationFraction(hull, flight));
        Assert.Equal(0.5f, WorldView.JumpProgress(hull, flight), 3);

        // A drive that needs no time to calculate is ready once charged.
        var instant = flight;
        instant.FtlCalculateSeconds = 0f;
        hull = new HullView { Jump = JumpPhase.Charge, JumpTimer = flight.FtlEnergy, JumpCalculation = 0f };
        Assert.Equal(1f, WorldView.CalculationFraction(hull, instant));
        Assert.Equal(1f, WorldView.JumpProgress(hull, instant));
    }

    [Fact]
    public void EveryObserverSeesAnotherPlayersHullJumpTheSameWayEvenOneWhoJoinsMidJump()
    {
        // A real game over UDP: the host jumps one of its ships, a second player watches, a third joins mid-jump.
        var port = TestNet.Port(11);
        // Charging as fast as a world may let it keeps the test short; the passage is drawn from progress alone.
        var settings = Content.SettingCatalog.Defaults;
        settings.Set(Content.SettingCatalog.Find("jumpChargeSpeed")!, 4f);
        using var host = GameSession.StartLocal(new ServerConfig { MaxPlayers = 3, Visibility = ServerVisibility.Unlisted, Port = port, Settings = settings }, Content, "Jumper");
        var hosting = TestNet.WaitFor(host, s => s.State == SessionState.Active);
        using var watcher = GameSession.Join("127.0.0.1", port, Content, "Watcher");
        uint ship = 0;
        TestNet.WaitFor(watcher, s =>
        {
            for (var i = 0; i < s.HullCount; i++)
            {
                ref readonly var hull = ref s.Hulls[i];
                if (hull.Owner == hosting.PlayerId && Content.Designs[hull.Design].Flight.Ftl == FtlKind.Rift)
                    ship = hull.Id;
            }

            return s.State == SessionState.Active && ship != 0;
        });

        var galaxy = hosting.Galaxy!;
        var target = galaxy.CenterOf(galaxy.SectorOf(galaxy.CenterOf(new SectorCoord(0, 0, 0))).Offset(1, 0, 0));
        host.Client.Input.IssueFleetOrder(ship, new HullOrder { Kind = OrderKind.Jump, Target = target });

        GameSession? latecomer = null;
        try
        {
            var phases = new HashSet<JumpPhase>();
            var lastOffset = 0f;
            var lastPhase = JumpPhase.None;
            var lateProgress = -1f;
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(5);
                Assert.Null(watcher.Fault);
                if (!TryFind(watcher.Client.AcquireSnapshot(), ship, out var seen))
                    continue;
                var flight = Content.Designs[seen.Design].Flight;
                phases.Add(seen.Jump);
                if (seen.Jump == JumpPhase.None && lastPhase == JumpPhase.Arrive)
                    break;
                if (seen.Jump == JumpPhase.None)
                    continue;

                // What the watcher draws follows the server's stage: forward through the charge, then out of the far
                // opening; it never steps backward within a stage.
                var progress = WorldView.JumpProgress(seen, flight);
                var shape = HullVisuals.Passage(seen.Jump, progress, flight);
                if (seen.Jump == lastPhase)
                    Assert.True(shape.Offset >= lastOffset - 1e-3f, $"{seen.Jump}: {shape.Offset} after {lastOffset}");
                lastOffset = shape.Offset;
                lastPhase = seen.Jump;

                // Halfway through the charge a third player joins. From its first sight of the ship it draws the jump
                // where it is, not from its start.
                if (seen.Jump == JumpPhase.Charge && latecomer is null && progress > 0.5f)
                    latecomer = GameSession.Join("127.0.0.1", port, Content, "Latecomer");
                if (latecomer is not null && lateProgress < 0f && TryFind(latecomer.Client.AcquireSnapshot(), ship, out var late) && late.Jump == JumpPhase.Charge)
                    lateProgress = WorldView.JumpProgress(late, flight);
            }

            Assert.True(phases.Contains(JumpPhase.Arrive), string.Join(", ", phases));
            Assert.Contains(JumpPhase.Charge, phases);
            Assert.Contains(JumpPhase.Tunnel, phases);
            Assert.True(lateProgress > 0.5f, $"the latecomer first saw the charge at {lateProgress}");
            Assert.True(TryFind(watcher.Client.AcquireSnapshot(), ship, out var arrived));
            Assert.True(arrived.Position.RelativeTo(target).Length() < 1f);
        }
        finally
        {
            latecomer?.Dispose();
        }
    }

    private static bool TryFind(SessionSnapshot snapshot, uint id, out HullView hull)
    {
        for (var i = 0; i < snapshot.HullCount; i++)
        {
            if (snapshot.Hulls[i].Id != id)
                continue;
            hull = snapshot.Hulls[i];
            return true;
        }

        hull = default;
        return false;
    }
}
