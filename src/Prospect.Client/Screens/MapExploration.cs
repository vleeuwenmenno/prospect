using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The sectors the player holds a chart of, as a lookup the map can ask for every cell on screen each frame. It is
/// built from <see cref="ExplorationView.Charts"/> and rebuilt only when that list changes, never per frame, and
/// sized once to the content's chart limit so it takes no memory after it is made.
/// </summary>
internal sealed class ChartIndex
{
    private readonly SectorCoord[] _keys;
    private readonly bool[] _used;
    private readonly int _mask;
    private int _count = -1;
    private uint _signature;

    public ChartIndex(int chartCapacity)
    {
        var size = 16;
        while (size < chartCapacity * 2)
            size <<= 1;
        _keys = new SectorCoord[size];
        _used = new bool[size];
        _mask = size - 1;
    }

    /// <summary>How many charts the index holds.</summary>
    public int Count => Math.Max(_count, 0);

    /// <summary>Brings the index up to date with the snapshot's charts. Cheap when nothing changed.</summary>
    public void Refresh(ExplorationView view)
    {
        // A signature over every chart catches a different world with the same number of charts.
        var signature = 0u;
        for (var i = 0; i < view.ChartCount; i++)
            signature = signature * 31u + Hash(view.Charts[i].Sector);
        if (view.ChartCount == _count && signature == _signature)
            return;
        _count = view.ChartCount;
        _signature = signature;
        Array.Clear(_used);
        for (var i = 0; i < view.ChartCount; i++)
        {
            var slot = (int)(Hash(view.Charts[i].Sector) & (uint)_mask);
            while (_used[slot] && _keys[slot] != view.Charts[i].Sector)
                slot = (slot + 1) & _mask;
            _keys[slot] = view.Charts[i].Sector;
            _used[slot] = true;
        }
    }

    public bool Contains(SectorCoord sector)
    {
        var slot = (int)(Hash(sector) & (uint)_mask);
        while (_used[slot])
        {
            if (_keys[slot] == sector)
                return true;
            slot = (slot + 1) & _mask;
        }

        return false;
    }

    private static uint Hash(SectorCoord c) => (uint)(c.X * 73856093 ^ c.Y * 19349663 ^ c.Z * 83492791);
}

/// <summary>
/// Where the player's live sensor coverage is: every owned hull in the snapshot with a sensor block left, as a
/// sphere of the content's sensor radius. This mirrors what the server counts (an owned, equipped hull) so the map
/// can draw it; the server alone decides what is seen. Equipment is worked out again only when a hull's design or
/// lost-block count changes.
/// </summary>
internal sealed partial class SensorCoverage
{
    private uint[] _ids = [];
    private uint[] _revisions = [];
    private byte[] _designs = [];
    private bool[] _sensor = [];
    private bool[] _charting = [];

    /// <summary>Sensor centres, in world metres; the first <see cref="Count"/> are meaningful.</summary>
    public Double3[] Positions = [];

    /// <summary>The hull each sensor belongs to, parallel to <see cref="Positions"/>.</summary>
    public uint[] Ids = [];

    /// <summary>Whether each sensor is on the hull being flown, parallel to <see cref="Positions"/>.</summary>
    public bool[] Piloted = [];
    public int Count;
    public double Radius;

    /// <summary>Whether the hull being flown has a charting block left, so a scan could start.</summary>
    public bool OwnCanChart;

