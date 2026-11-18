using System.Security.Cryptography;
using Prospect.Client.Screens;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The advanced locator: the one-kind rule a design is held to, the filter on the wire and in the server's pulse, the
/// radius its marks word as a diameter, and its popup. Nothing here draws.
/// </summary>
public sealed class AdvancedLocatorTests
{
    private static readonly GameContent Content = GameContent.Load();

    private static DesignMount Locator(string type, int parent, BlockFacing face, int u, int v) =>
        new(parent, face, u, v, Content.Blocks.Get(type));

    private static BoxDesign BothLocatorKinds()
    {
        // Two plating blocks side by side, a basic locator on the first block's top face and the advanced one on the
        // second's: exactly what a ship may not carry.
        DesignBox[] boxes = [new(new(0, 0, 0), new(1, 1, 1), Content.Blocks.Get("core")), new(new(1, 0, 0), new(1, 1, 1), Content.Blocks.Get("hull"))];
        return new BoxDesign("both", 1, boxes, Content.Blocks.Limits, "test", HullClass.Ship, default,
            [Locator("asteroidLocator", 0, BlockFacing.Up, 2, 2), Locator("advancedLocator", 1, BlockFacing.Up, 1, 1)]);
    }

    [Fact]
    public void ADesignWithBothKindsOfLocatorIsRefusedByItsBytesAndByTheDraft()
    {
        // The constructor does not ask, as it does not ask about drives, so arithmetic tests may build one; every path
        // a design really takes does ask.
        var design = BothLocatorKinds();
        Assert.Equal(ModuleFamily.AsteroidLocator, design.Mounts[0].Type.Module.Family);
        Assert.NotEqual(design.Mounts[0].Type.Module.Filter, design.Mounts[1].Type.Module.Filter);
        var bytes = DesignBytes.Encode(HullDesign.Of(design, Content.Rules));
        var refused = Assert.Throws<InvalidDataException>(() => DesignBytes.Decode(bytes, SHA256.HashData(bytes), Content));
        Assert.Contains("one kind only", refused.Message);

        // The draft refuses the second kind where it is placed, and says which rule it broke.
        using var copy = new ContentCopy();
        var content = copy.Load();
        var draft = BuildDraft.FromBlueprint(content.Pod, content);
        var (parent, u, v) = FreeFace(draft, content);
        var basic = content.Blocks.Get("asteroidLocator");
        var advanced = content.Blocks.Get("advancedLocator");
        Assert.Equal(DraftEdit.Done, draft.AddMount(parent, BlockFacing.Up, u, v, basic));
        // The pod already carries basic locators, so the advanced one is the second kind.
        Assert.Equal(DraftEdit.MixedLocators, draft.CanAddMount(parent, BlockFacing.Up, u, v, advanced));
        Assert.Equal(DraftEdit.MixedLocators, draft.AddMount(parent, BlockFacing.Up, u, v, advanced));
        // Several of the same kind stay allowed: they are redundancy, not a mix. The new part is where it always was.
        var (second, u2, v2) = FreeFace(draft, content);
        Assert.Equal(DraftEdit.Done, draft.AddMount(second, BlockFacing.Up, u2, v2, basic));
        Assert.Equal(DraftEdit.MixedLocators, draft.CanAddMount(second, BlockFacing.Up, u2, v2, advanced));
    }

    [Fact]
    public void ADesignFileWithBothKindsIsRefusedOnLoad()
    {
        using var copy = new ContentCopy();
        const string Design = """
            {"version": 9, "name": "both", "class": "ship", "blockSize": 1, "color": [1, 1, 1],
             "blocks": [{"type": "core", "at": [0, 0, 0]}, {"type": "hull", "at": [1, 0, 0]}],
             "mounts": [{"on": 0, "face": "up", "at": [2, 2], "type": "asteroidLocator"},
                        {"on": 1, "face": "up", "at": [1, 1], "type": "advancedLocator"}]}
            """;
        var path = Path.Combine(copy.Root, "both.json");
        File.WriteAllText(path, Design);
        var refused = Assert.Throws<InvalidDataException>(() => BoxDesign.Load(path, copy.Load().Blocks));
        Assert.Contains("one kind only", refused.Message);
        // One kind on its own loads: the rule is about mixing them.
        File.WriteAllText(path, Design.Replace("\"asteroidLocator\"", "\"advancedLocator\""));
        var loaded = BoxDesign.Load(path, copy.Load().Blocks);
        Assert.True(loaded.Mounts[0].Type.Module.Filter);
    }

