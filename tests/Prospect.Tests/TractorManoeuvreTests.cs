using System.Numerics;
using Prospect.Engine.Time;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Pickup while the ship manoeuvres: the shipped ship drifts, thrusts and stops, turns or is nudged about while a
/// loose container or an ore chunk is in its beams' reach, and the body must come in as it does with the ship at
/// rest. Ticks run in the server's order: plan, hull step, pull, body step.
/// Before the beam carried its load along with the ship (<see cref="Approach.Carry"/>) and measured a stall by what
/// the body has left to do (<see cref="Approach.Left"/>), a pilot nudging the ship about while mining got 2 chunks of
/// 7 into the hopper with 7 stall releases, where a ship at rest got 13 of 13 with none.
/// </summary>
public sealed class TractorManoeuvreTests(ITestOutputHelper output)
{
    private static readonly GameContent Content = GameContent.Load();
    private const float Hz = TickLoop.SimulationHz;

    /// <summary>What the pilot does in one tick of a scenario.</summary>
    private delegate FlightInput Pilot(long tick);

    /// <param name="Drift">The hull's velocity at the start; a scenario with one flies with the dampeners off.</param>
    private sealed record Motion(string Name, Pilot Pilot, Vector3 Drift = default, bool Dampeners = true);

    private static sbyte Axis(float share) => FlightInput.Quantize(share);

    /// <summary>The input that turns the shipped ship at <paramref name="degrees"/> a second about one axis.</summary>
    private static sbyte Turn(float degrees, float rate) => Axis(degrees * MathF.PI / 180 / rate);

    private static sbyte Yaw(float degrees) => Turn(degrees, Content.Pod.Flight.TurnRate.Y);

    /// <summary>
    /// A new small input every three quarters of a second, a third of them hands off: what a pilot lining up on a
    /// rock does. <paramref name="thrust"/> and <paramref name="turn"/> are the largest shares of the ship's thrust
    /// and turn rate asked.
    /// </summary>
    private static FlightInput Nudge(long tick, uint seed, float thrust = .4f, float turn = .3f)
    {
        var random = ((uint)(tick / 45) + 1) * 2654435761u ^ seed;
        float Next() { random ^= random << 13; random ^= random >> 17; random ^= random << 5; return (random & 0xffff) / 32767.5f - 1; }
        Next();
        if (Next() < -.33f) return default;
        return new FlightInput
        {
            Strafe = Axis(Next() * thrust),
            Vertical = Axis(Next() * thrust),
            Forward = Axis(Next() * thrust),
            Pitch = Axis(Next() * turn),
            Yaw = Axis(Next() * turn),
            Roll = Axis(Next() * turn * .5f),
        };
    }

    /// <summary>What a pilot does who is not trying to shake the load off: drifting, thrusting a little, turning slowly.</summary>
    private static readonly Motion[] Gentle =
    [
        new("at rest", _ => default),
        new("drift 2 m/s sideways", _ => default, new Vector3(2, 0, 0), Dampeners: false),
        new("thrust and stop", tick => new FlightInput { Forward = tick % 180 < 60 ? Axis(.35f) : (sbyte)0 }),
        new("strafe and stop", tick => new FlightInput { Strafe = tick % 240 < 60 ? Axis(.35f) : tick % 240 is >= 120 and < 180 ? Axis(-.35f) : (sbyte)0 }),
        new("yaw 5 deg/s", _ => new FlightInput { Yaw = Yaw(5) }),
        new("yaw 15 deg/s", _ => new FlightInput { Yaw = Yaw(15) }),
        new("pitch 10 deg/s", _ => new FlightInput { Pitch = Turn(10, Content.Pod.Flight.TurnRate.X) }),
        new("yaw 10 deg/s and drift", _ => new FlightInput { Yaw = Yaw(10) }, new Vector3(2, 0, 0), Dampeners: false),
        new("nudging about", tick => Nudge(tick, 7)),
        new("nudging about, again", tick => Nudge(tick, 1234567)),
    ];

