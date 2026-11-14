using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Game.Client;
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
        var stats = own.Stats;
        for (var slot = 0; slot < 3; slot++)
            _moduleEquipment[slot] = own.Id == 0 ? default : stats.Module(ModuleRules.Families[slot]);

        _gasLedger.Update(snapshot.Modules, galaxy);
        var gas = _moduleEquipment[0];
        _gasTarget.Update(galaxy, world.OwnPosition, gas.Block != 0 ? gas.Range : 0, world.Time);
        var targets = new ModuleTargets { HasCloud = _gasTarget.Has, Cloud = _gasTarget.Anchor, CloudInRange = gas.Block != 0 && _gasTarget.InRange(gas.Range) };
        var asteroid = _moduleEquipment[1];
        if (asteroid.Block != 0 && ScannableRock(world) is { } rock && world.TryGetRock(rock, out var rockAt, out var rockRadius))
        {
            targets.Rock = rock;
            targets.RockInRange = ModuleScanRules.InRange(world.OwnPosition, rockAt, rockRadius, asteroid.Range);
        }

        _moduleTargets = targets;
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

    /// <summary>The asteroid the scanner would scan: the one targeted in flight, or the first one selected in the command view.</summary>
    private static RockRef? ScannableRock(WorldView world) =>
        world.Mode == ViewMode.Command && world.SelectedRocks.Length > 0 ? world.SelectedRocks[0] : world.TargetRock;

    /// <summary>
    /// The module keys. They work in the flight and command views, never over the editor, a dialog, a menu, the map,
    /// the controls page or while typing; a ready module asks the server, one that cannot says why on its chip.
    /// </summary>
    private void HandleModuleKeys(PlayerInputBridge bridge)
    {
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
        var notice = ModuleRules.Request(family, _moduleGate[slot], _moduleTargets, bridge);
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

    /// <summary>
    /// A chip for each module the ship being flown carries, in a row over the console's speed readout: the key, the
    /// module, its tier colour, and what it would do now (ready, running with its time left, cooling, or why not).
    /// Clicking a chip does what the key does, where the pointer is free. Nothing is drawn for a module the ship does
    /// not have, and nothing at all without one.
    /// </summary>
    private void DrawModuleStrip(ImDrawListPtr background, SessionSnapshot snapshot, WorldView world, float alpha)
    {
        var present = ModulesPresent();
        if (present == 0)
            return;
        var look = ModuleStyle;
        var strip = look.Strip;
        var layout = FlightConsole.Layout(_ui.DisplaySize, Kit.Scale);
        var width = Kit.Px(strip.ChipWidth);
        var height = Kit.Px(strip.ChipHeight);
        var gap = Kit.Px(strip.Gap);
        var total = width * present + gap * (present - 1);
        var origin = new float2(MathF.Round(layout.CenterX - total * 0.5f), layout.Top - height - Kit.Px(Theme.Space3));
        var size = new float2(total, height);
        ImGui.SetNextWindowPos(origin);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##modulestrip"u8, RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var draw = ImGui.GetWindowDrawList();
        var previous = Kit.Fade;
        Kit.Fade = alpha;
        var shown = 0;
        var hovered = -1;
        var hoveredMin = default(float2);
        for (var slot = 0; slot < 3; slot++)
        {
            if (_moduleGate[slot] == ModuleGate.Absent)
                continue;
            var min = origin + new float2((width + gap) * shown++, 0);
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
                RequestModule(slot, bridge);
        }

        Kit.Fade = previous;
        ImGui.End();
        if (hovered >= 0)
            DrawModuleTip(ImGui.GetForegroundDrawList(), hovered, hoveredMin, new float2(width, height), snapshot);
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
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, statusAt, Theme.TextDim, Kit.F($"COOL {ModuleRules.CooldownSeconds(scan, snapshot.ServerTick)} S"));
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
    /// What the target panel says of an asteroid below its title: what the player's scan found (ore and its grade as a
    /// bar, the host rock's share, mass and size, who scanned it and how long ago) or that it is unscanned, with the
    /// key. A detail the scanner's quality did not resolve is not shown. Returns the height used, unscaled pixels
    /// already scaled.
    /// </summary>
    private float DrawRockAnalysis(ImDrawListPtr draw, SessionSnapshot snapshot, WorldView world, RockRef rock, float2 at, float width)
    {
        var look = ModuleStyle;
        var galaxy = snapshot.Galaxy!;
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
                    scannerGate == ModuleGate.Cooling ? Kit.F($"COOLING {ModuleRules.CooldownSeconds(snapshot.Modules.Scan, snapshot.ServerTick)} S")
                    : scannerGate == ModuleGate.Running ? Kit.F($"{ModuleRules.SecondsLeft(snapshot.Modules.Scan, snapshot.ServerTick):0.0} S LEFT")
                    : "OUT OF RANGE"u8, width);
            return Kit.Px(34);
        }

        ref var record = ref snapshot.Modules.Records[index];
        var y = at.Y;
        var rowHeight = Kit.Px(18);
        var tier = look.Tier(record.Quality);
        if (record.Resource >= 0 && record.Resource < galaxy.Resources.Length)
        {
            var resource = galaxy.Resources[record.Resource];
            var dot = at + new float2(Kit.Px(4), Kit.Px(8));
            draw.AddCircleFilled(dot, Kit.Px(4), Kit.Color(StarColor(resource.Color)), 12);
            var name = Kit.Measure(_ui.SansMedium, Theme.FontCaption, resource.Name).X;
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(at.X + Kit.Px(14), y + Kit.Px(1)), Theme.Text, resource.Name);
            var barLeft = at.X + Kit.Px(14) + name + Kit.Px(Theme.Space2);
            var reading = Kit.F($"{record.Grade * 100f:0} % ORE   ROCK {(1f - record.Grade) * 100f:0} %");
            var readingWidth = Kit.Measure(_ui.Mono, Theme.FontCaption, reading).X;
            var barRight = Math.Max(barLeft + Kit.Px(24), at.X + width - readingWidth - Kit.Px(Theme.Space2));
            DrawFractionBar(draw, new float2(barLeft, y + Kit.Px(5)), barRight - barLeft, Kit.Px(7), record.Grade, StarColor(resource.Color));
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + width - readingWidth, y + Kit.Px(1)), Theme.TextDim, reading);
        }
        else
        {
            draw.AddCircleFilled(at + new float2(Kit.Px(4), Kit.Px(8)), Kit.Px(4), Kit.Color(Theme.TextMute), 12);
            Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(at.X + Kit.Px(14), y + Kit.Px(1)), Theme.TextDim, "Bare rock: no ore"u8);
        }

        y += rowHeight;
        var mass = MassText(record.Mass);
        var ore = MassText(record.Mass * record.Grade);
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(at.X, y), Theme.TextDim, Kit.F($"MASS {mass}   ORE {ore}"), width);
        y += rowHeight;
        var age = ModuleAge.Seconds(record.RecordedTick, snapshot.ServerTick);
        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(at.X, y), tier.Rgb, tier.Label);
        var tierWidth = Kit.Measure(_ui.SansMedium, Theme.FontCaption, tier.Label).X;
        Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, new float2(at.X + tierWidth + Kit.Px(Theme.Space2), y), Theme.TextMute,
            Kit.F($"SCANNED {ModuleAge.Value(age):0} {ModuleAge.Unit(age)} AGO"), Math.Max(0f, width - tierWidth - Kit.Px(Theme.Space2)));
        return rowHeight * 3;
    }

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
            var text = Kit.F($"{cls.Label}  {distanceText}");
            Kit.Text(draw, _ui.Mono, Theme.FontCaption, at + new float2(Kit.Px(field.RingPixels + 4), -Kit.Px(7)), cls.Rgb, text, labelAlpha);
        }

        // While the pulse is fresh, its legend and a note when the server could not list everything.
        if (age <= field.VisionSeconds + field.FadeSeconds && drawn > 0)
            DrawLocatorLegend(draw, alpha * LocatorField.Alpha(true, false, age, field), snapshot);
    }

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
