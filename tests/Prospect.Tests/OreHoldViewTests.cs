using Prospect.Client.Screens;
using Prospect.Client.Ui;
using Prospect.Engine.IO;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class OreHoldViewTests
{
    private static readonly GameContent Content = GameContent.Load(Path.Combine(AppContext.BaseDirectory, "content"));

    private static byte DesignWithBins()
    {
        for (byte i = 0; i < Content.Designs.Length; i++)
            if (Content.Designs[i].Bins.Length >= 4)
                return i;
        throw new InvalidOperationException("No design with ore bins found.");
    }

    private static byte DesignWithoutBins()
    {
        for (byte i = 0; i < Content.Designs.Length; i++)
            if (Content.Designs[i].Bins.Length == 0)
                return i;
        throw new InvalidOperationException("No design without ore bins found.");
    }

    private static SessionSnapshot Snapshot(byte design, OreBin[] ore)
    {
        var snapshot = new SessionSnapshot(Content)
        {
            Galaxy = Content.Galaxy.WithSeed(17),
            State = SessionState.Active,
            PlayerId = 1,
            OwnHull = 0,
            HullCount = 1,
        };
        snapshot.Hulls[0] = new HullView
        {
            Id = 10,
            Owner = 1,
            Design = design,
            IsOwn = true,
            Stats = new HullStats(),
            Ore = ore,
        };
        return snapshot;
    }

    private static OreBin Bin(double kg, int resource, float grade) => new() { Kg = kg, Resource = resource, Grade = grade };

    [Fact]
    public void TotalsAddEachOresKilogramsWeightGradeByMassAndCountItsBins()
    {
        var design = DesignWithBins();
        var bins = Content.Designs[design].Bins;
        var ore = new[] { Bin(100, 1, 0.5f), Bin(300, 0, 0.8f), Bin(100, 1, 0.9f), OreBin.Empty };
        var view = new OreHoldView();
        view.Update(Snapshot(design, ore));

        Assert.True(view.HasHull);
        Assert.Equal(bins.Length, view.BinCount);
        Assert.Equal(3, view.BinsUsed);
        Assert.Equal(500.0, view.HeldKg);
        Assert.Equal(ShipSheet.OreCapacity(Content.Designs[design]), view.CapacityKg);

        // Ores come in the order the bins first hold them: resource 1, then resource 0.
        var totals = view.Totals;
        Assert.Equal(2, totals.Length);
        Assert.Equal(1, totals[0].Resource);
        Assert.Equal(200.0, totals[0].Kg);
        Assert.Equal(2, totals[0].Bins);
        Assert.Equal((100 * 0.5 + 100 * 0.9) / 200.0, totals[0].Grade, 5);
        Assert.Equal(0, totals[1].Resource);
        Assert.Equal(300.0, totals[1].Kg);
        Assert.Equal(1, totals[1].Bins);
        Assert.Equal(0.8, totals[1].Grade, 5);
    }

    [Fact]
    public void EmptyUntoldAndUnknownBinsReadAsEmptyTiles()
    {
        var design = DesignWithBins();
        var resources = Content.Galaxy.Resources.Length;
        // Only two bins told; the first holds an ore this galaxy lacks, the second is full of a known one.
        var ore = new[] { Bin(50, resources + 3, 0.5f), Bin(900, 0, 0.6f) };
        var view = new OreHoldView();
        view.Update(Snapshot(design, ore));

        var tiles = view.Tiles;
        Assert.Equal(Content.Designs[design].Bins.Length, tiles.Length);
        Assert.True(tiles[0].IsEmpty);
        Assert.False(tiles[1].IsEmpty);
        Assert.True(tiles[2].IsEmpty);
        Assert.True(tiles[3].IsEmpty);
        Assert.Equal(1, view.BinsUsed);
        Assert.Single(view.Totals.ToArray());
        Assert.Equal(Content.Designs[design].Bins[1].Capacity, tiles[1].Capacity);
        Assert.Equal((float)Math.Min(1.0, 900 / tiles[1].Capacity), tiles[1].Fill, 5);
    }

    [Fact]
    public void AHullWithNoBinsHasNothingToShowAndNoShipHasNoHull()
    {
        var view = new OreHoldView();
        view.Update(Snapshot(DesignWithoutBins(), []));
        Assert.True(view.HasHull);
        Assert.Equal(0, view.BinCount);
        Assert.Equal(0, view.Layout(300f, 1f, CargoWindowLook.Current));
        Assert.Empty(view.Tiles.ToArray());

        var none = Snapshot(DesignWithBins(), []);
        none.OwnHull = -1;
        view.Update(none);
        Assert.False(view.HasHull);
        Assert.Equal(0, view.BinCount);
    }

    [Fact]
    public void TilesWrapAtTheGridWidthAtEveryScale()
    {
        var design = DesignWithBins();
        var look = CargoWindowLook.Current;
        var view = new OreHoldView();
        view.Update(Snapshot(design, [Bin(100, 0, 0.5f)]));

        foreach (var scale in new[] { 1f, 2f })
        {
            var tileW = MathF.Round(look.OreTileWidth * scale);
            var tileH = MathF.Round(look.OreTileHeight * scale);
            var gap = MathF.Round(Theme.Space2 * scale);

            // Room for exactly two tiles a row: four bins fill two rows.
            var height = view.Layout(tileW * 2 + gap, scale, look);
            Assert.Equal(tileH * 2 + gap, height);
            var tiles = view.Tiles;
            Assert.Equal(0f, tiles[0].X);
            Assert.Equal(tileW + gap, tiles[1].X);
            Assert.Equal(0f, tiles[2].X);
            Assert.Equal(tileH + gap, tiles[2].Y);
            foreach (var tile in tiles)
                Assert.True(tile.X + tile.Width <= tileW * 2 + gap + 0.001f);

            // A narrow window still puts one tile on a row rather than none.
            Assert.Equal(tileH * 4 + gap * 3, view.Layout(10f, scale, look));

            // A wide one holds all four on a row.
            Assert.Equal(tileH, view.Layout(tileW * 4 + gap * 3, scale, look));
        }
    }

    [Fact]
    public void TheSavedTabNamesAKnownTabAndTheStripSharesANarrowWidth()
    {
        Assert.Equal(CargoTab.Containers, OreHoldView.TabOf(0));
        Assert.Equal(CargoTab.Ore, OreHoldView.TabOf(1));
        Assert.Equal(CargoTab.Containers, OreHoldView.TabOf(5));
        Assert.Equal(CargoTab.Containers, OreHoldView.TabOf(-1));

        var look = CargoWindowLook.Current;
        var first = OreHoldView.TabRect(CargoTab.Containers, 1000f, 1f, look);
        var second = OreHoldView.TabRect(CargoTab.Ore, 1000f, 1f, look);
        Assert.Equal(0f, first.X);
        Assert.True(second.X >= first.Width);
        Assert.Equal(look.TabWidth, first.Width);

        // A strip narrower than two tabs shrinks them to fit, never overlapping.
        var narrowFirst = OreHoldView.TabRect(CargoTab.Containers, 150f, 1f, look);
        var narrowSecond = OreHoldView.TabRect(CargoTab.Ore, 150f, 1f, look);
        Assert.True(narrowSecond.X >= narrowFirst.Width);
        Assert.True(narrowSecond.X + narrowSecond.Width <= 150f);
    }

    [Fact]
    public void UpdatingAndLayingOutTakeNoMemory()
    {
        var design = DesignWithBins();
        var snapshot = Snapshot(design, [Bin(100, 1, 0.5f), Bin(300, 0, 0.8f), Bin(100, 1, 0.9f), Bin(5, 2, 0.1f)]);
        var view = new OreHoldView();
        var look = CargoWindowLook.Current;
        view.Update(snapshot);
        view.Layout(300f, 1f, look);

        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            view.Update(snapshot);
            view.Layout(300f, 1f, look);
            _ = OreHoldView.TabRect(CargoTab.Ore, 400f, 1f, look);
        }

        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void TheCargoWindowLookLoadsAndRefusesWhatIsOutOfRange()
    {
        var path = AppPaths.Content("visuals", "cargo_window.json");
        var shipped = CargoWindowLook.Load(path);
        Assert.Equal(shipped.OreTileWidth, CargoWindowLook.Current.OreTileWidth);

        var text = File.ReadAllText(path);
        var scratch = Path.Combine(Path.GetTempPath(), "cargo-window-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            foreach (var (from, to) in new[]
            {
                ("\"tabWidth\": 112", "\"tabWidth\": 5"),
                ("\"oreTileHeight\": 56", "\"oreTileHeight\": 400"),
                ("\"oreBarHeight\": 4", "\"oreBarHeight\": 30"),
                ("\"oreEmptyAlpha\": 0.45", "\"oreEmptyAlpha\": 2"),
            })
            {
                Assert.Contains(from, text);
                File.WriteAllText(scratch, text.Replace(from, to));
                Assert.Throws<InvalidDataException>(() => CargoWindowLook.Load(scratch));
            }
        }
        finally
        {
            File.Delete(scratch);
        }
    }
}
