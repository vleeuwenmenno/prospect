using Brutal.GlfwApi;
using Prospect.Client.Input;
using Prospect.Client.Screens;
using Prospect.Client.Settings;
using Xunit;

namespace Prospect.Tests;

/// <summary>The catalogue of controls, its tabs and the search over them.</summary>
public sealed class ControlsCatalogueTests
{
    private static readonly string Content = Path.Combine(AppContext.BaseDirectory, "content");
    private static readonly string Bindings = Path.Combine(Content, "input", "bindings.json");
    private static readonly string Catalogue = Path.Combine(Content, "input", "controls.json");

    private static ControlsCatalog Shipped() => new(Catalogue, ActionMap.Load(Bindings));

    [Fact]
    public void EveryActionAndAxisIsInTheCatalogueWithATabAndALabel()
    {
        var catalog = Shipped();
        var titles = catalog.Tabs.Select(t => t.Title).ToArray();
        for (var action = (InputAction)0; action < InputAction.Count; action++)
        {
            var row = Assert.Single(catalog.Rows, r => r.Kind == RowKind.Action && r.Index == (int)action);
            Assert.Contains(row.Category, titles);
            Assert.False(string.IsNullOrWhiteSpace(row.Action));
            Assert.Equal(row.Category, catalog.TabOf(action));
            Assert.NotEqual(ControlScope.None, catalog.ScopeOf(action));
        }

        for (var axis = (InputAxis)0; axis < InputAxis.Count; axis++)
        {
            var row = Assert.Single(catalog.Rows, r => r.Kind == RowKind.Axis && r.Index == (int)axis);
            Assert.Contains(row.Category, titles);
            Assert.False(string.IsNullOrWhiteSpace(catalog.LabelOf(axis, false)));
            Assert.False(string.IsNullOrWhiteSpace(catalog.LabelOf(axis, true)));
        }

        Assert.All(catalog.Tabs, tab => Assert.NotEmpty(tab.Rows));
    }

