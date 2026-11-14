using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Screens;
using Prospect.Client.Settings;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The in-game tips apart from drawing: the content file and its refusals, the board's rules for stacking, Next,
/// closing and hiding, where a tip is placed, and the hidden list in the settings file. The panel itself is in
/// <see cref="FrameOrderTests"/>, which owns Dear ImGui's one context.
/// </summary>
public sealed class TipTests
{
    private static readonly string Content = Path.Combine(AppContext.BaseDirectory, "content");
    private static string Shipped => Path.Combine(Content, "visuals", "tips.json");

    private static ActionMap Actions() => ActionMap.Load(Path.Combine(Content, "input", "bindings.json"));

    private static TipCatalogue Catalogue() => TipCatalogue.Load(Shipped, Actions());

    private static string Scratch() => Path.Combine(Path.GetTempPath(), "prospect-tips-" + Guid.NewGuid().ToString("N"));

    /// <summary>The shipped file with one piece of text changed, written where the test can load it.</summary>
    private static string Edited(string from, string to)
    {
        var text = File.ReadAllText(Shipped);
        Assert.Contains(from, text);
        var path = Scratch() + ".json";
        File.WriteAllText(path, text.Replace(from, to));
        return path;
    }

    [Fact]
    public void TheShippedTipsLoadWithTheKeysTheBindingsNameAndEveryTriggerHasATip()
    {
        var catalogue = Catalogue();
        Assert.True(catalogue.Tips.Length >= 10);
        foreach (var tip in catalogue.Tips)
        {
            Assert.DoesNotContain('{', tip.Text);
            Assert.NotEmpty(tip.TitleBytes);
            Assert.NotEmpty(tip.TextBytes);
        }

        var layers = catalogue.Tips.Single(t => t.Id == "map-layers");
        Assert.Contains("PAGE UP and PAGE DOWN", layers.Text);
        Assert.Contains("V tilts", layers.Text);
        Assert.Equal("You can also edit the drone: press Ctrl+B.", catalogue.Tips.Single(t => t.Id == "drone-edit").Text);
        foreach (var trigger in TipTriggers.All)
            Assert.Contains(catalogue.Tips, t => t.Trigger == trigger);
    }

    [Fact]
    public void TheUnchartedSectorTipNamesTheScanKeyAndFollowsARebinding()
    {
        var catalogue = TipCatalogue.Load(Shipped, ActionMap.Load(Path.Combine(Content, "input", "bindings.json")));
        var tip = catalogue.Tips.Single(t => t.Id == "scan-key");
        Assert.Equal(TipTriggers.UnchartedSector, tip.Trigger);
        Assert.Contains("Press K to scan this sector", tip.Text);
        Assert.Contains("K does the same", catalogue.Tips.Single(t => t.Id == "map-scan").Text);

        var path = Scratch() + ".json";
        File.WriteAllText(path, File.ReadAllText(Path.Combine(Content, "input", "bindings.json")).Replace("\"ScanSector\": [\"Key:K\", \"Pad:DpadLeft\"]", "\"ScanSector\": [\"Key:J\"]"));
        Assert.Contains("Press J to scan this sector", TipCatalogue.Load(Shipped, ActionMap.Load(path)).Tips.Single(t => t.Id == "scan-key").Text);

        // The board brings it up on its trigger, once until it is hidden.
        var board = new TipBoard(catalogue, new ClientSettings());
        Assert.Equal(1, board.Fire(TipTriggers.UnchartedSector));
        Assert.Equal("scan-key", board.Current.Id);
    }

    [Fact]
    public void ARebindingChangesTheKeyATipNames()
    {
        var path = Scratch() + ".json";
        File.WriteAllText(path, File.ReadAllText(Path.Combine(Content, "input", "bindings.json")).Replace("\"ToggleEditor\": [\"Key:B\"]", "\"ToggleEditor\": [\"Key:K\"]"));
        var catalogue = TipCatalogue.Load(Shipped, ActionMap.Load(path));
        Assert.Equal("You can also edit the drone: press Ctrl+K.", catalogue.Tips.Single(t => t.Id == "drone-edit").Text);
        Assert.Equal("(unbound)", TipCatalogue.Resolve("{Screenshot}", ActionMap.Load(WriteBindings("\"Screenshot\": []"))));
    }

    private static string WriteBindings(string actions)
    {
        var path = Scratch() + ".json";
        File.WriteAllText(path, "{\"actions\": {" + actions + "}, \"axes\": {}}");
        return path;
    }

