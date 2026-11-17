using System.Text;
using Prospect.Engine.IO;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// A copy of the shipped <c>content/</c> in a temporary directory, for a test to change and load. The game's own
/// content is never written to. With <c>fleet</c> the copy is <see cref="FleetContent"/>'s instead: the test fleet's
/// ships in place of the shipped one.
/// </summary>
internal sealed class ContentCopy : IDisposable
{
    public ContentCopy(bool fleet = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "prospect-content-" + Guid.NewGuid().ToString("N"));
        var source = AppPaths.Content();
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        if (fleet)
            FleetContent.Fit(Root);
    }

    /// <summary>The directory, laid out as <c>content/</c> is.</summary>
    public string Root { get; }

    /// <summary>Every file of the copy, as a path relative to <see cref="Root"/>.</summary>
    public IEnumerable<string> Files => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(Root, file));

    public string Read(string file) => File.ReadAllText(Path.Combine(Root, file));

    public void Write(string file, string text) => File.WriteAllText(Path.Combine(Root, file), text, new UTF8Encoding(false));

    /// <summary>Changes the one place in a file where <paramref name="old"/> stands. Fails if it is not there exactly once.</summary>
    public ContentCopy Replace(string file, string old, string replacement)
    {
        var text = Read(file);
        var at = text.IndexOf(old, StringComparison.Ordinal);
        Assert.True(at >= 0 && text.IndexOf(old, at + 1, StringComparison.Ordinal) < 0, $"{file} should hold {old} exactly once");
        Write(file, text[..at] + replacement + text[(at + old.Length)..]);
        return this;
    }

    public GameContent Load() => GameContent.Load(Root);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

/// <summary>Content a server and a client must not share: the shipped content with one block's hull points changed.</summary>
internal static class OtherContent
{
    private static readonly Lazy<GameContent> Loaded = new(() =>
    {
        using var copy = new ContentCopy();
        return copy.Replace("blocks/blocks.json", "\"Hull plating\", \"mass\": 2.0, \"hull\": 400", "\"Hull plating\", \"mass\": 2.0, \"hull\": 401").Load();
    });

    public static GameContent Value => Loaded.Value;
}