    [Fact]
    public void EveryActionTheBindingsFileNamesIsInTheCatalogue()
    {
        var catalog = Shipped();
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Bindings));
        foreach (var entry in document.RootElement.GetProperty("actions"u8).EnumerateObject())
        {
            var action = Enum.Parse<InputAction>(entry.Name);
            Assert.Contains(catalog.Rows, r => r.Kind == RowKind.Action && r.Index == (int)action && r.Category.Length > 0);
        }
    }

    [Theory]
    [InlineData("Halt", "Jump & navigation")]
    [InlineData("ScanSector", "Scanning & exploration")]
    [InlineData("ToggleEditor", "Ship editor")]
    [InlineData("MapTilt", "Galaxy map")]
    [InlineData("MapLayerUp", "Galaxy map")]
    [InlineData("MapLayerDown", "Galaxy map")]
    [InlineData("ToggleChat", "Social")]
    [InlineData("ToggleControls", "Interface")]
    [InlineData("ToggleCommand", "Command view")]
    [InlineData("Boost", "Flight")]
    public void ActionsSitInTheTabAPlayerWouldLookIn(string name, string tab) =>
        Assert.Equal(tab, Shipped().TabOf(Enum.Parse<InputAction>(name)));

    [Fact]
    public void HaltsLabelSaysItCancelsTheJumpAndTheEditorKeyDescribesNewShipAndEditingTheDrone()
    {
        var catalog = Shipped();
        Assert.Contains("jump", catalog.LabelOf(InputAction.Halt), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(catalog.Rows, r => r.Category == "Ship editor" && r.Action.StartsWith("New ship") && r.Binding.StartsWith("B opens New ship") && r.Binding.Contains("Ctrl"));
    }

    [Fact]
    public void TheFileMustNameEveryActionAndAxisExactlyOnceInAKnownTab()
    {
        var map = ActionMap.Load(Bindings);
        var text = File.ReadAllText(Catalogue);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text.Replace("\"action\": \"ScanSector\"", "\"action\": \"Halt\""));
            Assert.Contains("listed twice", Assert.Throws<InvalidDataException>(() => new ControlsCatalog(path, map)).Message);
            File.WriteAllText(path, text.Replace("\"tab\": \"Flight\", \"action\": \"Boost\"", "\"tab\": \"Nowhere\", \"action\": \"Boost\""));
            Assert.Contains("unknown tab", Assert.Throws<InvalidDataException>(() => new ControlsCatalog(path, map)).Message);
            var lines = File.ReadAllLines(Catalogue).Where(l => !l.Contains("\"action\": \"MapTilt\"")).ToArray();
            File.WriteAllLines(path, lines);
            Assert.Contains("MapTilt", Assert.Throws<InvalidDataException>(() => new ControlsCatalog(path, map)).Message);
            File.WriteAllText(path, text.Replace("\"scope\": [\"Pilot\"] },\n    { \"tab\": \"Flight\", \"action\": \"ToggleDampeners\"", "\"scope\": [\"Sideways\"] },\n    { \"tab\": \"Flight\", \"action\": \"ToggleDampeners\""));
            Assert.Throws<InvalidDataException>(() => new ControlsCatalog(path, map));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NoShippedControlClashesWithAnotherInAViewBothAreReadIn()
    {
        var map = ActionMap.Load(Bindings);
        var remapper = new ControlRemapper(map, new ControlsCatalog(Catalogue, map), new ClientSettings());
        foreach (var slot in ControlRemapper.AllSlots())
            for (var i = 0; i < remapper.Count(slot); i++)
                Assert.Empty(remapper.ConflictsOf(slot, remapper.Get(slot, i)));
    }

    [Fact]
    public void FixedTextFollowsTheLiveBindings()
    {
        var map = ActionMap.Load(Bindings);
        var catalog = new ControlsCatalog(Catalogue, map);
        Assert.Contains(catalog.Rows, r => r.Action.StartsWith("Show cursor") && r.Binding.StartsWith("Hold LeftShift or RightShift"));
        map.SetBindings(InputAction.ReleasePointer, [new InputBinding(InputBinding.Source.Key, (int)GlfwKey.LeftAlt)]);
        catalog.Refresh();
        Assert.Contains(catalog.Rows, r => r.Action.StartsWith("Show cursor") && r.Binding.StartsWith("Hold LeftAlt;"));
        map.SetBindings(InputAction.ReleasePointer, []);
        catalog.Refresh();
        Assert.Contains(catalog.Rows, r => r.Action.StartsWith("Show cursor") && r.Binding.StartsWith("Hold (unbound)"));
    }

    [Fact]
    public void SearchGreysTabsWithNoMatchCountsTheOthersAndLightsTheMatchingRows()
    {
        var catalog = Shipped();
        var browser = new ControlsBrowser(catalog);
        Assert.False(browser.Searching);
        Assert.All(Enumerable.Range(0, catalog.Tabs.Length), t => Assert.True(browser.TabActive(t)));

        browser.SetQuery("  scan ");
        Assert.True(browser.Searching);
        Assert.Equal("scan", browser.Query);
        var scanTab = Array.FindIndex(catalog.Tabs, t => t.Title == "Scanning & exploration");
        var flightTab = Array.FindIndex(catalog.Tabs, t => t.Title == "Flight");
        Assert.True(browser.Counts[scanTab] >= 2);
        Assert.Equal(catalog.Tabs[scanTab].Rows.Count(r => r.Matches("scan")), browser.Counts[scanTab]);
        Assert.False(browser.TabActive(flightTab));
        Assert.Equal(0, browser.Counts[flightTab]);
        Assert.True(browser.TabActive(scanTab));

        // The open tab had nothing, so the first tab with matches opened; a greyed tab can still be opened by hand.
        Assert.True(browser.Counts[browser.Selected] > 0);
        browser.Select(flightTab);
        Assert.Equal(flightTab, browser.Selected);
        Assert.All(catalog.Tabs[flightTab].Rows, row => Assert.False(browser.Highlighted(row)));
        Assert.Contains(catalog.Tabs[scanTab].Rows, row => browser.Highlighted(row) && row.Kind == RowKind.Action);
        Assert.Equal(browser.Counts.Sum(), browser.TotalMatches);

        // A key name finds the row bound to it, in whichever tab.
        browser.SetQuery("pagedown");
        Assert.Equal(1, browser.Counts[Array.FindIndex(catalog.Tabs, t => t.Title == "Galaxy map")]);
        Assert.Equal(1, browser.TotalMatches);

        browser.SetQuery("no-such-thing");
        Assert.Equal(0, browser.TotalMatches);
        Assert.All(Enumerable.Range(0, catalog.Tabs.Length), t => Assert.False(browser.TabActive(t)));

        // Clearing restores the normal view: every tab active, no row lit.
        browser.SetQuery("");
        Assert.False(browser.Searching);
        Assert.All(Enumerable.Range(0, catalog.Tabs.Length), t => Assert.True(browser.TabActive(t)));
        Assert.All(catalog.Rows, row => Assert.False(browser.Highlighted(row)));
        Assert.Equal(0, browser.TotalMatches);
    }

    [Fact]
    public void SearchFollowsARemappedKey()
    {
        var map = ActionMap.Load(Bindings);
        var catalog = new ControlsCatalog(Catalogue, map);
        var browser = new ControlsBrowser(catalog);
        browser.SetQuery("F7");
        Assert.Equal(0, browser.TotalMatches);
        map.SetBindings(InputAction.MapTilt, [new InputBinding(InputBinding.Source.Key, (int)GlfwKey.F7)]);
        catalog.Refresh();
        browser.Recount();
        Assert.Equal(1, browser.TotalMatches);
        Assert.Equal(1, browser.Counts[Array.FindIndex(catalog.Tabs, t => t.Title == "Galaxy map")]);
    }
}