    /// <summary>Collects this frame's sensors from the snapshot's own hulls, at the positions the world draws them.</summary>
    public void Collect(SessionSnapshot snapshot, WorldView world)
    {
        Count = 0;
        OwnCanChart = false;
        if (_ids.Length < snapshot.Hulls.Length)
        {
            // Sized once to the snapshot's hull capacity, never per frame.
            var capacity = snapshot.Hulls.Length;
            _ids = new uint[capacity];
            _revisions = new uint[capacity];
            _designs = new byte[capacity];
            _sensor = new bool[capacity];
            _charting = new bool[capacity];
            Positions = new Double3[capacity];
            Ids = new uint[capacity];
            Piloted = new bool[capacity];
        }

        Configure(world.Galaxy, snapshot.Content.Exploration);

        Radius = snapshot.Content.Exploration.SensorRadius;
        var designs = snapshot.Content.Designs;
        for (var i = 0; i < snapshot.HullCount && i < _ids.Length; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            if (hull.Owner != snapshot.PlayerId || hull.Design >= designs.Length)
                continue;
            if (_ids[i] != hull.Id || _revisions[i] != hull.Revision || _designs[i] != hull.Design)
            {
                _ids[i] = hull.Id;
                _revisions[i] = hull.Revision;
                _designs[i] = hull.Design;
                Equipment(designs[hull.Design], hull.Blocks.Span, out _sensor[i], out _charting[i]);
            }

            if (hull.IsOwn)
                OwnCanChart = _charting[i];
            if (_sensor[i])
            {
                Ids[Count] = hull.Id;
                Piloted[Count] = hull.IsOwn;
                Positions[Count++] = world.HullPosition(hull);
            }
        }
    }

    /// <summary>Whether a design has a sensor and a charting block among the blocks it still has.</summary>
    public static void Equipment(HullDesign design, ReadOnlySpan<ulong> alive, out bool sensor, out bool charting)
    {
        sensor = charting = false;
        var whole = alive.Length == 0;
        for (var b = 0; b < design.BlockCount && !(sensor && charting); b++)
        {
            var type = design.Types[b];
            if (!type.Sensor && !type.Charting)
                continue;
            if (!whole && (b >> 6) < alive.Length && !BlockSet.Has(alive, b))
                continue;
            sensor |= type.Sensor;
            charting |= type.Charting;
        }
    }

    /// <summary>True when a point lies inside any sensor's sphere.</summary>
    public bool Covers(Double3 point)
    {
        var r2 = Radius * Radius;
        for (var i = 0; i < Count; i++)
        {
            var d = point - Positions[i];
            if (d.X * d.X + d.Y * d.Y + d.Z * d.Z <= r2)
                return true;
        }

        return false;
    }

    /// <summary>
    /// How much of a sector live coverage reaches: 0 none, 1 part, 2 its centre and so presumably most of it. The
    /// test is each sphere against the sector's box.
    /// </summary>
    public int Reach(Galaxy galaxy, SectorCoord sector)
    {
        var center = galaxy.CenterOf(sector);
        var half = galaxy.SectorSize * 0.5;
        var r2 = Radius * Radius;
        var reach = 0;
        for (var i = 0; i < Count; i++)
        {
            var p = Positions[i];
            var dx = Math.Max(Math.Abs(p.X - center.X) - half, 0);
            var dy = Math.Max(Math.Abs(p.Y - center.Y) - half, 0);
            var dz = Math.Max(Math.Abs(p.Z - center.Z) - half, 0);
            if (dx * dx + dy * dy + dz * dz > r2)
                continue;
            var c = p - center;
            if (c.X * c.X + c.Y * c.Y + c.Z * c.Z <= r2)
                return 2;
            reach = 1;
        }

        return reach;
    }

    /// <summary>The radius of sensor <paramref name="index"/>'s circle on the horizontal plane at height <paramref name="planeY"/>, or 0.</summary>
    public double CrossSection(int index, double planeY)
    {
        var dy = Positions[index].Y - planeY;
        var r2 = Radius * Radius - dy * dy;
        return r2 > 0 ? Math.Sqrt(r2) : 0;
    }
}

