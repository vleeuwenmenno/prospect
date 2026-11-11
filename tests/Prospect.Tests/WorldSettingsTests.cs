using System.Diagnostics;
using System.Numerics;
using System.Text;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>What each world setting does to the simulation, on the shared code the server and the client both run.</summary>
public sealed class WorldSettingEffectTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly int Vessel = Content.StartingFleet[0];

    /// <summary>The content as a world with these settings plays by it.</summary>
    private static GameContent World(params string[] assignments) => Content.WithSettings(Content.SettingCatalog.Build(null, assignments));

    /// <summary>Flies a pod at an asteroid at a speed until it bounces back, is destroyed, or the ticks run out.</summary>
    private static HullState Crash(GameContent world, float speed, out ulong alive)
    {
        var galaxy = world.Galaxy.WithSeed(11);
        var rocks = new RockField(galaxy);
        // Fields are dense: pick a rock of ordinary size with nothing else on the way in.
        var side = -Vector3.UnitX;
        var rock = rocks.Sector(default).Asteroids[PowerAndDamageTests.ClearRock(galaxy, default, 80f, 260f, side).Index];
        var design = world.Pod;
        var flight = design.Flight;
        var hp = new float[HullDesign.MaxBlocks];
        for (var i = 0; i < design.Types.Length; i++)
            hp[i] = design.BlockHitPoints(i);
        var body = new HullBody
        {
            Design = design,
            Alive = design.FullMask,
            BlockHp = hp,
        };

        var state = HullState.Spawn(galaxy.CenterOf(default) + rock.LocalPosition + side * (PowerAndDamageTests.SurfaceToward(rock, side) + 60f), flight);
        state.Velocity = new Vector3(speed, 0, 0);
        var order = default(HullOrder);
        for (var tick = 0; tick < 300; tick++)
        {
            if (HullSim.Step(ref state, ref order, default, flight, ref body, rocks, tick) || state.Velocity.X < 0)
                break;
        }

        alive = body.Alive;
        return state;
    }

    /// <summary>What is left of a pod and a parked vessel after the pod has flown into the vessel.</summary>
    private readonly record struct Rammed(HullState Pod, HullState Vessel, ulong PodBlocks, ulong VesselBlocks);

    /// <summary>Flies a pod into a parked vessel, stepping both the way the server steps its hulls.</summary>
    private static Rammed Ram(GameContent world, float speed)
    {
        using var hulls = new HullStore();
        var field = new HullField(hulls, world.Designs);
        var vessel = hulls.Create(1, (byte)Vessel, world.Designs[Vessel], Double3.Zero).Index;
        var pod = hulls.Create(1, (byte)world.PodDesign, world.Pod, new Double3(30, 0, 0)).Index;
        hulls.State(pod).Velocity = new Vector3(-speed, 0, 0);

        for (var tick = 0; tick < 120; tick++)
        {
            field.BeginRound();
            Assert.False(field.Step(vessel, default), "the vessel was destroyed");
            Assert.False(field.Step(pod, default), "the pod was destroyed");
            Assert.True(field.Lost.IsEmpty, "a hull was destroyed");
        }

        return new Rammed(hulls.State(pod), hulls.State(vessel), hulls.Info(pod).Blocks, hulls.Info(vessel).Blocks);
    }

    [Fact]
    public void LoadedContentAndAWorldAtTheDefaultsAreTheSameAndSettingsNeverStack()
    {
        Assert.Same(Content, World());
        Assert.Same(Content, Content.WithSettings(Content.SettingCatalog.Defaults));

        // A world's figures always start from the files: going back to the defaults gives the loaded figures
        // again, and asking for the same settings twice does not apply them twice.
        var strong = World("hullStrength=2");
        Assert.NotSame(Content, strong);
        Assert.Equal(Content.Pod.Flight, strong.WithSettings(Content.SettingCatalog.Defaults).Pod.Flight);
        Assert.Equal(strong.Pod.Flight, strong.WithSettings(strong.Settings).Pod.Flight);
        Assert.Equal(strong.Pod.Flight, World("hullStrength=4").WithSettings(strong.Settings).Pod.Flight);

        // The loaded content is untouched by the worlds made from it.
        Assert.Equal(Content.SettingCatalog.Defaults, Content.Settings);
        Assert.Equal(Content.Pod.Stats.HullPoints * 2, strong.Pod.Stats.HullPoints);
    }

    [Fact]
    public void CollisionDamageAtZeroStillBouncesButNeverDamages()
    {
        var harmless = World("collisionDamage=0");
        var pod = harmless.Pod.Flight;

        // An asteroid at a speed that tears blocks off in a plain world.
        Crash(Content, 170, out var plain);
        Assert.NotEqual(Content.Pod.FullMask, plain);
        var state = Crash(harmless, 170, out var alive);
        Assert.True(state.Velocity.X < 0, "the hull should have bounced back");
        Assert.Equal(pod.ShieldCapacity, state.Shield);
        Assert.Equal(pod.HullPoints, state.Hull);
        Assert.Equal(harmless.Pod.FullMask, alive);

        // Another hull: both are shoved, neither is hurt.
        var rammed = Ram(harmless, 80);
        Assert.True(rammed.Pod.Velocity.X > -60f, $"the pod kept {rammed.Pod.Velocity.X} m/s");
        Assert.True(rammed.Vessel.Velocity.X < -5f, $"the vessel only reached {rammed.Vessel.Velocity.X} m/s");
        Assert.Equal(pod.ShieldCapacity, rammed.Pod.Shield);
        Assert.Equal(pod.HullPoints, rammed.Pod.Hull);
        Assert.Equal(harmless.Designs[Vessel].Flight.ShieldCapacity, rammed.Vessel.Shield);
        Assert.Equal(harmless.Designs[Vessel].Flight.HullPoints, rammed.Vessel.Hull);
        Assert.Equal(harmless.Pod.FullMask, rammed.PodBlocks);
        Assert.Equal(harmless.Designs[Vessel].FullMask, rammed.VesselBlocks);
    }

    [Fact]
    public void WithShieldsSetToNoneACollisionDamagesBlocksAndLeavesTheShieldFull()
    {
        var bare = World("collisionShields=None");
        var pod = bare.Pod.Flight;
        var vessel = bare.Designs[Vessel].Flight;

        // A bump that only costs shield in a plain world.
        var plain = Crash(Content, 40, out _);
        Assert.True(plain.Shield < pod.ShieldCapacity);
        Assert.Equal(pod.HullPoints, plain.Hull);

        var state = Crash(bare, 40, out var alive);
        Assert.True(state.Velocity.X < 0, "the hull should have bounced back");
        Assert.Equal(pod.ShieldCapacity, state.Shield);
        Assert.True(state.Hull < pod.HullPoints, "the blocks should have taken the damage");

        // With nothing in the way, the block that touched takes the whole hit and is lost.
        Assert.NotEqual(bare.Pod.FullMask, alive);

        var rammed = Ram(bare, 30);
        Assert.Equal(pod.ShieldCapacity, rammed.Pod.Shield);
        Assert.Equal(vessel.ShieldCapacity, rammed.Vessel.Shield);
        Assert.True(rammed.Pod.Hull < pod.HullPoints, "the pod's blocks should have taken the damage");
        Assert.True(rammed.Vessel.Hull < vessel.HullPoints, "the vessel's blocks should have taken the damage");
    }

    [Fact]
    public void AWeakShieldAbsorbsTheSameHitButLosesFourTimesAsMuch()
    {
        var weak = World("collisionShields=Weak");
        var capacity = Content.Pod.Flight.ShieldCapacity;

        // The factor is the content's, and the content says four.
        var setting = Content.SettingCatalog.Find("collisionShields")!;
        Assert.True(setting.TryParse("Weak", out var option));
        Assert.Equal(4f, setting.Number(option));
        Assert.Equal(4f, weak.Pod.Flight.CollisionShieldCost);
        Assert.Equal(1f, Content.Pod.Flight.CollisionShieldCost);

        // The same bump against an asteroid in both worlds.
        var full = capacity - Crash(Content, 20, out _).Shield;
        var state = Crash(weak, 20, out var alive);
        Assert.InRange(full, 20f, capacity / 4 - 1f);
        Assert.Equal(full * 4, capacity - state.Shield, 1);
        Assert.Equal(weak.Pod.Flight.HullPoints, state.Hull);
        Assert.Equal(weak.Pod.FullMask, alive);

        // And against another hull, for both of them.
        var plainRam = Ram(Content, 20);
        var weakRam = Ram(weak, 20);
        var vesselCapacity = Content.Designs[Vessel].Flight.ShieldCapacity;
        Assert.InRange(capacity - plainRam.Pod.Shield, 1f, capacity / 4 - 1f);
        Assert.Equal((capacity - plainRam.Pod.Shield) * 4, capacity - weakRam.Pod.Shield, 1);
        Assert.Equal((vesselCapacity - plainRam.Vessel.Shield) * 4, vesselCapacity - weakRam.Vessel.Shield, 1);
    }

    [Fact]
    public void AWeakShieldThatRunsOutPassesOnOnlyWhatItCouldNotAbsorb()
    {
        var weak = World("collisionShields=Weak");
        var design = weak.Pod;
        var flight = design.Flight;
        var hp = new float[HullDesign.MaxBlocks];
        for (var i = 0; i < design.Types.Length; i++)
            hp[i] = design.BlockHitPoints(i);
        var body = new HullBody
        {
            Design = design,
            Alive = design.FullMask,
            BlockHp = hp,
        };

        // 100 shield points at four a point absorb 25 of a 100 point hit; the other 75 reach the block.
        var state = HullState.Spawn(Double3.Zero, flight);
        state.Shield = 100;
        Assert.False(HullSim.Damage(ref state, flight, ref body, 1UL, 1, 100));
        Assert.Equal(0f, state.Shield);
        Assert.Equal(flight.HullPoints - 75f, state.Hull, 2);
        Assert.Equal(design.BlockHitPoints(0) - 75f, hp[0], 3);
    }

    [Fact]
    public void HullStrengthScalesEveryBlocksHitPoints()
    {
        var plain = Content.Pod;
        foreach (var factor in new[] { 0.25f, 2f, 4f })
        {
            var design = World($"hullStrength={factor}").Pod;
            Assert.Equal(plain.Flight.HullPoints * factor, design.Flight.HullPoints, 2);
            Assert.Equal(plain.Stats.HullPoints * factor, design.Stats.HullPoints, 2);

            // Nothing else about the hull changes: it weighs and flies the same.
            Assert.Equal(plain.Flight.Mass, design.Flight.Mass);
            Assert.Equal(plain.Flight.ShieldCapacity, design.Flight.ShieldCapacity);

            using var store = new HullStore(2);
            var hull = store.Create(1, 0, design, Double3.Zero);
            var hp = store.BlockHp(hull.Index);
            for (var i = 0; i < design.Types.Length; i++)
            {
                Assert.Equal(plain.Types[i].Hull * factor, hp[i]);
                Assert.Equal(hp[i], design.BlockHitPoints(i));
            }

            // What the hull is spawned with, the sum of its blocks, and the figure on the data sheet agree exactly.
            Assert.Equal(design.Flight.HullPoints, store.State(hull.Index).Hull);
            Assert.Equal(design.Flight.HullPoints, hp.ToArray().Sum());
        }

        // A stronger hull keeps a block that a plain one loses to the same impact.
        Crash(Content, 60, out var plainLeft);
        Crash(World("hullStrength=4"), 60, out var strongLeft);
        Assert.NotEqual(plain.FullMask, plainLeft);
        Assert.Equal(plain.FullMask, strongLeft);
    }

    [Fact]
    public void JumpChargeSpeedScalesTheTimeToChargeAndNotTheEnergy()
    {
        static float SecondsCharging(in FlightParams flight, out float mostTakenIn)
        {
            var state = HullState.Spawn(Double3.Zero, flight);
            var order = new HullOrder { Kind = OrderKind.Jump, Target = new Double3(0, 0, -1e6) };
            var ticks = 0;
            mostTakenIn = 0;
            for (var i = 0; i < 60 * 120 && state.Jump != JumpPhase.Tunnel; i++)
            {
                HullSim.Step(ref state, ref order, default, flight);
                if (state.Jump != JumpPhase.Charge)
                    continue;
                ticks++;
                mostTakenIn = Math.Max(mostTakenIn, state.JumpTimer);
            }

            Assert.Equal(JumpPhase.Tunnel, state.Jump);
            return ticks * HullSim.Dt;
        }

        var plain = Content.Pod.Flight;
        var normal = SecondsCharging(plain, out var normalEnergy);
        foreach (var factor in new[] { 0.25f, 0.5f, 2f, 4f })
        {
            var flight = World($"jumpChargeSpeed={factor}").Pod.Flight;

            // The drive takes power that much faster; the jump still costs what it cost.
            Assert.Equal(plain.FtlPower * factor, flight.FtlPower, 3);
            Assert.Equal(plain.FtlEnergy, flight.FtlEnergy);
            Assert.Equal(plain.Mass, flight.Mass);

            var seconds = SecondsCharging(flight, out var energy);
            Assert.Equal(plain.FtlCalculateSeconds, flight.FtlCalculateSeconds);
            var expected = Math.Max(flight.FtlCalculateSeconds, flight.FtlEnergy / flight.FtlPower);
            Assert.InRange(seconds, expected - 0.1f, expected + 0.1f);

            // The drive fires when it has taken in the jump's energy: the most it held before that is just short of it.
            Assert.InRange(energy, flight.FtlEnergy - flight.FtlPower * HullSim.Dt - 0.01f, flight.FtlEnergy);
            Assert.InRange(normalEnergy, plain.FtlEnergy - plain.FtlPower * HullSim.Dt - 0.01f, plain.FtlEnergy);
        }
    }

    [Fact]
    public void ShieldRechargeScalesHowFastAShieldComesBack()
    {
        static float RechargedInOneSecond(in FlightParams flight)
        {
            var state = HullState.Spawn(Double3.Zero, flight);
            state.Shield = 0;
            for (var i = 0; i < 60; i++)
                FlightModel.Step(ref state, default, flight, HullSim.Dt);
            return state.Shield;
        }

        var plain = Content.Pod.Flight;
        var normal = RechargedInOneSecond(plain);
        Assert.InRange(normal, 1f, plain.ShieldCapacity / 4 - 1f);
        foreach (var factor in new[] { 0.25f, 2f, 4f })
        {
            var flight = World($"shieldRecharge={factor}").Pod.Flight;
            Assert.Equal(plain.ShieldRecharge * factor, flight.ShieldRecharge, 3);
            Assert.Equal(plain.ShieldRecharge * factor, World($"shieldRecharge={factor}").Pod.Stats.ShieldRecharge, 3);
            Assert.Equal(normal * factor, RechargedInOneSecond(flight), 1);

            // The wait before it starts, and what it holds, are not part of this setting.
            Assert.Equal(plain.ShieldDelay, flight.ShieldDelay);
            Assert.Equal(plain.ShieldCapacity, flight.ShieldCapacity);
        }
    }
}

