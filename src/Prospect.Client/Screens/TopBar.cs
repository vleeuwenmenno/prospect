using Brutal.ImGuiApi;
using Brutal.Numerics;
using Prospect.Client.Ui;

namespace Prospect.Client.Screens;

/// <summary>Where the navigation bar's tabs and its account area go, in pixels.</summary>
internal readonly record struct TopBarLayout(float BrandWidth, float AccountLeft, float AccountWidth, float Height);

/// <summary>
/// The interactive part of the navigation bar along the top edge: its window, its layout and its tabs. The look and
/// what a tab does are <c>ScreenHost</c>'s; this part has no fonts, so the frame order tests run it as it is. Over
/// the map the bar's rectangle is blocked for the map's gestures and the bar claims the map's stale hover, as the
/// map's other windows do.
/// </summary>
internal static class TopBar
{
    /// <summary>The brand mark's width, a tab's padding at each side and the narrowest account area, unscaled.</summary>
    public const float BrandWidth = 48f;
    public const float TabPad = 16f;
    public const float TightTabPad = 8f;
    public const float AccountMin = 96f;
    public const float AccountMax = 260f;

    // Never raised by a click and never in keyboard navigation, so a click on it while flying with the pointer
    // released leaves W and Space to the hull.
    private const ImGuiWindowFlags Flags = ScreenHost.RootFlags | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    /// <summary>
    /// Lays the bar out: the tabs from the brand mark to the right, each its label's width plus padding, and the
    /// account area at the right edge, as wide as asked up to <see cref="AccountMax"/>. Short of room the tabs'
    /// padding tightens first, then the account area narrows to <see cref="AccountMin"/>, where its text is cut.
    /// Writes each tab's left edge and width.
    /// </summary>
    public static TopBarLayout Layout(float2 display, float scale, ReadOnlySpan<float> labels, float accountWant, Span<float> tabLeft, Span<float> tabWidth)
    {
        float Px(float value) => MathF.Round(value * scale);
        var brand = Px(BrandWidth);
        var margin = Px(HudLayout.Gap);
        var accountMin = Math.Min(Px(AccountMin), Math.Max(0f, accountWant));
        var labelsWidth = 0f;
        foreach (var label in labels)
            labelsWidth += label;
        var room = display.X - brand - margin * 2 - accountMin;
        var pad = labelsWidth + labels.Length * Px(TabPad) * 2 <= room ? Px(TabPad) : Px(TightTabPad);

        var x = brand;
        for (var i = 0; i < labels.Length; i++)
        {
            tabLeft[i] = x;
            tabWidth[i] = labels[i] + pad * 2;
            x += tabWidth[i];
        }

        var accountWidth = Math.Clamp(Math.Min(accountWant, Px(AccountMax)), 0f, Math.Max(0f, display.X - x - margin * 2));
        return new TopBarLayout(brand, display.X - margin - accountWidth, accountWidth, HudLayout.TopBarBottom(scale));
    }

    /// <summary>Opens the bar's window across the top, above every other. Pair with <see cref="End"/>.</summary>
    public static void Begin(float2 display, float scale, MapPointer? map)
    {
        ImGui.SetNextWindowPos(float2.Zero);
        ImGui.SetNextWindowSize(new float2(Math.Max(display.X, 1f), HudLayout.TopBarBottom(scale)));
        ImGui.Begin("##topbar"u8, Flags);
        ImGui.Internal.BringWindowToDisplayFront(ImGui.Internal.GetCurrentWindow());
        map?.Claim();
    }

    /// <summary>One tab or the account area's button.</summary>
    public static bool Tab(ImString id, float2 min, float2 size)
    {
        ImGui.SetCursorScreenPos(min);
        return ImGui.InvisibleButton(id, new float2(Math.Max(size.X, 1f), Math.Max(size.Y, 1f)));
    }

    public static void End() => ImGui.End();
}