    [Theory]
    [InlineData("\"trigger\": \"mapOpened\"", "\"trigger\": \"nowhere\"")]
    [InlineData("\"id\": \"map-fog\"", "\"id\": \"map-layers\"")]
    [InlineData("{MapTilt}", "{NotAnAction}")]
    [InlineData("{MapTilt}", "{MapTilt")]
    [InlineData("\"offset\": [0, 40]", "\"offset\": [0]")]
    [InlineData("\"region\": \"editorParts\"", "\"region\": \"moon\"")]
    [InlineData("\"panelWidth\": 340", "\"panelWidth\": 20")]
    [InlineData("\"trigger\": \"droneEditHint\"", "\"trigger\": \"mapOpened\"")]
    public void AFileWithAnUnknownRepeatedOrOutOfRangeEntryIsRefusedWhole(string from, string to)
    {
        var path = Edited(from, to);
        Assert.Throws<InvalidDataException>(() => TipCatalogue.Load(path, Actions()));
    }

    [Fact]
    public void ATriggerBringsUpItsTipsInFileOrderAndTheyStack()
    {
        var board = new TipBoard(Catalogue(), new ClientSettings());
        Assert.False(board.Active);
        Assert.Equal(3, board.Fire(TipTriggers.CommandViewOpened));
        Assert.Equal(3, board.Count);
        Assert.Equal(0, board.Index);
        Assert.Equal("command-select", board.Current.Id);

        // The same trigger again adds nothing that is already waiting; another adds to the end of the stack.
        Assert.Equal(0, board.Fire(TipTriggers.CommandViewOpened));
        Assert.Equal(1, board.Fire(TipTriggers.ShieldOffline));
        Assert.Equal(4, board.Count);
    }

    [Fact]
    public void NextGoesRoundTheStackAndWraps()
    {
        var board = new TipBoard(Catalogue(), new ClientSettings());
        board.Fire(TipTriggers.CommandViewOpened);
        var seen = new List<string> { board.Current.Id };
        for (var i = 0; i < 3; i++)
        {
            board.Next();
            seen.Add(board.Current.Id);
        }

        Assert.Equal(["command-select", "command-jump", "command-fog", "command-select"], seen);
        Assert.Equal(3, board.Count);
    }

    [Fact]
    public void ClosingWithoutTheTickShowsTheTipsAgainNextTimeAndLeavesTheSettingsAlone()
    {
        var settings = new ClientSettings();
        var board = new TipBoard(Catalogue(), settings);
        board.Fire(TipTriggers.MapOpened);
        board.Close();
        Assert.False(board.Active);
        Assert.Empty(settings.HiddenTips);
        Assert.False(board.TakeChanged());
        Assert.Equal(2, board.Fire(TipTriggers.MapOpened));
    }

    [Fact]
    public void ClosingWithTheTickHidesThatTipForGoodAndKeepsTheRest()
    {
        var settings = new ClientSettings();
        var board = new TipBoard(Catalogue(), settings);
        board.Fire(TipTriggers.MapOpened);
        board.DontShowAgain = true;
        board.Close();
        Assert.Equal(["map-layers"], settings.HiddenTips);
        Assert.True(board.TakeChanged());
        Assert.False(board.TakeChanged());
        Assert.False(board.DontShowAgain);

        // Only the ticked one is gone; the other still comes.
        Assert.Equal(1, board.Fire(TipTriggers.MapOpened));
        Assert.Equal("map-fog", board.Current.Id);
    }

    [Fact]
    public void ATickedTipLeavesTheStackOnNextAndTheNextOneIsShown()
    {
        var settings = new ClientSettings();
        var board = new TipBoard(Catalogue(), settings);
        board.Fire(TipTriggers.CommandViewOpened);
        board.Next();
        board.DontShowAgain = true;
        board.Next();
        Assert.Equal(["command-jump"], settings.HiddenTips);
        Assert.Equal(2, board.Count);
        Assert.Equal("command-fog", board.Current.Id);
        Assert.False(board.DontShowAgain);

        // A ticked last tip leaves too, and the stack wraps to the first.
        board.DontShowAgain = true;
        board.Next();
        Assert.Equal(1, board.Count);
        Assert.Equal("command-select", board.Current.Id);
    }

    [Fact]
    public void ResetShowsEveryTipAgain()
    {
        var settings = new ClientSettings();
        var board = new TipBoard(Catalogue(), settings);
        board.Fire(TipTriggers.EnergyLow);
        board.DontShowAgain = true;
        board.Close();
        Assert.Equal(0, board.Fire(TipTriggers.EnergyLow));
        board.TakeChanged();

        board.Reset();
        Assert.Empty(settings.HiddenTips);
        Assert.True(board.TakeChanged());
        Assert.Equal(1, board.Fire(TipTriggers.EnergyLow));

        // Nothing hidden: nothing to save.
        board.Clear();
        board.Reset();
        Assert.False(board.TakeChanged());
    }

