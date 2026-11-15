using System.Numerics;
using System.Text;
using Brutal.Numerics;
using Prospect.Client.Screens;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The fuel row's cells and per-fuel details, and the fuel callout's state, grouping and label placement, all
/// without fonts.
/// </summary>
public sealed class FuelCalloutTests
{
    private static readonly FlightConsoleLayout Layout = FlightConsole.Layout(new float2(1280f, 720f), 1f);

    private static FuelFlow Fuel(float nitrogen, float hydrazine, float bipropellant) => new()
    {
        Amount = new FuelTanks { Nitrogen = nitrogen * 0.5f, Hydrazine = hydrazine * 0.5f, Bipropellant = bipropellant * 0.5f },
        Capacity = new FuelTanks { Nitrogen = nitrogen, Hydrazine = hydrazine, Bipropellant = bipropellant },
        Burn = new FuelTanks { Nitrogen = 0.2f },
    };

    private static float2 Middle(in HudRect rect) => new((rect.Left + rect.Right) * 0.5f, (rect.Top + rect.Bottom) * 0.5f);

    [Fact]
    public void EachFuelHasItsOwnCellAcrossTheRowAndTheGapsBelongToNone()
    {
        var fuel = Fuel(100f, 0f, 400f);
        var row = FlightConsole.BarRect(Layout, ConsoleBar.Fuel);
        var first = ConsoleFuel.CellRect(Layout, fuel, 0);
        var second = ConsoleFuel.CellRect(Layout, fuel, 1);
        Assert.Equal(row.Left, first.Left);
        Assert.True(second.Right <= row.Right && second.Right > row.Right - 2f);
        Assert.Equal(Layout.RowGap, second.Left - first.Right);
        Assert.Equal(FuelKind.Nitrogen, ConsoleFuel.CellAt(Layout, fuel, Middle(first)));
        Assert.Equal(FuelKind.Bipropellant, ConsoleFuel.CellAt(Layout, fuel, Middle(second)));
        Assert.Equal(FuelKind.None, ConsoleFuel.CellAt(Layout, fuel, new float2((first.Right + second.Left) * 0.5f, Middle(first).Y)));
        Assert.Equal(FuelKind.None, ConsoleFuel.CellAt(Layout, fuel, new float2(Middle(first).X, row.Top - 4f)));
        Assert.Equal(0, ConsoleFuel.CellIndex(fuel, FuelKind.Nitrogen));
        Assert.Equal(1, ConsoleFuel.CellIndex(fuel, FuelKind.Bipropellant));
        Assert.Equal(-1, ConsoleFuel.CellIndex(fuel, FuelKind.Hydrazine));

        // A hull with no tank has one cell, and it is no fuel's.
        var none = Fuel(0f, 0f, 0f);
        Assert.Equal(FuelKind.None, ConsoleFuel.CellAt(Layout, none, Middle(ConsoleFuel.CellRect(Layout, none, 0))));
    }

    [Fact]
    public void AClickOpensSwitchesAndClosesAndOnlyAShownPointerOverNoWindowClicks()
    {
        var fuel = Fuel(100f, 200f, 400f);
        var callout = new FuelCallout();
        callout.Follow(7, fuel);
        var nitrogen = Middle(ConsoleFuel.CellRect(Layout, fuel, 0));
        var biprop = Middle(ConsoleFuel.CellRect(Layout, fuel, 2));

        Assert.Equal(FuelKind.None, callout.Point(Layout, fuel, nitrogen, false, false, true));
        Assert.Equal(FuelKind.None, callout.Point(Layout, fuel, nitrogen, true, true, true));
        Assert.False(callout.IsOpen);

        Assert.Equal(FuelKind.Nitrogen, callout.Point(Layout, fuel, nitrogen, true, false, false));
        Assert.False(callout.IsOpen);
        callout.Point(Layout, fuel, nitrogen, true, false, true);
        Assert.Equal(FuelKind.Nitrogen, callout.Open);
        callout.Point(Layout, fuel, biprop, true, false, true);
        Assert.Equal(FuelKind.Bipropellant, callout.Open);
        callout.Point(Layout, fuel, biprop, true, false, true);
        Assert.False(callout.IsOpen);

        // It belongs to its hull and to a fuel the hull still has.
        callout.Point(Layout, fuel, biprop, true, false, true);
        callout.Follow(7, fuel);
        Assert.Equal(FuelKind.Bipropellant, callout.Open);
        callout.Follow(7, Fuel(100f, 200f, 0f));
        Assert.False(callout.IsOpen);
        callout.Toggle(FuelKind.Nitrogen);
        callout.Follow(8, fuel);
        Assert.False(callout.IsOpen);
    }

    private static CalloutTank Tank(float x, float y, float z, float kilograms, bool lost = false) => new()
    {
        Local = new Vector3(x, y, z),
        Screen = new float2(640f + x * 10f, 300f - y * 10f),
        OnScreen = true,
        Kilograms = kilograms,
        Capacity = 1000f,
        Lost = lost,
    };

