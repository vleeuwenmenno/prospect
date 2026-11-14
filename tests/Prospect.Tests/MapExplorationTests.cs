using Brutal.Numerics;
using Prospect.Client.Screens;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>The map's exploration helpers that need no Dear ImGui: charts, coverage, projection, bounds, scan readout.</summary>
public sealed class MapExplorationTests
{
    [Fact]
    public void TheChartIndexFollowsTheSnapshotsChartsIncludingAnotherWorldWithTheSameCount()
    {
        var view = new ExplorationView(16);
        var index = new ChartIndex(16);
        index.Refresh(view);
        Assert.False(index.Contains(new SectorCoord(1, 0, 2)));

        view.Charts[0] = new SectorChart(1, new SectorCoord(1, 0, 2), 10);
        view.Charts[1] = new SectorChart(1, new SectorCoord(-5, 3, 7), 11);
        view.ChartCount = 2;
        index.Refresh(view);
        Assert.True(index.Contains(new SectorCoord(1, 0, 2)));
        Assert.True(index.Contains(new SectorCoord(-5, 3, 7)));
        Assert.False(index.Contains(new SectorCoord(1, 1, 2)));
        Assert.Equal(2, index.Count);

        view.Charts[1] = new SectorChart(1, new SectorCoord(9, 0, 9), 12);
        index.Refresh(view);
        Assert.False(index.Contains(new SectorCoord(-5, 3, 7)));
        Assert.True(index.Contains(new SectorCoord(9, 0, 9)));
    }

    [Fact]
    public void TheChartIndexHoldsTheContentLimitWithoutLosingOne()
    {
        var content = GameContent.Load();
        var capacity = content.Exploration.ChartsPerOwner;
        var view = new ExplorationView(capacity);
        for (var i = 0; i < capacity; i++)
            view.Charts[i] = new SectorChart(1, new SectorCoord(i % 64, i / 1024, i / 64 % 16), i);
        view.ChartCount = capacity;
        var index = new ChartIndex(capacity);
        index.Refresh(view);
        for (var i = 0; i < capacity; i++)
            Assert.True(index.Contains(view.Charts[i].Sector));
        Assert.False(index.Contains(new SectorCoord(1000, 0, 0)));
    }

    [Fact]
    public void EquipmentCountsOnlyBlocksStillThere()
    {
        var content = GameContent.Load();
        var design = content.Designs.First(d => d.Types.Any(t => t.Sensor) && d.Types.Any(t => t.Charting));
        SensorCoverage.Equipment(design, ReadOnlySpan<ulong>.Empty, out var sensor, out var charting);
        Assert.True(sensor);
        Assert.True(charting);

        var alive = new ulong[BlockSet.WordCount(design.BlockCount)];
        for (var b = 0; b < design.BlockCount; b++)
            if (!design.Types[b].Charting)
                alive[b >> 6] |= 1UL << (b & 63);
        SensorCoverage.Equipment(design, alive, out sensor, out charting);
        Assert.True(sensor);
        Assert.False(charting);
    }

    [Fact]
    public void TheProjectionPicksTheSameSectorItDrawsFlatAndTilted()
    {
        foreach (var tilt in new[] { 0f, 0.5f, 1.0f })
        {
            var view = new MapProjection(new float2(640, 360), new float2(3.2f, -7.6f), 44f, 2, tilt, 2.2f);
            for (var x = -2; x <= 8; x++)
                for (var z = -12; z <= -3; z++)
                    Assert.Equal(new SectorCoord(x, 2, z), view.SectorAt(view.ToScreen(x, z) + new float2(10, 5)));
        }

        // Tilted, a hull a layer up is drawn above its foot on the chosen layer; flat, they coincide.
        var tilted = new MapProjection(new float2(640, 360), float2.Zero, 44f, 0, 1f, 2f);
        Assert.True(tilted.ToScreen(0, 1, 0).Y < tilted.ToScreen(0, 0, 0).Y);
        var flatView = new MapProjection(new float2(640, 360), float2.Zero, 44f, 0, 0f, 2f);
        Assert.Equal(flatView.ToScreen(0, 0, 0), flatView.ToScreen(0, 1, 0));
    }