    /// <summary>A top face of the draft with room for an advanced locator: the block, and where on it the part goes.</summary>
    private static (int Parent, int U, int V) FreeFace(BuildDraft draft, GameContent content)
    {
        var advanced = content.Blocks.Get("asteroidLocator");
        var boxes = draft.Boxes.ToArray();
        for (var index = 0; index < boxes.Length; index++)
            for (var u = 0; u < FaceMounts.PerCell; u++)
                for (var v = 0; v < FaceMounts.PerCell; v++)
                    if (draft.CanAddMount(index, BlockFacing.Up, u, v, advanced) == DraftEdit.Done)
                        return (index, u, v);
        throw new InvalidOperationException($"the pod has no free top face for a locator ({boxes.Length} blocks, {draft.CanAddMount(0, BlockFacing.Up, 0, 0, advanced)})");
    }

    [Fact]
    public void TheFilterTravelsWithThePulseAndItsBoundsAreChecked()
    {
        var filter = new LocatorFilter(120, 800, LocatorFilter.BitOf(0) | LocatorFilter.BitOf(3));
        var request = new ModuleScanRequest(ModuleFamily.AsteroidLocator, default, 0, filter);
        Span<byte> bytes = stackalloc byte[ModuleMessage.RequestWireSize];
        var writer = new NetWriter(bytes);
        ModuleMessage.WriteRequest(ref writer, request);
        Assert.Equal(ModuleMessage.RequestWireSize, writer.Length);
        var reader = new NetReader(bytes[1..]);
        Assert.True(ModuleMessage.ReadRequest(ref reader, out var actual));
        Assert.Equal(request, actual);
        Assert.Equal(2, actual.Filter.OreCount);

        // A range the wrong way round, or one that reaches past what a diameter may be, is refused as it is read.
        foreach (var bad in new[] { new LocatorFilter(500, 200, 0), new LocatorFilter(0, 0, 0) with { MinMetres = 400, MaxMetres = 100 } })
        {
            writer = new NetWriter(bytes);
            ModuleMessage.WriteRequest(ref writer, new ModuleScanRequest(ModuleFamily.AsteroidLocator, default, 0, bad));
            reader = new NetReader(bytes[1..]);
            Assert.False(ModuleMessage.ReadRequest(ref reader, out _));
        }

        // Unknown ore bits, and a resource this galaxy does not have, are the server's to refuse.
        Assert.True(filter.Valid(Content.Galaxy));
        var galaxy = Content.Galaxy;
        Assert.False(new LocatorFilter(0, 0, LocatorFilter.BitOf(galaxy.Resources.Length)).Valid(galaxy));
        Assert.True(LocatorFilter.AllOres(galaxy) != 0);
        Assert.Equal(galaxy.Resources.Length, new LocatorFilter(0, 0, LocatorFilter.AllOres(galaxy)).OreCount);
    }

