using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Designs built straight from boxes for tests of storage, sums and damage; the loader admits none of them yet.</summary>
internal static class TestDesigns
{
    public static readonly GameContent Content = GameContent.Load();
    private static readonly ConcurrentDictionary<int, HullDesign> CubeDesigns = new();
    private static readonly ConcurrentDictionary<(int, uint), HullDesign> MixedDesigns = new();
    private static readonly Lazy<HullDesign> StationDesign = new(() => Mixed(150_000, 1234));

    /// <summary>
    /// A design of this many unit cubes in layers of fifty by fifty. Block 0 is its core, the next few a
    /// generator, a thruster, a gyro, a shield emitter and a drive as far as the count allows; the rest is plating.
    /// </summary>
    public static HullDesign Cubes(int blocks) =>
        CubeDesigns.GetOrAdd(
            blocks,
            static count =>
            {
                string[] working = ["core", "generator", "thruster", "gyro", "shield", "hyperdrive"];
                var boxes = new DesignBox[count];
                for (var i = 0; i < count; i++)
                    boxes[i] = new DesignBox(new GridSize(i % 50, i / 2500, i / 50 % 50), new GridSize(1, 1, 1), Content.Blocks.Get(i < working.Length ? working[i] : "hull"));
                return Of(boxes);
            }
        );

    /// <summary>A seeded design of boxes of mixed sizes and types, as <see cref="BoxDesignTests.Generate"/> makes them. Block 0 is its only core.</summary>
    public static HullDesign Mixed(int blocks, uint seed) => MixedDesigns.GetOrAdd((blocks, seed), static key => Of(BoxDesignTests.Generate(key.Item1, key.Item2)));

    /// <summary>A station of 150,000 boxes of mixed sizes. Validating it takes seconds, so it is built once.</summary>
    public static HullDesign Station => StationDesign.Value;

    /// <summary>A design of exactly these boxes, under the content's rules.</summary>
    public static HullDesign Of(params DesignBox[] boxes) => HullDesign.Of(new BoxDesign("Test", 1f, boxes, Content.Blocks.Limits, "test"), Content.Rules);
}

/// <summary>
/// Comparing hull figures, and working them out a second way: a plain scan of every block in double precision,
/// with the system's own power function for the size rules. It shares nothing with the running sums but the
/// definitions, so it is what they are checked against, within the rounding of single precision.
/// </summary>
internal static class Figures
{
    /// <summary>How far the running sums may be from the reference, as a share of the figure.</summary>
    public const double ReferenceTolerance = 1e-6;