    [Fact]
    public void TheBoundsAreWhereTheDiscHoldsMatter()
    {
        var shape = GameContent.Load().Galaxy.Shape;
        MapBounds.Layers(shape, 0.01f, out var low, out var high);
        Assert.True(low < shape.Center.Y && high > shape.Center.Y);
        Assert.True(shape.LayerFalloff(high) >= 0.01f);
        Assert.True(shape.LayerFalloff(high + 1) < 0.01f);
        var center = new SectorCoord((int)shape.Center.X, (int)shape.Center.Y, (int)shape.Center.Z);
        Assert.True(MapBounds.Contains(shape, 0.01f, center));
        Assert.False(MapBounds.Contains(shape, 0.01f, center with { Y = high + 1 }));
        Assert.False(MapBounds.Contains(shape, 0.01f, center with { X = center.X + (int)MapBounds.EdgeRadius(shape) + 1 }));
        Assert.Equal(0f, shape.Density(center.X + MapBounds.EdgeRadius(shape) + 1, center.Z));
    }

    [Fact]
    public void TheCardPinsReplacesAndDismissesAsDecided()
    {
        var card = new MapCardState();
        var a = new SectorCoord(1, 0, 1);
        var b = new SectorCoord(2, 0, 1);
        Assert.True(card.Shown(true, a, out var shown, out var preview));
        Assert.True(preview);
        Assert.False(card.Shown(false, a, out _, out _));

        card.Click(a, true);
        card.Click(b, true);
        Assert.True(card.Shown(true, a, out shown, out preview));
        Assert.Equal(b, shown);
        Assert.False(preview);

        card.Click(a, false);
        Assert.False(card.Pinned);
        card.Pin(a);
        Assert.True(card.Dismiss());
        Assert.False(card.Dismiss());
    }

    [Fact]
    public void ACancelledScanIsExplainedByWhatChangedSinceItStarted()
    {
        var here = new SectorCoord(1, 0, 1);
        ScanStop Cancel(uint hull, SectorCoord sector, bool canChart)
        {
            var tracker = new ScanTracker();
            tracker.Update(new ScanView { State = ScanState.Scanning, Sector = here, StartedTick = 0, CompletesTick = 300 }, 7, here, true, 0.1f);
            tracker.Update(new ScanView { State = ScanState.Cancelled, Sector = here }, hull, sector, canChart, 0.1f);
            Assert.Equal(0f, tracker.SinceResult);
            return tracker.Stop;
        }

        Assert.Equal(ScanStop.LeftSector, Cancel(7, here.Offset(1, 0, 0), true));
        Assert.Equal(ScanStop.ControlChanged, Cancel(8, here, true));
        Assert.Equal(ScanStop.ChartingLost, Cancel(7, here, false));
        Assert.Equal(ScanStop.Unknown, Cancel(7, here, true));

        var scan = new ScanView { State = ScanState.Scanning, StartedTick = 100, CompletesTick = 400 };
        Assert.Equal(0.5f, ScanTracker.Progress(scan, 250));
        Assert.Equal(1f, ScanTracker.Progress(scan, 900));
        Assert.Equal(0f, ScanTracker.Progress(scan, 50));
    }

    [Fact]
    public void ASectorNameWrittenWithoutAllocatingIsTheSameName()
    {
        var galaxy = GameContent.Load().Galaxy;
        Span<char> name = stackalloc char[galaxy.MaxNameLength];
        for (var x = -20; x <= 20; x += 3)
            for (var z = -20; z <= 20; z += 5)
            {
                var coord = new SectorCoord(x, x % 3, z);
                var text = galaxy.NameOf(coord);
                Assert.Equal(text, new string(name[..galaxy.NameOf(coord, name)]));
                Assert.Matches("^[A-Z][a-z]+ [1-9][0-9][0-9]$", text);
            }
    }

