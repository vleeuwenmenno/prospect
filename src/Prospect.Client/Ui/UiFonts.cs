using Brutal.ImGuiApi;
using Prospect.Engine.IO;

namespace Prospect.Client.Ui;

/// <summary>The four interface fonts, each with the Japanese face merged in behind it.</summary>
internal readonly record struct UiFontSet(ImFontPtr Sans, ImFontPtr SansMedium, ImFontPtr SansBold, ImFontPtr Mono);

/// <summary>
/// Loads the shipped interface fonts into a Dear ImGui atlas. Player names, chat and server names can be Japanese,
/// which Space Grotesk and JetBrains Mono do not cover, so every font gets Noto Sans JP merged in as a second
/// source. The game and the tests load through here, so they measure the same fonts.
/// </summary>
internal static class UiFonts
{
    /// <summary>The Japanese face merged behind every interface font, in <c>assets/fonts</c>.</summary>
    public const string JapaneseFile = "NotoSansJP-Regular.otf";

    /// <summary>
    /// Inclusive code point ranges the Japanese face must not supply, ending in 0. What is left is CJK punctuation,
    /// kana, enclosed and compatibility CJK, the ideographs and their extensions, and the full and half width forms.
    /// Latin, Greek, Cyrillic, symbols, U+FFFD and emoji are excluded, so the primary fonts keep their own glyphs and
    /// advances, and a character neither font has still shows as the primary font's fallback glyph.
    /// </summary>
    private static ReadOnlySpan<uint> NotJapanese =>
        [0x0001, 0x2FFF, 0x3100, 0x31EF, 0x4DC0, 0x4DFF, 0xA000, 0xF8FF, 0xFB00, 0xFEFF, 0xFFF0, 0x1FFFF, 0x30000, 0x10FFFF, 0];

    /// <summary>Adds the interface fonts at the body size; the atlas's font loader must already be set.</summary>
    public static UiFontSet Load(ImFontAtlasPtr atlas) =>
        new(
            Add(atlas, "SpaceGrotesk-Regular.ttf"),
            Add(atlas, "SpaceGrotesk-Medium.ttf"),
            Add(atlas, "SpaceGrotesk-Bold.ttf"),
            Add(atlas, "JetBrainsMono-Regular.ttf")
        );

    private static unsafe ImFontPtr Add(ImFontAtlasPtr atlas, string file)
    {
        var font = atlas.AddFontFromFileTTF(AppPaths.Asset("fonts", file), Theme.FontBody);

        // Dear ImGui copies the config and the exclusion list, so both may live on the stack. The defaults are those
        // of the C++ ImFontConfig constructor; the struct here starts zeroed.
        fixed (uint* exclude = NotJapanese)
        {
            var config = new ImFontConfig
            {
                MergeMode = true,
                FontDataOwnedByAtlas = true,
                GlyphMaxAdvanceX = float.MaxValue,
                RasterizerMultiply = 1f,
                RasterizerDensity = 1f,
                GlyphExcludeRanges = exclude,
            };
            atlas.AddFontFromFileTTF(AppPaths.Asset("fonts", JapaneseFile), Theme.FontBody, &config);
        }

        return font;
    }
}