/// <summary>
/// The map's view of the galaxy: flat, one layer seen from straight above, or tilted, the same layers seen at an
/// angle with the layers around the chosen one stacked above and below it. Both are orthographic, so the chosen
/// layer stays a grid of rectangles and a point on screen names exactly one sector on it. Units are sectors: a
/// sector's centre is at whole numbers.
/// </summary>
internal readonly struct MapProjection
{
    public readonly float2 Middle;
    public readonly float2 Center;
    public readonly float Cell;
    public readonly int Layer;
    public readonly float Cos;
    public readonly float Sin;
    public readonly float Spacing;

    public MapProjection(float2 middle, float2 center, float cell, int layer, float tiltRadians, float layerSpacing)
    {
        Middle = middle;
        Center = center;
        Cell = cell;
        Layer = layer;
        Cos = MathF.Cos(tiltRadians);
        Sin = MathF.Sin(tiltRadians);
        Spacing = layerSpacing;
    }

    /// <summary>A sector's height on screen along z.</summary>
    public float CellZ => Cell * Cos;

    public bool Tilted => Sin > 0.001f;

    /// <summary>A point in sector units (x, layer, z) on screen.</summary>
    public float2 ToScreen(float x, float y, float z) =>
        Middle + new float2((x - Center.X) * Cell, ((z - Center.Y) * Cos - (y - Layer) * Spacing * Sin) * Cell);

    /// <summary>A point on the chosen layer, at its plane.</summary>
    public float2 ToScreen(float x, float z) => ToScreen(x, Layer, z);

    /// <summary>The point of the chosen layer's plane under a screen point, in sector units.</summary>
    public float2 OnLayer(float2 screen) => new(Center.X + (screen.X - Middle.X) / Cell, Center.Y + (screen.Y - Middle.Y) / CellZ);

    /// <summary>The sector of the chosen layer under a screen point.</summary>
    public SectorCoord SectorAt(float2 screen)
    {
        var at = OnLayer(screen);
        return new SectorCoord((int)MathF.Floor(at.X + 0.5f), Layer, (int)MathF.Floor(at.Y + 0.5f));
    }
}

/// <summary>
/// What the map is looking at: the chosen layer and whether it is tilted. Keys come through the action map, so a
/// rebound key and the headless tests take the same path as the shipped bindings.
/// </summary>
internal sealed class MapNavigation
{
    /// <summary>The layer the flat view shows and the tilted view centres on.</summary>
    public int Layer;

    /// <summary>Whether the tilted view is chosen, and how far the map has turned toward it, 0 to 1.</summary>
    public bool Tilt;
    public float TiltBlend;

    /// <summary>
    /// Reads the layer and tilt keys. Returns true when the view flattened this frame, so the caller can show the
    /// pinned sector's layer.
    /// </summary>
    public bool HandleKeys(ActionMap actions, InputDevices devices, bool keyboard)
    {
        if (actions.Pressed(devices, InputAction.MapLayerUp, keyboard))
            Layer++;
        if (actions.Pressed(devices, InputAction.MapLayerDown, keyboard))
            Layer--;
        if (!actions.Pressed(devices, InputAction.MapTilt, keyboard))
            return false;
        Tilt = !Tilt;
        return !Tilt;
    }

    /// <summary>Moves the tilt toward the chosen view.</summary>
    public void Advance(float deltaTime, float seconds)
    {
        var step = seconds <= 0f ? 1f : deltaTime / seconds;
        TiltBlend = Math.Clamp(TiltBlend + (Tilt ? step : -step), 0f, 1f);
    }

    public void Reset(int layer)
    {
        Layer = layer;
        Tilt = false;
        TiltBlend = 0f;
    }
}

/// <summary>
/// The decided sector card's rules. Hovering shows a temporary card. Any click on the map outside the pinned card
/// closes it: a left click on a sector then pins that sector's card in the same click (so a click on another sector
/// replaces the card, on the pinned sector leaves it pinned, and outside the galaxy's bounds closes it), and a right
/// click only closes it, since the right click opens the course menu. A click on the card itself, its X, its buttons
/// or its list never gets here: the card is a window the map blocks. A click that only closes the course menu is the
/// menu's and changes no card, and Escape closes the course menu, then the card. While one is pinned, hovering shows
/// no second card and leaves the pinned one alone.
/// </summary>
internal sealed class MapCardState
{
    public bool Pinned { get; private set; }
    public SectorCoord Sector { get; private set; }