    /// <summary>Everything the ship can do: full turn rates, full thrust and boost with the dampeners braking after it.</summary>
    private static readonly Motion[] Hard =
    [
        new("yaw 45 deg/s", _ => new FlightInput { Yaw = Yaw(45) }),
        new("yaw full", _ => new FlightInput { Yaw = 127 }),
        new("pitch full", _ => new FlightInput { Pitch = 127 }),
        new("roll full", _ => new FlightInput { Roll = 127 }),
        new("yaw full, reversing", tick => new FlightInput { Yaw = (sbyte)(tick % 120 < 60 ? 127 : -127) }),
        new("full thrust and stop", tick => new FlightInput { Forward = (sbyte)(tick % 180 < 60 ? 127 : 0) }),
        new("full boost and stop", tick => new FlightInput { Forward = (sbyte)(tick % 180 < 60 ? 127 : 0), Boost = tick % 180 < 60 }),
        new("full strafe, reversing", tick => new FlightInput { Strafe = (sbyte)(tick % 120 < 60 ? 127 : -127) }),
        new("hard nudging", tick => Nudge(tick, 99, 1, .9f)),
    ];

    public static TheoryData<int> GentleMotions() => [.. Enumerable.Range(0, Gentle.Length)];

    public static TheoryData<int> HardMotions() => [.. Enumerable.Range(0, Hard.Length)];

    /// <param name="Stalls">Times the stall rule let the body go.</param>
    /// <param name="Drops">Times the beam let it go for another reason.</param>
    private readonly record struct Outcome(bool Stowed, float Seconds, int Stalls, int Drops, float Held, float Unpowered, string Last)
    {
        public override string ToString() =>
            $"{(Stowed ? "stowed" : "NOT stowed")} after {Seconds:F1} s, stalls {Stalls}, drops {Drops}, held {Held:F1} s, unpowered {Unpowered:F1} s, {Last}";
    }

    private enum Body
    {
        /// <summary>An empty one-metre container at a place in the hull's frame, free to be taken at once.</summary>
        Container,

        /// <summary>The same, full of iron: 7.9 t.</summary>
        IronContainer,

        /// <summary>A container let out of the first rack as the server lets it out, with its pickup delay.</summary>
        Jettisoned,

        /// <summary>The heaviest ore chunk the mining rules make, at a place in the hull's frame.</summary>
        Chunk,
    }

    private static readonly Vector3 AboveTheRacks = new(.5f, 9, 1);
    private static readonly Vector3 FarAbove = new(.5f, 30, 1);
    private static readonly Vector3 AheadOfTheNose = new(.5f, 4, -18);
    private static readonly Vector3 FarAhead = new(.5f, 6, -35);

