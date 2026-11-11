using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;
using Prospect.Game.Client;

namespace Prospect.Client.Screens;

internal enum Screen
{
    Splash,
    MainMenu,
    Join,
    Settings,
    NewGame,
    Credits,
    Session,
}

/// <summary>
/// Every screen of the game and the transitions between them. One screen is current at a time; each is drawn as a
/// borderless full-window surface using the kit, fading and sliding in when it becomes current.
/// </summary>
internal sealed partial class ScreenHost
{
    private const ImGuiWindowFlags RootFlags =
        ImGuiWindowFlags.NoDecoration
        | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoBringToFrontOnFocus;

    private readonly App _app;
    private readonly UiContext _ui;
    private Screen _screen = Screen.Splash;
    private Screen _settingsReturn = Screen.MainMenu;
    private float _screenTime;

    public ScreenHost(App app, UiContext ui)
    {
        _app = app;
        _ui = ui;
        InitializeJoin();
        InitializeSettings();
        InitializeNewGame();
    }

    public Screen Current => _screen;

    public void Show(Screen screen)
    {
        if (_screen == Screen.Settings && screen != Screen.Settings)
            LeaveSettings();
        _screen = screen;
        _screenTime = 0;
    }

    public void Draw(SessionSnapshot? snapshot)
    {
        _screenTime += _ui.DeltaTime;
        var fade = Kit.EaseOut(_screenTime / Theme.ScreenEnter);
        Kit.Fade = fade;

        // A session that ends or appears moves the player between the menu and the game regardless of screen.
        if (_screen == Screen.Session && snapshot is null)
            Show(Screen.MainMenu);

        switch (_screen)
        {
            case Screen.Splash:
                DrawSplash();
                break;
            case Screen.MainMenu:
                DrawBackdrop();
                DrawMainMenu(fade);
                break;
            case Screen.Join:
                DrawBackdrop();
                DrawJoin(fade);
                break;
            case Screen.Settings:
                if (_settingsReturn == Screen.Session)
                    DrawDim(0.72f);
                else
                    DrawBackdrop();
                DrawSettings(fade);
                break;
            case Screen.NewGame:
                DrawBackdrop();
                DrawNewGame(fade);
                break;
            case Screen.Credits:
                DrawBackdrop();
                DrawCredits(fade);
                break;
            case Screen.Session when snapshot is not null:
                DrawSession(snapshot, fade);
                break;
        }

        Kit.Fade = 1f;
        if (_app.ShowPerformance)
            DrawPerformance(snapshot);
    }

    /// <summary>Opens a full-window surface for a screen. Always pair with <see cref="EndRoot"/>.</summary>
    private void BeginRoot(ImString name, ImGuiWindowFlags extraFlags = default)
    {
        ImGui.SetNextWindowPos(float2.Zero);
        ImGui.SetNextWindowSize(_ui.DisplaySize);
        ImGui.Begin(name, RootFlags | extraFlags);
    }

    private static void EndRoot() => ImGui.End();

    /// <summary>The slide-in offset for content of the current screen.</summary>
    private static float Slide(float fade) => (1f - fade) * Kit.Px(16);

    /// <summary>Opens a panel at a position: draws its frame and starts a padded layout region inside it.</summary>
    private static void BeginPanel(ImString id, float2 min, float2 size)
    {
        Kit.PanelFrame(ImGui.GetWindowDrawList(), min, min + size);
        var padding = Kit.Px(Theme.Space6);
        ImGui.SetCursorScreenPos(min + new float2(padding, padding));
        ImGui.BeginChild(id, size - new float2(padding * 2, padding * 2), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
    }

    private static void EndPanel() => ImGui.EndChild();

    /// <summary>The menu backdrop: a deep gradient, a faint survey grid and slowly turning orbit rings.</summary>
    private void DrawBackdrop()
    {
        var draw = ImGui.GetBackgroundDrawList();
        var size = _ui.DisplaySize;
        var top = Kit.Packed(Theme.Backdrop);
        var bottom = Kit.Packed(Theme.Void);
        draw.AddRectFilledMultiColor(float2.Zero, size, top, top, bottom, bottom);

        var step = Kit.Px(72);
        var grid = Kit.Opaque(Theme.LineStrong, 0.10f);
        for (var x = step; x < size.X; x += step)
            draw.AddLine(new float2(x, 0), new float2(x, size.Y), grid);
        for (var y = step; y < size.Y; y += step)
            draw.AddLine(new float2(0, y), new float2(size.X, y), grid);

        var center = new float2(size.X * 0.74f, size.Y * 0.52f);
        for (var ring = 0; ring < 4; ring++)
        {
            var radius = size.Y * (0.16f + ring * 0.13f);
            draw.AddCircle(center, radius, Kit.Opaque(Theme.LineStrong, 0.42f - ring * 0.07f), 128);

            // One body per ring, the outer ones slower, as in a real system.
            var angle = _ui.Time * (0.16f / (1f + ring * 0.9f)) + ring * 1.9f;
            var body = center + new float2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            draw.AddCircleFilled(body, Kit.Px(ring == 1 ? 4f : 2.5f), Kit.Opaque(ring == 1 ? Theme.Accent : Theme.TextDim), 16);
        }

        draw.AddCircleFilled(center, Kit.Px(5), Kit.Opaque(Theme.Text), 24);
        draw.AddCircle(center, Kit.Px(11), Kit.Opaque(Theme.Text, 0.25f), 32);
    }

    private void DrawDim(float alpha)
    {
        ImGui.GetBackgroundDrawList().AddRectFilled(float2.Zero, _ui.DisplaySize, Kit.Opaque(Theme.Void, alpha));
    }

    /// <summary>The strip of key hints along the bottom edge of a screen.</summary>
    private void DrawHints(bool back)
    {
        var draw = ImGui.GetWindowDrawList();
        var position = new float2(Kit.Px(96), _ui.DisplaySize.Y - Kit.Px(56));
        var pad = _app.Devices.PadConnected;
        position.X += Kit.KeyHint(draw, position, pad ? "D-PAD"u8 : "ARROWS"u8, "Navigate"u8);
        position.X += Kit.KeyHint(draw, position, pad ? "A"u8 : "ENTER"u8, "Select"u8);
        if (back)
            Kit.KeyHint(draw, position, pad ? "START"u8 : "ESC"u8, "Back"u8);
    }
}
