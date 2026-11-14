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
    /// Sensor coverage in the command view, on the command plane: a thin boundary line per sensor drawn from its
    /// sampled reach in every direction, the sphere's outline as the volume, and outside the boundary a few faint
    /// strokes that darken gently with distance, so unseen space reads as unseen without hiding what lies behind.
    /// Lost contacts show at their last position, hollow and fading. The sector's knowledge state sits under the
    /// view's title.
    /// </summary>
    private void DrawCommandFog(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var look = MapLook;
        var plane = world.CommandFocus.Y;
        var radius = _coverage.Radius;
        Span<double> reach = stackalloc double[SensorCoverage.ReachSamples];
        for (var i = 0; i < _coverage.Count; i++)
        {
            var p = _coverage.Positions[i];
            if (world.ProjectDisc(p, (float)radius, out var sphere, out var pixels, out _))
                draw.AddCircle(sphere, pixels, Kit.Color(Theme.Good, look.CommandRingAlpha * 0.5f * alpha), 64, Kit.Px(1f));

            if (!_coverage.SampleReach(i, plane, reach))
                continue;
            var center = new Double3(p.X, plane, p.Z);

            // The fade outside: wide faint strokes at growing distance, each darker than the one inside it. Left
            // out where another sensor covers the stroke, so overlapping coverage stays clear.
            for (var ring = look.CommandFadeRings; ring >= 1; ring--)
            {
                var scale = 1 + (look.CommandFogReach - 1) * ring / look.CommandFadeRings;
                var strength = look.CommandFogAlpha * alpha * ring / look.CommandFadeRings;
                DrawReachLine(draw, world, center, reach, scale, Kit.Color(0x020305, strength), Kit.Px(look.CommandFadeWidth), plane, i);
            }

            DrawReachLine(draw, world, center, reach, 1, Kit.Color(Theme.Good, look.CommandRingAlpha * alpha), Kit.Px(look.CommandRingWidth), plane, -1);
            if (world.Project(new Double3(p.X, plane, p.Z - reach[reach.Length * 3 / 4]), out var labelAt))
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, labelAt + new float2(Kit.Px(6), -Kit.Px(16)), Theme.Good, Kit.F($"SENSOR RANGE {radius / 1000:0.0} KM"), alpha * 0.7f);
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

    /// <summary>
    /// A closed line round <paramref name="center"/> through the sampled reach distances, each scaled by
    /// <paramref name="scale"/>, so the boundary follows whatever the reach is in each direction. Segments whose
    /// midpoint lies inside another sensor's ring (any sensor but <paramref name="except"/>) are skipped.
    /// </summary>
    private void DrawReachLine(ImDrawListPtr draw, WorldView world, Double3 center, ReadOnlySpan<double> reach, double scale, uint color, float thickness, double plane, int except)
    {
        var n = reach.Length;
        for (var k = 0; k < n; k++)
        {
            var next = k + 1 == n ? 0 : k + 1;
            var a0 = k * (Math.Tau / n);
            var a1 = (k + 1) * (Math.Tau / n);
            var from = center + new Double3(Math.Cos(a0), 0, Math.Sin(a0)) * (reach[k] * scale);
            var to = center + new Double3(Math.Cos(a1), 0, Math.Sin(a1)) * (reach[next] * scale);
            if (except >= 0 && CoveredOnPlane((from + to) * 0.5, plane, except))
                continue;
            if (world.ProjectSegment(from, to, out var s0, out var s1))
                draw.AddLine(s0, s1, color, thickness);
        }
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
