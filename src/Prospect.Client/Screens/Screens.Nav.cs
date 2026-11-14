using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The plotted jump in the pilot view: the NAV marker with its card, and the jump strip above the pilot console
/// that says what the drive is doing and what the pilot can do about it. Nothing here takes the pointer.
/// </summary>
internal sealed partial class ScreenHost
{
    /// <summary>Seconds the strip says "not aligned" after the boost key asked for a jump off the NAV marker.</summary>
    private const float NavRefusedSeconds = 2.5f;

    /// <summary>What the strip says this frame; <see cref="JumpPrompt.None"/> when it is not drawn.</summary>
    private JumpPrompt _jumpPrompt;

    /// <summary>Where the jump strip sits this frame, at the top centre under the target readout and scan strip; empty when not drawn.</summary>
    private HudRect _jumpStrip;

    // The plotted destination's name, made once per destination: naming a sector allocates.
    private SectorCoord _navCoord;
    private Galaxy? _navGalaxy;
    private string _navName = "";

    /// <summary>The strip's state for the hull being flown.</summary>
    private JumpPrompt PromptFor(WorldView world)
    {
        var own = world.Own;
        var prompt = HudLayout.Prompt(
            world.NavTarget is not null,
            world.NavAimed,
            world.Time - world.NavRefusedAt < NavRefusedSeconds,
            own.Jump,
            own.Order.Kind == OrderKind.Jump,
            own.Order.Kind == OrderKind.MoveTo
        );

        // The server's refusal of a plot or a start is news for a few seconds, over a plot or an idle strip.
        var news = world.NavRefusal != JumpRefusal.None && world.Time - world.NavRefusalAt < MapLook.CourseRefusalSeconds;
        return news && prompt is JumpPrompt.None or JumpPrompt.OffCourse or JumpPrompt.Refused or JumpPrompt.Aligned ? JumpPrompt.Denied : prompt;
    }

    private string NavName(WorldView world, Double3 nav)
    {
        var coord = world.Galaxy!.SectorOf(nav);
        if (_navName.Length == 0 || coord != _navCoord || !ReferenceEquals(_navGalaxy, world.Galaxy))
        {
            _navCoord = coord;
            _navGalaxy = world.Galaxy;
            _navName = world.Galaxy!.NameOf(coord);
        }

        return _navName;
    }

    private static ReadOnlySpan<byte> DriveName(FtlKind kind) =>
        kind switch
        {
            FtlKind.Hyperspace => "HYPERSPACE DRIVE"u8,
            FtlKind.Wormhole => "WORMHOLE GENERATOR"u8,
            _ => "RIFT CUTTER"u8,
        };

    private static ReadOnlySpan<byte> Distance(float metres) =>
        !float.IsFinite(metres) ? "-"u8
        : metres >= 10_000f ? Kit.F($"{metres / 1000f:0} KM")
        : metres >= 1000f ? Kit.F($"{metres / 1000f:0.0} KM")
        : Kit.F($"{metres:0} M");

    // The boost key's name as UTF-8 for drawing, made again only when the bindings name another key.
    private string? _boostKeyFrom;
    private byte[] _boostKey = [];

    private byte[] BoostKey(WorldView world)
    {
        if (!ReferenceEquals(_boostKeyFrom, world.BoostKeyName))
        {
            _boostKeyFrom = world.BoostKeyName;
            _boostKey = System.Text.Encoding.UTF8.GetBytes(world.BoostKeyName);
        }

        return _boostKey;
    }

