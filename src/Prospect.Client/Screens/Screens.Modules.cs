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
/// The ship modules in the interface: their keys and chips, the asteroid analysis on the target panel, the
/// locator's vision over the view, and the gas the player has sampled. Everything shown is the player's own
/// knowledge from <see cref="SessionSnapshot.Modules"/>; a request goes through <see cref="PlayerInputBridge"/>
/// and the server decides. Nothing here allocates: text is formatted through <see cref="Kit.F"/> and every list is
/// preallocated.
/// </summary>
internal sealed partial class ScreenHost
{
    private readonly ModuleTracker _moduleTrack = new();
    private readonly GasTargeting _gasTarget = new();
    private readonly GasLedger _gasLedger = new();
    private readonly ModuleGate[] _moduleGate = new ModuleGate[3];
    private readonly ModuleEquipment[] _moduleEquipment = new ModuleEquipment[3];
    private readonly byte[][] _moduleKeyNames = [[], [], []];
    private readonly string[] _moduleKeyStrings = ["", "", ""];
    private int _moduleKeysVersion = -1;
    private ModuleTargets _moduleTargets;

    /// <summary>The galaxy of this frame's snapshot, for the locator filter's fitting and the popup's ore list.</summary>
    private Galaxy? _galaxyThisFrame;
    private readonly DroneTracker _droneTrack = new();
    private readonly DronePopup _dronePopup = new();
    private bool _dronePopupWanted;
    private readonly LocatorPopup _locatorPopup = new();
    private bool _locatorPopupWanted;

    /// <summary>Opens the drone settings popup as soon as the flown hull has a chip for it. Used by <c>--drone-popup</c>, for captures.</summary>
    public void RequestDronePopup() => _dronePopupWanted = true;

    /// <summary>Opens the advanced locator's filter popup as soon as the flown hull carries one. Used by <c>--locator-popup</c>, for captures.</summary>
    public void RequestLocatorPopup() => _locatorPopupWanted = true;
    private DroneChipState _droneState;
    private byte[] _droneKeyName = [];
    private byte[] _droneRecallKeyName = [];
    private byte[] _droneFetchKeyName = [];
    private int _droneKeysVersion = -1;
    private bool _tipSawGasScanner;
    private bool _tipSawAsteroidTarget;
    private bool _tipSawLocator;

    /// <summary>The ModuleLook, read once.</summary>
    private static ModuleLook ModuleStyle => ModuleLook.Current;