    [Fact]
    public void APulseWithAFilterReportsOnlyMatchingRocksWithTheirRadiusAndABasicPulseStaysCoarse()
    {
        // A pulse with no filter reports every rock in reach, each with its radius.
        RockRef rock = default;
        int ore;
        float radius;
        using (var rig = new ModuleScanTests.Rig(locator: "advancedLocator"))
        {
            rig.PlaceNearRock();
            var range = rig.Content.Pod.Stats.AsteroidLocator.Range;
            Assert.True(rig.Content.Pod.Stats.AsteroidLocator.Filter);
            rig.Server.RequestModuleScan(rig.Owner(), new(ModuleFamily.AsteroidLocator, default));
            rig.Until(() => rig.Scan().State == ModuleScanState.Completed);
            var records = rig.Server.ModuleKnowledge(rig.Owner()).ToArray();
            Assert.NotEmpty(records);
            Assert.All(records, record => Assert.True(record.Radius > 0));
            Assert.All(records, record => Assert.True(ModuleScanRules.InRange(rig.Scan().Origin, record.Position, 0, range)));

            // One rock with an ore to look for, its diameter and its identity.
            var first = Array.Find(records, record => record.Resource >= 0);
            ore = first.Resource;
            radius = first.Radius;
            rock = new RockRef(first.Sector, first.Index);
            Assert.True(ore >= 0 && radius > 0);
        }

        // One ore and a diameter range in a fresh world: only the rocks that answer both are reported, and the record
        // of the rock the pulse was aimed at is among them.
        var filter = new LocatorFilter((ushort)Math.Max(1, radius * 2 - 1), (ushort)Math.Ceiling(radius * 2 + 1), LocatorFilter.BitOf(ore));
        using var filtered = new ModuleScanTests.Rig(locator: "advancedLocator");
        filtered.PlaceNearRock();
        filtered.Clients[0].Input.PulseAsteroidLocator(filter);
        filtered.Until(() => filtered.Clients[0].AcquireSnapshot().Modules.Scan.State == ModuleScanState.Completed);
        var seen = filtered.Clients[0].AcquireSnapshot().Modules;
        Assert.True(seen.Count > 0);
        var found = false;
        for (var i = 0; i < seen.Count; i++)
        {
            Assert.Equal(ore, seen.Records[i].Resource);
            Assert.True(filter.WithinDiameter(seen.Records[i].Radius));
            // A locator still reports no composition and no mass: the radius is all its marks add.
            Assert.Equal(0, seen.Records[i].Mass);
            Assert.Equal(0, seen.Records[i].Grade);
            Assert.True(seen.Records[i].Radius > 0);
            found |= seen.Records[i].Sector == rock.Sector && seen.Records[i].Index == rock.Index;
        }

        Assert.True(found, "the rock the filter was built from is in its own pulse");
        Assert.Equal(0, filtered.Wires[0].Leaks + filtered.Wires[1].Leaks);

        // A filter for a basic locator, and one naming an ore this galaxy has not, are refused and cool nothing.
        using var basic = new ModuleScanTests.Rig();
        basic.PlaceNearRock();
        basic.Server.RequestModuleScan(basic.Owner(), new(ModuleFamily.AsteroidLocator, default, 0, filter));
        Assert.Equal(ModuleScanState.InvalidTarget, basic.Scan().State);
        Assert.Equal(0, basic.Scan().LocatorCooldownUntilTick);
        filtered.Until(() => filtered.Tick > filtered.Scan().CooldownUntil(ModuleFamily.AsteroidLocator));
        var cooled = filtered.Scan().LocatorCooldownUntilTick;
        var unknown = new LocatorFilter(0, 0, LocatorFilter.BitOf(filtered.Server.Galaxy.Resources.Length));
        filtered.Server.RequestModuleScan(filtered.Owner(), new(ModuleFamily.AsteroidLocator, default, 0, unknown));
        Assert.Equal(ModuleScanState.InvalidTarget, filtered.Scan().State);
        // A refusal before dispatch starts no new cooldown: the owner keeps the countdown it had.
        Assert.Equal(cooled, filtered.Scan().LocatorCooldownUntilTick);
    }