    /// <summary>A map click on <paramref name="sector"/>: pins its card, or dismisses when it is outside the bounds.</summary>
    public void Click(SectorCoord sector, bool inBounds)
    {
        if (!inBounds)
        {
            Pinned = false;
            return;
        }

        Pinned = true;
        Sector = sector;
    }

    public void Pin(SectorCoord sector)
    {
        Pinned = true;
        Sector = sector;
    }

    /// <summary>A right click on the map outside the card: closes a pinned card and pins nothing.</summary>
    public void RightClick() => Pinned = false;

    /// <summary>Escape: true when it closed a pinned card, so it closes nothing else.</summary>
    public bool Dismiss()
    {
        var was = Pinned;
        Pinned = false;
        return was;
    }

    /// <summary>
    /// The card to show this frame: the pinned one, or a preview of the hovered sector when the pointer is on the
    /// map and nothing is pinned.
    /// </summary>
    public bool Shown(bool hovering, SectorCoord hover, out SectorCoord sector, out bool preview)
    {
        preview = !Pinned;
        sector = Pinned ? Sector : hover;
        return Pinned || hovering;
    }
}

/// <summary>
/// The pinned sector card's window over the map: where it goes beside its sector, its scrolling ship list and its
/// buttons. The look is <c>ScreenHost</c>'s; this part has no fonts, so the frame order tests run it as it is. A
/// preview card is only drawn on the map's own draw list and is never a window, so the pointer moving onto it
/// cannot take the hover from the map.
/// </summary>
internal static class MapSectorCard
{
    // Unlike the full-window surfaces the card comes to the front when it appears, but stays under the course menu.
    private const ImGuiWindowFlags Flags = ScreenHost.RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    /// <summary>
    /// Right of the sector's cell, or left of it where that would leave the screen, its top level with the cell and
    /// kept between <paramref name="top"/> and the bottom margin.
    /// </summary>
    public static float2 Place(float2 anchor, float halfCell, float2 size, float2 display, float top, float gap, float margin)
    {
        var min = new float2(anchor.X + halfCell + gap, anchor.Y - halfCell);
        if (min.X + size.X > display.X - margin)
            min.X = anchor.X - halfCell - gap - size.X;
        min.X = Math.Clamp(min.X, margin, Math.Max(margin, display.X - size.X - margin));
        min.Y = Math.Clamp(min.Y, top, Math.Max(top, display.Y - size.Y - margin));
        return new float2(MathF.Round(min.X), MathF.Round(min.Y));
    }

    /// <summary>The close button's side, in unscaled pixels.</summary>
    public const float CloseSize = 22f;

    private const float ClosePad = 8f;

    /// <summary>The close button's top left corner: the card's top right corner, inside its padding.</summary>
    public static float2 CloseMin(float2 min, float2 size, float scale) =>
        new(min.X + size.X - MathF.Round((CloseSize + ClosePad) * scale), min.Y + MathF.Round(ClosePad * scale * 0.75f));

    /// <summary>The close button's size.</summary>
    public static float2 CloseExtent(float scale) => new(MathF.Round(CloseSize * scale), MathF.Round(CloseSize * scale));

    /// <summary>
    /// The pinned card's close button, in the card's window. True on the frame it is pressed and released on, which
    /// does what Escape does. A press on it starts in the card's window, so the map beneath never sees it.
    /// </summary>
    public static bool Close(float2 min, float2 size, float scale) => Button("##cardclose"u8, CloseMin(min, size, scale), CloseExtent(scale));

