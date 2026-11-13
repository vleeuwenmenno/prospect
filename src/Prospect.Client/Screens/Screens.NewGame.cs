using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;
using Prospect.Game.Lobby;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    private float WorldRowWidth => Math.Clamp(_ui.DisplaySize.X / Kit.Scale - Theme.Space6 * 2 - 62, 320, 600);

    /// <summary>Room left of each setting for the bar that marks a changed one.</summary>
    private const float MarkerGutter = 14;

    private static readonly string[] VisibilityOptions = ["Private", "Unlisted", "Public"];

    /// <summary>The settings being chosen on the New game screen; handed to the app on Start or Back.</summary>
    private WorldSettings _newGame;

    /// <summary>How the game is hosted, as chosen on the New game screen; the text fields hold the rest until Start or Back.</summary>
    private HostingOptions _hosting = new();
    private readonly ImInputString _hostName = new(ServerNames.MaxBytes + 1);
    private readonly ImInputString _hostPassword = new(PasswordBytes);
    private readonly ImInputString _hostPort = new(8);
    private bool _hostPasswordShown;
    private string _newGameError = "";

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

    /// <summary>Opens the New game screen with the next game's settings, and the reason the last start failed if it did.</summary>
    public void OpenNewGame(string error = "")
    {
        _newGame = _app.NewGameSettings;
        _hosting = _app.NewGameHosting.Clone();
        var name = _hosting.Name;
        var fallback = _hosting.Visibility == ServerVisibility.Private && _app.SavesWorlds ? $"{_app.Settings.PlayerName}'s world" : $"{_app.Settings.PlayerName}'s game";
        if (name.Length == 0 && !ServerNames.TryNormalize(fallback, out name))
            name = "";
        _hostName.SetValue(name);
        _hostPassword.SetValue(_hosting.Password);
        _hostPort.SetValue(_hosting.Port.ToString());
        _hostPasswordShown = false;
        _newGameError = error;
        Show(Screen.NewGame);
    }

    /// <summary>
    /// Reads the hosting fields into <see cref="_hosting"/>. False, with a sentence in <see cref="_newGameError"/>,
    /// when the port is not a port; the name and the password are checked by the game when it starts.
    /// </summary>
    private bool ReadHostingFields()
    {
        _hosting.Name = _hostName.ToString().Trim();
        _hosting.Password = _hostPassword.ToString();
        if (ushort.TryParse(_hostPort.ToString(), out var port) && port > 0)
        {
            _hosting.Port = port;
            return true;
        }

        if (_hosting.Visibility == ServerVisibility.Private)
            return true;
        _newGameError = "The port must be a number from 1 to 65535.";
        return false;
    }

    private void StartNewGame()
    {
        _newGameError = "";
        if (!ReadHostingFields())
            return;
        if (!_app.StartLocalGame(_newGame, _hosting, out var error))
            _newGameError = error;
    }

    /// <summary>The hosting section of the New game screen: who can join, and when others can, how.</summary>
    private void DrawHostingEditor()
    {
        Kit.Caption("HOSTING"u8);
        Kit.Space(Theme.Space2);
        var visibility = (int)_hosting.Visibility;
        if (Kit.Choice("Who can join"u8, VisibilityOptions, ref visibility, WorldRowWidth))
        {
            _hosting.Visibility = (ServerVisibility)visibility;
            _newGameError = "";
        }

        Kit.Space(Theme.Space1);
        ImGui.Indent(Kit.Px(Theme.Space3));
        switch (_hosting.Visibility)
        {
            case ServerVisibility.Private:
                Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Only you. Nothing is opened on the network and nobody else can join."u8, WorldRowWidth - 24);
                Kit.Space(Theme.Space1);
                if (_app.SavesWorlds)
                    Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Good, Kit.F($"Saved automatically every {_app.Settings.AutosaveSeconds} s and when you leave. Continue it later from the main menu."), WorldRowWidth - 24);
                else
                    Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Accent, "Saved worlds are off for this run: this game is not saved."u8, WorldRowWidth - 24);
                break;
            case ServerVisibility.Unlisted:
                Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, "Others join with your address and the port below. It is not shown in the server browser."u8, WorldRowWidth - 24);
                break;
            default:
                if (_app.Listing is { } listing)
                    Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextDim, Kit.F($"Shown in the server browser on {listing.Url.Host}. Others join from there, or with your address and the port below."), WorldRowWidth - 24);
                else
                    Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Bad, "The server list is turned off, so this game is not listed. Others can still join with your address and the port below."u8, WorldRowWidth - 24);
                break;
        }

        if (_hosting.Visibility != ServerVisibility.Private && _app.Accounts.View.State != AccountState.SignedIn)
        {
            // Hosting for others needs a sign-in; choosing Private again takes the need away. Start says why it
            // refuses until then, and nothing starts by itself once signed in.
            Kit.Space(Theme.Space3);
            DrawSignInGate(true, Kit.Px(WorldRowWidth - 24));
        }

        if (_hosting.Visibility != ServerVisibility.Private)
        {
            Kit.Space(Theme.Space1);
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Accent, "The port must be reachable from outside: forward it on your router if you have one. The game does not do that for you."u8, WorldRowWidth - 24);
            Kit.Space(Theme.Space1);
            Kit.Paragraph(
                _ui.Sans,
                Theme.FontSmall,
                Theme.Accent,
                "A hosted game is not saved yet: the game cannot yet tell who a returning player is. Choose Private for a world that is saved."u8,
                WorldRowWidth - 24
            );
        }

        ImGui.Unindent(Kit.Px(Theme.Space3));

        if (_hosting.Visibility == ServerVisibility.Private && _app.SavesWorlds)
        {
            Kit.Space(Theme.Space4);
            Kit.TextField("WORLD NAME"u8, "##worldname"u8, _hostName, WorldRowWidth);
        }

        if (_hosting.Visibility != ServerVisibility.Private)
        {
            Kit.Space(Theme.Space4);
            Kit.TextField("GAME NAME"u8, "##hostname"u8, _hostName, WorldRowWidth);
            Kit.Space(Theme.Space3);
            Kit.PasswordField("PASSWORD (OPTIONAL)"u8, "##hostpassword"u8, _hostPassword, ref _hostPasswordShown, WorldRowWidth - 70);
            Kit.Space(Theme.Space1);
            Kit.Label(_ui.Sans, Theme.FontSmall, Theme.TextMute, Kit.F($"Up to {Passwords.MaxChars} characters. Leave it empty for none. You are never asked for it yourself."));
            Kit.Space(Theme.Space3);
            var slots = (float)Math.Clamp(_hosting.MaxPlayers, 2, 64);
            if (Kit.Slider("Player slots"u8, ref slots, 2, 64, 1, Kit.F($"{(int)slots}"), WorldRowWidth))
                _hosting.MaxPlayers = (int)slots;
            _hosting.MaxPlayers = (int)slots;
            Kit.Space(Theme.Space1);
            ImGui.Indent(Kit.Px(Theme.Space3));
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Players at once, you included."u8, WorldRowWidth - 24);
            ImGui.Unindent(Kit.Px(Theme.Space3));
            Kit.Space(Theme.Space3);
            Kit.TextField("PORT (UDP)"u8, "##hostport"u8, _hostPort, 140, ImGuiInputTextFlags.CharsDecimal);
        }

        Kit.Space(Theme.Space5);
        Kit.Caption("WORLD"u8);
        Kit.Space(Theme.Space2);
    }

    private void DrawNewGame(float fade)
    {
        BeginRoot("##newgame"u8);
        var catalog = _app.Content.SettingCatalog;
        var min = new float2(Kit.Px(96), _ui.DisplaySize.Y * 0.08f + Slide(fade));
        var size = new float2(Kit.Px(WorldRowWidth + Theme.Space6 * 2 + 30), Math.Min(Kit.Px(1100), _ui.DisplaySize.Y * 0.92f - Kit.Px(72)));
        min.X = Math.Min(min.X, Math.Max(Kit.Px(8), (_ui.DisplaySize.X - size.X) * 0.5f));
        BeginPanel("##newgamepanel"u8, min, size);

        if (Compact)
        {
            Kit.Caption("LOCAL GAME  /  NEW GAME"u8, Theme.Accent);
            Kit.Space(Theme.Space3);
        }
        else
        {
            Kit.Caption("LOCAL GAME"u8, Theme.Accent);
            Kit.Space(Theme.Space1);
            Kit.Heading("New Game"u8);
            Kit.Space(Theme.Space2);
            Kit.Body("A new world: who can join, its name and its settings. Its settings stay as they are chosen here."u8);
            Kit.Space(Theme.Space4);
        }

        // The list scrolls on its own so the actions below it stay in view however short the window is.
        var errorHeight = 0f;
        if (_newGameError.Length > 0)
        {
            ImGui.PushFont(_ui.Sans, Theme.FontSmall);
            errorHeight = ImGui.CalcTextSize(_newGameError, false, Kit.Px(WorldRowWidth)).Y + Kit.Px(Theme.Space2);
            ImGui.PopFont();
        }

        var footer = Kit.Px(Theme.ControlHeight + Theme.Space4) + errorHeight;
        var listHeight = Math.Max(Kit.Px(80), ImGui.GetContentRegionAvail().Y - footer);
        ImGui.BeginChild("##newgamelist"u8, new float2(0, listHeight), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
        ImGui.Indent(Kit.Px(MarkerGutter));
        DrawHostingEditor();
        var changed = 0;
        foreach (var definition in catalog.Definitions)
        {
            if (_newGame.Get(definition) != definition.Default)
                changed++;
        }

        DrawSettingGroups(catalog, false);
        ImGui.Unindent(Kit.Px(MarkerGutter));
        ImGui.EndChild();

        Kit.Space(Theme.Space4);
        if (_newGameError.Length > 0)
        {
            // A failed start stays on this screen with the reason, so the player can change what is wrong.
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Bad, _newGameError, WorldRowWidth);
            Kit.Space(Theme.Space2);
        }

        var label = _hosting.Visibility != ServerVisibility.Private ? "Host Game"u8 : _app.SavesWorlds ? "Create World"u8 : "Start"u8;
        if (Kit.Button(label, ButtonKind.Primary, 180))
            StartNewGame();
        ImGui.SameLine(0, Kit.Px(Theme.Space3));
        if (Kit.Button("Back"u8, ButtonKind.Secondary, 120) || BackPressed())
        {
            _app.NewGameSettings = _newGame;
            ReadHostingFields();
            _newGameError = "";
            _app.NewGameHosting = _hosting.Clone();
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
        if (definition.Upper is { } upper)
        {
            var high = _newGame.Get(upper);
            if (Kit.RangeSlider(definition.Name, ref value, ref high, definition.Minimum, definition.Maximum, definition.Step, definition.Unit, WorldRowWidth))
            {
                _newGame.Set(definition, value);
                _newGame.Set(upper, high);
            }
        }
        else switch (definition.Kind)
            {
                case SettingKind.Integer:
                case SettingKind.Multiplier:
                    if (Kit.Slider(definition.Name, ref value, definition.Minimum, definition.Maximum, definition.Step, definition.Kind == SettingKind.Multiplier ? Kit.F($"{value:0.##}{definition.Unit}") : Kit.F($"{value:0}"), WorldRowWidth))
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

        var changed = _newGame.Get(definition) != definition.Default || (definition.Upper is { } paired && _newGame.Get(paired) != paired.Default);
        Kit.Space(Theme.Space1);
        ImGui.Indent(Kit.Px(Theme.Space3));
        var textTop = ImGui.GetCursorScreenPos();
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, definition.Description, WorldRowWidth - 150);
        var bottom = ImGui.GetCursorScreenPos().Y;
        if (changed)
        {
            ImGui.SetCursorScreenPos(new float2(top.X + Kit.Px(WorldRowWidth - 66), textTop.Y));
            if (Kit.LinkButton("RESET"u8))
            {
                _newGame.Set(definition, definition.Default);
                if (definition.Upper is { } resetUpper) _newGame.Set(resetUpper, resetUpper.Default);
            }
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
            _inForceValue[definition.Index] = definition.Kind == SettingKind.Multiplier ? definition.Format(value) + definition.Unit : definition.Format(value);
            _inForceDefault[definition.Index] =
                value == definition.Default ? "" : "default " + definition.Format(definition.Default) + (definition.Kind == SettingKind.Multiplier ? definition.Unit : "");
        }

        foreach (var definition in definitions)
            if (definition.Upper is { } upper)
            {
                _inForceValue[definition.Index] = definition.Format(content.Settings.Get(definition)) + " – " + upper.Format(content.Settings.Get(upper)) + " " + definition.Unit;
                _inForceDefault[definition.Index] = content.Settings.Get(definition) == definition.Default && content.Settings.Get(upper) == upper.Default ? "" : "default " + definition.Format(definition.Default) + " – " + upper.Format(upper.Default);
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
        DrawSettingGroups(content.SettingCatalog, true);

        ImGui.Unindent(Kit.Px(MarkerGutter));
        ImGui.EndChild();

        Kit.Space(Theme.Space4);
        if (Kit.Button("Back"u8, ButtonKind.Secondary, 120))
            _worldSettingsOpen = false;

        EndPanel();
        EndRoot();
    }
    private void DrawSettingGroups(WorldSettingCatalog catalog, bool readOnly)
    {
        DrawSettingGroup(catalog, "", "", readOnly);
        if (ImGui.CollapsingHeader("Asteroids"u8, ImGuiTreeNodeFlags.DefaultOpen)) DrawSettingGroup(catalog, "Asteroids", "", readOnly);
        if (ImGui.CollapsingHeader("Resources / Materials"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawSettingGroup(catalog, "Resources", "", readOnly);
            foreach (var resource in _app.Content.Galaxy.Resources)
            {
                ImGui.PushID(resource.Id);
                if (ImGui.CollapsingHeader(resource.Name)) DrawSettingGroup(catalog, "Resources", resource.Id, readOnly);
                ImGui.PopID();
            }
        }
    }

    private void DrawSettingGroup(WorldSettingCatalog catalog, string group, string material, bool readOnly)
    {
        foreach (var definition in catalog.Definitions)
        {
            if (definition.IsUpper || definition.Group != group || definition.Material != material) continue;
            if (readOnly) DrawInForceSetting(definition);
            else DrawSettingEditor(definition);
        }
    }

    private void DrawInForceSetting(SettingDefinition definition)
    {
        var draw = ImGui.GetWindowDrawList();
        var top = ImGui.GetCursorScreenPos();
        Kit.Label(_ui.Sans, Theme.FontBody, Theme.Text, definition.Name);
        Kit.Label(_ui.Mono, Theme.FontSmall, Theme.Text, _inForceValue[definition.Index]);
        var standard = _inForceDefault[definition.Index];
        if (standard.Length > 0)
            Kit.Label(_ui.Mono, Theme.FontCaption, Theme.Accent, standard);
        var line = ImGui.GetCursorScreenPos();
        draw.AddLine(line, line + new float2(Kit.Px(WorldRowWidth), 0), Kit.Color(Theme.Line, 0.6f));
        Kit.Space(Theme.Space1);
        ImGui.Indent(Kit.Px(Theme.Space3));
        Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.TextMute, definition.Description, WorldRowWidth - 150);
        ImGui.Unindent(Kit.Px(Theme.Space3));
        var bottom = ImGui.GetCursorScreenPos().Y;
        if (standard.Length > 0)
            ChangedMarker(top, bottom);
        Kit.Space(Theme.Space4);
    }

}
