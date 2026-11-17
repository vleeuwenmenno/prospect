using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

/// <summary>
/// The drone scan in the interface: the target panel's scan block and the drone result it shows in place of a
/// module's, and the hologram over its rock. Everything shown is what the server sent
/// (<see cref="SessionSnapshot.DroneScans"/>, <see cref="BayView"/>): a request goes through
/// <see cref="PlayerInputBridge"/> and the server decides. Nothing here allocates.
/// </summary>
internal sealed partial class ScreenHost
{
    private readonly ScanHologramState _hologramState = new();
    private readonly ScanHologram _hologramTarget = new();
    private readonly ScanHologram _hologramScan = new();
    private string _droneScanKey = "", _hologramKey = "";
    private int _scanKeysVersion = -1;

    /// <summary>Whether the drone scan's hologram shows. A session choice, never saved.</summary>
    internal bool ScanHologramOn => _hologramState.On;

    private static ModuleLook.DroneScanLook ScanLook => ModuleLook.Current.DroneScan;

    /// <summary>The rock the flown hull's drone scan is of while it has not ended, else none.</summary>
    private static RockRef? ScanRockOf(in BayView bays) => bays.ScanPhase != DroneScanPhase.None ? bays.ScanRock : null;

    /// <summary>Reads the scan keys' names and this frame's results (which turn the hologram on). Called with the module state.</summary>
    private void UpdateDroneScans(SessionSnapshot snapshot, WorldView world)
    {
        if (_scanKeysVersion != _app.Actions.Version)
        {
            _scanKeysVersion = _app.Actions.Version;
            static string Name(ActionMap actions, InputAction action)
            {
                var bindings = actions.Bindings(action);
                return bindings.Length == 0 ? "UNBOUND" : DroneScanUi.ShortKey(bindings[0].DisplayName.ToUpperInvariant());
            }

            _droneScanKey = Name(_app.Actions, InputAction.DroneScan);
            _hologramKey = Name(_app.Actions, InputAction.ToggleScanHologram);
        }

        _hologramState.Observe(snapshot.DroneScans, ScannableRock(world), ScanRockOf(snapshot.Bays));
    }

