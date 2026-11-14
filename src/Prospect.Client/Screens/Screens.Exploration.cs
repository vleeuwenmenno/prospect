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
    /// <summary>Opens the map with the card of the player's own sector pinned. Used by <c>--open map-card</c>.</summary>
    public void OpenMapCard()
    {
        OpenMap();
        _mapCard.Pin(_app.World.Sector.Coord);
    }

    /// <summary>Opens the map tilted, with the own sector's card pinned. Used by <c>--open map-tilt</c>.</summary>
    public void OpenMapTilt()
    {
        OpenMapCard();
        _mapNav.Tilt = true;
        _mapNav.TiltBlend = 1f;
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
    /// Fog of war in the command view, on the command plane: each sensor's sphere cut by the plane as a ring, the
    /// sphere's outline as the volume, and outside the rings a darkening that deepens with distance, so unseen space
    /// reads as unseen. Lost contacts show at their last position, hollow and fading. The sector's knowledge state
    /// sits under the view's title.
    /// </summary>
    private void DrawCommandFog(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var look = MapLook;
        var plane = world.CommandFocus.Y;
        var radius = _coverage.Radius;
        const int segments = 48;
        const int bands = 4;
        for (var i = 0; i < _coverage.Count; i++)
        {
            var p = _coverage.Positions[i];
            if (world.ProjectDisc(p, (float)radius, out var sphere, out var pixels, out _))
                draw.AddCircle(sphere, pixels, Kit.Color(Theme.Good, look.LiveRingAlpha * 0.25f * alpha), 64, Kit.Px(1f));

            var r = _coverage.CrossSection(i, plane);
            if (r <= 0)
                continue;
            for (var k = 0; k < segments; k++)
            {
                var a0 = k * (MathF.Tau / segments);
                var a1 = (k + 1) * (MathF.Tau / segments);
                var c0 = new Double3(Math.Cos(a0), 0, Math.Sin(a0));
                var c1 = new Double3(Math.Cos(a1), 0, Math.Sin(a1));
                var center = new Double3(p.X, plane, p.Z);
                if (world.ProjectSegment(center + c0 * r, center + c1 * r, out var s0, out var s1))
                    draw.AddLine(s0, s1, Kit.Color(Theme.Good, look.LiveRingAlpha * alpha), Kit.Px(1.5f));

                // The fog outside: bands of growing darkness out to the configured reach, left out where another
                // sensor's ring covers the band.
                for (var band = 0; band < bands; band++)
                {
                    var inner = r * (1 + (look.CommandFogReach - 1) * band / bands);
                    var outer = r * (1 + (look.CommandFogReach - 1) * (band + 1) / bands);
                    var middle = center + (c0 + c1) * (0.25 * (inner + outer));
                    if (CoveredOnPlane(middle, plane, i))
                        continue;
                    if (!world.Project(center + c0 * inner, out var q0) || !world.Project(center + c1 * inner, out var q1)
                        || !world.Project(center + c1 * outer, out var q2) || !world.Project(center + c0 * outer, out var q3))
                        continue;
                    Span<float2> quad = [q0, q1, q2, q3];
                    draw.AddConvexPolyFilled(quad, Kit.Color(0x020305, look.CommandFogAlpha * alpha * (band + 1) / bands));
                }
            }

            if (world.Project(new Double3(p.X, plane, p.Z - r), out var labelAt))
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, labelAt + new float2(Kit.Px(6), -Kit.Px(16)), Theme.Good, Kit.F($"SENSOR RANGE {radius / 1000:0.0} KM"), alpha);
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
        var state = _coverage.Count == 0 ? "NO SENSOR COVERAGE"u8 : charted ? "SECTOR CHARTED   LIVE INSIDE THE RINGS"u8 : "SECTOR UNCHARTED   SCAN FROM THE MAP CARD"u8;
        var width = Kit.Measure(_ui.Mono, Theme.FontCaption, state).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(MathF.Round((_ui.DisplaySize.X - width) * 0.5f), HudTop + Kit.Px(40)),
            _coverage.Count == 0 ? Theme.Bad : charted ? Theme.Info : Theme.Accent, state, alpha);
    }

    /// <summary>Whether a point on the command plane lies inside the ring of any sensor other than <paramref name="except"/>.</summary>
    private bool CoveredOnPlane(Double3 point, double plane, int except)
    {
        for (var j = 0; j < _coverage.Count; j++)
        {
            if (j == except)
                continue;
            var r = _coverage.CrossSection(j, plane);
            var dx = point.X - _coverage.Positions[j].X;
            var dz = point.Z - _coverage.Positions[j].Z;
            if (dx * dx + dz * dz < r * r)
                return true;
        }

        return false;
    }
}
