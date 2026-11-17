using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Screens;
using Prospect.Client.Ui;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The action group strip apart from drawing: which groups show and in what order, what marks a cell on, firing and
/// cutting, where the strip sits against the console and the chips at any size, the selector's values, and the look
/// file. The drawing is in <see cref="FrameOrderTests"/>, which owns Dear ImGui's one context.
/// </summary>
public sealed class ActionGroupStripTests
{
    private static readonly string Content = Path.Combine(AppContext.BaseDirectory, "content");

    private static ActionGroupLook Look() => ActionGroupLook.Load(Path.Combine(Content, "visuals", "action_groups.json"));

    private static ModuleLook.StripLook Chips() => ModuleLook.Load(Path.Combine(Content, "visuals", "modules.json")).Strip;

    [Fact]
    public void ThereIsACellForEveryGroupWithAPartInKeyOrderAndNoneWithoutAny()
    {
        var strip = new ActionGroupStrip();
        strip.Begin();
        strip.End(ActionGroups.AllMask, true);
        Assert.Equal(0, strip.Count);

        // Parts added in any order: the cells run 1 to 9, then group 10 (the key 0); a group with no part has none.
        strip.Begin();
        foreach (var group in new[] { 10, 3, 1, 3, 7 })
            strip.Add(group, false);
        strip.End(0, false);
        Assert.Equal([1, 3, 7, 10], strip.Cells.ToArray().Select(c => c.Group));
        Assert.Equal([1, 2, 1, 1], strip.Cells.ToArray().Select(c => c.Parts));
        Assert.False(strip.TryGet(2, out _));
        Assert.True(strip.TryGet(3, out var third));
        Assert.Equal(2, third.Parts);
    }

    [Fact]
    public void AGroupWithNoGroupOrABadOneIsNotInAnyCell()
    {
        var strip = new ActionGroupStrip();
        strip.Begin();
        strip.Add(ActionGroups.None, true);
        strip.Add(ActionGroups.NotOverridden, true);
        strip.Add(11, true);
        strip.Add(-1, true);
        strip.End(ActionGroups.AllMask, true);
        Assert.Equal(0, strip.Count);
    }

    [Fact]
    public void ACellIsOnWhenItsBitIsFiringOnlyWhileOnAndCuttingFromItsOwnLasers()
    {
        var strip = new ActionGroupStrip();
        strip.Begin();
        strip.Add(1, false);
        strip.Add(2, true);
        strip.Add(2, false);
        strip.Add(3, false);
        strip.End((ushort)(ActionGroups.Bit(1) | ActionGroups.Bit(2)), true);
        Assert.True(strip.TryGet(1, out var one));
        Assert.True(strip.TryGet(2, out var two));
        Assert.True(strip.TryGet(3, out var three));
        Assert.True(one.On && one.Firing && !one.Cutting);
        Assert.True(two.On && two.Firing && two.Cutting);
        Assert.False(three.On || three.Firing || three.Cutting);

        // The fire button up: on, not firing.
        strip.End((ushort)(ActionGroups.Bit(1) | ActionGroups.Bit(2)), false);
        strip.TryGet(1, out one);
        Assert.True(one.On);
        Assert.False(one.Firing);

        // A group whose bit is on but that has no part is no cell, and a cell never shows a bit it was not given.
        strip.End(ActionGroups.Bit(5), true);
        Assert.Equal(3, strip.Count);
        Assert.All(strip.Cells.ToArray(), c => Assert.False(c.On));
    }

