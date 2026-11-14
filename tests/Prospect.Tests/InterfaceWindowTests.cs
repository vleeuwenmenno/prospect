using Brutal.GlfwApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Screens;
using Prospect.Client.Settings;
using Prospect.Client.Social;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Interface mode, the interface windows' saved layout and the Charts window's list, none of which need Dear ImGui.</summary>
public sealed class InterfaceWindowTests
{
    private static readonly SectorCoord Home = new(0, 0, 0);

    private static ExplorationView ChartsAround(int radius, int capacity = 4096)
    {
        var view = new ExplorationView(capacity);
        var tick = 100L;
        for (var dy = -radius; dy <= radius; dy++)
            for (var dz = -radius; dz <= radius; dz++)
                for (var dx = -radius; dx <= radius; dx++)
                    view.Charts[view.ChartCount++] = new SectorChart(1, Home.Offset(dx, dy, dz), tick += 7 + (dx * 31 + dz * 17 + dy * 5) % 11);
        return view;
    }

    private static Galaxy TheGalaxy => GameContent.Load().Galaxy;

    [Fact]
    public void TheChartListOrdersByDistanceAgeAndNameAndTurnsEachRound()
    {
        var galaxy = TheGalaxy;
        var view = ChartsAround(1);
        var list = new ChartCatalogue(view.Charts.Length, galaxy.MaxNameLength);
        list.Update(view, galaxy, Home);
        Assert.Equal(27, list.Total);
        Assert.Equal(27, list.Count);

        // Nearest first: the sector the ship is in, then the six that share a face, and so on out to the corners.
        Assert.Equal(Home, view.Charts[list.ChartAt(0)].Sector);
        var previous = -1.0;
        for (var row = 0; row < list.Count; row++)
        {
            var d = ChartCatalogue.MetersTo(galaxy, view.Charts[list.ChartAt(row)].Sector, galaxy.CenterOf(Home));
            Assert.True(d >= previous);
            previous = d;
        }

        // Choosing the order in use turns it round: farthest first.
        list.Choose(ChartSort.Distance);
        list.Update(view, galaxy, Home);
        Assert.True(list.Descending);
        Assert.NotEqual(Home, view.Charts[list.ChartAt(0)].Sector);

        // Newest first, then oldest.
        list.Choose(ChartSort.Age);
        list.Update(view, galaxy, Home);
        Assert.False(list.Descending);
        for (var row = 1; row < list.Count; row++)
            Assert.True(view.Charts[list.ChartAt(row - 1)].RecordedTick >= view.Charts[list.ChartAt(row)].RecordedTick);
        list.Choose(ChartSort.Age);
        list.Update(view, galaxy, Home);
        for (var row = 1; row < list.Count; row++)
            Assert.True(view.Charts[list.ChartAt(row - 1)].RecordedTick <= view.Charts[list.ChartAt(row)].RecordedTick);

        // A to Z.
        list.Choose(ChartSort.Name);
        list.Update(view, galaxy, Home);
        for (var row = 1; row < list.Count; row++)
            Assert.True(list.NameOf(list.ChartAt(row - 1)).CompareTo(list.NameOf(list.ChartAt(row)), StringComparison.OrdinalIgnoreCase) <= 0);
    }

    [Fact]
    public void TheNameFilterKeepsMatchingChartsIgnoringCaseAndTheTotalStays()
    {
        var galaxy = TheGalaxy;
        var view = ChartsAround(1);
        var list = new ChartCatalogue(view.Charts.Length, galaxy.MaxNameLength);
        list.Update(view, galaxy, Home);
        var name = list.NameOf(list.ChartAt(5)).ToString();
        var part = name[1..^1].ToUpperInvariant();

        list.SetFilter(part);
        list.Update(view, galaxy, Home);
        Assert.True(list.Count >= 1 && list.Count <= 27);
        Assert.Equal(27, list.Total);
        for (var row = 0; row < list.Count; row++)
            Assert.Contains(part, list.NameOf(list.ChartAt(row)).ToString(), StringComparison.OrdinalIgnoreCase);

        list.SetFilter("no sector is called this ###");
        list.Update(view, galaxy, Home);
        Assert.Equal(0, list.Count);
        Assert.Equal(27, list.Total);

        list.SetFilter("");
        list.Update(view, galaxy, Home);
        Assert.Equal(27, list.Count);
    }