    /// <summary>
    /// Runs one scenario on the shipped ship for at most <paramref name="seconds"/>. <paramref name="start"/> is where
    /// the body is, in the hull's grid frame, at rest in space. <paramref name="knock"/> is added to the hull's velocity
    /// one second in, in a single tick, as a collision would; <paramref name="battery"/> is the share of its charge the
    /// ship starts with.
    /// </summary>
    private static Outcome Run(Motion motion, Body kind, Vector3 start, float seconds, GameContent? with = null, Vector3 knock = default, float battery = 1)
    {
        var content = with ?? Content;
        var design = content.Pod; using var hulls = new HullStore();
        var origin = content.Galaxy.CenterOf(new(0, 100, 0));
        var h = hulls.Create(1, (byte)content.PodDesign, design, origin);
        hulls.State(h.Index).Velocity = motion.Drift;
        hulls.State(h.Index).Energy *= battery;
        var field = new HullField(hulls, content.Designs);
        var bodies = new FreeContainers(); var chunks = new OreChunks();
        var pivot = (design.CenterOfMass + new Vector3(.5f)) * design.BlockSize;
        const int size = 3;
        var chunk = kind == Body.Chunk;
        if (chunk)
            Assert.True(chunks.Add(content, new OreChunk
            {
                Position = origin + start - pivot,
                Orientation = Quaternion.Identity,
                Resource = 0,
                Grade = .75f,
                Mass = content.Mining.ChunkMaxMassKg,
            }, out _));
        else if (kind == Body.Jettisoned)
        {
            var rack = 0;
            while (design.Boxes[rack].Type.Cargo <= 0) rack++;
            Assert.True(bodies.Add(content, CargoEjection.Make(content.Cargo, design,
                new HoldContainer(rack, size, -1, 0, content.Cargo.Sizes[size].Hull, default),
                hulls.State(h.Index), hulls.Flight(h.Index).CenterOfMass, 0, 0, false), out _));
        }
        else
        {
            var iron = kind == Body.IronContainer;
            Assert.True(bodies.Add(content, new FreeContainer
            {
                Position = origin + start - pivot,
                Orientation = Quaternion.Identity,
                Size = size,
                Resource = iron ? 0 : -1,
                Amount = iron ? 7874 : 0,
                Hull = content.Cargo.Sizes[size].Hull,
            }, out _));
        }

        var tractor = new TractorSimulation(); var scratch = new byte[4096];
        var contact = new ContainerSimulation(content.Galaxy); var ore = new OreChunkSimulation(content.Galaxy);
        var rocks = new RockField(content.Galaxy);
        var stalls = 0; var drops = 0; var held = 0; var unpowered = 0; var was = false;
        var ready = chunk ? chunks.Items[0].RetryTick : bodies.Items[0].PickupReadyTick;
        var ticks = (long)(seconds * Hz);
        for (long tick = 0; tick < ticks; tick++)
        {
            field.BeginRound();
            tractor.Plan(bodies, chunks, content, field, scratch, tick);
            var input = motion.Pilot(tick);
            input.Dampeners = motion.Dampeners;
            field.Step(h.Index, input);
            if (tick == 60) hulls.State(h.Index).Velocity += knock;
            tractor.Pull(bodies, chunks, content, field, scratch, tick);
            contact.Step(bodies, content, field, rocks, tick);
            ore.Step(chunks, content, field, rocks, tick);
            if (chunk ? chunks.Items.IsEmpty : bodies.Items.IsEmpty)
                return new(true, (tick + 1) / Hz, stalls, drops, held / Hz, unpowered / Hz, "home");
            var now = chunk ? chunks.Items[0].TractorHull != 0 : bodies.Items[0].TractorHull != 0;
            var powered = chunk ? chunks.Items[0].TractorPowered : bodies.Items[0].TractorPowered;
            var retry = chunk ? chunks.Items[0].RetryTick : bodies.Items[0].PickupReadyTick;
            if (now) { held++; if (!powered) unpowered++; }
            // The stall rule alone sets the time before which no beam may try again.
            if (was && !now) { if (retry > ready) stalls++; else drops++; }
            ready = retry; was = now;
        }

        var pose = hulls.State(h.Index);
        var position = chunk ? chunks.Items[0].Position : bodies.Items[0].Position;
        var velocity = chunk ? chunks.Items[0].Velocity : bodies.Items[0].Velocity;
        var at = HullRotation.Rotate(position.RelativeTo(pose.Position), Quaternion.Conjugate(pose.Orientation)) + pivot;
        return new(false, seconds, stalls, drops, held / Hz, unpowered / Hz,
            $"held={was} at <{at.X:F1} {at.Y:F1} {at.Z:F1}> relative speed {(velocity - pose.Velocity).Length():F2}");
    }

    [Theory]
    [MemberData(nameof(GentleMotions))]
    public void ABodyComesInAsFastAsAtRestWhileTheShipDriftsThrustsOrTurnsGently(int index)
    {
        var motion = Gentle[index];
        foreach (var (kind, start) in new[] { (Body.Container, AboveTheRacks), (Body.IronContainer, AboveTheRacks), (Body.Chunk, AheadOfTheNose) })
        {
            var outcome = Run(motion, kind, start, 30);
            output.WriteLine($"{motion.Name}, {kind}: {outcome}");
            Assert.True(outcome.Stowed, $"{motion.Name}, {kind}: {outcome}");
            Assert.Equal((0, 0), (outcome.Stalls, outcome.Drops));
            // At rest: 6.6 s for the container, 6.5 s for the chunk. Before the load went along with the ship, the
            // second nudging run never stowed the container (two stall releases, then out of range).
            Assert.True(outcome.Seconds < 9, $"{motion.Name}, {kind}: took {outcome.Seconds:F1} s");
        }
    }

