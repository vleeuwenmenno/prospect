using System.Numerics;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The mining laser through a real server and real clients ticked by hand: who may fire, when a beam is refused,
/// what a beam cuts and the ore it frees. Every refusal is held against a control that fires with the one condition
/// changed, so a test that sees nothing happen has seen the condition and not a broken setup.
/// </summary>
public sealed class MiningLaserTests
{
    private static readonly Vector3 Up = Vector3.UnitY;

    /// <summary>A direction leaning <paramref name="degrees"/> from straight up toward +X (or -X when negative).</summary>
    private static Vector3 Lean(float degrees)
    {
        var angle = degrees * MathF.PI / 180f;
        return new Vector3(MathF.Sin(angle), MathF.Cos(angle), 0);
    }

    /// <summary>
    /// Ticks a beam held still takes to make its first scrape: the bite it must fill, at the rate it cuts. Read from the
    /// content, since the two figures have been retuned more than once.
    /// </summary>
    private static readonly int FirstScrape = (int)MathF.Ceiling(MiningFixtures.Content.Mining.BiteMetres
        / MiningFixtures.Content.Blocks.Get("miningLaser").Laser!.CutRate / HullSim.Dt);

    /// <summary>Whether the aim cut <paramref name="rock"/> in the next <paramref name="ticks"/> ticks, with the aim kept up.</summary>
    private static bool Cuts(MiningRig rig, RockRef rock, ushort groups, Vector3 direction, int ticks = 0)
    {
        if (ticks <= 0) ticks = FirstScrape + 60;
        var before = rig.Edits(rock);
        rig.Aim(0, groups, direction);
        rig.Step(ticks);
        return rig.Edits(rock) > before;
    }

    private static void Stop(MiningRig rig)
    {
        rig.Aim(0, 0, Up);
        rig.Step(4);
    }

    private static LaserBeamView[] Beams(MiningRig rig)
    {
        var s = rig.Snap(0);
        return s.LaserBeams.AsSpan(0, s.LaserBeamCount).ToArray();
    }

    // ---------------------------------------------------------------- firing

    [Fact]
    public void ALaserInAnActiveGroupCutsTheAimedRockAndASweepingBeamChainsItsScrapesIntoOneStroke()
    {
        using var rig = new MiningRig();
        var (rock, asteroid) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        Assert.Equal(0, rig.Edits(rock));
        var design = rig.DesignOf();
        var a = MiningFixtures.PartA(design);

        // The aim sweeps along the surface at about two metres a second, so each scrape is fresh from the last.
        for (var t = 0; t < 240; t++)
        {
            var angle = t * 0.00133f;
            rig.Aim(0, MiningFixtures.Mask(1), Vector3.Normalize(Up + Vector3.UnitZ * angle));
            rig.Step();
            if (t == 100) Assert.Contains(Beams(rig), b => b is { State: LaserBeamState.Cutting } && b.Part == a && b.Hull == rig.Own());
        }

        var edits = MiningFixtures.EditList(rig.Server, rock);
        Assert.True(edits.Count >= 6, $"only {edits.Count} scrapes in four seconds of cutting");
        for (var i = 0; i < edits.Count; i++)
        {
            Assert.True(rig.Content.RockEdits.Valid(edits[i], asteroid.Radius));
            Assert.Equal((int)(design.Mounts[design.Lasers[a]].Type.Laser!.CutRadius * 1000), edits[i].RadiusMm);
            if (i > 0) Assert.True(edits[i].StartsAtEndOf(edits[i - 1]), $"scrape {i} does not start where scrape {i - 1} ended");
        }

        // Only that rock was cut, and the cut is in the rock where the beam met it: the surface straight above the hull.
        Assert.Equal(edits.Count, rig.Server.RockEdits.Log.Count);
        var first = edits[0];
        var at = new Vector3(first.Bx, first.By, first.Bz) / RockScrape.Units;
        Assert.True(rig.Server.RockEdits.CutOf(rock).Sample(at, out _) < 0, "the first scrape is a cut in the rock");
    }

    [Fact]
    public void ABeamHeldStillOnOneSpotKeepsOneStrokeWhileItStaysOnTheRock()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        for (var t = 0; t < FirstScrape * 4; t++)
        {
            rig.Aim(0, MiningFixtures.Mask(1), Up);
            rig.Step();
        }