    [Fact]
    public void TheShippedMapTuningLoadsAndBadFiguresAreRefused()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "content", "visuals", "map.json");
        var tuning = MapTuning.Load(path);
        Assert.InRange(tuning.FogAlpha, 0.01f, 1f);
        Assert.InRange(tuning.CardListRows, 2, 32);

        var bad = Path.GetTempFileName();
        try
        {
            File.WriteAllText(bad, File.ReadAllText(path).Replace("\"tiltDegrees\": 58", "\"tiltDegrees\": 95"));
            Assert.Throws<InvalidDataException>(() => MapTuning.Load(bad));
        }
        finally
        {
            File.Delete(bad);
        }
    }

    [Fact]
    public void EveryStarClassHasItsOwnIconShapeAndTheFeatureColoursAreDistinct()
    {
        var look = MapTuning.Load();
        var galaxy = GameContent.Load().Galaxy;
        var shapes = new HashSet<StarIconShape>();
        foreach (var star in galaxy.StarClasses)
        {
            Assert.Contains(look.StarIcons, icon => icon.Star == star.Name);
            Assert.True(shapes.Add(look.ShapeOf(star.Name)), $"{star.Name} shares its shape");
        }

        Assert.Equal(StarIconShape.Circle, look.ShapeOf("A star the file does not name"));
        Assert.NotEqual(look.CloudRgb, look.FieldRgb);
        Assert.NotEqual(Theme.Info, look.CloudRgb);
    }

    [Fact]
    public void TheMapFileRefusesAFeatureColourThatIsNotSixHexDigits()
    {
        var folder = Directory.CreateTempSubdirectory("map-tuning");
        try
        {
            var path = Path.Combine(folder.FullName, "map.json");
            File.WriteAllText(path, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "map.json")).Replace("\"cloudColor\": \"B78CF0\"", "\"cloudColor\": \"purple\""));
            Assert.Throws<InvalidDataException>(() => MapTuning.Load(path));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public void TheLegendSitsUnderTheUnitListAndMovesBesideItWhereThatIsTooShort()
    {
        const int rows = MapLegend.FeatureRows + 5;
        var display = new float2(1920, 1080);
        var few = MapUnitList.Layout(display, 1f, 3, 140f, 400f);
        var under = MapLegend.Layout(display, 1f, few, rows, 20f);
        Assert.Equal(rows, under.Rows);
        Assert.Equal(few.Min.X, under.Min.X);
        Assert.True(under.Min.Y >= few.Min.Y + few.Size.Y);
        Assert.True(under.Min.Y + under.Size.Y <= display.Y);

        // A unit list that reaches the bottom leaves no room under it: the legend goes beside it, level with its top.
        var many = MapUnitList.Layout(display, 1f, 60, 140f, 400f);
        var beside = MapLegend.Layout(display, 1f, many, rows, 20f);
        Assert.True(beside.Shown);
        Assert.Equal(many.Min.Y, beside.Min.Y);
        Assert.True(beside.Min.X >= many.Min.X + many.Size.X);

        // On a short screen the star types are the first rows to go, the feature rows the last.
        var short_ = new float2(1920, 520);
        var shortUnits = MapUnitList.Layout(short_, 1f, 3, 140f, 400f);
        var shortLegend = MapLegend.Layout(short_, 1f, shortUnits, rows, 20f);
        Assert.InRange(shortLegend.Rows, 0, rows - 1);
        Assert.True(!shortLegend.Shown || shortLegend.Min.Y + shortLegend.Size.Y <= short_.Y);

        // A twice-scaled interface keeps the same promise.
        var big = MapUnitList.Layout(display, 2f, 3, 280f, 800f);
        var bigLegend = MapLegend.Layout(display, 2f, big, rows, 20f);
        Assert.True(!bigLegend.Shown || bigLegend.Min.Y + bigLegend.Size.Y <= display.Y);
    }
}