    /// <summary>True if two spans of sums hold the same bytes.</summary>
    public static bool SameSums(ReadOnlySpan<HullSum> a, ReadOnlySpan<HullSum> b) => MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));

    /// <summary>Fails unless every field of two sets of figures is the same to the last bit.</summary>
    public static void AssertIdentical<T>(T expected, T actual)
        where T : struct
    {
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var a = field.GetValue(expected)!;
            var b = field.GetValue(actual)!;
            var same = (a, b) switch
            {
                (float x, float y) => BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y),
                (Vector3 x, Vector3 y) => BitConverter.SingleToInt32Bits(x.X) == BitConverter.SingleToInt32Bits(y.X)
                    && BitConverter.SingleToInt32Bits(x.Y) == BitConverter.SingleToInt32Bits(y.Y)
                    && BitConverter.SingleToInt32Bits(x.Z) == BitConverter.SingleToInt32Bits(y.Z),
                _ => a.Equals(b),
            };
            Assert.True(same, $"{typeof(T).Name}.{field.Name}: expected {a}, got {b}");
        }
    }

    /// <summary>Fails unless every field of two sets of figures agrees within a share of its size.</summary>
    public static void AssertClose<T>(T expected, T actual, double tolerance = ReferenceTolerance)
        where T : struct
    {
        static bool Close(float x, float y, double tolerance) => Math.Abs((double)x - y) <= tolerance * Math.Max(Math.Abs((double)x), Math.Abs((double)y));

        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var a = field.GetValue(expected)!;
            var b = field.GetValue(actual)!;
            var same = (a, b) switch
            {
                (float x, float y) => Close(x, y, tolerance),
                (Vector3 x, Vector3 y) => Close(x.X, y.X, tolerance) && Close(x.Y, y.Y, tolerance) && Close(x.Z, y.Z, tolerance),
                _ => a.Equals(b),
            };
            Assert.True(same, $"{typeof(T).Name}.{field.Name}: expected {a}, got {b}");
        }
    }

    /// <summary>The figures of a hull with the blocks of <paramref name="alive"/>, by a scan of every block.</summary>
    public static void Reference(HullDesign design, FlightRules rules, ReadOnlySpan<ulong> alive, out FlightParams flight, out HullStats stats)
    {
        static double Scaled(BlockType type, string stat, double value, long cells) =>
            cells == 1 ? value : value * Math.Pow(cells, type.SizeRules.TryGetValue(stat, out var exponent) ? exponent : 1.0);

        var size = (double)design.BlockSize;
        var pivot = design.Pivot;
        double mass = 0, hull = 0, shield = 0, recharge = 0, engineThrust = 0, thrusterThrust = 0, torque = 0;
        double generated = 0, idlePower = 0, thrustPower = 0, shieldPower = 0, storage = 0, cargo = 0, cells = 0, crew = 0;
        double ix = 0, iy = 0, iz = 0, radius = 0;
        var min = new Double3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new Double3(double.MinValue, double.MinValue, double.MinValue);
        var blocks = 0;
        var ftl = FtlKind.None;
        var ftlPower = 0.0;
        var ftlMaxDistance = 0f;
        var boxes = design.Boxes;
        for (var i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            var volume = (long)box.Size.X * box.Size.Y * box.Size.Z;
            var x = (box.At.X + (box.Size.X - 1) / 2.0 - pivot.X) * size;
            var y = (box.At.Y + (box.Size.Y - 1) / 2.0 - pivot.Y) * size;
            var z = (box.At.Z + (box.Size.Z - 1) / 2.0 - pivot.Z) * size;
            var (sx, sy, sz) = (box.Size.X * size, box.Size.Y * size, box.Size.Z * size);

            // The collision sphere is that of the intact hull, a unit cube's corner taken at 0.87 of its side.
            radius = Math.Max(radius, Math.Sqrt(x * x + y * y + z * z) + 0.87f * size * Math.Sqrt((box.Size.X * box.Size.X + box.Size.Y * box.Size.Y + box.Size.Z * box.Size.Z) / 3.0));
            if (!BlockSet.Has(alive, i))
                continue;

            var type = box.Type;
            var m = Scaled(type, "mass", type.Mass, volume);
            blocks++;
            cells += volume;
            mass += m;
            hull += Scaled(type, "hull", type.Hull, volume);
            shield += Scaled(type, "shield", type.Shield, volume);
            recharge += Scaled(type, "shieldRecharge", type.ShieldRecharge, volume);
            torque += Scaled(type, "torque", type.Torque, volume);
            generated += Scaled(type, "powerOutput", type.PowerOutput, volume);
            storage += Scaled(type, "energyStorage", type.EnergyStorage, volume);
            cargo += Scaled(type, "cargo", type.Cargo, volume);
            crew += Math.Floor(Scaled(type, "crew", type.Crew, volume));
            if (ftl == FtlKind.None)
            {
                ftl = type.Ftl;
                ftlMaxDistance = type.FtlMaxDistance;
            }
            if (type.Ftl == ftl && ftl != FtlKind.None)
                ftlPower += Scaled(type, "ftlPower", type.FtlPower, volume);

            var thrust = Scaled(type, "thrust", type.Thrust, volume);
            if (type.ForwardOnly)
                engineThrust += thrust;
            else
                thrusterThrust += thrust;

            var draw = Scaled(type, "powerDraw", type.PowerDraw, volume);
            if (type.Thrust > 0)
                thrustPower += draw;
            else if (type.Shield > 0)
                shieldPower += draw;
            else
                idlePower += draw;

            ix += m * (y * y + z * z + (sy * sy + sz * sz) / 12);
            iy += m * (x * x + z * z + (sx * sx + sz * sz) / 12);
            iz += m * (x * x + y * y + (sx * sx + sy * sy) / 12);
            min = new Double3(Math.Min(min.X, box.At.X), Math.Min(min.Y, box.At.Y), Math.Min(min.Z, box.At.Z));
            max = new Double3(Math.Max(max.X, box.At.X + box.Size.X), Math.Max(max.Y, box.At.Y + box.Size.Y), Math.Max(max.Z, box.At.Z + box.Size.Z));
        }

        var massFloor = Math.Max(mass, 0.01f);
        var inertia = new Double3(Math.Max(ix, 0.001f), Math.Max(iy, 0.001f), Math.Max(iz, 0.001f));
        var lateral = thrusterThrust / massFloor;
        var forward = (engineThrust + thrusterThrust) / massFloor;
        var turnAcceleration = new Vector3((float)(torque / inertia.X), (float)(torque / inertia.Y), (float)(torque / inertia.Z));
        var turnRate = new Vector3(
            (float)(Math.Sqrt(torque / inertia.X) * rules.TurnRatePerRootAcceleration),
            (float)(Math.Sqrt(torque / inertia.Y) * rules.TurnRatePerRootAcceleration),
            (float)(Math.Sqrt(torque / inertia.Z) * rules.TurnRatePerRootAcceleration)
        );
        var maxSpeed = rules.SpeedPerRootAcceleration * Math.Sqrt(forward);

        flight = new FlightParams
        {
            Acceleration = new Vector3((float)lateral, (float)lateral, (float)forward),
            ReverseAcceleration = (float)lateral,
            BoostMultiplier = rules.BoostThrustMultiplier,
            MaxSpeed = (float)maxSpeed,
            BoostMaxSpeed = (float)(maxSpeed * rules.BoostSpeedMultiplier),
            TurnRate = turnRate,
            TurnAcceleration = turnAcceleration,
            HeatRate = 1f / rules.BoostHeatSeconds,
            CoolRate = 1f / rules.CoolSeconds,
            Mass = (float)massFloor,
            Inertia = new Vector3((float)inertia.X, (float)inertia.Y, (float)inertia.Z),
            Radius = (float)radius,
            HullPoints = (float)(hull * rules.HullStrength),
            ShieldCapacity = (float)shield,
            ShieldRecharge = (float)(recharge * rules.ShieldRechargeMultiplier),
            ShieldDelay = rules.ShieldRechargeDelaySeconds,
            PowerGenerated = (float)generated,
            IdlePower = (float)idlePower,
            ThrustPower = (float)thrustPower,
            ShieldPower = (float)(shieldPower * rules.ShieldRechargeMultiplier),
            ShieldRestartFraction = rules.ShieldRestartFraction,
            ShieldContactSpeed = rules.ShieldContactSpeed,
            ShieldRestartPower = (float)(shieldPower * rules.ShieldRechargeMultiplier * rules.ShieldRestartPowerMultiplier),
            BoostPowerMultiplier = rules.BoostPowerMultiplier,
            EnergyStorage = (float)storage,
            BoostEnergyFloor = (float)(rules.BoostEnergyFloor * storage),
            CollisionSafeSpeed = rules.CollisionSafeSpeed,
            CollisionDamagePerTonneSpeedSquared = rules.CollisionDamagePerTonneSpeedSquared,
            CollisionBounce = rules.CollisionBounce,
            CollisionShieldCost = rules.CollisionShieldCost,
            Ftl = ftl,
            FtlPower = (float)(ftlPower * rules.FtlChargeMultiplier),
            FtlMaxDistance = ftlMaxDistance,
            FtlEnergy = (float)(rules.FtlEnergyPerTonne * massFloor),
            FtlAlignCosine = MathF.Cos(rules.FtlAlignDegrees * MathF.PI / 180f),
            FtlCalculateSeconds = rules.FtlCalculateSeconds,
            FtlTunnelSeconds = rules.FtlTunnelSeconds,
            FtlSpeed = rules.FtlSpeed,
            FtlArriveSeconds = rules.FtlArriveSeconds,
        };

        stats = new HullStats
        {
            HullPoints = flight.HullPoints,
            ShieldCapacity = flight.ShieldCapacity,
            ShieldRecharge = flight.ShieldRecharge,
            Mass = flight.Mass,
            Volume = (float)(cells * size * size * size),
            Size = blocks > 0 ? new Vector3((float)((max.X - min.X) * size), (float)((max.Y - min.Y) * size), (float)((max.Z - min.Z) * size)) : Vector3.Zero,
            Blocks = blocks,
            CargoHold = (float)cargo,
            Crew = (int)crew,
            Acceleration = (float)forward,
            Deceleration = (float)lateral,
            LateralAcceleration = (float)lateral,
            MaxSpeed = flight.MaxSpeed,
            BoostMaxSpeed = flight.BoostMaxSpeed,
            TurnRate = turnRate,
            PowerRequired = (float)(idlePower + thrustPower + shieldPower * rules.ShieldRechargeMultiplier),
            PowerGenerated = flight.PowerGenerated,
            EnergyStorage = flight.EnergyStorage,
            Ftl = ftl,
            FtlEnergy = flight.FtlEnergy,
            FtlPower = flight.FtlPower,
            FtlMaxDistance = flight.FtlMaxDistance,
        };
    }
}