    [Fact]
    public void ALocatorRecordWithARadiusIsSoundOnTheWireAndInASave()
    {
        // An advanced pulse's record says how wide the rock is and nothing else about it; a basic pulse's says no width.
        var record = new ModuleObservation
        {
            Owner = 1,
            Family = ModuleFamily.AsteroidLocator,
            Quality = ModuleQuality.Common,
            Sector = new(1, 2, 3),
            Index = 4,
            RecordedTick = 5,
            Radius = 42,
            Resource = 3,
            Mix1 = -1,
            Mix2 = -1
        };
        Assert.True(ModuleObservationRules.Valid(record));
        var bytes = new byte[ModuleMessage.ObservationWireSize];
        var writer = new NetWriter(bytes);
        ModuleMessage.WriteObservation(ref writer, record);
        var reader = new NetReader(bytes);
        Assert.True(ModuleMessage.ReadObservation(ref reader, out var read));
        Assert.Equal(42f, read.Radius);
        Assert.Equal(record.Resource, read.Resource);

        // The rest of a locator stays coarse: a mass, a grade or a mix-in is no locator's to report.
        record.Mass = 10;
        Assert.False(ModuleObservationRules.Valid(record));
        record.Mass = 0;
        record.Grade = .4f;
        Assert.False(ModuleObservationRules.Valid(record));
        record.Grade = 0;
        record.Mix1 = 2;
        Assert.False(ModuleObservationRules.Valid(record));
    }

    [Fact]
    public void ABasicLocatorsRecordsKeepNoRadiusAndNoFilterReachesIt()
    {
        using var rig = new ModuleScanTests.Rig();
        rig.PlaceNearRock();
        Assert.False(rig.Content.Pod.Stats.AsteroidLocator.Filter);
        // The basic chip pulses as it always did: no filter, and its records word no size.
        rig.Clients[0].Input.PulseAsteroidLocator();
        rig.Until(() => rig.Scan().State == ModuleScanState.Completed);
        var records = rig.Server.ModuleKnowledge(rig.Owner()).ToArray();
        Assert.NotEmpty(records);
        Assert.All(records, record => Assert.Equal(0, record.Radius));
        rig.Until(() => rig.Clients[0].AcquireSnapshot().Modules.Count > 0);
        var seen = rig.Clients[0].AcquireSnapshot().Modules;
        for (var i = 0; i < seen.Count; i++)
            Assert.Equal(0, seen.Records[i].Radius);
    }

    [Fact]
    public void ThePopupKeepsToItsHullAndToAShipWithTheAdvancedLocator()
    {
        var popup = new LocatorPopup();
        var look = ModuleLook.Load(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "modules.json")).Filter;
        popup.Follow(5, true);
        popup.Toggle();
        Assert.True(popup.IsOpen);
        // The press that opened it is on the chip, so the outside-press rule leaves it be; a press elsewhere closes it.
        popup.Press(true, false, true);
        Assert.True(popup.IsOpen);
        popup.Press(true, true, false);
        Assert.True(popup.IsOpen);
        popup.Press(true, false, false);
        Assert.False(popup.IsOpen);
        popup.Toggle();
        popup.Follow(5, true);
        Assert.True(popup.IsOpen);
        popup.Follow(6, true);
        Assert.False(popup.IsOpen);
        popup.Toggle();
        popup.Follow(6, false);
        Assert.False(popup.IsOpen);