    /// <summary>Opens the pinned card's window. Pair with <see cref="End"/>.</summary>
    public static void Begin(float2 min, float2 size, MapPointer map)
    {
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##mapcard"u8, Flags);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        map.Claim();
    }

    /// <summary>Opens the ship list's scrolling area; the wheel scrolls it. Pair with <see cref="EndList"/>.</summary>
    public static void BeginList(float2 min, float2 size, MapPointer map)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.BeginChild("##mapcardships"u8, size, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar);
        map.Claim();
    }

    public static void EndList() => ImGui.EndChild();

    public static void End() => ImGui.End();

    /// <summary>One of the card's buttons at a rectangle.</summary>
    public static bool Button(ImString id, float2 min, float2 size)
    {
        ImGui.SetCursorScreenPos(min);
        return ImGui.InvisibleButton(id, new float2(Math.Max(size.X, 1f), Math.Max(size.Y, 1f)));
    }

    /// <summary>One row of the ship list at the cursor, the list's full width, so the list scrolls by rows.</summary>
    public static void Row(int index, float height)
    {
        ImGui.PushID(index);
        ImGui.Dummy(new float2(Math.Max(ImGui.GetContentRegionAvail().X, 1f), height));
        ImGui.PopID();
    }
}

/// <summary>
/// The map's layer control in the bottom right corner: layer up and down, a ruler of the layers around the chosen one,
/// and flat or tilted. Its buttons do what Page Up, Page Down and the tilt key do. Sizes are unscaled pixels; the
/// look is <c>ScreenHost</c>'s.
/// </summary>
internal static class MapLayerBar
{
    public const float Width = 196f;
    public const float Header = 30f;
    public const float ButtonHeight = 26f;
    public const float RulerRow = 18f;
    public const int RulerLayers = 3;
    public const float Footer = 30f;
    private const float Pad = 8f;

    private const ImGuiWindowFlags Flags = ScreenHost.RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    /// <summary>The bar's size: header, the up button, the ruler, the down button and the view toggle.</summary>
    public static float2 Size(float scale) => new(MathF.Round(Width * scale),
        MathF.Round((Header + ButtonHeight * 2 + RulerRow * (RulerLayers * 2 + 1) + Footer + Pad * 2) * scale));

    /// <summary>
    /// In the bottom right corner, clear of the performance overlay at the top right; never above
    /// <paramref name="top"/>, the bottom of the navigation bar.
    /// </summary>
    public static float2 Min(float2 display, float top, float scale) =>
        new(display.X - MathF.Round((MapUnitList.HoverRight + Width) * scale),
            Math.Max(top, display.Y - MathF.Round(MapUnitList.HoverRight * scale) - Size(scale).Y));

    public static float2 UpMin(float2 min, float scale) => min + new float2(MathF.Round(Pad * scale), MathF.Round(Header * scale));

    public static float2 ButtonSize(float scale) => new(MathF.Round((Width - Pad * 2) * scale), MathF.Round(ButtonHeight * scale));

    /// <summary>The top of the ruler row for a layer <paramref name="offset"/> above the chosen one (negative below).</summary>
    public static float RulerRowTop(float2 min, int offset, float scale) =>
        min.Y + MathF.Round((Header + ButtonHeight + RulerRow * (RulerLayers - offset)) * scale);

    public static float2 DownMin(float2 min, float scale) =>
        new(min.X + MathF.Round(Pad * scale), min.Y + MathF.Round((Header + ButtonHeight + RulerRow * (RulerLayers * 2 + 1)) * scale));

    /// <summary>The flat and tilted halves of the view toggle.</summary>
    public static float2 FlatMin(float2 min, float scale) =>
        new(min.X + MathF.Round(Pad * scale), DownMin(min, scale).Y + MathF.Round((ButtonHeight + Pad) * scale));

    public static float2 ToggleSize(float scale) => new(MathF.Round((Width - Pad * 2) * 0.5f * scale), MathF.Round((Footer - 4f) * scale));