    [Theory]
    [MemberData(nameof(HardMotions))]
    public void ABodyStillComesInWhileTheShipDoesAllItCan(int index)
    {
        var motion = Hard[index];
        foreach (var (kind, start) in new[] { (Body.Container, AboveTheRacks), (Body.Chunk, AheadOfTheNose), (Body.Chunk, FarAhead) })
        {
            var outcome = Run(motion, kind, start, 40);
            output.WriteLine($"{motion.Name}, {kind} at {start}: {outcome}");
            Assert.True(outcome.Stowed, $"{motion.Name}, {kind} at {start}: {outcome}");
            Assert.Equal((0, 0), (outcome.Stalls, outcome.Drops));
            // The worst of them took 11.3 s (a chunk 35 m ahead of a ship swinging from full yaw to full yaw).
            Assert.True(outcome.Seconds < 16, $"{motion.Name}, {kind} at {start}: took {outcome.Seconds:F1} s");
        }
    }

    [Theory]
    [MemberData(nameof(GentleMotions))]
    public void AContainerFarOutIsBroughtInWhileTheShipManoeuvresGently(int index)
    {
        var motion = Gentle[index];
        var outcome = Run(motion, Body.Container, FarAbove, 30);
        output.WriteLine($"{motion.Name}: {outcome}");
        Assert.True(outcome.Stowed, $"{motion.Name}: {outcome}");
        Assert.Equal((0, 0), (outcome.Stalls, outcome.Drops));
        // At rest: 9.4 s.
        Assert.True(outcome.Seconds < 12, $"{motion.Name}: took {outcome.Seconds:F1} s");
    }

    [Theory]
    [MemberData(nameof(GentleMotions))]
    public void AJettisonedContainerIsTakenBackAfterItsDelayWhileTheShipManoeuvresGently(int index)
    {
        var motion = Gentle[index];
        // Pitching at 10 degrees a second turns the ship's back on the container within the ten seconds of the
        // pickup delay: both beams sit on the deck and see nothing below it. That is the beams' view, not their pull.
        if (motion.Name.StartsWith("pitch", StringComparison.Ordinal)) return;
        var outcome = Run(motion, Body.Jettisoned, default, 40);
        output.WriteLine($"{motion.Name}: {outcome}");
        Assert.True(outcome.Stowed, $"{motion.Name}: {outcome}");
        Assert.Equal((0, 0), (outcome.Stalls, outcome.Drops));
        // At rest: 19.0 s, ten of them the pickup delay.
        Assert.True(outcome.Seconds < 23, $"{motion.Name}: took {outcome.Seconds:F1} s");
    }

    [Fact]
    public void AShipKnockedHarderThanItsBeamsCarryDoesNotJamItsLoad()
    {
        // Eight metres a second in one tick is 480 m/s², far past what the beam carries over. The container lags, the
        // pull makes it up, and the stall rule does not take the ship's own jolt for a jammed load.
        var outcome = Run(Gentle[0] with { Dampeners = false }, Body.Container, AboveTheRacks, 40, knock: new Vector3(0, 0, -8));
        output.WriteLine(outcome.ToString());
        Assert.True(outcome.Stowed, outcome.ToString());
        Assert.Equal((0, 0), (outcome.Stalls, outcome.Drops));
        Assert.True(outcome.Seconds < 16, $"took {outcome.Seconds:F1} s");
    }