    /// <summary>
    /// The jump plotted on the map: a marker on its direction while that is clear of the panels, or an arrow at the
    /// edge of the clear room pointing the way to turn while it is behind or under a panel, the strip or the console.
    /// While the pilot still has to act on it, a card beside the marker names the destination and its distance;
    /// once the drive is at work the card folds back to the marker's label, to keep the view clear. The marker
    /// turns green once the nose is on it.
    /// </summary>
    private void DrawNavTarget(ImDrawListPtr draw, WorldView world, in HudCueRoom room, JumpPrompt prompt)
    {
        if (world.NavTarget is not { } nav)
            return;
        var color = world.NavAimed ? Theme.Good : Theme.Info;
        var inFront = world.Project(nav, out var screen);
        if (inFront && room.Clear(screen, room.Pad(true)))
        {
            DrawTargetMarker(draw, screen, color);
            var asks = prompt is JumpPrompt.OffCourse or JumpPrompt.Refused or JumpPrompt.Aligned;
            if (!asks || !DrawNavCard(draw, world, room, nav, screen, color, prompt == JumpPrompt.Aligned))
                NavLabel(draw, room, screen, 1f, color);
            return;
        }

        // Obstructed: an arrow at the edge of the clear room, toward the marker's point while it is in front, the
        // way across the view while it is behind, with its label beside it.
        var view = world.ToView(nav);
        var toward = inFront ? screen - room.Start : new float2(view.X, -view.Y);
        var length = MathF.Sqrt(toward.X * toward.X + toward.Y * toward.Y);
        var across = length > 1e-4f ? toward / length : new float2(0, 1);
        // With no clear room that way, no arrow: never one drawn under a panel.
        if (!room.Edge(room.Start, across, out var at))
            return;
        var size = Kit.Px(11);
        var side = new float2(-across.Y, across.X);
        var tip = at + across * size;
        var back = at - across * (size * 0.45f);
        draw.AddTriangleFilled(tip, back + side * (size * 0.8f), back - side * (size * 0.8f), Kit.Color(color));
        NavLabel(draw, room, at, across.X > 0f ? -1f : 1f, color);
    }

