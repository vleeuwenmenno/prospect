using System.Text;
using System.Text.Json.Nodes;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class ModuleSchemaTests
{
    private static JsonObject Document(string family, string quality, int mark)
    {
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.Content("blocks", "blocks.json")))!.AsObject();
        root["moduleTiers"] = new JsonObject
        {
            [quality] = new JsonObject { ["range"] = 2, ["accuracy"] = 2, ["scanSeconds"] = 0.5, ["powerDraw"] = 1.5, ["cooldownSeconds"] = 0.5 }
        };
        root["blocks"]!["hull"]!["module"] = new JsonObject
        {
            ["family"] = family,
            ["quality"] = quality,
            ["mark"] = mark,
            ["variant"] = "standard",
            ["range"] = 1000,
            ["accuracy"] = 0.25,
            ["scanSeconds"] = 4,
            ["powerDraw"] = 2,
            ["cooldownSeconds"] = 10
        };
        return root;
    }

    private static ModuleSpec Parse(JsonObject root) =>
        BlockCatalog.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()), "test").Get("hull").Module;

    [Theory]
    [InlineData("Common", ModuleQuality.Common)]
    [InlineData("Uncommon", ModuleQuality.Uncommon)]
    [InlineData("Rare", ModuleQuality.Rare)]
    [InlineData("Epic", ModuleQuality.Epic)]
    [InlineData("Legendary", ModuleQuality.Legendary)]
    [InlineData("Alien Tech", ModuleQuality.AlienTech)]
    public void EveryQualitySupportsIndependentMarksAndContentMultipliers(string name, ModuleQuality quality)
    {
        for (var mark = 1; mark <= 3; mark++)
        {
            var module = Parse(Document("gasScanner", name, mark));
            Assert.Equal(new ModuleSpec(ModuleFamily.GasScanner, quality, mark, "standard", 2000, 0.5, 2, 3, 5), module);
        }
    }

    [Theory]
    [InlineData("gasScanner", ModuleFamily.GasScanner)]
    [InlineData("asteroidScanner", ModuleFamily.AsteroidScanner)]
    [InlineData("asteroidLocator", ModuleFamily.AsteroidLocator)]
    public void FamiliesDecode(string name, ModuleFamily family) =>
        Assert.Equal(family, Parse(Document(name, "Common", 1)).Family);

    [Theory]
    [InlineData("family", "unknown")]
    [InlineData("quality", "unknown")]
    [InlineData("variant", " ")]
    public void InvalidIdentityIsRefused(string key, string value)
    {
        var root = Document("gasScanner", "Common", 1);
        root["blocks"]!["hull"]!["module"]![key] = value;
        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void InvalidMarksAreRefused(int mark) =>
        Assert.Throws<InvalidDataException>(() => Parse(Document("gasScanner", "Common", mark)));

    [Theory]
    [InlineData("range")]
    [InlineData("accuracy")]
    [InlineData("scanSeconds")]
    [InlineData("powerDraw")]
    [InlineData("cooldownSeconds")]
    public void NonpositiveMultipliersAreRefused(string key)
    {
        var root = Document("gasScanner", "Common", 1);
        root["moduleTiers"]!["Common"]![key] = 0;
        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Fact]
    public void AccuracyCannotExceedOne()
    {
        var root = Document("gasScanner", "Common", 1);
        root["moduleTiers"]!["Common"]!["accuracy"] = 5;
        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Fact]
    public void ExistingBlocksHaveNoModules() =>
        Assert.All(BlockCatalog.Load(AppPaths.Content("blocks", "blocks.json")).Types, block => Assert.Equal(default, block.Module));
}