    [Theory]
    [InlineData(1280f, 720f, 1f)]
    [InlineData(1280f, 720f, 2f)]
    [InlineData(1920f, 1080f, 1f)]
    [InlineData(1920f, 1080f, 2f)]
    [InlineData(1655f, 688f, 1f)]
    public void TheStripKeepsClearOfTheConsoleTheFuelRowTheChipsAndTheStatusSlotAtAnySize(float width, float height, float scale)
    {
        var display = new float2(width, height);
        var look = Look();
        var chips = Chips();
        var console = FlightConsole.Layout(display, scale);
        var consoleArea = new HudRect(console.LeftX, console.Top, console.Right, console.FuelTop + console.FuelRow);
        foreach (var groups in new[] { 1, 3, 10 })
            foreach (var modules in new[] { 0, 1, 4 })
            {
                var rect = ScreenHost.ActionGroupsRect(display, scale, groups, modules, chips, look, out var cellWidth);
                var label = $"{groups} groups, {modules} chips, {width}x{height} at {scale}";
                Assert.True(rect.Left >= MathF.Round(FlightConsole.Side * scale) - 0.5f && rect.Right <= display.X - MathF.Round(FlightConsole.Side * scale) + 0.5f, label);
                Assert.True(cellWidth >= MathF.Round(look.MinCellWidth * scale) - 0.5f, label);

                // Above the console and the fuel row, and above the slot the status chip and the align tag use.
                Assert.True(rect.Bottom <= consoleArea.Top - MathF.Round((Theme.Space2 + HudLayout.ChipHeight) * scale) + 0.5f, label);
                if (modules > 0)
                    Assert.True(rect.Bottom <= ScreenHost.StripRect(display, scale, modules, chips).Top, label);

                // The tips' cluster takes it in, so a tip on top of the console sits above the strip as well.
                var area = ScreenHost.ConsoleArea(display, scale, modules, chips, rect);
                Assert.True(area.Top <= rect.Top && area.Left <= rect.Left && area.Right >= rect.Right, label);
            }
    }

    [Fact]
    public void WithoutGroupsTheStripTakesNoRoomAndTheTipsClusterIsTheOldOne()
    {
        var display = new float2(1280, 720);
        var chips = Chips();
        var rect = ScreenHost.ActionGroupsRect(display, 1f, 0, 3, chips, Look(), out var cellWidth);
        Assert.Equal(0f, rect.Height);
        Assert.Equal(0f, cellWidth);
        Assert.Equal(ScreenHost.ConsoleArea(display, 1f, 3, chips), ScreenHost.ConsoleArea(display, 1f, 3, chips, rect));
    }

    [Fact]
    public void CellsLieSideBySideInsideTheStrip()
    {
        var look = Look();
        var rect = ScreenHost.ActionGroupsRect(new float2(1280, 720), 1f, 10, 0, Chips(), look, out var cellWidth);
        var gap = MathF.Round(look.Gap);
        var previousRight = float.MinValue;
        for (var i = 0; i < 10; i++)
        {
            var cell = ActionGroupStrip.CellRect(rect, i, cellWidth, gap);
            Assert.True(cell.Left >= rect.Left && cell.Right <= rect.Right + 0.5f);
            Assert.True(cell.Left >= previousRight);
            previousRight = cell.Right;
        }
    }

    [Fact]
    public void TheSelectorSendsTheGroupNoneOrBackToTheDesignAndShowsTheChoiceMade()
    {
        // The ten groups are options 0 to 9 (group 1 to 10), then none, then the design's own.
        for (var option = 0; option < ActionGroups.Count; option++)
        {
            Assert.Equal((byte)(option + 1), ActionGroupSelector.ValueOf(option));
            Assert.Equal(option, ActionGroupSelector.Chosen((byte)(option + 1)));
        }

        Assert.Equal(ActionGroups.None, ActionGroupSelector.ValueOf(ActionGroupSelector.NoneOption));
        Assert.Equal(ActionGroupSelector.NoneOption, ActionGroupSelector.Chosen(ActionGroups.None));
        Assert.Equal(ActionGroups.NotOverridden, ActionGroupSelector.ValueOf(ActionGroupSelector.DesignOption));
        Assert.Equal(ActionGroupSelector.DesignOption, ActionGroupSelector.Chosen(ActionGroups.NotOverridden));
        Assert.Equal(ActionGroupSelector.Options, ActionGroups.Count + 2);
    }