    [Fact]
    public void UpToTheLimitEachTankHasItsLabelAndBeyondItNearTanksShareOne()
    {
        var groups = new int[TankFuel.MaxTanks];
        var labels = new CalloutLabel[FuelCalloutLayout.MaxLabels];
        CalloutTank[] few = [Tank(-4, 0, 0, 340f), Tank(4, 0, 0, 1000f), Tank(0, 2, 0, 0f, lost: true)];
        Assert.Equal(3, FuelCalloutLayout.Group(few, 8, groups, labels));
        Assert.Equal([0, 1, 2], groups[..3]);
        Assert.Equal(340f, labels[0].Kilograms);
        Assert.Equal(1, labels[2].Lost);
        Assert.Equal(new float2(600f, 300f), labels[0].Anchor);

        // Two clusters of ten far apart, two labels: each cluster is one.
        var many = new CalloutTank[20];
        for (var i = 0; i < 10; i++)
        {
            many[i] = Tank(-40 + i * 0.5f, 0, 0, 100f);
            many[10 + i] = Tank(40 + i * 0.5f, 0, 0, 50f, lost: i == 3);
        }

        Assert.Equal(2, FuelCalloutLayout.Group(many, 2, groups, labels));
        Assert.Equal(10, labels[0].Tanks);
        Assert.Equal(10, labels[1].Tanks);
        Assert.Equal(1000f, labels[0].Kilograms);
        Assert.Equal(450f, labels[1].Kilograms);
        Assert.Equal(1, labels[1].Lost);
        Assert.True(labels[0].Anchor.X < 640f && labels[1].Anchor.X > 640f);

        // Eight labels for twenty tanks: every tank counted once, the same answer every time.
        var count = FuelCalloutLayout.Group(many, 8, groups, labels);
        Assert.Equal(8, count);
        var total = 0;
        for (var i = 0; i < count; i++)
            total += labels[i].Tanks;
        Assert.Equal(20, total);
        var again = new int[TankFuel.MaxTanks];
        FuelCalloutLayout.Group(many, 8, again, new CalloutLabel[FuelCalloutLayout.MaxLabels]);
        Assert.Equal(groups[..20], again[..20]);

        // A group with none of its tanks in front of the camera has no label to draw.
        for (var i = 0; i < 10; i++)
            many[i].OnScreen = false;
        FuelCalloutLayout.Group(many, 2, groups, labels);
        Assert.False(labels[0].Visible);
        Assert.True(labels[1].Visible);
    }

    [Fact]
    public void LabelsStandBesideTheHullClearOfEachOtherTheScreenAndTheConsole()
    {
        var groups = new int[TankFuel.MaxTanks];
        var labels = new CalloutLabel[FuelCalloutLayout.MaxLabels];
        var sizes = new float2[FuelCalloutLayout.MaxLabels];
        var tanks = new CalloutTank[30];
        for (var i = 0; i < tanks.Length; i++)
            tanks[i] = Tank((i % 6) - 2.5f, (i / 6) - 2f, 0, 500f);
        var count = FuelCalloutLayout.Group(tanks, 8, groups, labels);
        Assert.Equal(8, count);
        for (var i = 0; i < count; i++)
            sizes[i] = new float2(170f + i * 3f, 20f);

        var bounds = new HudRect(16f, 16f, 1264f, Layout.Top - 12f);
        FuelCalloutLayout.Place(labels, count, sizes, new float2(640f, 300f), bounds, 56f, 4f);
        Assert.False(FuelCalloutLayout.AnyOverlap(labels, count));
        for (var i = 0; i < count; i++)
        {
            var rect = labels[i].Rect;
            Assert.True(rect.Left >= bounds.Left && rect.Right <= bounds.Right && rect.Top >= bounds.Top && rect.Bottom <= bounds.Bottom, $"{i}: {rect}");
            Assert.Equal(sizes[i].X, rect.Width);
            // Each label is on its anchor's side, beyond every anchor on that side.
            if (labels[i].Left)
                Assert.True(rect.Right <= labels[i].Anchor.X);
            else
                Assert.True(rect.Left >= labels[i].Anchor.X);
        }

        // Low on the screen the column climbs above the console; at the left edge everything goes right.
        for (var i = 0; i < tanks.Length; i++)
            tanks[i].Screen = new float2(30f + i, Layout.Top - 2f);
        FuelCalloutLayout.Group(tanks, 8, groups, labels);
        FuelCalloutLayout.Place(labels, count, sizes, new float2(40f, Layout.Top), bounds, 56f, 4f);
        Assert.False(FuelCalloutLayout.AnyOverlap(labels, count));
        for (var i = 0; i < count; i++)
        {
            Assert.False(labels[i].Left);
            Assert.True(labels[i].Rect.Bottom <= bounds.Bottom);
        }
    }

