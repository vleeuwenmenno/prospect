using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    /// <summary>
    /// Opens the map with the card of the player's own sector, or the sector offset from it, pinned. Used by
    /// <c>--open map-card</c>, <c>map-card-near</c> and <c>map-card-far</c>.
    /// </summary>
    public void OpenMapCard(int dx = 0, int dz = 0)
    {
        OpenMap();
        _mapCard.Pin(_app.World.Sector.Coord.Offset(dx, 0, dz));
    }

    /// <summary>Opens the map tilted, with the own sector's card pinned. Used by <c>--open map-tilt</c>.</summary>
    public void OpenMapTilt()
    {
        OpenMapCard();
        _mapNav.Tilt = true;
        _mapNav.TiltBlend = 1f;
    }

    /// <summary>The map zoomed in as far as it goes on the own position, to see the shape of sensor reach. Used by <c>--open map-reach</c> and <c>map-reach-tilt</c>.</summary>
    public void OpenMapReach(bool tilt)
    {
        OpenMap();
        _mapCell = MapMaxCell;
        _mapNav.Tilt = tilt;
        _mapNav.TiltBlend = tilt ? 1f : 0f;
    }

    /// <summary>Starts a scan of the own sector and shows it on the pinned card. Used by <c>--open scan</c>.</summary>
    public void OpenScan()
    {
        OpenMapCard();
        Bridge?.RequestScan();
    }

    /// <summary>Starts a scan of the own sector as the key would, in the flight view. Used by <c>--open scan-pilot</c>.</summary>
    public void OpenScanPilot() => Bridge?.RequestScan();

    // The scan key's name for drawing, kept until the binding changes so the frame never builds it again.
    private InputBinding _scanKeyBinding;
    private byte[] _scanKey = [];

    /// <summary>The first control bound to the scan action, in capitals as UTF-8; "UNBOUND" when none is.</summary>
    private ReadOnlySpan<byte> ScanKeyName()
    {
        var bindings = _app.Actions.Bindings(InputAction.ScanSector);
        if (bindings.IsEmpty)
            return "UNBOUND"u8;
        if (_scanKey.Length == 0 || bindings[0] != _scanKeyBinding)
        {
            _scanKeyBinding = bindings[0];
            _scanKey = System.Text.Encoding.UTF8.GetBytes(bindings[0].DisplayName.ToUpperInvariant());
        }

        return _scanKey;
    }

    /// <summary>
    /// The scan on the flight and command displays, small and muted under the top of the view: a bar while it runs,
    /// then for a few seconds what came of it, and under that the time until the sector can be scanned again. Flight
    /// goes on throughout. With the pointer shown, resting it on the strip names the scan key.
    /// </summary>
    private void DrawScanStrip(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha, bool pointable)
    {
        var look = MapLook;
        var scan = snapshot.Exploration.Scan;
        var here = world.Sector.Coord;
        var show = ScanRules.Strip(scan, here, snapshot.ServerTick, _scan.SinceResult, look.ScanResultSeconds);
        var running = show.Running;
        var cooldown = show.CooldownSeconds;
        var result = show.ResultWindow ? ScanResult(scan, scan.Sector, out _) : default;
        if (!running && cooldown == 0 && result.Length == 0)
            return;
        var display = _ui.DisplaySize;
        var width = Math.Min(Kit.Px(look.ScanStripWidth), display.X - Kit.Px(Theme.Space5) * 2);
        var top = HudTop + (world.CommandBlend > 0.5f ? Kit.Px(62) : Kit.Px(Theme.Space2));
        var min = new float2(MathF.Round((display.X - width) * 0.5f), top);
        var previous = Kit.Fade;
        Kit.Fade = alpha * look.ScanStripAlpha;
        var height = 0f;
        if (running)
        {
            var progress = ScanTracker.Progress(scan, snapshot.ServerTick);
            var left = (1f - progress) * snapshot.Content.Exploration.ScanTicks / TickLoop.SimulationHz;
            var label = Kit.F($"SCANNING SECTOR {scan.Sector.X} {scan.Sector.Y} {scan.Sector.Z}   {left:0.0} S");
            var labelWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, label).X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(min.X + (width - labelWidth) * 0.5f, min.Y), Theme.Info, label);
            var bar = min + new float2(0, Kit.Px(18));
            draw.AddRectFilled(bar, bar + new float2(width, Kit.Px(4)), Kit.Color(Theme.PanelRaised, 0.9f));
            draw.AddRectFilled(bar, bar + new float2(width * progress, Kit.Px(4)), Kit.Color(Theme.Info));
            height = Kit.Px(22);
        }
        else
        {
            var line = min.Y;
            if (result.Length > 0)
            {
                ScanResult(scan, scan.Sector, out var color);
                var resultWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, result).X;
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(MathF.Round((display.X - resultWidth) * 0.5f), line), color, result,
                    Math.Clamp(look.ScanResultSeconds - _scan.SinceResult, 0f, 1f));
                line += Kit.Px(16);
                height = Kit.Px(16);
            }

            if (cooldown > 0)
            {
                var text = Kit.F($"NEXT SCAN IN {cooldown} S");
                var textWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, text).X;
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(MathF.Round((display.X - textWidth) * 0.5f), line), Theme.TextDim, text, look.ScanCooldownAlpha / look.ScanStripAlpha);
                height += Kit.Px(16);
            }
        }

        Kit.Fade = previous;
        if (pointable && !_ui.PointerOverInterface)
        {
            var mouse = ImGui.GetIO().MousePos;
            if (mouse.X >= min.X && mouse.X <= min.X + width && mouse.Y >= min.Y && mouse.Y <= min.Y + height)
                DrawScanTip(ImGui.GetForegroundDrawList(), snapshot, world, min, new float2(width, height));
        }
    }

    /// <summary>
    /// A tooltip for the scan, under a control or readout: its key, and in a line what it does now (ready, running,
    /// cooling down, or no charting block). It takes no input and is drawn over everything.
    /// </summary>
    private void DrawScanTip(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float2 anchorMin, float2 anchorSize)
    {
        var scan = snapshot.Exploration.Scan;
        var gate = ScanRules.Gate(scan, world.Sector.Coord, snapshot.ServerTick);
        var seconds = snapshot.Content.Exploration.ScanTicks / (float)TickLoop.SimulationHz;
        var title = Kit.F($"SCAN SECTOR   {ScanKeyName()}");
        var note = gate switch
        {
            ScanGate.Running => "Flight goes on. Leaving the sector or switching ship cancels."u8,
            ScanGate.Cooling => Kit.F($"Cooling down: ready in {ScanRules.CooldownSeconds(scan, world.Sector.Coord, snapshot.ServerTick)} s."),
            _ => !_coverage.OwnCanChart ? "This ship has no charting block."u8 : Kit.F($"Charts the sector you are in in {seconds:0} s. Flight goes on."),
        };
        var pad = Kit.Px(Theme.Space2);
        var titleSize = Kit.Measure(_ui.Mono, Theme.FontCaption, title);
        var noteSize = Kit.Measure(_ui.Sans, Theme.FontCaption, note);
        var size = new float2(Math.Max(titleSize.X, noteSize.X) + pad * 2, titleSize.Y + noteSize.Y + pad * 2 + Kit.Px(2));
        var display = _ui.DisplaySize;
        var min = new float2(Math.Clamp(anchorMin.X + (anchorSize.X - size.X) * 0.5f, Kit.Px(Theme.Space2), Math.Max(Kit.Px(Theme.Space2), display.X - size.X - Kit.Px(Theme.Space2))), anchorMin.Y + anchorSize.Y + Kit.Px(Theme.Space2));
        if (min.Y + size.Y > display.Y - Kit.Px(Theme.Space2))
            min.Y = Math.Max(Kit.Px(Theme.Space2), anchorMin.Y - size.Y - Kit.Px(Theme.Space2));
        Hud.Plate(draw, min, min + size, Theme.Info, true);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(pad, pad), Theme.Accent, title);
        Kit.Text(draw, _ui.Sans, Theme.FontCaption, min + new float2(pad, pad + titleSize.Y + Kit.Px(2)), Theme.TextDim, note);
    }

    /// <summary>
    /// Sensor coverage in the command view, on the command plane, for the selected ships (the ship being flown when
    /// nothing is selected). Ships within the content's radar link distance of each other, directly or through a
    /// chain, share a picture: their gas-shaped reach is drawn as one merged outline with no line inside it, and
    /// ships farther apart each get their own. A thin line is the boundary and a few faint strokes follow it
    /// outward, darkening gently, so unseen space reads as unseen without hiding what lies behind. A faint amber
    /// line marks the plain range where gas cut it short. Lost contacts show at their last position, hollow and fading. The sector's
    /// knowledge state sits under the view's title.
    /// </summary>
    private void DrawCommandFog(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var look = MapLook;
        var plane = world.CommandFocus.Y;
        var radius = _coverage.Radius;
        _coverage.LinkDistance = look.RadarLinkMeters;
        _coverage.SelectActive(world.Selection);
        _coverage.LinkActive();
        _coverage.Refresh(plane, true);
        var outline = _coverage.Outline();
        var labelled = false;
        for (var s = 0; s < _coverage.ActiveCount; s++)
        {
            var i = _coverage.ActiveIndex(s);
            var alone = true;
            for (var other = 0; other < _coverage.ActiveCount && alone; other++)
                alone = other == s || _coverage.GroupOf(other) != _coverage.GroupOf(s);

            if (!_coverage.Ring(i, out var x, out var z, out var nominal, out var samples))
                continue;

            // A ship on its own keeps the sphere's silhouette as its volume; linked ships show the outline only. Where
            // gas cut the reach the amber plain-range line already shows how far it would reach, so a second, differently
            // projected circle for the same range is left out.
            var cut = false;
            for (var k = 0; k < samples.Length && !cut; k++)
                cut = samples[k] < nominal * 0.995;
            if (alone && !(cut && look.GasRangeAlpha > 0) && world.ProjectDisc(_coverage.Positions[i], (float)radius, out var sphere, out var pixels, out _))
                draw.AddCircle(sphere, pixels, Kit.Color(Theme.Good, look.CommandRingAlpha * 0.5f * alpha), 64, Kit.Px(1f));
            if (!labelled && world.Project(new Double3(x, plane, z - samples[samples.Length * 3 / 4]), out var labelAt))
            {
                labelled = true;
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, labelAt + new float2(Kit.Px(6), -Kit.Px(16)), Theme.Good, Kit.F($"SENSOR RANGE {radius / 1000:0.0} KM"), alpha * 0.7f);
            }
        }

        // The fade outside: wide faint strokes standing off the boundary at growing distance, each darker than the
        // one inside it. Left out where another sensor covers the stroke, so overlapping coverage stays clear.
        for (var ring = look.CommandFadeRings; ring >= 1; ring--)
        {
            var strength = look.CommandFogAlpha * alpha * ring / look.CommandFadeRings;
            var color = Kit.Color(0x020305, strength);
            var fadeWidth = Kit.Px(look.CommandFadeWidth);
            for (var k = 0; k < outline.Length; k++)
            {
                ref readonly var piece = ref outline[k];
                var stand = (look.CommandFogReach - 1) * piece.Reach * ring / look.CommandFadeRings;
                var ox = piece.NX * stand;
                var oz = piece.NZ * stand;
                if (_coverage.InsideShown((piece.AX + piece.BX) * 0.5 + ox, (piece.AZ + piece.BZ) * 0.5 + oz))
                    continue;
                if (world.ProjectSegment(new Double3(piece.AX + ox, plane, piece.AZ + oz), new Double3(piece.BX + ox, plane, piece.BZ + oz), out var s0, out var s1))
                    draw.AddLine(s0, s1, color, fadeWidth);
            }
        }

        // Where gas cut a sensor short, a faint amber line marks how far it would have seen: the plain range, outlined
        // once where such circles overlap. It is a line and not a fill, so it can never cover more than a pixel's width
        // of the view however thick the gas.
        if (look.GasRangeAlpha > 0)
        {
            var lost = _coverage.LostRange(true);
            var amber = Kit.Color(Theme.Accent, look.GasRangeAlpha * alpha);
            for (var k = 0; k < lost.Length; k++)
            {
                ref readonly var piece = ref lost[k];
                if (world.ProjectSegment(new Double3(piece.AX, plane, piece.AZ), new Double3(piece.BX, plane, piece.BZ), out var s0, out var s1))
                    draw.AddLine(s0, s1, amber, Kit.Px(1f));
            }
        }

        var line = Kit.Color(Theme.Good, look.CommandRingAlpha * alpha);
        for (var k = 0; k < outline.Length; k++)
        {
            ref readonly var piece = ref outline[k];
            if (world.ProjectSegment(new Double3(piece.AX, plane, piece.AZ), new Double3(piece.BX, plane, piece.BZ), out var s0, out var s1))
                draw.AddLine(s0, s1, line, Kit.Px(look.CommandRingWidth));
        }

        if (look.LinkLineAlpha > 0)
        {
            for (var l = 0; l < _coverage.LinkCount; l++)
            {
                var (a, b) = _coverage.Link(l);
                if (world.ProjectSegment(_coverage.Positions[a], _coverage.Positions[b], out var s0, out var s1))
                    draw.AddLine(s0, s1, Kit.Color(Theme.Good, look.LinkLineAlpha * alpha), Kit.Px(1f));
            }
        }

        // Lost contacts: last known position only, never a live target.
        var exploration = snapshot.Exploration;
        var designs = snapshot.Content.Designs;
        var lifetime = Math.Max(1, snapshot.Content.Exploration.ContactTicks);
        for (var i = 0; i < exploration.ContactCount; i++)
        {
            ref var contact = ref exploration.Contacts[i];
            if (contact.Live || contact.Owner == snapshot.PlayerId || !world.Project(contact.Position, out var at))
                continue;
            var design = designs[Math.Min(contact.Design, designs.Length - 1)];
            var age = Math.Max(0, snapshot.ServerTick - contact.LostTick);
            var fade = look.StaleFade * alpha * (design.Class == HullClass.Station ? 1f : Math.Clamp(1f - (float)age / lifetime, 0.15f, 1f));
            DrawContactMarker(draw, at, Kit.Px(7), Theme.TextDim, fade, false, design.Class);
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(Kit.Px(12), -Kit.Px(7)), Theme.TextMute,
                Kit.F($"LOST {age / (float)TickLoop.SimulationHz:0} S AGO   {design.Name}"), fade);
        }

        // What the player knows of the sector the camera is in.
        var sector = world.Galaxy!.SectorOf(world.CommandFocus);
        var charted = Charted(sector);
        var state = _coverage.Count == 0 ? "NO SENSOR COVERAGE"u8
            : _coverage.ActiveCount == 0 ? "NO SENSOR ON THE SELECTED SHIPS"u8
            : charted ? "SECTOR CHARTED   LIVE INSIDE THE RINGS"u8 : "SECTOR UNCHARTED   SCAN FROM THE MAP CARD"u8;
        var width = Kit.Measure(_ui.Mono, Theme.FontCaption, state).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(MathF.Round((_ui.DisplaySize.X - width) * 0.5f), HudTop + Kit.Px(40)),
            _coverage.ActiveCount == 0 ? Theme.Bad : charted ? Theme.Info : Theme.Accent, state, alpha);
    }
}