/// <summary>What the identity of a game's content covers, what it leaves out and what it does not care about.</summary>
public sealed class ContentIdentityTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Fact]
    public void ModuleTiersAndGasMixturesAreSharedContent()
    {
        using var copy = new ContentCopy();
        var blocks = System.Text.Json.Nodes.JsonNode.Parse(copy.Read("blocks/blocks.json"))!;
        blocks["moduleTiers"]!["Rare"]!["range"] = 1.75;
        copy.Write("blocks/blocks.json", blocks.ToJsonString());
        var changed = copy.Load();
        Assert.NotEqual(Content.Identity, changed.Identity);
        var galaxy = System.Text.Json.Nodes.JsonNode.Parse(copy.Read("galaxy/galaxy.json"))!;
        galaxy["gasComposition"]!["purityMin"] = .4;
        copy.Write("galaxy/galaxy.json", galaxy.ToJsonString());
        Assert.NotEqual(changed.Identity, copy.Load().Identity);
    }

    [Fact]
    public void RockEditCellSizeIsSharedContentBecauseEverySideBuildsItsBricksFromIt()
    {
        // A cell of half a metre is a valid figure only with a least radius that is not below it, so both change.
        using var copy = new ContentCopy();
        copy.Replace("rules/rock_edits.json", "\"cellMetres\": 0.25", "\"cellMetres\": 0.5");
        copy.Replace("rules/rock_edits.json", "\"minRadiusMetres\": 0.25", "\"minRadiusMetres\": 0.5");
        var changed = copy.Load();
        Assert.NotEqual(Content.Identity, changed.Identity);
        Assert.Equal(0.5f, changed.RockEdits.CellMetres);
        Assert.Equal(Content.RockEdits.CellMetres, GameContent.Load().RockEdits.CellMetres);
    }

    [Fact]
    public void MiningRulesAreSharedContentBecauseTheServerCutsAndSpawnsByThemAndAClientShowsTheirLimits()
    {
        // A changed figure is another identity, for a game and for a save.
        using var figure = new ContentCopy();
        figure.Replace("rules/mining.json", "\"oreKg\": 300", "\"oreKg\": 41");
        var changed = figure.Load();
        Assert.NotEqual(Content.Identity, changed.Identity);
        Assert.NotEqual(Content.SaveIdentity, changed.SaveIdentity);
        Assert.Equal(41f, changed.Mining.ChunkOreKg);
        Assert.Equal(300f, Content.Mining.ChunkOreKg);

        using var spaced = new ContentCopy();
        // JSON is hashed in its canonical form, so spacing is not a change.
        spaced.Write("rules/mining.json", spaced.Read("rules/mining.json") + "\n");
        Assert.Equal(Content.Identity, spaced.Load().Identity);
        Assert.Equal(Content.Identity, GameContent.Load().Identity);

        // The laser's own tiers are in blocks.json, which is shared already.
        using var tiers = new ContentCopy();
        var blocks = System.Text.Json.Nodes.JsonNode.Parse(tiers.Read("blocks/blocks.json"))!;
        blocks["laserTiers"]!["Rare"]!["cutRate"] = 1.8;
        tiers.Write("blocks/blocks.json", blocks.ToJsonString());
        Assert.NotEqual(Content.Identity, tiers.Load().Identity);
    }

    [Fact]
    public void RockEditRulesThatContradictEachOtherAreRefusedWhenTheContentLoads()
    {
        // The cell alone, with a least radius below it, is not content the game accepts.
        using var copy = new ContentCopy();
        copy.Replace("rules/rock_edits.json", "\"cellMetres\": 0.25", "\"cellMetres\": 0.5");
        Assert.Throws<InvalidDataException>(() => copy.Load());
    }

    [Fact]
    public void GasSensorPenaltyIsSharedContent()
    {
        using var copy = new ContentCopy();
        copy.Replace("rules/exploration.json", "\"gasOpticalPenalty\": 1.0", "\"gasOpticalPenalty\": 0.5");
        Assert.NotEqual(Content.Identity, copy.Load().Identity);
    }

    [Fact]
    public void ExplorationRulesAreSharedContent()
    {
        using var copy = new ContentCopy();
        copy.Replace("rules/exploration.json", "5000", "6000");
        Assert.NotEqual(Content.Identity, copy.Load().Identity);
    }

    [Fact]
    public void GeometryAndAggregateAdmissionBudgetsAreShared()
    {
        using var geometry = new ContentCopy();
        geometry.Replace("blocks/blocks.json", "6000000", "5999999");
        Assert.NotEqual(Content.Identity, geometry.Load().Identity);
        using var catalogue = new ContentCopy();
        catalogue.Replace("net/blocks.json", "196608", "196607");
        Assert.NotEqual(Content.Identity, catalogue.Load().Identity);
    }

    [Fact]
    public void BlockRecoveryLimitsAreSharedContent()
    {
        using var copy = new ContentCopy();
        copy.Replace("net/blocks.json", "1024", "2048");
        Assert.NotEqual(Content.Identity, copy.Load().Identity);
    }

    [Fact]
    public void TheShippedContentHasOneIdentityWheneverAndWhereverItIsLoaded()
    {
        Assert.Equal(Content.Identity, GameContent.Load().Identity);
        Assert.NotEqual(default, Content.Identity);
        Assert.Equal(ContentIdentity.WireSize * 2, Content.Identity.ToString().Length);

        // The same files in another directory are the same content.
        using var copy = new ContentCopy();
        Assert.Equal(Content.Identity, copy.Load().Identity);
    }

    [Fact]
    public async Task EachSharedFileIsReadOnceSoTheIdentityIsOfWhatWasLoaded()
    {
        // A named pipe gives its bytes to one reader, once. If the identity were worked out from one read of a
        // file and the game loaded from another, the second read would find nobody writing and wait for ever; a
        // file saved between two such reads would give an identity that is not of the content in play.
        if (OperatingSystem.IsWindows())
            return;

        using var copy = new ContentCopy();
        string[] shared = ["blocks/cargo.json", "blocks/blocks.json", "rules/settings.json", "rules/exploration.json", "ships/fleet.json", "ships/prospector.json", "ships/drone.json", "ships/work_drone.json", "blocks/drones.json", "galaxy/galaxy.json", "galaxy/interiors.json", "net/blocks.json", "rules/rock_edits.json"];
        var writers = new List<Task>();
        foreach (var file in shared)
        {
            var path = Path.Combine(copy.Root, file);
            var bytes = File.ReadAllBytes(path);
            File.Delete(path);
            using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [path]))
            {
                await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, mkfifo.ExitCode);
            }

            writers.Add(
                Task.Run(
                    () =>
                    {
                        using var pipe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                        pipe.Write(bytes);
                    },
                    TestContext.Current.CancellationToken
                )
            );
        }

        var load = Task.Run(copy.Load, TestContext.Current.CancellationToken);
        Assert.Same(load, await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)));
        Assert.Equal(Content.Identity, (await load).Identity);
        await Task.WhenAll(writers);
    }

    [Fact]
    public void LineEndingsAndIndentationDoNotChangeIt()
    {
        // As a checkout on Windows has the files, and then as a tool that indents otherwise would leave them. A
        // line break in JSON is never inside a string, so this changes every file's bytes and nothing it says.
        using var copy = new ContentCopy();
        var rewritten = 0;
        foreach (var file in copy.Files.ToArray())
        {
            var text = copy.Read(file).ReplaceLineEndings("\n");
            Assert.DoesNotContain("\r", text);
            copy.Write(file, text.Replace("\n", "  \r\n\t") + "\r\n\r\n");
            rewritten++;
        }

        Assert.True(rewritten >= 9, $"{rewritten} content files were rewritten");
        Assert.Equal(Content.Identity, copy.Load().Identity);
    }

    [Theory]
    // The block catalogue: a block's figure, a flight rule, a size rule, a design limit, a block's name.
    [InlineData("blocks/blocks.json", "\"Hull plating\", \"mass\": 2.0, \"hull\": 400", "\"Hull plating\", \"mass\": 2.0, \"hull\": 401")]
    [InlineData("blocks/blocks.json", "\"collisionBounce\": 0.3", "\"collisionBounce\": 0.31")]
    [InlineData("blocks/blocks.json", "\"volumeExponent\": 1.25", "\"volumeExponent\": 1.5")]
    [InlineData("blocks/blocks.json", "\"maxBoxSide\": 64", "\"maxBoxSide\": 63")]
    [InlineData("blocks/cargo.json", "\"hull\": 50", "\"hull\": 51")]
    [InlineData("blocks/blocks.json", "\"Hull plating\"", "\"Hull plate\"")]
    // The drone figures: how many a player may have out, and how they come home.
    [InlineData("blocks/drones.json", "\"maxPerPlayer\": 12", "\"maxPerPlayer\": 11")]
    [InlineData("blocks/drones.json", "\"dockRadiusMetres\": 0.6", "\"dockRadiusMetres\": 0.7")]
    // The world setting catalogue: what a setting may be set to.
    [InlineData("rules/settings.json", "\"name\": \"Weak\",\n          \"value\": 4", "\"name\": \"Weak\",\n          \"value\": 5")]
    // The galaxy rules.
    [InlineData("galaxy/galaxy.json", "\"winding\": 4.5", "\"winding\": 4.6")]
    // What is inside every asteroid.
    [InlineData("galaxy/interiors.json", "\"ore\": 3.0", "\"ore\": 3.1")]
    // Rock edits: what a scrape may be and how much a rock may hold, which the server enforces and the client checks.
    [InlineData("rules/rock_edits.json", "\"maxSegmentMetres\": 4", "\"maxSegmentMetres\": 3")]
    [InlineData("rules/rock_edits.json", "\"maxEditsPerRock\": 2048", "\"maxEditsPerRock\": 2047")]
    [InlineData("rules/rock_edits.json", "\"maxRadiusMetres\": 2", "\"maxRadiusMetres\": 3")]
    public void AnyChangeToWhatBothSidesSimulateGivesAnotherIdentity(string file, string old, string replacement)
    {
        using var copy = new ContentCopy();
        Assert.NotEqual(Content.Identity, copy.Replace(file, old, replacement).Load().Identity);
    }

    [Theory]
    // What only the client draws with and what only one player's keys do.
    [InlineData("visuals/shields.json", "\"rippleSeconds\": 0.85", "\"rippleSeconds\": 1.3")]
    [InlineData("visuals/gas.json", "\"viewRangeSectors\": 5", "\"viewRangeSectors\": 3")]
    [InlineData("input/bindings.json", "\"Key:F3\"", "\"Key:F4\"")]
    // Where the list of games is, which a host may change for their own.
    [InlineData("net/listing.json", "\"refreshSeconds\": 30", "\"refreshSeconds\": 31")]
    // Limits the server enforces by its own files, whatever a client's say.
    [InlineData("net/social.json", "\"partySize\": 8", "\"partySize\": 6")]
    // A shipped design: one block of another type, and one block elsewhere.
    [InlineData("ships/prospector.json", "{\"at\": [0, -1, 2], \"type\": \"hull\"", "{\"at\": [0, -1, 2], \"type\": \"battery\"")]
    [InlineData("ships/prospector.json", "{\"at\": [0, 0, 3], \"type\": \"hull\"}", "{\"at\": [0, 1, 3], \"type\": \"hull\"}")]
    // The fleet list: the order of the designs, which is what a design number means, and what a player starts with.
    [InlineData("ships/fleet.json", "\"prospector\", \"drone\", \"work_drone\"", "\"drone\", \"prospector\", \"work_drone\"")]
    [InlineData("ships/fleet.json", "\"startingFleet\": []", "\"startingFleet\": [\"prospector\"]")]
    public void WhatCannotPutTheSimulationOutOfStepIsLeftOut(string file, string old, string replacement)
    {
        using var copy = new ContentCopy();
        Assert.Equal(Content.Identity, copy.Replace(file, old, replacement).Load().Identity);
    }

    [Fact]
    public void ADesignTheFleetListDoesNotNameIsLeftOut()
    {
        // Until designs travel by hash, the designs that matter are the ones a design number can mean.
        using var copy = new ContentCopy();
        copy.Write("ships/unlisted.json", copy.Read("ships/drone.json"));
        Assert.Equal(Content.Identity, copy.Load().Identity);
    }

    [Fact]
    public void AWorldsSettingsDoNotChangeIt()
    {
        // They travel in the welcome: a client need not have had them beforehand, only the catalogue they come from.
        var settings = Content.SettingCatalog.Defaults;
        Content.SettingCatalog.Assign(ref settings, "collisionDamage=2.5");
        var world = Content.WithSettings(settings);
        Assert.NotSame(Content, world);
        Assert.NotEqual(Content.Settings, world.Settings);
        Assert.Equal(Content.Identity, world.Identity);
    }

    [Fact]
    public void AFileCountsForWhatItSaysAndForNothingElse()
    {
        static ContentIdentity Of(params (string Name, string Json)[] files)
        {
            var builder = new ContentIdentityBuilder();
            foreach (var (name, json) in files)
                builder.Add(name, Encoding.UTF8.GetBytes(json));
            return builder.Build();
        }

        var plain = Of(("a.json", "{\"name\":\"Ada\",\"mass\":[1,2.5,true,null]}"));

        // Spacing, line endings and how a string or a property name is escaped say nothing.
        Assert.Equal(plain, Of(("a.json", "{\r\n\t\"name\" : \"\\u0041da\",\r\n  \"ma\\u0073s\" : [ 1, 2.5,\ttrue , null ]\r\n}\r\n")));

        // A value, a name, an entry more, and the order of entries and of properties all do.
        Assert.NotEqual(plain, Of(("a.json", "{\"name\":\"Ada\",\"mass\":[1,2.6,true,null]}")));
        Assert.NotEqual(plain, Of(("a.json", "{\"name\":\"Ada\",\"mass\":[1,2.5,false,null]}")));
        Assert.NotEqual(plain, Of(("a.json", "{\"name\":\"ada\",\"mass\":[1,2.5,true,null]}")));
        Assert.NotEqual(plain, Of(("a.json", "{\"name\":\"Ada\",\"Mass\":[1,2.5,true,null]}")));
        Assert.NotEqual(plain, Of(("a.json", "{\"name\":\"Ada\",\"mass\":[1,2.5,true,null,null]}")));
        Assert.NotEqual(plain, Of(("a.json", "{\"name\":\"Ada\",\"mass\":[2.5,1,true,null]}")));
        Assert.NotEqual(plain, Of(("a.json", "{\"mass\":[1,2.5,true,null],\"name\":\"Ada\"}")));

        // A number is taken as written: the loaders do not all read 1 and 1.0 as one type.
        Assert.NotEqual(plain, Of(("a.json", "{\"name\":\"Ada\",\"mass\":[1.0,2.5,true,null]}")));

        // A string that reads like a number, a truth value or nothing is still a string.
        Assert.NotEqual(Of(("a.json", "[1]")), Of(("a.json", "[\"1\"]")));
        Assert.NotEqual(Of(("a.json", "[true]")), Of(("a.json", "[\"true\"]")));
        Assert.NotEqual(Of(("a.json", "[null]")), Of(("a.json", "[\"null\"]")));

        // Which file says it counts, and so does the order of the files.
        Assert.NotEqual(plain, Of(("b.json", "{\"name\":\"Ada\",\"mass\":[1,2.5,true,null]}")));
        Assert.NotEqual(Of(("a.json", "[1]"), ("b.json", "[2]")), Of(("b.json", "[2]"), ("a.json", "[1]")));

        // Files do not run together: where one ends and the next begins is part of what is hashed.
        Assert.NotEqual(Of(("a.json", "[[1],[2]]")), Of(("a.json", "[1]"), ("a.json", "[2]")));
    }

    [Fact]
    public void AnotherEvaluatorIsOtherContentWithTheSameFiles()
    {
        var json = "{\"mass\":2}"u8.ToArray();
        var current = new ContentIdentityBuilder();
        current.Add("blocks/blocks.json", json);
        var named = new ContentIdentityBuilder(ContentIdentity.EvaluatorVersion);
        named.Add("blocks/blocks.json", json);
        var next = new ContentIdentityBuilder(ContentIdentity.EvaluatorVersion + 1);
        next.Add("blocks/blocks.json", json);

        Assert.Equal(current.Build(), named.Build());
        Assert.NotEqual(current.Build(), next.Build());
    }

    [Fact]
    public void TheContentATestUsesAsAnotherGamesIsNotTheShippedOne()
    {
        Assert.NotEqual(Content.Identity, OtherContent.Value.Identity);
        Assert.Equal(OtherContent.Value.Identity, OtherContent.Value.WithSettings(Content.Settings).Identity);
    }
}
