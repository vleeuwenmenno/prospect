using System.Text.Json.Nodes;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// What an asteroid is made of: one main ore, up to two of its partners mixed in and always some rock; and what a
/// scan publishes of it.
/// </summary>
public sealed class AsteroidCompositionTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static GameContent World(params string[] assignments) => Content.WithSettings(Content.SettingCatalog.Build(null, assignments));

    private static IEnumerable<SectorCoord> Sample()
    {
        yield return default;
        for (var x = -24; x <= 24; x += 4)
            for (var z = -24; z <= 24; z += 4)
                yield return new SectorCoord(x, (x + z) / 8 % 2, z);
    }

    [Fact]
    public void EveryAsteroidKeepsRockAndMixesInOnlyPartnersItsSectorCarries()
    {
        var galaxy = Content.Galaxy.WithSeed(42);
        var again = Content.Galaxy.WithSeed(42);
        var cap = galaxy.AsteroidFields!.MaxOre;
        Assert.Equal(0.7f, cap);
        var ice = Array.FindIndex(galaxy.Resources, r => r.Id == "ice");
        Assert.Empty(galaxy.Resources[ice].Partners);
        var content = new SectorContent();
        var repeat = new SectorContent();
        int ore = 0, one = 0, two = 0, icy = 0, bare = 0;
        foreach (var coord in Sample())
        {
            galaxy.Generate(coord, content);
            again.Generate(coord, repeat);
            Assert.True(Sectors.Bytes(content).SequenceEqual(Sectors.Bytes(repeat)), $"sector {coord} differs between two runs");
            var character = content.Summary.Character;
            for (var i = 0; i < content.AsteroidCount; i++)
            {
                var rock = content.Asteroids[i];
                if (rock.Resource < 0)
                {
                    bare++;
                    Assert.Equal((0f, -1, 0f, -1, 0f), (rock.Grade, rock.Mix1, rock.Mix1Share, rock.Mix2, rock.Mix2Share));
                    continue;
                }

                ore++;
                Assert.True(rock.Grade > 0);
                Assert.True((double)rock.Grade + rock.Mix1Share + rock.Mix2Share <= cap + 1e-6, $"sector {coord} rock {i} is {rock.Ore} ore");
                var main = galaxy.Resources[rock.Resource];
                if (rock.Resource == ice)
                    icy++;
                if (rock.Mix1 < 0)
                {
                    Assert.Equal((0f, -1, 0f), (rock.Mix1Share, rock.Mix2, rock.Mix2Share));
                    continue;
                }

                Assert.Contains(rock.Mix1, main.Partners);
                Assert.True(character.Has(rock.Mix1), $"sector {coord} mixes in a resource it does not have");
                Assert.InRange(rock.Mix1Share, float.Epsilon, rock.Grade);
                if (rock.Mix2 < 0)
                {
                    one++;
                    Assert.Equal(0f, rock.Mix2Share);
                    continue;
                }

                two++;
                Assert.NotEqual(rock.Mix1, rock.Mix2);
                Assert.Contains(rock.Mix2, main.Partners);
                Assert.True(character.Has(rock.Mix2), $"sector {coord} mixes in a resource it does not have");
                Assert.InRange(rock.Mix2Share, float.Epsilon, rock.Mix1Share);
            }
        }

        // The sample has every case: bare rock, ore alone, one mix-in and two, and ice, which takes none.
        Assert.True(bare > 100 && one > 100 && two > 100 && icy > 20 && ore > one + two, $"{bare} bare, {ore} ore, {one} with one mix-in, {two} with two, {icy} ice");
    }

    [Fact]
    public void NoSettingLiftsAnAsteroidOverTheMostOreItMayHold()
    {
        var galaxy = World("asteroidOreShare=2", "asteroidOreGrade=5", "oreGradeMin=1", "oreGradeMax=1").Galaxy.WithSeed(42);
        var cap = galaxy.AsteroidFields!.MaxOre;
        var content = new SectorContent();
        var ore = 0;
        foreach (var coord in Sample().Take(40))
        {
            galaxy.Generate(coord, content);
            for (var i = 0; i < content.AsteroidCount; i++)
            {
                var rock = content.Asteroids[i];
                if (rock.Resource < 0)
                    continue;
                ore++;

                // Every rock is asked to be all ore, so every rock is at the cap, split between its resources.
                Assert.Equal(cap, rock.Ore, 5);
            }
        }

        Assert.True(ore > 1000);
    }

    [Fact]
    public void MixInsComeFromTheirOwnDrawsAndChangeNeitherTheRocksNorTheirOre()
    {
        using var copy = new ContentCopy();
        var json = JsonNode.Parse(copy.Read("galaxy/galaxy.json"))!;
        MixIns(json)["firstChance"] = 0;
        copy.Write("galaxy/galaxy.json", json.ToJsonString());
        var plain = copy.Load().Galaxy.WithSeed(42);
        var mixed = Content.Galaxy.WithSeed(42);
        var a = new SectorContent();
        var b = new SectorContent();
        var withMix = 0;
        foreach (var coord in Sample().Take(40))
        {
            plain.Generate(coord, a);
            mixed.Generate(coord, b);
            Assert.Equal(a.AsteroidCount, b.AsteroidCount);
            for (var i = 0; i < a.AsteroidCount; i++)
            {
                var alone = a.Asteroids[i];
                var rock = b.Asteroids[i];
                Assert.Equal(-1, alone.Mix1);
                Assert.Equal((alone.LocalPosition, alone.Radius, alone.Rotation, alone.Shape, alone.Resource), (rock.LocalPosition, rock.Radius, rock.Rotation, rock.Shape, rock.Resource));
                Assert.Equal(alone.Grade, rock.Ore, 5);
                if (rock.Mix1 < 0)
                    Assert.Equal(alone.Grade, rock.Grade);
                else
                    withMix++;
            }
        }

        Assert.True(withMix > 500);
    }

    private static JsonNode MixIns(JsonNode galaxy) => Fields(galaxy)["ore"]!["mixIns"]!;

    private static JsonNode Fields(JsonNode galaxy) => galaxy["sectorModules"]!.AsArray().First(m => (string)m!["type"]! == "asteroidFields")!;

    [Theory]
    [InlineData("self")]
    [InlineData("unknown")]
    [InlineData("twice")]
    [InlineData("weight")]
    [InlineData("allOre")]
    [InlineData("chance")]
    [InlineData("secondLarger")]
    [InlineData("halfTheOre")]
    public void ContentThatBreaksTheCompositionRulesIsRefused(string fault)
    {
        using var copy = new ContentCopy();
        var json = JsonNode.Parse(copy.Read("galaxy/galaxy.json"))!;
        var iron = json["resources"]!.AsArray().First(r => (string)r!["id"]! == "iron")!["partners"]!.AsArray();
        switch (fault)
        {
            case "self": iron.Add(new JsonObject { ["id"] = "iron", ["weight"] = 1 }); break;
            case "unknown": iron.Add(new JsonObject { ["id"] = "unobtainium", ["weight"] = 1 }); break;
            case "twice": iron.Add(new JsonObject { ["id"] = "nickel", ["weight"] = 1 }); break;
            case "weight": iron.Add(new JsonObject { ["id"] = "gold", ["weight"] = 0 }); break;
            case "allOre": Fields(json)["ore"]!["maxOre"] = 1; break;
            case "chance": MixIns(json)["secondChance"] = 1.5; break;
            case "secondLarger": MixIns(json)["secondShare"] = new JsonArray(0.02, 0.2); break;
            case "halfTheOre": MixIns(json)["firstShare"] = new JsonArray(0.12, 0.45); break;
        }

        copy.Write("galaxy/galaxy.json", json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => copy.Load());
    }

    private static ModuleObservation Analysis(float grade, int mix1, float share1, int mix2, float share2) => new()
    {
        Owner = 1,
        Family = ModuleFamily.AsteroidScanner,
        Radius = 40,
        Mass = 1000,
        Resource = 0,
        Grade = grade,
        Mix1 = mix1,
        Mix1Share = share1,
        Mix2 = mix2,
        Mix2Share = share2,
    };

    private static ModuleEquipment Scanner(double accuracy, ModuleQuality quality) => new(1, quality, 1, 3000, accuracy, 5, 9, 10);

    [Fact]
    public void AShareBelowTheScannersResolutionIsATraceAndABetterScannerResolvesIt()
    {
        // Iron 40 %, nickel 12 %, copper 3 %: a Common scanner (accuracy 0.2) resolves five points.
        var common = Analysis(0.40f, 2, 0.12f, 1, 0.03f);
        ModuleScanRules.Disclose(ref common, Scanner(0.2, ModuleQuality.Common));
        Assert.Equal(0.40f, common.Grade, 5);
        Assert.Equal((2, 1), (common.Mix1, common.Mix2));
        Assert.Equal(0.10f, common.Mix1Share, 5);

        // The copper keeps its name and gets no figure: not a rounded-up 5 %, and not a guess.
        Assert.Equal(0f, common.Mix2Share);
        Assert.True(ModuleObservationRules.Valid(common));

        // Alien Tech (accuracy 1) resolves one point and so sees the whole mix.
        var best = Analysis(0.40f, 2, 0.12f, 1, 0.03f);
        ModuleScanRules.Disclose(ref best, Scanner(1, ModuleQuality.AlienTech));
        Assert.Equal(0.40f, best.Grade, 5);
        Assert.Equal(0.12f, best.Mix1Share, 5);
        Assert.Equal(0.03f, best.Mix2Share, 5);
        Assert.True(ModuleObservationRules.Valid(best));

        // No scanner claims more than is there, and the ore mass of a resource is the mass times its share.
        foreach (var record in new[] { common, best })
        {
            Assert.True(record.Grade <= 0.40f + 1e-6f && record.Mix1Share <= 0.12f + 1e-6f && record.Mix2Share <= 0.03f + 1e-6f);
            Assert.Equal(1000, record.Mass);
        }

        Assert.Equal(120f, best.Mass * best.Mix1Share, 3);

        // A share that is a whole number of steps keeps its last step, and a main ore under one step is a trace too.
        Assert.Equal(0.70f, ModuleScanRules.Resolved(0.7f, 0.2), 5);
        Assert.Equal(0.35f, ModuleScanRules.Resolved(0.35f, 0.2), 5);
        Assert.Equal(0f, ModuleScanRules.Resolved(0.049f, 0.2));
        Assert.Equal(0.04f, ModuleScanRules.Resolved(0.049f, 1), 5);
    }

    [Fact]
    public void ALocatorAndAGasSampleNameNoMixIns()
    {
        var locator = Analysis(0.40f, 2, 0.12f, 1, 0.03f);
        locator.Family = ModuleFamily.AsteroidLocator;
        ModuleScanRules.Disclose(ref locator, Scanner(1, ModuleQuality.AlienTech));
        Assert.Equal((0, 0f, -1, 0f, -1, 0f), (locator.Resource, locator.Grade, locator.Mix1, locator.Mix1Share, locator.Mix2, locator.Mix2Share));
        Assert.True(ModuleObservationRules.Valid(locator));
        locator.Mix1 = 2;
        Assert.False(ModuleObservationRules.Valid(locator));

        var gas = new ModuleObservation { Owner = 1, Family = ModuleFamily.GasScanner, Resource = -1, Mix1 = -1, Mix2 = -1 };
        Assert.True(ModuleObservationRules.Valid(gas));
        gas.Mix1 = 0;
        Assert.False(ModuleObservationRules.Valid(gas));
    }

    [Theory]
    [InlineData(0.4f, 0, 0.1f, -1, 0f)] // the main ore named again
    [InlineData(0.4f, -1, 0f, 1, 0f)] // a second without a first
    [InlineData(0.4f, -1, 0.1f, -1, 0f)] // a share without a resource
    [InlineData(0.4f, 2, 0.1f, 2, 0.05f)] // the same mix-in twice
    [InlineData(0.4f, 2, 0.1f, -1, 0.05f)] // a second share without a second resource
    [InlineData(0.4f, 2, 0.1f, 1, 0.2f)] // the second larger than the first
    [InlineData(0.1f, 2, 0.2f, -1, 0f)] // a mix-in larger than the main ore
    [InlineData(0.4f, 32, 0.1f, -1, 0f)] // no such resource
    [InlineData(0.4f, 2, float.NaN, -1, 0f)]
    public void AnAnalysisWithAnImpossibleMixIsRefusedOnTheWire(float grade, int mix1, float share1, int mix2, float share2)
    {
        var record = Analysis(grade, mix1, share1, mix2, share2);
        Assert.False(ModuleObservationRules.Valid(record));
        Span<byte> bytes = stackalloc byte[ModuleMessage.ObservationWireSize];
        var writer = new NetWriter(bytes);
        ModuleMessage.WriteObservation(ref writer, record);
        Assert.Equal(ModuleMessage.ObservationWireSize, writer.Length);
        var reader = new NetReader(bytes);
        Assert.False(ModuleMessage.ReadObservation(ref reader, out _));

        // Bare rock names no mix-in either.
        var bare = Analysis(0, 2, 0, -1, 0);
        bare.Resource = -1;
        Assert.False(ModuleObservationRules.Valid(bare));
    }

    [Fact]
    public void TheSurveyMessageIsBoundedByItsLargestObservation()
    {
        Assert.Equal(131, ModuleMessage.ObservationWireSize);
        Assert.Equal(95 + 8 * 131, ModuleMessage.MaxWireSize);
        Span<byte> bytes = stackalloc byte[ModuleMessage.ObservationWireSize];
        var writer = new NetWriter(bytes);
        var record = Analysis(0.4f, 2, 0.1f, 1, 0f);
        Assert.True(ModuleObservationRules.Valid(record));
        ModuleMessage.WriteObservation(ref writer, record);
        Assert.Equal(ModuleMessage.ObservationWireSize, writer.Length);
        var reader = new NetReader(bytes);
        Assert.True(ModuleMessage.ReadObservation(ref reader, out var decoded));
        Assert.Equal(0, reader.Remaining);
        Assert.Equal((record.Resource, record.Grade, record.Mix1, record.Mix1Share, record.Mix2, record.Mix2Share),
            (decoded.Resource, decoded.Grade, decoded.Mix1, decoded.Mix1Share, decoded.Mix2, decoded.Mix2Share));
    }
}