    /// <summary>The drone scan key and the hologram's key. They work where the module keys do.</summary>
    private void HandleDroneScanKeys(PlayerInputBridge bridge, SessionSnapshot snapshot, WorldView world)
    {
        DroneScanUi.HandleKeys(_app.Actions, _app.Devices, !_ui.WantsTextInput, snapshot.Bays, world.Own.Id, ScannableRock(world), bridge);
        if (_app.Actions.Pressed(_app.Devices, InputAction.ToggleScanHologram, !_ui.WantsTextInput))
            _hologramState.Toggle();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The drone result on the target panel
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// How tall the target panel's analysis is for a drone result, unscaled pixels: the composition's bar and rows, then
    /// the footer (<see cref="DroneScanUi.RecordFooterHeight"/>).
    /// </summary>
    private float DroneAnalysisHeight(in DroneScanRecord record, int resources)
    {
        var look = ModuleStyle;
        var composition = RockComposition.From(record.AsObservation(), resources);
        return look.CompositionBarHeight + 6 + look.CompositionRowHeight * Math.Max(1, composition.Rows) + DroneScanUi.RecordFooterHeight(look.DroneScan, _hologramState.On);
    }

    /// <summary>A resolved share the drones found, to a tenth of a percent: they resolve far finer than a scanner module.</summary>
    private static ReadOnlySpan<byte> DroneShareText(float share) =>
        share > 0f ? Kit.F($"{share * 100f:0.0} %") : "TRACE"u8;

    private static ReadOnlySpan<byte> DroneRockShareText(in RockComposition composition) =>
        composition.RockIsCeiling ? Kit.F($"UP TO {composition.Remainder * 100f:0.0} %") : Kit.F($"{composition.Remainder * 100f:0.0} %");

    /// <summary>
    /// What the target panel says of an asteroid the player's drones scanned, in place of a scanner module's analysis:
    /// the bulk composition at the drones' resolution, who found it, the veins and pockets it mapped and a hologram button. The mass is a module's and only shows when the player also has one.
    /// Returns the height used, in screen pixels.
    /// </summary>
    private float DrawDroneAnalysis(ImDrawListPtr draw, SessionSnapshot snapshot, RockRef rock, int index, float2 at, float width)
    {
        var look = ModuleStyle;
        var scan = look.DroneScan;
        var galaxy = snapshot.Galaxy!;
        ref var record = ref snapshot.DroneScans.Records[index];
        var composition = RockComposition.From(record.AsObservation(), galaxy.Resources.Length);
        var rowHeight = Kit.Px(look.CompositionRowHeight);
        var module = FindAnalysis(snapshot, rock);
        var mass = module >= 0 ? snapshot.Modules.Records[module].Mass : 0f;
        var y = at.Y;

        DrawCompositionBar(draw, galaxy, composition, new float2(at.X, y), width, Kit.Px(look.CompositionBarHeight), look);
        y += Kit.Px(look.CompositionBarHeight + 6);
        var massColumn = Kit.Measure(_ui.Mono, Theme.FontCaption, "0000.00 MT"u8).X;
        var massRight = at.X + width;
        var shareRight = massRight - (mass > 0f ? massColumn + Kit.Px(Theme.Space2) : 0f);
        if (composition.Count == 0)
        {
            CompositionRow(draw, "Bare rock"u8, look.RockRgb, default, mass > 0f ? MassText(mass) : default, new float2(at.X, y), width, shareRight, massRight);
            y += rowHeight;
        }
        else
        {
            for (var part = 0; part < composition.Count; part++)
            {
                var resource = galaxy.Resources[composition.ResourceOf(part)];
                var share = composition.ShareOf(part);
                CompositionRow(draw, Kit.F($"{resource.Name}"), StarColor(resource.Color), DroneShareText(share),
                    mass > 0f && share > 0f ? MassText(mass * share) : default, new float2(at.X, y), width, shareRight, massRight);
                y += rowHeight;
            }

            CompositionRow(draw, "Rock"u8, look.RockRgb, DroneRockShareText(composition),
                mass > 0f && !composition.RockIsCeiling ? MassText(mass * composition.Remainder) : default, new float2(at.X, y), width, shareRight, massRight);
            y += rowHeight;
        }

        // Who found it, and how long ago: the drones' own tier colour, as a module's quality shows on its analysis.
        var tier = look.Tier(ModuleQuality.AlienTech);
        var lineHeight = Kit.Px(scan.LineHeight);
        var age = ModuleAge.Seconds(record.RecordedTick, snapshot.ServerTick);
        var label = "DRONE SCAN"u8;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(at.X, y + Kit.Px(2)), tier.Rgb, label);
        var ageText = Kit.F($"FOUND {ModuleAge.Value(age):0} {ModuleAge.Unit(age)} AGO");
        var ageWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, ageText).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + width - ageWidth, y + Kit.Px(2)), Theme.TextMute, ageText);
        y += lineHeight;

        DroneScanUi.Pockets(record, out var pockets, out var unstable);
        var veins = (int)record.Map.VeinCount;
        var pocketText = Kit.F($"{pockets} {(pockets == 1 ? "POCKET" : "POCKETS")}, {unstable} UNSTABLE");
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(at.X, y + Kit.Px(2)), Theme.TextDim, Kit.F($"{veins} {(veins == 1 ? "VEIN" : "VEINS")}   {pocketText}"), width);
        y += lineHeight + Kit.Px(scan.Gap);

        var on = _hologramState.On;
        var buttonSize = new float2(Kit.Px(scan.HologramButtonWidth), Kit.Px(scan.ButtonHeight));
        var holoLabel = Kit.F($"{_hologramKey}  HOLOGRAM {(on ? "ON" : "OFF")}");
        if (ModuleButton("##scanholo"u8, new float2(at.X, y), buttonSize, holoLabel, true, on ? Theme.Info : Theme.LineStrong))
            _hologramState.Toggle();
        y += buttonSize.Y;
        if (on)
        {
            DrawHologramLegend(draw, new float2(at.X, y + Kit.Px(3)), width);
            y += lineHeight;
        }

        return y - at.Y;
    }

    /// <summary>The hologram's legend: veins, ore pockets, unstable pockets and easy-to-reach cells, each with its colour.</summary>
    private void DrawHologramLegend(ImDrawListPtr draw, float2 at, float width)
    {
        var scan = ScanLook;
        var x = at.X;
        LegendKey(draw, "VEINS"u8, scan.VeinKeyRgb, at.Y, ref x);
        LegendKey(draw, "ORE POCKET"u8, scan.OrePocketKeyRgb, at.Y, ref x);
        LegendKey(draw, "UNSTABLE"u8, scan.UnstableRgb, at.Y, ref x);
        LegendKey(draw, "EASY"u8, scan.EasyRgb, at.Y, ref x);
    }

    private void LegendKey(ImDrawListPtr draw, ReadOnlySpan<byte> word, uint rgb, float y, ref float cursor)
    {
        var side = Kit.Px(7);
        draw.AddRectFilled(new float2(cursor, y + Kit.Px(3)), new float2(cursor + side, y + Kit.Px(3) + side), Kit.Color(rgb));
        cursor += side + Kit.Px(4);
        cursor += Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(cursor, y), Theme.TextDim, word).X + Kit.Px(Theme.Space3);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The scan buttons
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The block of the target panel that sends the flown hull's drones to scan the targeted rock, as the scan key does:
    /// a drone scan button; while the hull's scan is of this rock a status button (MOVING 2/4, SCAN
    /// (READY) which orders it, SCANNING with its time) beside CANCEL; and under them a line for the server's answer or
    /// a hint. A pointer captured by flight presses nothing, so the key does the same.
    /// </summary>
    private void DrawDroneScanButtons(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, RockRef rock, float2 at, float width)
    {
        var look = ScanLook;
        var bays = snapshot.Bays;
        var own = world.Own.Id;
        var y = at.Y + Kit.Px(look.Gap);
        var size = new float2(width, Kit.Px(look.ButtonHeight));
        var gap = Kit.Px(Theme.Space2);
        var half = new float2((width - gap) * 0.5f, size.Y);
        var onRock = DroneScanUi.OnRock(bays, rock);
        var idle = bays.ScanPhase == DroneScanPhase.None;
        var bridge = Bridge;
        if (idle)
        {
            if (ModuleButton("##scandrones"u8, new float2(at.X, y), size, Kit.F($"{_droneScanKey}  DRONE SCAN"), true, Theme.Accent) && bridge is not null)
                DroneScanUi.Press(bays, own, rock, bridge);
        }
        else if (onRock)
        {
            var phase = bays.ScanPhase;
            var key = _droneScanKey;
            var label = phase switch
            {
                DroneScanPhase.Moving => Kit.F($"MOVING {bays.ScanInPlace}/{bays.ScanDrones}"),
                DroneScanPhase.Ready => Kit.F($"{key}  SCAN (READY)"),
                DroneScanPhase.Scanning => Kit.F($"SCANNING {DroneScanUi.SecondsLeft(bays, snapshot.ServerTick):0.0} S"),
                _ => "RETURNING"u8,
            };
            var ready = phase == DroneScanPhase.Ready;
            if (ModuleButton("##scanstatus"u8, new float2(at.X, y), half, label, ready, ready ? Theme.Good : Theme.LineStrong) && bridge is not null)
                DroneScanUi.StartIfReady(bays, own, bridge);
            var active = DroneScanUi.Active(bays);
            if (ModuleButton("##scancancel"u8, new float2(at.X + half.X + gap, y), half, "CANCEL SCAN"u8, active, Theme.Bad) && bridge is not null)
                DroneScanUi.CancelIfActive(bays, own, bridge);
        }
        else
        {
            ModuleButton("##scanelsewhere"u8, new float2(at.X, y), size, bays.ScanPhase == DroneScanPhase.Returning ? "DRONES RETURNING"u8 : "SCAN UNDER WAY ON ANOTHER ROCK"u8, false, Theme.LineStrong);
        }

        y += size.Y + Kit.Px(look.Gap);
        var line = new float2(at.X, y + Kit.Px(1));
        var speaking = _droneTrack.Speaking(ModuleStyle.Strip.ResultSeconds);
        if (speaking != DroneReply.None && DroneUi.IsScanReply(speaking) && (DroneUi.Refused(speaking) || speaking is DroneReply.ScanDone or DroneReply.ScanCancelled))
        {
            var needed = bays.ReplyCount;
            var words = speaking switch
            {
                DroneReply.TooLarge when needed > 0 => Kit.F($"ROCK TOO LARGE: NEEDS {needed} DRONES"),
                DroneReply.TooFew when needed > 0 => Kit.F($"TOO FEW DRONES: NEEDS {needed}"),
                _ => DroneUi.Words(speaking),
            };
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, line, DroneReplyColour(speaking), words, width);
        }
        else if (onRock && bays.ScanPhase == DroneScanPhase.Scanning)
        {
            DrawFractionBar(draw, line + new float2(0, Kit.Px(4)), width, Kit.Px(6), DroneScanUi.Progress(bays, snapshot.ServerTick), Theme.Info);
        }
        else
        {
            ReadOnlySpan<byte> hint = !onRock && !idle ? default
                : idle ? "Drones map the rock: bulk, veins and pockets."u8
                : bays.ScanPhase == DroneScanPhase.Ready ? "Every drone is in place: scan when ready."u8
                : bays.ScanPhase == DroneScanPhase.Moving ? "Drones are moving into place."u8
                : default;
            Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, line, Theme.TextMute, hint, width);
        }
    }

    /// <summary>The colour an answer is spoken in: a refusal in the warning colour, a cancelled scan dim, the rest in the good one.</summary>
    private static uint DroneReplyColour(DroneReply reply) => DroneUi.Refused(reply) ? Theme.Bad : reply == DroneReply.ScanCancelled ? Theme.TextDim : Theme.Good;

    // ---------------------------------------------------------------------------------------------------------------
    // The hologram
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The drone scan's hologram over the rock targeted (the one selected, in the command view) and over the rock
    /// the hull's scan is of, while it is on: veins in their ore's colour, pockets as circles, the heat map as dots on
    /// the surface, each dimmer on the far side of the rock. Drawn in the flight and command views from the record
    /// alone and the rock's pose; allocation-free (fixed buffers, <see cref="ScanHologram"/>).
    /// </summary>
    private void DrawScanHologram(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        if (!_hologramState.On || snapshot.Galaxy is null || snapshot.DroneScans.Count == 0) return;
        var target = ScannableRock(world);
        var scan = ScanRockOf(snapshot.Bays);
        if (target is { } t) DrawScanHologramOf(draw, snapshot, world, t, _hologramTarget, alpha);
        if (scan is { } s && s != target) DrawScanHologramOf(draw, snapshot, world, s, _hologramScan, alpha);
    }

    private void DrawScanHologramOf(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, RockRef rock, ScanHologram hologram, float alpha)
    {
        var index = snapshot.DroneScans.Find(rock);
        if (index < 0 || !world.TryGetRockPose(rock, out var position, out var rotation, out _)) return;
        var look = ScanLook;
        if (!hologram.Build(snapshot.DroneScans.Records[index], world.HologramView, position, rotation, look)) return;
        var resources = snapshot.Galaxy!.Resources;
        var previous = Kit.Fade;
        Kit.Fade = previous * alpha;

        // The far side first, so what is in front of the rock is drawn over what is behind it.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < hologram.SegmentCount; i++)
            {
                ref var segment = ref hologram.Segments[i];
                if ((segment.Strength < 0.65f ? 0 : 1) != pass) continue;
                var rgb = (uint)segment.Resource < (uint)resources.Length ? StarColor(resources[segment.Resource].Color) : look.VeinKeyRgb;
                var colour = Kit.Color(rgb, look.VeinAlpha * segment.Strength);
                var a = new float2(segment.A.X, segment.A.Y);
                var b = new float2(segment.B.X, segment.B.Y);
                draw.AddLine(a, b, colour, segment.Width);
                if (segment.Width >= 3f)
                {
                    draw.AddCircleFilled(a, segment.Width * 0.5f, colour, 12);
                    draw.AddCircleFilled(b, segment.Width * 0.5f, colour, 12);
                }
            }

            for (var i = 0; i < hologram.PocketCount; i++)
            {
                ref var pocket = ref hologram.Pockets[i];
                if ((pocket.Strength < 0.65f ? 0 : 1) != pass) continue;
                var centre = new float2(pocket.Centre.X, pocket.Centre.Y);
                var rgb = pocket.Unstable ? look.UnstableRgb : (uint)pocket.Resource < (uint)resources.Length ? StarColor(resources[pocket.Resource].Color) : look.OrePocketKeyRgb;
                draw.AddCircleFilled(centre, pocket.Radius, Kit.Color(rgb, look.PocketFillAlpha * pocket.Strength), 24);
                draw.AddCircle(centre, pocket.Radius, Kit.Color(rgb, look.PocketRingAlpha * pocket.Strength), 24, Kit.Px(1.5f));
                if (pocket.Unstable)
                    draw.AddCircle(centre, pocket.Radius + Kit.Px(look.UnstableRingPixels), Kit.Color(look.UnstableRgb, look.PocketRingAlpha * pocket.Strength * 0.8f), 24, Kit.Px(1.2f));
            }

            for (var i = 0; i < hologram.DotCount; i++)
            {
                ref var dot = ref hologram.Dots[i];
                if ((dot.Strength < 0.65f ? 0 : 1) != pass) continue;
                var at = new float2(dot.At.X, dot.At.Y);
                var radius = Kit.Px(look.HeatDotPixels);
                switch (dot.Kind)
                {
                    case HologramDot.Unstable:
                        draw.AddCircleFilled(at, radius * 1.3f, Kit.Color(look.UnstableRgb, dot.Strength), 10);
                        break;
                    case HologramDot.Easy:
                        draw.AddCircleFilled(at, radius * (0.8f + 0.5f * dot.Ease), Kit.Color(look.EasyRgb, (0.4f + 0.6f * dot.Ease) * dot.Strength), 10);
                        break;
                    default:
                        draw.AddCircleFilled(at, radius * 0.8f, Kit.Color(look.FaintRgb, look.HeatFaintAlpha * dot.Strength), 8);
                        break;
                }
            }
        }

        Kit.Fade = previous;
    }
}