        // Escape closes it before the view does, in either view.
        Assert.Equal(ScreenHost.BackStep.LocatorPopup, ScreenHost.NextBackStep(false, false, false, false, false, false, Prospect.Client.World.ViewMode.Pilot, false, true));
        Assert.Equal(ScreenHost.BackStep.LocatorPopup, ScreenHost.NextBackStep(false, false, false, false, false, false, Prospect.Client.World.ViewMode.Command, false, true));
        Assert.Equal(ScreenHost.BackStep.PilotView, ScreenHost.NextBackStep(false, false, false, false, false, false, Prospect.Client.World.ViewMode.Command, false, false));
    }

    [Fact]
    public void TheFilterIsSteppedWithinItsBoundsAndItsOresToggledAgainstTheGalaxy()
    {
        var galaxy = Content.Galaxy;
        var look = ModuleLook.Load(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "modules.json")).Filter;
        var any = new LocatorFilter();
        Assert.Equal("ANY DIAMETER", System.Text.Encoding.UTF8.GetString(LocatorPopup.RangeWords(any)));
        Assert.Equal("1 ORE", System.Text.Encoding.UTF8.GetString(LocatorPopup.OreWords(LocatorPopup.ToggleOre(any, 2))));

        // Both bounds step the client's figure; the lower never passes the upper one, and neither passes what the
        // controls reach.
        Assert.Equal(0, any.Stepped(min: true, steps: -1, look.StepMetres, look.MaxDiameterMetres).MinMetres);
        var filter = any.Stepped(min: true, steps: 3, look.StepMetres, look.MaxDiameterMetres);
        Assert.Equal(3 * look.StepMetres, filter.MinMetres);
        filter = any.Stepped(min: false, steps: 1, look.StepMetres, look.MaxDiameterMetres);
        Assert.Equal(look.MaxDiameterMetres, filter.MaxMetres);
        filter = filter.Stepped(min: false, steps: -3, look.StepMetres, look.MaxDiameterMetres);
        Assert.Equal(look.MaxDiameterMetres - 3 * look.StepMetres, filter.MaxMetres);
        // An upper bound of no bound steps down from the widest there is.
        Assert.Equal(look.MaxDiameterMetres - 2 * look.StepMetres, any.Stepped(min: false, steps: -2, look.StepMetres, look.MaxDiameterMetres).MaxMetres);
        // The lower stops at the upper: a range of 100 to 200 m cannot be stepped over.
        var narrow = new LocatorFilter((ushort)look.StepMetres, (ushort)(2 * look.StepMetres));
        Assert.Equal(narrow.MaxMetres, narrow.Stepped(min: true, steps: 5, look.StepMetres, look.MaxDiameterMetres).MinMetres);
        Assert.Equal(0, narrow.Stepped(min: true, steps: -5, look.StepMetres, look.MaxDiameterMetres).MinMetres);
        Assert.Equal(narrow.MinMetres, narrow.Stepped(min: false, steps: -5, look.StepMetres, look.MaxDiameterMetres).MaxMetres);

        // Ores: one bit each, all or none, and a range on its own reports every ore and bare rock alike.
        var all = LocatorPopup.AllOres(galaxy, any);
        Assert.Equal(galaxy.Resources.Length, all.OreCount);
        Assert.Equal(0, LocatorPopup.NoOres(all).OreCount);
        Assert.True(any.Allows(-1, 10) && any.Allows(2, 10));
        Assert.False(all.Allows(-1, 10));
        Assert.True(all.Allows(0, 10));
        Assert.True(new LocatorFilter(0, 0, LocatorFilter.BitOf(3)).Allows(3, 10));
        Assert.False(new LocatorFilter(0, 0, LocatorFilter.BitOf(3)).Allows(2, 10));
        Assert.Equal("ALL".Length, "ALL".Length);
    }

    [Fact]
    public void ADiameterIsWordedInWholeMetresAndTheEveryFramePartsTakeNoMemory()
    {
        var size = ScreenHost.DiameterWords(90f);
        Assert.Equal("180 M", System.Text.Encoding.UTF8.GetString(size));
        Assert.True(ScreenHost.DiameterWords(0).IsEmpty);
        var look = ModuleLook.Load(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "modules.json")).Filter;
        var galaxy = Content.Galaxy;
        var popup = new LocatorPopup();
        popup.Follow(5, true);
        var filter = new LocatorFilter(100, 2000, LocatorFilter.AllOres(galaxy));
        var sink = 0;
        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            popup.Press(true, true, false);
            filter = LocatorPopup.Step(filter, true, 1, look);
            filter = LocatorPopup.ToggleOre(filter, 1);
            filter = LocatorPopup.AllOres(galaxy, filter);
            sink += LocatorPopup.RangeWords(filter).Length + LocatorPopup.OreWords(filter).Length + ScreenHost.DiameterWords(12f).Length;
            sink += filter.Allows(1, 40) ? 1 : 0;
        }

        Assert.Equal(0, probe.Bytes);
        Assert.True(sink > 0);
    }
}
