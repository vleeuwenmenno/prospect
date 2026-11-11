using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private const float WorldRowWidth = 600;

    /// <summary>Room left of each setting for the bar that marks a changed one.</summary>
    private const float MarkerGutter = 14;

    /// <summary>The settings being chosen on the New game screen; handed to the app on Start or Back.</summary>
    private WorldSettings _newGame;

    /// <summary>The option names of each choice setting, by the setting's index, for the choice widget.</summary>
    private string[][] _settingOptions = [];

    /// <summary>
    /// The settings in force shown read-only in a game, formatted once when the view opens: the value, and the
    /// default when the value differs from it (empty otherwise).
    /// </summary>
    private string[] _inForceValue = [];
    private string[] _inForceDefault = [];
    private bool _worldSettingsOpen;
    private bool _worldSettingsRequested;

    private void InitializeNewGame()
    {
        var definitions = _app.Content.SettingCatalog.Definitions;
        _settingOptions = new string[definitions.Length][];
        foreach (var definition in definitions)
            _settingOptions[definition.Index] = [.. definition.Options];
    }

    public void OpenNewGame()
    {
        _newGame = _app.NewGameSettings;
        Show(Screen.NewGame);
    }

    private void DrawNewGame(float fade)
    {
        BeginRoot("##newgame"u8);
        var catalog = _app.Content.SettingCatalog;
        var min = new float2(Kit.Px(96), _ui.DisplaySize.Y * 0.08f + Slide(fade));
        var size = new float2(Kit.Px(WorldRowWidth + Theme.Space6 * 2 + 30), Math.Min(Kit.Px(1100), _ui.DisplaySize.Y * 0.92f - Kit.Px(72)));
        BeginPanel("##newgamepanel"u8, min, size);

        Kit.Caption("LOCAL GAME"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("New Game"u8);
        Kit.Space(Theme.Space2);
        Kit.Body("World settings. The server keeps them for as long as the game runs."u8);
        Kit.Space(Theme.Space4);

        // The list scrolls on its own so the actions below it stay in view however short the window is.
        var footer = Kit.Px(Theme.ControlHeight + Theme.Space4);
        var listHeight = Math.Max(Kit.Px(80), ImGui.GetContentRegionAvail().Y - footer);
        ImGui.BeginChild("##newgamelist"u8, new float2(0, listHeight), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
        ImGui.Indent(Kit.Px(MarkerGutter));
        var changed = 0;
        foreach (var definition in catalog.Definitions)
        {
            if (_newGame.Get(definition) != definition.Default)
                changed++;
            DrawSettingEditor(definition);
        }

        ImGui.Unindent(Kit.Px(MarkerGutter));
        ImGui.EndChild();

        Kit.Space(Theme.Space4);
        if (Kit.Button("Start"u8, ButtonKind.Primary, 160))
            _app.StartLocalGame(_newGame);
        ImGui.SameLine(0, Kit.Px(Theme.Space3));
        if (Kit.Button("Back"u8, ButtonKind.Secondary, 120) || BackPressed())
        {
            _app.NewGameSettings = _newGame;
            Show(Screen.MainMenu);
        }

        if (changed > 0)
        {
            ImGui.SameLine(0, Kit.Px(Theme.Space3));
            if (Kit.Button("Reset All"u8, ButtonKind.Secondary, 140))
                _newGame = catalog.Defaults;
        }

        EndPanel();
        DrawHints(back: true);
        EndRoot();
    }

    /// <summary>One setting on the New game screen: the control its kind calls for, its explanation and a reset.</summary>
    private void DrawSettingEditor(SettingDefinition definition)
    {
        ImGui.PushID(definition.Id);
        var top = ImGui.GetCursorScreenPos();
        var value = _newGame.Get(definition);
        switch (definition.Kind)
        {
            case SettingKind.Multiplier:
                if (Kit.Slider(definition.Name, ref value, definition.Minimum, definition.Maximum, definition.Step, Kit.F($"{value:0.##}x"), WorldRowWidth))
                    _newGame.Set(definition, value);
                break;
            case SettingKind.Choice:
                var index = (int)value;
                if (Kit.Choice(definition.Name, _settingOptions[definition.Index], ref index, WorldRowWidth))
                    _newGame.Set(definition, index);
                break;
            default:
                var on = value != 0;
                if (Kit.Toggle(definition.Name, ref on, WorldRowWidth))
                    _newGame.Set(definition, on ? 1 : 0);
                break;
        }

        var changed = _newGame.Get(definition) != definition.Default;
        Kit.Space(Theme.Space1);
        ImGui.Indent(Kit.Px(Theme.Space3));
        var textTop = ImGui.GetCursorScreenPos();
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, definition.Description, WorldRowWidth - 150);
        var bottom = ImGui.GetCursorScreenPos().Y;
        if (changed)
        {
            ImGui.SetCursorScreenPos(new float2(top.X + Kit.Px(WorldRowWidth - 66), textTop.Y));
            if (Kit.LinkButton("RESET"u8))
                _newGame.Set(definition, definition.Default);
            bottom = Math.Max(bottom, ImGui.GetCursorScreenPos().Y);
            ImGui.SetCursorScreenPos(new float2(textTop.X, bottom));
        }

        ImGui.Unindent(Kit.Px(Theme.Space3));
        if (changed)
            ChangedMarker(top, bottom);
        Kit.Space(Theme.Space4);
        ImGui.PopID();
    }

    /// <summary>The accent bar beside a setting whose value is not its default.</summary>
    private static void ChangedMarker(float2 top, float bottom)
    {
        var x = top.X - Kit.Px(10);
        ImGui.GetWindowDrawList().AddRectFilled(new float2(x, top.Y), new float2(x + Kit.Px(3), bottom), Kit.Color(Theme.Accent));
    }

    /// <summary>Opens the in-game menu at the world settings in force, once the session has a snapshot to show.</summary>
    public void RequestWorldSettings()
    {
        _sessionMenuOpen = true;
        _worldSettingsRequested = true;
    }

    /// <summary>Opens the read-only view of the world settings in force, from the in-game menu.</summary>
    private void OpenWorldSettings(SessionSnapshot snapshot)
    {
        var content = snapshot.Content;
        var definitions = content.SettingCatalog.Definitions;
        _inForceValue = new string[definitions.Length];
        _inForceDefault = new string[definitions.Length];
        foreach (var definition in definitions)
        {
            var value = content.Settings.Get(definition);
            _inForceValue[definition.Index] = definition.Kind == SettingKind.Multiplier ? definition.Format(value) + "x" : definition.Format(value);
            _inForceDefault[definition.Index] =
                value == definition.Default ? "" : "default " + definition.Format(definition.Default) + (definition.Kind == SettingKind.Multiplier ? "x" : "");
        }

        _worldSettingsOpen = true;
        _worldSettingsRequested = false;
    }

    private void DrawWorldSettings(SessionSnapshot snapshot)
    {
        DrawDim(0.72f);
        BeginRoot("##worldsettings"u8);
        var content = snapshot.Content;
        var size = new float2(Kit.Px(WorldRowWidth + Theme.Space6 * 2 + 30), Math.Min(Kit.Px(900), _ui.DisplaySize.Y * 0.84f));
        var min = (_ui.DisplaySize - size) * 0.5f;
        BeginPanel("##worldsettingspanel"u8, min, size);

        Kit.Caption(_app.Session is { IsLocal: true } ? "LOCAL GAME"u8 : "MULTIPLAYER"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("World Settings"u8);
        Kit.Space(Theme.Space2);
        Kit.Body("The settings this world runs by. The server chose them when it started."u8);
        Kit.Space(Theme.Space4);

        var footer = Kit.Px(Theme.ControlHeight + Theme.Space4);
        var listHeight = Math.Max(Kit.Px(80), ImGui.GetContentRegionAvail().Y - footer);
        ImGui.BeginChild("##worldsettingslist"u8, new float2(0, listHeight), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
        ImGui.Indent(Kit.Px(MarkerGutter));
        var draw = ImGui.GetWindowDrawList();
        foreach (var definition in content.SettingCatalog.Definitions)
        {
            var top = ImGui.GetCursorScreenPos();
            var rowSize = new float2(Kit.Px(WorldRowWidth), Kit.Px(34));
            var nameSize = Kit.Measure(_ui.Sans, Theme.FontBody, definition.Name);
            Kit.Text(draw, _ui.Sans, Theme.FontBody, top + new float2(Kit.Px(Theme.Space3), (rowSize.Y - nameSize.Y) * 0.5f), Theme.Text, definition.Name);
            var value = _inForceValue[definition.Index];
            var valueSize = Kit.Measure(_ui.Mono, Theme.FontSmall, value);
            var valueAt = top + new float2(rowSize.X - valueSize.X - Kit.Px(Theme.Space3), (rowSize.Y - valueSize.Y) * 0.5f);
            Kit.Text(draw, _ui.Mono, Theme.FontSmall, valueAt, Theme.Text, value);
            var standard = _inForceDefault[definition.Index];
            if (standard.Length > 0)
            {
                var standardSize = Kit.Measure(_ui.Mono, Theme.FontCaption, standard);
                Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(valueAt.X - standardSize.X - Kit.Px(Theme.Space3), top.Y + (rowSize.Y - standardSize.Y) * 0.5f), Theme.Accent, standard);
            }

            draw.AddLine(top + new float2(0, rowSize.Y - 0.5f), top + new float2(rowSize.X, rowSize.Y - 0.5f), Kit.Color(Theme.Line, 0.6f));
            ImGui.Dummy(rowSize);
            Kit.Space(Theme.Space1);
            ImGui.Indent(Kit.Px(Theme.Space3));
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, definition.Description, WorldRowWidth - 150);
            ImGui.Unindent(Kit.Px(Theme.Space3));
            var bottom = ImGui.GetCursorScreenPos().Y;
            if (standard.Length > 0)
                ChangedMarker(top, bottom);
            Kit.Space(Theme.Space4);
        }

        ImGui.Unindent(Kit.Px(MarkerGutter));
        ImGui.EndChild();

        Kit.Space(Theme.Space4);
        if (Kit.Button("Back"u8, ButtonKind.Secondary, 120))
            _worldSettingsOpen = false;

        EndPanel();
        EndRoot();
    }
}