    [Fact]
    public void WithTipsOffNothingFires()
    {
        var board = new TipBoard(Catalogue(), new ClientSettings()) { Enabled = false };
        Assert.Equal(0, board.Fire(TipTriggers.MapOpened));
        Assert.False(board.Active);
    }

    [Fact]
    public void EscapeClosesAShownTipAndOnlyThen()
    {
        var board = new TipBoard(Catalogue(), new ClientSettings());
        Assert.False(board.Escape(true, true));
        board.Fire(TipTriggers.MapOpened);

        // A tip covered by the menu, or no press: Escape is someone else's.
        Assert.False(board.Escape(false, true));
        Assert.False(board.Escape(true, false));
        Assert.True(board.Active);

        // Shown and pressed: the tip took it, and nothing else may.
        Assert.True(board.Escape(true, true));
        Assert.False(board.Active);
        Assert.False(board.Escape(true, true));
    }

    [Fact]
    public void WhatWasHiddenRoundTripsThroughTheSettingsFileAndJunkIsDropped()
    {
        var path = Scratch() + ".json";
        var settings = new ClientSettings();
        settings.HiddenTips.AddRange(["map-layers", "energy-low", "map-layers", "", new string('x', 200)]);
        settings.Save(path);
        var loaded = ClientSettings.Load(path);
        Assert.Equal(["map-layers", "energy-low"], loaded.HiddenTips);

        // Reset, saved, loaded: empty. A file with no list at all loads as empty too.
        loaded.HiddenTips.Clear();
        loaded.Save(path);
        Assert.Empty(ClientSettings.Load(path).HiddenTips);
        File.WriteAllText(path, "{ \"VSync\": false, \"HiddenTips\": null }");
        Assert.Empty(ClientSettings.Load(path).HiddenTips);

        var many = new ClientSettings();
        for (var i = 0; i < 400; i++)
            many.HiddenTips.Add("tip-" + i);
        many.Save(path);
        Assert.Equal(ClientSettings.MaxHiddenTips, ClientSettings.Load(path).HiddenTips.Count);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void ATipIsPlacedOnScreenUnderTheBarWhateverItIsAnchoredTo(float scale)
    {
        var display = new float2(1280, 720);
        var size = new float2(340 * scale, 190 * scale);
        var margin = FloatingWindow.Margin * scale;
        var top = HudLayout.TopBarBottom(scale) + margin;
        var console = new HudRect(300, 600, 980, 700);
        var point = new HudRect(1270, 400, 1270, 400);

        foreach (var region in Enum.GetValues<TipRegion>())
            foreach (var side in Enum.GetValues<TipSide>())
                foreach (var known in new[] { true, false })
                {
                    var anchor = new TipAnchor(region, side, 0, 0);
                    var area = region is TipRegion.Cursor or TipRegion.Target ? point : console;
                    var at = TipPlacement.Place(anchor, known, area, size, display, scale);
                    Assert.True(at.X >= margin - 0.01f && at.X + size.X <= display.X - margin + 0.01f, $"{region} {side} {known}: x {at.X}");
                    Assert.True(at.Y >= top - 0.01f && at.Y + size.Y <= display.Y - margin + 0.01f, $"{region} {side} {known}: y {at.Y}");
                }

        // Inside the screen at the named corner.
        var corner = TipPlacement.Place(new TipAnchor(TipRegion.Screen, TipSide.BottomLeft, 0, 0), true, default, size, display, scale);
        Assert.Equal(new float2(margin, display.Y - margin - size.Y), corner);

        // Outside a panel, above it, centred along it; and an unknown region falls back to the top right.
        var above = TipPlacement.Place(new TipAnchor(TipRegion.Console, TipSide.Top, 0, 0), true, console, size, display, scale);
        Assert.Equal(console.Top - TipPlacement.Gap * scale - size.Y, above.Y, 0.5f);
        Assert.Equal((console.Left + console.Right - size.X) * 0.5f, above.X, 0.5f);
        var lost = TipPlacement.Place(new TipAnchor(TipRegion.EditorBar, TipSide.Top, 0, 0), false, default, size, display, scale);
        Assert.Equal(new float2(display.X - margin - size.X, top), lost);

        // Beside a point near the right edge the tip flips to the left of it, never over it.
        var beside = TipPlacement.Place(new TipAnchor(TipRegion.Cursor, TipSide.BottomRight, 0, 0), true, point, size, display, scale);
        Assert.True(beside.X + size.X <= point.Left);
    }
}
