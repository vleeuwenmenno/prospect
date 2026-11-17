using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>The Cargo window's tabs, in the order they are drawn and saved (<see cref="FloatingWindow.Tab"/>).</summary>
internal enum CargoTab
{
    /// <summary>The hold's containers: the window as it was before ore.</summary>
    Containers = 0,

    /// <summary>The ore bins' totals and a tile per bin.</summary>
    Ore = 1,
}

/// <summary>A tab's place in the Cargo window's tab strip, in pixels relative to the strip's origin.</summary>
internal readonly record struct CargoTabRect(float X, float Y, float Width, float Height);

/// <summary>What one ore the bins hold amounts to: kilograms, grade weighted by mass, and the bins it fills.</summary>
internal readonly record struct OreTotal(int Resource, double Kg, float Grade, int Bins);

/// <summary>A bin as the ORE tab shows it: its place, what it holds and how full it is.</summary>
internal readonly record struct OreTile(float X, float Y, float Width, float Height, int Resource, double Kg, double Capacity, float Grade, float Fill)
{
    /// <summary>True while the bin holds nothing (or an ore this galaxy does not know), and shows as EMPTY.</summary>
    public bool IsEmpty => Resource < 0;
}

/// <summary>
/// Font-free state for the Cargo window's ORE tab and the tab strip: the totals of each ore the bins hold, the tile
/// of every bin and the wrapped layout of those tiles. It reads only <see cref="HullView.Ore"/> and the design's bins
/// (what the Ship window's ORE section reads, through <see cref="ShipSheet"/> for the held and capacity sums); it
/// works out nothing the server could contradict. Fixed buffers, so updating and laying out take no memory.
/// </summary>
internal sealed class OreHoldView
{
    /// <summary>How many tabs the Cargo window has.</summary>
    public const int TabCount = 2;

    private readonly OreTile[] _tiles = new OreTile[OreBins.MaxBins];
    private readonly OreTotal[] _totals = new OreTotal[OreBins.MaxBins];
    private readonly double[] _gradeKg = new double[OreBins.MaxBins];

    /// <summary>True when a ship is flown; without one the tab says so.</summary>
    public bool HasHull { get; private set; }

    /// <summary>Bins the design has (at most <see cref="OreBins.MaxBins"/>), and how many hold ore.</summary>
    public int BinCount { get; private set; }

    public int BinsUsed { get; private set; }

    /// <summary>Kilograms held and the kilograms the bins hold when full, from <see cref="ShipSheet"/>.</summary>
    public double HeldKg { get; private set; }

    public double CapacityKg { get; private set; }

    /// <summary>The ores the bins hold, in the order they first appear in the bins.</summary>
    public ReadOnlySpan<OreTotal> Totals => _totals.AsSpan(0, TotalCount);

    /// <summary>The number of different ores held.</summary>
    public int TotalCount { get; private set; }

    /// <summary>The bins' tiles for the most recent <see cref="Layout"/>, in bin order.</summary>
    public ReadOnlySpan<OreTile> Tiles => _tiles.AsSpan(0, BinCount);

    /// <summary>The tab a window's saved number names: anything unknown is CONTAINERS.</summary>
    public static CargoTab TabOf(int saved) => saved == (int)CargoTab.Ore ? CargoTab.Ore : CargoTab.Containers;

    /// <summary>
    /// A tab's rectangle: tabs sit side by side from the strip's origin, <see cref="CargoWindowLook.TabWidth"/> wide
    /// (cut to share the width when the window is narrower).
    /// </summary>
    public static CargoTabRect TabRect(CargoTab tab, float stripWidth, float scale, CargoWindowLook look)
    {
        var gap = MathF.Round(Theme.Space1 * scale);
        var width = MathF.Min(MathF.Round(look.TabWidth * scale), MathF.Max(1f, MathF.Floor((stripWidth - gap * (TabCount - 1)) / TabCount)));
        return new CargoTabRect((int)tab * (width + gap), 0f, width, MathF.Round(look.TabHeight * scale));
    }

    /// <summary>
    /// Reads the flown hull's bins: the totals of each ore and a tile per bin, in the ore's galaxy index. A bin the
    /// server has not told of yet, one that is empty and one with an ore this galaxy lacks all read as empty.
    /// </summary>
    public void Update(SessionSnapshot snapshot)
    {
        HasHull = false;
        BinCount = 0;
        BinsUsed = 0;
        TotalCount = 0;
        HeldKg = 0;
        CapacityKg = 0;
        if (snapshot.OwnHull < 0 || snapshot.OwnHull >= snapshot.Hulls.Length)
            return;

        var hull = snapshot.Hulls[snapshot.OwnHull];
        var design = snapshot.Content.Designs[hull.Design];
        HasHull = true;
        var bins = design.Bins;
        BinCount = Math.Min(bins.Length, _tiles.Length);
        var held = hull.Ore.Span;
        var resources = (snapshot.Galaxy ?? snapshot.Content.Galaxy).Resources;
        HeldKg = ShipSheet.OreHeld(held);
        CapacityKg = ShipSheet.OreCapacity(design);

        for (var i = 0; i < BinCount; i++)
        {
            var capacity = bins[i].Capacity;
            if (i >= held.Length || held[i].IsEmpty || (uint)held[i].Resource >= (uint)resources.Length)
            {
                _tiles[i] = new OreTile(0f, 0f, 0f, 0f, -1, 0.0, capacity, 0f, 0f);
                continue;
            }

            var bin = held[i];
            var fill = capacity > 0.0 ? Math.Clamp((float)(bin.Kg / capacity), 0f, 1f) : 0f;
            _tiles[i] = new OreTile(0f, 0f, 0f, 0f, bin.Resource, bin.Kg, capacity, bin.Grade, fill);
            BinsUsed++;

            var slot = -1;
            for (var t = 0; t < TotalCount; t++)
                if (_totals[t].Resource == bin.Resource) { slot = t; break; }
            if (slot < 0)
            {
                slot = TotalCount++;
                _totals[slot] = new OreTotal(bin.Resource, 0.0, 0f, 0);
                _gradeKg[slot] = 0.0;
            }

            var total = _totals[slot];
            _gradeKg[slot] += bin.Kg * bin.Grade;
            var kg = total.Kg + bin.Kg;
            _totals[slot] = new OreTotal(bin.Resource, kg, kg > 0.0 ? (float)(_gradeKg[slot] / kg) : 0f, total.Bins + 1);
        }
    }

    /// <summary>
    /// Lays the bins' tiles out left to right within <paramref name="gridWidth"/> pixels, wrapping when a tile would
    /// overflow. Returns the layout's height in pixels; call after <see cref="Update"/>.
    /// </summary>
    public float Layout(float gridWidth, float scale, CargoWindowLook look)
    {
        if (BinCount == 0)
            return 0f;
        var gap = MathF.Round(Theme.Space2 * scale);
        var w = MathF.Round(look.OreTileWidth * scale);
        var h = MathF.Round(look.OreTileHeight * scale);
        var x = 0f;
        var y = 0f;
        for (var i = 0; i < BinCount; i++)
        {
            if (x > 0f && x + w > gridWidth)
            {
                x = 0f;
                y += h + gap;
            }

            _tiles[i] = _tiles[i] with { X = x, Y = y, Width = w, Height = h };
            x += w + gap;
        }

        return y + h;
    }
}