    /// <summary>
    /// The marker's "NAV" beside it, on the side asked for (1 for the right) where that is clear of the panels, else
    /// on the other side, else left out.
    /// </summary>
    private void NavLabel(ImDrawListPtr draw, in HudCueRoom room, float2 at, float side, uint color)
    {
        var offset = Kit.Px(HudLayout.CueLabel);
        if (!room.Clear(at + new float2(offset * side, 0), room.Pad(true)))
        {
            side = -side;
            if (!room.Clear(at + new float2(offset * side, 0), room.Pad(true)))
                return;
        }

        var size = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "NAV"u8);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, at + new float2(offset * side - size.X * 0.5f, -size.Y * 0.5f), color, "NAV"u8);
    }

    /// <summary>
    /// The NAV card, tied to the marker by a thin line: the destination's name and how far it is. False, with
    /// nothing drawn, when it has no room clear of the panels and of the marker.
    /// </summary>
    private bool DrawNavCard(ImDrawListPtr draw, WorldView world, in HudCueRoom room, Double3 nav, float2 marker, uint color, bool lit)
    {
        var name = NavName(world, nav);
        var coord = _navCoord;
        var distance = Distance(nav.RelativeTo(world.OwnPosition).Length());
        var details = Kit.F($"{distance}   SECTOR {coord.X} {coord.Y} {coord.Z}");
        var pad = Kit.Px(Theme.Space3);
        var tag = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "NAV"u8).X + Kit.Px(Theme.Space2);
        var nameWidth = Kit.Measure(_ui.SansMedium, Theme.FontSmall, Kit.F($"{name}")).X;
        var detailWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, details).X;
        var width = Math.Clamp(Math.Max(tag + nameWidth, detailWidth) + pad * 2 + Kit.Px(4), Kit.Px(HudLayout.NavCardMin), Kit.Px(HudLayout.NavCardMax));
        // Rounded up, so a line measured to a fraction of a pixel is never cut by the rounding.
        var size = new float2(MathF.Ceiling(width), Kit.Px(HudLayout.NavCardHeight));
        var free = HudLayout.NavFree(_ui.DisplaySize, Kit.Scale, world.TargetHull != 0 || world.TargetRock is not null, _jumpStrip);
        if (!HudLayout.PlaceNavCard(marker, size, free, room, Kit.Scale, out var min))
            return false;

        // The tie: from the marker's ring to the middle of the card's near side.
        var max = min + size;
        var anchor = HudLayout.NavTieAnchor(marker, min, size);
        var toward = anchor - marker;
        var reach = MathF.Sqrt(toward.X * toward.X + toward.Y * toward.Y);
        if (reach > Kit.Px(HudLayout.NavMarkerClear))
            draw.AddLine(marker + toward * (Kit.Px(HudLayout.NavMarkerClear) / reach), anchor, Kit.Color(color, 0.6f), 1f);

        Hud.Plate(draw, min, max, color, lit);
        var text = min + new float2(pad + Kit.Px(4), Kit.Px(5));
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, text + new float2(0, Kit.Px(2)), color, "NAV"u8);
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, text + new float2(tag, 0), Theme.Text, name, size.X - pad * 2 - Kit.Px(4) - tag);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, text + new float2(0, Kit.Px(18)), Theme.TextDim, details, size.X - pad * 2 - Kit.Px(4));
        return true;
    }

    /// <summary>
    /// The jump strip at the top centre of the pilot view, under the target readout and the scan strip, drawn only while there is something to say: a plotted course, the
    /// drive at work, or a move order. Left of its divider, the state and a line under it; right of it, the one
    /// action the pilot can take with the loaded key, or the drive's calculation and charge, which run together.
    /// </summary>
    private void DrawJumpStrip(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, JumpPrompt prompt)
    {
        if (prompt == JumpPrompt.None)
            return;
        var own = world.Own;
        var flight = snapshot.Flight;
        var strip = _jumpStrip;
        var tone = prompt switch
        {
            JumpPrompt.Aligned => Theme.Good,
            JumpPrompt.Refused or JumpPrompt.Denied => Theme.Bad,
            JumpPrompt.Autopilot => Theme.Accent,
            _ => Theme.Info,
        };
        var calculation = WorldView.CalculationFraction(own, flight);
        var charge = WorldView.ChargeFraction(own, flight);
        Hud.Pointed(draw, new float2(strip.Left, strip.Top), new float2(strip.Right, strip.Bottom), Kit.Px(HudLayout.StripPoint), tone, 0.85f, prompt is JumpPrompt.Aligned or JumpPrompt.Refused);
        HudLayout.StripColumns(strip, Kit.Scale, out var statusLeft, out var divider, out var actionLeft, out var actionRight);
        var lean = Kit.Px(4);
        draw.AddLine(new float2(divider + lean, strip.Top + Kit.Px(8)), new float2(divider - lean, strip.Bottom - Kit.Px(8)), Kit.Color(Theme.LineStrong), 1f);

        // The state, and under it where to or how to stop.
        var statusWidth = divider - lean - Kit.Px(Theme.Space1) - statusLeft;
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontSmall, new float2(statusLeft, strip.Top + Kit.Px(4)), tone, HudLayout.PromptWord(prompt, calculation >= 1f), statusWidth);
        ReadOnlySpan<byte> under = prompt switch
        {
            JumpPrompt.OffCourse or JumpPrompt.Refused or JumpPrompt.Aligned or JumpPrompt.Denied when world.NavTarget is { } nav => Kit.F($"{NavName(world, nav)}"),
            JumpPrompt.Aligning or JumpPrompt.Working => "HALT CANCELS"u8,
            JumpPrompt.Autopilot => "MOVE ORDER"u8,
            _ => DriveName(flight.Ftl),
        };
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(statusLeft, strip.Top + Kit.Px(22)), Theme.TextDim, under, statusWidth);

        var middle = (strip.Top + strip.Bottom) * 0.5f;
        var room = actionRight - actionLeft;
        var key = BoostKey(world);
        var caption = Kit.Measure(_ui.SansMedium, Theme.FontCaption, "Ag"u8).Y;
        var line = MathF.Round(middle - caption * 0.5f);
        switch (prompt)
        {
            case JumpPrompt.OffCourse:
            case JumpPrompt.Refused:
                {
                    // Turn first: the boost key only starts a jump with the nose on the marker.
                    var x = actionLeft + Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(actionLeft, line), Theme.Text, "AIM AT NAV, THEN"u8).X + Kit.Px(Theme.Space2);
                    if (x + Hud.KeyChipWidth(_ui.Mono, Theme.FontCaption, key) <= actionRight)
                        Hud.KeyChip(draw, _ui.Mono, Theme.FontCaption, x, middle, Theme.TextDim, key);
                    break;
                }
            case JumpPrompt.Aligned:
                {
                    // The key starts the drive; what follows is the calculation and the charge, said in plain figures.
                    var x = actionLeft + Hud.KeyChip(draw, _ui.Mono, Theme.FontCaption, actionLeft, middle, Theme.Good, key) + Kit.Px(Theme.Space2);
                    x += Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(x, line), Theme.Text, "START JUMP"u8).X;
                    DrawDriveCost(draw, flight, x, actionRight, line);
                    break;
                }
            case JumpPrompt.Denied:
                Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(actionLeft, line), Theme.Bad, RefusalWords(snapshot, world.NavRefusal), room);
                break;
            case JumpPrompt.Ordered:
                break;
            case JumpPrompt.Aligning:
                {
                    var x = actionLeft + Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(actionLeft, line), Theme.Info, DriveName(flight.Ftl)).X;
                    DrawDriveCost(draw, flight, x, actionRight, line);
                    break;
                }
            case JumpPrompt.Working:
                {
                    // Two gauges side by side: the calculation and the charge run at once, and the drive fires when
                    // both are full.
                    var gap = Kit.Px(Theme.Space3);
                    var column = (room - gap) * 0.5f;
                    var remaining = MathF.Max(0f, (1f - calculation) * flight.FtlCalculateSeconds);
                    var calc = calculation >= 1f ? "CALCULATED"u8 : Kit.F($"CALC {MathF.Ceiling(remaining):0} S");
                    StripGauge(draw, actionLeft, column, strip, calc, calculation);
                    // Kit.F reuses one buffer, so the energy figures are formatted here rather than through
                    // Energy(), whose text would be overwritten by the outer format.
                    var capacity = FlightConsole.Shown(flight.FtlEnergy, float.MaxValue);
                    var stored = FlightConsole.Shown(own.JumpTimer, capacity);
                    var giga = FlightConsole.Thousands(capacity);
                    var scale = giga ? 1000f : 1f;
                    var head = charge >= 1f ? "CHARGED" : "CHARGE";
                    var charged = giga
                        ? Kit.F($"{head} {stored / scale:0} / {capacity / scale:0} GJ")
                        : Kit.F($"{head} {stored:0} / {capacity:0} MJ");
                    StripGauge(draw, actionLeft + column + gap, column, strip, charged, charge);
                    break;
                }
            case JumpPrompt.Transit:
                StripGauge(draw, actionLeft, room, strip, DriveName(flight.Ftl), WorldView.JumpProgress(own, flight));
                break;
            case JumpPrompt.Autopilot:
                if (own.Order.Kind == OrderKind.MoveTo)
                    Kit.Text(draw, _ui.Mono, Theme.FontSmall, new float2(actionLeft, MathF.Round(middle - Kit.Measure(_ui.Mono, Theme.FontSmall, "0"u8).Y * 0.5f)), Theme.Text, Distance(own.Order.Target.RelativeTo(world.OwnPosition).Length()));
                break;
        }
    }

    /// <summary>What the drive needs once started, right-aligned in the strip when it fits: seconds of calculation and the charge.</summary>
    private void DrawDriveCost(ImDrawListPtr draw, in FlightParams flight, float from, float right, float y)
    {
        var cost = flight.FtlCalculateSeconds > 0f
            ? Kit.F($"{flight.FtlCalculateSeconds:0} S + {FlightConsole.Shown(flight.FtlEnergy, float.MaxValue):0} MJ")
            : Kit.F($"{FlightConsole.Shown(flight.FtlEnergy, float.MaxValue):0} MJ");
        var width = Kit.Measure(_ui.Mono, Theme.FontCaption, cost).X;
        if (from + Kit.Px(Theme.Space3) + width <= right)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(right - width, y + Kit.Px(1)), Theme.TextDim, cost);
    }

    /// <summary>A labelled gauge in the strip's action column: the label above, a thin bar along the bottom.</summary>
    private void StripGauge(ImDrawListPtr draw, float left, float width, in HudRect strip, ReadOnlySpan<byte> label, float fraction)
    {
        fraction = Math.Clamp(float.IsFinite(fraction) ? fraction : 0f, 0f, 1f);
        var done = fraction >= 1f;
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(left, strip.Top + Kit.Px(7)), done ? Theme.Good : Theme.Text, label, width);
        var top = strip.Bottom - Kit.Px(12);
        var bottom = top + Kit.Px(3);
        draw.AddRectFilled(new float2(left, top), new float2(left + width, bottom), Kit.Color(Theme.Line, 0.85f));
        if (fraction > 0f)
            draw.AddRectFilled(new float2(left, top), new float2(left + MathF.Round(width * fraction), bottom), Kit.Color(done ? Theme.Good : Theme.Info));
    }
}
