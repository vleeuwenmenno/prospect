using Brutal.ImGuiApi;
using Brutal.Numerics;
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

    /// <summary>
    /// The scan on the flight and command displays: a bar while it runs, then for a few seconds what came of it,
    /// under the top of the view. Flight goes on throughout.
    /// </summary>
    private void DrawScanStrip(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var scan = snapshot.Exploration.Scan;
        var running = scan.State == ScanState.Scanning;
        if (!running && _scan.SinceResult > MapLook.ScanResultSeconds)
            return;
        var display = _ui.DisplaySize;
        var width = Math.Min(Kit.Px(300), display.X - Kit.Px(Theme.Space5) * 2);
        var top = HudTop + (world.CommandBlend > 0.5f ? Kit.Px(62) : Kit.Px(Theme.Space2));
        var min = new float2(MathF.Round((display.X - width) * 0.5f), top);
        var previous = Kit.Fade;
        Kit.Fade = alpha;
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
        }
        else
        {
            var result = ScanResult(scan, scan.Sector, out var color);
            if (result.Length > 0)
            {
                var resultWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, result).X;
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(MathF.Round((display.X - resultWidth) * 0.5f), min.Y), color, result,
                    Math.Clamp(MapLook.ScanResultSeconds - _scan.SinceResult, 0f, 1f));
            }
        }

        Kit.Fade = previous;
    }

    /// <summary>
    /// Sensor coverage in the command view, on the command plane, for the selected ships (the ship being flown when
    /// nothing is selected). Ships within the content's radar link distance of each other, directly or through a
    /// chain, share a picture: their gas-shaped reach is drawn as one merged outline with no line inside it, and
    /// ships farther apart each get their own. A thin line is the boundary and a few faint strokes follow it
    /// outward, darkening gently, so unseen space reads as unseen without hiding what lies behind. A very faint tint
    /// shows where gas cut the range. Lost contacts show at their last position, hollow and fading. The sector's
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

            // A ship on its own keeps the sphere's silhouette as its volume; linked ships show the outline only.
            if (alone && world.ProjectDisc(_coverage.Positions[i], (float)radius, out var sphere, out var pixels, out _))
                draw.AddCircle(sphere, pixels, Kit.Color(Theme.Good, look.CommandRingAlpha * 0.5f * alpha), 64, Kit.Px(1f));
            if (!_coverage.Ring(i, out var x, out var z, out var nominal, out var samples))
                continue;
            if (look.GasTintAlpha > 0)
                DrawGasTint(draw, world, i, x, z, nominal, samples, plane, Kit.Color(Theme.Accent, look.GasTintAlpha * alpha));
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

    /// <summary>
    /// Fills, very faintly, the sliver between a sensor's plain sphere and its gas-shaped boundary where gas cut the
    /// range, so a short side reads as lost range rather than as nothing. Left out where another shown sensor covers
    /// the sliver, and drawn once where the slivers of several sensors overlap.
    /// </summary>
    private void DrawGasTint(ImDrawListPtr draw, WorldView world, int index, double x, double z, double nominal, ReadOnlySpan<double> samples, double plane, uint color)
    {
        var n = samples.Length;
        var tolerance = nominal * 0.005;
        for (var k = 0; k < n; k++)
        {
            var next = k + 1 == n ? 0 : k + 1;
            if (samples[k] >= nominal - tolerance && samples[next] >= nominal - tolerance)
                continue;
            var a0 = k * (Math.Tau / n);
            var a1 = next * (Math.Tau / n);
            double c0 = Math.Cos(a0), s0 = Math.Sin(a0), c1 = Math.Cos(a1), s1 = Math.Sin(a1);
            var mx = x + (nominal + (samples[k] + samples[next]) * 0.5) * 0.5 * (c0 + c1) * 0.5;
            var mz = z + (nominal + (samples[k] + samples[next]) * 0.5) * 0.5 * (s0 + s1) * 0.5;
            if (_coverage.InsideShown(mx, mz, index) || _coverage.InsideEarlierDisc(mx, mz, index))
                continue;
            if (world.Project(new Double3(x + nominal * c0, plane, z + nominal * s0), out var p0)
                && world.Project(new Double3(x + nominal * c1, plane, z + nominal * s1), out var p1)
                && world.Project(new Double3(x + samples[next] * c1, plane, z + samples[next] * s1), out var p2)
                && world.Project(new Double3(x + samples[k] * c0, plane, z + samples[k] * s0), out var p3))
                draw.AddQuadFilled(p0, p1, p2, p3, color);
        }
    }
}