    [Fact]
    public void WithoutTheCarryAManoeuvringShipLeavesItsLoadBehind()
    {
        // The same runs with the carry turned off in a copy of the content: the pull alone (4 m/s²) cannot keep a load
        // in step with a ship that brakes at 10 m/s² and thrusts at 11, which is why the figure exists.
        using var copy = new ContentCopy();
        copy.Replace("blocks/cargo.json", "\"carryAcceleration\": 25", "\"carryAcceleration\": 0");
        var content = copy.Load();
        Assert.Equal(0f, content.Cargo.Tractor.CarryAcceleration);
        var thrust = Array.Find(Hard, motion => motion.Name == "full thrust and stop")!;
        var without = Run(thrust, Body.Container, AboveTheRacks, 40, content);
        var with = Run(thrust, Body.Container, AboveTheRacks, 40);
        output.WriteLine($"carry off: {without}");
        output.WriteLine($"carry on:  {with}");
        Assert.False(without.Stowed);
        Assert.True(with.Stowed && with.Seconds < 9, with.ToString());
        // At rest the carry has nothing to do: the two contents stow the container in the same tick.
        Assert.Equal(Run(Gentle[0], Body.Container, AboveTheRacks, 40).Seconds, Run(Gentle[0], Body.Container, AboveTheRacks, 40, content).Seconds);
    }

    private readonly record struct Mined(int Spawned, int Taken, int Stalls, float Held, float Unpowered, float MeanSeconds)
    {
        public override string ToString() =>
            $"{Taken} of {Spawned} chunks taken, stalls {Stalls}, held {Held:F1} s, unpowered {Unpowered:F1} s, {MeanSeconds:F1} s a chunk";
    }

    /// <summary>
    /// A mining run on the shipped ship: both lasers held on (their demand planned as the server plans it, so the
    /// battery runs flat and the lasers wait for it), and for the first <paramref name="cutting"/> seconds a chunk
    /// breaking off 18 m ahead of the nose every four seconds the lasers cut, at rest in the rock's frame.
    /// </summary>
    private static Mined Mine(Motion motion, float seconds, float cutting)
    {
        var design = Content.Pod; using var hulls = new HullStore();
        var origin = Content.Galaxy.CenterOf(new(0, 100, 0));
        var h = hulls.Create(1, (byte)Content.PodDesign, design, origin);
        hulls.State(h.Index).Velocity = motion.Drift;
        var field = new HullField(hulls, Content.Designs);
        var bodies = new FreeContainers(); var chunks = new OreChunks();
        var pivot = (design.CenterOfMass + new Vector3(.5f)) * design.BlockSize;
        var tractor = new TractorSimulation(); var scratch = new byte[4096];
        var ore = new OreChunkSimulation(Content.Galaxy); var rocks = new RockField(Content.Galaxy);
        var spawned = 0; var taken = 0; var stalls = 0; var held = 0; var unpowered = 0; var cut = 0; var total = 0f;
        var born = new Dictionary<uint, long>(); var retries = new Dictionary<uint, long>();
        var ticks = (long)(seconds * Hz);
        for (long tick = 0; tick < ticks; tick++)
        {
            field.BeginRound();
            tractor.Plan(bodies, chunks, Content, field, scratch, tick);
            ref var info = ref hulls.Info(h.Index);
            info.LaserDemand = 0; info.Lasers.Firing = 0;
            if (LaserHeat.MayFire(info.Lasers))
                for (var k = 0; k < design.Lasers.Length; k++)
                {
                    if (info.Lasers.IsLocked(k)) continue;
                    info.LaserDemand += design.Mounts[design.Lasers[k]].Type.Laser!.Power;
                    info.Lasers.Firing |= 1UL << k;
                }
            var input = motion.Pilot(tick);
            input.Dampeners = motion.Dampeners;
            field.Step(h.Index, input);
            if (info.Power.Laser > 0 && tick < cutting * Hz && ++cut % 240 == 0)
            {
                var pose = hulls.State(h.Index);
                var place = new Vector3(.5f + (spawned % 3 - 1) * 1.5f, 3 + spawned % 2 * 2, -18);
                Assert.True(chunks.Add(Content, new OreChunk
                {
                    Position = pose.Position + HullRotation.Rotate(place - pivot, pose.Orientation),
                    Orientation = Quaternion.Identity,
                    Resource = 0,
                    Grade = .75f,
                    Mass = Content.Mining.ChunkMaxMassKg,
                    Born = tick,
                }, out var id));
                born[id] = tick; spawned++;
            }

            tractor.Pull(bodies, chunks, Content, field, scratch, tick);
            ore.Step(chunks, Content, field, rocks, tick);
            foreach (var id in born.Keys.ToArray())
            {
                var found = false;
                foreach (ref readonly var item in chunks.Items)
                {
                    if (item.Id != id) continue;
                    found = true;
                    if (item.TractorHull != 0) { held++; if (!item.TractorPowered) unpowered++; }
                    if (retries.TryGetValue(id, out var before) && item.RetryTick > before) stalls++;
                    retries[id] = item.RetryTick;
                }

                if (found) continue;
                taken++; total += (tick - born[id]) / Hz; born.Remove(id);
            }
        }

        return new(spawned, taken, stalls, held / Hz, unpowered / Hz, taken > 0 ? total / taken : -1);
    }

