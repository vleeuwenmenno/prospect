using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The mining laser's content and its design files: the block and its tiers, <c>content/rules/mining.json</c>, the
/// version 7 design file with its per-mount group, and the version 5 design bytes.
/// </summary>
public sealed class MiningLaserContentTests
{
    private static readonly GameContent Shipped = GameContent.Load();

    private static string Refused(Action load)
    {
        var e = Assert.ThrowsAny<Exception>(load);
        Assert.IsAssignableFrom<InvalidDataException>(e);
        return e.Message;
    }

    private static void LaserEntry(JsonNode root, Action<JsonObject> change) => change(root["blocks"]!["miningLaser"]!["laser"]!.AsObject());

    [Fact]
    public void TheMiningLaserIsACommonFaceMountWithItsFiguresAndGroupable()
    {
        var type = Shipped.Blocks.Get("miningLaser");
        var laser = type.Laser!;
        Assert.NotNull(type.Mount);
        Assert.Equal(0f, type.Thrust);
        Assert.Equal(0f, type.PowerDraw);
        Assert.Null(type.Tractor);
        Assert.Equal((60f, 0.5f, 0.25f, 3f, 40f, ModuleQuality.Common), (laser.Range, laser.CutRadius, laser.CutRate, laser.Power, laser.GimbalDegrees, laser.Quality));
        Assert.Equal(MathF.Cos(40f * MathF.PI / 180f), laser.GimbalCosine);
        Assert.True(ActionGroups.Groupable(type));
        Assert.False(ActionGroups.Groupable(Shipped.Blocks.Get("coldGasThruster")));
        Assert.False(ActionGroups.Groupable(Shipped.Blocks.Get("tractorBeam")));
        Assert.False(ActionGroups.Groupable(null));
    }

    [Fact]
    public void ATierScalesEveryFigureOfTheLaser()
    {
        var content = MiningFixtures.Make(blocks: root => LaserEntry(root, laser => laser["quality"] = "Rare"));
        var rare = content.Blocks.Get("miningLaser").Laser!;
        Assert.Equal(ModuleQuality.Rare, rare.Quality);
        Assert.Equal(60f * 1.3f, rare.Range, 3);
        Assert.Equal(0.5f * 1.2f, rare.CutRadius, 4);
        Assert.Equal(0.25f * 1.7f, rare.CutRate, 4);
        Assert.Equal(3f * 1.2f, rare.Power, 4);
        Assert.Equal(40f, rare.GimbalDegrees);
        Assert.NotEqual(Shipped.Identity, content.Identity);
    }

    [Fact]
    public void TheGimbalLimitIsTheOneRuleForWhetherABeamMayLeaveTheFace()
    {
        var laser = Shipped.Blocks.Get("miningLaser").Laser!;
        var outward = System.Numerics.Vector3.UnitY;
        Assert.True(laser.WithinGimbal(outward, outward));
        Assert.True(laser.WithinGimbal(outward, System.Numerics.Vector3.Normalize(new(MathF.Sin(39f * MathF.PI / 180f), MathF.Cos(39f * MathF.PI / 180f), 0))));
        Assert.False(laser.WithinGimbal(outward, System.Numerics.Vector3.Normalize(new(MathF.Sin(41f * MathF.PI / 180f), MathF.Cos(41f * MathF.PI / 180f), 0))));
        Assert.False(laser.WithinGimbal(outward, System.Numerics.Vector3.UnitX));
        Assert.False(laser.WithinGimbal(outward, -outward));
    }

    [Theory]
    [InlineData("range", 0.0)]
    [InlineData("range", 1001.0)]
    [InlineData("cutRadius", 0.0)]
    [InlineData("cutRadius", 2.5)]
    [InlineData("cutRate", -1.0)]
    [InlineData("cutRate", 11.0)]
    [InlineData("power", 0.0)]
    [InlineData("power", 10001.0)]
    [InlineData("gimbalDegrees", 0.0)]
    [InlineData("gimbalDegrees", 90.0)]
    [InlineData("gimbalDegrees", 120.0)]
    public void ALaserFigureOutsideItsBoundsIsRefused(string key, double value) =>
        Refused(() => MiningFixtures.Make(blocks: root => LaserEntry(root, laser => laser[key] = value)));

    [Fact]
    public void ATierMultiplierThatIsZeroOrPushesAFigureOverItsBoundIsRefused()
    {
        Refused(() => MiningFixtures.Make(blocks: root => root["laserTiers"]!["Common"]!["range"] = 0));
        Refused(() => MiningFixtures.Make(blocks: root => root["laserTiers"]!["Common"]!["cutRadius"] = 5));
    }