    [Fact]
    public void GroupingAndPlacingTakeNoMemory()
    {
        var groups = new int[TankFuel.MaxTanks];
        var labels = new CalloutLabel[FuelCalloutLayout.MaxLabels];
        var sizes = new float2[FuelCalloutLayout.MaxLabels];
        var tanks = new CalloutTank[TankFuel.MaxTanks];
        for (var i = 0; i < tanks.Length; i++)
        {
            tanks[i] = Tank(i % 17 - 8, i / 17 - 7, i % 3, 10f * i);
            sizes[i % sizes.Length] = new float2(150f, 18f);
        }

        var bounds = new HudRect(16f, 16f, 1264f, 600f);
        FuelCalloutLayout.Place(labels, FuelCalloutLayout.Group(tanks, 8, groups, labels), sizes, new float2(640f, 300f), bounds, 56f, 4f);
        var allocation = new AllocationProbe();
        while (allocation.Measuring())
        {
            var count = FuelCalloutLayout.Group(tanks, 8, groups, labels);
            FuelCalloutLayout.Place(labels, count, sizes, new float2(640f, 300f), bounds, 56f, 4f);
        }

        Assert.Equal(0, allocation.Bytes);
    }

    private struct Recorder(List<string> lines) : IConsoleTipSink
    {
        public void Title(ReadOnlySpan<byte> title, ReadOnlySpan<byte> state, uint stateColor) =>
            lines.Add($"{Encoding.UTF8.GetString(title)}: {Encoding.UTF8.GetString(state)}");

        public void Line(ReadOnlySpan<byte> label, ReadOnlySpan<byte> value, uint valueColor) =>
            lines.Add($"{Encoding.UTF8.GetString(label)}: {Encoding.UTF8.GetString(value)}");

        public void Note(ReadOnlySpan<byte> text, uint color) => lines.Add(Encoding.UTF8.GetString(text));
    }

    private static List<string> Tip(in HullView own, in FuelPick pick)
    {
        UiText.ResetArena();
        var lines = new List<string>();
        var recorder = new Recorder(lines);
        ConsoleTip.Write(ref recorder, ConsoleBar.Fuel, own, default, pick);
        return lines;
    }

    [Fact]
    public void AFuelsDetailsTellOfThatFuelAloneWithItsTanks()
    {
        var own = new HullView { Fuel = Fuel(100f, 0f, 4000f) };
        var nitrogen = Tip(own, new FuelPick(FuelKind.Nitrogen, 2, 0, false));
        Assert.Equal("NITROGEN: OK", nitrogen[0]);
        Assert.Contains("STORED: 50 / 100 KG", nitrogen);
        Assert.Contains("BURN: -0.20 KG/S", nitrogen);
        Assert.Contains("LEFT AT THIS BURN: 4.2 MIN", nitrogen);
        Assert.Contains("TANKS: 2", nitrogen);
        Assert.Contains("CLICK TO SHOW THE TANKS", nitrogen);
        Assert.DoesNotContain(nitrogen, line => line.Contains("BIPROP", StringComparison.Ordinal));

        var biprop = Tip(own, new FuelPick(FuelKind.Bipropellant, 5, 1, true));
        Assert.Equal("BIPROPELLANT: OK", biprop[0]);
        Assert.Contains("STORED: 2000 / 4000 KG", biprop);
        Assert.Contains("BURN: —", biprop);
        Assert.Contains("TANKS: 4 OF 5, 1 LOST", biprop);
        Assert.Contains("CLICK TO HIDE THE TANKS", biprop);
        Assert.DoesNotContain(biprop, line => line.Contains("NITROGEN", StringComparison.Ordinal));

        var none = Tip(new HullView(), default);
        Assert.Equal("FUEL: NONE", none[0]);
    }

    [Fact]
    public void TanksOfAFuelAreCountedFromTheDesignAndTheLostFromTheBlocksLeft()
    {
        var content = GameContent.Load();
        var design = content.Designs.First(d => d.Tanks.Length > 0);
        var kind = design.Tanks[0].Kind;
        var expected = 0;
        foreach (var tank in design.Tanks)
            expected += tank.Kind == kind ? 1 : 0;
        var alive = new ulong[(design.BlockCount + 63) / 64];
        for (var b = 0; b < design.BlockCount; b++)
            BlockSet.Add(alive, b);
        Assert.Equal(expected, ConsoleFuel.Tanks(design, alive, kind, out var lost));
        Assert.Equal(0, lost);
        BlockSet.Remove(alive, design.Tanks[0].Block);
        Assert.Equal(expected, ConsoleFuel.Tanks(design, alive, kind, out lost));
        Assert.Equal(1, lost);
        Assert.Equal(0, ConsoleFuel.Tanks(design, alive, FuelKind.None, out _));
    }
}