    [Theory]
    [MemberData(nameof(GentleMotions))]
    public void MiningWhileTheShipIsNudgedAboutBringsEveryChunkIn(int index)
    {
        var motion = Gentle[index];
        var mined = Mine(motion, 150, 110);
        output.WriteLine($"{motion.Name}: {mined}");
        // The lasers run the battery flat here, as they do in play, and thrust is paid before the beams: a beam
        // misses some ticks of power, which costs time and never a chunk.
        Assert.True(mined.Spawned >= 8, $"{motion.Name}: only {mined.Spawned} chunks broke off");
        Assert.Equal(mined.Spawned, mined.Taken);
        Assert.Equal(0, mined.Stalls);
        // At rest: 6.7 s a chunk. Nudged about, two of seven came in before the fix, and three of seven.
        Assert.True(mined.MeanSeconds < 10, $"{motion.Name}: {mined.MeanSeconds:F1} s a chunk");
    }

    [Fact]
    public void TheCarryGoesAlongWithTheShipUpToItsLimitAndSaysWhenTheShipDidMore()
    {
        var limit = Content.Cargo.Tractor.CarryAcceleration;
        // The shipped ship's own manoeuvres are within it: full thrust is about 11 m/s², its dampeners brake at
        // about 10 and boost doubles thrust.
        Assert.InRange(limit, 23f, 100f);
        var velocity = new Vector3(1, 2, 3);
        var small = new Vector3(0, limit * HullSim.Dt * .5f, 0);
        Assert.True(Approach.Carry(ref velocity, small, limit));
        Assert.Equal(new Vector3(1, 2, 3) + small, velocity);

        velocity = default;
        Assert.False(Approach.Carry(ref velocity, new Vector3(0, 0, 8), limit));
        Assert.Equal(limit * HullSim.Dt, velocity.Z, 5);
        Assert.Equal((0f, 0f), (velocity.X, velocity.Y));

        // No carry at all (a beam without power): nothing is added, and any change of the ship's is a lag.
        velocity = default;
        Assert.False(Approach.Carry(ref velocity, new Vector3(0, 0, .01f), 0));
        Assert.Equal(Vector3.Zero, velocity);
        Assert.True(Approach.Carry(ref velocity, Vector3.Zero, 0));
    }

    [Fact]
    public void WhatABodyHasLeftToDoShrinksWhileTheBeamBrakesItAndStandsStillWhenItIsHeldBack()
    {
        const float acceleration = 4; const float share = .7f;
        // A body 10 m from its goal flying away from it at 6 m/s, pulled as the approach rule pulls: its path grows for
        // a second and a half, which the old measure (the path alone) counted as a beam gaining nothing.
        var distance = 10f; var away = 6f; var before = float.PositiveInfinity; var pathGrew = false;
        for (var tick = 0; tick < 600 && distance > .05f; tick++)
        {
            var wanted = Approach.Speed(acceleration, distance, share);
            var error = wanted + away;
            var left = Approach.Left(distance, error, acceleration);
            Assert.True(left < before, $"tick {tick}: {left} after {before}");
            before = left;
            away -= Math.Min(error, acceleration * HullSim.Dt);
            pathGrew |= away > 0;
            distance += away * HullSim.Dt;
        }

        Assert.True(pathGrew);
        Assert.True(distance <= .05f);
        // With nothing to correct it is the path itself, and a beam with no pull has no room to count.
        Assert.Equal(10f, Approach.Left(10, 0, acceleration));
        Assert.Equal(10f, Approach.Left(10, 5, 0));
    }