    [Fact]
    public void ALaserIsAMountWithNoThrustTractorModuleOrStandingDraw()
    {
        Refused(() => MiningFixtures.Make(blocks: root => root["blocks"]!["miningLaser"]!["powerDraw"] = 1));
        Refused(() => MiningFixtures.Make(blocks: root => root["blocks"]!["miningLaser"]!.AsObject().Remove("mount")));
        Refused(() => MiningFixtures.Make(blocks: root => LaserEntry(root, laser => laser["quality"] = "Mythic")));
    }

    [Fact]
    public void ACutRadiusThatNoScrapeMayHaveOrABiteWiderThanTheBeamIsRefusedWhenTheContentLoads()
    {
        // The laser's 0.5 m cut is below a least scrape radius of 0.6 m.
        var radius = Refused(() => MiningFixtures.Make(other: copy => copy.Replace("rules/rock_edits.json", "\"minRadiusMetres\": 0.25", "\"minRadiusMetres\": 0.6")));
        Assert.Contains("scrape radius", radius);

        // Or above the greatest.
        Refused(() => MiningFixtures.Make(other: copy => copy.Replace("rules/rock_edits.json", "\"maxRadiusMetres\": 2", "\"maxRadiusMetres\": 0.4").Replace("rules/rock_edits.json", "\"radiusMetres\": 1", "\"radiusMetres\": 0.3")));

        // A bite of 1.2 m is more than the 1 m the beam is wide.
        var bite = Refused(() => MiningFixtures.Make(other: copy => copy.Replace("rules/mining.json", "\"biteMetres\": 0.25", "\"biteMetres\": 1.2")));
        Assert.Contains("bite", bite);
    }

    private static readonly string MiningJson = File.ReadAllText(Path.Combine(Prospect.Engine.IO.AppPaths.Content(), "rules", "mining.json"));

