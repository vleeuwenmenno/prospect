using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

internal sealed partial class ScreenHost
{
    /// <summary>Seconds the framework logo stays fully visible before the menu may appear.</summary>
    private const float SplashHold = 2.4f;
    private const float SplashFade = 0.4f;
    private const float SplashSkippableAfter = 1.2f;

    private float _splashEnd = SplashFade + SplashHold;

    /// <summary>The BRUTAL logo on black, shown before anything else as its licence requires.</summary>
    private void DrawSplash()
    {
        var draw = ImGui.GetBackgroundDrawList();
        var size = _ui.DisplaySize;
        draw.AddRectFilled(float2.Zero, size, Kit.Opaque(0x000000));

        var skip =
            _screenTime > SplashSkippableAfter
            && (_app.Actions.Pressed(_app.Devices, InputAction.Pause, true)
                || ImGui.IsKeyPressed(ImGuiKey.Enter, false)
                || ImGui.IsKeyPressed(ImGuiKey.Space, false)
                || ImGui.IsKeyPressed(ImGuiKey.GamepadFaceDown, false)
                || ImGui.IsMouseClicked(ImGuiMouseButton.Left));
        if (skip && _screenTime < _splashEnd)
            _splashEnd = _screenTime;

        var alpha = Math.Min(Kit.EaseOut(_screenTime / SplashFade), 1f - Kit.EaseOut((_screenTime - _splashEnd) / SplashFade));
        var logo = _app.BrutalLogo;
        var width = Math.Min(Kit.Px(440), size.X * 0.5f);
        var height = width * logo.Height / logo.Width;
        var min = new float2((size.X - width) * 0.5f, (size.Y - height) * 0.5f);
        draw.AddImage(_app.BrutalLogoRef, min, min + new float2(width, height), float2.Zero, float2.One, Kit.Opaque(0xFFFFFF, alpha));

        if (_screenTime >= _splashEnd + SplashFade)
            _app.SplashFinished();
    }

    private void DrawMainMenu(float fade)
    {
        BeginRoot("##menu"u8);
        var draw = ImGui.GetWindowDrawList();
        var size = _ui.DisplaySize;
        var left = Kit.Px(96);
        // A short window keeps the menu whole: a smaller title, and the entries scroll in what is left above the hints.
        var shortWindow = size.Y < Kit.Px(760);
        var y = (shortWindow ? Math.Max(HudTop + Kit.Px(Theme.Space4), size.Y * 0.06f) : size.Y * 0.2f) + Slide(fade);

        Kit.Text(draw, _ui.SansMedium, Theme.FontCaption, new float2(left + Kit.Px(4), y), Theme.Accent, "COMMAND POD  /  FLEET OPERATIONS"u8);
        y += Kit.Px(18);
        var title = Kit.Text(draw, _ui.SansBold, shortWindow ? Theme.FontHeading : Theme.FontTitle, new float2(left, y), Theme.Text, "PROSPECT"u8);
        y += title.Y + Kit.Px(2);
        draw.AddRectFilled(new float2(left + Kit.Px(4), y), new float2(left + Kit.Px(4) + title.X * 0.5f, y + Kit.Px(2)), Kit.Color(Theme.Accent));
        if (!shortWindow)
        {
            y += Kit.Px(Theme.Space4);
            Kit.Text(draw, _ui.Sans, Theme.FontBody, new float2(left + Kit.Px(4), y), Theme.TextDim, "Cut the rock. Haul the ore. Build the fleet."u8);
            y += Kit.Px(Theme.Space7);
        }

        y += Kit.Px(shortWindow ? Theme.Space3 + 8 : 8);
        ImGui.SetCursorScreenPos(new float2(left, y));
        var itemsHeight = Math.Max(Kit.Px(46), size.Y - Kit.Px(72) - y);
        ImGui.BeginChild("##menuitems"u8, new float2(Kit.Px(400), itemsHeight), ImGuiChildFlags.NavFlattened, ImGuiWindowFlags.NoBackground);
        draw = ImGui.GetWindowDrawList();
        ImGui.BeginGroup();
        // Continue is the world played last, named beside it; a world that is gone is reported when chosen, never
        // made again.
        if (_app.Worlds.List.LastWorld is not null)
        {
            if (Kit.MenuItem("Continue"u8, 360))
            {
                _menuError = "";
                _app.ContinueWorld();
            }

            var item = ImGui.GetItemRectMin();
            var last = _app.Worlds.LastWorld;
            var label = last?.Name ?? "Last world not found";
            var labelWidth = Math.Min(Kit.Measure(_ui.Mono, Theme.FontCaption, label).X, Kit.Px(200));
            Kit.ClippedText(draw, _ui.Mono, Theme.FontCaption, item + new float2(Kit.Px(360) - labelWidth - Kit.Px(Theme.Space3), Kit.Px(17)), last is { Ready: true } ? Theme.TextDim : Theme.Bad, label, Kit.Px(200));
        }

        if (_app.SavesWorlds && Kit.MenuItem("Load World"u8, 360))
            OpenLoadWorld();
        if (Kit.MenuItem("New Game"u8, 360))
            OpenNewGame();
        if (Kit.MenuItem("Join Server"u8, 360))
            Show(Screen.Join);
        if (Kit.MenuItem("Friends"u8, 360))
            Show(Screen.Account);
        if (Kit.MenuItem("Settings"u8, 360))
            OpenSettings(Screen.MainMenu);
        if (Kit.MenuItem("Credits"u8, 360))
            Show(Screen.Credits);
        if (Kit.MenuItem("Quit"u8, 360))
            _app.Quit();
        ImGui.EndGroup();
        if (_menuError.Length > 0)
        {
            Kit.Space(Theme.Space2);
            ImGui.Indent(Kit.Px(Theme.Space4));
            Kit.Paragraph(_ui.Sans, Theme.FontSmall, Theme.Bad, _menuError, 420);
            ImGui.Unindent(Kit.Px(Theme.Space4));
        }

        ImGui.EndChild();
        draw = ImGui.GetWindowDrawList();

        var version = Kit.Measure(_ui.Mono, Theme.FontCaption, App.VersionLabel);
        Kit.Text(draw, _ui.Mono, Theme.FontCaption, new float2(size.X - version.X - Kit.Px(96), size.Y - Kit.Px(50)), Theme.TextMute, App.VersionLabel);
        DrawHints(back: false);
        EndRoot();
    }