    [Fact]
    public void ACellShowsItsHottestLaserAndIsOverheatedOnlyWhenEveryLaserOfItIsShutDown()
    {
        var strip = new ActionGroupStrip();
        strip.Begin();

        // Group 1: two lasers, one shut down. Group 2: one laser, shut down. Group 3: cool. Heat out of range is held to 0..1.
        strip.Add(1, false, 0.4f);
        strip.Add(1, false, 1f, locked: true, unlock: 0.3f);
        strip.Add(2, true, 0.7f, locked: true, unlock: 0.25f);
        strip.Add(3, false);
        strip.Add(4, false, float.NaN);
        strip.Add(4, false, 7f);
        strip.End(ActionGroups.AllMask, true);

        Assert.True(strip.TryGet(1, out var mixed));
        Assert.Equal((1f, 1, 2, false, 0.3f), (mixed.Heat, mixed.Locked, mixed.Parts, mixed.Overheated, mixed.UnlockHeat));
        Assert.True(strip.TryGet(2, out var down));
        Assert.Equal((0.7f, 1, true, 0.25f), (down.Heat, down.Locked, down.Overheated, down.UnlockHeat));
        Assert.True(strip.TryGet(3, out var cool));
        Assert.Equal((0f, 0, false, 0f), (cool.Heat, cool.Locked, cool.Overheated, cool.UnlockHeat));
        Assert.True(strip.TryGet(4, out var held));
        Assert.Equal(1f, held.Heat);

        // A new count starts cool.
        strip.Begin();
        strip.Add(2, false);
        strip.End(0, false);
        Assert.True(strip.TryGet(2, out down));
        Assert.Equal((0f, false), (down.Heat, down.Overheated));
    }

    [Fact]
    public void TheStripReadsTheFlownHullsLaserHeatFromTheSnapshotAndNoOtherHulls()
    {
        var design = MiningFixtures.Content.Pod;
        var snapshot = new Prospect.Game.Client.SessionSnapshot(MiningFixtures.Content);
        var a = MiningFixtures.PartA(design);
        var b = MiningFixtures.PartB(design);
        snapshot.LaserHeatHull = 9;
        snapshot.LaserHeat.Heat[a] = 0.55f;
        snapshot.LaserHeat.Heat[b] = 1f;
        snapshot.LaserHeat.Locked = 1UL << b;

        var strip = new ActionGroupStrip();
        strip.Read(snapshot, 9, design, 0, false);
        Assert.True(strip.TryGet(1, out var first));
        Assert.Equal((0.55f, false), (first.Heat, first.Overheated));
        Assert.True(strip.TryGet(2, out var second));
        Assert.Equal((1f, true), (second.Heat, second.Overheated));
        Assert.Equal(design.Mounts[design.Lasers[b]].Type.Laser!.UnlockHeat, second.UnlockHeat);
        Assert.True(ActionGroupStrip.IsLocked(snapshot, 9, b));
        Assert.Equal(0.55f, ActionGroupStrip.HeatOf(snapshot, 9, a));

        // The heat is the flown hull's: another hull of the same design reads cool.
        strip.Read(snapshot, 12, design, 0, false);
        Assert.True(strip.TryGet(2, out second));
        Assert.Equal((0f, false), (second.Heat, second.Overheated));
        Assert.False(ActionGroupStrip.IsLocked(snapshot, 12, b));
        Assert.Equal(0f, ActionGroupStrip.HeatOf(snapshot, 0, a));
    }

    [Fact]
    public void TheHeatBarsColourStepsFromCoolToWarmToHot()
    {
        var look = Look();
        Assert.Equal(look.HeatCoolRgb, look.HeatRgb(0f));
        Assert.Equal(look.HeatCoolRgb, look.HeatRgb(look.HeatWarmFrom - 0.01f));
        Assert.Equal(look.HeatWarmRgb, look.HeatRgb(look.HeatWarmFrom));
        Assert.Equal(look.HeatHotRgb, look.HeatRgb(look.HeatHotFrom));
        Assert.Equal(look.HeatHotRgb, look.HeatRgb(1f));
        Assert.Equal("OVERHEAT", System.Text.Encoding.UTF8.GetString(look.OverheatedLabel));
        Assert.Equal("HOT", System.Text.Encoding.UTF8.GetString(look.OverheatedShortLabel));
    }

