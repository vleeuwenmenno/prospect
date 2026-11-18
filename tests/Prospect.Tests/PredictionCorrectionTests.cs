using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// What each snapshot corrects of the flown hull, on the shipped ship, through a real server and a real predicting
/// client ticked by hand. A hull that touches nothing is predicted exactly, whatever it does and carries: every
/// snapshot must find the client where the server is. The last test only measures, and prints what it finds: a ship
/// that manoeuvres while its drones fetch is struck by them.
/// </summary>
public sealed class PredictionCorrectionTests(ITestOutputHelper output)
{
    private static readonly GameContent Shipped = GameContent.Load();

    /// <summary>The shipped content with drone ore hauling switched on, for the measurement of what that did.</summary>
    private static readonly Lazy<GameContent> OreFetchOn = new(() =>
    {
        using var copy = new ContentCopy().Replace("blocks/drones.json", "\"oreFetch\": false", "\"oreFetch\": true");
        return copy.Load();
    });

    // What counts as agreement: metres, radians, metres a second and radians a second. Positions are doubles on both
    // sides and velocities floats, so a hull that is predicted exactly differs by rounding in the last place only.
    private const float Metres = 1e-4f, Radians = 1e-4f, MetresPerSecond = 1e-3f, RadiansPerSecond = 1e-4f;

    /// <summary>A server and one client on an in-process link, ticked by hand, with every snapshot's correction kept.</summary>
    private sealed class Rig : IDisposable
    {
        internal readonly ServerHost Server;
        internal readonly ClientSession Client;
        internal readonly HullStore Hulls;
        internal readonly int Slot;
        internal readonly uint Own;
        private readonly ClientWorld _world;
        private readonly ManualTime _clock = new();
        private readonly LoopbackLink _link = new();
        private long _tick, _seen;
        private Double3 _before, _earlier;
        private int _placed;

        /// <summary>Snapshots reconciled since <see cref="Reset"/>, and how many of them moved the hull.</summary>
        internal int Snapshots, Corrected;

        /// <summary>The largest correction since <see cref="Reset"/>, a figure each.</summary>
        internal float Position, Angle, Velocity, Spin, Energy;

        /// <summary>Contacts between the flown hull and another hull since <see cref="Reset"/>.</summary>
        internal int Contacts;

        /// <summary>Ticks in which the server moved the flown hull over two centimetres off an even path, and the furthest.</summary>
        internal int Jolts;

        /// <inheritdoc cref="Jolts" />
        internal float Jolt;

        internal Rig(bool overRock = false, GameContent? content = null)
        {
            content ??= Shipped;
            var config = overRock
                ? new ServerConfig { MaxPlayers = 1, Clock = _clock, ScriptedRockScrapeRadius = 100, ScriptedRockScrapeStandoff = 25 }
                : new ServerConfig { MaxPlayers = 1, Clock = _clock };
            Server = new ServerHost(config, content, [_link.Server]);
            Client = new ClientSession(_link.Client, content, "Pilot", clock: _clock);
            _world = (ClientWorld)typeof(ClientSession).GetField("_world", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client)!;
            Hulls = MiningFixtures.Field<HullStore>(Server, "_hulls");
            for (var n = 0; n < 4000 && !(Snap().OwnHull >= 0 && Snap().Hulls[Snap().OwnHull].Id != 0); n++)
            {
                Step();
                Thread.Sleep(1);
            }

            Assert.True(Snap().OwnHull >= 0, "the client was given a hull");
            Own = Snap().Hulls[Snap().OwnHull].Id;
            Slot = (int)(Own & 0xFFFF);
            var field = MiningFixtures.Field<HullField>(Server, "_field");
            var remember = field.ContactObserved;
            field.ContactObserved = (a, b, tick) =>
            {
                if (a == Slot || b == Slot) Contacts++;
                remember?.Invoke(a, b, tick);
            };

            // The scripted placement over the rock happens two seconds in; everything has settled a second later.
            Step(overRock ? 210 : 120);
            Reset();
        }