    public static float2 TiltMin(float2 min, float scale) => FlatMin(min, scale) + new float2(ToggleSize(scale).X, 0);

    public static void Begin(float2 min, float2 size, MapPointer map)
    {
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##maplayers"u8, Flags);
        if (ImGui.IsWindowAppearing())
            ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        map.Claim();
    }

    public static bool Button(ImString id, float2 min, float2 size)
    {
        ImGui.SetCursorScreenPos(min);
        return ImGui.InvisibleButton(id, size);
    }

    public static void End() => ImGui.End();

    /// <summary>Applies the bar's buttons to the navigation. Returns true when the view flattened.</summary>
    public static bool Apply(MapNavigation navigation, bool up, bool down, bool flat, bool tilt)
    {
        if (up)
            navigation.Layer++;
        if (down)
            navigation.Layer--;
        if (tilt)
            navigation.Tilt = true;
        if (flat && navigation.Tilt)
        {
            navigation.Tilt = false;
            return true;
        }

        return false;
    }
}

/// <summary>Where the galaxy holds matter: the map's playable bounds. Nothing the server enforces yet.</summary>
internal static class MapBounds
{
    /// <summary>The disc's edge in sectors: beyond it <see cref="GalaxyShape.Density"/> is zero.</summary>
    public static float EdgeRadius(GalaxyShape shape) => shape.Radius * 1.25f;

    /// <summary>The lowest and highest layers whose falloff is at least <paramref name="edgeFalloff"/>.</summary>
    public static void Layers(GalaxyShape shape, float edgeFalloff, out int low, out int high)
    {
        var reach = shape.Thickness * MathF.Sqrt(-MathF.Log(edgeFalloff));
        low = (int)MathF.Ceiling(shape.Center.Y - reach);
        high = (int)MathF.Floor(shape.Center.Y + reach);
    }

    public static bool Contains(GalaxyShape shape, float edgeFalloff, SectorCoord sector)
    {
        Layers(shape, edgeFalloff, out var low, out var high);
        if (sector.Y < low || sector.Y > high)
            return false;
        var dx = sector.X - shape.Center.X;
        var dz = sector.Z - shape.Center.Z;
        var edge = EdgeRadius(shape);
        return dx * dx + dz * dz <= edge * edge;
    }
}

/// <summary>
/// Why a scan stopped, as far as the client can tell: the server sends only that it was cancelled. A change of the
/// controlled hull is not here: the server reports that as no scan at all, and the readout is not shown for it.
/// </summary>
internal enum ScanStop : byte
{
    None,
    LeftSector,
    ChartingLost,

    /// <summary>Nothing else changed, so the player cancelled it (the scan key again, or the card's button).</summary>
    Requested,
}

/// <summary>
/// The scan's readout: how far it is, and when it stops why. The server reports a cancelled scan without a reason,
/// so the reason is inferred from what changed since it started: the sector, then its charting block; with neither
/// changed the player cancelled it.
/// </summary>
internal sealed class ScanTracker
{
    private ScanState _seen;

    /// <summary>Seconds since the scan last finished, was cancelled or refused; large while none has.</summary>
    public float SinceResult { get; private set; } = float.MaxValue;

    public ScanStop Stop { get; private set; }

    /// <summary>Follows the snapshot's scan, inferring a cancellation's reason.</summary>
    public void Update(in ScanView scan, SectorCoord ownSector, bool ownCanChart, float deltaTime)
    {
        if (SinceResult < float.MaxValue)
            SinceResult += deltaTime;
        if (scan.State == _seen)
            return;
        if (scan.State == ScanState.Scanning)
        {
            Stop = ScanStop.None;
        }
        else if (scan.State is ScanState.Completed or ScanState.Cancelled or ScanState.NoEquipment or ScanState.ChartLimit or ScanState.Cooldown)
        {
            SinceResult = 0f;
            Stop = scan.State != ScanState.Cancelled ? ScanStop.None
                : ownSector != scan.Sector ? ScanStop.LeftSector
                : !ownCanChart ? ScanStop.ChartingLost
                : ScanStop.Requested;
        }

        _seen = scan.State;
    }