    [Fact]
    public void KeyNamesShowTheDigitOfANumberRowKeyAndTheTenthGroupIsZero()
    {
        var actions = ActionMap.Load(Path.Combine(Content, "input", "bindings.json"));
        var keys = new ActionGroupKeys();
        keys.Update(actions);
        for (var group = 1; group <= 9; group++)
            Assert.Equal(group.ToString(), System.Text.Encoding.UTF8.GetString(keys.Name(group)));
        Assert.Equal("0", System.Text.Encoding.UTF8.GetString(keys.Name(10)));
        Assert.Equal(InputAction.ActionGroup0, ActionGroupStrip.ActionOf(10));
        Assert.Equal("Page Up", ActionGroupKeys.Short("Page Up"));
        Assert.Equal("Number", ActionGroupKeys.Short("Number"));
    }

    [Fact]
    public void TheLookFileRefusesFiguresOutOfRange()
    {
        var text = File.ReadAllText(Path.Combine(Content, "visuals", "action_groups.json"));
        var scratch = Path.Combine(Path.GetTempPath(), "prospect-groups-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(scratch, text.Replace("\"cellWidth\": 104", "\"cellWidth\": 5"));
            Assert.Throws<InvalidDataException>(() => ActionGroupLook.Load(scratch));
            File.WriteAllText(scratch, text.Replace("\"firingColor\": \"FF8A4F\"", "\"firingColor\": \"red\""));
            Assert.Throws<InvalidDataException>(() => ActionGroupLook.Load(scratch));
            File.WriteAllText(scratch, text.Replace("\"label\": \"LASER\"", "\"label\": \"\""));
            Assert.Throws<InvalidDataException>(() => ActionGroupLook.Load(scratch));
            File.WriteAllText(scratch, text.Replace("\"heatBarPixels\": 3", "\"heatBarPixels\": 40"));
            Assert.Throws<InvalidDataException>(() => ActionGroupLook.Load(scratch));
            File.WriteAllText(scratch, text.Replace("\"heatHotFrom\": 0.85", "\"heatHotFrom\": 0.4"));
            Assert.Throws<InvalidDataException>(() => ActionGroupLook.Load(scratch));
            File.WriteAllText(scratch, text.Replace("\"overheatedColor\": \"FF3B30\"", "\"overheatedColor\": \"hot\""));
            Assert.Throws<InvalidDataException>(() => ActionGroupLook.Load(scratch));
            File.WriteAllText(scratch, text.Replace("\"overheatedLabel\": \"OVERHEAT\"", "\"overheatedLabel\": \"\""));
            Assert.Throws<InvalidDataException>(() => ActionGroupLook.Load(scratch));
        }
        finally
        {
            File.Delete(scratch);
        }

        Assert.Equal("LASER", System.Text.Encoding.UTF8.GetString(Look().Kind(ActionPartKind.Laser)));
    }

    [Fact]
    public void TheActionGroupTipNamesTheNumberKeys()
    {
        var actions = ActionMap.Load(Path.Combine(Content, "input", "bindings.json"));
        var tip = TipCatalogue.Load(Path.Combine(Content, "visuals", "tips.json"), actions).Tips.Single(t => t.Trigger == TipTriggers.ActionGroupReady);
        Assert.Contains("Press 1, 2", tip.Text);
        Assert.Equal(TipRegion.Console, tip.Anchor.Region);
    }

    [Fact]
    public void EscapeClosesTheActionGroupPopupBeforeBackingOutOfTheView()
    {
        Assert.Equal(ScreenHost.BackStep.ActionGroups, ScreenHost.NextBackStep(false, false, false, false, false, false, Prospect.Client.World.ViewMode.Command, true));
        Assert.Equal(ScreenHost.BackStep.PilotView, ScreenHost.NextBackStep(false, false, false, false, false, false, Prospect.Client.World.ViewMode.Command));
        Assert.Equal(ScreenHost.BackStep.SessionMenu, ScreenHost.NextBackStep(false, true, false, false, false, false, Prospect.Client.World.ViewMode.Pilot, true));
    }
}