        internal SessionSnapshot Snap() => Client.AcquireSnapshot();

        internal ref HullState State => ref Hulls.State(Slot);

        internal ref HullInfo Info => ref Hulls.Info(Slot);

        internal void Step(int count = 1)
        {
            for (var n = 0; n < count; n++)
            {
                _clock.Advance(ManualTime.Tick);
                ((ITickHandler)Server).Tick(_tick);
                ((ITickHandler)Client).Tick(_tick);
                _tick++;
                if (Own == 0) continue;

                var correction = _world.LastCorrection;
                if (correction.Count != _seen)
                {
                    _seen = correction.Count;
                    Snapshots++;
                    if (correction.Position > Metres || correction.Angle > Radians || correction.Velocity > MetresPerSecond || correction.Spin > RadiansPerSecond)
                        Corrected++;
                    Position = Math.Max(Position, correction.Position);
                    Angle = Math.Max(Angle, correction.Angle);
                    Velocity = Math.Max(Velocity, correction.Velocity);
                    Spin = Math.Max(Spin, correction.Spin);
                    Energy = Math.Max(Energy, correction.Energy);
                }

                // The second difference of the server's own place for the hull: a twentieth of a metre a tick is more
                // than the ship's thrust can make of it, so anything above was a contact moving the hull.
                var place = State.Position;
                if (_placed >= 2)
                {
                    var uneven = (place.RelativeTo(_before) - _before.RelativeTo(_earlier)).Length();
                    Jolt = Math.Max(Jolt, uneven);
                    if (uneven > 0.02f) Jolts++;
                }

                _earlier = _before;
                _before = place;
                _placed++;
            }
        }

        /// <summary>Holds these controls for a number of ticks.</summary>
        internal void Fly(PlayerControls controls, int ticks)
        {
            Client.Input.Submit(controls);
            Step(ticks);
        }

        /// <summary>A direction this far to the right of and above the hull's nose, in radians.</summary>
        internal Vector3 Aim(float right, float up) =>
            Vector3.Transform(Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(-right, up, 0)), State.Orientation);

        /// <summary>Fires group 1 as the pilot's camera would aim it, behind and above the hull and looking along its nose; zero groups lets go.</summary>
        internal void Fire(bool on)
        {
            var camera = Vector3.Transform(new Vector3(0, 6, 22), State.Orientation);
            Client.Input.AimLasers(Own, on ? ActionGroups.Bit(1) : (ushort)0, camera, Vector3.Transform(-Vector3.UnitZ, State.Orientation));
        }

        internal double OreAboard()
        {
            var kilograms = 0.0;
            foreach (var bin in Hulls.BlockStates.Bins(Info.BlockState)) kilograms += bin.Kg;
            return kilograms;
        }

        internal void Reset()
        {
            Snapshots = Corrected = Contacts = Jolts = 0;
            Position = Angle = Velocity = Spin = Energy = Jolt = 0;
            _placed = 0;
            _seen = _world.LastCorrection.Count;
        }

        internal string Figures =>
            $"{Corrected} of {Snapshots} snapshots corrected the hull (at most {Position:0.0000} m, {Angle * 180 / MathF.PI:0.0000} deg, {Velocity:0.0000} m/s, {Spin:0.0000} rad/s; battery {Energy:0.00} MJ)";

