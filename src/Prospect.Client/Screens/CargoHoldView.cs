using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>A placed tile in the Cargo window's container grid, in pixels relative to the grid's origin.</summary>
internal readonly record struct CargoTileRect(float X, float Y, float Width, float Height);

/// <summary>
/// Font-free state and tile layout for the Cargo window: hold totals, selection lifetime across hold revisions,
/// FTL/rack/drone gates, fill/health fractions and wrapped size-stepped tile placement.
/// </summary>
internal sealed class CargoHoldView
{
    private readonly CargoTileRect[] _tiles = new CargoTileRect[BlockStateStore.MaxContainers];
    private long _lastCargoTick = -1;
    private uint _lastHullId;

    public bool HasHull { get; private set; }
    public bool IsDrone { get; private set; }
    public bool HasRacks { get; private set; }
    public bool InFtlTransit { get; private set; }
    public int UsedUnits { get; private set; }
    public int CapacityUnits { get; private set; }
    public double TotalMassKg { get; private set; }
    public int ContainerCount { get; private set; }
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Refreshes totals and gates from the current snapshot, clearing selection when the hold changes.</summary>
    public void Update(SessionSnapshot snapshot)
    {
        if (snapshot.OwnHull < 0 || snapshot.OwnHull >= snapshot.Hulls.Length)
        {
            HasHull = false;
            IsDrone = false;
            HasRacks = false;
            InFtlTransit = false;
            UsedUnits = 0;
            CapacityUnits = 0;
            TotalMassKg = 0;
            ContainerCount = 0;
            SelectedIndex = -1;
            _lastHullId = 0;
            _lastCargoTick = -1;
            return;
        }

        var hull = snapshot.Hulls[snapshot.OwnHull];
        var design = snapshot.Content.Designs[hull.Design];
        HasHull = true;
        IsDrone = snapshot.OwnIsDrone || design.BlockSize < 1f;
        CapacityUnits = (int)MathF.Round(hull.Stats.CargoHold);
        HasRacks = CapacityUnits > 0;
        InFtlTransit = hull.Jump == JumpPhase.Tunnel;

        if (_lastHullId != hull.Id || _lastCargoTick != snapshot.OwnCargoTick)
        {
            _lastHullId = hull.Id;
            _lastCargoTick = snapshot.OwnCargoTick;
            SelectedIndex = -1;
        }

        var cargo = hull.Cargo.Span;
        ContainerCount = Math.Min(cargo.Length, _tiles.Length);
        var used = 0;
        var mass = 0.0;
        var sizes = snapshot.Content.Cargo.Sizes;
        for (var i = 0; i < cargo.Length; i++)
        {
            var c = cargo[i];
            if ((uint)c.Size < (uint)sizes.Length)
                used += sizes[c.Size].Units;
            mass += c.Amount;
        }

        UsedUnits = used;
        TotalMassKg = mass;
        if (SelectedIndex >= ContainerCount)
            SelectedIndex = -1;
    }

    /// <summary>Selects a container by hold index, or clears the selection when out of range.</summary>
    public void Select(int index) =>
        SelectedIndex = (uint)index < (uint)ContainerCount ? index : -1;

    /// <summary>Clears the active container selection.</summary>
    public void ClearSelection() => SelectedIndex = -1;

    /// <summary>Tile width and height in unscaled pixels, growing in four steps with container size class (0..7).</summary>
    public static (float Width, float Height) TileSizeUnscaled(int sizeIndex) => sizeIndex switch
    {
        <= 1 => (48f, 44f),
        <= 3 => (62f, 44f),
        <= 5 => (80f, 50f),
        _ => (104f, 56f),
    };

    /// <summary>
    /// Lays out tiles left-to-right within <paramref name="gridWidth"/> pixels, wrapping to a new row when a tile
    /// would overflow. Returns the total layout height in pixels.
    /// </summary>
    public float Layout(ReadOnlySpan<HoldContainer> cargo, float gridWidth, float scale)
    {
        var count = Math.Min(cargo.Length, _tiles.Length);
        if (count == 0)
            return 0f;

        var gap = MathF.Round(Theme.Space2 * scale);
        var x = 0f;
        var y = 0f;
        var rowHeight = 0f;

        for (var i = 0; i < count; i++)
        {
            var (uw, uh) = TileSizeUnscaled(cargo[i].Size);
            var w = MathF.Round(uw * scale);
            var h = MathF.Round(uh * scale);

            if (x > 0f && x + w > gridWidth)
            {
                x = 0f;
                y += rowHeight + gap;
                rowHeight = 0f;
            }

            _tiles[i] = new CargoTileRect(x, y, w, h);
            x += w + gap;
            if (h > rowHeight)
                rowHeight = h;
        }

        return y + rowHeight;
    }

    /// <summary>The computed tile rectangles for the most recent <see cref="Layout"/> call.</summary>
    public ReadOnlySpan<CargoTileRect> Tiles => _tiles.AsSpan(0, ContainerCount);

    /// <summary>Fill fraction (0..1) of a container against its resource density and unit volume.</summary>
    public static float FillFraction(SessionSnapshot snapshot, in HoldContainer container)
    {
        if (container.Resource < 0 || snapshot.Galaxy is null)
            return 0f;
        var resources = snapshot.Galaxy.Resources;
        if ((uint)container.Resource >= (uint)resources.Length)
            return 0f;
        var sizes = snapshot.Content.Cargo.Sizes;
        if ((uint)container.Size >= (uint)sizes.Length)
            return 0f;
        var unit = snapshot.Content.Cargo.UnitSize;
        var units = sizes[container.Size].Units;
        var density = snapshot.Content.Cargo.Density(resources[container.Resource].Id);
        var maxKg = units * unit * unit * unit * density;
        return maxKg > 0.0 ? Math.Clamp((float)(container.Amount / maxKg), 0f, 1f) : 0f;
    }

    /// <summary>Hull points fraction (0..1) of a container against its size class maximum.</summary>
    public static float HealthFraction(SessionSnapshot snapshot, in HoldContainer container)
    {
        var sizes = snapshot.Content.Cargo.Sizes;
        if ((uint)container.Size >= (uint)sizes.Length)
            return 0f;
        var maxHp = sizes[container.Size].Hull;
        return maxHp > 0f ? Math.Clamp(container.Hull / maxHp, 0f, 1f) : 0f;
    }
}