/// <summary>World settings coming in from the content file, a values file, the command line and the network.</summary>
public sealed class WorldSettingParsingTests
{
    private static readonly WorldSettingCatalog Catalog = GameContent.Load().SettingCatalog;

    /// <summary>A catalog with one setting of each kind, for what the shipped five do not cover.</summary>
    private static readonly WorldSettingCatalog Mixed = WorldSettingCatalog.Parse(
        """
        {
          "settings": [
            { "id": "damage", "name": "Damage", "description": "A multiplier.", "kind": "multiplier", "default": 1, "minimum": 0.5, "maximum": 3, "step": 0.5 },
            { "id": "rules", "name": "Rules", "description": "A choice whose options stand for their position.", "kind": "choice", "default": "Middle", "options": [ { "name": "First" }, { "name": "Middle" }, { "name": "Last" } ] },
            { "id": "pvp", "name": "PvP", "description": "A switch.", "kind": "switch", "default": false }
          ]
        }
        """u8.ToArray(),
        "mixed.json"
    );

    private static float Value(WorldSettingCatalog catalog, in WorldSettings settings, string id) => settings.Get(catalog.Find(id)!);

    private static string TempFile(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"prospect-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void TheShippedSettingsAreTheFiveWithTheirRangesAndDefaults()
    {
        Assert.Equal(["collisionDamage", "collisionShields", "shieldRecharge", "jumpChargeSpeed", "hullStrength"], Catalog.Definitions.ToArray().Select(d => d.Id));

        var damage = Catalog.Find("collisionDamage")!;
        Assert.Equal((SettingKind.Multiplier, 1f, 0f, 4f), (damage.Kind, damage.Default, damage.Minimum, damage.Maximum));

        var shields = Catalog.Find("collisionShields")!;
        Assert.Equal(SettingKind.Choice, shields.Kind);
        Assert.Equal(["Full", "Weak", "None"], shields.Options);
        Assert.Equal("Full", shields.Format(shields.Default));
        Assert.Equal([1f, 4f, 0f], new[] { shields.Number(0), shields.Number(1), shields.Number(2) });

        foreach (var id in new[] { "shieldRecharge", "jumpChargeSpeed", "hullStrength" })
        {
            var setting = Catalog.Find(id)!;
            Assert.Equal((SettingKind.Multiplier, 1f, 0.25f, 4f), (setting.Kind, setting.Default, setting.Minimum, setting.Maximum));
        }

        // Every setting has what a screen needs to show it, and a default it accepts unchanged.
        foreach (var setting in Catalog.Definitions)
        {
            Assert.False(string.IsNullOrWhiteSpace(setting.Name));
            Assert.False(string.IsNullOrWhiteSpace(setting.Description));
            Assert.Equal(setting.Default, setting.Clamp(setting.Default));
            Assert.Equal(setting.Default, Catalog.Defaults.Get(setting));
            if (setting.Kind == SettingKind.Multiplier)
                Assert.True(setting.Step > 0);
        }
    }

    [Fact]
    public void AssignmentsSetValuesAndTheLaterOneWins()
    {
        var warnings = new List<string>();
        var settings = Catalog.Build(null, ["collisionDamage=0.5", "collisionShields=weak", "COLLISIONDAMAGE = 2", "hullStrength=1.5"], warnings.Add);

        Assert.Empty(warnings);
        Assert.Equal(2f, Value(Catalog, settings, "collisionDamage"));
        Assert.Equal(1f, Value(Catalog, settings, "collisionShields"));
        Assert.Equal(1.5f, Value(Catalog, settings, "hullStrength"));
        Assert.Equal(1f, Value(Catalog, settings, "shieldRecharge"));
        Assert.Equal(4f, Catalog.Number(settings, "collisionShields"));
        Assert.NotEqual(Catalog.Defaults, settings);
    }

    [Fact]
    public void EveryKindReadsTheValuesAPersonWouldWrite()
    {
        var settings = Mixed.Build(null, ["damage=2.5", "rules=last", "pvp=on"]);
        Assert.Equal(2.5f, Mixed.Number(settings, "damage"));
        Assert.Equal(2f, Mixed.Number(settings, "rules"));
        Assert.Equal(1f, Mixed.Number(settings, "pvp"));
        Assert.Equal(["2.5", "Last", "on"], Mixed.Definitions.ToArray().Select(d => d.Format(settings.Get(d))));

        settings = Mixed.Build(null, ["pvp=false", "rules=First"]);
        Assert.Equal(0f, Mixed.Number(settings, "pvp"));
        Assert.Equal(0f, Mixed.Number(settings, "rules"));

        var path = TempFile("""{ "damage": 1.5, "rules": "First", "pvp": true }""");
        try
        {
            settings = Mixed.Build(path, []);
            Assert.Equal((1.5f, 0f, 1f), (Mixed.Number(settings, "damage"), Mixed.Number(settings, "rules"), Mixed.Number(settings, "pvp")));

            // The command line is applied after the file.
            settings = Mixed.Build(path, ["damage=3"]);
            Assert.Equal((3f, 0f, 1f), (Mixed.Number(settings, "damage"), Mixed.Number(settings, "rules"), Mixed.Number(settings, "pvp")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnUnknownIdIsIgnoredWithAWarningWhereverItComesFrom()
    {
        var warnings = new List<string>();
        var settings = Catalog.Build(null, ["gravity=9.81", "collisionDamage=2"], warnings.Add);
        Assert.Equal(2f, Value(Catalog, settings, "collisionDamage"));
        Assert.Contains("gravity", Assert.Single(warnings));

        warnings.Clear();
        var path = TempFile("""{ "gravity": 9.81, "frobnicate": "yes", "hullStrength": 3 }""");
        try
        {
            settings = Catalog.Build(path, [], warnings.Add);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(3f, Value(Catalog, settings, "hullStrength"));
        Assert.Equal(2, warnings.Count);
        Assert.Contains("gravity", warnings[0]);

        // On the network: a server whose content has a setting this game does not.
        Span<byte> buffer = stackalloc byte[128];
        var writer = new NetWriter(buffer);
        writer.WriteByte(3);
        writer.WriteString("gravity");
        writer.WriteSingle(9.81f);
        writer.WriteString("hullStrength");
        writer.WriteSingle(3f);
        writer.WriteString("frobnicate");
        writer.WriteSingle(1f);
        var reader = new NetReader(writer.Written);
        var received = Catalog.Read(ref reader);
        Assert.True(reader.IsValid);
        Assert.Equal(settings, received);
    }

    [Fact]
    public void AValueOutOfRangeIsBroughtIntoRangeWhereverItComesFrom()
    {
        var warnings = new List<string>();
        var settings = Catalog.Build(null, ["collisionDamage=9", "shieldRecharge=0", "hullStrength=-3", "jumpChargeSpeed=1e9"], warnings.Add);
        Assert.Equal(4f, Value(Catalog, settings, "collisionDamage"));
        Assert.Equal(0.25f, Value(Catalog, settings, "shieldRecharge"));
        Assert.Equal(0.25f, Value(Catalog, settings, "hullStrength"));
        Assert.Equal(4f, Value(Catalog, settings, "jumpChargeSpeed"));
        Assert.Equal(4, warnings.Count);
        Assert.Contains("collisionDamage", warnings[0]);

        var path = TempFile("""{ "collisionDamage": 100, "shieldRecharge": -1 }""");
        try
        {
            settings = Catalog.Build(path, []);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(4f, Value(Catalog, settings, "collisionDamage"));
        Assert.Equal(0.25f, Value(Catalog, settings, "shieldRecharge"));

        // On the network, including numbers that are not numbers and options that do not exist.
        Span<byte> buffer = stackalloc byte[256];
        var writer = new NetWriter(buffer);
        writer.WriteByte(5);
        writer.WriteString("collisionDamage");
        writer.WriteSingle(float.PositiveInfinity);
        writer.WriteString("collisionShields");
        writer.WriteSingle(99f);
        writer.WriteString("shieldRecharge");
        writer.WriteSingle(float.NegativeInfinity);
        writer.WriteString("jumpChargeSpeed");
        writer.WriteSingle(float.NaN);
        writer.WriteString("hullStrength");
        writer.WriteSingle(-1e30f);
        var reader = new NetReader(writer.Written);
        settings = Catalog.Read(ref reader);
        Assert.True(reader.IsValid);
        Assert.Equal(4f, Value(Catalog, settings, "collisionDamage"));
        Assert.Equal(2f, Value(Catalog, settings, "collisionShields"));
        Assert.Equal(0.25f, Value(Catalog, settings, "shieldRecharge"));
        Assert.Equal(1f, Value(Catalog, settings, "jumpChargeSpeed"));
        Assert.Equal(0.25f, Value(Catalog, settings, "hullStrength"));

        // A value set directly goes through the same gate, and so does a whole set of values of unknown origin.
        var loose = default(WorldSettings);
        loose.Set(Catalog.Find("collisionShields")!, 1.4f);
        Assert.Equal(1f, Value(Catalog, loose, "collisionShields"));
        Assert.Equal(Catalog.Sanitize(loose), GameContent.Load().WithSettings(loose).Settings);
        Assert.Equal(0.25f, Value(Catalog, Catalog.Sanitize(loose), "hullStrength"));
    }

    [Fact]
    public void AValueOfTheWrongKindIsRefusedWithAMessageThatSaysWhatIsExpected()
    {
        var settings = Catalog.Defaults;
        var wrongOption = Assert.Throws<InvalidDataException>(() => Catalog.Build(null, ["collisionShields=Sturdy"]));
        Assert.Contains("collisionShields", wrongOption.Message);
        Assert.Contains("Full, Weak, None", wrongOption.Message);

        var notANumber = Assert.Throws<InvalidDataException>(() => Catalog.Build(null, ["collisionDamage=lots"]));
        Assert.Contains("a number from 0 to 4", notANumber.Message);

        Assert.Contains("id=value", Assert.Throws<InvalidDataException>(() => Catalog.Build(null, ["collisionDamage"])).Message);
        Assert.Contains("on or off", Assert.Throws<InvalidDataException>(() => Mixed.Build(null, ["pvp=maybe"])).Message);
        Assert.Throws<InvalidDataException>(() => Catalog.Build(null, ["collisionDamage=NaN"]));

        // Nothing was changed along the way.
        Assert.Equal(Catalog.Defaults, settings);
    }

    [Theory]
    [InlineData("""{ "collisionDamage": 2, """, "not valid JSON")]
    [InlineData("""[ "collisionDamage", 2 ]""", "expected one object")]
    [InlineData("""{ "collisionDamage": "lots" }""", "collisionDamage is \"lots\", which is not a number from 0 to 4")]
    [InlineData("""{ "collisionShields": 1 }""", "collisionShields is 1, which is not one of Full, Weak, None")]
    [InlineData("""{ "collisionShields": "Sturdy" }""", "which is not one of Full, Weak, None")]
    [InlineData("""{ "hullStrength": null }""", "hullStrength is null")]
    public void AMalformedValuesFileIsReportedWithItsPathAndWhatIsWrong(string text, string expected)
    {
        var path = TempFile(text);
        try
        {
            var error = Assert.Throws<InvalidDataException>(() => Catalog.Build(path, []));
            Assert.StartsWith(path, error.Message);
            Assert.Contains(expected, error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingValuesFileIsReportedByName()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prospect-no-such-file-{Guid.NewGuid():N}.json");
        var error = Assert.ThrowsAny<IOException>(() => Catalog.Build(path, []));
        Assert.Contains(path, error.Message);
    }

    [Theory]
    [InlineData("""{ "settings": [ """, "not valid JSON")]
    [InlineData("""{ "rules": [] }""", "needs a \"settings\" list")]
    [InlineData("""{ "settings": [ { "id": "a b", "name": "A", "description": "x", "kind": "switch", "default": true } ] }""", "letters and digits")]
    [InlineData("""{ "settings": [ { "id": "a", "description": "x", "kind": "switch", "default": true } ] }""", "setting \"a\": needs \"name\"")]
    [InlineData("""{ "settings": [ { "id": "a", "name": "A", "description": "x", "kind": "slider", "default": 1 } ] }""", "unknown kind \"slider\"")]
    [InlineData("""{ "settings": [ { "id": "a", "name": "A", "description": "x", "kind": "switch", "default": 1 } ] }""", "default of true or false")]
    [InlineData("""{ "settings": [ { "id": "a", "name": "A", "description": "x", "kind": "multiplier", "default": 5, "minimum": 0, "maximum": 4, "step": 1 } ] }""", "minimum <= default <= maximum")]
    [InlineData("""{ "settings": [ { "id": "a", "name": "A", "description": "x", "kind": "multiplier", "default": 1, "minimum": 0, "maximum": 4 } ] }""", "needs \"step\"")]
    [InlineData("""{ "settings": [ { "id": "a", "name": "A", "description": "x", "kind": "choice", "default": "One", "options": [] } ] }""", "list of 1 to 16 options")]
    [InlineData("""{ "settings": [ { "id": "a", "name": "A", "description": "x", "kind": "choice", "default": "Two", "options": [ { "name": "One" } ] } ] }""", "the default \"Two\" is not one of its options")]
    [InlineData("""{ "settings": [ { "id": "a", "name": "A", "description": "x", "kind": "choice", "default": "One", "options": [ { "name": "One" }, { "name": "one" } ] } ] }""", "listed twice")]
    [InlineData(
        """{ "settings": [ { "id": "a", "name": "A", "description": "x", "kind": "switch", "default": true }, { "id": "A", "name": "A", "description": "x", "kind": "switch", "default": true } ] }""",
        "defined twice"
    )]
    public void AMalformedSettingsListIsReportedWithItsSourceAndWhatIsWrong(string text, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => WorldSettingCatalog.Parse(Encoding.UTF8.GetBytes(text), "settings.json"));
        Assert.StartsWith("settings.json: ", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void ASettingTheGameAppliesHasToBeInTheList()
    {
        var error = Assert.Throws<InvalidDataException>(() => Mixed.Number(Mixed.Defaults, "collisionDamage"));
        Assert.Contains("mixed.json", error.Message);
        Assert.Contains("collisionDamage", error.Message);
    }

    /// <summary>A list of as many settings as a world can hold, each with an id as long as an id can be.</summary>
    private static string FullList(int count)
    {
        var text = new StringBuilder("""{ "settings": [""");
        for (var i = 0; i < count; i++)
        {
            var id = $"setting{i:00}".PadRight(WorldSettingCatalog.MaxIdLength, 'x');
            text.Append(i == 0 ? "" : ",");
            text.Append($$"""{ "id": "{{id}}", "name": "S", "description": "x", "kind": "multiplier", "default": 1, "minimum": 0, "maximum": {{i + 1}}, "step": 1 }""");
        }

        return text.Append("]}").ToString();
    }

    [Fact]
    public void TheMostSettingsAWorldCanHaveFitTheWelcomeMessage()
    {
        var catalog = WorldSettingCatalog.Parse(Encoding.UTF8.GetBytes(FullList(WorldSettings.Capacity)), "full.json");
        var settings = catalog.Defaults;
        foreach (var setting in catalog.Definitions)
            settings.Set(setting, setting.Maximum);

        Span<byte> buffer = stackalloc byte[WelcomeMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        new WelcomeMessage { ServerName = new string('W', ProtocolInfo.MaxNameChars), Settings = settings }.Write(ref writer, catalog);
        Assert.InRange(writer.Length, WorldSettingCatalog.MaxWireSize, WelcomeMessage.MaxWireSize);

        var reader = new NetReader(writer.Written[1..]);
        Assert.True(WelcomeMessage.TryRead(ref reader, catalog, out var welcome));
        Assert.Equal(settings, welcome.Settings);
        Assert.Equal(0, reader.Remaining);

        var tooMany = Assert.Throws<InvalidDataException>(() => WorldSettingCatalog.Parse(Encoding.UTF8.GetBytes(FullList(WorldSettings.Capacity + 1)), "full.json"));
        Assert.Contains("at most 32", tooMany.Message);
    }

    [Fact]
    public void GarbageFromTheNetworkNeverThrowsAndAlwaysLeavesUsableSettings()
    {
        var random = new Random(4321);
        var junk = new byte[96];
        for (var round = 0; round < 5000; round++)
        {
            random.NextBytes(junk);
            var reader = new NetReader(junk.AsSpan(0, random.Next(0, junk.Length)));
            var settings = Catalog.Read(ref reader);
            Assert.Equal(Catalog.Sanitize(settings), settings);
        }

        // A message that names real settings, with bytes for values and an end that comes too soon.
        var buffer = new byte[WorldSettingCatalog.MaxWireSize];
        for (var round = 0; round < 5000; round++)
        {
            var writer = new NetWriter(buffer);
            writer.WriteByte((byte)random.Next(0, 256));
            foreach (var setting in Catalog.Definitions)
            {
                writer.WriteString(random.Next(4) == 0 ? "unknown" : setting.Id);
                random.NextBytes(junk.AsSpan(0, 4));
                writer.WriteBytes(junk.AsSpan(0, 4));
            }

            var reader = new NetReader(buffer.AsSpan(0, random.Next(0, writer.Length + 1)));
            var settings = Catalog.Read(ref reader);
            Assert.Equal(Catalog.Sanitize(settings), settings);
            foreach (var setting in Catalog.Definitions)
                Assert.Equal(setting.Clamp(settings.Get(setting)), settings.Get(setting));
        }

        // A welcome cut short is refused as a whole; the settings read so far are still usable.
        var cut = new NetReader([1, 2, 3]);
        Assert.False(WelcomeMessage.TryRead(ref cut, Catalog, out var welcome));
        Assert.Equal(Catalog.Defaults, welcome.Settings);
    }
}

/// <summary>Real servers with settings of their own, and the clients that join them.</summary>
public sealed class WorldSettingSessionTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static HullView WaitFor(GameSession session, Func<HullView, bool> condition, int seconds)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var snapshot = session.Client.AcquireSnapshot();
            if (snapshot.OwnHull >= 0 && condition(snapshot.Hulls[snapshot.OwnHull]))
                return snapshot.Hulls[snapshot.OwnHull];
            Assert.Null(session.Fault);
            Thread.Sleep(5);
        }

        throw new TimeoutException("The hull did not reach the expected state");
    }

    [Fact]
    public void AClientPlaysByTheSettingsOfTheServerItJoinsAndNotByItsOwnFiles()
    {
        var catalog = Content.SettingCatalog;
        var settings = catalog.Build(null, ["collisionDamage=0.5", "collisionShields=Weak", "shieldRecharge=0.5", "jumpChargeSpeed=4", "hullStrength=2"]);
        var port = (ushort)(51000 + Environment.ProcessId % 2000);
        using var server = new ServerHost(new ServerConfig { MaxPlayers = 2, Settings = settings }, Content, RakNetTransport.Listen(port, 8));
        server.Start();

        // The client is given the content as loaded: nothing on its side says what the server chose.
        using var session = GameSession.Join("127.0.0.1", port, Content, "Pilot");
        var pod = WaitFor(session, _ => true, 10);
        var snapshot = session.Client.AcquireSnapshot();
        var world = server.Content;

        Assert.Equal(settings, world.Settings);
        Assert.Equal(settings, snapshot.Content.Settings);
        Assert.NotEqual(Content.Settings, snapshot.Content.Settings);
        Assert.Equal(world.Designs.Length, snapshot.Content.Designs.Length);
        for (var i = 0; i < world.Designs.Length; i++)
        {
            Assert.Equal(world.Designs[i].Flight, snapshot.Content.Designs[i].Flight);
            Assert.Equal(world.Designs[i].Stats, snapshot.Content.Designs[i].Stats);
        }

        // The figures the client predicts its own hull with, and the hull the server gave it.
        Assert.Equal(world.Pod.Flight, snapshot.Flight);
        Assert.Equal(Content.Pod.Flight.HullPoints * 2, snapshot.Flight.HullPoints);
        Assert.Equal(Content.Pod.Flight.FtlPower * 4, snapshot.Flight.FtlPower);
        Assert.Equal(4f, snapshot.Flight.CollisionShieldCost);
        Assert.Equal(world.Pod.Flight.HullPoints, pod.Hull);

        // The content the client was started with is as it was, for the next server it joins.
        Assert.Equal(catalog.Defaults, Content.Settings);
        Assert.Equal(1f, Content.Pod.Flight.CollisionShieldCost);

        // Charging uses the server multiplier; calculation still sets the minimum jump duration.
        var galaxy = snapshot.Galaxy!;
        // Keep this settings test nearby; long-distance transit is covered by manual session ticks.
        var far = new SectorCoord(1, 0, -1);
        session.Client.Input.IssueOrder(new HullOrder { Kind = OrderKind.Jump, Target = galaxy.CenterOf(far) });
        WaitFor(session, hull => hull.Jump == JumpPhase.Charge, 8);
        var charging = Stopwatch.GetTimestamp();
        var worst = 0f;
        while (session.Client.AcquireSnapshot() is { OwnHull: >= 0 } now && now.Hulls[now.OwnHull].Jump == JumpPhase.Charge && Stopwatch.GetElapsedTime(charging).TotalSeconds < 20)
        {
            worst = Math.Max(worst, now.PredictionError);
            Thread.Sleep(5);
        }

        var seconds = (float)Stopwatch.GetElapsedTime(charging).TotalSeconds;
        var expected = Math.Max(world.Pod.Flight.FtlCalculateSeconds, world.Pod.Flight.FtlEnergy / world.Pod.Flight.FtlPower);
        Assert.InRange(seconds, expected - 0.5f, expected + 0.5f);
        Assert.Equal(Content.Pod.Flight.FtlCalculateSeconds, world.Pod.Flight.FtlCalculateSeconds);

        var arrived = WaitFor(session, hull => hull.Jump == JumpPhase.None && galaxy.SectorOf(hull.Position) == far, 20);
        Assert.Equal(pod.Id, arrived.Id);
        Thread.Sleep(500);
        worst = Math.Max(worst, session.Client.AcquireSnapshot().PredictionError);
        Assert.True(worst < 0.5f, $"prediction was off by up to {worst:0.000} m");
        Assert.Null(session.Fault);
        Assert.Null(server.Fault);
    }

    [Fact]
    public void RammingAFleetHullUnderOtherSettingsDamagesByThoseSettingsAndThePredictionHolds()
    {
        var settings = Content.SettingCatalog.Build(null, ["collisionDamage=2", "collisionShields=Weak", "shieldRecharge=0.25", "hullStrength=2"]);
        using var session = GameSession.StartLocal(new ServerConfig { MaxPlayers = 1, Settings = settings }, Content, "Pilot");
        var pod = WaitFor(session, _ => true, 10);
        var world = session.Client.AcquireSnapshot().Content;
        Assert.Equal(settings, world.Settings);
        Assert.Equal(world.Pod.Flight, session.Client.AcquireSnapshot().Flight);

        HullView Vessel(uint id = 0)
        {
            var snapshot = session.Client.AcquireSnapshot();
            return snapshot.Hulls.Take(snapshot.HullCount).FirstOrDefault(h => !h.IsOwn && (id == 0 || h.Id == id));
        }

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (Vessel().Id == 0 && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(20);
        var parked = Vessel();
        Assert.NotEqual(0u, parked.Id);
        var vesselFlight = world.Designs[parked.Design].Flight;
        Assert.Equal(vesselFlight.ShieldCapacity, parked.Shield);
        Assert.Equal(Content.Designs[parked.Design].Flight.HullPoints * 2, parked.Hull);

        // Back into the vessel on the thrusters, as the ram at the default settings does, and keep them on for a
        // while after the impact so the bounces and the shoving that follow count too.
        var toVessel = Vector3.Normalize(parked.Position.RelativeTo(pod.Position));
        session.Client.Input.Submit(new PlayerControls { Strafe = toVessel.X, Forward = -toVessel.Z });
        var worst = 0f;
        var struckAt = 0L;
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 15 && (struckAt == 0 || Stopwatch.GetElapsedTime(struckAt).TotalSeconds < 2))
        {
            worst = Math.Max(worst, session.Client.AcquireSnapshot().PredictionError);
            if (struckAt == 0 && Vessel(parked.Id).Shield < vesselFlight.ShieldCapacity)
                struckAt = Stopwatch.GetTimestamp();
            Thread.Sleep(5);
        }

        session.Client.Input.Submit(default);
        Assert.True(struckAt != 0, "the pod never reached the vessel");

        var after = Vessel(parked.Id);
        var flown = session.Client.AcquireSnapshot() is { OwnHull: >= 0 } last ? last.Hulls[last.OwnHull] : default;
        Assert.Equal(pod.Id, flown.Id);
        Assert.True(after.Position.RelativeTo(parked.Position).Length() > 1f, "the vessel was not moved");
        Assert.True(after.Shield < vesselFlight.ShieldCapacity, "the vessel was not damaged");
        Assert.True(flown.Shield < world.Pod.Flight.ShieldCapacity, "the pod was not damaged");

        // The same bound the ram at the default settings keeps: the client collided by the server's figures.
        Assert.True(worst < 0.15f, $"prediction was off by up to {worst:0.000} m");
        Assert.Null(session.Fault);
    }
}