        internal void AssertNeverCorrected(string when)
        {
            Assert.True(Snapshots > 0, $"{when}: no snapshot was reconciled");
            Assert.True(Corrected == 0, $"{when}: {Figures}");
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
            _link.Dispose();
        }
    }

    [Fact]
    public void AShipThatTouchesNothingIsNeverCorrectedAtRestUnderThrustOrTurning()
    {
        using var rig = new Rig();

        // Short legs there and back, so the ship stays in the open space it starts in.
        rig.Fly(default, 300);
        rig.AssertNeverCorrected("at rest");
        rig.Fly(new PlayerControls { Forward = 1 }, 180);
        rig.Fly(default, 300);
        rig.Fly(new PlayerControls { Forward = -1 }, 180);
        rig.Fly(default, 300);
        rig.AssertNeverCorrected("forward, back and braking");
        rig.Fly(new PlayerControls { Forward = 1, Boost = true }, 120);
        rig.Fly(new PlayerControls { Forward = -1, Boost = true }, 120);
        rig.Fly(default, 420);
        rig.AssertNeverCorrected("boosting");
        rig.Fly(new PlayerControls { Strafe = 1 }, 150);
        rig.Fly(new PlayerControls { Strafe = -1 }, 150);
        rig.Fly(new PlayerControls { Vertical = 1 }, 150);
        rig.Fly(new PlayerControls { Vertical = -1 }, 150);
        rig.Fly(default, 300);
        rig.AssertNeverCorrected("sideways, up and down");
        rig.Fly(new PlayerControls { Roll = 1 }, 150);
        rig.Fly(new PlayerControls { Align = true, Aim = rig.Aim(1.2f, 0.3f) }, 240);
        rig.Fly(new PlayerControls { Align = true, Aim = rig.Aim(-2.2f, -0.4f), Forward = 1 }, 180);
        rig.Fly(new PlayerControls { Align = true, Aim = rig.Aim(3f, 0.2f), Forward = 1 }, 180);
        rig.Fly(default, 360);
        rig.AssertNeverCorrected("rolling and swinging onto the view");
        Assert.Equal(0, rig.Contacts);
    }

    [Fact]
    public void MiningToOverheatAndAFlatBatteryWithOreComingAboardNeverCorrectsThePose()
    {
        using var rig = new Rig(overRock: true);
        var mass = rig.Hulls.Flight(rig.Slot).Mass;
        rig.Fire(true);
        bool overheated = false, waited = false;
        for (var n = 0; n < 3000; n++)
        {
            rig.Step();
            overheated |= rig.Info.Lasers.Locked != 0;
            waited |= rig.Info.Lasers.AwaitingPower;
        }

        // All of it happened: the lasers shut down hot, the beams brought ore aboard, and the battery ran flat.
        Assert.True(overheated, "a laser overheated");
        Assert.True(waited, "the lasers waited for power");
        Assert.True(rig.OreAboard() > 1000, $"the bins hold {rig.OreAboard():0} kg");
        Assert.True(rig.Hulls.Flight(rig.Slot).Mass > mass + 1f, "the ore is part of what the hull weighs");
        rig.AssertNeverCorrected("mining at rest");

        // The battery is predicted from the beams and the chunks the client was last told of: near, never exact.
        Assert.True(rig.Energy < 1.5f, $"the battery was corrected by {rig.Energy:0.00} MJ");

        // With the ore aboard the hull is heavier and its centre of mass has moved: it still flies as predicted.
        rig.Fire(false);
        rig.Reset();
        rig.Fly(new PlayerControls { Forward = -1 }, 180);
        rig.Fly(new PlayerControls { Roll = 1 }, 120);
        rig.Fly(new PlayerControls { Strafe = 1, Align = true, Aim = rig.Aim(1f, 0.4f) }, 240);
        rig.Fly(default, 360);
        rig.AssertNeverCorrected("flying with ore aboard");
        Assert.Equal(0, rig.Contacts);
    }

    [Fact]
    public void DronesHoldingStationAreNotStruckInOrdinaryFlight()
    {
        using var rig = new Rig();
        rig.Client.Input.LaunchDrones(rig.Own, 3);
        rig.Step(600);
        Assert.Equal(4, rig.Hulls.Count);
        rig.Reset();

        rig.Fly(new PlayerControls { Forward = 1 }, 180);
        rig.Fly(default, 300);
        rig.Fly(new PlayerControls { Forward = -1 }, 180);
        rig.Fly(default, 300);
        rig.Fly(new PlayerControls { Strafe = 1 }, 150);
        rig.Fly(new PlayerControls { Strafe = -1 }, 150);
        rig.Fly(new PlayerControls { Vertical = 1 }, 150);
        rig.Fly(new PlayerControls { Vertical = -1 }, 150);
        rig.Fly(default, 300);
        rig.Fly(new PlayerControls { Align = true, Aim = rig.Aim(1.2f, 0.3f), Forward = 1 }, 240);
        rig.Fly(new PlayerControls { Align = true, Aim = rig.Aim(-2.2f, -0.2f), Forward = 1 }, 240);
        rig.Fly(default, 420);
        Assert.Equal(0, rig.Contacts);
        rig.AssertNeverCorrected("flying with drones holding station");
    }

    /// <summary>
    /// Measurement, not a rule yet: a ship that mines, has its drones fetch the ore and moves about is struck by the
    /// drones that carry it (they brake at 5.3 m/s², the ship at over 6), and a contact moves the ship as far as it
    /// moves the drone, whatever they weigh. The figures are printed; the fix that takes them to zero is undecided.
    /// </summary>
    [Fact]
    public void MeasuresAShipManoeuvringWhileItsDronesFetchOre()
    {
        using var rig = new Rig(overRock: true, OreFetchOn.Value);
        rig.Client.Input.LaunchDrones(rig.Own, 3);
        rig.Step(18);
        rig.Client.Input.SetDroneSettings(rig.Own, true, true);
        rig.Fire(true);
        rig.Reset();
        var fetched = false;
        for (var leg = 0; leg < 40; leg++)
        {
            var way = leg % 2 == 0 ? 1 : -1;
            rig.Client.Input.Submit(leg % 4 < 2 ? new PlayerControls { Strafe = way } : new PlayerControls { Vertical = way });
            for (var n = 0; n < 240; n++)
            {
                rig.Step();
                fetched |= rig.Snap().DroneCarryCount > 0;
            }
        }

        // The scenario is what it says: drones were out and carried ore while the ship flew.
        Assert.True(rig.Hulls.Count >= 4, $"{rig.Hulls.Count} hulls");
        Assert.True(fetched, "a drone carried a chunk");
        output.WriteLine($"MEASURE 160 s of mining, strafing and climbing with drones fetching: {rig.Contacts} contacts between the ship and a drone; the server moved the ship unevenly in {rig.Jolts} ticks (at most {rig.Jolt:0.000} m in one); {rig.Figures}");
    }

    /// <summary>
    /// The shipped content has drone ore hauling off: with "pick up ores" asked for, drones launched and the ship mining
    /// and strafing, no drone carries a chunk and the server never has to correct the ship's prediction.
    /// </summary>
    [Fact]
    public void WithTheShippedContentMiningWhileStrafingWithDronesOutNeedsNoCorrection()
    {
        using var rig = new Rig(overRock: true);
        rig.Client.Input.LaunchDrones(rig.Own, 3);
        rig.Step(18);
        rig.Client.Input.SetDroneSettings(rig.Own, true, true);
        rig.Fire(true);
        rig.Reset();
        for (var leg = 0; leg < 40; leg++)
        {
            var way = leg % 2 == 0 ? 1 : -1;
            rig.Client.Input.Submit(leg % 4 < 2 ? new PlayerControls { Strafe = way } : new PlayerControls { Vertical = way });
            for (var n = 0; n < 240; n++)
            {
                rig.Step();
                Assert.Equal(0, rig.Snap().DroneCarryCount);
            }
        }

        Assert.True(rig.Hulls.Count >= 4, $"{rig.Hulls.Count} hulls");
        Assert.Equal(0, rig.Contacts);
        rig.AssertNeverCorrected("mining and strafing with drones out");
    }
}