/// <summary>
/// A hull's running sums: one leaf for every group of blocks, figures that are a pure function of the blocks
/// left, and the same code in a block state store on the server and on a predicting client.
/// </summary>
public sealed class HullSumsTests
{
    private static readonly GameContent Content = TestDesigns.Content;

    private static int[] Shuffled(int count, uint seed)
    {
        var order = Enumerable.Range(0, count).ToArray();
        for (var i = order.Length - 1; i > 0; i--)
        {
            seed = unchecked(seed * 1664525 + 1013904223);
            var j = (int)(seed % (uint)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    /// <summary>
    /// Checks a hull's running sums against sums built afresh for the same blocks, to the last bit, and both
    /// against the reference scan within its tolerance.
    /// </summary>
    private static void Check(HullDesign design, ReadOnlySpan<HullSum> running, ReadOnlySpan<ulong> alive, bool reference = true)
    {
        var fresh = new HullSum[design.Sums.NodeCount];
        design.Sums.Build(fresh, alive);
        Assert.True(Figures.SameSums(fresh, running), "the running sums differ from sums built for the same blocks");
        design.Figures(running, out var flight, out var stats);
        design.Figures(fresh, out var freshFlight, out var freshStats);
        Figures.AssertIdentical(freshFlight, flight);
        Figures.AssertIdentical(freshStats, stats);
        Assert.Equal(BlockSet.Count(alive), stats.Blocks);
        if (!reference)
            return;

        Figures.Reference(design, Content.Rules, alive, out var expected, out var expectedStats);
        Figures.AssertClose(expected, flight);
        Figures.AssertClose(expectedStats, stats);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(4096)]
    public void AnyOrderOfLossesGivesTheFiguresOfTheBlocksThatAreLeft(int blocks)
    {
        var design = TestDesigns.Mixed(blocks, 77);
        var sums = design.Sums;
        Assert.Equal(blocks, sums.BlockCount);
        Assert.Equal(2 * ((blocks + BoxHullSums.GroupSize - 1) / BoxHullSums.GroupSize) - 1, sums.NodeCount);

        // Intact: a copy of the design's sums gives the design's own figures, bit for bit.
        var first = sums.Intact.ToArray();
        var second = sums.Intact.ToArray();
        var alive = design.FullSet.ToArray();
        design.Figures(first, out var intact, out var intactStats);
        Figures.AssertIdentical(design.Flight, intact);
        Figures.AssertIdentical(design.Stats, intactStats);
        Check(design, first, alive);

        // Every block goes, in one order and in another. Wherever the two hulls have lost the same blocks
        // their sums are the same bytes; here that is checked at the halfway set and at the end.
        var order = Shuffled(blocks, 82);
        var half = order.Take(blocks / 2).Order().ToArray();
        var aliveSecond = design.FullSet.ToArray();
        foreach (var block in Enumerable.Reverse(half))
        {
            BlockSet.Remove(aliveSecond, block);
            sums.Lose(second, aliveSecond, block);
        }

        var every = Math.Max(1, blocks / 24);
        for (var i = 0; i < order.Length; i++)
        {
            BlockSet.Remove(alive, order[i]);
            sums.Lose(first, alive, order[i]);

            // Losing it again changes nothing.
            sums.Lose(first, alive, order[i]);
            if (i + 1 == half.Length)
            {
                Assert.True(BlockSet.Equal(alive, aliveSecond));
                Assert.True(Figures.SameSums(first, second), "two orders of the same losses gave different sums");
            }

            if (i % every == 0 || i + 1 == half.Length || i >= order.Length - 2)
                Check(design, first, alive, reference: blocks <= 65 || i % (every * 6) == 0 || i >= order.Length - 2);
        }

        // Nothing left: no mass but the floor, no blocks, no drive.
        design.Figures(first, out var none, out var noneStats);
        Assert.Equal(0, noneStats.Blocks);
        Assert.Equal(0.01f, none.Mass);
        Assert.Equal(FtlKind.None, none.Ftl);
        Assert.Equal(Vector3.Zero, noneStats.Size);
        Assert.Equal(design.Flight.Radius, none.Radius);
    }

    [Fact]
    public void AHullIsNotDestroyedWhenAnEnormousBlockGoesAndRoundsTheOthersOutOfTheTotal()
    {
        // Beside a block of 1e20 hit points the core's own do not show in the total at all. Losing the big block
        // leaves a total of nothing by subtraction; the hull still has its core and is still there.
        var plate = new BlockType { Id = "plate", Name = "plate", Mass = 1, Hull = 1e20f };
        var design = TestDesigns.Of(
            new DesignBox(new GridSize(0, 0, 0), new GridSize(1, 1, 1), Content.Blocks.Get("core")),
            new DesignBox(new GridSize(1, 0, 0), new GridSize(1, 1, 1), plate));
        using var states = new BlockStateStore();
        var body = states.Body(design);
        var state = HullState.Spawn(Double3.Zero, design.Flight);
        state.ShieldOffline = true;

        Assert.False(HullSim.Damage(ref state, design.Flight, ref body, [1UL << 1], 1, float.MaxValue));
        Assert.False(BlockSet.Has(states.Alive(body.State), 1));
        Assert.True(BlockSet.Has(states.Alive(body.State), 0));
        Assert.Equal(0, states.CoresLost(body.State));

        // Its core going is what destroys it.
        Assert.True(HullSim.Damage(ref state, design.Flight, ref body, [1UL << 0], 1, float.MaxValue));
    }

    [Fact]
    public void TheEdgesOfAGroupAndAWholeGroupAreLostLikeAnyOtherBlocks()
    {
        // A hundred blocks are four groups: 0 to 31, 32 to 63, 64 to 95 and the short one, 96 to 99.
        var design = TestDesigns.Mixed(100, 5);
        var sums = design.Sums;
        Assert.Equal(7, sums.NodeCount);
        var running = sums.Intact.ToArray();
        var alive = design.FullSet.ToArray();

        void Lose(int block)
        {
            BlockSet.Remove(alive, block);
            sums.Lose(running, alive, block);
            Check(design, running, alive);
        }

        // The first and the last block of a group, then the rest of it, so the group is empty.
        Lose(32);
        Lose(63);
        for (var block = 33; block < 63; block++)
            Lose(block);

        // The first and last block of the design, the edges of its other groups, and the short group whole.
        foreach (var block in new[] { 0, 99, 31, 64, 95, 96, 98, 97 })
            Lose(block);

        // Every block that is left, from the top down.
        for (var block = 99; block >= 0; block--)
        {
            if (BlockSet.Has(alive, block))
                Lose(block);
        }

        Assert.False(BlockSet.Any(alive));
        Assert.Throws<ArgumentOutOfRangeException>(() => sums.Lose(running, alive, 100));
        Assert.Throws<ArgumentException>(() => sums.Lose(running.AsSpan(1), alive, 0));
    }

    [Fact]
    public void LosingEveryCoreLeavesFiguresForWhatIsLeft()
    {
        var blocks = new DesignBox[80];
        for (var i = 0; i < blocks.Length; i++)
        {
            var type = i switch
            {
                3 or 40 or 79 => "core",
                1 => "generator",
                2 => "thruster",
                4 => "gyro",
                _ => "hull",
            };
            blocks[i] = new DesignBox(new GridSize(i * 2, 0, 0), new GridSize(2, 1 + i % 3, 1), Content.Blocks.Get(type));
        }

        var design = TestDesigns.Of(blocks);
        Assert.Equal(3, BlockSet.Count(design.CoreSet));
        using var store = new BlockStateStore();
        var state = store.Create(design, hitPoints: true);
        foreach (var core in new[] { 40, 3, 79 })
        {
            Assert.True(store.Lose(state, core));
            Check(design, store.Sums(state), store.Alive(state));
        }

        Assert.Equal(3, store.CoresLost(state));
        design.Figures(store.Sums(state), out var flight, out var stats);
        Assert.Equal(77, stats.Blocks);
        Assert.True(flight.Mass < design.Flight.Mass);

        // The cores drew power all the time; the generator's output is untouched.
        Assert.True(flight.IdlePower < design.Flight.IdlePower);
        Assert.Equal(design.Flight.PowerGenerated, flight.PowerGenerated);
    }

    [Fact]
    public void TheFirstDriveLeftInFileOrderDecidesTheKindWhereverItsGroupSitsInTheTree()
    {
        // Seventy blocks are three groups, and a tree of three leaves does not keep them in file order from left
        // to right. The drives sit one in each group: a rift cutter first, then two hyperspace drives.
        var blocks = new DesignBox[70];
        for (var i = 0; i < blocks.Length; i++)
            blocks[i] = new DesignBox(new GridSize(i, 0, 0), new GridSize(1, 1, 1), Content.Blocks.Get(i switch { 0 => "core", 3 => "riftcutter", 40 or 66 => "hyperdrive", _ => "hull" }));
        var design = TestDesigns.Of(blocks);
        var sums = design.Sums;
        var running = sums.Intact.ToArray();
        var alive = design.FullSet.ToArray();

        FlightParams After(int lost)
        {
            BlockSet.Remove(alive, lost);
            sums.Lose(running, alive, lost);
            Check(design, running, alive);
            design.Figures(running, out var flight, out _);
            return flight;
        }

        Assert.Equal(FtlKind.Rift, design.Flight.Ftl);
        Assert.Equal(10f, design.Flight.FtlPower);

        // With the rift cutter gone the next drive in the file decides, and both drives of its kind supply power.
        var next = After(3);
        Assert.Equal(FtlKind.Hyperspace, next.Ftl);
        Assert.Equal(16f, next.FtlPower);
        var last = After(40);
        Assert.Equal(FtlKind.Hyperspace, last.Ftl);
        Assert.Equal(8f, last.FtlPower);
        var none = After(66);
        Assert.Equal(FtlKind.None, none.Ftl);
        Assert.Equal(0f, none.FtlPower);
    }

    [Fact]
    public void ThePivotNeverMovesWhateverIsLost()
    {
        var design = TestDesigns.Of(
            new DesignBox(new GridSize(0, 0, 0), new GridSize(1, 1, 1), Content.Blocks.Get("core")),
            new DesignBox(new GridSize(3, 0, 0), new GridSize(4, 2, 2), Content.Blocks.Get("hull")),
            new DesignBox(new GridSize(-9, 0, 0), new GridSize(2, 2, 1), Content.Blocks.Get("engine"))
        );
        var pivot = design.Pivot;
        var offsets = design.BlockOffsets.ToArray();
        var sums = design.Sums;
        var running = sums.Intact.ToArray();
        var alive = design.FullSet.ToArray();
        foreach (var block in new[] { 1, 2 })
        {
            BlockSet.Remove(alive, block);
            sums.Lose(running, alive, block);
        }

        // Only the core is left: one cube of three tonnes, still turning about the intact hull's centre.
        design.Figures(running, out var flight, out _);
        Assert.Equal(pivot, design.Pivot);
        Assert.Equal(offsets, design.BlockOffsets);
        Assert.NotEqual(0.0, pivot.X);
        Assert.Equal((float)(3 * (pivot.X * pivot.X + pivot.Z * pivot.Z + 1.0 / 6)), flight.Inertia.Y);
        Assert.Equal(design.Flight.Radius, flight.Radius);

        // A box's centre is the middle of its cells, and its half extents are in metres.
        Assert.Equal(new Vector3((float)(4.5 - pivot.X), (float)(0.5 - pivot.Y), (float)(0.5 - pivot.Z)), design.BlockOffsets[1]);
        Assert.Equal(new Vector3(2f, 1f, 1f), design.BlockHalfExtents[1]);
        Assert.Equal(new Vector3(0.5f), design.BlockHalfExtents[0]);
        Assert.Equal(design.Boxes[2].Type.Hull * 4, design.BlockHitPoints(2));
    }

    [Fact]
    public void OtherRulesChangeTheFiguresAndTheHitPointsAndShareTheSums()
    {
        var design = TestDesigns.Mixed(300, 9);
        var rules = Content.Rules with { HullStrength = 2.5f, ShieldRechargeMultiplier = 0.5f, FtlChargeMultiplier = 3f };
        var changed = design.WithRules(rules);
        Assert.Same(design.Sums, changed.Sums);
        Assert.Equal(design.Flight.HullPoints * 2.5f, changed.Flight.HullPoints);
        Assert.Equal(design.Flight.ShieldRecharge * 0.5f, changed.Flight.ShieldRecharge);
        Assert.Equal(design.Flight.FtlPower * 3f, changed.Flight.FtlPower);
        Assert.Equal(design.Flight.Mass, changed.Flight.Mass);
        Assert.Equal(design.Flight.Inertia, changed.Flight.Inertia);
        for (var b = 0; b < design.BlockCount; b++)
            Assert.Equal(design.BlockHitPoints(b) * 2.5f, changed.BlockHitPoints(b));

        // The original is left as it was, and one hull's sums read under either world's rules.
        Assert.Equal(Content.Rules.HullStrength * (float)design.Sums.HullPoints(0), design.BlockHitPoints(0));
        Figures.Reference(changed, rules, changed.FullSet, out var expected, out var expectedStats);
        Figures.AssertClose(expected, changed.Flight);
        Figures.AssertClose(expectedStats, changed.Stats);
    }

    [Fact]
    public void AHullsStateTakesTheMemoryItsDesignSaysAndTheDesignKeepsItsShareOnce()
    {
        Assert.Equal(200, BoxHullSums.NodeBytes);
        (int Blocks, long Sums, long Predicted, long Decided)[] expected =
        [
            (64, 600, 1_404, 1_660),
            (300, 3_800, 7_468, 8_668),
            (4_096, 51_000, 100_692, 117_076),
            (150_000, 1_875_000, 3_693_780, 4_293_780),
        ];
        foreach (var (blocks, sumBytes, predicted, decided) in expected)
        {
            var design = blocks == 150_000 ? TestDesigns.Station : TestDesigns.Mixed(blocks, 3);
            Assert.Equal(sumBytes, design.Sums.StorageBytes);
            Assert.Equal(predicted, BlockStateStore.BytesFor(design, hitPoints: false));
            Assert.Equal(decided, BlockStateStore.BytesFor(design, hitPoints: true));
            Console.WriteLine($"SUMS {blocks} blocks: sums {sumBytes} B a hull; block state {predicted} B predicted, {decided} B decided; design keeps {design.Sums.DesignBytes} B for evaluation");
        }
    }

    [Fact]
    public void AStationOfAHundredAndFiftyThousandBoxesIsCreatedLosesBlocksAndIsReadWithinBudget()
    {
        const int blocks = 150_000;
        const int losses = 2_000;
        var design = TestDesigns.Station;
        using var store = new BlockStateStore();

        // Warm the store's handle table and the code, so what is measured is the work and not the first call.
        store.Release(store.Create(design, hitPoints: true));
        var order = Shuffled(blocks, 31);

        var timer = new Stopwatch();
        var allocation = new AllocationProbe();
        var state = default(BlockStateHandle);
        var create = 0.0;
        var lose = 0.0;
        var mass = 0f;
        var runs = 0;
        while (allocation.Run())
        {
            // A run in doubt is made again on a fresh hull.
            if (runs++ > 0)
                store.Release(state);
            allocation.Start();
            timer.Restart();
            state = store.Create(design, hitPoints: true);
            create = timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            mass = 0f;
            for (var i = 0; i < losses; i++)
            {
                Assert.True(store.Lose(state, order[i]));
                design.Figures(store.Sums(state), out var flight, out _);
                mass += flight.Mass;
            }

            lose = timer.Elapsed.TotalMilliseconds;
            allocation.Stop();
        }

        var allocated = allocation.Bytes;
        Console.WriteLine($"SUMS station: create {create:F3} ms; {losses} losses with a read each {lose:F3} ms ({lose * 1000 / losses:F2} us each); allocated {allocated} B managed; state {BlockStateStore.BytesFor(design, true)} B");

        // Budgets for a debug build on a busy machine: a hull appears in under 50 ms, a loss and a read of the
        // figures takes under 200 microseconds on average, and none of it takes managed memory.
        Assert.True(create < 50, $"creating the hull took {create} ms");
        Assert.True(lose / losses < 0.2, $"a loss took {lose / losses} ms");
        Assert.Equal(0L, allocated);
        Assert.True(BlockStateStore.BytesFor(design, hitPoints: true) < 5L * 1024 * 1024);
        Assert.True(mass > 0);

        Assert.Equal((uint)losses, store.Revision(state));
        Assert.Equal(blocks - losses, BlockSet.Count(store.Alive(state)));
        Check(design, store.Sums(state), store.Alive(state));
        var total = 0.0;
        foreach (var points in store.HitPoints(state))
            total += points;
        Assert.Equal(total, store.HitTotal(state), total * 1e-9);
    }
}

/// <summary>
/// A hull of a few hundred boxes of mixed sizes in the live path: stored, damaged through the simulation's own
/// damage, refitted from its running sums and destroyed with its core. It is not flown into anything: collision
/// still takes every block for a unit cube.
/// </summary>
public sealed class MixedBoxHullTests
{
    private static readonly GameContent Content = TestDesigns.Content;
    private static readonly HullDesign Design = TestDesigns.Mixed(300, 21);

    [Fact]
    public void AMixedHullIsDamagedRefittedFromItsSumsAndDiesWithItsCore()
    {
        Assert.Contains(Design.Boxes.ToArray(), box => box.Size != new GridSize(1, 1, 1));
        using var store = new HullStore(4);
        var field = new HullField(store, [Design]);
        var hull = store.Create(1, 0, Design, Double3.Zero).Index;
        var states = store.BlockStates;
        var block = store.Info(hull).BlockState;
        ref var state = ref store.State(hull);
        Figures.AssertIdentical(Design.Flight, store.Flight(hull));
        Assert.Equal(Design.Flight.HullPoints, state.Hull);

        // The shield is down, so every blow reaches the blocks it touched.
        state.ShieldOffline = true;
        var body = new HullBody { Design = Design, Alive = store.Blocks(hull), States = states, State = block };
        var touching = new ulong[Design.FullSet.Length];
        uint seed = 4;
        uint Next() => seed = unchecked(seed * 1664525 + 1013904223);

        void InStep()
        {
            // The total the hull reports is what its blocks have left, and never below nothing.
            var sum = 0.0;
            foreach (var points in store.BlockHp(hull))
                sum += points;
            var reported = store.State(hull).Hull;
            Assert.True(reported >= 0f);
            Assert.True(Math.Abs(sum - reported) <= 1e-3 * Math.Max(sum, 1.0), $"the hull reports {reported}, its blocks have {sum}");
            Assert.Equal(Design.BlockCount - BlockSet.Count(store.Blocks(hull)), (int)store.Revision(hull));

            // Refitted, it flies on the figures of sums built afresh for the blocks it has left.
            field.Refit(hull);
            Assert.Equal(store.Revision(hull), store.Info(hull).FittedRevision);
            Design.Evaluate(store.Blocks(hull), out var flight, out _);
            Figures.AssertIdentical(flight, store.Flight(hull));
        }

        // Thousands of blows on a few blocks each, none on the core: blocks wear down and go, the hull lives.
        for (var hit = 0; hit < 4000; hit++)
        {
            Array.Clear(touching);
            var count = 1 + (int)(Next() >> 8) % 4;
            for (var i = 0; i < count; i++)
                BlockSet.Add(touching, 1 + (int)((Next() >> 8) % (uint)(Design.BlockCount - 1)));
            var touched = BlockSet.Count(touching);
            Assert.False(HullSim.Damage(ref state, store.Flight(hull), ref body, touching, touched, touched * (40f + (Next() >> 8) % 300)));
            if (hit % 250 == 0)
                InStep();
        }

        InStep();
        var lost = (int)store.Revision(hull);
        Assert.InRange(lost, 50, Design.BlockCount - 50);
        Assert.True(store.Flight(hull).Mass < Design.Flight.Mass);

        // The core is block 0. The blow that takes it destroys the hull.
        Array.Clear(touching);
        BlockSet.Add(touching, 0);
        Assert.False(HullSim.Damage(ref state, store.Flight(hull), ref body, touching, 1, Design.BlockHitPoints(0) * 0.5f));
        Assert.True(HullSim.Damage(ref state, store.Flight(hull), ref body, touching, 1, Design.BlockHitPoints(0)));
        Assert.Equal(1, states.CoresLost(block));
        InStep();

        // Whatever is left goes too: the hull reports exactly nothing, and its sums are those of no blocks.
        var rest = store.Blocks(hull).ToArray();
        Assert.True(HullSim.Damage(ref state, store.Flight(hull), ref body, rest, BlockSet.Count(rest), 1e12f));
        Assert.False(BlockSet.Any(store.Blocks(hull)));
        Assert.Equal(0f, state.Hull);
        Assert.Equal(0.0, states.HitTotal(block));
        InStep();
        Assert.Equal(0f, store.Flight(hull).ShieldCapacity);

        store.Destroy(store.HandleAt(hull));
        Assert.False(states.IsLive(block));
        Assert.Equal(0, states.Count);
    }

    [Fact]
    public void DamagingAndRefittingAMixedHullTakesNoManagedMemory()
    {
        using var store = new HullStore(4);
        var field = new HullField(store, [Design]);
        var hull = store.Create(1, 0, Design, Double3.Zero).Index;
        store.State(hull).ShieldOffline = true;
        var touching = new ulong[Design.FullSet.Length];

        void Blow(int at)
        {
            var body = new HullBody { Design = Design, Alive = store.Blocks(hull), States = store.BlockStates, State = store.Info(hull).BlockState };
            Array.Clear(touching);
            BlockSet.Add(touching, 1 + at % (Design.BlockCount - 1));
            BlockSet.Add(touching, 1 + at * 7 % (Design.BlockCount - 1));
            HullSim.Damage(ref store.State(hull), store.Flight(hull), ref body, touching, 2, 5000f);
            field.Refit(hull);
        }

        Blow(0);
        var allocation = new AllocationProbe();
        for (var i = 1; i < 200; i++)
            while (allocation.Measuring()) Blow(i);
        Assert.Equal(0, allocation.Bytes);
        Assert.True(store.Revision(hull) > 100);
    }
}