    private static MiningRules Parse(string text) => MiningRules.Parse(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void TheShippedMiningFiguresParseToTicksAndLimits()
    {
        var rules = Shipped.Mining;
        Assert.Equal(0.25f, rules.BiteMetres);
        Assert.Equal(9, rules.EditIntervalTicks);
        Assert.Equal(30, rules.StrokeGapTicks);
        Assert.Equal(1f, rules.MoveRadii);
        Assert.Equal(16, rules.MaxLasersPerHull);
        Assert.Equal(400f, rules.MaxAimOffsetMetres);
        Assert.Equal(300f, rules.ChunkOreKg);
        Assert.Equal(3000f, rules.ChunkDensity);
        Assert.Equal(2f, rules.ChunkSpeed);
        Assert.Equal(1.2f, rules.ChunkSpin);
        Assert.Equal(64, rules.ChunksPerSector);
        Assert.Equal(30L * 60 * 60, rules.ChunkLifetimeTicks);
        Assert.Equal(0.3f, rules.ChunkRestitution);
        Assert.Equal(512, rules.ChunkContactBudget);
        Assert.Equal(Shipped.Mining.ChunkOreKg, Parse(MiningJson).ChunkOreKg);

        // A lifetime of zero is allowed: such chunks never end.
        Assert.Equal(0L, Parse(MiningJson.Replace("\"lifetimeMinutes\": 30", "\"lifetimeMinutes\": 0")).ChunkLifetimeTicks);
        // The capsule a scrape takes out: its cross-section and the length it swept, times the bite, in kilograms of rock.
        Assert.Equal((Math.PI * 0.25 + 2 * 0.5 * 2) * 0.25 * 2.5 * 1000, rules.ScrapeKg(0.5f, 2f, 2.5f), 6);
    }

    [Theory]
    [InlineData("\"biteMetres\": 0.25", "\"biteMetres\": 0.01")]
    [InlineData("\"biteMetres\": 0.25", "\"biteMetres\": 5")]
    [InlineData("\"editSeconds\": 0.15", "\"editSeconds\": 0")]
    [InlineData("\"editSeconds\": 0.15", "\"editSeconds\": 11")]
    [InlineData("\"strokeGapSeconds\": 0.5", "\"strokeGapSeconds\": 0")]
    [InlineData("\"strokeGapSeconds\": 0.5", "\"strokeGapSeconds\": 20")]
    [InlineData("\"moveRadii\": 1", "\"moveRadii\": 0.05")]
    [InlineData("\"moveRadii\": 1", "\"moveRadii\": 9")]
    [InlineData("\"maxPerHull\": 16", "\"maxPerHull\": 0")]
    [InlineData("\"maxPerHull\": 16", "\"maxPerHull\": 65")]
    [InlineData("\"maxAimOffsetMetres\": 400", "\"maxAimOffsetMetres\": 0")]
    [InlineData("\"maxAimOffsetMetres\": 400", "\"maxAimOffsetMetres\": 10001")]
    [InlineData("\"oreKg\": 300", "\"oreKg\": 0.5")]
    [InlineData("\"oreKg\": 300", "\"oreKg\": 2000000")]
    [InlineData("\"densityKgPerCubicMetre\": 3000", "\"densityKgPerCubicMetre\": 99")]
    [InlineData("\"densityKgPerCubicMetre\": 3000", "\"densityKgPerCubicMetre\": 30001")]
    [InlineData("\"speed\": 2", "\"speed\": -1")]
    [InlineData("\"speed\": 2", "\"speed\": 51")]
    [InlineData("\"spin\": 1.2", "\"spin\": 21")]
    [InlineData("\"perSector\": 64", "\"perSector\": 0")]
    [InlineData("\"perSector\": 64", "\"perSector\": 257")]
    [InlineData("\"lifetimeMinutes\": 30", "\"lifetimeMinutes\": -1")]
    [InlineData("\"lifetimeMinutes\": 30", "\"lifetimeMinutes\": 1441")]
    [InlineData("\"restitution\": 0.3", "\"restitution\": 1.5")]
    [InlineData("\"restitution\": 0.3", "\"restitution\": -0.1")]
    [InlineData("\"contactBudget\": 512", "\"contactBudget\": 0")]
    [InlineData("\"contactBudget\": 512", "\"contactBudget\": 4097")]
    public void AMiningFigureOutOfRangeIsRefused(string was, string becomes)
    {
        Assert.Contains(was, MiningJson);
        Assert.Contains("mining.json", Refused(() => Parse(MiningJson.Replace(was, becomes))));
    }

    [Fact]
    public void AMiningFileMissingAFigureOrHoldingNoNumberIsNotAccepted()
    {
        Assert.ThrowsAny<Exception>(() => Parse(MiningJson.Replace("\"restitution\": 0.3,", "")));
        Assert.ThrowsAny<Exception>(() => Parse(MiningJson.Replace("\"oreKg\": 300", "\"oreKg\": \"forty\"")));
        Assert.ThrowsAny<Exception>(() => Parse("{}"));
    }

    // ---------------------------------------------------------------- design files and bytes

    private static BoxDesign Read(string text) => BoxDesign.Read(Encoding.UTF8.GetBytes(text), MiningFixtures.Content.Blocks, "test.json");

    [Fact]
    public void AVersionSevenFileRoundTripsItsGroupsAndWritesOnlyTheOnesThatAreSet()
    {
        var design = Read(MiningFixtures.PodJson());
        var groups = design.Mounts.ToArray().Where(m => m.Type.Laser is not null).Select(m => (int)m.Group).OrderBy(g => g).ToArray();
        Assert.Equal([0, 1, 2], groups);

        using var stream = new MemoryStream();
        design.Write(stream);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"version\":7", text);
        Assert.Equal(2, text.Split("\"group\":").Length - 1);
        Assert.DoesNotContain("\"group\":0", text);

        var again = Read(text);
        Assert.Equal(design.Mounts.ToArray(), again.Mounts.ToArray());
        Assert.Equal(1, again.Mounts.ToArray().Count(m => m.Group == 1));
        Assert.Equal(1, again.Mounts.ToArray().Count(m => m.Group == 2));
    }

    [Fact]
    public void AVersionSixFileWithAGroupIsRefusedAndWithoutOneStillLoads()
    {
        var text = MiningFixtures.PodJson();
        var message = Refused(() => Read(text.Replace("\"version\":7", "\"version\":6")));
        Assert.Contains("version 7", message);

        var without = JsonNode.Parse(text)!;
        foreach (var mount in without["mounts"]!.AsArray()) mount!.AsObject().Remove("group");
        without["version"] = 6;
        Assert.All(Read(without.ToJsonString()).Mounts.ToArray(), mount => Assert.Equal(0, mount.Group));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(-1)]
    [InlineData(255)]
    public void AGroupThatIsNotOneToTenOrNoneIsRefused(int group)
    {
        var pod = JsonNode.Parse(MiningFixtures.PodJson())!;
        var laser = pod["mounts"]!.AsArray().Last()!.AsObject();
        laser["group"] = group;
        Refused(() => Read(pod.ToJsonString()));
    }