    // ---------------------------------------------------------------------------------------------------------------
    // State, keys and requests
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Works out, once a frame, what the ship being flown carries, what each module would meet now (cooldown, target,
    /// reach) and the knowledge lists the windows read. Called with the game on screen, before anything is drawn.
    /// </summary>
    private void UpdateModules(SessionSnapshot snapshot, WorldView world)
    {
        var own = world.Own;
        var galaxy = snapshot.Galaxy;
        _galaxyThisFrame = galaxy;
        var stats = own.Stats;
        for (var slot = 0; slot < 3; slot++)
            _moduleEquipment[slot] = own.Id == 0 ? default : stats.Module(ModuleRules.Families[slot]);

        _gasLedger.Update(snapshot.Modules, galaxy);
        var gas = _moduleEquipment[0];
        _gasTarget.Update(galaxy, world.OwnPosition, gas.Mount != 0 ? gas.Range : 0, world.Time);
        var targets = new ModuleTargets { HasCloud = _gasTarget.Has, Cloud = _gasTarget.Anchor, CloudInRange = gas.Mount != 0 && _gasTarget.InRange(gas.Range) };
        var asteroid = _moduleEquipment[1];
        if (asteroid.Mount != 0 && ScannableRock(world) is { } rock && world.TryGetRock(rock, out var rockAt, out var rockRadius))
        {
            targets.Rock = rock;
            targets.RockInRange = ModuleScanRules.InRange(world.OwnPosition, rockAt, rockRadius, asteroid.Range);
        }

        _moduleTargets = targets;
        UpdateDroneScans(snapshot, world);
        _droneState = DroneUi.State(snapshot.Bays, own.Id);
        _droneTrack.Update(snapshot.Bays, _ui.DeltaTime);
        _dronePopup.Follow(own.Id, _droneState != DroneChipState.Absent);
        if (_dronePopupWanted && _droneState != DroneChipState.Absent)
        {
            _dronePopupWanted = false;
            if (!_dronePopup.IsOpen) _dronePopup.Toggle();
        }
        // The advanced locator's popup belongs to a hull that carries one; the basic locator has nothing to filter.
        _locatorPopup.Follow(own.Id, HasAdvancedLocator);
        if (_locatorPopupWanted && HasAdvancedLocator)
        {
            _locatorPopupWanted = false;
            if (!_locatorPopup.IsOpen) _locatorPopup.Toggle();
        }
        if (_droneKeysVersion != _app.Actions.Version)
        {
            _droneKeysVersion = _app.Actions.Version;
            var launch = _app.Actions.Bindings(InputAction.LaunchDrones);
            _droneKeyName = System.Text.Encoding.UTF8.GetBytes(launch.Length == 0 ? "UNBOUND" : launch[0].DisplayName.ToUpperInvariant());
            var recall = _app.Actions.Bindings(InputAction.RecallDrones);
            _droneRecallKeyName = System.Text.Encoding.UTF8.GetBytes(recall.Length == 0 ? "UNBOUND" : recall[0].DisplayName.ToUpperInvariant());
            var fetch = _app.Actions.Bindings(InputAction.FetchContainer);
            _droneFetchKeyName = System.Text.Encoding.UTF8.GetBytes(fetch.Length == 0 ? "UNBOUND" : fetch[0].DisplayName.ToUpperInvariant());
        }
        var scan = snapshot.Modules.Scan;
        _moduleTrack.Update(scan, own.Id, (float)_moduleEquipment[2].Range, _ui.DeltaTime);
        for (var slot = 0; slot < 3; slot++)
            _moduleGate[slot] = ModuleRules.Gate(ModuleRules.Families[slot], _moduleEquipment[slot], scan, own.Id, snapshot.ServerTick, targets);
        if (_moduleKeysVersion != _app.Actions.Version)
        {
            _moduleKeysVersion = _app.Actions.Version;
            for (var slot = 0; slot < 3; slot++)
            {
                var bindings = _app.Actions.Bindings(ModuleRules.ActionOf(ModuleRules.Families[slot]));
                _moduleKeyStrings[slot] = bindings.Length == 0 ? "UNBOUND" : bindings[0].DisplayName.ToUpperInvariant();
                _moduleKeyNames[slot] = System.Text.Encoding.UTF8.GetBytes(_moduleKeyStrings[slot]);
            }
        }
    }

    /// <summary>Whether the ship being flown carries the advanced locator, whose pulse takes a filter.</summary>
    private bool HasAdvancedLocator => _moduleEquipment[2].Mount != 0 && _moduleEquipment[2].Filter;

    /// <summary>
    /// The advanced locator's filter as the player left it, fitted to this world: a range inside the controls' own
    /// and only ores this galaxy has. Read every frame, so a file edited by hand or a world of another seed can never
    /// send a filter the server would refuse.
    /// </summary>
    private LocatorFilter LocatorFilterNow(Galaxy? galaxy)
    {
        var look = ModuleStyle.Filter;
        var filter = _app.Settings.LocatorFilter;
        var widest = (ushort)Math.Min(look.MaxDiameterMetres, LocatorFilter.MaxDiameterMetres);
        var max = filter.MaxMetres > widest ? widest : filter.MaxMetres;
        var min = filter.MaxMetres != 0 && filter.MinMetres > max ? max : filter.MinMetres;
        var ores = galaxy is null ? filter.Ores : filter.Ores & LocatorFilter.AllOres(galaxy);
        return new LocatorFilter(min, max, ores);
    }

    /// <summary>Keeps what the popup changed in the settings, and writes them when the popup is let go.</summary>
    private void SetLocatorFilter(in LocatorFilter filter, bool save)
    {
        _app.Settings.SetLocatorFilter(filter);
        if (save && !_app.Devices.Muted)
            _app.Settings.Save();
    }

    /// <summary>The asteroid the scanner would scan: the one targeted in flight, or the first one selected in the command view.</summary>
    private static RockRef? ScannableRock(WorldView world) =>
        world.Mode == ViewMode.Command && world.SelectedRocks.Length > 0 ? world.SelectedRocks[0] : world.TargetRock;

    /// <summary>
    /// The module keys. They work in the flight and command views, never over the editor, a dialog, a menu, the map,
    /// the controls page or while typing; a ready module asks the server, one that cannot says why on its chip.
    /// </summary>
    private void HandleModuleKeys(PlayerInputBridge bridge, SessionSnapshot snapshot)
    {
        DroneUi.HandleKeys(_app.Actions, _app.Devices, !_ui.WantsTextInput, snapshot.Bays, _app.World.Own.Id, bridge,
            DroneUi.FetchSubject(_app.World.HoverContainerId, _app.World.TargetContainer));
        HandleDroneScanKeys(bridge, snapshot, _app.World);
        for (var slot = 0; slot < 3; slot++)
        {
            var family = ModuleRules.Families[slot];
            if (_moduleGate[slot] == ModuleGate.Absent || !_app.Actions.Pressed(_app.Devices, ModuleRules.ActionOf(family), !_ui.WantsTextInput))
                continue;
            RequestModule(slot, bridge);
        }
    }

    /// <summary>Asks for a module's scan as its key or chip does, and puts a known refusal on the chip.</summary>
    private void RequestModule(int slot, PlayerInputBridge bridge)
    {
        var family = ModuleRules.Families[slot];
        var notice = ModuleRules.Request(family, _moduleGate[slot], _moduleTargets, bridge,
            slot == 2 ? LocatorFilterNow(_galaxyThisFrame) : default);
        if (notice != ModuleNotice.None)
            _moduleTrack.Note(family, notice);
    }

    /// <summary>
    /// How many rows the map index adds for the locator's classes: all of them while the ship flown has a locator or the
    /// player knows any locator mark, none otherwise, so the index does not explain what the player cannot have.
    /// </summary>
    private int LocatorLegendRows(SessionSnapshot snapshot)
    {
        if (_moduleGate[2] != ModuleGate.Absent)
            return ModuleStyle.Classes.Length;
        var modules = snapshot.Modules;
        for (var i = 0; i < modules.Count; i++)
            if (modules.Records[i].Family == ModuleFamily.AsteroidLocator)
                return ModuleStyle.Classes.Length;
        return 0;
    }

    /// <summary>
    /// Runs a module as its key would, for the scripted captures: true once the request went out, false while the
    /// module cannot (it is absent, cooling, has no target or cloud in reach) so the caller tries again next frame.
    /// </summary>
    public bool RunModuleScript(string family)
    {
        var slot = family switch { "gas" => 0, "asteroid" => 1, _ => 2 };
        if (_moduleGate[slot] != ModuleGate.Ready || Bridge is not { } bridge)
            return false;
        RequestModule(slot, bridge);
        return true;
    }

    /// <summary>Chooses the part list's tab by its name. Used by <c>--open modules</c>, for captures.</summary>
    public void SelectEditorTab(string name)
    {
        var categories = Tuning.Categories;
        for (var i = 0; i < categories.Length; i++)
            if (categories[i].Name == name)
                _palette.Category = i;
    }

    /// <summary>Types <paramref name="text"/> into the part list's search, for captures (<c>--open edit-laser</c>): a long tab's card is then in view without a scroll.</summary>
    public void SearchEditorParts(string text) => _palette.Query = text;

    /// <summary>Opens the GAS window in interface mode, or with <paramref name="pinned"/> pins it over the flight view. Used by <c>--open gas</c> and <c>pinned-gas</c>.</summary>
    public void OpenGasWindow(bool pinned)
    {
        OpenPane(SocialPane.Gas);
        if (!pinned)
            return;
        Pane(SocialPane.Gas).Pinned = true;
        CloseWorkspace();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The strip over the console
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>How many modules the ship being flown has.</summary>
    private int ModulesPresent()
    {
        var n = 0;
        for (var slot = 0; slot < 3; slot++)
            n += _moduleGate[slot] != ModuleGate.Absent ? 1 : 0;
        return n;
    }

    /// <summary>Chips in the row over the console: one a module, and one for the drone bays when the ship has any.</summary>
    private int StripChips() => ModulesPresent() + (_droneState != DroneChipState.Absent ? 1 : 0);

    /// <summary>
    /// Where the chip row is for <paramref name="present"/> modules: centred over the console. The chips and the
    /// tips that keep clear of them (<see cref="TipRegion.Console"/>) both read it, so they cannot disagree.
    /// </summary>
    internal static HudRect StripRect(float2 display, float scale, int present, ModuleLook.StripLook strip)
    {
        float Px(float design) => MathF.Round(design * scale);
        var layout = FlightConsole.Layout(display, scale);
        var width = Px(strip.ChipWidth);
        var height = Px(strip.ChipHeight);
        var total = width * present + Px(strip.Gap) * (present - 1);
        var left = MathF.Round(layout.CenterX - total * 0.5f);
        var top = layout.Top - height - Px(Theme.Space3);
        return new HudRect(left, top, left + total, top + height);
    }

    /// <summary>
    /// A chip for each module the ship being flown carries, in a row over the console's speed readout: the key, the
    /// module, its tier colour, and what it would do now (ready, running with its time left, cooling, or why not).
    /// Clicking a chip does what the key does, where the pointer is free. Nothing is drawn for a module the ship does
    /// not have, and nothing at all without one.
    /// </summary>
    private void DrawModuleStrip(ImDrawListPtr background, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var present = StripChips();
        if (present == 0)
            return;
        var look = ModuleStyle;
        var strip = look.Strip;
        var width = Kit.Px(strip.ChipWidth);
        var height = Kit.Px(strip.ChipHeight);
        var gap = Kit.Px(strip.Gap);
        var rect = StripRect(_ui.DisplaySize, Kit.Scale, present, strip);
        var origin = new float2(rect.Left, rect.Top);
        var size = new float2(rect.Width, rect.Height);
        ImGui.SetNextWindowPos(origin);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##modulestrip"u8, RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var draw = ImGui.GetWindowDrawList();
        var previous = Kit.Fade;
        Kit.Fade = alpha;
        var shown = 0;
        var hovered = -1;
        var hoveredMin = default(float2);
        var locatorChipMin = default(float2);
        var locatorChipShown = false;
        for (var slot = 0; slot < 3; slot++)
        {
            if (_moduleGate[slot] == ModuleGate.Absent)
                continue;
            var min = origin + new float2((width + gap) * shown++, 0);
            if (slot == 2)
            {
                locatorChipMin = min;
                locatorChipShown = HasAdvancedLocator;
            }
            ImGui.PushID(slot);
            ImGui.SetCursorScreenPos(min);
            var pressed = ImGui.InvisibleButton("##chip"u8, new float2(width, height));
            var hot = ImGui.IsItemHovered();
            ImGui.PopID();
            DrawModuleChip(draw, slot, min, new float2(width, height), hot, snapshot);
            if (hot)
            {
                hovered = slot;
                hoveredMin = min;
            }

            if (pressed && Bridge is { } bridge)
            {
                // The advanced locator's chip opens its filter popup, as the DRONES chip opens the drone settings; the
                // popup's own button pulses with the filter. The basic locator's chip pulses, as it always did.
                if (slot == 2 && HasAdvancedLocator)
                    _locatorPopup.Toggle();
                else
                    RequestModule(slot, bridge);
            }
        }

        // The bays' chip comes last; a click opens the drone settings popup over it (a click again closes it).
        var droneChipMin = default(float2);
        var droneChipShown = false;
        if (_droneState != DroneChipState.Absent)
        {
            var min = origin + new float2((width + gap) * shown, 0);
            ImGui.PushID(3);
            ImGui.SetCursorScreenPos(min);
            var pressed = ImGui.InvisibleButton("##chip"u8, new float2(width, height));
            var hot = ImGui.IsItemHovered();
            ImGui.PopID();
            DrawDroneChip(draw, min, new float2(width, height), hot, snapshot);
            droneChipMin = min;
            droneChipShown = true;
            if (pressed) _dronePopup.Toggle();
        }

        Kit.Fade = previous;
        ImGui.End();
        if (droneChipShown && _dronePopup.IsOpen)
            DrawDronePopup(snapshot, world, droneChipMin, new float2(width, height));
        if (_locatorPopup.IsOpen && locatorChipShown)
            DrawLocatorPopup(snapshot, world, locatorChipMin, new float2(width, height));
        if (hovered >= 0)
            DrawModuleTip(ImGui.GetForegroundDrawList(), hovered, hoveredMin, new float2(width, height), snapshot);
    }

    /// <summary>
    /// The bays' chip: the key that acts now and what it does (launch while drones are inside, recall while any are
    /// out), then drones out and inside, a rebuild, or the server's answer.
    /// </summary>
    private void DrawDroneChip(ImDrawListPtr draw, float2 min, float2 size, bool hot, SessionSnapshot snapshot)
    {
        var look = ModuleStyle;
        var tier = look.Tier(ModuleQuality.Common);
        var max = min + size;
        var bays = snapshot.Bays;
        bays.Totals(out var stocked, out var outside, out var pending, out var capacity);
        var reply = _droneTrack.Speaking(look.Strip.ResultSeconds);
        var previous = Kit.Fade;
        Kit.Fade = previous * (hot ? 1f : _droneState == DroneChipState.Empty ? look.Strip.IdleAlpha : look.Strip.ReadyAlpha);
        Hud.Plate(draw, min, max, tier.Rgb, hot);
        draw.AddRectFilled(min + new float2(0, Kit.Px(4)), min + new float2(Kit.Px(3), size.Y - Kit.Px(4)), Kit.Color(tier.Rgb));
        var pad = Kit.Px(Theme.Space2) + Kit.Px(3);
        var inner = size.X - pad - Kit.Px(Theme.Space2);
        var recalling = _droneState == DroneChipState.Out;
        var key = recalling ? _droneRecallKeyName : _droneKeyName;
        var keyWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, key).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(pad, Kit.Px(4)), Theme.Accent, key);
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, min + new float2(pad + keyWidth + Kit.Px(Theme.Space2), Kit.Px(4)), Theme.Text, recalling ? "RECALL"u8 : "LAUNCH"u8, Math.Max(0f, inner - keyWidth - Kit.Px(Theme.Space2)));
        var statusAt = min + new float2(pad, Kit.Px(20));
        var rebuild = DroneUi.RebuildSeconds(bays, snapshot.ServerTick);
        // A scan under way speaks for itself (where the drones are, ready, the time left) unless the server just refused
        // something: the refusal says why a key did nothing.
        var scanning = DroneScanUi.Active(bays) || bays.ScanPhase == DroneScanPhase.Returning;
        if (reply != DroneReply.None && (DroneUi.Refused(reply) || !scanning))
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, DroneReplyColour(reply), DroneUi.Words(reply), inner);
        else if (scanning)
        {
            switch (bays.ScanPhase)
            {
                case DroneScanPhase.Moving:
                    Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.Info, Kit.F($"SCAN {bays.ScanInPlace}/{bays.ScanDrones}"), inner);
                    break;
                case DroneScanPhase.Ready:
                    Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.Good, "SCAN READY"u8, inner);
                    break;
                case DroneScanPhase.Scanning:
                    Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.Info, Kit.F($"SCANNING {DroneScanUi.SecondsLeft(bays, snapshot.ServerTick):0.0} S"), inner);
                    break;
                default:
                    Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.TextDim, "SCAN DONE"u8, inner);
                    break;
            }
        }
        else if (bays.Fetching > 0)
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.Info, Kit.F($"{bays.Fetching} FETCHING  {stocked} IN"), inner);
        else if (rebuild <= 0 && _app.World.OreChunksNear is > 0 and var chunks)
            // Loose ore close to the ship: the one line that says there is something to collect (see OreChunkLook.NearMetres).
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.Good, Kit.F($"{chunks} {(chunks == 1 ? "CHUNK" : "CHUNKS")} NEAR"), inner);
        else if (_droneState == DroneChipState.Out)
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.Info, Kit.F($"{outside} DRONES OUT  {stocked} IN"), inner);
        else if (_droneState == DroneChipState.Ready)
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, pending > 0 ? Theme.Info : Theme.Good, Kit.F($"{stocked}/{capacity} DRONES IN"), inner);
        else if (rebuild > 0)
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.TextDim, Kit.F($"REBUILD {rebuild} S"), inner);
        else
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.TextMute, "EMPTY"u8, inner);

        // A running scan shows as a bar along the foot, filling as it nears its end; so does a drone being rebuilt.
        if (bays.ScanPhase == DroneScanPhase.Scanning)
        {
            var track = new float2(min.X + Kit.Px(3), max.Y - Kit.Px(4));
            draw.AddRectFilled(track, new float2(max.X - Kit.Px(1), max.Y - Kit.Px(1)), Kit.Color(Theme.PanelRaised, 0.9f));
            draw.AddRectFilled(track, new float2(track.X + (max.X - Kit.Px(1) - track.X) * DroneScanUi.Progress(bays, snapshot.ServerTick), max.Y - Kit.Px(1)), Kit.Color(Theme.Info));
        }
        else if (rebuild > 0 && bays.NextRebuild() is var done and > 0)
        {
            var full = Math.Max(1f, MaxRebuildTicks(snapshot));
            var progress = Math.Clamp(1f - (done - snapshot.ServerTick) / full, 0f, 1f);
            var track = new float2(min.X + Kit.Px(3), max.Y - Kit.Px(4));
            draw.AddRectFilled(track, new float2(max.X - Kit.Px(1), max.Y - Kit.Px(1)), Kit.Color(Theme.PanelRaised, 0.9f));
            draw.AddRectFilled(track, new float2(track.X + (max.X - Kit.Px(1) - track.X) * progress, max.Y - Kit.Px(1)), Kit.Color(Theme.TextDim));
        }
        Kit.Fade = previous;
    }

    /// <summary>Rows of the popup's status block: counts, rebuild, last answer.</summary>
    private const int DronePopupStatusLines = 3;

    /// <summary>
    /// The drone settings popup, over the DRONES chip: one Launch/Recall button (the keys' own action), the
    /// "Pick up cargo" and "Pick up ores" checkboxes (the server's settings, read back from the view) and three status lines. It
    /// only takes presses while the pointer is shown; a press anywhere else closes it (<see cref="DronePopup.Press"/>).
    /// A fixed height keeps its place and the outside-press test from depending on what the lines say.
    /// </summary>
    private void DrawDronePopup(SessionSnapshot snapshot, WorldView world, float2 chipMin, float2 chipSize)
    {
        var bays = snapshot.Bays;
        var pad = Kit.Px(Theme.Space3);
        var gap = Kit.Px(Theme.Space2);
        var line = Kit.Px(16);
        var width = Kit.Px(244);
        var height = pad * 2 + line + gap + Kit.Px(Theme.ControlHeight) + gap + Kit.Px(24) + Kit.Px(24) + gap + line * DronePopupStatusLines
            + gap + line + gap + Kit.Px(Theme.ControlHeight);
        var display = _ui.DisplaySize;
        var edge = Kit.Px(Theme.Space2);
        var min = new float2(
            Math.Clamp(chipMin.X, edge, Math.Max(edge, display.X - width - edge)),
            Math.Max(edge, chipMin.Y - gap - height));
        var size = new float2(width, height);

        var pointer = ImGui.GetMousePos();
        var overPopup = pointer.X >= min.X && pointer.X < min.X + size.X && pointer.Y >= min.Y && pointer.Y < min.Y + size.Y;
        var overChip = pointer.X >= chipMin.X && pointer.X < chipMin.X + chipSize.X && pointer.Y >= chipMin.Y && pointer.Y < chipMin.Y + chipSize.Y;
        _dronePopup.Press(ImGui.IsMouseClicked(ImGuiMouseButton.Left), overPopup, overChip);
        if (!_dronePopup.IsOpen)
            return;

        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##dronepopup"u8, RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        RaiseIfAppearing();
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        var inner = size.X - pad * 2;
        var at = min + new float2(pad, pad);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, at, Theme.TextMute, "DRONES"u8);
        at.Y += line + gap;

        var own = world.Own.Id;
        bays.Totals(out var stocked, out var outside, out var pending, out var capacity);
        var recalling = outside > 0;
        ImGui.SetCursorScreenPos(at);
        if (Kit.Button(recalling ? "Recall drones"u8 : "Launch drones"u8, ButtonKind.Primary, inner / Kit.Scale) && Bridge is { } bridge)
            DronePopup.LaunchOrRecall(bays, own, bridge);
        at.Y += Kit.Px(Theme.ControlHeight) + gap;

        ImGui.SetCursorScreenPos(at);
        if (EditorCheckbox("##autopickup"u8, "Pick up cargo"u8, new float2(inner, Kit.Px(24)), true, bays.AutoPickup) && Bridge is { } settings)
            DronePopup.ToggleAutoPickup(bays, own, settings);
        at.Y += Kit.Px(24);

        // Ore pickup: the second flag beside auto pickup, read back from the server like it.
        ImGui.SetCursorScreenPos(at);
        if (EditorCheckbox("##pickupores"u8, "Pick up ores"u8, new float2(inner, Kit.Px(24)), true, bays.PickUpOres) && Bridge is { } oreSettings)
            DronePopup.ToggleOres(bays, own, oreSettings);
        at.Y += Kit.Px(24) + gap;

        Kit.Text(draw, _ui.Mono, Theme.FontCaption, at, Theme.Info, Kit.F($"{stocked} IN  {outside} OUT  {bays.Fetching} FETCHING"));
        at.Y += line;
        var rebuild = DroneUi.RebuildSeconds(bays, snapshot.ServerTick);
        if (rebuild > 0)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim, Kit.F($"NEXT DRONE IN {rebuild} S"));
        else if (pending > 0)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim, Kit.F($"{pending} LAUNCHING"));
        else if (stocked >= capacity)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim, "BAYS FULL"u8);
        at.Y += line;
        var last = _droneTrack.Latest;
        if (last != DroneReply.None)
        {
            const string Label = "LAST  ";
            var labelWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, Label).X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextMute, Label);
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(labelWidth, 0), DroneReplyColour(last), DroneUi.Words(last), Math.Max(0f, inner - labelWidth));
        }
        at.Y += line + gap;

        // The drone scan: where it stands, a button to order it once the drones are in place, and one to give it up.
        var ready = bays.ScanPhase == DroneScanPhase.Ready;
        var active = DroneScanUi.Active(bays);
        var scanText = bays.ScanPhase switch
        {
            DroneScanPhase.Moving => Kit.F($"DRONE SCAN  {bays.ScanInPlace}/{bays.ScanDrones} IN PLACE"),
            DroneScanPhase.Ready => Kit.F($"DRONE SCAN READY  {bays.ScanDrones} DRONES"),
            DroneScanPhase.Scanning => Kit.F($"SCANNING  {DroneScanUi.SecondsLeft(bays, snapshot.ServerTick):0.0} S LEFT"),
            DroneScanPhase.Returning => "SCAN OVER  DRONES RETURNING"u8,
            _ => "NO SCAN UNDER WAY"u8,
        };
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at, ready || active ? Theme.Info : Theme.TextMute, scanText, inner);
        at.Y += line + gap;
        var half = (inner / Kit.Scale - Theme.Space2) * 0.5f;
        ImGui.SetCursorScreenPos(at);
        if (Kit.Button("Scan"u8, ButtonKind.Primary, half, ready) && Bridge is { } scanBridge)
            DroneScanUi.StartIfReady(bays, own, scanBridge);
        ImGui.SetCursorScreenPos(at + new float2(Kit.Px(half + Theme.Space2), 0));
        if (Kit.Button("Cancel scan"u8, ButtonKind.Secondary, half, active) && Bridge is { } cancelBridge)
            DroneScanUi.CancelIfActive(bays, own, cancelBridge);
        ImGui.End();
    }

    /// <summary>
    /// The advanced locator's filter popup, over the LOCATOR chip: a diameter bound on a row each, the ores of the
    /// galaxy with ALL and NONE, and the pulse button that sends the filter as it stands. It only takes presses while
    /// the pointer is shown, and a press anywhere else closes it (<see cref="LocatorPopup.Press"/>). Every control
    /// writes the client's own preference; the server is told the filter with the pulse and nothing else.
    /// </summary>
    private void DrawLocatorPopup(SessionSnapshot snapshot, WorldView world, float2 chipMin, float2 chipSize)
    {
        var look = ModuleStyle;
        var figures = look.Filter;
        var galaxy = snapshot.Galaxy;
        var resources = Math.Min(galaxy?.Resources.Length ?? 0, LocatorFilter.MaxResources);
        var pad = Kit.Px(Theme.Space3);
        var gap = Kit.Px(Theme.Space2);
        var line = Kit.Px(16);
        var control = Kit.Px(Theme.ControlHeight);
        var row = Kit.Px(figures.RowHeight);
        var width = Kit.Px(figures.Width);
        var perRow = Math.Max(1, figures.OresPerRow);
        var oreRows = (resources + perRow - 1) / perRow;
        var height = pad * 2 + line + gap + control * 2 + gap + control + gap + oreRows * row + gap + control + gap + line;
        var display = _ui.DisplaySize;
        var edge = Kit.Px(Theme.Space2);
        var min = new float2(
            Math.Clamp(chipMin.X, edge, Math.Max(edge, display.X - width - edge)),
            Math.Clamp(chipMin.Y - gap - height, edge, Math.Max(edge, display.Y - height - edge)));
        var size = new float2(width, height);

        var pointer = ImGui.GetMousePos();
        var overPopup = pointer.X >= min.X && pointer.X < min.X + size.X && pointer.Y >= min.Y && pointer.Y < min.Y + size.Y;
        var overChip = pointer.X >= chipMin.X && pointer.X < chipMin.X + chipSize.X && pointer.Y >= chipMin.Y && pointer.Y < chipMin.Y + chipSize.Y;
        _locatorPopup.Press(ImGui.IsMouseClicked(ImGuiMouseButton.Left), overPopup, overChip);
        if (!_locatorPopup.IsOpen)
            return;

        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##locatorpopup"u8, RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        RaiseIfAppearing();
        var draw = ImGui.GetWindowDrawList();
        Kit.PanelFrame(draw, min, min + size);
        var inner = size.X - pad * 2;
        var at = min + new float2(pad, pad);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, at, Theme.TextMute, "ADVANCED LOCATOR"u8);
        at.Y += line + gap;

        var filter = LocatorFilterNow(galaxy);
        var changed = false;
        DrawLocatorBound("MIN"u8, at, true, filter, figures, ref changed, out filter);
        at.Y += control;
        DrawLocatorBound("MAX"u8, at, false, filter, figures, ref changed, out filter);
        at.Y += control + gap;

        // What is looked for: the shortcuts on their row, each only ores this galaxy has.
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(at.X, at.Y + (control - line) * 0.5f), Theme.TextMute, "LOOK FOR"u8);
        var half = 62f;
        ImGui.SetCursorScreenPos(new float2(at.X + inner - Kit.Px(half * 2 + Theme.Space2), at.Y));
        if (Kit.Button("All"u8, ButtonKind.Secondary, half, galaxy is not null) && galaxy is not null)
        {
            filter = LocatorPopup.AllOres(galaxy, filter);
            changed = true;
        }

        ImGui.SetCursorScreenPos(new float2(at.X + inner - Kit.Px(half), at.Y));
        if (Kit.Button("None"u8, ButtonKind.Secondary, half))
        {
            filter = LocatorPopup.NoOres(filter);
            changed = true;
        }

        at.Y += control + gap;
        var swatch = Kit.Px(figures.SwatchPixels);
        var cellWidth = inner / perRow;
        var oreAt = at;
        for (var r = 0; r < resources; r++)
        {
            var column = r % perRow;
            if (column == 0 && r > 0)
                oreAt.Y += row;
            var cell = new float2(oreAt.X + cellWidth * column, oreAt.Y);
            ImGui.PushID(r);
            var resource = galaxy!.Resources[r];
            // The ore's own colour first, where the galaxy keeps it: the colour its containers wear.
            var swatchAt = new float2(cell.X, cell.Y + (row - swatch) * 0.5f);
            draw.AddRectFilled(swatchAt, swatchAt + new float2(swatch, swatch), Kit.Color(StarColor(resource.Color)));
            ImGui.SetCursorScreenPos(new float2(cell.X + swatch + Kit.Px(Theme.Space1), cell.Y));
            if (EditorCheckbox("##ore"u8, Kit.F($"{resource.Name}"), new float2(cellWidth - swatch - Kit.Px(Theme.Space1) - Kit.Px(Theme.Space2), row), true, filter.HasOre(r)))
            {
                filter = LocatorPopup.ToggleOre(filter, r);
                changed = true;
            }

            ImGui.PopID();
        }

        at.Y += oreRows * row + gap;
        ImGui.SetCursorScreenPos(at);
        var gate = _moduleGate[2];
        if (Kit.Button("Pulse"u8, ButtonKind.Primary, inner / Kit.Scale, gate == ModuleGate.Ready) && Bridge is { } bridge)
            RequestModule(2, bridge);
        at.Y += control + gap;

        // What the filter reports, and why the pulse cannot go now. Each figure is built before the line that holds
        // it: one Kit.F's text is never built inside another's.
        var range = LocatorPopup.RangeWords(filter);
        var ores = LocatorPopup.OreWords(filter);
        var words = Kit.F($"{range}   {ores}");
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, at, Theme.TextDim, words);
        if (gate != ModuleGate.Ready)
        {
            ReadOnlySpan<byte> why = gate switch
            {
                ModuleGate.Running => Kit.F($"{ModuleRules.SecondsLeft(snapshot.Modules.Scan, snapshot.ServerTick):0.0} S"),
                ModuleGate.Cooling => Kit.F($"COOL {ModuleRules.CooldownSeconds(snapshot.Modules.Scan, ModuleFamily.AsteroidLocator, snapshot.ServerTick)} S"),
                ModuleGate.Waiting => "BUSY"u8,
                _ => "WAIT"u8,
            };
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + inner - Kit.Measure(_ui.Mono, Theme.FontCaption, why).X, at.Y), Theme.Accent, why);
        }

        if (changed)
            SetLocatorFilter(filter, save: true);
        ImGui.End();
    }

    /// <summary>
    /// One diameter bound of the filter popup: its name, a button each side to step it and what it stands at. A step
    /// is the client's own figure; the bound never passes the other one, and a bound of zero means "no bound".
    /// </summary>
    private void DrawLocatorBound(ReadOnlySpan<byte> name, float2 at, bool min, LocatorFilter filter,
        in LocatorFilterLook figures, ref bool changed, out LocatorFilter result)
    {
        var draw = ImGui.GetWindowDrawList();
        var row = Kit.Px(Theme.ControlHeight);
        const float Button = 26f;
        var nameWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, name).X;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(at.X, at.Y + (row - Kit.Measure(_ui.SansMedium, Theme.FontCaption, name).Y) * 0.5f), Theme.TextMute, name);
        var valueX = at.X + nameWidth + Kit.Px(Theme.Space2);
        var lower = min ? filter.MinMetres > 0 : filter.MaxMetres > filter.MinMetres;
        ImGui.SetCursorScreenPos(new float2(valueX, at.Y));
        if (Kit.Button("-"u8, ButtonKind.Secondary, Button, lower))
        {
            filter = LocatorPopup.Step(filter, min, -1, figures);
            changed = true;
        }

        var readout = min
            ? (filter.MinMetres == 0 ? "ANY"u8 : Kit.F($"{filter.MinMetres} M"))
            : (filter.MaxMetres == 0 ? "ANY"u8 : Kit.F($"{filter.MaxMetres} M"));
        var field = Math.Max(Kit.Px(56), Kit.Measure(_ui.Mono, Theme.FontCaption, "00000 M"u8).X + Kit.Px(Theme.Space2));
        var fieldAt = new float2(valueX + Kit.Px(Button) + Kit.Px(Theme.Space1), at.Y);
        draw.AddRectFilled(fieldAt, fieldAt + new float2(field, row), Kit.Color(Theme.Panel, 0.95f));
        draw.AddRect(fieldAt, fieldAt + new float2(field, row), Kit.Color(Theme.Line, 0.9f));
        var text = Kit.Measure(_ui.Mono, Theme.FontCaption, readout);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, fieldAt + new float2(MathF.Round((field - text.X) * 0.5f), MathF.Round((row - text.Y) * 0.5f)), Theme.Text, readout);

        ImGui.SetCursorScreenPos(new float2(fieldAt.X + field + Kit.Px(Theme.Space1), at.Y));
        var widest = figures.MaxDiameterMetres;
        var highest = filter.MaxMetres == 0 ? widest : filter.MaxMetres;
        var upper = min ? filter.MinMetres < highest : highest < widest;
        if (Kit.Button("+"u8, ButtonKind.Secondary, Button, upper))
        {
            filter = LocatorPopup.Step(filter, min, 1, figures);
            changed = true;
        }

        result = filter;
    }

    /// <summary>The longest rebuild of the flown hull's bays, in ticks: what the rebuild bar measures against.</summary>
    private float MaxRebuildTicks(SessionSnapshot snapshot)
    {
        var own = _app.World.Own;
        if (own.Id == 0 || snapshot.Content is not { } content || own.Design >= content.Designs.Length) return 0;
        var design = content.Designs[own.Design];
        var longest = 0L;
        foreach (var block in design.Bays) longest = Math.Max(longest, design.Types[block].Bay!.RebuildTicks);
        return longest;
    }

    /// <summary>One chip: tier stripe, key and name, status and, while it runs, a bar along its foot.</summary>
    private void DrawModuleChip(ImDrawListPtr draw, int slot, float2 min, float2 size, bool hot, SessionSnapshot snapshot)
    {
        var look = ModuleStyle;
        var family = ModuleRules.Families[slot];
        var equipment = _moduleEquipment[slot];
        var gate = _moduleGate[slot];
        var tier = look.Tier(equipment.Quality);
        var scan = snapshot.Modules.Scan;
        var max = min + size;
        var notice = _moduleTrack.Notice(family, look.Strip.ResultSeconds);
        var ready = gate == ModuleGate.Ready;
        var fade = ready || gate == ModuleGate.Running ? look.Strip.ReadyAlpha : look.Strip.IdleAlpha;
        var previous = Kit.Fade;
        Kit.Fade = previous * (hot ? 1f : fade);
        Hud.Plate(draw, min, max, tier.Rgb, hot);
        draw.AddRectFilled(min + new float2(0, Kit.Px(4)), min + new float2(Kit.Px(3), size.Y - Kit.Px(4)), Kit.Color(tier.Rgb));
        var pad = Kit.Px(Theme.Space2) + Kit.Px(3);
        var inner = size.X - pad - Kit.Px(Theme.Space2);
        var keyText = _moduleKeyNames[slot];
        var keyWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, keyText).X;
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(pad, Kit.Px(4)), Theme.Accent, keyText);
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, min + new float2(pad + keyWidth + Kit.Px(Theme.Space2), Kit.Px(4)), Theme.Text, look.Family(family).Short, Math.Max(0f, inner - keyWidth - Kit.Px(Theme.Space2)));
        var statusAt = min + new float2(pad, Kit.Px(20));
        var speaking = notice != ModuleNotice.None && gate != ModuleGate.Running;
        switch (speaking ? ModuleGate.Absent : gate)
        {
            case ModuleGate.Running:
                var progress = ModuleRules.Progress(scan, snapshot.ServerTick);
                if (scan.State == ModuleScanState.Preparing)
                    Kit.Text(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.Info, "PREPARING"u8);
                else
                    Kit.Text(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.Info, Kit.F($"{ModuleRules.SecondsLeft(scan, snapshot.ServerTick):0.0} S"));
                var track = new float2(min.X + Kit.Px(3), max.Y - Kit.Px(4));
                draw.AddRectFilled(track, new float2(max.X - Kit.Px(1), max.Y - Kit.Px(1)), Kit.Color(Theme.PanelRaised, 0.9f));
                draw.AddRectFilled(track, new float2(track.X + (max.X - Kit.Px(1) - track.X) * progress, max.Y - Kit.Px(1)), Kit.Color(Theme.Info));
                break;
            case ModuleGate.Cooling:
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.TextDim, Kit.F($"COOL {ModuleRules.CooldownSeconds(scan, family, snapshot.ServerTick)} S"));
                break;
            case ModuleGate.Waiting:
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.TextMute, "WAITING"u8);
                break;
            case ModuleGate.NoTarget:
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.TextMute, "NO TARGET"u8);
                break;
            case ModuleGate.NoCloud:
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.TextMute, "NO CLOUD"u8);
                break;
            case ModuleGate.OutOfRange:
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.Accent, "OUT OF RANGE"u8);
                break;
            case ModuleGate.Absent:
                break;
            default:
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.Good, "READY"u8);
                break;
        }

        // A result or a refusal speaks over the status for a few seconds, whatever the gate says now.
        if (speaking)
        {
            var words = ModuleRules.Words(notice);
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, statusAt, notice == ModuleNotice.Done ? Theme.Good : notice == ModuleNotice.Cancelled ? Theme.TextDim : Theme.Bad, words, inner);
        }

        Kit.Fade = previous;
    }

    /// <summary>A module's tooltip over the chip: its key, quality, figures, and what the chip would do or why not. Takes no input.</summary>
    private void DrawModuleTip(ImDrawListPtr draw, int slot, float2 chipMin, float2 chipSize, SessionSnapshot snapshot)
    {
        var look = ModuleStyle;
        var family = ModuleRules.Families[slot];
        var equipment = _moduleEquipment[slot];
        var tier = look.Tier(equipment.Quality);
        var gate = _moduleGate[slot];
        var title = Kit.F($"{look.Family(family).Label}   {_moduleKeyStrings[slot]}");
        var reach = equipment.Range >= 1000 ? Kit.F($"RANGE {equipment.Range / 1000:0.##} KM   SCAN {equipment.ScanSeconds:0.#} S") : Kit.F($"RANGE {equipment.Range:0} M   SCAN {equipment.ScanSeconds:0.#} S");
        var power = Kit.F($"DRAWS {equipment.PowerDraw:0.##} MW   COOLDOWN {equipment.CooldownSeconds:0.#} S");
        var quality = Kit.F($"{tier.Label}  MK {equipment.Mark}");
        var notice = _moduleTrack.Notice(family, look.Strip.ResultSeconds);
        var note = notice != ModuleNotice.None && notice != ModuleNotice.Done ? ModuleRules.Sentence(notice) : gate switch
        {
            ModuleGate.Ready => family switch
            {
                ModuleFamily.GasScanner => "Samples the cloud you are in or beside. Flight goes on."u8,
                ModuleFamily.AsteroidScanner => "Analyses the targeted asteroid: ore, grade and mass."u8,
                _ => "Pulses once: classes every asteroid in range as ice, metal or bare rock."u8,
            },
            ModuleGate.Running => "Running. Flight goes on; a jump or a change of ship cancels it."u8,
            ModuleGate.Cooling => ModuleRules.Sentence(ModuleNotice.Cooldown),
            ModuleGate.Waiting => ModuleRules.Sentence(ModuleNotice.Busy),
            ModuleGate.NoTarget => ModuleRules.Sentence(ModuleNotice.NoTarget),
            ModuleGate.NoCloud => ModuleRules.Sentence(ModuleNotice.NoCloud),
            _ => ModuleRules.Sentence(ModuleNotice.OutOfRange),
        };
        var pad = Kit.Px(Theme.Space2);
        var lineHeight = Kit.Px(15);
        var width = Math.Max(Math.Max(Kit.Measure(_ui.Mono, Theme.FontCaption, title).X, Kit.Measure(_ui.Mono, Theme.FontCaption, reach).X), Kit.Measure(_ui.Sans, Theme.FontCaption, note).X) + pad * 2;
        var size = new float2(width, lineHeight * 5 + pad * 2);
        var display = _ui.DisplaySize;
        var min = new float2(Math.Clamp(chipMin.X + (chipSize.X - size.X) * 0.5f, Kit.Px(Theme.Space2), Math.Max(Kit.Px(Theme.Space2), display.X - size.X - Kit.Px(Theme.Space2))), chipMin.Y - size.Y - Kit.Px(Theme.Space2));
        Hud.Plate(draw, min, min + size, tier.Rgb, true);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(pad, pad), Theme.Accent, title);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, min + new float2(pad, pad + lineHeight), tier.Rgb, quality);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(pad, pad + lineHeight * 2), Theme.Text, reach);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, min + new float2(pad, pad + lineHeight * 3), Theme.Text, power);
        Kit.Text(draw, _ui.Sans, Theme.FontCaption, min + new float2(pad, pad + lineHeight * 4), Theme.TextDim, note);
    }

    /// <summary>
    /// A small pressable button drawn in its own window over the HUD: the target panel's Scan. A pointer captured by
    /// flight cannot press it, so the key does the same. Disabled, it is muted and presses nothing.
    /// </summary>
    private bool ModuleButton(ImString id, float2 min, float2 size, ReadOnlySpan<byte> label, bool enabled, uint tone)
    {
        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin(id, RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.SetCursorScreenPos(min);
        var pressed = ImGui.InvisibleButton("##b"u8, size);
        var hot = Kit.Animate(ImGui.GetItemID(), enabled && ImGui.IsItemHovered() ? 1f : 0f);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, Kit.Color(enabled ? Kit.Mix(Theme.PanelRaised, Theme.Panel, 1f - hot) : Theme.Panel, 0.95f));
        draw.AddRect(min, min + size, Kit.Color(enabled ? Kit.Mix(tone, Theme.Accent, hot) : Theme.Line, enabled ? 0.9f : 0.6f));
        var text = Kit.Measure(_ui.SansMedium, Theme.FontCaption, label);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, min + new float2(MathF.Round((size.X - text.X) * 0.5f), MathF.Round((size.Y - text.Y) * 0.5f)),
            enabled ? Kit.Mix(Theme.TextDim, Theme.Text, hot) : Theme.TextMute, label);
        ImGui.End();
        return pressed && enabled;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The asteroid analysis
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The retained analysis of an asteroid, or -1: the player has not scanned it with an asteroid scanner.</summary>
    private static int FindAnalysis(SessionSnapshot snapshot, RockRef rock) =>
        snapshot.Modules.Find(ModuleFamily.AsteroidScanner, rock.Sector, rock.Index);

    /// <summary>Mass in tonnes as the readouts word it: kilotonnes up to a million tonnes, then megatonnes.</summary>
    private static ReadOnlySpan<byte> MassText(float tonnes) =>
        tonnes >= 1e6f ? Kit.F($"{tonnes / 1e6f:0.00} MT") : Kit.F($"{tonnes / 1e3f:0.0} KT");

    /// <summary>
    /// A resource mixed in with an asteroid's main ore as a scan found it: its name and share, or its name and TRACE when
    /// the scanner did not resolve a figure. Empty when there is none.
    /// </summary>
    private static ReadOnlySpan<byte> MixText(Galaxy galaxy, int resource, float share) =>
        resource < 0 || resource >= galaxy.Resources.Length ? default
        : share > 0 ? Kit.F($"{galaxy.Resources[resource].Name} {share * 100f:0} %")
        : Kit.F($"{galaxy.Resources[resource].Name} TRACE");

    /// <summary>
    /// What the target panel says of an asteroid below its title: what the player's scan found (ore and its grade as a
    /// bar, the host rock's share, mass and size, who scanned it and how long ago) or that it is unscanned, with the
    /// key. A detail the scanner's quality did not resolve is not shown. Returns the height used, unscaled pixels
    /// already scaled.
    /// </summary>
    private float DrawRockAnalysis(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, RockRef rock, float2 at, float width)
    {
        var look = ModuleStyle;
        var galaxy = snapshot.Galaxy!;
        // What the player's drones found is finer than any scanner module's analysis and comes first.
        var drone = snapshot.DroneScans.Find(rock);
        if (drone >= 0)
            return DrawDroneAnalysis(draw, snapshot, rock, drone, at, width);
        var index = FindAnalysis(snapshot, rock);
        if (index < 0)
        {
            var scannerGate = _moduleGate[1];
            var key = _moduleKeyStrings[1];
            if (scannerGate == ModuleGate.Absent)
            {
                Kit.ClippedText(draw, _ui.Sans, Theme.FontCaption, at, Theme.TextMute, "Unscanned - this ship has no asteroid scanner"u8, width);
                return Kit.Px(18);
            }

            var buttonWidth = Kit.Px(64);
            Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, at + new float2(0, Kit.Px(2)), Theme.Info, Kit.F($"Unscanned - press {key} to scan"), width - buttonWidth - Kit.Px(Theme.Space2));
            var notice = _moduleTrack.Notice(ModuleFamily.AsteroidScanner, look.Strip.ResultSeconds);
            var label = scannerGate == ModuleGate.Running ? "SCANNING"u8 : "SCAN"u8;
            var tone = scannerGate == ModuleGate.Ready ? Theme.Info : Theme.LineStrong;
            if (ModuleButton("##targetscan"u8, new float2(at.X + width - buttonWidth, at.Y - Kit.Px(1)), new float2(buttonWidth, Kit.Px(20)), label, scannerGate == ModuleGate.Ready, tone))
                if (Bridge is { } bridge)
                    RequestModule(1, bridge);
            if (notice != ModuleNotice.None && notice != ModuleNotice.Done)
                Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(0, Kit.Px(18)), Theme.Bad, ModuleRules.Words(notice), width);
            else if (scannerGate is ModuleGate.Cooling or ModuleGate.OutOfRange or ModuleGate.Running)
                Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, at + new float2(0, Kit.Px(18)), Theme.TextMute,
                    scannerGate == ModuleGate.Cooling ? Kit.F($"COOLING {ModuleRules.CooldownSeconds(snapshot.Modules.Scan, ModuleFamily.AsteroidScanner, snapshot.ServerTick)} S")
                    : scannerGate == ModuleGate.Running ? Kit.F($"{ModuleRules.SecondsLeft(snapshot.Modules.Scan, snapshot.ServerTick):0.0} S LEFT")
                    : "OUT OF RANGE"u8, width);
            return Kit.Px(34);
        }

        ref var record = ref snapshot.Modules.Records[index];
        var composition = RockComposition.From(record, galaxy.Resources.Length);
        var tier = look.Tier(record.Quality);
        var rowHeight = Kit.Px(look.CompositionRowHeight);
        var y = at.Y;

        DrawCompositionBar(draw, galaxy, composition, new float2(at.X, y), width, Kit.Px(look.CompositionBarHeight), look);
        y += Kit.Px(look.CompositionBarHeight + 6);

        var shareColumn = Kit.Measure(_ui.Mono, Theme.FontCaption, "UP TO 100 %"u8).X;
        var massColumn = Kit.Measure(_ui.Mono, Theme.FontCaption, "0000.00 MT"u8).X;
        var massRight = at.X + width;
        var shareRight = massRight - massColumn - Kit.Px(Theme.Space2);
        if (composition.Count == 0)
        {
            CompositionRow(draw, "Bare rock"u8, look.RockRgb, default, MassText(record.Mass), new float2(at.X, y), width, shareRight, massRight);
            y += rowHeight;
        }
        else
        {
            for (var part = 0; part < composition.Count; part++)
            {
                var resource = galaxy.Resources[composition.ResourceOf(part)];
                var share = composition.ShareOf(part);
                // A trace is named but has no figure and no mass: it would claim more than the scanner resolved.
                CompositionRow(draw, Kit.F($"{resource.Name}"), StarColor(resource.Color), ShareText(share),
                    share > 0f ? MassText(record.Mass * share) : default, new float2(at.X, y), width, shareRight, massRight);
                y += rowHeight;
            }

            var rockMass = composition.RockIsCeiling ? default : MassText(record.Mass * composition.Remainder);
            CompositionRow(draw, "Rock"u8, look.RockRgb, RockShareText(composition), rockMass, new float2(at.X, y), width, shareRight, massRight);
            y += rowHeight;
        }

        var age = ModuleAge.Seconds(record.RecordedTick, snapshot.ServerTick);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(at.X, y + Kit.Px(2)), tier.Rgb, tier.Label);
        var tierWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, tier.Label).X;
        var total = MassText(record.Mass);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + tierWidth + Kit.Px(Theme.Space2), y + Kit.Px(2)), Theme.TextMute,
            Kit.F($"MASS {total}   SCANNED {ModuleAge.Value(age):0} {ModuleAge.Unit(age)} AGO"), Math.Max(0f, width - tierWidth - Kit.Px(Theme.Space2)));
        return Kit.Px(look.AnalysisHeight(composition));
    }

    /// <summary>
    /// The one stacked bar: main ore, mix-ins, then rock, each in its colour. A trace is a thin sliver, never a share-sized
    /// segment; rock takes what is left of the bar, which with a trace is rock and traces together.
    /// </summary>
    private static void DrawCompositionBar(ImDrawListPtr draw, Galaxy galaxy, in RockComposition composition, float2 at, float width, float height, ModuleLook look)
    {
        draw.AddRectFilled(at, at + new float2(width, height), Kit.Color(look.RockRgb, 0.95f));
        var x = at.X;
        var sliver = Math.Min(Kit.Px(look.TraceMarkPixels), width * 0.05f);
        for (var part = 0; part < composition.Count; part++)
        {
            var share = composition.ShareOf(part);
            var segment = share > 0f ? Math.Min(width * share, at.X + width - x) : sliver;
            draw.AddRectFilled(new float2(x, at.Y), new float2(x + segment, at.Y + height), Kit.Color(StarColor(galaxy.Resources[composition.ResourceOf(part)].Color), 0.95f));
            x += segment;
        }

        draw.AddRect(at, at + new float2(width, height), Kit.Color(Theme.Line, 0.9f));
    }

    /// <summary>One composition row: swatch, name, share and ore mass, the last two right-aligned in their columns.</summary>
    private void CompositionRow(ImDrawListPtr draw, ReadOnlySpan<byte> name, uint rgb, ReadOnlySpan<byte> share, ReadOnlySpan<byte> mass, float2 at, float width, float shareRight, float massRight)
    {
        var top = Kit.Px(1);
        var side = Kit.Px(8);
        draw.AddRectFilled(at + new float2(0, Kit.Px(3)), at + new float2(side, Kit.Px(3) + side), Kit.Color(rgb));
        Kit.ClippedText(draw, _ui.SansMedium, Theme.FontCaption, at + new float2(Kit.Px(14), top), Theme.Text, name, Math.Max(0f, shareRight - at.X - Kit.Px(14)));
        if (share.Length != 0)
        {
            var shareSize = Kit.Measure(_ui.Mono, Theme.FontCaption, share).X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(shareRight - shareSize, at.Y + top), Theme.TextDim, share);
        }

        if (mass.Length != 0)
        {
            var massSize = Kit.Measure(_ui.Mono, Theme.FontCaption, mass).X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(massRight - massSize, at.Y + top), Theme.TextDim, mass);
        }
    }

    /// <summary>A resolved share as a whole percentage, or TRACE when the scanner named the resource without resolving a figure.</summary>
    private static ReadOnlySpan<byte> ShareText(float share) =>
        share > 0f ? Kit.F($"{share * 100f:0} %") : "TRACE"u8;

    /// <summary>
    /// The rock's share in the one wording every readout uses: a figure when every share is resolved, "UP TO x %" while a
    /// trace exists, because the remainder is then rock and traces together.
    /// </summary>
    private static ReadOnlySpan<byte> RockShareText(in RockComposition composition) =>
        composition.RockIsCeiling ? Kit.F($"UP TO {composition.Remainder * 100f:0} %") : Kit.F($"{composition.Remainder * 100f:0} %");

    /// <summary>A flat bar: the track and a fill to <paramref name="fraction"/>.</summary>
    private static void DrawFractionBar(ImDrawListPtr draw, float2 at, float width, float height, float fraction, uint rgb)
    {
        draw.AddRectFilled(at, at + new float2(width, height), Kit.Color(Theme.PanelRaised, 0.95f));
        draw.AddRectFilled(at, at + new float2(width * Math.Clamp(fraction, 0f, 1f), height), Kit.Color(rgb, 0.95f));
        draw.AddRect(at, at + new float2(width, height), Kit.Color(Theme.Line, 0.9f));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The locator's vision
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The locator's result over the view: a marker on every asteroid the locator classed, in its class's colour. The
    /// pulse's own asteroids appear nearest first as its wave runs out, stay at full strength for the vision time, then
    /// fade; in the command view they fade down to a faint mark and stay, as the player's knowledge. In the flight
    /// view an old pulse shows nothing. A marker is a screen-space mark where the asteroid was observed, never a live
    /// track. Allocation-free: one pass over the retained records, projecting each.
    /// </summary>
    private void DrawLocatorVision(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, float alpha, bool command)
    {
        var modules = snapshot.Modules;
        var galaxy = snapshot.Galaxy;
        if (galaxy is null || modules.Count == 0)
            return;
        var look = ModuleStyle;
        var field = look.Locator;
        var age = _moduleTrack.PulseAge;
        var origin = _moduleTrack.PulseOrigin;
        var range = _moduleTrack.PulseRange > 0 ? _moduleTrack.PulseRange : 5000f;
        var display = _ui.DisplaySize;
        Span<int> nearest = stackalloc int[Math.Max(field.LabelNearest, 1)];
        Span<double> nearestDistance = stackalloc double[Math.Max(field.LabelNearest, 1)];
        var labelled = 0;
        var drawn = 0;
        for (var i = 0; i < modules.Count; i++)
        {
            ref var record = ref modules.Records[i];
            if (record.Family != ModuleFamily.AsteroidLocator)
                continue;
            var fresh = record.RecordedTick >= _moduleTrack.PulseTick;
            var strength = LocatorField.Alpha(fresh, command, age, field);
            if (fresh)
                strength *= LocatorField.Reveal((record.Position - origin).Length, range, age, field);
            strength *= alpha;
            if (strength < 0.02f || !world.Project(record.Position, out var at))
                continue;
            if (at.X < -20f || at.Y < -20f || at.X > display.X + 20f || at.Y > display.Y + 20f)
                continue;
            drawn++;
            var rgb = look.Classes[look.ClassOf(galaxy, record.Resource)].Rgb;
            if (fresh && age <= field.VisionSeconds + field.FadeSeconds)
            {
                var ring = Kit.Px(field.RingPixels);
                draw.AddCircle(at, ring, Kit.Color(rgb, strength * 0.8f), 20, Kit.Px(1.2f));
                draw.AddCircleFilled(at, Kit.Px(field.MarkerPixels), Kit.Color(rgb, strength), 12);
                // The few nearest take a label; found by a small insertion into arrays of the label count.
                if (field.LabelNearest > 0)
                {
                    var distance = (record.Position - world.OwnPosition).Length;
                    var slot = labelled < field.LabelNearest ? labelled : -1;
                    if (slot < 0 && distance < nearestDistance[field.LabelNearest - 1])
                        slot = field.LabelNearest - 1;
                    if (slot >= 0)
                    {
                        if (slot == labelled)
                            labelled++;
                        while (slot > 0 && nearestDistance[slot - 1] > distance)
                        {
                            nearestDistance[slot] = nearestDistance[slot - 1];
                            nearest[slot] = nearest[slot - 1];
                            slot--;
                        }

                        nearestDistance[slot] = distance;
                        nearest[slot] = i;
                    }
                }
            }
            else
            {
                var half = Kit.Px(field.RetainedMarkerPixels);
                draw.AddCircleFilled(at, half, Kit.Color(rgb, strength), 10);
            }
        }

        // Labels last, over the markers: class and distance for the nearest few of the fresh pulse.
        var labelAlpha = LocatorField.Alpha(true, false, age, field) * alpha;
        for (var n = 0; n < labelled && labelAlpha > 0.05f; n++)
        {
            ref var record = ref modules.Records[nearest[n]];
            if (!world.Project(record.Position, out var at))
                continue;
            var cls = look.Classes[look.ClassOf(galaxy, record.Resource)];
            var meters = nearestDistance[n];
            var distanceText = meters >= 1000 ? Kit.F($"{meters / 1000:0.0} KM") : Kit.F($"{meters:0} M");
            // An advanced locator's pulse reports the rock's radius, which its mark words as the diameter, as every
            // other readout words a rock. A basic pulse reports none, and its labels stay as they were. The size is
            // built first: one Kit.F's text is built into the next, never inside it.
            var size = DiameterWords(record.Radius);
            var text = size.IsEmpty
                ? Kit.F($"{cls.Label}  {distanceText}")
                : Kit.F($"{cls.Label}  {size}  {distanceText}");
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(Kit.Px(field.RingPixels + 4), -Kit.Px(7)), cls.Rgb, text, labelAlpha);
        }

        // While the pulse is fresh, its legend and a note when the server could not list everything.
        if (age <= field.VisionSeconds + field.FadeSeconds && drawn > 0)
            DrawLocatorLegend(draw, alpha * LocatorField.Alpha(true, false, age, field), snapshot);
    }

    /// <summary>
    /// A rock's size as a locator's mark words it: its diameter in whole metres, from the radius the pulse reported.
    /// Never a second shape of the figure: the target panel's size reads the same way.
    /// </summary>
    internal static ReadOnlySpan<byte> DiameterWords(float radiusMetres) => radiusMetres > 0 ? Kit.F($"{radiusMetres * 2f:0} M") : default;

    /// <summary>A single line of the classes with their colours, centred under the top of the view while a pulse shows.</summary>
    private void DrawLocatorLegend(ImDrawListPtr draw, float alpha, SessionSnapshot snapshot)
    {
        var look = ModuleStyle;
        var classes = look.Classes;
        var gap = Kit.Px(Theme.Space3);
        var dot = Kit.Px(6);
        var total = 0f;
        for (var c = 0; c < classes.Length; c++)
            total += dot * 2 + Kit.Px(4) + Kit.Measure(_ui.Mono, Theme.FontCaption, classes[c].Label).X + (c > 0 ? gap : 0f);
        var x = MathF.Round((_ui.DisplaySize.X - total) * 0.5f);
        var y = HudTop + Kit.Px(Theme.Space2) + Kit.Px(64);
        for (var c = 0; c < classes.Length; c++)
        {
            if (c > 0)
                x += gap;
            draw.AddCircleFilled(new float2(x + dot, y + Kit.Px(7)), dot * 0.7f, Kit.Color(classes[c].Rgb, alpha));
            x += dot * 2 + Kit.Px(4);
            x += Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(x, y), classes[c].Rgb, classes[c].Label, alpha).X;
        }

        if (_moduleTrack.PulsePartial)
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(MathF.Round((_ui.DisplaySize.X - Kit.Measure(_ui.Mono, Theme.FontCaption, "PARTIAL: MORE ASTEROIDS MAY BE IN RANGE"u8).X) * 0.5f), y + Kit.Px(16)), Theme.Accent, "PARTIAL: MORE ASTEROIDS MAY BE IN RANGE"u8, alpha);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Gas knowledge
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One cloud's composition: its kind, how much of it the player has sampled and how long ago, then each resolved
    /// gas as a small bar with its share, the unresolved remainder, and the purity and density. Returns the height used.
    /// Only resolved gases are named; what the scanner could not resolve is one muted row, never spread over the others.
    /// </summary>
    private float DrawGasBars(ImDrawListPtr draw, SessionSnapshot snapshot, int entry, float2 at, float width, int maxRows, bool header)
    {
        var look = ModuleStyle;
        var galaxy = snapshot.Galaxy!;
        var names = galaxy.GasComposition!.Names;
        var line = Kit.Px(17);
        var y = at.Y;
        if (header)
        {
            var kind = look.Kind(_gasLedger.Kind(entry));
            var tier = look.Tier(_gasLedger.Best(entry));
            var age = ModuleAge.Seconds(_gasLedger.Newest(entry), snapshot.ServerTick);
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(at.X, y), kind.Rgb, kind.Label);
            var kindWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, kind.Label).X;
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(at.X + kindWidth + Kit.Px(Theme.Space2), y), tier.Rgb, tier.Label);
            var meta = Kit.F($"{_gasLedger.Regions(entry)}/8 SAMPLES   {ModuleAge.Value(age):0} {ModuleAge.Unit(age)} AGO");
            var metaWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, meta).X;
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + width - metaWidth, y), Theme.TextMute, meta);
            y += line;
        }

        var shown = 0;
        var hidden = 0;
        var nameColumn = Kit.Measure(_ui.Mono, Theme.FontCaption, "IONISED HYDROGEN "u8).X;
        var reading = Kit.Measure(_ui.Mono, Theme.FontCaption, "100 %"u8).X;
        var barWidth = Math.Max(Kit.Px(24), Math.Min(Kit.Px(look.CardBarWidth * 1.4f), width - nameColumn - reading - Kit.Px(Theme.Space3)));
        for (var g = 0; g < GasComposition.Capacity; g++)
        {
            var share = _gasLedger.Share(entry, g);
            if (share <= 0f)
                continue;
            if (shown >= maxRows)
            {
                hidden++;
                continue;
            }

            shown++;
            var rgb = look.GasColor(names, g);
            draw.AddRectFilled(new float2(at.X, y + Kit.Px(3)), new float2(at.X + Kit.Px(6), y + Kit.Px(11)), Kit.Color(rgb));
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + Kit.Px(12), y), Theme.Text, names[g], nameColumn - Kit.Px(12));
            DrawFractionBar(draw, new float2(at.X + nameColumn, y + Kit.Px(4)), barWidth, Kit.Px(6), share, rgb);
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + nameColumn + barWidth + Kit.Px(Theme.Space2), y), Theme.TextDim, Kit.F($"{share * 100f:0} %"));
            y += line;
        }

        var unresolved = _gasLedger.Unresolved(entry);
        if (unresolved * 100f >= look.UnresolvedPercent || shown == 0)
        {
            draw.AddRectFilled(new float2(at.X, y + Kit.Px(3)), new float2(at.X + Kit.Px(6), y + Kit.Px(11)), Kit.Color(Theme.LineStrong));
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + Kit.Px(12), y), Theme.TextMute, "UNRESOLVED"u8, nameColumn - Kit.Px(12));
            DrawFractionBar(draw, new float2(at.X + nameColumn, y + Kit.Px(4)), barWidth, Kit.Px(6), unresolved, Theme.LineStrong);
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + nameColumn + barWidth + Kit.Px(Theme.Space2), y), Theme.TextMute, Kit.F($"{unresolved * 100f:0} %"));
            y += line;
        }

        if (hidden > 0)
        {
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(at.X, y), Theme.TextMute, Kit.F($"+{hidden} MORE GASES"));
            y += line;
        }

        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(at.X, y), Theme.TextDim, Kit.F($"PURITY {_gasLedger.Purity(entry) * 100f:0} %   DENSITY {_gasLedger.Density(entry) * 100f:0} %"));
        return y + line - at.Y;
    }

    /// <summary>How tall <see cref="DrawGasBars"/> is for a cloud: the header, each resolved gas, the unresolved row, any hidden note, and the figures.</summary>
    private float GasBarsHeight(SessionSnapshot snapshot, int entry, int maxRows, bool header)
    {
        var look = ModuleStyle;
        var shown = 0;
        var hidden = false;
        for (var g = 0; g < GasComposition.Capacity; g++)
        {
            if (_gasLedger.Share(entry, g) <= 0f)
                continue;
            if (shown < maxRows)
                shown++;
            else
                hidden = true;
        }

        var unresolved = _gasLedger.Unresolved(entry) * 100f >= look.UnresolvedPercent || shown == 0;
        return Kit.Px(17) * ((header ? 1 : 0) + shown + (unresolved ? 1 : 0) + (hidden ? 1 : 0) + 1);
    }

    /// <summary>
    /// The GAS window: every cloud the player has sampled, newest first, each as its composition. It is the player's
    /// knowledge (<see cref="SessionSnapshot.Modules"/>): a cloud nobody scanned is not here, and a cloud shows only
    /// what the scanner resolved. A cloud's row can show it on the map.
    /// </summary>
    private void DrawGasPane(SessionSnapshot snapshot)
    {
        var galaxy = snapshot.Galaxy;
        if (galaxy?.GasComposition is null)
            return;
        var title = Kit.F($"Gas   {_gasLedger.Count} {(_gasLedger.Count == 1 ? "cloud" : "clouds")} sampled");
        if (!BeginPane(SocialPane.Gas, "##gas"u8, title, true, out var width, out var input))
            return;
        var draw = ImGui.GetWindowDrawList();
        var interactive = input != FloatingInput.Passive;
        if (_gasLedger.Count == 0)
        {
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute,
                _moduleGate[0] != ModuleGate.Absent
                    ? "No cloud sampled yet. Fly into or beside a gas cloud and press the gas scan key."u8
                    : "No cloud sampled yet. A ship with a gas scanner samples the cloud it is in or beside."u8, width / Kit.Scale);
            EndPane();
            return;
        }

        for (var row = 0; row < _gasLedger.Count; row++)
        {
            var entry = _gasLedger.At(row);
            var anchor = _gasLedger.Anchor(entry);
            var height = GasBarsHeight(snapshot, entry, 8, true) + Kit.Px(40);
            var at = ImGui.GetCursorScreenPos();
            ImGui.PushID(row);
            var meters = (float)ChartCatalogue.MetersTo(galaxy, anchor, _app.World.OwnPosition);
            var distance = Distance(meters);
            Kit.Text(draw, _ui.SansMedium, Theme.FontBody, at, Theme.Text, MapName(galaxy, anchor));
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(0, Kit.Px(20)), Theme.TextDim, Kit.F($"{anchor.X} : {anchor.Z}   LAYER {anchor.Y:+0;-0;0}   {distance}"));
            ImGui.PushItemFlag(ImGuiItemFlags.NoNav, true);
            var buttonWidth = Kit.Px(96);
            ImGui.SetCursorScreenPos(new float2(at.X + width - buttonWidth, at.Y));
            if (ChartAction("##gasmap"u8, "SHOW ON MAP"u8, true, buttonWidth, interactive))
                ShowChartOnMap(anchor);
            ImGui.PopItemFlag();
            DrawGasBars(draw, snapshot, entry, at + new float2(0, Kit.Px(40)), width, 8, true);
            draw.AddLine(new float2(at.X, at.Y + height - Kit.Px(4)), new float2(at.X + width, at.Y + height - Kit.Px(4)), Kit.Color(Theme.Line, 0.5f));
            ImGui.SetCursorScreenPos(at);
            ImGui.Dummy(new float2(width, height));
            ImGui.PopID();
        }

        Kit.Space(Theme.Space1);
        Kit.Paragraph(_ui.Sans, Theme.FontCaption, Theme.TextMute, "Shares are means over the sampled regions of each cloud. What the scanner could not resolve stays unresolved."u8, width / Kit.Scale);
        EndPane();
    }

    /// <summary>
    /// The extra height a charted sector's card needs for the gas the player sampled in the cloud anchored there, or
    /// zero: a caption and the cloud's top gases.
    /// </summary>
    private float GasCardHeight(SessionSnapshot snapshot, SectorCoord sector)
    {
        var entry = _gasLedger.Find(sector);
        if (entry < 0 || snapshot.Galaxy?.GasComposition is null)
            return 0f;
        return Kit.Px(CardLine) + GasBarsHeight(snapshot, entry, GasCardRows, true) + Kit.Px(Theme.Space2);
    }

    private const int GasCardRows = 4;

    /// <summary>The gas section of a sector card: the cloud anchored in the sector, as its composition, from the player's samples.</summary>
    private void DrawGasCard(ImDrawListPtr draw, SessionSnapshot snapshot, SectorCoord sector, float2 at, float width)
    {
        var entry = _gasLedger.Find(sector);
        if (entry < 0 || snapshot.Galaxy?.GasComposition is null)
            return;
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, at, Theme.Accent, "GAS SAMPLED"u8);
        DrawGasBars(draw, snapshot, entry, at + new float2(0, Kit.Px(CardLine)), width, GasCardRows, true);
    }
}