    [Fact]
    public void TheListRebuildsOnlyWhenWhatItDependsOnChangesAndWorksOutFewDetailsAFrame()
    {
        var galaxy = TheGalaxy;
        var view = ChartsAround(1);
        var list = new ChartCatalogue(view.Charts.Length, galaxy.MaxNameLength);
        list.Update(view, galaxy, Home);
        var builds = list.Rebuilds;
        list.Update(view, galaxy, Home);
        Assert.Equal(builds, list.Rebuilds);
        list.Update(view, galaxy, Home.Offset(1, 0, 0));
        Assert.Equal(builds + 1, list.Rebuilds);

        // A new chart rebuilds too.
        view.Charts[view.ChartCount++] = new SectorChart(1, new SectorCoord(9, 0, 9), 5000);
        list.Update(view, galaxy, Home.Offset(1, 0, 0));
        Assert.Equal(builds + 2, list.Rebuilds);
        Assert.Equal(28, list.Total);

        // Only a few rows' details a frame; the rest wait for the next, and a known row is free.
        var got = 0;
        for (var i = 0; i < 10; i++)
            got += list.TryDetail(i, out _) ? 1 : 0;
        Assert.Equal(ChartCatalogue.DetailsPerFrame, got);
        Assert.True(list.TryDetail(0, out var first));
        Assert.InRange(first.StarClass, 0, galaxy.StarClasses.Length - 1);
        list.Update(view, galaxy, Home.Offset(1, 0, 0));
        Assert.True(list.TryDetail(ChartCatalogue.DetailsPerFrame, out _));
    }

    [Fact]
    public void ARowsDetailMatchesWhatTheMapCardCounts()
    {
        var galaxy = TheGalaxy;
        var view = ChartsAround(2);
        var list = new ChartCatalogue(view.Charts.Length, galaxy.MaxNameLength);
        var checkedAny = false;
        var fields = new ChartField[AsteroidFieldsModule.MaxFieldsInReach];
        var clouds = new GasCloudPlacement[GasCloudModule.MaxPerSector];
        for (var frame = 0; frame < 60; frame++)
        {
            list.Update(view, galaxy, Home);
            for (var row = 0; row < list.Count; row++)
            {
                var index = list.ChartAt(row);
                if (!list.TryDetail(index, out var detail))
                    continue;
                var sector = view.Charts[index].Sector;
                Assert.Equal(view.ChartFields(galaxy, sector, fields), detail.Fields);
                Assert.Equal(galaxy.GasClouds?.Intersecting(galaxy, sector, clouds) ?? 0, detail.Clouds);
                checkedAny = true;
            }
        }

        Assert.True(checkedAny);
    }

    [Fact]
    public void TheListHoldsTheContentLimitOfCharts()
    {
        var galaxy = TheGalaxy;
        var content = GameContent.Load();
        var capacity = content.Exploration.ChartsPerOwner;
        var view = new ExplorationView(capacity);
        for (var i = 0; i < capacity; i++)
            view.Charts[i] = new SectorChart(1, new SectorCoord(i % 64, i / 1024, i / 64 % 16), i);
        view.ChartCount = capacity;
        var list = new ChartCatalogue(capacity, galaxy.MaxNameLength);
        list.Update(view, galaxy, Home);
        Assert.Equal(capacity, list.Total);
        Assert.Equal(capacity, list.Count);
    }

    [Fact]
    public void TheInterfaceKeyLatchesOnAndOffAndIsLeftToATextField()
    {
        var actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        var devices = new InputDevices();
        var mode = new InterfaceMode();
        Assert.Contains(actions.Bindings(InputAction.ToggleInterface).ToArray(), b => b.DisplayName == "Tab");
        Assert.DoesNotContain(actions.Bindings(InputAction.TogglePlayers).ToArray(), b => b.DisplayName == "Tab");

        devices.BeginFrame();
        devices.OnKey(GlfwKey.Tab, GlfwKeyAction.Press);
        Assert.False(mode.HandleKey(actions, devices, true, false));
        Assert.False(mode.Active);
        Assert.True(mode.HandleKey(actions, devices, false, false));
        Assert.True(mode.Active);
        Assert.True(mode.Shows(true, false));
        Assert.False(mode.Shows(false, true));

        devices.BeginFrame();
        Assert.False(mode.HandleKey(actions, devices, false, false));
        Assert.True(mode.Active);
        devices.OnKey(GlfwKey.Tab, GlfwKeyAction.Release);
        devices.BeginFrame();
        devices.OnKey(GlfwKey.Tab, GlfwKeyAction.Press);
        Assert.True(mode.HandleKey(actions, devices, false, false));
        Assert.False(mode.Active);
        Assert.False(mode.Shows(true, false));
        Assert.True(mode.Shows(true, true));
    }