    /// <summary>How far a running scan is, 0 to 1, at the server tick the snapshot estimates.</summary>
    public static float Progress(in ScanView scan, long serverTick)
    {
        var span = scan.CompletesTick - scan.StartedTick;
        return span <= 0 ? 1f : Math.Clamp((float)(serverTick - scan.StartedTick) / span, 0f, 1f);
    }
}

/// <summary>Whether the scan key and the card's button may ask for a scan now, by what the snapshot says.</summary>
internal enum ScanGate : byte
{
    /// <summary>A request may go out; the server still decides.</summary>
    Ready,

    /// <summary>A scan is already running.</summary>
    Running,

    /// <summary>The sector was scanned, or a scan of it cancelled, too recently.</summary>
    Cooling,
}

/// <summary>What the scan readout on the flight and command displays has to show this frame.</summary>
internal readonly record struct ScanStrip(bool Running, bool ResultWindow, int CooldownSeconds)
{
    /// <summary>Nothing to draw: no scan of the hull flown.</summary>
    public static readonly ScanStrip None = new(false, false, 0);

    /// <summary>True when anything may be drawn: a bar, a result still in its window, or a cooldown.</summary>
    public bool Any => Running || ResultWindow || CooldownSeconds > 0;
}

/// <summary>
/// The client's reading of the server's scan cooldown. The server enforces it; this only keeps the key and the
/// button from asking while the answer is known to be no, and says how long is left.
/// </summary>
internal static class ScanRules
{
    /// <summary>What a request for the sector the player is in would meet, at the snapshot's server tick.</summary>
    public static ScanGate Gate(in ScanView scan, SectorCoord here, long serverTick) =>
        scan.State == ScanState.Scanning ? ScanGate.Running
        : scan.CoolingDown(here, serverTick) ? ScanGate.Cooling
        : ScanGate.Ready;