    [Fact]
    public void AGroupOnAPartThatIsNoLaserIsRefusedWhateverTheFileSays()
    {
        var pod = JsonNode.Parse(MiningFixtures.PodJson())!;
        var thruster = pod["mounts"]!.AsArray().First(m => m!["type"]!.GetValue<string>() == "coldGasThruster")!.AsObject();
        thruster["group"] = 3;
        Assert.Contains("action group", Refused(() => Read(pod.ToJsonString())));
        Assert.False(ActionGroups.Allowed(Shipped.Blocks.Get("coldGasThruster"), 3));
        Assert.True(ActionGroups.Allowed(Shipped.Blocks.Get("coldGasThruster"), 0));
        Assert.True(ActionGroups.Allowed(Shipped.Blocks.Get("miningLaser"), 10));
        Assert.False(ActionGroups.Allowed(Shipped.Blocks.Get("miningLaser"), 11));
        Assert.False(ActionGroups.Allowed(Shipped.Blocks.Get("miningLaser"), -1));
    }

    [Fact]
    public void TheGroupHelpersMapGroupsToBitsAndDigits()
    {
        Assert.Equal(1, ActionGroups.Bit(1));
        Assert.Equal(512, ActionGroups.Bit(10));
        Assert.Equal(0, ActionGroups.Bit(0));
        Assert.Equal(0, ActionGroups.Bit(11));
        Assert.Equal(ActionGroups.AllMask, (ushort)Enumerable.Range(1, 10).Sum(g => ActionGroups.Bit(g)));
        Assert.Equal(0, ActionGroups.Digit(10));
        Assert.Equal(7, ActionGroups.Digit(7));

        var mount = new DesignMount(0, BlockFacing.Up, 2, 2, Shipped.Blocks.Get("miningLaser"), 4);
        Assert.Equal(4, ActionGroups.Effective(mount, ActionGroups.NotOverridden));
        Assert.Equal(7, ActionGroups.Effective(mount, 7));
        Assert.Equal(0, ActionGroups.Effective(mount, 0));
    }

    [Fact]
    public void DesignBytesKeepEachMountsGroupAndRefuseAGroupItsPartMayNotHave()
    {
        var content = MiningFixtures.Content;
        var design = content.Pod;
        Assert.Equal(3, design.Lasers.Length);
        Assert.Equal(5, DesignBytes.Version);
        var bytes = DesignBytes.Encode(design);
        var decoded = DesignBytes.Decode(bytes, SHA256.HashData(bytes), content);
        Assert.Equal(design.Mounts.ToArray(), decoded.Mounts.ToArray());
        Assert.Equal(design.Lasers, decoded.Lasers);
        var mounts = design.Mounts.ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, design.Lasers.Select(i => (int)mounts[i].Group).OrderBy(g => g));
        Assert.Equal(bytes, DesignBytes.Encode(decoded));

        // The group is the last byte of the last mount's record. Eleven and 255 are no groups.
        foreach (var bad in new byte[] { 11, 255 })
        {
            var changed = (byte[])bytes.Clone();
            changed[^1] = bad;
            Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(changed, SHA256.HashData(changed), content));
        }

        // A group on a thruster, which only a laser may have.
        var plain = DesignBytes.Encode(Shipped.Pod);
        Assert.Equal(0, plain[^1]);
        var onThruster = (byte[])plain.Clone();
        onThruster[^1] = 3;
        Assert.Contains("action group", Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(onThruster, SHA256.HashData(onThruster), Shipped)).Message);

        // A change of a laser's group changes the bytes of the design and so its hash: two groupings are two designs.
        var other = (byte[])bytes.Clone();
        var last = mounts[^1];
        other[^1] = (byte)(last.Group == 4 ? 5 : 4);
        Assert.NotEqual(SHA256.HashData(bytes), SHA256.HashData(other));
        if (last.Type.Laser is not null)
            Assert.Equal(other[^1], DesignBytes.Decode(other, SHA256.HashData(other), content).Mounts.ToArray()[^1].Group);
    }

    [Fact]
    public void ADesignListsItsLasersInMountOrderAndAPodWithoutOnesHasNone()
    {
        var design = MiningFixtures.Content.Pod;
        var mounts = design.Mounts.ToArray();
        Assert.Equal(Enumerable.Range(0, mounts.Length).Where(i => mounts[i].Type.Laser is not null), design.Lasers);
        Assert.Empty(Shipped.Pod.Lasers);
        Assert.Equal(3, new[] { MiningFixtures.PartA(design), MiningFixtures.PartB(design), MiningFixtures.PartC(design) }.Distinct().Count());
        Assert.Equal(1, mounts[design.Lasers[MiningFixtures.PartA(design)]].Group);
        Assert.Equal(2, mounts[design.Lasers[MiningFixtures.PartB(design)]].Group);
        Assert.Equal(0, mounts[design.Lasers[MiningFixtures.PartC(design)]].Group);
    }
}