    [Fact]
    public void WithTheBatteryFlatThrustIsPaidBeforeTheBeams()
    {
        // The one limit left that is not the beam's pull: power. The ship makes 5.2 MW more than it needs idle, a beam
        // holding a chunk asks 2.27 MW, and thrust is paid first. With charge in the battery that never shows. With
        // the battery flat, as the lasers leave it, a beam is paid only while thrust leaves it its share.
        var flight = Content.Pod.Flight;
        var spare = flight.PowerGenerated - flight.IdlePower;
        var beam = Content.Pod.Mounts[Content.Pod.Tractors[0]].Type.Tractor!.Power(Content.Mining.ChunkMaxMassKg);
        Assert.Equal(5.2f, spare, 2);
        Assert.Equal(2.27f, beam, 2);

        // A third of the thrust takes 1.7 MW: the beam keeps its power and the chunk comes in as ever.
        var gentle = Run(Gentle[2], Body.Chunk, AheadOfTheNose, 30, battery: 0);
        output.WriteLine($"thrust and stop, battery flat: {gentle}");
        Assert.True(gentle.Stowed && gentle.Seconds < 9, gentle.ToString());
        Assert.Equal((0, 0f), (gentle.Stalls, gentle.Unpowered));

        // Full reverse thrust asks 8.6 MW, more than the generator has to give: the beam gets nothing, pulls nothing,
        // and lets the chunk go at the stall, as a beam without power always did.
        var reverse = new Motion("full reverse", _ => new FlightInput { Forward = -127 });
        var starved = Run(reverse, Body.Chunk, AheadOfTheNose, 6, battery: 0);
        output.WriteLine($"full reverse, battery flat: {starved}");
        Assert.False(starved.Stowed);
        Assert.Equal(1, starved.Stalls);
        Assert.Equal(starved.Held, starved.Unpowered);
        Assert.Equal(Content.Cargo.Tractor.StallSeconds, starved.Held, 1);

        // The same thrust with charge in the battery: the beam is paid and the carry keeps the chunk coming.
        var charged = Run(reverse, Body.Chunk, AheadOfTheNose, 30);
        output.WriteLine($"full reverse, battery full: {charged}");
        Assert.True(charged.Stowed && charged.Seconds < 9, charged.ToString());
        Assert.Equal(0, charged.Stalls);
    }

    [Fact]
    public void TheScenarioTable()
    {
        // The figures the tests above hold, in one place: every motion, each kind of body.
        foreach (var motion in Gentle.Concat(Hard))
        {
            var container = Run(motion, Body.Container, AboveTheRacks, 60);
            var far = Run(motion, Body.Container, FarAbove, 90);
            var chunk = Run(motion, Body.Chunk, AheadOfTheNose, 60);
            var jettisoned = Run(motion, Body.Jettisoned, default, 60);
            var mined = Mine(motion, 150, 110);
            output.WriteLine($"MOVE {motion.Name,-24} container {Cell(container)} | 30 m out {Cell(far)} | chunk {Cell(chunk)} | jettisoned {Cell(jettisoned)} | mining {mined.Taken,2}/{mined.Spawned,-2} stalls {mined.Stalls} unpowered {mined.Unpowered,5:F1} s");
        }

        static string Cell(Outcome o) => $"{(o.Stowed ? $"{o.Seconds,5:F1} s" : "   never")} stalls {o.Stalls}";
    }
}