    /// <summary>
    /// The scan key: when pressed, asks the server to scan the sector the ship being flown is in, unless the sector is
    /// cooling down, which the snapshot already says; pressed while a scan runs it asks the server to cancel it
    /// instead. A ship with no charting block still asks, so the server's refusal is what the readout tells. The same
    /// requests as the map card's button. True when a request went out.
    /// </summary>
    public static bool HandleKey(ActionMap actions, InputDevices devices, bool keyboardAvailable, in ScanView scan, SectorCoord here, long serverTick, PlayerInputBridge bridge)
    {
        if (!actions.Pressed(devices, InputAction.ScanSector, keyboardAvailable))
            return false;
        switch (Gate(scan, here, serverTick))
        {
            case ScanGate.Running:
                bridge.CancelScan();
                return true;
            case ScanGate.Ready:
                bridge.RequestScan();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// What the strip shows: the bar while a scan runs, else a result for <paramref name="resultSeconds"/> after it
    /// ended, and the cooldown while there is one. Only for the hull flown: a scan is bound to the ship that began it,
    /// so with another hull controlled (<paramref name="ownHull"/>) nothing shows.
    /// </summary>
    public static ScanStrip Strip(in ScanView scan, uint ownHull, SectorCoord here, long serverTick, float sinceResult, float resultSeconds) =>
        ownHull == 0 || scan.Hull != ownHull ? ScanStrip.None
        : scan.State == ScanState.Scanning ? new ScanStrip(true, false, 0)
        : new ScanStrip(false, sinceResult <= resultSeconds, CooldownSeconds(scan, here, serverTick));

    /// <summary>Whole seconds of cooldown left for <paramref name="here"/>, rounded up so it never reads 0 while still cooling; 0 when none.</summary>
    public static int CooldownSeconds(in ScanView scan, SectorCoord here, long serverTick) =>
        scan.CoolingDown(here, serverTick) ? (int)((scan.CooldownUntilTick - serverTick + TickLoop.SimulationHz - 1) / TickLoop.SimulationHz) : 0;
}

/// <summary>
/// Which of the player's own hulls on the map the pointer is on, for the name label beside it. The hull nearest the
/// pointer within a radius wins. While one does, the sector's preview card stays away: the pointer is on a unit,
/// not asking about the sector under it. A pinned card is not affected.
/// </summary>
internal struct MapUnitHover
{
    private float _best;

    /// <summary>The hull the pointer is on, 0 for none.</summary>
    public uint Hull { get; private set; }

    /// <summary>Where that hull is drawn.</summary>
    public float2 At { get; private set; }

    /// <summary>Starts a frame's search with the radius in pixels the pointer must be within.</summary>
    public MapUnitHover(float radius)
    {
        _best = radius * radius;
        Hull = 0;
        At = default;
    }

    /// <summary>Offers an own hull drawn at <paramref name="at"/>.</summary>
    public void Consider(uint hull, float2 at, float2 mouse)
    {
        var offset = at - mouse;
        var distance = offset.X * offset.X + offset.Y * offset.Y;
        if (distance > _best)
            return;
        _best = distance;
        Hull = hull;
        At = at;
    }

    /// <summary>True when a hull is under the pointer.</summary>
    public readonly bool Hit => Hull != 0;
}

/// <summary>Where the map's legend goes and how many of its rows fit; the look is <c>ScreenHost</c>'s.</summary>
internal readonly record struct MapLegendLayout(float2 Min, float2 Size, float Header, float Row, int Rows)
{
    /// <summary>True when there is room for at least one row.</summary>
    public bool Shown => Rows > 0;
}

/// <summary>
/// The map's legend: a vertical index on the left, under the unit list. Its rows are listed from the most to the
/// least important, so where the screen is short the last (the star types) are the ones left out. Where even the
/// features do not fit under the unit list it sits beside it. It is a surface over the map: the map blocks its
/// rectangle, so a press on it picks nothing.
/// </summary>
internal static class MapLegend
{
    /// <summary>Rows before the star types: charted, live sensors, unexplored, lost contact, asteroid field, gas cloud, and the two unit status icons (lightning, cold).</summary>
    public const int FeatureRows = 8;

    private const float Width = 200f;
    private const float HeaderHeight = 26f;
    private const float Gap = 8f;
    private const float PadBelow = 6f;

    /// <summary>
    /// Lays the legend out for <paramref name="rows"/> rows under the unit list, or beside it where the room below
    /// is under the feature rows, and drops what does not fit.
    /// </summary>
    public static MapLegendLayout Layout(float2 display, float scale, in MapUnitLayout units, int rows, float rowHeight)
    {
        float Px(float value) => MathF.Round(value * scale);
        var row = Px(rowHeight);
        var header = Px(HeaderHeight);
        var pad = Px(PadBelow);
        var bottom = display.Y - Px(HudLayout.Margin);
        var width = Px(Width);

        var top = units.Min.Y + units.Size.Y + Px(Gap);
        var fit = (int)MathF.Floor((bottom - top - header - pad) / row);
        var min = new float2(units.Min.X, top);
        if (fit < Math.Min(rows, FeatureRows))
        {
            // Beside the unit list, top level with it, where the whole column is there for it.
            min = new float2(units.Min.X + units.Size.X + Px(Gap), units.Min.Y);
            fit = (int)MathF.Floor((bottom - min.Y - header - pad) / row);
            if (min.X + width > display.X - Px(HudLayout.Margin))
                fit = 0;
        }

        var shown = Math.Clamp(fit, 0, rows);
        return new MapLegendLayout(min, new float2(width, header + row * shown + pad), header, row, shown);
    }
}