    [Fact]
    public void TheNewWindowsStartOpenAndShowOnlyInAGameInInterfaceModeOrPinned()
    {
        var panes = new SocialPanes();
        var detail = new SocialDetail();
        foreach (var pane in new[] { SocialPane.Charts, SocialPane.Inventory, SocialPane.Player })
        {
            Assert.True(panes[pane].Open);
            Assert.False(panes.Shown(pane, false, true, false, detail));
            Assert.True(panes.Shown(pane, true, true, false, detail));
            Assert.False(panes.Shown(pane, true, false, false, detail));
            panes[pane].Pinned = true;
            Assert.True(panes.Shown(pane, false, true, false, detail));
            Assert.False(panes.Shown(pane, false, false, false, detail));
            Assert.Equal(PaneClose.Closed, panes.Close(pane, false));
            Assert.False(panes[pane].Open || panes[pane].Pinned);
        }
    }

    [Fact]
    public void WindowPinsOpenStatesAndPlacesRoundTripThroughASettingsFileUnscaled()
    {
        var path = Path.GetTempFileName();
        try
        {
            var settings = new ClientSettings();
            var panes = new SocialPanes();
            var saves = 0;
            var layout = new WindowLayout(settings, panes, () => saves++);
            layout.Restore(2f);

            panes[SocialPane.Charts].Pinned = true;
            panes[SocialPane.Charts].Placed = true;
            panes[SocialPane.Charts].Min = new float2(600, 240);
            panes[SocialPane.Inventory].Close();
            panes[SocialPane.Party].Open = true;

            // A held drag is not saved; its release is, once.
            Assert.False(layout.Track(2f, true));
            Assert.Equal(0, saves);
            Assert.True(layout.Track(2f, false));
            Assert.Equal(1, saves);
            Assert.False(layout.Track(2f, false));
            Assert.Equal(1, saves);

            settings.Save(path);
            var loaded = ClientSettings.Load(path);
            var again = new SocialPanes();
            new WindowLayout(loaded, again, () => { }).Restore(2f);
            Assert.True(again[SocialPane.Charts].Open && again[SocialPane.Charts].Pinned && again[SocialPane.Charts].Placed);
            Assert.Equal(new float2(600, 240), again[SocialPane.Charts].Min);
            Assert.False(again[SocialPane.Inventory].Open);
            Assert.True(again[SocialPane.Party].Open);
            Assert.True(again[SocialPane.Player].Open);

            // Saved unscaled: at another interface scale the same window lands in proportion.
            var half = new SocialPanes();
            new WindowLayout(loaded, half, () => { }).Restore(1f);
            Assert.Equal(new float2(300, 120), half[SocialPane.Charts].Min);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AHandEditedSettingsFileCannotGrowTheWindowListOrThrowAWindowAway()
    {
        var path = Path.GetTempFileName();
        try
        {
            var entries = string.Join(",", Enumerable.Range(0, 40).Select(i => $"\"W{i}\":{{\"Open\":true,\"Pinned\":true,\"Placed\":true,\"X\":1e30,\"Y\":-1e30}}"));
            File.WriteAllText(path, $"{{\"Windows\":{{{entries},\"\":{{}},\"Charts\":{{\"Open\":true,\"Pinned\":false,\"Placed\":true,\"X\":999999,\"Y\":5}}}}}}");
            var loaded = ClientSettings.Load(path);
            Assert.True(loaded.Windows.Count <= ClientSettings.MaxWindows);
            Assert.All(loaded.Windows.Values, w => Assert.InRange(w.X, -ClientSettings.MaxWindowCoordinate, ClientSettings.MaxWindowCoordinate));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