        // A scrape takes longer than the stroke gap, but the beam never left the rock: every scrape joins the last.
        var edits = MiningFixtures.EditList(rig.Server, rock);
        Assert.True(edits.Count >= 3, $"only {edits.Count} scrapes");
        for (var i = 1; i < edits.Count; i++)
            Assert.True(edits[i].StartsAtEndOf(edits[i - 1]), $"scrape {i} starts a new stroke");
    }

    [Fact]
    public void ABeamThatStaysWhereItIsDeepensOneSpotAndLettingGoOfTheGroupsStopsIt()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(1), Up, FirstScrape * 4 + 5));
        var edits = MiningFixtures.EditList(rig.Server, rock);
        Assert.InRange(edits.Count, 3, 8);

        // Each scrape is a point cut at the same place: well within the cut's own radius of the one before.
        for (var i = 1; i < edits.Count; i++)
        {
            var d = new Vector3(edits[i].Bx - edits[i - 1].Bx, edits[i].By - edits[i - 1].By, edits[i].Bz - edits[i - 1].Bz) / RockScrape.Units;
            Assert.True(d.Length() < rig.Content.Mining.BiteMetres + 0.1f, $"scrape {i} is {d.Length()} m from the last");
        }

        Stop(rig);
        var count = rig.Server.RockEdits.Log.Count;
        rig.Step(300);
        Assert.Equal(count, rig.Server.RockEdits.Log.Count);
        Assert.Empty(Beams(rig));

        // And it starts again from the next aim message.
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(1), Up));
    }

    [Fact]
    public void BothLasersOfTwoActiveGroupsFireAndAnInactiveOrUngroupedOneDoesNot()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        var design = rig.DesignOf();
        var (a, b, c) = (MiningFixtures.PartA(design), MiningFixtures.PartB(design), MiningFixtures.PartC(design));

        rig.Aim(0, MiningFixtures.Mask(1, 2), Up);
        rig.Step(20);
        Assert.Equal(new[] { a, b }.OrderBy(x => x), Beams(rig).Select(x => (int)x.Part).OrderBy(x => x));

        // Group 2 only: B alone. Every group on: A and B, never C, which is in none.
        rig.Aim(0, MiningFixtures.Mask(2), Up);
        rig.Step(20);
        Assert.Equal([b], Beams(rig).Select(x => (int)x.Part));
        rig.Aim(0, ActionGroups.AllMask, Up);
        rig.Step(20);
        Assert.Equal(new[] { a, b }.OrderBy(x => x), Beams(rig).Select(x => (int)x.Part).OrderBy(x => x));
        Assert.DoesNotContain(Beams(rig), x => x.Part == c);

        // A group nobody is in fires nothing, and the beams end at once.
        rig.Aim(0, MiningFixtures.Mask(3, 4, 5, 6, 7, 8, 9, 10), Up);
        rig.Step(10);
        Assert.Empty(Beams(rig));
        var count = rig.Server.RockEdits.Log.Count;
        rig.Step(150);
        Assert.Equal(count, rig.Server.RockEdits.Log.Count);
    }

    [Fact]
    public void NoGroupSetOnAnyLaserMeansNothingFiresWhateverTheMask()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        var design = rig.DesignOf();
        Assert.True(Cuts(rig, rock, ActionGroups.AllMask, Up), "control: the groups fire");
        Stop(rig);

        var hull = rig.Own();
        foreach (var part in new[] { MiningFixtures.PartA(design), MiningFixtures.PartB(design) })
            rig.Input().SetActionGroup(hull, part, ActionGroups.None);
        rig.Step(10);
        Assert.Equal(0, rig.Server.GroupOf(rig.Slot(), design, MiningFixtures.PartA(design)));
        Assert.False(Cuts(rig, rock, ActionGroups.AllMask, Up));
        Assert.Empty(Beams(rig));
    }

    [Fact]
    public void ALaserDoesNotFireFromAnotherPlayersHullOrForAHullThePlayerDoesNotFly()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(1), Up), "control: the owner fires");
        Stop(rig);

        // The other player names this hull: refused, and the beam does not start.
        var count = rig.Server.RockEdits.Log.Count;
        rig.Input(1).AimLasers(rig.Own(0), MiningFixtures.Mask(1), Vector3.Zero, Up);
        rig.Step(FirstScrape + 80);
        Assert.Equal(count, rig.Server.RockEdits.Log.Count);
        Assert.Empty(Beams(rig));

        // The owner naming a hull they do not fly (one of the rest of their fleet, or one that is not there).
        var other = Enumerable.Range(0, rig.Hulls.Slots).Where(i => rig.Hulls.IsAliveAt(i) && i != rig.Slot(0) && rig.Hulls.Info(i).Owner == rig.Hulls.Info(rig.Slot(0)).Owner).ToArray();
        Assert.NotEmpty(other);
        rig.Input(0).AimLasers(rig.Hulls.HandleAt(other[0]).Packed, MiningFixtures.Mask(1), Vector3.Zero, Up);
        rig.Step(FirstScrape + 80);
        Assert.Equal(count, rig.Server.RockEdits.Log.Count);
        rig.Input(0).AimLasers(0x7FFF_0000u | 9, MiningFixtures.Mask(1), Vector3.Zero, Up);
        rig.Step(FirstScrape + 80);
        Assert.Equal(count, rig.Server.RockEdits.Log.Count);

        // A camera farther from the hull than the content allows is refused too, and it cuts any aim that was running.
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(1), Up), "control: the owner fires again");
        var far = rig.Content.Mining.MaxAimOffsetMetres + 1;
        rig.Input(0).AimLasers(rig.Own(0), MiningFixtures.Mask(1), new Vector3(0, -far, 0), Up);
        rig.Step(10);
        count = rig.Server.RockEdits.Log.Count;
        rig.Step(FirstScrape + 80);
        Assert.Equal(count, rig.Server.RockEdits.Log.Count);
    }

    [Fact]
    public void AHullInTransitDoesNotFireAndOneThatArrivesCanAgain()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(1), Up), "control: the hull fires out of transit");

        // Each tick the hull is put back into the phase, as the server keeps it only for the phases a jump order holds.
        foreach (var phase in new[] { JumpPhase.Align, JumpPhase.Charge, JumpPhase.Tunnel, JumpPhase.Arrive })
        {
            var count = rig.Server.RockEdits.Log.Count;
            for (var t = 0; t < FirstScrape + 40; t++)
            {
                rig.Hulls.State(rig.Slot()).Jump = phase;
                rig.Step();
            }

            Assert.Equal(count, rig.Server.RockEdits.Log.Count);
            Assert.Empty(Beams(rig));
        }

        // The phases moved the hull (an arrival puts it somewhere); out of transit and back at the rock it fires again.
        rig.Hulls.State(rig.Slot()).Jump = JumpPhase.None;
        rig.Place(0, rock, Up, 25);
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(1), Up));
    }

    [Fact]
    public void ABeamAimedBehindItsFaceOrBeyondItsGimbalLimitIsNotFiredAndOneWithinItIs()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);

        // Control: aimed from below, straight out of the face, and 30 degrees off it.
        rig.Place(0, rock, Up, 25);
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(2), Up), "control: straight out of the face");
        Stop(rig);
        rig.Place(0, rock, Lean(-30), 25);
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(2), Lean(-30)), "within the gimbal limit");
        Stop(rig);

        // The rock lies behind the faces (the laser faces up, the rock is below), and to the side, 90 degrees off.
        foreach (var direction in new[] { -Up, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, Lean(50), Lean(-60) })
        {
            rig.Place(0, rock, direction, 25);
            Assert.False(Cuts(rig, rock, MiningFixtures.Mask(1, 2), direction), $"aimed along {direction}");
            Assert.Empty(Beams(rig));
            Stop(rig);
        }
    }

    [Fact]
    public void ARockBeyondTheLasersRangeIsNotCutAndTheBeamEndsInSpaceAtItsRange()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        var range = rig.Content.Blocks.Get("miningLaser").Laser!.Range;
        rig.Place(0, rock, Up, 40);
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(2), Up), "control: inside the range");
        Stop(rig);

        // Twice the range away: the aim finds no rock within what the lasers reach, so the beams go out to their range and cut nothing.
        rig.Place(0, rock, Up, range * 2);
        Assert.False(Cuts(rig, rock, MiningFixtures.Mask(1, 2), Up));
        var beams = Beams(rig);
        Assert.NotEmpty(beams);
        Assert.All(beams, beam => Assert.Equal(LaserBeamState.Firing, beam.State));
        Assert.All(beams, beam => Assert.InRange(beam.End.Length(), range - 6, range + 6));

        // Just beyond it too.
        rig.Place(0, rock, Up, range + 8);
        Assert.False(Cuts(rig, rock, MiningFixtures.Mask(1, 2), Up));
        Stop(rig);
    }

    [Fact]
    public void ALaserNeedsPowerAndStopsWhenTheBatteryIsFlatAndTheGeneratorCannotPayForIt()
    {
        // Each laser asks 20 power, far more than the pod's 6 of generation: only the battery can pay for it.
        using var rig = new MiningRig(MiningFixtures.Make(blocks: root => root["blocks"]!["miningLaser"]!["laser"]!["power"] = 20));
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        rig.Hulls.State(rig.Slot()).Energy = 60;
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(2), Up), "control: a full battery pays for the beam");
        Assert.NotEmpty(Beams(rig));
        Assert.True(rig.Hulls.State(rig.Slot()).Energy < 60, "the beam draws from the battery");
        Assert.Equal(20f, rig.Hulls.Info(rig.Slot()).Power.Laser);

        rig.Hulls.State(rig.Slot()).Energy = 0;
        rig.Step(2);
        var count = rig.Server.RockEdits.Log.Count;

        // The first tick the battery cannot pay shuts the laser off, and it waits: what the generator makes beyond the
        // pod's own draw goes into the battery, and no tick fires on that trickle. Nothing is cut.
        var paid = 0;
        var floor = rig.Hulls.Flight(rig.Slot()).LaserEnergyFloor;
        for (var t = 0; t < FirstScrape; t++)
        {
            rig.Step();
            if (rig.Hulls.Info(rig.Slot()).Power.Laser > 0) paid++;
            Assert.True(rig.Hulls.State(rig.Slot()).Energy < floor, "the battery has not yet reached the floor the laser waits for");
        }

        Assert.Equal(0, paid);
        Assert.True(rig.Hulls.Info(rig.Slot()).Lasers.AwaitingPower, "the laser waits for power");
        Assert.Empty(Beams(rig));
        Assert.Equal(count, rig.Server.RockEdits.Log.Count);

        // Charge in the battery and the very next ticks fire it again.
        rig.Hulls.State(rig.Slot()).Energy = 60;
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(2), Up), "a charged battery fires it again");
    }

    [Fact]
    public void AnEmptyBatteryStillFiresALaserThatTheGeneratorCanPayFor()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        rig.Hulls.State(rig.Slot()).Energy = 0;
        var power = rig.Content.Blocks.Get("miningLaser").Laser!.Power;
        Assert.True(rig.Hulls.Flight(rig.Slot()).PowerGenerated > power + rig.Hulls.Flight(rig.Slot()).IdlePower, "the pod's generator covers one laser");
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(2), Up));
        Assert.Equal(power, rig.Hulls.Info(rig.Slot()).Power.Laser);
    }

    [Fact]
    public void ALaserThatFiresDrawsItsPowerAndNothingWhenIdle()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        rig.Step(10);
        Assert.Equal(0f, rig.Hulls.Info(rig.Slot()).Power.Laser);
        var power = rig.Content.Blocks.Get("miningLaser").Laser!.Power;
        rig.Aim(0, MiningFixtures.Mask(1, 2), Up);
        rig.Step(10);
        Assert.Equal(2 * power, rig.Hulls.Info(rig.Slot()).Power.Laser, 3);
        Assert.Equal(2 * power, rig.Hulls.Info(rig.Slot()).LaserDemand, 3);
        Stop(rig);
        Assert.Equal(0f, rig.Hulls.Info(rig.Slot()).Power.Laser);
        Assert.Equal(0f, rig.Hulls.Info(rig.Slot()).LaserDemand);
    }

    [Fact]
    public void TheHullsOwnBlocksInTheWayBlockABeamAndTheOtherSideIsClear()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);

        // Laser A (group 1) sits next to a wall of plating that stands up beside it on the +X side.
        rig.Place(0, rock, Lean(-30), 25);
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(1), Lean(-30)), "control: leaning away from the wall");
        Stop(rig);
        rig.Place(0, rock, Lean(30), 25);
        Assert.False(Cuts(rig, rock, MiningFixtures.Mask(1), Lean(30)), "leaning into the wall");
        Assert.Empty(Beams(rig));
        Stop(rig);

        // Laser B is at the other end of the hull; the wall is not in its way.
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(2), Lean(30)), "the other laser has a clear line");
    }

    [Fact]
    public void ATurnedHullFiresFromWhereItsLasersNowFaceAndNotFromWhereTheyFacedBefore()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);

        // Turned a quarter about the nose axis, the lasers face +X; turned half a turn about X, they face down.
        var cases = new (Quaternion Turn, Vector3 Faces)[]
        {
            (Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2), Vector3.UnitX),
            (Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI), -Vector3.UnitY),
            (Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), Vector3.UnitZ),
        };
        foreach (var (turn, faces) in cases)
        {
            rig.Place(0, rock, faces, 25, turn);
            var held = rig.Hulls.State(rig.Slot()).Orientation;
            Assert.True(MathF.Abs(Quaternion.Dot(held, turn)) > 0.999f, "the test's turn is what the hull holds");
            Assert.True(Cuts(rig, rock, MiningFixtures.Mask(1, 2), faces), $"the lasers face {faces}");
            Assert.True(MathF.Abs(Quaternion.Dot(rig.Hulls.State(rig.Slot()).Orientation, turn)) > 0.999f, "and the hull stayed turned while it cut");
            Stop(rig);

            // Aimed from where the lasers faced before the turn, they do not fire.
            var behind = -faces;
            rig.Place(0, rock, behind, 25, turn);
            Assert.False(Cuts(rig, rock, MiningFixtures.Mask(1, 2), behind), $"aimed along {behind}, behind the faces");
            Stop(rig);
        }
    }

    [Fact]
    public void TheAimRayStartsAtTheCameraNotAtTheHull()
    {
        using var rig = new MiningRig();
        var (rock, asteroid) = MiningFixtures.FindRock(rig.Rocks, 40f);
        rig.Place(0, rock, Up, 25);
        var hull = rig.Hulls.State(rig.Slot()).Position;
        var centre = rig.Content.Galaxy.CenterOf(rock.Sector) + asteroid.LocalPosition;

        // A camera 12 m to the side of the hull looking straight up meets the rock 12 m off the hull's own line.
        var offset = new Vector3(12, -3, 0);
        rig.Input(0).AimLasers(rig.Own(0), MiningFixtures.Mask(2), offset, Up);
        rig.Step(FirstScrape + 60);
        var edits = MiningFixtures.EditList(rig.Server, rock);
        Assert.NotEmpty(edits);
        var cut = centre + Vector3.Transform(edits[0].B, asteroid.Rotation);
        Assert.InRange(cut.RelativeTo(hull).X, 9, 15);
        Assert.InRange(MathF.Abs(cut.RelativeTo(hull).Z), 0, 4);

        // And the same aim from the hull's own place meets it above the hull.
        Stop(rig);
        rig.Aim(0, MiningFixtures.Mask(2), Up);
        rig.Step(FirstScrape + 60);
        var again = MiningFixtures.EditList(rig.Server, rock);
        var onAxis = centre + Vector3.Transform(again[^1].B, asteroid.Rotation);
        Assert.InRange(MathF.Abs(onAxis.RelativeTo(hull).X), 0, 4);
    }

    [Fact]
    public void ARockThatIsFullOfEditsTakesNoMoreAndTheBeamKeepsShowingWithNothingComingOff()
    {
        using var rig = new MiningRig(MiningFixtures.Make(other: copy => copy
            .Replace("rules/rock_edits.json", "\"maxEditsPerRock\": 2048", "\"maxEditsPerRock\": 6")
            // A mark small enough that the six scrapes the rock allows break ore off it, so the rock, not the ore, is
            // what stops the beam.
            .Replace("rules/mining.json", "\"oreKg\": 400", "\"oreKg\": 20")));
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        for (var t = 0; t < 600; t++)
        {
            rig.Aim(0, MiningFixtures.Mask(1, 2), Vector3.Normalize(Up + Vector3.UnitZ * (t * 0.0006f)));
            rig.Step();
        }

        Assert.Equal(6, rig.Edits(rock));
        var chunks = rig.Server.OreChunks.NextId;
        Assert.True(chunks > 1, "the first six scrapes broke chunks off");
        Assert.NotEmpty(Beams(rig));
        Assert.All(Beams(rig), beam => Assert.Equal(LaserBeamState.Cutting, beam.State));
        for (var t = 0; t < 300; t++)
        {
            rig.Aim(0, MiningFixtures.Mask(1, 2), Vector3.Normalize(Up + Vector3.UnitZ * (0.36f + t * 0.0006f)));
            rig.Step();
        }

        Assert.Equal(6, rig.Edits(rock));
        Assert.Equal(chunks, rig.Server.OreChunks.NextId);
        Assert.NotEmpty(Beams(rig));
    }

    [Fact]
    public void NoMoreLasersFireTogetherThanTheContentAllowsAHull()
    {
        using var rig = new MiningRig(MiningFixtures.Make(other: copy => copy.Replace("rules/mining.json", "\"maxPerHull\": 16", "\"maxPerHull\": 1")));
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        rig.Aim(0, ActionGroups.AllMask, Up);
        rig.Step(20);
        Assert.Equal(0, Assert.Single(Beams(rig)).Part);
    }

    [Fact]
    public void ALaserWhoseBlockIsLostStopsFiringAndTheOthersCarryOn()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        var design = rig.DesignOf();
        var (a, b) = (MiningFixtures.PartA(design), MiningFixtures.PartB(design));
        rig.Aim(0, MiningFixtures.Mask(1, 2), Up);
        rig.Step(20);
        Assert.Equal(new[] { a, b }.OrderBy(p => p), Beams(rig).Select(x => (int)x.Part).OrderBy(p => p));

        // The plating that carries laser A goes; the part goes with it, with no state of its own to leave behind.
        var blocks = rig.Hulls.Info(rig.Slot()).BlockState;
        Assert.True(rig.Hulls.BlockStates.Lose(blocks, design.Mounts[design.Lasers[a]].Parent));
        rig.Step(20);
        Assert.Equal([b], Beams(rig).Select(x => (int)x.Part));
        Assert.Equal(design.Mounts[design.Lasers[b]].Type.Laser!.Power, rig.Hulls.Info(rig.Slot()).Power.Laser);
        Assert.True(Cuts(rig, rock, MiningFixtures.Mask(1, 2), Up));
    }

    [Fact]
    public void GroupTenIsTheKeyZeroAndFiresLikeAnyOther()
    {
        using var rig = new MiningRig();
        var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
        rig.Place(0, rock, Up, 25);
        var design = rig.DesignOf();
        var a = MiningFixtures.PartA(design);
        rig.Input().SetActionGroup(rig.Own(), a, 10);
        rig.Step(10);
        Assert.Equal(512, ActionGroups.Bit(10));
        rig.Aim(0, MiningFixtures.Mask(10), Up);
        rig.Step(20);
        Assert.Equal([a], Beams(rig).Select(x => (int)x.Part));
        rig.Aim(0, (ushort)(ActionGroups.AllMask & ~512), Up);
        rig.Step(20);
        Assert.DoesNotContain(a, Beams(rig).Select(x => (int)x.Part));
    }

    [Fact]
    public void WhatAClientMayBeSentInOneTickStillFitsTheServersAllowanceWithTheLaserCommands()
    {
        var bytes = ClientSession.SocialMessagesPerTick * SocialMessage.MaxWireSize + FleetOrdersMessage.MaxWireSize + 5 + ClientSession.EditorBytesPerTick
            + ExplorationMessage.ScanWireSize + ModuleMessage.RequestWireSize + JettisonCargoMessage.MaxWireSize + DroneCommandMessage.MaxWireSize
            + LaserAimMessage.MaxWireSize + SetActionGroupMessage.MaxWireSize;
        Assert.True(bytes <= ServerHost.PlayerBytesPerTick, $"{bytes} bytes against an allowance of {ServerHost.PlayerBytesPerTick}");
    }
}