    private void DrawCredits(float fade)
    {
        BeginRoot("##credits"u8);
        var min = new float2(Kit.Px(96), _ui.DisplaySize.Y * 0.12f + Slide(fade));
        var size = new float2(Kit.Px(680), Kit.Px(628));
        BeginPanel("##creditspanel"u8, min, size);

        Kit.Caption("CREDITS"u8, Theme.Accent);
        Kit.Space(Theme.Space1);
        Kit.Heading("PROSPECT"u8);
        Kit.Space(Theme.Space2);
        Kit.Body("A space sandbox about mining, building and commanding a fleet."u8);
        Kit.Space(Theme.Space5);

        Kit.Caption("BUILT WITH"u8);
        Kit.Space(Theme.Space3);
        var logo = _app.BrutalLogo;
        var logoWidth = Kit.Px(220);
        ImGui.Image(_app.BrutalLogoRef, new float2(logoWidth, logoWidth * logo.Height / logo.Width));
        Kit.Space(Theme.Space3);
        Kit.Body("BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited."u8, Theme.Text);
        Kit.Space(Theme.Space1);
        Kit.Body("Used under the BRUTAL License v1.0.  www.ahwoo.com/brutal"u8);
        Kit.Space(Theme.Space5);

        Kit.Caption("THIRD-PARTY SOFTWARE"u8);
        Kit.Space(Theme.Space2);
        Kit.Body("Dear ImGui  -  Omar Cornut and contributors  -  MIT"u8);
        Kit.Body("GLFW  -  Marcus Geelnard, Camilla Lowy  -  zlib"u8);
        Kit.Body("RakNet  -  Oculus VR, LLC  -  BSD"u8);
        Kit.Body("Vulkan Memory Allocator  -  Advanced Micro Devices  -  MIT"u8);
        Kit.Body("FreeType  -  The FreeType Project  -  FTL"u8);
        Kit.Body("stb  -  Sean Barrett  -  MIT / public domain"u8);
        Kit.Space(Theme.Space2);

        Kit.Caption("TYPEFACES"u8);
        Kit.Space(Theme.Space2);
        Kit.Body("Space Grotesk  -  Florian Karsten  -  SIL Open Font License 1.1"u8);
        Kit.Body("JetBrains Mono  -  JetBrains  -  SIL Open Font License 1.1"u8);
        Kit.Body("Noto Sans JP  -  Adobe  -  SIL Open Font License 1.1"u8);
        Kit.Space(Theme.Space5);
        Kit.Label(_ui.Sans, Theme.FontSmall, Theme.TextMute, "Full licence texts are in the LICENSES folder beside the game."u8);
        Kit.Space(Theme.Space5);

        if (Kit.Button("Back"u8, ButtonKind.Secondary, 120) || BackPressed())
            Show(Screen.MainMenu);

        EndPanel();
        DrawHints(back: true);
        EndRoot();
    }

    /// <summary>True when the player asked to go back (Escape or the pad's Start button).</summary>
    private bool BackPressed() => _screenTime > 0.1f && _app.Actions.Pressed(_app.Devices, InputAction.Pause, true);
}
