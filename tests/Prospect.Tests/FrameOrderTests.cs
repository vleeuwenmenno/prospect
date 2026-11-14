using System.Numerics;
using System.Reflection;
using System.Text;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.FreeType;
using Brutal.Numerics;
using Prospect.Client.Input;
using Prospect.Client.Screens;
using Prospect.Client.Settings;
using Prospect.Client.Ui;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Engine.Jobs;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The client's frame as <c>App.Run</c> orders it, headless: window events polled into the real
/// <see cref="InputDevices"/> and, through <see cref="UiPointerInput"/>, into a real Dear ImGui context as
/// <c>UiPlatform</c> feeds it; then <see cref="PointerRouter.Update"/> (capture, quarantine, interface frame,
/// ownership), the real <see cref="WorldView"/> reading the mouse, and an interface window drawn after it, as the
/// quick buttons are. A poll can hold several events at once, as GLFW delivers them. No window, no GPU: the world
/// view never touches its renderer while updating. Dear ImGui keeps one global context, so every test that needs
/// it lives in this class, whose tests run one at a time.
/// </summary>
public sealed class FrameOrderTests : IDisposable
{
    private static readonly float2 Screen = new(1280, 720);
    private static readonly float2 ButtonMin = new(1100, 20);
    private static readonly float2 ButtonSize = new(150, 32);
    private static readonly float2 OverButton = new(1175, 36);
    private static readonly float2 EmptySpace = new(90, 660);

    /// <summary>Dear ImGui as <c>UiContext</c> drives it, minus fonts and rendering.</summary>
    private sealed class HeadlessInterface(InputDevices devices) : IInterfacePointer
    {
        public bool Captured;
        public float2 Cursor;

        public void SetPointerCaptured(bool captured)
        {
            Captured = captured;
            UiPointerInput.Captured(ImGui.GetIO(), captured, Cursor);
            if (!captured)
                devices.PlacePointer(Cursor);
        }

        public void NewFrame()
        {
            var io = ImGui.GetIO();
            io.DisplaySize = Screen;
            io.DeltaTime = 1f / 60f;
            ImGui.NewFrame();
        }

        public bool PointerOverInterface => ImGui.GetIO().WantCaptureMouse;
        public bool PointerOverWindow(float2 position) => UiPointerInput.OverWindow(position);
    }

    private readonly ImGuiContextPtr _context;
    private readonly LoopbackLink _link = new();
    private readonly ClientSession _client;
    private readonly ServerHost _server;
    private readonly JobSystem _jobs = new(1, "TestJob");
    private readonly InputDevices _devices = new();
    private readonly HeadlessInterface _ui;
    private readonly PointerRouter _router;
    private readonly WorldView _world;
    private readonly ActionMap _actions;
    private SessionSnapshot _snapshot;
    private int _buttonClicks;
    private long _tick;
    private bool _surfaceOpen;

    // The map with its unit list, drawn instead of the quick buttons while set, and what the map took from it.
    private bool _mapShown;
    private readonly MapPointer _map = new();
    private readonly uint[] _units = new uint[64];
    private int _unitCount;
    private int _mapRowLimit = int.MaxValue;
    private int _mapPresses;
    private int _mapReleases;
    private int _mapRightClicks;
    private float _mapWheel;
    private float2 _mapPan;

    // A social window drawn after the world with the production helper, as ScreenHost draws a pinned or open one,
    // and what its title bar did. Full is a surface the player opened; otherwise the pointer decides, as in the game.
    private FloatingWindow? _floating;
    private bool _floatingFull;
    private bool _floatingField;
    private readonly ImInputString _floatingText = new(64);
    private int _floatingCloses;
    private float2 _floatingDisplay = Screen;
    private float _floatingScale = 1f;
    private HudRect _floatingReserved;
    private HudRect _floatingDrawn;
    private static readonly float2 FloatingSize = new(360, 240);
    private static readonly float2 FloatingAt = new(400, 200);

    // The navigation bar over the map, its tab and how often that tab was pressed.
    private bool _barShown;
    private int _barClicks;
    private static readonly float2 BarTabMin = new(48, 0);
    private static readonly float2 BarTabSize = new(80, 28);

    // The sector control at the top left, submitted before the frame's layout as ScreenHost does, in the world and
    // before the map's surface; and whether its key may act this frame (no menu, Controls or workspace).
    private bool _sectorShown;
    private bool _sectorKeyAllowed = true;
    private readonly SectorInfoToggle _sector = new();
    private static readonly float2 OverSector = new(84f, 44f);

    // The exploration map's sector card, layer bar and navigation, as ScreenHost runs them with the production
    // helpers: a fixed card size stands in for the measured one, which needs fonts.
    private bool _exploreMap;
    private readonly MapCardState _card = new();
    private readonly MapNavigation _nav = new();
    private float2 _navCenter;
    private const float NavCell = 44f;
    private static readonly float2 PinnedCardSize = new(340, 320);
    private const int CardRows = 20;
    private bool _cardShown;
    private bool _cardPreview;
    private SectorCoord _cardShownSector;
    private float _cardScroll;
    private int _mapCloses;
    private int _cardCloses;
    private MapUnitHover _unitHover = new(0f);

    // The course menu as the map opens it on a right click, and what its rows ordered.
    private static readonly float2 MenuSize = MapCourseMenu.Size(1f);
    private bool _menuOpen;
    private float2 _menuMin;
    private float _menuTime;
    private Double3 _menuTarget;
    private int _menuMoves;

    public FrameOrderTests()
    {
        _context = ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.IniFilename = null;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        // The spacing the game's style uses, so rows sit where the layout puts them.
        ImGui.GetStyle().WindowPadding = float2.Zero;
        ImGui.GetStyle().ItemSpacing = float2.Zero;

        var content = GameContent.Load();
        _client = new ClientSession(_link.Client, content, "Pilot");
        _server = new ServerHost(new ServerConfig { MaxPlayers = 1 }, content, [_link.Server]);
        for (var i = 0; i < 10; i++)
        {
            ((ITickHandler)_server).Tick(_tick);
            ((ITickHandler)_client).Tick(_tick++);
        }

        _snapshot = _client.AcquireSnapshot();
        Assert.Equal(SessionState.Active, _snapshot.State);
        Assert.True(_snapshot.OwnHull >= 0);

        var actions = ActionMap.Load(Path.Combine(AppContext.BaseDirectory, "content", "input", "bindings.json"));
        _ui = new HeadlessInterface(_devices);
        _actions = actions;
        _router = new PointerRouter(_devices, actions, _ui);
        _world = new WorldView(_devices, actions, new ClientSettings(), _jobs, null!, content.Designs);
        Frame();
        Frame();
    }

    public void Dispose()
    {
        _jobs.CompleteAll();
        _jobs.Dispose();
        _client.Dispose();
        _server.Dispose();
        _link.Dispose();
        ImGui.DestroyContext(_context);
    }

    private PlayerInputBridge Bridge => _client.Input;

    [Theory]
    [InlineData(1280f, 720f, 1f)]
    [InlineData(1655f, 688f, 1f)]
    [InlineData(1280f, 720f, 2f)]
    [InlineData(1655f, 688f, 2f)]
    public void TheConsoleKeepsEveryHullPointDigitWithTheShippedFont(float width, float height, float scale)
    {
        var shipped = ShippedFonts();
        var (mono, sans) = (shipped.Mono, shipped.SansMedium);
        ImGui.GetStyle().FontScaleMain = scale;
        _ui.NewFrame();
        try
        {
            // The shipped Prospector with hullStrength=4 has 23,120 HP. Use the game's font loader and active
            // scale: measuring the font outside ImGui gives different advances and can overstate its width.
            var layout = FlightConsole.Layout(new float2(width, height), scale);
            var available = layout.Compact
                ? layout.GroupWidth - MathF.Round((FlightConsole.Slant + 2 * FlightConsole.Pad + FlightConsole.ColumnGap) * scale)
                    - Kit.Measure(sans, Theme.FontSmall, "HULL"u8).X
                : MathF.Round(FlightConsole.ReadingColumn * scale);
            Assert.InRange(Kit.Measure(mono, Theme.FontSmall, "23120 / 23120"u8).X, 1f, available);
        }
        finally
        {
            ImGui.Render();
        }
    }

    [Theory]
    [InlineData(1280f, 720f, 1f)]
    [InlineData(1655f, 688f, 1f)]
    [InlineData(1920f, 1080f, 1f)]
    [InlineData(1280f, 720f, 2f)]
    [InlineData(1655f, 688f, 2f)]
    public void TheJumpStripAndTheSpeedHexagonHoldTheirWordsWithTheShippedFonts(float width, float height, float scale)
    {
        var shipped = ShippedFonts();
        var (mono, sans) = (shipped.Mono, shipped.SansMedium);
        ImGui.GetStyle().FontScaleMain = scale;
        _ui.NewFrame();
        try
        {
            float Px(float design) => MathF.Round(design * scale);
            float Chip(ReadOnlySpan<byte> key) => Kit.Measure(mono, Theme.FontCaption, key).X + Px(Theme.Space2) * 2;
            var strip = HudLayout.Strip(new float2(width, height), scale);
            HudLayout.StripColumns(strip, scale, out var statusLeft, out var divider, out var actionLeft, out var actionRight);
            var room = actionRight - actionLeft;

            // Every state word fits left of the divider.
            var status = divider - Px(4) - Px(Theme.Space1) - statusLeft;
            foreach (var prompt in Enum.GetValues<JumpPrompt>())
            {
                Assert.InRange(Kit.Measure(sans, Theme.FontSmall, HudLayout.PromptWord(prompt, false)).X, 0f, status);
                Assert.InRange(Kit.Measure(sans, Theme.FontSmall, HudLayout.PromptWord(prompt, true)).X, 0f, status);
            }

            // Off course: the instruction and the boost key as the loaded bindings name it, and as a long pad name.
            var aim = Kit.Measure(sans, Theme.FontCaption, "AIM AT NAV, THEN"u8).X + Px(Theme.Space2);
            Assert.InRange(aim + Chip("SPACE"u8), 1f, room);
            Assert.InRange(aim + Chip("PAD Y (TRIANGLE)"u8), 1f, room);

            // Aligned: the key, what it does, and what the drive needs once started, for the largest live drive.
            var energy = 0f;
            foreach (var design in GameContent.Load().Designs)
                energy = Math.Max(energy, design.Flight.FtlEnergy);
            var cost = Kit.Measure(mono, Theme.FontCaption, Encoding.UTF8.GetBytes($"12 S + {energy:0} MJ")).X;
            Assert.InRange(Chip("SPACE"u8) + Px(Theme.Space2) + Kit.Measure(sans, Theme.FontCaption, "START JUMP"u8).X + Px(Theme.Space3) + cost, 1f, room);

            // Working: calculation and charge side by side, each label inside its own gauge's width.
            var column = (room - Px(Theme.Space3)) * 0.5f;
            Assert.InRange(Kit.Measure(mono, Theme.FontCaption, "CALC 12 S"u8).X, 1f, column);
            Assert.InRange(Kit.Measure(mono, Theme.FontCaption, Encoding.UTF8.GetBytes($"CHARGED {energy:0} / {energy:0} MJ")).X, 1f, column);

            // The quick buttons clear the sector panel: with their keys at the interface's own scale, and at worst
            // without them, as the bar drops them when they would not fit.
            float Button(ReadOnlySpan<byte> label, ReadOnlySpan<byte> key) =>
                Kit.Measure(sans, Theme.FontCaption, label).X + Px(Theme.Space3) * 2 + (key.IsEmpty ? 0f : Chip(key) + Px(Theme.Space2));
            var clear = HudLayout.ColumnRight(new float2(width, height), scale) + Px(HudLayout.Gap);
            var keyed = Button("PLAYERS"u8, "TAB"u8) + Button("SOCIAL"u8, "O"u8) + Button("CHAT"u8, "ENTER"u8) + Px(Theme.Space2) * 2;
            var bare = Button("PLAYERS"u8, default) + Button("SOCIAL"u8, default) + Button("CHAT"u8, default) + Px(Theme.Space2) * 2;
            if (scale == 1f)
                Assert.True(width - Px(Theme.Space5) - keyed >= clear, $"keyed {keyed}");
            Assert.True(width - Px(Theme.Space5) - bare >= clear, $"bare {bare}");

            // The boost heat row holds the dampeners beside its heat or its warning: stacked in the reading column
            // with room, and in a compact row at the end of the bar's line, leaving the bar most of the row.
            var console = FlightConsole.Layout(new float2(width, height), scale);
            var dampeners = Kit.Measure(sans, Theme.FontCaption, "DAMPENERS OFF"u8).X;
            var warning = Math.Max(Kit.Measure(sans, Theme.FontSmall, "NO ENERGY"u8).X, Kit.Measure(sans, Theme.FontSmall, "OVERHEAT"u8).X);
            if (!console.Compact)
            {
                Assert.InRange(dampeners, 1f, Px(FlightConsole.ReadingColumn));
                Assert.InRange(warning, 1f, Px(FlightConsole.ReadingColumn));
            }
            else
            {
                var inner = console.GroupWidth - Px(FlightConsole.Slant) - Px(FlightConsole.Pad) * 2;
                var label = Kit.Measure(sans, Theme.FontSmall, "BOOST HEAT"u8).X;
                Assert.InRange(label + Px(FlightConsole.ColumnGap) + warning, 1f, inner);
                Assert.True(inner - dampeners - Px(FlightConsole.ColumnGap) >= inner * 0.4f, $"bar {inner - dampeners} of {inner}");
            }

            // The NAV label beside a cue keeps within the cue's height and clear of its arrow head.
            var nav = Kit.Measure(sans, Theme.FontCaption, "NAV"u8);
            Assert.InRange(nav.Y * 0.5f, 1f, Px(HudLayout.CueReach));
            Assert.InRange(nav.X * 0.5f, 1f, Px(HudLayout.CueReach));
            Assert.True(Px(HudLayout.CueLabel) - nav.X * 0.5f > Px(11) * 0.8f);
        }
        finally
        {
            ImGui.Render();
        }
    }

    [Theory]
    [InlineData(1280f, 720f, 1f)]
    [InlineData(1655f, 688f, 1f)]
    [InlineData(1920f, 1080f, 1f)]
    [InlineData(1280f, 720f, 2f)]
    [InlineData(1655f, 688f, 2f)]
    [InlineData(1920f, 1080f, 2f)]
    public void TheBarHoldsEveryTabAndTheAccountNameWithTheShippedFonts(float width, float height, float scale)
    {
        var shipped = ShippedFonts();
        ImGui.GetStyle().FontScaleMain = scale;
        _ui.NewFrame();
        try
        {
            var display = new float2(width, height);
            var font = shipped.SansMedium;
            var bar = HudLayout.TopBarBottom(scale);
            Assert.True(Kit.Measure(font, Theme.FontSmall, "Agメノ"u8).Y <= bar - 2 * scale);
            Span<float> left = stackalloc float[5];
            Span<float> tabWidth = stackalloc float[5];
            Span<float> measured = stackalloc float[5];
            ReadOnlySpan<string> game = ["PLAY", "FLEET", "MAP", "SOCIAL", "SETTINGS"];
            foreach (var count in new[] { 5, 3 })
            {
                var labels = measured[..count];
                for (var i = 0; i < count; i++)
                    labels[i] = Kit.Measure(font, Theme.FontSmall, Encoding.UTF8.GetBytes(count == 5 ? game[i] : game[i == 0 ? 0 : i + 2])).X;
                // The Japanese name, the longest a name may be, and the sign-in prompt.
                foreach (var name in new[] { "メノ", "WWWWWWWWWWWWWWWWWWWWWWWW", "SIGN IN WITH DISCORD" })
                {
                    var want = Kit.Measure(font, Theme.FontSmall, Encoding.UTF8.GetBytes(name)).X + MathF.Round(Theme.Space3 * scale) * 2;
                    var layout = TopBar.Layout(display, scale, labels, want, left, tabWidth);
                    var tabsRight = left[count - 1] + tabWidth[count - 1];
                    Assert.True(tabsRight <= layout.AccountLeft, $"{name}: tabs end {tabsRight}, account {layout.AccountLeft}");
                    Assert.True(layout.AccountLeft + layout.AccountWidth <= width);
                    Assert.True(layout.AccountWidth >= Math.Min(want, MathF.Round(TopBar.AccountMin * scale)), $"{name}: {layout.AccountWidth}");
                    if (name == "メノ")
                        Assert.Equal(want, layout.AccountWidth);
                    for (var i = 0; i < count; i++)
                        Assert.True(tabWidth[i] > labels[i]);
                }
            }
        }
        finally
        {
            ImGui.Render();
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void JapaneseNamesDrawWithRealGlyphsInEveryShippedFont(float scale)
    {
        var loaded = ShippedFonts();
        ImGui.GetStyle().FontScaleMain = scale;
        _ui.NewFrame();
        try
        {
            var size = MathF.Round(Theme.FontBody * scale);
            ImFontPtr[] all = [loaded.Sans, loaded.SansMedium, loaded.SansBold, loaded.Mono];
            for (var i = 0; i < all.Length; i++)
            {
                var font = all[i];
                var baked = font.GetFontBaked(size);

                // Katakana, hiragana, kanji, CJK punctuation and a full width form come from the Japanese face, the
                // second source; Latin stays with the primary font. ImFontGlyph's first word is the C++ bit field
                // Colored:1, Visible:1, SourceIdx:4, Codepoint:26.
                foreach (var c in "メノひらがな東京の宇宙港、。ー・Ａ")
                {
                    var glyph = baked.FindGlyphNoFallback(c);
                    Assert.False(glyph.IsNull(), $"U+{(int)c:X4} is missing");
                    var bits = glyph.Codepoint;
                    Assert.Equal((uint)c, bits >> 6);
                    Assert.Equal(1u, (bits >> 2) & 0xF);
                    Assert.True(glyph.AdvanceX > size * 0.5f, $"U+{(int)c:X4} advances {glyph.AdvanceX}");
                    Assert.True((bits & 2) != 0, $"U+{(int)c:X4} draws nothing");
                }
                var latin = baked.FindGlyphNoFallback('A');
                Assert.False(latin.IsNull());
                Assert.Equal((uint)'A', latin.Codepoint >> 6);
                Assert.Equal(0u, (latin.Codepoint >> 2) & 0xF);

                // The Japanese face covers Japanese only: Cyrillic, symbols and emoji come from the primary font or not
                // at all (JetBrains Mono has its own Cyrillic), so anything else still shows the primary's fallback.
                foreach (var c in new uint[] { 0x0416, 0x2604, 0x26CF, 0x1F600 })
                {
                    var other = baked.FindGlyphNoFallback(c);
                    Assert.True(other.IsNull() || ((other.Codepoint >> 2) & 0xF) == 0, $"U+{c:X4} came from the Japanese face");
                }
                // JetBrains Mono has its own U+FFFD, which stays its fallback.
                Assert.Equal(i == all.Length - 1 ? 0xFFFDu : '?', font.FallbackChar);

                // The regression: the name "メノ" measures as its two full width Japanese glyphs, wider than two
                // fallback glyphs, on the primary font's line. The FreeType loader fits each source's ascent and descent
                // to the pixel size, so a Noto Sans JP em is about 0.69 of it.
                var me = baked.FindGlyphNoFallback('メ').AdvanceX;
                Assert.Equal(me, baked.FindGlyphNoFallback('ノ').AdvanceX);
                Assert.InRange(me, size * 0.6f, size * 0.8f);
                var name = Kit.Measure(font, Theme.FontBody, "メノ"u8);
                var fallback = Kit.Measure(font, Theme.FontBody, "??"u8);
                Assert.Equal(2 * me, name.X, 0.01f);
                Assert.True(name.X > fallback.X, $"メノ {name.X}, fallback {fallback.X}");
                Assert.Equal(fallback.Y, name.Y);
            }
        }
        finally
        {
            ImGui.Render();
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void WorldRangeHandlesClampCrossingAndRemainUsableInAScrollingPanel(float scale)
    {
        var fonts = ShippedFonts();
        var kit = (UiContext)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UiContext));
        void Field(string name, object value) => typeof(UiContext).GetField("<" + name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(kit, value);
        Field("Sans", fonts.Sans);
        Field("SansMedium", fonts.SansMedium);
        Field("SansBold", fonts.SansBold);
        Field("Mono", fonts.Mono);
        Field("Scale", scale);
        Field("DeltaTime", 1f / 60f);
        ImGui.GetStyle().FontScaleMain = scale;
        var low = 20f;
        var high = 80f;
        var track = float2.Zero;
        var trackEnd = float2.Zero;
        var open = true;
        var scrollMax = 0f;
        var scroll = 0f;
        var header = float2.Zero;
        void Draw()
        {
            _ui.NewFrame();
            Kit.BeginFrame(kit);
            ImGui.SetNextWindowPos(new float2(20, 20));
            ImGui.SetNextWindowSize(new float2(1000, 500));
            ImGui.Begin("range test"u8, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize);
            ImGui.BeginChild("settings"u8, new float2(960, 300));
            ImGui.SetNextItemOpen(open);
            var expanded = ImGui.CollapsingHeader("Copper"u8);
            header = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
            open = expanded;
            if (expanded)
            {
                Kit.RangeSlider("Grade"u8, ref low, ref high, 0, 100, 1, "%", 440);
                track = ImGui.GetItemRectMin();
                trackEnd = ImGui.GetItemRectMax();
                for (var i = 0; i < 20; i++) Kit.Paragraph(fonts.Sans, Theme.FontBody, Theme.Text, "Material details"u8, 440);
            }
            scrollMax = ImGui.GetScrollMaxY();
            scroll = ImGui.GetScrollY();
            ImGui.EndChild();
            ImGui.End();
            ImGui.Render();
        }
        float2 At(float value) => new(track.X + 8 * scale + (trackEnd.X - track.X - 16 * scale) * value / 100, (track.Y + trackEnd.Y) / 2);
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        Draw(); Draw();
        Assert.True(scrollMax > 0);
        io.AddMousePosEvent(track.X + 25 * scale, track.Y - 10 * scale); Draw();
        io.AddMouseButtonEvent(0, true); Draw();
        io.AddMouseButtonEvent(0, false); Draw();
        for (var i = 0; i < 2; i++)
        {
            io.AddKeyEvent(ImGuiKey.RightArrow, true); Draw();
            io.AddKeyEvent(ImGuiKey.RightArrow, false); Draw();
        }
        Assert.Equal(22, low);
        io.AddMousePosEvent(At(22).X, At(22).Y); Draw();
        io.AddMouseButtonEvent(0, true); Draw();
        io.AddMousePosEvent(At(95).X, At(95).Y); Draw();
        Assert.Equal(80, low);
        Assert.Equal(80, high);
        io.AddMouseButtonEvent(0, false); Draw();
        io.AddMousePosEvent(At(90).X, At(90).Y); Draw();
        io.AddMouseButtonEvent(0, true); Draw();
        io.AddMousePosEvent(At(10).X, At(10).Y); Draw();
        Assert.Equal(low, high);
        io.AddMouseButtonEvent(0, false); Draw();
        io.AddMousePosEvent(header.X, header.Y); Draw();
        io.AddMouseButtonEvent(0, true); Draw();
        io.AddMouseButtonEvent(0, false); Draw();
        Assert.False(open);
        open = true;
        Draw(); Draw();
        io.AddMousePosEvent(500, 250); Draw();
        io.AddMouseWheelEvent(0, -5); Draw(); Draw();
        Assert.True(scroll > 0);
    }

    /// <summary>The interface fonts, loaded into this test's context as the game loads them.</summary>
    private static UiFontSet ShippedFonts()
    {
        var fonts = ImGui.GetIO().Fonts;
        fonts.SetFontLoader(ImGuiFreeType.GetFontLoader());
        return UiFonts.Load(fonts);
    }

    // One GLFW event each, delivered to both listeners exactly as the window callbacks do.
    private void Move(float2 to)
    {
        _ui.Cursor = to;
        _devices.OnCursorPos(new double2(to.X, to.Y));
        UiPointerInput.Position(ImGui.GetIO(), to, _ui.Captured);
    }

    private void Button(GlfwMouseButton button, bool press)
    {
        _devices.OnMouseButton(button, press ? GlfwButtonAction.Press : GlfwButtonAction.Release);
        UiPointerInput.Button(ImGui.GetIO(), (int)button, press, _ui.Captured);
    }

    private void Click(GlfwMouseButton button = GlfwMouseButton.Left)
    {
        Button(button, true);
        Button(button, false);
    }

    private void Wheel(float lines)
    {
        _devices.OnScroll(lines);
        UiPointerInput.Wheel(ImGui.GetIO(), new float2(0, lines), _ui.Captured);
    }

    private void Key(GlfwKey key, bool press) => _devices.OnKey(key, press ? GlfwKeyAction.Press : GlfwKeyAction.Release);

    /// <summary>One frame of <c>App.Run</c>: poll, route, world, interface.</summary>
    private void Frame(Action? poll = null, Func<bool>? shortcut = null)
    {
        _devices.BeginFrame();
        poll?.Invoke();
        var pointerFrame = new PointerFrame { InSpace = true, Focused = true, SurfaceOpen = _surfaceOpen, CommandView = _world.PointerView };
        _router.Update(pointerFrame);
        var handled = shortcut?.Invoke() ?? false;
        // As App.Run: the sector key after the other interface shortcuts, never consuming the frame's input.
        if (_sectorShown)
            _sector.HandleShortcut(_actions, _devices, _sectorKeyAllowed, ImGui.GetIO().WantTextInput, _router.PointerOverInterface);
        pointerFrame.SurfaceOpen = _surfaceOpen;
        _router.RefreshCapture(pointerFrame);
        var worldInput = !_surfaceOpen && !handled && (!ImGui.GetIO().WantCaptureKeyboard || _world.HoldsInterfacePress);
        _router.BeginWorldMouse(worldInput);
        _world.PointerOverPanel = _router.PointerOverWindow;
        _world.Update(_snapshot, Bridge, 1f / 60f, worldInput, Screen, Screen, _router.PointerOverInterface);
        _router.EndWorldMouse();
        pointerFrame.CommandView = _world.PointerView;
        _router.RefreshCapture(pointerFrame);

        if (_mapShown)
        {
            DrawMapSurface();
            ImGui.Render();
            return;
        }

        DrawFloating();
        DrawCommandCourseMenu();
        // As the editor screen does before its panels: a press on one ends a turn, keeping it.
        _world.EndPartRotationOnPanelPress();
        DrawPalette();

        // The quick buttons, drawn after the world as ScreenHost draws them.
        ImGui.SetNextWindowPos(ButtonMin);
        ImGui.SetNextWindowSize(ButtonSize);
        ImGui.Begin("##quickbar"u8, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
        ImGui.SetCursorScreenPos(ButtonMin);
        if (ImGui.InvisibleButton("##players"u8, ButtonSize))
            _buttonClicks++;
        ImGui.End();
        ImGui.Render();
    }

    // The command view's course menu as ScreenHost draws it, without the drawing: the same window, rows and
    // actions, from the world's own menu state and plan, and what its rows did.
    private bool _courseMenuShown;
    private int _courseMoves;
    private int _courseJumps;

    private void DrawCommandCourseMenu()
    {
        var menu = _world.CourseMenu;
        if (!_courseMenuShown || !menu.IsOpen)
            return;
        var size = MapCourseMenu.Size(1f);
        var shown = MapCourseMenu.Shown(MapCourseMenu.Place(menu.At, size, Screen, 1f), _world.Time - menu.OpenedAt, 1f);
        MapCourseMenu.BeginFree("##coursemenu"u8, shown, size);
        var rowSize = MapCourseMenu.RowSize(size, 1f);
        var plan = _world.PlanJump(menu.Target);
        var acted = false;
        if (MapCourseMenu.Item("Move here"u8, MapCourseMenu.RowMin(shown, 0, 1f), rowSize) && plan.Ships > 0)
        {
            _world.OrderMoveSelected(Bridge, menu.Target);
            _courseMoves++;
            acted = true;
        }

        if (MapCourseMenu.Item("Jump here"u8, MapCourseMenu.RowMin(shown, 1, 1f), rowSize) && plan.Ready > 0)
        {
            _world.OrderJump(Bridge, menu.Target);
            _courseJumps++;
            acted = true;
        }

        MapCourseMenu.End();
        if (acted)
            menu.Close();
    }

    private float2 CourseRow(int row)
    {
        var menu = _world.CourseMenu;
        var size = MapCourseMenu.Size(1f);
        var shown = MapCourseMenu.Shown(MapCourseMenu.Place(menu.At, size, Screen, 1f), _world.Time - menu.OpenedAt, 1f);
        return MapCourseMenu.RowMin(shown, row, 1f) + MapCourseMenu.RowSize(size, 1f) * 0.5f;
    }

    // A part card of the editor's part list, submitted with the production helper after the world as ScreenHost
    // submits it, a search field like the list's and a shape button handed back as the list's buttons are. As
    // BeginEditorPanel does, the items sit in a child body of a window without keyboard navigation: the child keeps
    // navigation, so a click in it claims the keyboard until it is handed back.
    private bool _paletteShown;
    private int _palettePart;
    private readonly ImInputString _paletteSearch = new(48);
    private static readonly float2 PaletteMin = new(20, 120);
    private static readonly float2 CardSize = new(220, 70);
    private static readonly float2 OverCard = PaletteMin + new float2(110, 35);
    private static readonly float2 OverSearch = PaletteMin + new float2(110, 90);
    private static readonly float2 OverShapeBox = PaletteMin + new float2(110, 122);

    private void DrawPalette()
    {
        if (!_paletteShown)
            return;
        var size = new float2(CardSize.X, CardSize.Y + 70);
        ImGui.SetNextWindowPos(PaletteMin);
        ImGui.SetNextWindowSize(size);
        ImGui.Begin("##editorparts"u8, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoScrollbar);
        ImGui.SetCursorScreenPos(PaletteMin);
        ImGui.BeginChild("##editorpartsbody"u8, size, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar);
        ImGui.SetCursorScreenPos(PaletteMin);
        ImGui.PushID(_palettePart);
        EditorPalette.Card(_palettePart, CardSize, _world.Editor);
        ImGui.PopID();
        ImGui.SetCursorScreenPos(PaletteMin + new float2(0, CardSize.Y + 10));
        ImGui.SetNextItemWidth(CardSize.X);
        ImGui.InputText("##partsearch"u8, _paletteSearch.Buffer, ImGuiInputTextFlags.NoUndoRedo);
        ImGui.SetCursorScreenPos(PaletteMin + new float2(0, CardSize.Y + 40));
        if (EditorPalette.HandBack(ImGui.InvisibleButton("##shapebox"u8, new float2(CardSize.X, 24))))
            _world.Editor.SetPartShape(BlockShape.Box);
        ImGui.EndChild();
        ImGui.End();
    }

    private FloatingInput FloatingMode => _floatingFull ? FloatingInput.Full : _router.Captured ? FloatingInput.Passive : FloatingInput.Pointer;

    /// <summary>The social window as ScreenHost submits it, without the drawing: title bar, body and an optional field.</summary>
    private void DrawFloating()
    {
        if (_floating is not { } window)
            return;
        var input = FloatingMode;
        // As ScreenHost does: the heads-up markers reserve the window's place first, then the window is drawn.
        _floatingReserved = window.Prepare(FloatingSize, FloatingAt, _floatingDisplay, _floatingScale, input);
        var frame = window.Begin("##testwindow"u8, FloatingSize, FloatingAt, _floatingDisplay, _floatingScale, input, true);
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        _floatingDrawn = new HudRect(position.X, position.Y, position.X + size.X, position.Y + size.Y);
        Assert.Equal(frame.Min, position);
        if (frame.Close)
            _floatingCloses++;
        ImGui.BeginChild("##body"u8, frame.Size - new float2(0, frame.Title), ImGuiChildFlags.None, FloatingWindow.ChildFlags(input));
        if (_floatingField)
            ImGui.InputText("##field"u8, _floatingText.Buffer);
        ImGui.EndChild();
        FloatingWindow.End();
    }

    private float2 TitleOf(FloatingWindow window) => window.Min + new float2(60, FloatingWindow.TitleHeight * 0.5f);

    private static float2 PinOf(FloatingWindow window) =>
        CloseOf(window) - new float2(FloatingWindow.ButtonSize + Theme.Space1, 0);

    private static float2 CloseOf(FloatingWindow window)
    {
        var inset = (FloatingWindow.TitleHeight - FloatingWindow.ButtonSize) * 0.5f;
        return new float2(window.Min.X + FloatingSize.X - inset - FloatingWindow.ButtonSize * 0.5f, window.Min.Y + inset + FloatingWindow.ButtonSize * 0.5f);
    }

    private float2 FieldOf(FloatingWindow window) => window.Min + new float2(40, FloatingWindow.TitleHeight + 10);

    [Fact]
    public void PickingDropsRetainedMaskWhenCameraAdvancedWithoutARender()
    {
        _world.PrepareRockSelection(_world.CameraPosition, Screen.X / Screen.Y, Prospect.Client.Render.SceneRenderer.MaxInstances);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var slots = (WorldView.SectorSlot[])typeof(WorldView).GetField("_slots", flags)!.GetValue(_world)!;
        slots[0].Drawn[0] = 1;
        var fov = typeof(WorldView).GetField("_tanHalfFov", flags)!;
        fov.SetValue(_world, (float)fov.GetValue(_world)! * 1.1f);
        Frame();
        Assert.Equal(0UL, slots[0].Drawn[0]);
    }

    [Fact]
    public void MutedCommandOrbitProtectsFocusAcrossYRowsAndReleasesItAfterPilotBlend()
    {
        Assert.False(_world.SetCommandCapturePose(default, 90000, 0, -0.7f));
        _devices.Muted = true;
        var focus = default(Double3);
        Assert.True(_world.SetCommandCapturePose(focus, 90000, 0, -0.7f));
        Frame();
        var galaxy = _snapshot.Galaxy!;
        var requests = (SectorRequests)typeof(WorldView).GetField("_sectorRequests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_world)!;
        Assert.Equal(1, galaxy.SectorOf(_world.CameraPosition).Y);
        Assert.True(requests.Contains(default));
        _world.SetCommandCapturePose(focus, 90000, 0, -0.8f);
        Frame();
        Assert.Equal(2, galaxy.SectorOf(_world.CameraPosition).Y);
        Assert.True(requests.Contains(default));
        _world.SetCommandCapturePose(new Double3(400000, 0, 0), 90000, 0, -0.8f);
        Frame();
        Assert.Equal(new SectorCoord(10, 0, 0), requests.Coordinates[0]);
        _world.SetMode(ViewMode.Pilot);
        for (var i = 0; i < 30; i++) Frame();
        Assert.Equal(0, _world.CommandBlend);
        Assert.Equal(75, requests.Coordinates.Length);
        Assert.False(requests.Contains(new SectorCoord(10, 0, 0)));
    }

    [Fact]
    public void BoxSelectionUsesRenderedRockMaskEvenWhenOtherReadyRocksAreInsideBox()
    {
        _jobs.CompleteAll();
        _world.SetMode(ViewMode.Command);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var slots = (WorldView.SectorSlot[])typeof(WorldView).GetField("_slots", flags)!.GetValue(_world)!;
        foreach (var item in slots) Array.Clear(item.Drawn);
        var slot = slots[0];
        slot.Galaxy = _snapshot.Galaxy;
        slot.State = WorldView.SectorSlot.Ready;
        slot.Displaced = true;
        slot.Coord = _snapshot.Galaxy!.SectorOf(_world.CameraPosition);
        var forward = (Vector3)typeof(WorldView).GetField("_forward", flags)!.GetValue(_world)!;
        var local = (_world.CameraPosition + forward * 1000).RelativeTo(_snapshot.Galaxy.CenterOf(slot.Coord));
        slot.Content.Asteroids = [new() { Radius = 100, LocalPosition = local, Rotation = Quaternion.Identity },
            new() { Radius = 100, LocalPosition = local, Rotation = Quaternion.Identity }];
        slot.Content.AsteroidCount = 2;
        slot.Offsets = new Vector3[2];
        slot.Drawn[0] = 2;
        // Remove hull priority from this fixture, so the full-screen box considers rocks.
        var count = _snapshot.HullCount;
        _snapshot.HullCount = 0;
        typeof(WorldView).GetMethod("SelectBox", flags)!.Invoke(_world, [_snapshot, float2.Zero, Screen, false]);
        _snapshot.HullCount = count;
        var selected = (RockRef[])typeof(WorldView).GetField("_selectedRocks", flags)!.GetValue(_world)!;
        Assert.Equal(1, (int)typeof(WorldView).GetField("_selectedRockCount", flags)!.GetValue(_world)!);
        Assert.Equal(new RockRef(slot.Coord, 1), selected[0]);
    }

    /// <summary>
    /// App ends an RTS measurement at the frame of its last sample, and a run that went on after it (the session
    /// closing and saving, or a quit before the script finished) at a time just after it, as here.
    /// </summary>
    [Fact]
    public void RtsMeasurementIsCompleteOnlyWhenItEndsAtTheLastRecordedWorldFrame()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[{\"pilot\":true,\"waitReady\":false,\"hold\":0}]");
            Frame();
            Assert.NotNull(_world.Galaxy);
            var stats = new Prospect.Client.Diagnostics.FrameStats();
            bool Run(float end)
            {
                var capture = new Prospect.Client.Diagnostics.RtsCapture(path);
                Assert.False(capture.Advance(_world, 0, 1f));
                capture.Record(_world, 1f, stats, (int)Screen.X, (int)Screen.Y, true);
                return capture.Report(false, 0, end, 1, 0);
            }

            Assert.True(Run(1f));
            Assert.False(Run(MathF.BitIncrement(1f)));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".csv");
            File.Delete(path + ".csv.meta.json");
        }
    }

    [Fact]
    public void APinnedWindowInFlightTakesNothingAndLeavesWAndBoostToTheHull()
    {
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        _floating = new FloatingWindow { Open = true, Pinned = true };
        Frame();
        Frame();
        Assert.True(_router.Captured);
        Bridge.Consume(out _, out _);

        Frame(() =>
        {
            Key(GlfwKey.W, true);
            Key(GlfwKey.Space, true);
        });
        var sent = Sent();
        Assert.True(sent.Forward > 0);
        Assert.True(sent.Boost);
        Assert.False(ImGui.GetIO().WantCaptureKeyboard);
        Assert.False(ImGui.GetIO().WantCaptureMouse);

        // Shift shows the pointer: the pinned window takes a press on its pin, and only the pointer. W and Space,
        // held throughout, still reach the hull once the click is done, as keyboard navigation never rests in it.
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() => Move(PinOf(_floating)));
        Frame();
        Assert.False(_router.Captured);
        Assert.True(_router.PointerOverInterface);
        Frame(() => Click());
        Frame();
        Assert.False(_floating.Pinned);
        Assert.Equal(0, _buttonClicks);
        Bridge.Consume(out _, out _);
        Frame();
        sent = Sent();
        Assert.True(sent.Forward > 0);
        Assert.True(sent.Boost);
        Assert.False(ImGui.GetIO().WantCaptureKeyboard);
    }

    [Fact]
    public void ADraggedWindowMovesByItsTitleStaysOnScreenAndTakesAClickWhereItNowIs()
    {
        _surfaceOpen = true;
        _floatingFull = true;
        _floating = new FloatingWindow { Open = true };
        Frame(() => Move(EmptySpace));
        Frame();
        var start = _floating.Min;
        Assert.Equal(FloatingAt, start);

        var grab = TitleOf(_floating);
        Frame(() => Move(grab));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Frame(() => Move(grab + new float2(120, 60)));
        Frame(() => Move(grab + new float2(200, 100)));
        Frame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(start + new float2(200, 100), _floating.Min);

        // The very next poll clicks the close mark where the window now is.
        Frame(() =>
        {
            Move(CloseOf(_floating));
            Click();
        });
        Frame();
        Assert.Equal(1, _floatingCloses);

        // Dragged far past the top left, the title bar stays on screen and under the navigation bar.
        grab = TitleOf(_floating);
        Frame(() => Move(grab));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Frame(() => Move(grab - new float2(2000, 2000)));
        Frame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(new float2(FloatingWindow.Margin, HudLayout.TopBarBottom(1f) + FloatingWindow.Margin), _floating.Min);
    }

    [Fact]
    public void DuringAHeldDragTheMarkersReserveExactlyWhereTheWindowIsDrawn()
    {
        _surfaceOpen = true;
        _floatingFull = true;
        _floating = new FloatingWindow { Open = true };
        Frame(() => Move(EmptySpace));
        Frame();
        var start = _floating.Min;
        var grab = TitleOf(_floating);
        Frame(() => Move(grab));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Assert.Equal(start, _floating.Min);

        // Every frame of the drag: the reservation is the drawn rectangle, and each motion moves it exactly once.
        for (var step = 1; step <= 5; step++)
        {
            Frame(() => Move(grab + new float2(40, 25) * step));
            Assert.Equal(_floatingReserved, _floatingDrawn);
            Assert.Equal(start + new float2(40, 25) * step, _floating.Min);
        }

        // The window shrinks under the held drag, then the interface scale doubles: still one rectangle, on screen.
        _floatingDisplay = new float2(700, 420);
        Frame(() => Move(grab + new float2(240, 150)));
        Assert.Equal(_floatingReserved, _floatingDrawn);
        Assert.True(_floatingDrawn.Right <= 700 - FloatingWindow.Margin && _floatingDrawn.Bottom <= 420 - FloatingWindow.Margin, $"{_floatingDrawn}");
        _floatingScale = 2f;
        Frame(() => Move(grab + new float2(200, 120)));
        Assert.Equal(_floatingReserved, _floatingDrawn);
        Assert.True(_floatingDrawn.Top >= HudLayout.TopBarBottom(2f) + FloatingWindow.Margin * 2, $"{_floatingDrawn}");

        // Moved and let go in one poll: the release ends the drag, and the window stays where it was drawn.
        _floatingDisplay = Screen;
        _floatingScale = 1f;
        Frame();
        var held = _floating.Min;
        Frame(() =>
        {
            Move(grab + new float2(-300, 0));
            Button(GlfwMouseButton.Left, false);
        });
        Assert.Equal(_floatingReserved, _floatingDrawn);
        Assert.Equal(held, _floating.Min);
        Frame(() => Move(grab + new float2(-400, 0)));
        Assert.Equal(held, _floating.Min);
        Assert.Equal(_floatingReserved, _floatingDrawn);
    }

    [Fact]
    public void AWindowTakesNoClickInThePollItAppearsAndTheNextClickLandsOnIt()
    {
        _surfaceOpen = true;
        _floatingFull = true;
        var window = new FloatingWindow { Open = true };
        Frame(() => Move(EmptySpace));
        Frame(() =>
        {
            // The window appears under a pointer that clicks in the same poll: that click was not aimed at it.
            _floating = window;
            Move(PinOf(window) + (FloatingAt - window.Min));
            Click();
        });
        Frame();
        Assert.False(window.Pinned);
        Frame(() => Click());
        Frame();
        Assert.True(window.Pinned);
        Assert.Equal(0, _buttonClicks);
    }

    [Fact]
    public void TypingInAWindowOwnsTheKeysUntilTheWorkspaceLetsGoOfThem()
    {
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        _surfaceOpen = true;
        _floatingFull = true;
        _floatingField = true;
        _floating = new FloatingWindow { Open = true, Pinned = true };
        Frame(() => Move(EmptySpace));
        Frame(() => Move(FieldOf(_floating)));
        Frame(() => Click());
        Frame();
        Assert.True(ImGui.GetIO().WantTextInput);

        // The workspace closes; the pinned window stays with its field. Without letting go, the field would keep
        // every key from the hull.
        _surfaceOpen = false;
        _floatingFull = false;
        Frame();
        Assert.True(ImGui.GetIO().WantCaptureKeyboard);
        FloatingWindow.ReleaseKeyboard();
        Frame();
        Bridge.Consume(out _, out _);
        Frame(() => Key(GlfwKey.W, true));
        Assert.False(ImGui.GetIO().WantCaptureKeyboard);
        Assert.True(Sent().Forward > 0);
        Assert.True(_floating.Open && _floating.Pinned);
    }

    [Fact]
    public void TheBarOverTheMapTakesItsClicksAndAMapDragAcrossItStaysTheMaps()
    {
        _barShown = true;
        OpenMap();
        var tab = BarTabMin + BarTabSize * 0.5f;
        Frame(() => Move(tab));
        Frame(() => Click());
        Frame();
        Assert.Equal(1, _barClicks);
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);

        // In the poll the pointer comes onto the bar it already belongs to the bar.
        Frame(() =>
        {
            Move(tab + new float2(10, 2));
            Click();
        });
        Frame();
        Assert.Equal(2, _barClicks);
        Assert.Equal(0, _mapPresses);

        Frame(() => Move(MapSpot));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Frame(() => Move(tab));
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(1, _mapPresses);
        Assert.Equal(1, _mapReleases);
        Assert.Equal(2, _barClicks);
    }

    [Fact]
    public void F2TogglesTheSectorPanelInFlightAndLeavesWAndBoostToTheHull()
    {
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        _sectorShown = true;
        Frame();
        Frame();
        Assert.True(_router.Captured);
        Assert.False(_sector.Open);
        Bridge.Consume(out _, out _);

        // The key toggles the panel in the frame it goes down, and W and Space pressed in the same poll still fly.
        Frame(() =>
        {
            Key(GlfwKey.W, true);
            Key(GlfwKey.Space, true);
            Key(GlfwKey.F2, true);
        });
        Assert.True(_sector.Open);
        var sent = Sent();
        Assert.True(sent.Forward > 0);
        Assert.True(sent.Boost);
        Assert.False(ImGui.GetIO().WantCaptureKeyboard);
        Assert.True(_router.Captured);

        // Held, it does nothing more; pressed again, it closes the panel.
        Frame();
        Frame();
        Assert.True(_sector.Open);
        Frame(() => Key(GlfwKey.F2, false));
        Frame(() => Key(GlfwKey.F2, true));
        Assert.False(_sector.Open);

        // With the pointer captured a click is the hull's, never the control's.
        Frame(() => Key(GlfwKey.F2, false));
        Frame(() => Move(OverSector));
        Frame(() => Click());
        Frame();
        Assert.False(_sector.Open);

        // Not while a menu or a surface has the input.
        _sectorKeyAllowed = false;
        Frame(() => Key(GlfwKey.F2, true));
        Assert.False(_sector.Open);
    }

    [Fact]
    public void F2LeavesTheSectorPanelAloneWhileAFieldIsBeingTypedIn()
    {
        _sectorShown = true;
        _surfaceOpen = true;
        _floatingFull = true;
        _floatingField = true;
        _floating = new FloatingWindow { Open = true };
        Frame(() => Move(EmptySpace));
        Frame(() => Move(FieldOf(_floating)));
        Frame(() => Click());
        Frame();
        Assert.True(ImGui.GetIO().WantTextInput);
        Frame(() => Key(GlfwKey.F2, true));
        Frame();
        Assert.False(_sector.Open);
    }

    /// <summary>The command view without Shift, the pointer shown and resting on empty space, the own hull selected.</summary>
    private void CommandView()
    {
        _world.SetMode(ViewMode.Command);
        Frame();
        Frame(() => Move(EmptySpace));
        Frame();
        Assert.False(_router.Captured);
        Assert.Equal(1, _world.Selection.Length);
    }

    [Theory]
    [InlineData(FtlKind.Rift)]
    [InlineData(FtlKind.Wormhole)]
    [InlineData(FtlKind.Hyperspace)]
    public void HiddenTransitHullsHaveNoWorldMarkerOrBoxSelectionButRemainOnTheMap(FtlKind drive)
    {
        CommandView();
        for (var i = 0; i < 60; i++)
            Frame();
        _world.FtlPreview = drive;
        ref var own = ref _snapshot.Hulls[_snapshot.OwnHull];
        own.Jump = JumpPhase.Tunnel;
        own.JumpTimer = _snapshot.Flight.FtlTunnelSeconds * 0.5f;
        Frame();
        Assert.False(_world.IsDrawn(own));
        Assert.True(_world.Project(_world.DrawnPosition(own), out var at));
        var start = at - new float2(20, 20);
        var end = at + new float2(20, 20);
        Frame(() => { Move(start); Button(GlfwMouseButton.Left, true); });
        Frame(() => Move(end));
        Frame(() => Button(GlfwMouseButton.Left, false));
        Assert.DoesNotContain(own.Id, _world.Selection.ToArray());

        var count = MapUnitList.Collect(_snapshot, _units);
        Assert.Contains(own.Id, _units.AsSpan(0, count).ToArray());
        _world.Select(own.Id, false);
        Assert.True(_world.IsSelected(own.Id));

        own.Jump = JumpPhase.Arrive;
        own.JumpTimer = _snapshot.Flight.FtlArriveSeconds * 0.8f;
        Frame();
        Assert.True(_world.IsDrawn(own));
    }

    /// <summary>The pilot view with Shift held, so the pointer is shown, resting on empty space.</summary>
    private void PilotWithShift()
    {
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() => Move(EmptySpace));
        Frame();
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.False(_router.Captured);
    }

    private Vector3 Nose => Vector3.Transform(-Vector3.UnitZ, _world.OwnOrientation);

    private PlayerControls Sent() => Bridge.Consume(out _, out _);

    // The map's layout at this test's screen, unscaled, under a sector panel ending where the game's does.
    private MapUnitLayout Units => MapUnitList.Layout(Screen, 1f, Math.Min(_unitCount, _mapRowLimit), 140f, MapUnitList.HoverCardLeft(Screen.X, 1f, 340f, MapUnitList.HoverRight));

    private static readonly float2 MapSpot = new(820, 420);

    /// <summary>The map and its unit list as <c>ScreenHost.DrawMap</c> submits them, without the drawing.</summary>
    private void DrawMapSurface()
    {
        _unitCount = MapUnitList.Collect(_snapshot, _units);
        var units = Units;
        _map.Block(units.Min, units.Size);
        if (_barShown)
            _map.Block(float2.Zero, new float2(Screen.X, HudLayout.TopBarBottom(1f)));
        if (_menuOpen)
        {
            _menuTime += 1f / 60f;
            _map.Block(MenuShown, MenuSize);
        }

        if (_exploreMap)
        {
            _map.Block(MapLayerBar.Min(Screen, HudLayout.TopBarBottom(1f), 1f), MapLayerBar.Size(1f));
            if (_card.Pinned)
                _map.Block(PinnedCardMin, PinnedCardSize);
            var legend = MapLegend.Layout(Screen, 1f, units, LegendRows, LegendRowHeight);
            if (legend.Shown)
                _map.Block(legend.Min, legend.Size);
        }

        _map.Begin(Screen);
        _mapPresses += _map.Pressed ? 1 : 0;
        _mapReleases += _map.BoxReleased ? 1 : 0;
        _mapRightClicks += _map.RightClicked ? 1 : 0;
        _mapWheel += _map.Wheel;
        _mapPan += _map.Pan;
        if (_map.RightClicked)
        {
            _menuMin = MapCourseMenu.Place(ImGui.GetIO().MousePos, MenuSize, Screen, 1f);
            _menuTime = 0;
            _menuOpen = true;
        }

        if (_exploreMap)
            ExploreMapInput();

        ImGui.End();

        MapUnitList.Begin(units, _map);
        MapUnitList.BeginRows(units, _map);
        for (var i = 0; i < Math.Min(_unitCount, _mapRowLimit); i++)
            MapUnitList.Row(_world, _units[i], units.Row);
        MapUnitList.EndRows();
        MapUnitList.End();

        if (_barShown)
        {
            TopBar.Begin(Screen, 1f, _map);
            if (TopBar.Tab("##tab"u8, BarTabMin, BarTabSize))
                _barClicks++;
            TopBar.End();
        }

        if (_exploreMap)
            ExploreMapWindows();

        if (!_menuOpen)
            return;
        var shown = MenuShown;
        var rowSize = MapCourseMenu.RowSize(MenuSize, 1f);
        MapCourseMenu.Begin(shown, MenuSize, _map);
        if (MapCourseMenu.Item("Move here"u8, MapCourseMenu.RowMin(shown, 0, 1f), rowSize))
        {
            _world.OrderMoveSelected(Bridge, _menuTarget);
            _menuMoves++;
            _menuOpen = false;
        }

        MapCourseMenu.Item("Jump here"u8, MapCourseMenu.RowMin(shown, 1, 1f), rowSize);
        MapCourseMenu.End();
    }

    private const int LegendRows = MapLegend.FeatureRows + 5;
    private const float LegendRowHeight = 20f;
    private const float UnitHoverPixels = 14f;

    private MapProjection NavView => new(Screen * 0.5f, _navCenter, NavCell, _nav.Layer, _nav.TiltBlend * 58f * (MathF.PI / 180f), 2.2f);

    private float2 PinnedCardMin => MapSectorCard.Place(NavView.ToScreen(_card.Sector.X, _card.Sector.Y, _card.Sector.Z), NavCell * 0.5f, PinnedCardSize, Screen, HudLayout.TopBarBottom(1f), 14f, 16f);

    private float2 CardListMin => PinnedCardMin + new float2(8, 120);

    private static readonly float2 CardListSize = new(324, 36 * 4);

    private float2 CardScanCenter => PinnedCardMin + new float2(170, 100);

    /// <summary>As ScreenHost after the map's surface: keys, Escape, the card's click rule and what it shows.</summary>
    private void ExploreMapInput()
    {
        if (_nav.HandleKeys(_actions, _devices, true) && _card.Pinned)
            _nav.Layer = _card.Sector.Y;
        _nav.Advance(1f / 60f, 0f);
        if (_actions.Pressed(_devices, InputAction.Pause, true) && !_card.Dismiss())
            _mapCloses++;
        var view = NavView;
        var hover = view.SectorAt(ImGui.GetIO().MousePos);
        if (_map.BoxReleased && !_map.BoxMoved)
            _card.Click(hover, MapBounds.Contains(_snapshot.Content.Galaxy.Shape, 0.01f, hover));
        // As ScreenHost's hull loop: the own hull nearest the pointer, within its radius, keeps the preview card away.
        _unitHover = new MapUnitHover(UnitHoverPixels);
        var size = _snapshot.Content.Galaxy.SectorSize;
        for (var i = 0; i < _snapshot.HullCount; i++)
        {
            ref var hull = ref _snapshot.Hulls[i];
            if (hull.Owner == _snapshot.PlayerId)
                _unitHover.Consider(hull.Id, view.ToScreen((float)(hull.Position.X / size), (float)(hull.Position.Y / size), (float)(hull.Position.Z / size)), ImGui.GetIO().MousePos);
        }

        _cardShown = _card.Shown(_map.Hovered && !_unitHover.Hit, hover, out _cardShownSector, out _cardPreview);
    }

    /// <summary>The layer bar and the pinned card, as windows over the map.</summary>
    private void ExploreMapWindows()
    {
        var barMin = MapLayerBar.Min(Screen, HudLayout.TopBarBottom(1f), 1f);
        MapLayerBar.Begin(barMin, MapLayerBar.Size(1f), _map);
        var up = MapLayerBar.Button("##layerup"u8, MapLayerBar.UpMin(barMin, 1f), MapLayerBar.ButtonSize(1f));
        var down = MapLayerBar.Button("##layerdown"u8, MapLayerBar.DownMin(barMin, 1f), MapLayerBar.ButtonSize(1f));
        var flat = MapLayerBar.Button("##mapflat"u8, MapLayerBar.FlatMin(barMin, 1f), MapLayerBar.ToggleSize(1f));
        var tilt = MapLayerBar.Button("##maptilt"u8, MapLayerBar.TiltMin(barMin, 1f), MapLayerBar.ToggleSize(1f));
        if (MapLayerBar.Apply(_nav, up, down, flat, tilt) && _card.Pinned)
            _nav.Layer = _card.Sector.Y;
        MapLayerBar.End();

        if (!_card.Pinned)
            return;
        var min = PinnedCardMin;
        MapSectorCard.Begin(min, PinnedCardSize, _map);
        if (MapSectorCard.Close(min, PinnedCardSize, 1f))
        {
            _card.Dismiss();
            _cardCloses++;
        }

        if (MapSectorCard.Button("##scan"u8, CardScanCenter - new float2(150, 14), new float2(300, 28)))
            Bridge.RequestScan();
        MapSectorCard.BeginList(CardListMin, CardListSize, _map);
        for (var i = 0; i < CardRows; i++)
            MapSectorCard.Row(i, 36);
        _cardScroll = ImGui.GetScrollY();
        MapSectorCard.EndList();
        MapSectorCard.End();
    }

    /// <summary>Opens the exploration map centred on the own sector, the pointer resting on the map.</summary>
    private void OpenExploreMap()
    {
        _exploreMap = true;
        var galaxy = _snapshot.Content.Galaxy;
        var own = _snapshot.Hulls[_snapshot.OwnHull].Position;
        _navCenter = new float2((float)(own.X / galaxy.SectorSize), (float)(own.Z / galaxy.SectorSize));
        _nav.Reset(galaxy.SectorOf(own).Y);
        _card.Dismiss();
        OpenMap();
    }

    /// <summary>The screen point at the centre of a sector of the chosen layer.</summary>
    private float2 SectorSpot(SectorCoord sector) => NavView.ToScreen(sector.X, sector.Y, sector.Z);

    private float2 MenuShown => MapCourseMenu.Shown(_menuMin, _menuTime, 1f);

    private float2 MenuRowCenter(int row) => MapCourseMenu.RowMin(MenuShown, row, 1f) + MapCourseMenu.RowSize(MenuSize, 1f) * 0.5f;

    /// <summary>Opens the map as a surface over the world, the pointer resting on the map.</summary>
    private void OpenMap()
    {
        _surfaceOpen = true;
        _mapShown = true;
        _map.Reset();
        Frame(() => Move(MapSpot));
        Frame();
        Frame();
        Assert.False(_router.Captured);
        Assert.Equal(3, _unitCount);
        _mapPresses = _mapReleases = _mapRightClicks = 0;
        _mapWheel = 0;
        _mapPan = default;
    }

    private float2 RowCenter(int row)
    {
        var units = Units;
        return units.RowsMin + new float2(units.Size.X * 0.5f, units.Row * (row + 0.5f));
    }

    private SectorCoord OwnSector => _snapshot.Content.Galaxy.SectorOf(_snapshot.Hulls[_snapshot.OwnHull].Position);

    [Fact]
    public void AHoveredSectorShowsAPreviewAClickPinsItAndAnotherClickReplacesIt()
    {
        OpenExploreMap();
        var a = OwnSector.Offset(3, 0, 1);
        var b = OwnSector.Offset(-2, 0, -3);

        Frame(() => Move(SectorSpot(a)));
        Frame();
        Assert.True(_cardShown);
        Assert.True(_cardPreview);
        Assert.Equal(a, _cardShownSector);
        Assert.False(_card.Pinned);

        Frame(() => Click());
        Frame();
        Assert.True(_card.Pinned);
        Assert.Equal(a, _card.Sector);
        Assert.False(_cardPreview);

        Frame(() => Move(SectorSpot(b)));
        Frame(() => Click());
        Frame();
        Assert.True(_card.Pinned);
        Assert.Equal(b, _card.Sector);
        Assert.Equal(b, _cardShownSector);
    }

    [Fact]
    public void HoveringAnotherSectorWhileACardIsPinnedShowsNoSecondPreviewAndLeavesItAlone()
    {
        OpenExploreMap();
        var a = OwnSector.Offset(3, 0, 1);
        Frame(() => Move(SectorSpot(a)));
        Frame(() => Click());
        Frame();
        Assert.Equal(a, _card.Sector);

        Frame(() => Move(SectorSpot(OwnSector.Offset(-2, 0, -3))));
        Frame();
        Assert.True(_cardShown);
        Assert.False(_cardPreview);
        Assert.Equal(a, _cardShownSector);
        Assert.Equal(a, _card.Sector);

        // A pointer on the layer bar is not on the map: with nothing pinned, no preview shows there.
        Frame(() => Key(GlfwKey.Escape, true));
        Frame(() => Key(GlfwKey.Escape, false));
        Assert.False(_card.Pinned);
        var bar = MapLayerBar.Min(Screen, HudLayout.TopBarBottom(1f), 1f);
        Frame(() => Move(bar + new float2(40, 60)));
        Frame();
        Assert.False(_cardShown);
    }

    private static float2 CloseSpot(float2 cardMin) =>
        MapSectorCard.CloseMin(cardMin, PinnedCardSize, 1f) + MapSectorCard.CloseExtent(1f) * 0.5f;

    [Fact]
    public void TheCardsCloseButtonUnpinsItLikeEscapeAndItsPressNeverReachesTheMap()
    {
        OpenExploreMap();
        _world.Select(_snapshot.Hulls[_snapshot.OwnHull].Id, false);
        var a = OwnSector.Offset(3, 0, 1);
        Frame(() => Move(SectorSpot(a)));
        Frame(() => Click());
        Frame();
        Assert.True(_card.Pinned);
        var selected = _world.Selection.Length;
        Assert.True(selected > 0);
        _mapPresses = _mapReleases = 0;

        // A click on the card beside the button does not close it.
        var closeSize = MapSectorCard.CloseExtent(1f);
        Frame(() => Move(CloseSpot(PinnedCardMin) - new float2(closeSize.X + 4f, 0)));
        Frame(() => Click());
        Frame();
        Assert.True(_card.Pinned);
        Assert.Equal(0, _cardCloses);

        // On the button: pinned no more, the map saw no press and no release, the selection and the map stand.
        Frame(() => Move(CloseSpot(PinnedCardMin)));
        Frame(() => Click());
        Frame();
        Assert.False(_card.Pinned);
        Assert.Equal(1, _cardCloses);
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Equal(selected, _world.Selection.Length);
        Assert.Equal(0, _mapCloses);
    }

    [Fact]
    public void TheCloseButtonSitsInTheCardsTopRightCornerInsideItAndClearOfItsLabel()
    {
        var min = new float2(100, 100);
        foreach (var scale in new[] { 1f, 2f })
        {
            var size = new float2(340 * scale, 320 * scale);
            var corner = MapSectorCard.CloseMin(min, size, scale);
            var extent = MapSectorCard.CloseExtent(scale);
            Assert.True(corner.X >= min.X + size.X - 40f * scale && corner.X + extent.X <= min.X + size.X);
            Assert.True(corner.Y >= min.Y && corner.Y + extent.Y <= min.Y + 36f * scale);
        }
    }

    [Fact]
    public void AnOwnUnitUnderThePointerKeepsThePreviewCardAwayAndThePinnedCardStays()
    {
        OpenExploreMap();
        ref var own = ref _snapshot.Hulls[_snapshot.OwnHull];
        var size = _snapshot.Content.Galaxy.SectorSize;
        var unit = NavView.ToScreen((float)(own.Position.X / size), (float)(own.Position.Y / size), (float)(own.Position.Z / size));

        Frame(() => Move(unit + new float2(4, 3)));
        Frame();
        Assert.True(_unitHover.Hit);
        Assert.False(_cardShown);

        // Off every unit, in the same map: the sector's card is back.
        var away = OwnSector.Offset(3, 0, 2);
        Frame(() => Move(SectorSpot(away)));
        Frame();
        Assert.False(_unitHover.Hit);
        Assert.True(_cardShown);
        Assert.True(_cardPreview);
        Assert.Equal(away, _cardShownSector);

        // A card pinned stays shown while the pointer is on a unit.
        Frame(() => Click());
        Frame();
        Assert.True(_card.Pinned);
        Frame(() => Move(unit));
        Frame();
        Assert.True(_unitHover.Hit);
        Assert.True(_cardShown);
        Assert.False(_cardPreview);
        Assert.Equal(away, _cardShownSector);
    }

    [Fact]
    public void ThePointerJustOutsideTheUnitRadiusIsNotOnTheUnit()
    {
        var hover = new MapUnitHover(UnitHoverPixels);
        hover.Consider(7, new float2(100, 100), new float2(100 + UnitHoverPixels + 1, 100));
        Assert.False(hover.Hit);
        hover.Consider(8, new float2(100, 100), new float2(100 + UnitHoverPixels - 1, 100));
        hover.Consider(9, new float2(130, 100), new float2(100 + UnitHoverPixels - 1, 100));
        Assert.Equal(8u, hover.Hull);
    }

    [Fact]
    public void APressOnTheLegendNeverReachesTheMap()
    {
        OpenExploreMap();
        var legend = MapLegend.Layout(Screen, 1f, Units, LegendRows, LegendRowHeight);
        Assert.True(legend.Shown);
        _mapPresses = _mapReleases = 0;
        Frame(() => Move(legend.Min + legend.Size * 0.5f));
        Frame(() => Click());
        Frame();
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.False(_card.Pinned);
    }

    [Fact]
    public void EscapeClosesThePinnedCardBeforeTheMapAndAClickOutsideTheGalaxyDismissesIt()
    {
        OpenExploreMap();
        Frame(() => Move(SectorSpot(OwnSector.Offset(3, 0, 1))));
        Frame(() => Click());
        Frame();
        Assert.True(_card.Pinned);

        Frame(() => Key(GlfwKey.Escape, true));
        Frame(() => Key(GlfwKey.Escape, false));
        Assert.False(_card.Pinned);
        Assert.Equal(0, _mapCloses);
        Frame(() => Key(GlfwKey.Escape, true));
        Frame(() => Key(GlfwKey.Escape, false));
        Assert.Equal(1, _mapCloses);

        // Pinned again, then a click on a sector beyond the galaxy's edge: empty space dismisses the card.
        Frame(() => Click());
        Frame();
        Assert.True(_card.Pinned);
        var shape = _snapshot.Content.Galaxy.Shape;
        _navCenter = new float2(shape.Center.X + MapBounds.EdgeRadius(shape) + 30f, shape.Center.Z);
        Frame(() => Click());
        Frame();
        Assert.False(_card.Pinned);
        Assert.False(MapBounds.Contains(shape, 0.01f, NavView.SectorAt(MapSpot)));
    }

    [Fact]
    public void TheCardsScanButtonRequestsAScanAndTheWheelScrollsItsShipsNotTheMap()
    {
        OpenExploreMap();
        Frame(() => Move(SectorSpot(OwnSector)));
        Frame(() => Click());
        Frame();
        Assert.True(_card.Pinned);
        Bridge.ConsumeScan();
        _mapPresses = _mapReleases = 0;

        Frame(() =>
        {
            Move(CardScanCenter);
            Click();
        });
        Frame();
        Frame();
        Assert.True(Bridge.ConsumeScan());
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Equal(OwnSector, _card.Sector);

        Frame(() => Move(CardListMin + CardListSize * 0.5f));
        Frame(() => Wheel(-2));
        Frame();
        Frame();
        Assert.True(_cardScroll > 0f);
        Assert.Equal(0f, _mapWheel);
        Assert.True(_card.Pinned);
    }

    [Fact]
    public void WithTheBarTheMenuTheLayerBarAndAPinnedCardAllShownAPressOnTheCardNeverReachesTheMap()
    {
        _barShown = true;
        OpenExploreMap();
        Frame(() => Move(SectorSpot(OwnSector.Offset(-3, 0, -3))));
        Frame(() => Click());
        Frame();
        Assert.True(_card.Pinned);
        Frame(() => Move(SectorSpot(OwnSector.Offset(6, 0, 4))));
        Frame(() => Click(GlfwMouseButton.Right));
        Frame();
        Assert.True(_menuOpen);
        _mapPresses = _mapReleases = 0;

        // Every surface the map can show at once is up: the unit list, the bar, the layer bar, the menu and the card.
        Frame(() => Move(CardListMin + new float2(20, 10)));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.True(_card.Pinned);
    }

    [Fact]
    public void PageKeysAndTheLayerBarChangeLayerAndTheTiltedViewPicksOnTheChosenLayer()
    {
        OpenExploreMap();
        var start = _nav.Layer;
        Frame(() => Key(GlfwKey.PageUp, true));
        Frame(() => Key(GlfwKey.PageUp, false));
        Assert.Equal(start + 1, _nav.Layer);
        Frame(() => Key(GlfwKey.PageDown, true));
        Frame(() => Key(GlfwKey.PageDown, false));
        Frame(() => Key(GlfwKey.PageDown, true));
        Frame(() => Key(GlfwKey.PageDown, false));
        Assert.Equal(start - 1, _nav.Layer);

        var bar = MapLayerBar.Min(Screen, HudLayout.TopBarBottom(1f), 1f);
        Frame(() =>
        {
            Move(MapLayerBar.UpMin(bar, 1f) + MapLayerBar.ButtonSize(1f) * 0.5f);
            Click();
        });
        Frame();
        Frame();
        Assert.Equal(start, _nav.Layer);
        Assert.Equal(0, _mapPresses);

        // Tilt by key: the point clicked names the sector of the chosen layer under it in the tilted projection.
        Frame(() => Key(GlfwKey.V, true));
        Frame(() => Key(GlfwKey.V, false));
        Frame();
        Assert.True(_nav.Tilt);
        Assert.Equal(1f, _nav.TiltBlend);
        var spot = new float2(800, 250);
        var flat = new MapProjection(Screen * 0.5f, _navCenter, NavCell, _nav.Layer, 0f, 2.2f).SectorAt(spot);
        Frame(() => Move(spot));
        Frame(() => Click());
        Frame();
        Assert.True(_card.Pinned);
        Assert.Equal(NavView.SectorAt(spot), _card.Sector);
        Assert.NotEqual(flat, _card.Sector);
        Assert.Equal(_nav.Layer, _card.Sector.Y);

        // Page away, then flatten with the bar: the flat view shows the pinned sector's layer.
        Frame(() => Key(GlfwKey.PageUp, true));
        Frame(() => Key(GlfwKey.PageUp, false));
        Assert.Equal(start + 1, _nav.Layer);
        Frame(() =>
        {
            Move(MapLayerBar.FlatMin(bar, 1f) + MapLayerBar.ToggleSize(1f) * 0.5f);
            Click();
        });
        Frame();
        Frame();
        Assert.False(_nav.Tilt);
        Assert.Equal(start, _nav.Layer);
        Assert.Equal(_card.Sector.Y, _nav.Layer);

        Frame(() =>
        {
            Move(MapLayerBar.TiltMin(bar, 1f) + MapLayerBar.ToggleSize(1f) * 0.5f);
            Click();
        });
        Frame();
        Frame();
        Assert.True(_nav.Tilt);
    }

    [Fact]
    public void AClickInNewlyGrownUnitRowsSelectsTheHullWithoutReachingTheMap()
    {
        _mapRowLimit = 1;
        OpenMap();
        _world.ClearSelection();
        Frame(() =>
        {
            _mapRowLimit = 3;
            Move(RowCenter(2));
            Click();
        });
        Frame();
        Frame();
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Single(_world.Selection.ToArray());
        Assert.True(_world.IsSelected(_units[2]));
    }

    [Fact]
    public void APressAndReleaseInSeparatePollsOnANewlyGrownRowSelectsItsHull()
    {
        _mapRowLimit = 1;
        OpenMap();
        _world.ClearSelection();

        // The list grows to two rows in the poll that moves onto the second and presses; the release comes later.
        Frame(() =>
        {
            _mapRowLimit = 2;
            Move(RowCenter(1));
            Button(GlfwMouseButton.Left, true);
        });
        Frame();
        Assert.Empty(_world.Selection.ToArray());
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Single(_world.Selection.ToArray());
        Assert.True(_world.IsSelected(_units[1]));
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Equal(default, _mapPan);
    }

    [Fact]
    public void AMapPressHeldWhileTheListGrowsUnderItStaysTheMapsAndPressesNoRow()
    {
        _mapRowLimit = 1;
        OpenMap();
        _world.ClearSelection();

        // Pressed on the map where the third row will be; the list grows under the held button, which is let go there.
        Frame(() =>
        {
            Move(RowCenter(2));
            Button(GlfwMouseButton.Left, true);
        });
        Assert.Equal(1, _mapPresses);
        Frame(() =>
        {
            _mapRowLimit = 3;
            Move(RowCenter(2) + new float2(0, 2));
        });
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(1, _mapPresses);
        Assert.Equal(1, _mapReleases);
        Assert.Empty(_world.Selection.ToArray());
    }

    [Fact]
    public void AClickInThePollThatOpensTheMapReachesNeitherTheMapNorARow()
    {
        CommandView();
        var own = _world.Own.Id;
        _unitCount = MapUnitList.Collect(_snapshot, _units);
        var row = Array.IndexOf(_units, own, 0, _unitCount);

        // The map opens as a shortcut does, after the interface frame began: nothing of it was on screen.
        Frame(() =>
        {
            Move(RowCenter(row));
            Click();
        }, () =>
        {
            _surfaceOpen = true;
            _mapShown = true;
            _map.Reset();
            return true;
        });
        Frame();
        Frame();
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Equal([own], _world.Selection.ToArray());

        // The first click after the list is shown is the row's.
        Frame(() => Click());
        Frame();
        Assert.Empty(_world.Selection.ToArray());
        Assert.Equal(0, _mapPresses);
    }

    [Fact]
    public void ACourseMenuOpenedAndClickedInOnePollOrdersTheSelectionWithoutAMapClick()
    {
        Span<FleetOrder> fleet = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        OpenMap();
        _world.SelectAllOwned();
        _menuTarget = _world.OwnPosition + new Vector3(800, 0, 0);
        Bridge.Consume(out _, out _);
        Bridge.ConsumeFleetOrders(fleet);

        // A right click on the map and a click on the menu it opens, in one poll.
        var menu = MapCourseMenu.Place(MapSpot, MenuSize, Screen, 1f);
        var move = MapCourseMenu.RowMin(menu, 0, 1f) + MapCourseMenu.RowSize(MenuSize, 1f) * 0.5f;
        Frame(() =>
        {
            Click(GlfwMouseButton.Right);
            Move(move);
            Click();
        });
        for (var i = 0; i < 4; i++)
            Frame();

        Assert.Equal(1, _mapRightClicks);
        Assert.Equal(1, _menuMoves);
        Assert.False(_menuOpen);
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Equal(default, _mapPan);
        Bridge.Consume(out var hasOrder, out var order);
        Assert.True(hasOrder);
        Assert.Equal(OrderKind.MoveTo, order.Kind);
        Assert.Equal(2, Bridge.ConsumeFleetOrders(fleet));
    }

    [Fact]
    public void ACourseMenuMovedByARightClickTakesTheNextClickAtItsNewPlace()
    {
        OpenMap();
        _world.SelectAllOwned();
        _menuTarget = _world.OwnPosition + new Vector3(800, 0, 0);
        Frame(() => Click(GlfwMouseButton.Right));
        Frame();
        Frame();
        Assert.True(_menuOpen);
        var first = _menuMin;

        // Moved by a right click elsewhere on the map, then clicked at its new place, all in one poll.
        var elsewhere = new float2(700, 200);
        var moved = MapCourseMenu.Place(elsewhere, MenuSize, Screen, 1f);
        Assert.NotEqual(first, moved);
        var move = MapCourseMenu.RowMin(moved, 0, 1f) + MapCourseMenu.RowSize(MenuSize, 1f) * 0.5f;
        Frame(() =>
        {
            Move(elsewhere);
            Click(GlfwMouseButton.Right);
            Move(move);
            Click();
        });
        for (var i = 0; i < 4; i++)
            Frame();

        Assert.Equal(moved, _menuMin);
        Assert.Equal(2, _mapRightClicks);
        Assert.Equal(1, _menuMoves);
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
    }

    [Fact]
    public void UnitRowsChangedWhileTheCourseMenuIsOpenDecideWhatItOrders()
    {
        Span<FleetOrder> fleet = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        OpenMap();
        _world.SelectAllOwned();
        _menuTarget = _world.OwnPosition + new Vector3(800, 0, 0);
        Bridge.Consume(out _, out _);
        Bridge.ConsumeFleetOrders(fleet);
        Frame(() => Click(GlfwMouseButton.Right));
        Frame();
        Frame();
        Assert.True(_menuOpen);

        // One fleet hull taken out in the list; the menu stays open.
        var own = _world.Own.Id;
        var dropped = Array.FindIndex(_units, 0, _unitCount, id => id != own);
        var kept = Array.FindLastIndex(_units, _unitCount - 1, _unitCount, id => id != own);
        Frame(() =>
        {
            Move(RowCenter(dropped));
            Click();
        });
        Frame();
        Assert.True(_menuOpen);
        Assert.Equal(2, _world.Selection.Length);

        // Move here, pressed and let go in separate polls: the hull being flown and the one fleet hull left.
        Frame(() =>
        {
            Move(MenuRowCenter(0));
            Button(GlfwMouseButton.Left, true);
        });
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(1, _menuMoves);
        Bridge.Consume(out var hasOrder, out _);
        Assert.True(hasOrder);
        Assert.Equal(1, Bridge.ConsumeFleetOrders(fleet));
        Assert.Equal(_units[kept], fleet[0].Hull);

        // Cleared with the menu open again: Move here orders nothing at all.
        _world.ClearSelection();
        Frame(() =>
        {
            Move(MapSpot);
            Click(GlfwMouseButton.Right);
        });
        Frame();
        Frame();
        Frame(() =>
        {
            Move(MenuRowCenter(0));
            Click();
        });
        Frame();
        Assert.Equal(2, _menuMoves);
        Bridge.Consume(out hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Equal(0, Bridge.ConsumeFleetOrders(fleet));
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
    }

    [Fact]
    public void AUnitRowClickAddsOrRemovesThatHullAndLeavesTheOthers()
    {
        OpenMap();
        _world.SelectAllOwned();
        Assert.Equal(3, _world.Selection.Length);
        var picked = _units[1];

        // A move onto the row and a click in one poll: the row's, not the map's.
        Frame(() =>
        {
            Move(RowCenter(1));
            Click();
        });
        Frame();
        Assert.Equal(2, _world.Selection.Length);
        Assert.False(_world.IsSelected(picked));
        Assert.True(_world.IsSelected(_units[0]));
        Assert.True(_world.IsSelected(_units[2]));

        // The rows did not move under the pointer: the same click puts it back.
        Frame(() => Click());
        Frame();
        Assert.Equal(3, _world.Selection.Length);
        Assert.True(_world.IsSelected(picked));

        // From an empty selection a row adds just its hull.
        _world.ClearSelection();
        Frame(() => Click());
        Frame();
        Assert.Equal(1, _world.Selection.Length);
        Assert.True(_world.IsSelected(picked));
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
    }

    [Fact]
    public void WheelRightClickAndMiddleDragOnTheUnitListNeverReachTheMap()
    {
        OpenMap();
        Frame(() =>
        {
            Move(RowCenter(0));
            Wheel(2);
        });
        Frame(() => Click(GlfwMouseButton.Right));
        Frame();
        Frame(() => Button(GlfwMouseButton.Middle, true));
        Frame(() => Move(RowCenter(0) + new float2(200, 0)));
        Frame(() => Move(MapSpot));
        Frame(() => Button(GlfwMouseButton.Middle, false));
        Assert.Equal(0f, _mapWheel);
        Assert.Equal(0, _mapRightClicks);
        Assert.Equal(default, _mapPan);
        Assert.Equal(0, _mapPresses);

        // The same on the map is the map's.
        Frame(() => Wheel(2));
        Frame(() => Click(GlfwMouseButton.Right));
        Frame();
        Frame(() => Button(GlfwMouseButton.Middle, true));
        Frame(() => Move(MapSpot + new float2(40, 10)));
        Frame(() => Button(GlfwMouseButton.Middle, false));
        Assert.Equal(2f, _mapWheel);
        Assert.Equal(1, _mapRightClicks);
        Assert.NotEqual(default, _mapPan);
    }

    [Fact]
    public void ADragBegunOnTheUnitListNeverBecomesAMapBoxAndAMapBoxPressesNoRow()
    {
        OpenMap();
        _world.SelectAllOwned();

        // Pressed on a row, let go on the map: no map gesture, and no row click either.
        Frame(() =>
        {
            Move(RowCenter(0));
            Button(GlfwMouseButton.Left, true);
        });
        Frame(() => Move(MapSpot));
        Frame(() => Move(MapSpot + new float2(60, 60)));
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(0, _mapPresses);
        Assert.Equal(0, _mapReleases);
        Assert.Equal(3, _world.Selection.Length);

        // Pressed on the map, dragged across the list and let go on a row: the map's box, and no row click.
        Frame(() =>
        {
            Move(MapSpot);
            Button(GlfwMouseButton.Left, true);
        });
        Frame(() => Move(RowCenter(1) + new float2(30, 0)));
        Frame(() => Move(RowCenter(1)));
        Assert.True(_map.Boxing);
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(1, _mapPresses);
        Assert.Equal(1, _mapReleases);
        Assert.True(_map.BoxMoved);
        Assert.Equal(3, _world.Selection.Length);
    }

    [Fact]
    public void MapMovesGoToExactlyTheSelectedHullsAndNowhereWhenNoneIsSelected()
    {
        Span<FleetOrder> fleet = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        var target = _world.OwnPosition + new Vector3(800, 0, 0);
        var own = _world.Own.Id;
        _unitCount = MapUnitList.Collect(_snapshot, _units);
        var others = _units.AsSpan(0, _unitCount).ToArray().Where(id => id != own).ToArray();
        Assert.Equal(2, others.Length);
        Bridge.Consume(out _, out _);
        Bridge.ConsumeFleetOrders(fleet);

        // All three, then one fleet hull taken out of the list: the two left are ordered, the third is not.
        _world.SelectAllOwned();
        Assert.Equal(3, _world.Selection.Length);
        foreach (var id in _world.Selection)
        {
            Assert.True(_world.TryGetHull(id, out var hull));
            Assert.Equal(_snapshot.PlayerId, hull.Owner);
        }

        _world.Select(others[0], true);
        _world.OrderMoveSelected(Bridge, target);
        Bridge.Consume(out var hasOrder, out var order);
        Assert.True(hasOrder);
        Assert.Equal(OrderKind.MoveTo, order.Kind);
        Assert.Equal(1, Bridge.ConsumeFleetOrders(fleet));
        Assert.Equal(others[1], fleet[0].Hull);
        Assert.Equal(OrderKind.MoveTo, fleet[0].Order.Kind);

        // The hull being flown taken out as well: only the one fleet hull is ordered.
        _world.Select(own, true);
        _world.OrderMoveSelected(Bridge, target);
        Bridge.Consume(out hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Equal(1, Bridge.ConsumeFleetOrders(fleet));
        Assert.Equal(others[1], fleet[0].Hull);

        // Cleared: nothing is ordered, not even the hull being flown.
        _world.ClearSelection();
        _world.OrderMoveSelected(Bridge, target);
        Bridge.Consume(out hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Equal(0, Bridge.ConsumeFleetOrders(fleet));

        // A hull that is gone is dropped from the selection, and does not make it fall back to the own hull.
        _world.Select(0xFFFF_FF00u, false);
        _world.OrderMoveSelected(Bridge, target);
        Assert.Equal(0, _world.Selection.Length);
        Bridge.Consume(out hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Equal(0, Bridge.ConsumeFleetOrders(fleet));

        // Outside the map an empty selection still means the hull being flown.
        _world.OrderMove(Bridge, target);
        Bridge.Consume(out hasOrder, out order);
        Assert.True(hasOrder);
        Assert.Equal(OrderKind.MoveTo, order.Kind);
    }

    [Fact]
    public void TheMapTakesControlOfExactlyOneSelectedOwnShipWithoutClosing()
    {
        var own = _world.Own.Id;
        _unitCount = MapUnitList.Collect(_snapshot, _units);
        var others = _units.AsSpan(0, _unitCount).ToArray().Where(id => id != own).ToArray();
        Assert.Equal(2, others.Length);
        Bridge.ConsumeControlRequest(out _);
        // The map is a surface: flight's own T handling is off while it is open.
        _surfaceOpen = true;

        bool Press(bool keyboard = true)
        {
            var sent = false;
            Frame(() => Key(GlfwKey.T, true));
            sent = _world.SwitchToSelectedFromMap(Bridge, keyboard);
            Frame(() => Key(GlfwKey.T, false));
            return sent;
        }

        // Nothing selected, the hull being flown, several, a rock with a hull, a stale id: no request.
        _world.ClearSelection();
        Assert.False(Press());
        _world.Select(own, false);
        Assert.False(Press());
        _world.Select(others[0], true);
        Assert.False(Press());
        _world.ClearSelection();
        _world.Select(0xFFFF_FF00u, false);
        Assert.False(Press());
        Assert.False(Bridge.ConsumeControlRequest(out _));

        // Typing owns the keyboard.
        _world.Select(others[0], false);
        Assert.False(Press(false));
        Assert.False(Bridge.ConsumeControlRequest(out _));

        // One other own ship: the request names it, the selection stays, and nothing moves until a snapshot says so.
        Assert.True(Press());
        Assert.True(Bridge.ConsumeControlRequest(out var requested));
        Assert.Equal(others[0], requested);
        Assert.Equal(1, _world.Selection.Length);
        Assert.Equal(others[0], _world.Selection[0]);
        Assert.Equal(own, _world.Own.Id);

        // A held key sends nothing more: only the frame of the press counts.
        Frame(() => Key(GlfwKey.T, true));
        Assert.True(_world.SwitchToSelectedFromMap(Bridge, true));
        Assert.True(Bridge.ConsumeControlRequest(out _));
        Frame();
        Assert.False(_world.SwitchToSelectedFromMap(Bridge, true));
        Frame(() => Key(GlfwKey.T, true));
        Assert.False(_world.SwitchToSelectedFromMap(Bridge, true));
        Assert.False(Bridge.ConsumeControlRequest(out _));
        Frame(() => Key(GlfwKey.T, false));
    }

    [Fact]
    public void TheMapSwitchKeyFollowsTheBindingFileNotTheLetterT()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"actions":{"SwitchHull":["Key:G"]},"axes":{}}""");
            var rebound = new WorldView(_devices, ActionMap.Load(path), new ClientSettings(), _jobs, null!, GameContent.Load().Designs);
            _unitCount = MapUnitList.Collect(_snapshot, _units);
            var other = _units.AsSpan(0, _unitCount).ToArray().First(id => id != _world.Own.Id);
            Bridge.ConsumeControlRequest(out _);

            bool Press(GlfwKey key)
            {
                _devices.BeginFrame();
                Key(key, true);
                rebound.Update(_snapshot, Bridge, 1f / 60f, false, Screen, Screen, false);
                var sent = rebound.SwitchToSelectedFromMap(Bridge, true);
                _devices.BeginFrame();
                Key(key, false);
                return sent;
            }

            rebound.Update(_snapshot, Bridge, 1f / 60f, false, Screen, Screen, false);
            rebound.Select(other, false);
            Assert.Equal("G", rebound.SwitchKeyName);
            Assert.False(Press(GlfwKey.T));
            Assert.False(Bridge.ConsumeControlRequest(out _));
            Assert.True(Press(GlfwKey.G));
            Assert.True(Bridge.ConsumeControlRequest(out var requested));
            Assert.Equal(other, requested);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheMapSwitchRefusesARockWithAShipAndAHullThatIsNotTheirs()
    {
        _surfaceOpen = true;
        _unitCount = MapUnitList.Collect(_snapshot, _units);
        var other = _units.AsSpan(0, _unitCount).ToArray().First(id => id != _world.Own.Id);
        Bridge.ConsumeControlRequest(out _);

        bool Press()
        {
            Frame(() => Key(GlfwKey.T, true));
            var sent = _world.SwitchToSelectedFromMap(Bridge, true);
            Frame(() => Key(GlfwKey.T, false));
            return sent;
        }

        // A ship and an asteroid selected together: not one ship, so nothing is taken. The asteroid list is private
        // to the view and only the command view fills it, so the test fills it as a pick there would.
        _world.Select(other, false);
        var rocks = typeof(WorldView).GetField("_selectedRocks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var count = typeof(WorldView).GetField("_selectedRockCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((RockRef[])rocks.GetValue(_world)!)[0] = new RockRef(new SectorCoord(0, 0, 0), 0);
        count.SetValue(_world, 1);
        Assert.False(Press());
        Assert.False(Bridge.ConsumeControlRequest(out _));
        _world.ClearSelection();

        // A hull another player owns cannot be selected for this: it is pruned and nothing is requested.
        ref var hull = ref FindHull(other);
        var owner = hull.Owner;
        hull.Owner = owner + 1;
        try
        {
            _world.Select(other, false);
            Assert.False(Press());
            Assert.Equal(0, _world.Selection.Length);
            Assert.False(Bridge.ConsumeControlRequest(out _));
        }
        finally
        {
            hull.Owner = owner;
        }
    }

    private ref HullView FindHull(uint id)
    {
        for (var i = 0; i < _snapshot.HullCount; i++)
        {
            if (_snapshot.Hulls[i].Id == id)
                return ref _snapshot.Hulls[i];
        }

        throw new InvalidOperationException("no such hull");
    }

    [Fact]
    public void TheMapSwitchKeyIsNotReadWhileTypingOrWhileAMenuOrControlsOwnTheKeyboard()
    {
        // A real text field takes focus through the frame path, then the production gate says no key is available.
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        _surfaceOpen = true;
        _floatingFull = true;
        _floatingField = true;
        _floating = new FloatingWindow { Open = true, Pinned = true };
        _unitCount = MapUnitList.Collect(_snapshot, _units);
        var other = _units.AsSpan(0, _unitCount).ToArray().First(id => id != _world.Own.Id);
        Bridge.ConsumeControlRequest(out _);
        Frame(() => Move(EmptySpace));
        Frame(() => Move(FieldOf(_floating)));
        Frame(() => Click());
        Frame();
        Assert.True(ImGui.GetIO().WantTextInput);

        _world.Select(other, false);
        Frame(() => Key(GlfwKey.T, true));
        var typing = ImGui.GetIO().WantTextInput;
        var available = WorldView.MapSwitchKeyAvailable(false, false, false, typing);
        Assert.False(available);
        Assert.False(_world.SwitchToSelectedFromMap(Bridge, available));
        Assert.False(Bridge.ConsumeControlRequest(out _));

        // The same press with nothing in the way is available, and each owner of the keyboard alone takes it away.
        Assert.True(WorldView.MapSwitchKeyAvailable(false, false, false, false));
        Assert.False(WorldView.MapSwitchKeyAvailable(true, false, false, false));
        Assert.False(WorldView.MapSwitchKeyAvailable(false, true, false, false));
        Assert.False(WorldView.MapSwitchKeyAvailable(false, false, true, false));
        FloatingWindow.ReleaseKeyboard();
    }

    [Fact]
    public void AMoveOntoAQuickButtonAndAClickInOnePollAreTheButtonsNotTheWorlds()
    {
        CommandView();
        Frame(() =>
        {
            Move(OverButton);
            Click();
        });
        Frame();

        // The world did not select behind the button; Dear ImGui completed the click on the next frame.
        Assert.Equal(1, _world.Selection.Length);
        Assert.Equal(1, _buttonClicks);

        // The same click on empty space is the world's: it clears the selection.
        Frame(() =>
        {
            Move(EmptySpace);
            Click();
        });
        Assert.Equal(0, _world.Selection.Length);
        Assert.Equal(1, _buttonClicks);
    }

    [Theory]
    [InlineData("ToggleControls")]
    [InlineData("TogglePlayers")]
    [InlineData("ToggleSocial")]
    [InlineData("ToggleChat")]
    public void MouseShortcutsUseCurrentHoverAndIgnoreCaptureTransitionClicks(string actionName)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $$$"""{"actions":{"{{{actionName}}}":["Mouse:Left"]},"axes":{}}""");
            var actions = ActionMap.Load(path);
            var action = Enum.Parse<InputAction>(actionName);
            var shortcuts = 0;
            bool Shortcut()
            {
                var pressed = actions.InterfaceShortcutPressed(_devices, action, ImGui.GetIO().WantTextInput, _ui.PointerOverInterface);
                if (pressed)
                    shortcuts++;
                return pressed;
            }

            // Shift and a click in one poll from the captured pilot view: the click belongs to neither owner.
            Frame(() =>
            {
                Key(GlfwKey.LeftShift, true);
                Move(EmptySpace);
                Click();
            }, Shortcut);
            Assert.Equal(0, shortcuts);
            _world.SetMode(ViewMode.Command);
            Frame();

            Frame(() =>
            {
                Move(OverButton);
                Click();
            }, Shortcut);
            Frame();
            Assert.Equal(0, shortcuts);
            Assert.Equal(1, _buttonClicks);

            // Leaving the button and clicking empty space in one poll must enable the shortcut immediately.
            Frame(() =>
            {
                Move(EmptySpace);
                Click();
            }, Shortcut);
            Assert.Equal(1, shortcuts);
            Assert.Equal(1, _world.Selection.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ShortcutsOpenAndCloseSurfacesWithTheCorrectCursorInTheSameFrame()
    {
        Assert.True(_router.Captured);
        Frame(shortcut: () => { _surfaceOpen = true; return true; });
        Assert.False(_router.Captured);
        Frame(shortcut: () => { _surfaceOpen = false; return true; });
        Assert.True(_router.Captured);
    }

    [Fact]
    public void WheelAndMiddleDragOverAQuickButtonLeaveTheCommandCameraAlone()
    {
        CommandView();
        var distance = _world.CommandDistance;
        Frame(() =>
        {
            Move(OverButton);
            Wheel(3);
        });
        Assert.Equal(distance, _world.CommandDistance);

        Frame(() => Button(GlfwMouseButton.Middle, true));
        Frame(() => Move(OverButton + new float2(-60, 8)));
        Frame(() => Move(EmptySpace));
        Frame(() => Move(EmptySpace + new float2(40, -40)));
        Assert.True(_world.CommandFollowing);
        Frame(() => Button(GlfwMouseButton.Middle, false));

        // Over empty space the wheel is the world's.
        Frame(() => Wheel(3));
        Assert.NotEqual(distance, _world.CommandDistance);
    }

    [Fact]
    public void ShiftAndAClickInOnePollReachNeitherTheHiddenInterfaceNorTheWorld()
    {
        Frame(() => Move(OverButton));
        Frame();
        Assert.True(_router.Captured);

        // Hidden: a click over where the button is reaches nothing.
        Frame(() => Click());
        Frame();
        Assert.Equal(0, _buttonClicks);

        // Shift and a right press in the same poll: made while the pointer was hidden, so neither the button nor
        // the camera drag gets it, even while it stays held and moves.
        Frame(() =>
        {
            Key(GlfwKey.LeftShift, true);
            Click();
            Button(GlfwMouseButton.Right, true);
        });
        Frame(() => Move(OverButton + new float2(-80, 30)));
        Frame(() => Move(EmptySpace));
        Assert.False(_router.Captured);
        Assert.False(_world.FreeLook);
        Assert.Equal(0, _buttonClicks);
        Frame(() => Button(GlfwMouseButton.Right, false));

        // Nothing is stuck: the next click on the button is the interface's.
        Frame(() => Move(OverButton));
        Frame(() => Click());
        Frame();
        Assert.Equal(1, _buttonClicks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShiftReleaseAndAClickInOnePollPressNoButton(bool releaseInSamePoll)
    {
        PilotWithShift();
        Frame(() => Move(OverButton));
        Frame(() =>
        {
            Button(GlfwMouseButton.Left, true);
            if (releaseInSamePoll)
                Button(GlfwMouseButton.Left, false);
            Key(GlfwKey.LeftShift, false);
        });
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Frame();
        Assert.True(_router.Captured);
        Assert.Equal(0, _buttonClicks);
        Assert.False(_world.FreeLook);
    }

    [Fact]
    public void CapturingCancelsMouseInputWithoutDiscardingQueuedKeyboardAndText()
    {
        PilotWithShift();
        Frame(() => Move(OverButton));
        var io = ImGui.GetIO();
        Frame(() =>
        {
            Button(GlfwMouseButton.Left, true);
            io.AddKeyEvent(ImGuiKey.F1, true);
            io.AddInputCharacter('x');
            Wheel(3);
            Button(GlfwMouseButton.Left, false);
            Key(GlfwKey.LeftShift, false);
        }, shortcut: () =>
        {
            // Text is available during the frame, then ImGui clears it at EndFrame.
            Assert.Equal((uint)'x', (uint)Assert.Single(io.InputQueueCharacters));
            return false;
        });
        Assert.True(_router.Captured);
        Assert.True(ImGui.IsKeyDown(ImGuiKey.F1));
        Assert.Equal(0f, io.MouseWheel);
        Assert.Equal(UiPointerInput.Absent, io.MousePos);
        Frame(() => io.AddKeyEvent(ImGuiKey.F1, false));
        Frame();
        Assert.False(ImGui.IsKeyDown(ImGuiKey.F1));
        Assert.Equal(0, _buttonClicks);
    }

    [Fact]
    public void CapturingCancelsAnActiveMousePressBeforeItsLaterRelease()
    {
        PilotWithShift();
        Frame(() => Move(OverButton));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Assert.True(ImGui.IsAnyItemActive());
        Frame(() => Key(GlfwKey.LeftShift, false));
        Assert.False(ImGui.IsAnyItemActive());
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame();
        Assert.Equal(0, _buttonClicks);
        Frame(() => Click());
        Frame();
        Assert.Equal(1, _buttonClicks);
    }

    [Fact]
    public void SwitchingToCommandGivesTheNewViewNoneOfTheSwitchFramesMouse()
    {
        Frame(() => Move(EmptySpace));
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.True(_router.Captured);

        // N, a click and the wheel in one poll from the pilot view, where the world owned the mouse.
        Frame(() =>
        {
            Key(GlfwKey.N, true);
            Click();
            Wheel(3);
        });
        Assert.Equal(ViewMode.Command, _world.Mode);
        Assert.Equal(150f, _world.CommandDistance);
        Assert.Equal(1, _world.Selection.Length);
    }

    [Fact]
    public void TheCommandKeyShowsThePointerInItsOwnFrameAndThePilotViewTakesItBack()
    {
        Frame(() => Move(EmptySpace));
        Assert.True(_router.Captured);

        // N without Shift: shown in the same frame, no release and recapture in between.
        Frame(() => Key(GlfwKey.N, true));
        Assert.Equal(ViewMode.Command, _world.Mode);
        Assert.False(_router.Captured);
        Frame(() => Key(GlfwKey.N, false));
        Frame(() => Move(EmptySpace));
        Assert.False(_router.Captured);

        // The following frames are the world's: wheel zoom, middle-drag pan, a click on empty space.
        var distance = _world.CommandDistance;
        Frame(() => Wheel(2));
        Assert.NotEqual(distance, _world.CommandDistance);
        Frame(() => Button(GlfwMouseButton.Middle, true));
        Frame(() => Move(EmptySpace + new float2(40, -40)));
        Frame(() => Button(GlfwMouseButton.Middle, false));
        Assert.False(_world.CommandFollowing);
        Assert.Equal(1, _world.Selection.Length);
        Frame(() => Click());
        Assert.Equal(0, _world.Selection.Length);

        // Shift in the command view changes nothing: the pointer stays shown either way.
        Frame(() => Key(GlfwKey.LeftShift, true));
        Assert.False(_router.Captured);
        Frame(() => Key(GlfwKey.LeftShift, false));
        Assert.False(_router.Captured);

        // Back to the pilot view: captured again in the same frame.
        Frame(() => Key(GlfwKey.N, true));
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.True(_router.Captured);
        Frame(() => Key(GlfwKey.N, false));

        // With Shift held, returning to the pilot view leaves the pointer shown.
        Frame(() => Key(GlfwKey.N, true));
        Frame(() => Key(GlfwKey.N, false));
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() => Key(GlfwKey.N, true));
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.False(_router.Captured);
    }

    [Fact]
    public void ShiftPressedOrReleasedDuringACommandDragDoesNotEndIt()
    {
        CommandView();
        Frame(() => Button(GlfwMouseButton.Middle, true));
        Frame(() =>
        {
            Key(GlfwKey.LeftShift, true);
            Move(EmptySpace + new float2(20, -20));
        });
        Frame(() =>
        {
            Key(GlfwKey.LeftShift, false);
            Move(EmptySpace + new float2(40, -40));
        });
        // The pan reached the world through both Shift changes and broke the follow on the own hull.
        Assert.False(_world.CommandFollowing);
        Frame(() => Button(GlfwMouseButton.Middle, false));
    }

    [Fact]
    public void ShiftKeepsItsCommandViewMeaningsWithoutCapturingThePointer()
    {
        CommandView();
        var focus = _world.CommandFocus;

        // Shift + left drag slides the plane, with the pointer shown throughout.
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Frame(() => Move(EmptySpace + new float2(0, -120)));
        Assert.False(_router.Captured);
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame(() => Key(GlfwKey.LeftShift, false));
        Assert.NotEqual(focus.Y, _world.CommandFocus.Y);
        Assert.False(_router.Captured);
    }

    [Fact]
    public void PilotSpaceBoostsAndRAndFMoveVerticallyWithoutCommandFocus()
    {
        Frame();
        Bridge.Consume(out _, out _);
        Frame(() => Key(GlfwKey.Space, true));
        var sent = Sent();
        Assert.True(sent.Boost);
        Assert.Equal(0f, sent.Vertical);
        Frame(() => Key(GlfwKey.Space, false));

        Frame(() => Key(GlfwKey.R, true));
        sent = Sent();
        Assert.Equal(1f, sent.Vertical);
        Assert.False(sent.Boost);
        Frame(() => Key(GlfwKey.R, false));

        var following = _world.CommandFollowing;
        Frame(() => Key(GlfwKey.F, true));
        sent = Sent();
        Assert.Equal(-1f, sent.Vertical);
        Assert.False(sent.Boost);
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.Equal(following, _world.CommandFollowing);
        Assert.True(_router.Captured);
    }

    [Fact]
    public void AClickHeldThroughTheCommandKeyNeverBecomesACommandAction()
    {
        Frame(() => Move(EmptySpace));
        Frame(() => Button(GlfwMouseButton.Left, true));
        Frame(() => Key(GlfwKey.N, true));
        Assert.Equal(ViewMode.Command, _world.Mode);
        Assert.Equal(1, _world.Selection.Length);

        // Dragged and released in the command view: no box, no click that clears the selection.
        Frame(() => Move(EmptySpace + new float2(200, -150)));
        Frame(() => Button(GlfwMouseButton.Left, false));
        Frame();
        Assert.Equal(1, _world.Selection.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingToPilotDoesNotTurnTheViewWithTheSwitchFramesMotion(bool alt)
    {
        CommandView();
        if (alt)
            Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() =>
        {
            Key(GlfwKey.N, true);
            Move(EmptySpace + new float2(300, 0));
        });
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.Equal(!alt, _router.Captured);
        var aim = Sent().Aim;
        Assert.True(Vector3.Distance(aim, Nose) < 1e-4f, $"aim {aim} nose {Nose}");
    }

    private uint OwnId => _snapshot.Hulls[_snapshot.OwnHull].Id;

    private uint RemoteOwnedId()
    {
        for (var i = 0; i < _snapshot.HullCount; i++)
            if (!_snapshot.Hulls[i].IsOwn && _snapshot.Hulls[i].Owner == _snapshot.PlayerId)
                return _snapshot.Hulls[i].Id;
        throw new InvalidOperationException("no other owned hull");
    }

    /// <summary>Opens the command view's menu at an empty place with the pointer there, settled and drawn.</summary>
    private void OpenCourseMenu(Double3 target)
    {
        CommandView();
        _courseMenuShown = true;
        Frame(() => Move(EmptySpace));
        _world.CourseMenu.Open(EmptySpace, target, _world.Time);
        for (var i = 0; i < 10; i++)
            Frame();
        Assert.True(_world.CourseMenu.IsOpen);
        Bridge.Consume(out _, out _);
        Span<FleetOrder> drain = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        Bridge.ConsumeFleetOrders(drain);
        Bridge.ConsumePlot(out _);
    }

    private Double3 Ahead(double metres) => _world.OwnPosition + new Double3(0, 0, -metres);

    private int FleetOrdersSent(Span<FleetOrder> into) => Bridge.ConsumeFleetOrders(into);

    [Fact]
    public void ARightClickInTheCommandViewOpensTheCourseMenuAndOrdersNothing()
    {
        CommandView();
        _courseMenuShown = true;
        Frame(() =>
        {
            Move(EmptySpace);
            Button(GlfwMouseButton.Right, true);
        });
        Frame(() => Button(GlfwMouseButton.Right, false));
        Frame();
        Assert.True(_world.CourseMenu.IsOpen);
        Assert.Equal(EmptySpace, _world.CourseMenu.At);
        // The point is on the command plane, where the pointer was when the button came up.
        Assert.InRange(Math.Abs(_world.CourseMenu.Target.Y - _world.CommandFocus.Y), 0.0, 1.0);
        Span<FleetOrder> fleet = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        Bridge.Consume(out var hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Equal(0, Bridge.ConsumeFleetOrders(fleet));
        Assert.False(Bridge.ConsumePlot(out _));
        Assert.Null(_world.NavTarget);
    }

    [Fact]
    public void ARightDragInTheCommandViewStillOnlyTurnsTheCamera()
    {
        CommandView();
        _courseMenuShown = true;
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(120, -30)));
        Frame(() => Button(GlfwMouseButton.Right, false));
        Frame();
        Assert.False(_world.CourseMenu.IsOpen);
    }

    [Fact]
    public void AClickElsewhereClosesTheCourseMenuAndOnlyCloses()
    {
        OpenCourseMenu(Ahead(100000));
        Assert.Equal(1, _world.Selection.Length);
        Frame(() =>
        {
            Move(EmptySpace + new float2(500, -300));
            Click();
        });
        Frame();
        Assert.False(_world.CourseMenu.IsOpen);
        // A plain click on empty space clears the selection; this one was only the menu's.
        Assert.Equal(1, _world.Selection.Length);
        Assert.Equal(0, _courseMoves + _courseJumps);

        // A right click elsewhere closes it and opens another at the new point.
        OpenCourseMenu(Ahead(100000));
        var elsewhere = EmptySpace + new float2(500, -300);
        Frame(() =>
        {
            Move(elsewhere);
            Button(GlfwMouseButton.Right, true);
        });
        Assert.False(_world.CourseMenu.IsOpen);
        Frame(() => Button(GlfwMouseButton.Right, false));
        Frame();
        Assert.True(_world.CourseMenu.IsOpen);
        Assert.Equal(elsewhere, _world.CourseMenu.At);
    }

    [Fact]
    public void EscapeClosesTheCourseMenuBeforeTheCommandViewAndLeavesTheViewAlone()
    {
        OpenCourseMenu(Ahead(100000));
        Assert.Equal(ScreenHost.BackStep.CourseMenu, ScreenHost.NextBackStep(false, false, false, _world.CourseMenu.IsOpen, ViewMode.Command));
        Assert.Equal(ScreenHost.BackStep.SessionMenu, ScreenHost.NextBackStep(false, false, true, true, ViewMode.Command));
        Assert.Equal(ScreenHost.BackStep.PilotView, ScreenHost.NextBackStep(false, false, false, false, ViewMode.Command));
        Assert.Equal(ScreenHost.BackStep.OpenMenu, ScreenHost.NextBackStep(false, false, false, false, ViewMode.Pilot));
        Assert.True(_world.CourseMenu.Close());
        Frame();
        Assert.Equal(ViewMode.Command, _world.Mode);
        Assert.False(_world.CourseMenu.Close());

        // Leaving the command view takes the menu with it.
        _world.CourseMenu.Open(EmptySpace, Ahead(100000), _world.Time);
        _world.SetMode(ViewMode.Pilot);
        Assert.False(_world.CourseMenu.IsOpen);
    }

    [Fact]
    public void MoveHereInTheCourseMenuIssuesTheMoveAndClosesIt()
    {
        OpenCourseMenu(Ahead(100000));
        Frame(() =>
        {
            Move(CourseRow(0));
            Click();
        });
        for (var i = 0; i < 3; i++)
            Frame();
        Assert.Equal(1, _courseMoves);
        Assert.False(_world.CourseMenu.IsOpen);
        Bridge.Consume(out var hasOrder, out var order);
        Assert.True(hasOrder);
        Assert.Equal(OrderKind.MoveTo, order.Kind);
        Assert.Equal(1, _world.Selection.Length);
    }

    [Fact]
    public void JumpHerePlotsTheShipBeingFlownAndOrdersTheOthers()
    {
        var far = Ahead(100000);
        OpenCourseMenu(far);

        // The ship being flown: only plotted. The pilot starts it with NAV and boost.
        var plan = _world.PlanJump(far);
        Assert.Equal(JumpBlock.None, plan.Block);
        Assert.Equal(1, plan.Ready);
        Assert.True(plan.PlotsOnly);
        Assert.True(plan.Reference.Blind);
        Assert.InRange(plan.Reference.Risk, 0.16f, 0.17f);
        Frame(() =>
        {
            Move(CourseRow(1));
            Click();
        });
        for (var i = 0; i < 3; i++)
            Frame();
        Assert.Equal(1, _courseJumps);
        Assert.False(_world.CourseMenu.IsOpen);
        Span<FleetOrder> fleet = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        Bridge.Consume(out var hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Equal(0, Bridge.ConsumeFleetOrders(fleet));
        Assert.True(Bridge.ConsumePlot(out var plot));
        Assert.Equal(OwnId, plot.Hull);
        Assert.Equal(far, plot.Target);
        Assert.Equal(far, _world.NavTarget);

        // A ship nobody flies: an order it carries out by itself, and no plot.
        var remote = RemoteOwnedId();
        _world.ClearSelection();
        _world.Select(remote, false);
        _world.CourseMenu.Open(EmptySpace, far, _world.Time);
        for (var i = 0; i < 10; i++)
            Frame();
        plan = _world.PlanJump(far);
        Assert.Equal(1, plan.ReadyRemote);
        Assert.False(plan.PlotsOnly);
        Frame(() =>
        {
            Move(CourseRow(1));
            Click();
        });
        for (var i = 0; i < 3; i++)
            Frame();
        Assert.Equal(2, _courseJumps);
        Assert.Equal(1, Bridge.ConsumeFleetOrders(fleet));
        Assert.Equal(remote, fleet[0].Hull);
        Assert.Equal(OrderKind.Jump, fleet[0].Order.Kind);
        Assert.Equal(far, fleet[0].Order.Target);
        Assert.False(Bridge.ConsumePlot(out _));
        Bridge.Consume(out hasOrder, out _);
        Assert.False(hasOrder);
    }

    [Fact]
    public void ASelectionOfBothPlotsTheOwnShipAndOrdersTheRest()
    {
        var far = Ahead(100000);
        CommandView();
        _world.SelectAllOwned();
        var plan = _world.PlanJump(far);
        Assert.Equal(3, plan.Ready);
        Assert.Equal(1, plan.ReadyPiloted);
        Assert.Equal(2, plan.ReadyRemote);
        Assert.True(plan.Mixed);
        Assert.False(plan.PlotsOnly);
        _world.OrderJump(Bridge, far);
        Span<FleetOrder> fleet = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        Assert.Equal(2, Bridge.ConsumeFleetOrders(fleet));
        Assert.True(Bridge.ConsumePlot(out var plot));
        Assert.Equal(OwnId, plot.Hull);
        Bridge.Consume(out var hasOrder, out _);
        Assert.False(hasOrder);
    }

    [Theory]
    [InlineData(3000.0, "TooClose")]
    [InlineData(10000.0, "SameSector")]
    [InlineData(500000.0, "TooFar")]
    public void ATargetOutOfRangeLeavesTheJumpRowDisabledWithItsReason(double metres, string reason)
    {
        var target = Ahead(metres);
        OpenCourseMenu(target);
        var plan = _world.PlanJump(target);
        Assert.Equal(Enum.Parse<JumpBlock>(reason), plan.Block);
        Assert.Equal(0, plan.Ready);
        Assert.Equal(metres, plan.Reference.Distance, 1.0);
        Assert.Equal(400000.0, plan.MaxDistance);
        Assert.Equal(5000.0, plan.MinDistance);

        // The row takes the click and does nothing: the menu stays open and nothing is sent.
        Frame(() =>
        {
            Move(CourseRow(1));
            Click();
        });
        for (var i = 0; i < 3; i++)
            Frame();
        Assert.Equal(0, _courseJumps);
        Assert.True(_world.CourseMenu.IsOpen);
        Span<FleetOrder> fleet = stackalloc FleetOrder[FleetOrdersMessage.MaxOrders];
        Assert.Equal(0, Bridge.ConsumeFleetOrders(fleet));
        Assert.False(Bridge.ConsumePlot(out _));
        Bridge.Consume(out var hasOrder, out _);
        Assert.False(hasOrder);
        Assert.Null(_world.NavTarget);

        // Move here still works from the same menu: it has no range.
        Frame(() =>
        {
            Move(CourseRow(0));
            Click();
        });
        Frame();
        Assert.Equal(1, _courseMoves);
    }

    [Fact]
    public void WithNothingSelectedBothRowsAreOffAndTheFiguresAreTheShipBeingFlowns()
    {
        var far = Ahead(100000);
        OpenCourseMenu(far);
        _world.ClearSelection();
        var plan = _world.PlanJump(far);
        Assert.Equal(JumpBlock.NoShips, plan.Block);
        Assert.Equal(0, plan.Ships);
        Assert.True(plan.HasReference);
        Assert.InRange(plan.Reference.Risk, 0.16f, 0.17f);
        Frame(() =>
        {
            Move(CourseRow(0));
            Click();
        });
        Frame(() =>
        {
            Move(CourseRow(1));
            Click();
        });
        Frame();
        Assert.Equal(0, _courseMoves + _courseJumps);
        Assert.True(_world.CourseMenu.IsOpen);
    }

    [Fact]
    public void AChartedDestinationHasNoBlindRisk()
    {
        var far = Ahead(100000);
        CommandView();
        var blind = _world.PlanJump(far);
        Assert.True(blind.Reference.Blind);
        Assert.True(blind.Reference.Risk > 0f);
        _snapshot.Exploration.Charts[_snapshot.Exploration.ChartCount++] = new SectorChart(_snapshot.PlayerId, _snapshot.Galaxy!.SectorOf(far), 1);
        Frame();
        var charted = _world.PlanJump(far);
        Assert.False(charted.Reference.Blind);
        Assert.Equal(0f, charted.Reference.Risk);
        Assert.Equal(JumpBlock.None, charted.Block);
    }

    [Fact]
    public void AnAnsweredPlotKeepsTheNavPointAndARefusedOneGivesBackTheServersOwn()
    {
        var far = Ahead(100000);
        CommandView();
        var own = OwnId;
        _world.OrderJump(Bridge, far);
        Assert.Equal(far, _world.NavTarget);

        // Accepted: the server's answer holds the point, and nothing changes.
        _snapshot.Exploration.Navigation = new NavigationView { Hull = own, ResultHull = own, HasPlot = true, Target = far };
        Frame();
        Assert.Equal(far, _world.NavTarget);
        Assert.Equal(JumpRefusal.None, _world.NavRefusal);

        // Refused: the marker returns to what the server holds, and the refusal is news.
        _world.OrderJump(Bridge, Ahead(250000));
        Assert.NotEqual(far, _world.NavTarget);
        _snapshot.Exploration.Navigation = new NavigationView { Hull = own, ResultHull = own, HasPlot = true, Target = far, Refusal = JumpRefusal.TooFar };
        Frame();
        Assert.Equal(far, _world.NavTarget);
        Assert.Equal(JumpRefusal.TooFar, _world.NavRefusal);
        Assert.True(_world.Time - _world.NavRefusalAt < 0.5f);

        // A refusal of a jump start (the hull drifted) is news too, with no plot waiting.
        _snapshot.Exploration.Navigation = new NavigationView { Hull = own, ResultHull = own, HasPlot = true, Target = far, Refusal = JumpRefusal.TooClose };
        Frame();
        Assert.Equal(JumpRefusal.TooClose, _world.NavRefusal);
        Assert.Equal(far, _world.NavTarget);
    }

    [Fact]
    public void TakingControlFromCommandEndsTheFramesCommandMouse()
    {
        CommandView();
        var distance = _world.CommandDistance;
        Frame(() =>
        {
            Key(GlfwKey.T, true);
            Wheel(3);
        });
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.Equal(distance, _world.CommandDistance);
        Assert.True(_router.Captured);
    }

    [Fact]
    public void ShiftAndARightDragOverTheWorldTurnTheCameraButNotTheHull()
    {
        PilotWithShift();
        Frame(() => Key(GlfwKey.W, true));
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);

        // Without the right button, the shown pointer turns nothing.
        Frame(() => Move(EmptySpace + new float2(150, 0)));
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);

        // Right press over the world, then drag: free look. The camera turns; under thrust the hull is not
        // told to align with it, and nothing is clicked or targeted.
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(400, 40)));
        var sent = Sent();
        Assert.True(_world.FreeLook);
        Assert.False(_router.Captured);
        Assert.True(sent.Forward > 0);
        Assert.False(sent.Align);
        Assert.True(Vector3.Distance(sent.Aim, Nose) > 0.05f, $"aim {sent.Aim} nose {Nose}");
        Assert.Equal(0u, _world.TargetHull);
        Assert.Equal(0, _buttonClicks);

        // Over the quick buttons mid-drag the drag stays the camera's and presses nothing.
        Frame(() => Move(OverButton));
        Assert.True(_world.FreeLook);

        // Letting go returns the view to the nose, as free look does.
        Frame(() => Button(GlfwMouseButton.Right, false));
        Frame();
        Assert.False(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);
        Assert.Equal(0, _buttonClicks);
    }

    [Fact]
    public void ARightDragBegunOnTheInterfaceNeverTurnsTheCamera()
    {
        PilotWithShift();
        Frame(() => Move(OverButton));
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace));
        Frame(() => Move(EmptySpace + new float2(300, 0)));
        Assert.False(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);
        Frame(() => Button(GlfwMouseButton.Right, false));
        Assert.Equal(0, _buttonClicks);
    }

    [Fact]
    public void ANewRightPressOnTheInterfaceCannotInheritACameraDrag()
    {
        PilotWithShift();
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(200, 0)));
        Assert.True(_world.FreeLook);
        Frame(() => Move(OverButton));

        // Both edges arrive in one poll: the new UI press must not continue the old world drag.
        Frame(() =>
        {
            Button(GlfwMouseButton.Right, false);
            Button(GlfwMouseButton.Right, true);
        });
        Frame(() => Move(EmptySpace));
        Frame(() => Move(EmptySpace + new float2(300, 0)));
        Assert.False(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);
        Assert.Equal(0, _buttonClicks);

        Frame(() => Button(GlfwMouseButton.Right, false));
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(400, 0)));
        Assert.True(_world.FreeLook);
    }

    [Fact]
    public void ReleaseMoveOntoUiAndPressInOnePollNeverStartsAnotherCameraDrag()
    {
        PilotWithShift();
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(100, 0)));
        Assert.True(_world.FreeLook);
        Frame(() =>
        {
            Button(GlfwMouseButton.Right, false);
            Move(OverButton);
            Button(GlfwMouseButton.Right, true);
        });
        Assert.False(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);
        Frame(() => Move(OverButton + new float2(20, 0)));
        Frame(() => Move(EmptySpace));
        Assert.False(_world.FreeLook);
    }

    [Fact]
    public void AUiPressFollowedByMotionInTheSamePollKeepsItsStartingPosition()
    {
        PilotWithShift();
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(100, 0)));
        Frame(() =>
        {
            Button(GlfwMouseButton.Right, false);
            Move(OverButton);
            Button(GlfwMouseButton.Right, true);
            Move(EmptySpace + new float2(200, 0));
        });
        Assert.False(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) < 1e-4f);
        Frame(() => Move(EmptySpace + new float2(300, 0)));
        Assert.False(_world.FreeLook);
    }

    [Fact]
    public void ANewRightPressOverSpaceCanStartAnotherCameraDragInTheSamePoll()
    {
        PilotWithShift();
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(100, 0)));
        Assert.True(_world.FreeLook);
        Frame(() =>
        {
            Button(GlfwMouseButton.Right, false);
            Button(GlfwMouseButton.Right, true);
        });
        Frame(() => Move(EmptySpace + new float2(200, 0)));
        Assert.True(_world.FreeLook);
        Assert.True(Vector3.Distance(Sent().Aim, Nose) > 0.05f);
    }

    [Fact]
    public void LettingGoOfShiftEndsTheCameraDragAndTheHeldButtonStaysMasked()
    {
        PilotWithShift();
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(200, 0)));
        Assert.True(_world.FreeLook);

        // Shift up with the button held: captured again, the drag ends and the held button is not free look.
        Frame(() => Key(GlfwKey.LeftShift, false));
        Assert.True(_router.Captured);
        Frame();
        Assert.False(_world.FreeLook);

        // Shift again with the same press still held: the drag does not come back.
        Frame(() => Key(GlfwKey.LeftShift, true));
        Frame(() => Move(EmptySpace + new float2(300, 0)));
        Assert.False(_world.FreeLook);
        Frame(() => Button(GlfwMouseButton.Right, false));

        // A fresh press is a drag again.
        Frame(() => Button(GlfwMouseButton.Right, true));
        Frame(() => Move(EmptySpace + new float2(100, 0)));
        Assert.True(_world.FreeLook);
        Frame(() => Button(GlfwMouseButton.Right, false));
    }

    [Theory]
    [InlineData(0x0000_0001_0000_0007UL, 0x0000_0002_0000_0007UL)]
    [InlineData(0x0000_0001_0000_0007UL, 0x0000_0001_0000_0008UL)]
    public void APressOnAnInvitationCannotAcceptItsReplacement(ulong shown, ulong replacement)
    {
        // Press Accept on one invitation; it is replaced before the release, the new card in the same place.
        var accepted = 0;
        void Card(ulong identity)
        {
            ImGui.GetIO().DisplaySize = Screen;
            ImGui.GetIO().DeltaTime = 1f / 60f;
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(new float2(100, 100));
            ImGui.SetNextWindowSize(new float2(300, 100));
            ImGui.Begin("##social"u8, ImGuiWindowFlags.NoDecoration);
            Kit.PushId(identity);
            ImGui.SetCursorScreenPos(new float2(120, 140));
            if (ImGui.InvisibleButton("Accept"u8, new float2(120, 40)))
                accepted++;
            Kit.PopId();
            ImGui.End();
            ImGui.Render();
        }

        var io = ImGui.GetIO();
        Card(shown);
        io.AddMousePosEvent(150, 160);
        Card(shown);
        io.AddMouseButtonEvent(0, true);
        Card(shown);
        io.AddMouseButtonEvent(0, false);
        Card(replacement);
        Card(replacement);
        Assert.Equal(0, accepted);

        // The same card is accepted by the same press and release.
        io.AddMouseButtonEvent(0, true);
        Card(replacement);
        io.AddMouseButtonEvent(0, false);
        Card(replacement);
        Assert.Equal(1, accepted);
    }

    // The pilot console at this test's screen, unscaled, and the middle of each of its rows.
    private static readonly FlightConsoleLayout Bars = FlightConsole.Layout(Screen, 1f);

    private static float2 MiddleOf(ConsoleBar bar)
    {
        var rect = FlightConsole.BarRect(Bars, bar);
        return new float2((rect.Left + rect.Right) * 0.5f, (rect.Top + rect.Bottom) * 0.5f);
    }

    /// <summary>The row the console lights this frame, from what ScreenHost passes it: pilot view, nothing open, the release key's pointer.</summary>
    private ConsoleBar ConsoleHovered => FlightConsole.Hovered(Bars, ImGui.GetIO().MousePos,
        !_surfaceOpen && _world.Mode == ViewMode.Pilot && _router.ReleaseHeld && !_router.Captured, _router.PointerOverInterface);

    [Fact]
    public void AConsoleRowIsPointedAtOnlyWithTheReleaseKeyAndLeavesWAndTheWorldAlone()
    {
        // Flying: the hidden pointer is over the shield bar, but the interface never sees it.
        Frame(() =>
        {
            Key(GlfwKey.W, true);
            Move(MiddleOf(ConsoleBar.Shield));
        });
        Frame();
        Assert.True(_router.Captured);
        Assert.Equal(ConsoleBar.None, ConsoleHovered);

        // Shift shows the pointer; each row is found where it is drawn, the gap between rows is none of them.
        PilotWithShift();
        Assert.Equal(ConsoleBar.None, ConsoleHovered);
        for (var bar = ConsoleBar.Shield; bar <= ConsoleBar.Heat; bar++)
        {
            Frame(() => Move(MiddleOf(bar)));
            Assert.Equal(bar, ConsoleHovered);
            Assert.False(ImGui.GetIO().WantCaptureMouse);
            Assert.False(ImGui.GetIO().WantCaptureKeyboard);
        }

        Frame(() => Move(new float2(MiddleOf(ConsoleBar.Hull).X, Bars.Top + Bars.Row + Bars.RowGap * 0.5f)));
        Assert.Equal(ConsoleBar.None, ConsoleHovered);

        // Pointing and clicking at a row takes nothing: W still reaches the hull, no interface button is pressed.
        Frame(() => Move(MiddleOf(ConsoleBar.Shield)));
        Frame(() => Click());
        Assert.Equal(ConsoleBar.Shield, ConsoleHovered);
        Bridge.Consume(out _, out _);
        Frame();
        Assert.True(Sent().Forward > 0);
        Assert.Equal(0, _buttonClicks);

        // Letting go of Shift captures the pointer again, and the interface forgets where it was.
        Frame(() => Key(GlfwKey.LeftShift, false));
        Frame();
        Assert.True(_router.Captured);
        Assert.False(FlightConsole.BarRect(Bars, ConsoleBar.Shield).Contains(ImGui.GetIO().MousePos));
        Assert.Equal(ConsoleBar.None, ConsoleHovered);

        // An open surface (chat, a menu) shows the pointer for itself: the interface sees it over the bar, and the
        // console still does not take it.
        _surfaceOpen = true;
        Frame();
        Frame(() => Move(MiddleOf(ConsoleBar.Shield)));
        Assert.False(_router.Captured);
        Assert.True(FlightConsole.BarRect(Bars, ConsoleBar.Shield).Contains(ImGui.GetIO().MousePos));
        Assert.Equal(ConsoleBar.None, ConsoleHovered);
    }

    [Fact]
    public void APinnedWindowOverTheConsoleKeepsItsRowsFromThePointer()
    {
        _floating = new FloatingWindow { Open = true, Pinned = true, Placed = true, Min = new float2(Bars.LeftX, Screen.Y - FloatingSize.Y - 4f) };
        PilotWithShift();
        Frame();
        Assert.True(_floatingDrawn.Contains(MiddleOf(ConsoleBar.Shield)), $"window {_floatingDrawn}");

        Frame(() => Move(MiddleOf(ConsoleBar.Shield)));
        Frame();
        Assert.True(_router.PointerOverInterface);
        Assert.Equal(ConsoleBar.None, ConsoleHovered);

        // The other group is in the clear.
        Frame(() => Move(MiddleOf(ConsoleBar.Energy)));
        Assert.False(_floatingDrawn.Contains(MiddleOf(ConsoleBar.Energy)));
        Assert.Equal(ConsoleBar.Energy, ConsoleHovered);
    }

    /// <summary>Collects a console row's details as text, as the tooltip would show them.</summary>
    private struct TipRecorder(List<string> lines) : IConsoleTipSink
    {
        public void Title(ReadOnlySpan<byte> title, ReadOnlySpan<byte> state, uint stateColor) =>
            lines.Add($"{Encoding.UTF8.GetString(title)}: {Encoding.UTF8.GetString(state)}");

        public void Line(ReadOnlySpan<byte> label, ReadOnlySpan<byte> value, uint valueColor) =>
            lines.Add($"{Encoding.UTF8.GetString(label)}: {Encoding.UTF8.GetString(value)}");

        public void Note(ReadOnlySpan<byte> text, uint color) => lines.Add(Encoding.UTF8.GetString(text));
    }

    private static List<string> Tip(ConsoleBar bar, in HullView own, in FlightParams flight)
    {
        UiText.ResetArena();
        var lines = new List<string>();
        var recorder = new TipRecorder(lines);
        ConsoleTip.Write(ref recorder, bar, own, flight);
        return lines;
    }

    [Fact]
    public void ARowsDetailsAreTheHullsOwnLiveFigures()
    {
        // A restart rule other than the shipped one: the threshold follows it.
        var flight = _snapshot.Flight;
        flight.ShieldCapacity = 3000f;
        flight.ShieldRestartFraction = 0.4f;
        flight.ShieldRecharge = 45f;
        flight.ShieldDelay = 2.5f;
        flight.ShieldRestartPower = 18f;
        var own = _snapshot.Hulls[_snapshot.OwnHull];
        own.Energy = flight.EnergyStorage;
        own.Shield = 600f;
        own.ShieldOffline = true;

        var offline = Tip(ConsoleBar.Shield, own, flight);
        Assert.Equal("SHIELD: OFFLINE", offline[0]);
        Assert.Contains("STORED: 600 / 3000", offline);
        Assert.Contains("RESTARTS AT: 1200 (40 %)", offline);
        Assert.Contains("PROGRESS: 50 %", offline);
        Assert.Contains("RECHARGE: 45 /S", offline);
        Assert.Contains("DELAY: 2.5 S AFTER DAMAGE", offline);
        Assert.Contains("RESTART DRAW: 18 MW", offline);
        Assert.Contains("NO PROTECTION UNTIL IT RESTARTS", offline);
        Assert.Equal(0.4f, FlightConsole.RestartMark(flight));

        // Online: the same rule is told as what a break would mean, and nothing says the hull is unprotected.
        own.ShieldOffline = false;
        own.Shield = 2400f;
        var online = Tip(ConsoleBar.Shield, own, flight);
        Assert.Equal("SHIELD: ONLINE", online[0]);
        Assert.Contains("CHARGE: 2400 / 3000", online);
        Assert.Contains("IF BROKEN: RESTARTS AT 40 %", online);
        Assert.DoesNotContain("NO PROTECTION UNTIL IT RESTARTS", online);

        // A flat battery stops the charge, and the details say so.
        own.Energy = 0f;
        Assert.Contains("BATTERY EMPTY: CHARGING ON SPARE POWER ONLY", Tip(ConsoleBar.Shield, own, flight));

        // No emitter: no threshold and no stored charge to speak of.
        flight.ShieldCapacity = 0f;
        own.ShieldOffline = true;
        var none = Tip(ConsoleBar.Shield, own, flight);
        Assert.Equal("SHIELD: NONE", none[0]);
        Assert.DoesNotContain(none, line => line.StartsWith("RESTARTS AT", StringComparison.Ordinal) || line.StartsWith("STORED", StringComparison.Ordinal));
        Assert.Equal(0f, FlightConsole.RestartMark(flight));

        // The battery's flow is read again each time it is asked for.
        own.Energy = flight.EnergyStorage * 0.5f;
        own.EnergyRate = -12.5f;
        Assert.Contains("NET FLOW: -12.5 MW", Tip(ConsoleBar.Energy, own, flight));
        own.EnergyRate = 3f;
        Assert.Contains("NET FLOW: +3.0 MW", Tip(ConsoleBar.Energy, own, flight));

        // Hull points, and the heat at which an overheated drive unlocks.
        own.Hull = flight.HullPoints * 0.25f;
        Assert.Contains($"POINTS: {flight.HullPoints * 0.25f:0} / {flight.HullPoints:0}", Tip(ConsoleBar.Hull, own, flight));
        own.Heat = 1f;
        own.BoostLocked = true;
        var heat = Tip(ConsoleBar.Heat, own, flight);
        Assert.Equal("BOOST HEAT: OVERHEATED", heat[0]);
        Assert.Contains($"UNLOCKS AT: {FlightModel.BoostUnlockHeat * 100f:0} %", heat);
    }

    [Theory]
    [InlineData(1280f, 720f, 1f)]
    [InlineData(1655f, 688f, 1f)]
    [InlineData(1920f, 1080f, 1f)]
    [InlineData(1280f, 720f, 2f)]
    [InlineData(1655f, 688f, 2f)]
    [InlineData(1920f, 1080f, 2f)]
    public void EveryRowsDetailsAndTheOfflineShieldFitTheScreenWithTheShippedFonts(float width, float height, float scale)
    {
        var shipped = ShippedFonts();
        var (mono, sans) = (shipped.Mono, shipped.SansMedium);
        ImGui.GetStyle().FontScaleMain = scale;
        _ui.NewFrame();
        try
        {
            UiText.ResetArena();
            var display = new float2(width, height);
            var layout = FlightConsole.Layout(display, scale);
            var side = MathF.Round(FlightConsole.Side * scale);

            // The largest figures the console prints in full, every state with its longest words.
            var flight = _snapshot.Flight;
            flight.ShieldCapacity = 99_999f;
            flight.HullPoints = 99_999f;
            flight.EnergyStorage = 99_999f;
            flight.PowerGenerated = 9_999f;
            flight.ShieldRestartPower = 999.9f;
            flight.ShieldPower = 999.9f;
            flight.ShieldDelay = 99.9f;
            flight.ShieldRecharge = 9_999f;
            flight.BoostEnergyFloor = 99_999f;
            var own = _snapshot.Hulls[_snapshot.OwnHull];
            own.Shield = 99_999f;
            own.Hull = 99_999f;
            own.EnergyRate = -9_999f;
            foreach (var state in new[] { 0, 1, 2 })
            {
                own.ShieldOffline = state == 1;
                own.BoostLocked = state == 1;
                own.Energy = state == 2 ? 0f : 99_999f;
                for (var bar = ConsoleBar.Shield; bar <= ConsoleBar.Heat; bar++)
                {
                    var size = ConsoleTip.Measure(mono, sans, scale, bar, own, flight);
                    var rect = FlightConsole.TipRect(layout, bar, size, display, scale);
                    Assert.Equal(size.X, rect.Width);
                    Assert.True(rect.Left >= side && rect.Right <= width - side, $"{bar} {state}: {rect}");
                    Assert.True(rect.Top >= 0f && rect.Bottom <= layout.Top, $"{bar} {state}: {rect}, console {layout.Top}");
                }
            }

            // Measuring a row's details every frame allocates nothing.
            own.ShieldOffline = true;
            ConsoleTip.Measure(mono, sans, scale, ConsoleBar.Shield, own, flight);
            var allocation = new AllocationProbe();
            for (var bar = ConsoleBar.Shield; bar <= ConsoleBar.Heat; bar++)
                while (allocation.Measuring()) ConsoleTip.Measure(mono, sans, scale, bar, own, flight);
            Assert.Equal(0, allocation.Bytes);

            // An offline shield's words fit its reading column; a compact row has OFFLINE beside its label.
            float Px(float design) => MathF.Round(design * scale);
            if (!layout.Compact)
            {
                Assert.InRange(Kit.Measure(mono, Theme.FontCaption, "RESTART 100 %"u8).X, 1f, Px(FlightConsole.ReadingColumn));
                Assert.InRange(Kit.Measure(sans, Theme.FontSmall, "OFFLINE"u8).X, 1f, Px(FlightConsole.ReadingColumn));
            }
            else
            {
                var room = layout.GroupWidth - Px(FlightConsole.Slant + 2 * FlightConsole.Pad + FlightConsole.ColumnGap) - Kit.Measure(sans, Theme.FontSmall, "SHIELD"u8).X;
                Assert.InRange(Kit.Measure(sans, Theme.FontSmall, "OFFLINE"u8).X, 1f, room);
            }
        }
        finally
        {
            ImGui.Render();
        }
    }

    /// <summary>Ticks the server and this client as their loops would, then takes the client's newest snapshot.</summary>
    private void Tick(int count = 1, bool sleep = false)
    {
        for (var i = 0; i < count; i++)
        {
            ((ITickHandler)_server).Tick(_tick);
            ((ITickHandler)_client).Tick(_tick++);
            if (sleep)
                Thread.Sleep(1);
        }

        _snapshot = _client.AcquireSnapshot();
    }

    /// <summary>
    /// One frame with the ship editor's keys read in the shortcut phase, as <c>ScreenHost.HandleEditorShortcut</c>
    /// reads them, and the job system pumped first, as <c>App.Run</c> pumps it.
    /// </summary>
    private void EditorFrame(Action? poll = null)
    {
        _jobs.Pump();
        Frame(poll, () => _world.Editor.HandleKeys(_actions, _devices, _snapshot.Hulls[_snapshot.OwnHull], Bridge, ImGui.GetIO().WantTextInput, _router.PointerOverInterface));
    }

    /// <summary>
    /// Draws frames until the editor's worker has checked the draft as it stands. A check that is running
    /// finishes before the next frame is drawn, so the frames that pass are the ones the editor waits before it
    /// checks an edit, and not however long its worker was kept from a processor. A frame is a sixtieth of a
    /// second here whatever the clock says, so 600 of them are ten seconds of the editor's time.
    /// </summary>
    private void AwaitCheck()
    {
        var editor = _world.Editor;
        for (var i = 0; i < 600 && !(editor.Check == DraftCheck.Ready && editor.CheckedVersion == editor.Draft!.Version); i++)
        {
            FinishJobs();
            EditorFrame();
        }
    }

    /// <summary>Waits for the jobs that are scheduled or running, pumping as the frame loop does; one that never ends fails the test.</summary>
    private void FinishJobs()
    {
        var limit = TimeSpan.FromSeconds(30);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var spin = new SpinWait();
        while (true)
        {
            _jobs.Pump();
            if (_jobs.Pending == 0)
                return;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > limit)
                Assert.Fail($"{_jobs.Pending} job(s) were still running after {limit.TotalSeconds:0} s; the editor's check is {_world.Editor.Check}");
            spin.SpinOnce(-1);
        }
    }

    /// <summary>Simulates and draws, as the game does, until a condition holds; the renderer reports every design installed.</summary>
    private bool PlayUntil(Func<bool> condition, int steps = 3000)
    {
        for (var i = 0; i < steps && !condition(); i++)
        {
            Tick(1, sleep: true);
            Bridge.ReportInstalledDesigns(_snapshot.Content.Designs.Length);
            EditorFrame();
        }

        return condition();
    }

    /// <summary>Presses and lets go of the editor key, then plays until the server's grant has opened the editor.</summary>
    private void OpenEditor()
    {
        EditorFrame(() => Key(GlfwKey.B, true));
        EditorFrame(() => Key(GlfwKey.B, false));
        Assert.True(PlayUntil(() => _world.Editor.Shown), $"the editor should open, not {_world.Editor.Stage} {_snapshot.Editor.Phase}/{_snapshot.Editor.Reason}");
    }

    /// <summary>Where the edited hull's centre is on screen this frame.</summary>
    private float2 HullOnScreen()
    {
        Assert.True(_world.Project(_world.OwnDrawnPosition, out var at));
        return at;
    }

    [Fact]
    public void TheEditorKeyAsksFirstAndTheGrantShowsTheEditorWithThePointerAndNothingForTheHull()
    {
        Frame();
        Assert.True(_router.Captured);
        Frame(() => Key(GlfwKey.W, true));
        Assert.True(Sent().Forward > 0);

        // The key asks the server; until it answers the world flies on as before, the pointer still steers.
        EditorFrame(() => Key(GlfwKey.B, true));
        Assert.Equal(EditorStage.Requesting, _world.Editor.Stage);
        EditorFrame(() => Key(GlfwKey.B, false));
        Assert.False(_world.Editor.Shown);
        Assert.True(_router.Captured);
        Assert.True(Sent().Forward > 0);

        // The frame that sees the grant shows the editor, frees the pointer and gives the hull no controls, with W
        // still held.
        for (var i = 0; i < 600 && _snapshot.Editor.Phase != EditorPhase.Open; i++)
            Tick();
        Assert.Equal(EditorPhase.Open, _snapshot.Editor.Phase);
        EditorFrame();
        Assert.True(_world.Editor.Shown);
        Assert.True(_world.PointerView);
        Assert.False(_router.Captured);
        var sent = Sent();
        Assert.Equal((0f, false, false), (sent.Forward, sent.Boost, sent.Align));

        // Boost, the command key and Halt are not flight's while the editor is open.
        EditorFrame(() =>
        {
            Key(GlfwKey.Space, true);
            Key(GlfwKey.N, true);
            Key(GlfwKey.H, true);
        });
        sent = Sent();
        Assert.Equal(ViewMode.Pilot, _world.Mode);
        Assert.False(sent.Boost);
        Assert.Equal(0f, sent.Forward);
        Assert.Equal(OrderKind.None, _snapshot.Hulls[_snapshot.OwnHull].Order.Kind);
    }

    [Fact]
    public void EscapeClosesTheEditorAndKeysHeldThroughItWaitToBeLetGo()
    {
        OpenEditor();
        EditorFrame(() => Key(GlfwKey.W, true));
        Assert.Equal(0f, Sent().Forward);

        EditorFrame(() => Key(GlfwKey.Escape, true));
        Assert.Equal(EditorStage.Closed, _world.Editor.Stage);
        Assert.Null(_world.Editor.Draft);
        EditorFrame(() => Key(GlfwKey.Escape, false));
        Assert.True(_router.Captured);
        Assert.True(_world.FlightHeld);
        EditorFrame();
        Assert.Equal(0f, Sent().Forward);

        // W let go and pressed again flies.
        EditorFrame(() => Key(GlfwKey.W, false));
        Assert.False(_world.FlightHeld);
        EditorFrame(() => Key(GlfwKey.W, true));
        Assert.True(Sent().Forward > 0);
        Assert.True(PlayUntil(() => _snapshot.Editor.Phase == EditorPhase.Closed));
        Assert.Equal(EditorReason.Cancelled, _snapshot.Editor.Reason);
    }

    [Fact]
    public void TheEditorKeyIsLeftToATextFieldThatHasTheKeyboard()
    {
        _floating = new FloatingWindow { Open = true, Pinned = false };
        _floatingFull = true;
        _floatingField = true;
        _surfaceOpen = true;
        Frame();
        Frame(() => Move(FieldOf(_floating)));
        Frame(() => Click());
        Frame();
        Assert.True(ImGui.GetIO().WantTextInput);
        EditorFrame(() => Key(GlfwKey.B, true));
        Assert.Equal(EditorStage.Closed, _world.Editor.Stage);
    }

    [Fact]
    public void ThePointerOverAPanelNeitherPlacesNorOrbitsAndOverTheShipItPlaces()
    {
        OpenEditor();
        var editor = _world.Editor;
        var blocks = editor.Draft!.Boxes.Length;
        for (var i = 0; i < 30; i++)
            EditorFrame();

        // Over the ship the part's ghost shows where it would go, and fits.
        var hull = HullOnScreen();
        EditorFrame(() => Move(hull));
        Assert.True(editor.HasHover);
        Assert.True(editor.HasGhost);
        Assert.Equal(DraftEdit.Done, editor.GhostCheck);

        // Over a panel: no ghost, and a click there is the panel's.
        EditorFrame(() => Move(OverButton));
        EditorFrame();
        Assert.True(_router.PointerOverInterface);
        Assert.False(editor.HasHover);
        EditorFrame(() => Click());
        EditorFrame();
        Assert.Equal(1, _buttonClicks);
        Assert.Equal(blocks, editor.Draft!.Boxes.Length);

        // A right drag begun on the panel does not orbit; one begun over space does.
        Vector3 Camera() => _world.CameraPosition.RelativeTo(_world.OwnDrawnPosition);
        var before = Camera();
        EditorFrame(() => Button(GlfwMouseButton.Right, true));
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() => Move(EmptySpace + new float2(120, 0)));
        EditorFrame(() => Button(GlfwMouseButton.Right, false));
        Assert.True((Camera() - before).Length() < 0.05f, $"moved {(Camera() - before).Length()}");
        EditorFrame(() => Button(GlfwMouseButton.Right, true));
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() => Move(EmptySpace + new float2(-120, 0)));
        EditorFrame(() => Button(GlfwMouseButton.Right, false));
        for (var i = 0; i < 30; i++)
            EditorFrame();
        Assert.True((Camera() - before).Length() > 1f);

        // A click over the ship places the part, and Ctrl+Z takes it back.
        hull = HullOnScreen();
        EditorFrame(() => Move(hull));
        Assert.Equal(DraftEdit.Done, editor.GhostCheck);
        EditorFrame(() => Click());
        Assert.Equal(blocks + 1, editor.Draft!.Boxes.Length);
        EditorFrame(() =>
        {
            Key(GlfwKey.LeftControl, true);
            Key(GlfwKey.Z, true);
        });
        Assert.Equal(blocks, editor.Draft!.Boxes.Length);
        EditorFrame(() => Key(GlfwKey.Z, false));
        EditorFrame(() => Key(GlfwKey.Y, true));
        Assert.Equal(blocks + 1, editor.Draft!.Boxes.Length);
    }

    [Fact]
    public void AnEditPlacedWithTheMouseIsAppliedByTheServerAndTheNewShipFlies()
    {
        OpenEditor();
        var editor = _world.Editor;
        var old = _snapshot.Hulls[_snapshot.OwnHull].Id;
        var oldBlocks = BlockSet.Count(_snapshot.Hulls[_snapshot.OwnHull].Blocks.Span);
        var designs = _snapshot.Content.Designs.Length;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        Assert.False(editor.CanApply);

        EditorFrame(() => Move(HullOnScreen()));
        EditorFrame(() => Click());
        Assert.True(editor.Draft!.CanUndo);
        // Apply waits for the worker's check of exactly this version.
        Assert.False(editor.CanApply);
        AwaitCheck();

        Assert.Equal(DraftCheck.Ready, editor.Check);
        Assert.True(editor.CanApply);
        Assert.Equal(oldBlocks + 1, editor.Checked.Design!.BlockCount);

        Assert.True(editor.Apply(Bridge));
        Assert.Equal(EditorStage.Applying, editor.Stage);
        Assert.False(editor.CanEdit);
        Assert.True(PlayUntil(() => editor.Stage == EditorStage.Closed), $"{_snapshot.Editor.Phase}/{_snapshot.Editor.Reason}");
        Assert.True(editor.NoticeApplied, $"{editor.Notice}");
        Assert.Null(editor.Draft);
        Assert.True(PlayUntil(() => _snapshot.Hulls[_snapshot.OwnHull].Id != old));
        ref var own = ref _snapshot.Hulls[_snapshot.OwnHull];
        Assert.Equal(_snapshot.Editor.Hull, own.Id);
        Assert.Equal(designs, own.Design);
        Assert.Equal(oldBlocks + 1, BlockSet.Count(own.Blocks.Span));

        // The pilot view is back, and the new ship takes the controls.
        EditorFrame();
        Assert.False(_world.Editor.Shown);
        Assert.True(_router.Captured);
        EditorFrame(() => Key(GlfwKey.W, true));
        Assert.True(Sent().Forward > 0);
    }

    [Fact]
    public void DamageClosesTheEditKeepsTheDraftAndTheNextGrantGoesOnWithIt()
    {
        OpenEditor();
        var editor = _world.Editor;
        var blocks = editor.Draft!.Boxes.Length;
        var token = editor.Token;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        EditorFrame(() => Move(HullOnScreen()));
        EditorFrame(() => Click());
        Assert.Equal(blocks + 1, editor.Draft!.Boxes.Length);

        // A hit while the edit is open: the server ends it, the editor says why and keeps the player's changes.
        var hulls = (HullStore)typeof(ServerHost).GetField("_hulls", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_server)!;
        var own = (int)(_snapshot.Hulls[_snapshot.OwnHull].Id & 0xFFFF);
        hulls.Info(own).DamageTick = _tick;
        Assert.True(PlayUntil(() => editor.Stage == EditorStage.Closed));
        Assert.Equal(EditorReason.Damaged, editor.Notice);
        Assert.True(editor.DraftKept);
        Assert.False(_world.Editor.Shown);
        Assert.True(_router.Captured);

        // Asking again at once is refused with the wait, and the draft is still kept.
        EditorFrame(() => Key(GlfwKey.B, true));
        EditorFrame(() => Key(GlfwKey.B, false));
        Assert.True(PlayUntil(() => editor.Stage == EditorStage.Closed));
        Assert.Equal(EditorReason.Quiet, editor.Notice);
        Assert.True(editor.NoticeWait > 0);
        Assert.NotNull(editor.Draft);

        // Once the hull is quiet again, the grant opens the same draft, on the edit's new token.
        hulls.Info(own).DamageTick = long.MinValue;
        OpenEditor();
        Assert.Equal(blocks + 1, editor.Draft!.Boxes.Length);
        Assert.True(editor.Draft.CanUndo);
        Assert.False(editor.DraftRestarted);
        Assert.NotEqual(token, editor.Token);
    }

    private int DraftBlocks => _world.Editor.Draft!.Boxes.Length;

    /// <summary>One frame with keys pressed together in one poll, then let go in the next.</summary>
    private void Chord(params GlfwKey[] keys)
    {
        EditorFrame(() =>
        {
            foreach (var key in keys)
                Key(key, true);
        });
        EditorFrame(() =>
        {
            foreach (var key in keys)
                Key(key, false);
        });
    }

    /// <summary>
    /// Moves the pointer over the ship, a frame per step, until the editor shows what <paramref name="found"/> wants
    /// there; false when no point near the ship does.
    /// </summary>
    private bool PointWhere(Func<ShipEditor, bool> found, out float2 at)
    {
        var middle = HullOnScreen();
        for (var ring = 0; ring <= 12; ring++)
        {
            for (var step = 0; step < Math.Max(1, ring * 8); step++)
            {
                var angle = step * MathF.Tau / Math.Max(1, ring * 8);
                at = middle + new float2(MathF.Cos(angle), MathF.Sin(angle)) * (ring * 14f);
                var point = at;
                EditorFrame(() => Move(point));
                if (found(_world.Editor))
                    return true;
            }
        }

        at = default;
        return false;
    }

    /// <summary>Opens the editor and selects two blocks with the middle button, the second with Shift; returns both.</summary>
    private (int First, int Second, float2 SecondAt) SelectTwoWithTheMiddleButton()
    {
        var editor = _world.Editor;
        EditorFrame(() => Move(HullOnScreen()));
        Assert.True(editor.HasHover);
        var first = editor.Hover.Block;
        EditorFrame(() => Click(GlfwMouseButton.Middle));
        Assert.Equal([first], editor.Selection.ToArray());
        Assert.True(PointWhere(e => e.HasHover && e.Hover.Block != first, out var at));
        var second = editor.Hover.Block;
        EditorFrame(() =>
        {
            Key(GlfwKey.LeftShift, true);
            Click(GlfwMouseButton.Middle);
        });
        EditorFrame(() => Key(GlfwKey.LeftShift, false));
        Assert.Equal([first, second], editor.Selection.ToArray());
        return (first, second, at);
    }

    [Fact]
    public void APartDraggedFromItsCardIsPlacedOnTheShipAndNothingElseOnAPanelReachesTheShip()
    {
        OpenEditor();
        var editor = _world.Editor;
        var types = _snapshot.Content.Blocks.Types;
        _palettePart = types.ToList().FindIndex(t => t.Id == "thruster");
        Assert.NotEqual(editor.Part, _palettePart);
        _paletteShown = true;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        var blocks = DraftBlocks;
        var hull = HullOnScreen();

        // Held on the card and dragged out over the ship: the card's part follows the pointer, nothing is placed yet.
        EditorFrame(() => Move(OverCard));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        EditorFrame(() => Move(OverCard + new float2(20, 0)));
        Assert.Equal(EditorCarry.Part, editor.Carry);
        Assert.Equal(_palettePart, editor.Part);
        EditorFrame(() => Move(hull));
        EditorFrame();
        Assert.True(_router.PointerOverInterface, "the part list still holds the press");
        Assert.True(editor.HasGhost);
        Assert.Equal(DraftEdit.Done, editor.GhostCheck);
        Assert.Equal(blocks, DraftBlocks);

        // Let go over the ship: placed without selecting it, and nothing is carried any more.
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(blocks + 1, DraftBlocks);
        Assert.Same(types[_palettePart], editor.Draft!.Boxes[blocks].Type);
        Assert.Empty(editor.Selection.ToArray());
        Assert.Equal(EditorCarry.None, editor.Carry);

        // Dragged out and back onto a panel: let go there, nothing is placed and the panel is not clicked.
        EditorFrame(() => Move(OverCard));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        EditorFrame(() => Move(OverCard + new float2(20, 0)));
        EditorFrame(() => Move(hull));
        Assert.True(editor.HasGhost);
        EditorFrame(() => Move(OverButton));
        EditorFrame();
        Assert.False(editor.HasGhost);
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        EditorFrame();
        Assert.Equal(blocks + 1, DraftBlocks);
        Assert.Equal(EditorCarry.None, editor.Carry);
        Assert.Equal(0, _buttonClicks);

        // A press that begins on any other panel never reaches the ship, wherever it is let go.
        EditorFrame(() => Move(OverButton));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        EditorFrame(() => Move(hull));
        EditorFrame();
        Assert.Equal(EditorCarry.None, editor.Carry);
        Assert.False(editor.HasGhost);
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        EditorFrame();
        Assert.Equal(blocks + 1, DraftBlocks);

        // And a drag let go with Escape first leaves the edit open with nothing changed.
        EditorFrame(() => Move(OverCard));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        EditorFrame(() => Move(OverCard + new float2(20, 0)));
        EditorFrame(() => Move(hull));
        EditorFrame(() => Key(GlfwKey.Escape, true));
        Assert.Equal(EditorCarry.None, editor.Carry);
        Assert.True(editor.Shown);
        EditorFrame(() => Key(GlfwKey.Escape, false));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(blocks + 1, DraftBlocks);
    }

    [Fact]
    public void ClosingDuringAPartDragAllowsTheFirstDragAfterReopening()
    {
        OpenEditor();
        var editor = _world.Editor;
        _paletteShown = true;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        EditorFrame(() => Move(OverCard));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        EditorFrame(() => Move(OverCard + new float2(20, 0)));
        Assert.Equal(EditorCarry.Part, editor.Carry);

        EditorFrame(() => Key(GlfwKey.B, true));
        Assert.Equal(EditorStage.Closed, editor.Stage);
        // The card is gone when the mouse is released, so its deactivation cannot reset the press.
        _paletteShown = false;
        EditorFrame(() => Key(GlfwKey.B, false));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        Tick(3);
        OpenEditor();
        _paletteShown = true;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        var blocks = DraftBlocks;
        EditorFrame(() => Move(OverCard));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        EditorFrame(() => Move(OverCard + new float2(20, 0)));
        Assert.Equal(EditorCarry.Part, editor.Carry);
        EditorFrame(() => Move(HullOnScreen()));
        EditorFrame();
        Assert.True(editor.HasGhost);
        Assert.Equal(DraftEdit.Done, editor.GhostCheck);
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(blocks + 1, DraftBlocks);
    }

    [Fact]
    public void AClickedCardSearchOrShapeGivesTheWorldBackSoTheGhostFollowsAndTheFirstClickPlacesOnce()
    {
        // As the game sets the interface up: keyboard navigation claims the keyboard for a focused window.
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.NavEnableGamepad;
        ImGui.GetIO().ConfigNavCursorVisibleAuto = true;
        OpenEditor();
        var editor = _world.Editor;
        var types = _snapshot.Content.Blocks.Types;
        _palettePart = types.ToList().FindIndex(t => t.Id == "thruster");
        Assert.NotEqual(editor.Part, _palettePart);
        _paletteShown = true;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        var blocks = DraftBlocks;

        // Click a card and let go: the part is chosen, nothing is carried or placed, and the keyboard is the world's.
        EditorFrame(() => Move(OverCard));
        EditorFrame();
        EditorFrame(() => Click());
        EditorFrame();
        Assert.Equal(_palettePart, editor.Part);
        Assert.True(editor.Placing);
        Assert.Equal(EditorCarry.None, editor.Carry);
        // Dear ImGui decides who has the keyboard before it reads navigation, so the frame after the release still
        // reports it claimed; the one after that, it is the world's.
        EditorFrame();
        Assert.False(ImGui.GetIO().WantCaptureKeyboard);
        Assert.Equal(blocks, DraftBlocks);

        // Straight on to the ship, with no click between: the ghost follows the pointer, and the first click places
        // the part there, once.
        Assert.True(PointWhere(e => e.HasGhost && e.GhostCheck == DraftEdit.Done, out _));
        var drawn = (editor.GhostAt, editor.PartSize, editor.PartShape);
        EditorFrame(() => Click());
        EditorFrame();
        EditorFrame();
        Assert.Equal(blocks + 1, DraftBlocks);
        Assert.Same(types[_palettePart], editor.Draft!.Boxes[^1].Type);
        Assert.Equal(drawn, (editor.Draft.Boxes[^1].At, editor.Draft.Boxes[^1].Size, editor.Draft.Boxes[^1].Shape));
        blocks++;

        // The search keeps the keyboard while it is typed in: the world takes nothing, not even the pointer's hover.
        EditorFrame(() => Move(OverSearch));
        EditorFrame(() => Click());
        EditorFrame();
        Assert.True(ImGui.GetIO().WantTextInput);
        EditorFrame(() => Move(HullOnScreen()));
        EditorFrame();
        Assert.False(editor.HasGhost);
        Assert.True(ImGui.GetIO().WantTextInput);

        // Choosing a card from there ends the typing and gives the world back, as above.
        EditorFrame(() => Move(OverCard));
        EditorFrame();
        EditorFrame(() => Click());
        EditorFrame();
        EditorFrame();
        Assert.False(ImGui.GetIO().WantTextInput);
        Assert.False(ImGui.GetIO().WantCaptureKeyboard);
        Assert.True(PointWhere(e => e.HasGhost && e.GhostCheck == DraftEdit.Done, out _));
        EditorFrame(() => Click());
        EditorFrame();
        Assert.Equal(blocks + 1, DraftBlocks);
        blocks++;

        // A shape button of the list does the same.
        EditorFrame(() => Move(OverShapeBox));
        EditorFrame();
        editor.SetPartShape(BlockShape.WedgeXPlusPlus);
        EditorFrame(() => Click());
        EditorFrame();
        EditorFrame();
        Assert.Equal(BlockShape.Box, editor.PartShape);
        Assert.False(ImGui.GetIO().WantCaptureKeyboard);
        Assert.True(PointWhere(e => e.HasGhost && e.GhostCheck == DraftEdit.Done, out _));
        EditorFrame(() => Click());
        EditorFrame();
        Assert.Equal(blocks + 1, DraftBlocks);
        blocks++;

        // A press on the card held and let go over the card, then over the ship: no drag, nothing placed by it.
        EditorFrame(() => Move(OverCard));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        EditorFrame(() => Move(OverCard + new float2(2, 0)));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        EditorFrame(() => Move(HullOnScreen()));
        EditorFrame();
        EditorFrame();
        Assert.Equal(EditorCarry.None, editor.Carry);
        Assert.Equal(blocks, DraftBlocks);
    }

    [Fact]
    public void AMiddleClickSelectsWhilePlacingShiftAddsOrRemovesAndAMiddleDragPansWithoutSelecting()
    {
        OpenEditor();
        var editor = _world.Editor;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        var blocks = DraftBlocks;
        Assert.True(editor.Placing);

        var (first, second, secondAt) = SelectTwoWithTheMiddleButton();
        Assert.True(editor.Placing, "selecting never stops placing");
        Assert.Equal(blocks, DraftBlocks);
        Assert.Equal(second, editor.Selected);

        // Shift and the middle button again on a selected block takes it out.
        EditorFrame(() => Move(secondAt));
        EditorFrame(() =>
        {
            Key(GlfwKey.LeftShift, true);
            Click(GlfwMouseButton.Middle);
        });
        EditorFrame(() => Key(GlfwKey.LeftShift, false));
        Assert.Equal([first], editor.Selection.ToArray());

        // A middle drag pans the camera and selects nothing.
        var focus = _world.EditorFocus;
        var start = HullOnScreen();
        EditorFrame(() => Move(start));
        EditorFrame(() => Button(GlfwMouseButton.Middle, true));
        EditorFrame(() => Move(start + new float2(60, 0)));
        EditorFrame(() => Move(start + new float2(140, 30)));
        EditorFrame(() => Button(GlfwMouseButton.Middle, false));
        Assert.True((_world.EditorFocus - focus).Length() > 0.1f, $"panned {(_world.EditorFocus - focus).Length()}");
        Assert.Equal([first], editor.Selection.ToArray());

        // A middle click on empty space clears the selection; with Shift it changes nothing.
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() =>
        {
            Key(GlfwKey.LeftShift, true);
            Click(GlfwMouseButton.Middle);
        });
        EditorFrame(() => Key(GlfwKey.LeftShift, false));
        Assert.Equal([first], editor.Selection.ToArray());
        EditorFrame(() => Click(GlfwMouseButton.Middle));
        Assert.Empty(editor.Selection.ToArray());
        Assert.Equal(blocks, DraftBlocks);
    }

    [Fact]
    public void AGroupIsDeletedPastedAndMovedEachAsOneUndoAndADropOnAPanelChangesNothing()
    {
        OpenEditor();
        var editor = _world.Editor;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        var before = editor.Draft!.Boxes.ToArray();
        SelectTwoWithTheMiddleButton();

        // Copy, then delete both at once; one undo brings both back, and the selection does not name moved blocks.
        Chord(GlfwKey.LeftControl, GlfwKey.C);
        Assert.Equal(2, editor.Clipboard.Length);
        Chord(GlfwKey.Delete);
        Assert.Equal(before.Length - 2, DraftBlocks);
        Assert.Empty(editor.Selection.ToArray());
        Chord(GlfwKey.LeftControl, GlfwKey.Z);
        Assert.Equal(before, editor.Draft!.Boxes.ToArray());
        Assert.Empty(editor.Selection.ToArray());

        // Paste: the copies follow the pointer, snapped to the face under it, and a click where they fit places both.
        Chord(GlfwKey.LeftControl, GlfwKey.V);
        Assert.Equal(EditorCarry.Paste, editor.Carry);
        Assert.Equal(before.Length, DraftBlocks);
        Assert.True(PointWhere(e => e.HasCarryGhost && e.CarryCheck == DraftEdit.Done, out _));
        Assert.Equal(before.Length, DraftBlocks);
        EditorFrame(() => Click());
        Assert.Equal(before.Length + 2, DraftBlocks);
        Assert.Equal(EditorCarry.None, editor.Carry);
        Assert.Empty(editor.Selection.ToArray());
        Chord(GlfwKey.LeftControl, GlfwKey.Z);
        Assert.Equal(before, editor.Draft!.Boxes.ToArray());

        // Move: a left drag from a selected block carries it; let go over a panel, nothing changes.
        EditorFrame(() => Move(HullOnScreen()));
        var held = editor.Hover.Block;
        EditorFrame(() => Click(GlfwMouseButton.Middle));
        var version = editor.Draft!.Version;
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        EditorFrame(() => Move(HullOnScreen() + new float2(12, 0)));
        Assert.Equal(EditorCarry.Move, editor.Carry);
        EditorFrame(() => Move(OverButton));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(EditorCarry.None, editor.Carry);
        Assert.Equal(version, editor.Draft!.Version);
        Assert.Equal(0, _buttonClicks);

        // Let go where it fits, it moves there; its index and the selection stay, and one undo puts it back.
        EditorFrame(() => Move(HullOnScreen()));
        Assert.Equal(held, editor.Hover.Block);
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        EditorFrame(() => Move(HullOnScreen() + new float2(12, 0)));
        Assert.True(PointWhere(e => e.HasCarryGhost && e.CarryCheck == DraftEdit.Done, out _));
        var by = editor.CarryBy;
        Assert.Equal(before, editor.Draft!.Boxes.ToArray());
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        var moved = editor.Draft!.Boxes.ToArray();
        Assert.Equal(before.Length, moved.Length);
        Assert.Equal(before[held] with { At = new GridSize(before[held].At.X + by.X, before[held].At.Y + by.Y, before[held].At.Z + by.Z) }, moved[held]);
        Assert.Equal([held], editor.Selection.ToArray());
        Chord(GlfwKey.LeftControl, GlfwKey.Z);
        Assert.Equal(before, editor.Draft!.Boxes.ToArray());
    }

    [Fact]
    public void AWedgeIsChosenTurnedPlacedCopiedPastedAndAppliedInItsShapeThroughTheServer()
    {
        OpenEditor();
        var editor = _world.Editor;
        var old = _snapshot.Hulls[_snapshot.OwnHull].Id;
        var designs = _snapshot.Content.Designs.Length;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        var blocks = DraftBlocks;

        // Choose a wedge, two cells long on x, and turn it a quarter about z: its slope turns and its size swaps.
        editor.SetPartSize(0, 2);
        editor.SetPartShape(BlockShape.WedgeXPlusPlus);
        editor.RotatePart(2);
        var shape = BlockShapes.Rotate(BlockShape.WedgeXPlusPlus, 2);
        Assert.Equal(shape, editor.PartShape);
        Assert.Equal(new GridSize(1, 2, 1), editor.PartSize);

        // Placed with a click where its ghost fits: the draft has it in that shape and size, unselected.
        Assert.True(PointWhere(e => e.HasGhost && e.GhostCheck == DraftEdit.Done, out _));
        EditorFrame(() => Click());
        Assert.Equal(blocks + 1, DraftBlocks);
        var placed = editor.Draft!.Boxes[^1];
        Assert.Equal((shape, new GridSize(1, 2, 1)), (placed.Shape, placed.Size));
        Assert.Empty(editor.Selection.ToArray());

        // Explicitly select the placed block before turning it; placement itself leaves it unselected.
        editor.Select(blocks, false);
        // A turn of the placed block, where it fits, is one undo; the wedge's slope turns with it.
        var rotated = false;
        for (var axis = 0; axis < 3; axis++)
        {
            if (editor.CanRotateSelected(axis) != DraftEdit.Done)
                continue;
            editor.RotateSelected(axis);
            Assert.Equal(BlockShapes.Rotate(shape, axis), editor.Draft!.Boxes[blocks].Shape);
            editor.Undo();
            Assert.Equal(placed, editor.Draft!.Boxes[blocks]);
            editor.Select(blocks, false);
            rotated = true;
            break;
        }
        Assert.True(rotated, "the placed wedge must have room for at least one tested rotation");

        // Copied and pasted, the copy keeps the shape.
        Chord(GlfwKey.LeftControl, GlfwKey.C);
        Assert.Equal(shape, editor.Clipboard[0].Shape);
        Chord(GlfwKey.LeftControl, GlfwKey.V);
        Assert.Equal(EditorCarry.Paste, editor.Carry);
        Assert.True(PointWhere(e => e.HasCarryGhost && e.CarryCheck == DraftEdit.Done, out _));
        EditorFrame(() => Click());
        Assert.Equal(blocks + 2, DraftBlocks);
        Assert.Equal((shape, placed.Size), (editor.Draft!.Boxes[^1].Shape, editor.Draft!.Boxes[^1].Size));

        // Checked as the server checks it and applied: the new ship has both wedges.
        AwaitCheck();

        Assert.Equal(DraftCheck.Ready, editor.Check);
        Assert.True(editor.Apply(Bridge));
        Assert.True(PlayUntil(() => editor.Stage == EditorStage.Closed), $"{_snapshot.Editor.Phase}/{_snapshot.Editor.Reason}");
        Assert.True(editor.NoticeApplied, $"{editor.Notice}");
        Assert.True(PlayUntil(() => _snapshot.Hulls[_snapshot.OwnHull].Id != old));
        ref var own = ref _snapshot.Hulls[_snapshot.OwnHull];
        Assert.Equal(designs, own.Design);
        var design = _snapshot.Content.Designs[own.Design];
        var wedges = 0;
        foreach (var box in design.Boxes)
            wedges += box.Shape == shape ? 1 : 0;
        Assert.Equal(2, wedges);
        Assert.Equal(design.BlockCount, BlockSet.Count(own.Blocks.Span));
    }

    [Fact]
    public void MergingTwoBlocksIsOneUndoAndTheSelectionNeverNamesAShiftedBlock()
    {
        OpenEditor();
        var editor = _world.Editor;
        var draft = editor.Draft!;
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(0, 2, 0), new GridSize(1, 1, 1), _snapshot.Content.Blocks.Get("hull")));
        Assert.Equal(DraftEdit.Done, draft.Add(new GridSize(0, 3, 0), new GridSize(1, 1, 1), _snapshot.Content.Blocks.Get("hull")));
        var boxes = draft.Boxes.ToArray();
        int first = -1, second = -1, other = -1;
        for (var i = 0; i < boxes.Length && first < 0; i++)
            for (var j = i + 1; j < boxes.Length && first < 0; j++)
                if (draft.CanMerge([i, j], out _) == DraftEdit.Done)
                    (first, second) = (i, j);
        Assert.True(first >= 0, "the pod has two blocks that make one box");
        for (var k = 0; k < boxes.Length && other < 0; k++)
            if (boxes[k].Type != boxes[first].Type)
                other = k;

        // Two types never merge, and the panel says so.
        editor.Select(first, false);
        editor.Select(other, true);
        editor.RefreshMerge();
        Assert.Equal(DraftEdit.MixedTypes, editor.MergeCheck);

        editor.Select(first, false);
        editor.Select(second, true);
        editor.RefreshMerge();
        Assert.Equal(DraftEdit.Done, editor.MergeCheck);
        var merged = editor.Merged;
        Assert.Equal(editor.MergeBefore.Hull, editor.MergeAfter.Hull, 1);
        editor.MergeSelected();
        Assert.Equal(boxes.Length - 1, DraftBlocks);
        Assert.Equal([first], editor.Selection.ToArray());
        Assert.Equal(merged, editor.Draft!.Boxes[first]);

        // Undo and redo each take the whole merge, and leave nothing selected rather than a block that moved.
        editor.Undo();
        Assert.Equal(boxes, editor.Draft!.Boxes.ToArray());
        Assert.Empty(editor.Selection.ToArray());
        editor.Redo();
        Assert.Equal(boxes.Length - 1, DraftBlocks);
        Assert.Empty(editor.Selection.ToArray());

        // A selection past the end after a delete of the last block is never kept.
        editor.Select(DraftBlocks - 1, false);
        editor.DeleteSelected();
        Assert.Empty(editor.Selection.ToArray());
    }

    [Fact]
    public void FocusFitsTheSelectionOrTheWholeDraftKeepsTheAngleAndIsLeftToATextField()
    {
        OpenEditor();
        var editor = _world.Editor;
        _paletteShown = true;
        for (var i = 0; i < 40; i++)
            EditorFrame();
        var source = editor.Source!;
        Vector3 Direction() => Vector3.Normalize(Vector3.Transform(
            _world.CameraPosition.RelativeTo(_world.OwnDrawnPosition), Quaternion.Conjugate(_world.OwnOrientation)) - _world.EditorFocus);

        // One block selected: the camera glides to its middle and close enough to fill the view with it.
        EditorFrame(() => Move(HullOnScreen()));
        var block = editor.Hover.Block;
        EditorFrame(() => Click(GlfwMouseButton.Middle));
        var angle = Direction();
        var whole = _world.EditorDistance;
        Chord(GlfwKey.F);
        for (var i = 0; i < 90; i++)
            EditorFrame();
        var box = editor.Draft!.Boxes[block];
        Assert.True((_world.EditorFocus - EditorGeometry.ToHull(EditorGeometry.Middle(box), source.Pivot, source.BlockSize)).Length() < 0.05f);
        Assert.True(Vector3.Distance(angle, Direction()) < 1e-3f, "the orbit's angle is kept");
        var near = _world.EditorDistance;
        Assert.True(near < whole, $"{near} against {whole}");

        // Nothing selected: the whole draft, from further away.
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() => Click(GlfwMouseButton.Middle));
        Assert.Empty(editor.Selection.ToArray());
        Chord(GlfwKey.F);
        for (var i = 0; i < 90; i++)
            EditorFrame();
        Assert.True(EditorGeometry.Bounds(editor.Draft!.Boxes, default, out var low, out var high));
        Assert.True((_world.EditorFocus - EditorGeometry.ToHull((low + high) * 0.5f, source.Pivot, source.BlockSize)).Length() < 0.05f);
        Assert.True(_world.EditorDistance > near);

        // Typing in the part search: F is a letter there, not a focus.
        var focus = _world.EditorFocus;
        EditorFrame(() => Move(OverSearch));
        EditorFrame(() => Click());
        EditorFrame();
        Assert.True(ImGui.GetIO().WantTextInput);
        EditorFrame(() => Move(HullOnScreen()));
        EditorFrame(() => Click(GlfwMouseButton.Middle));
        Chord(GlfwKey.F);
        for (var i = 0; i < 30; i++)
            EditorFrame();
        Assert.Equal(focus, _world.EditorFocus);
        Assert.True(editor.Shown);
    }

    /// <summary>The server's hull store, to wear a hull down as damage would without the stamp of a hit.</summary>
    private HullStore ServerHulls => (HullStore)typeof(ServerHost).GetField("_hulls", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_server)!;

    [Fact]
    public void DentOnlyShipRequiresRepairBeforeApplyingADraft()
    {
        var own = _snapshot.Hulls[_snapshot.OwnHull];
        var design = _snapshot.Content.Designs[own.Design];
        var index = (int)(own.Id & 0xFFFF);
        ServerHulls.Info(index).Visual.Hit(design, 0, design.BlockHitPoints(0) * 0.2f,
            design.BlockOffsets[0], Vector3.UnitX, _tick);
        Assert.True(PlayUntil(() => _snapshot.Hulls[_snapshot.OwnHull].Visual.Count > 0));
        OpenEditor();
        for (var i = 0; i < 30; i++) EditorFrame();
        var editor = _world.Editor;
        Assert.Equal(0, editor.MissingCount);
        Assert.True(editor.Damaged);
        Assert.True(editor.RepairOnly);
        Assert.False(editor.CanEdit);
        Assert.False(editor.Apply(Bridge));
        Assert.Equal(RepairBlock.None, editor.RepairState);
        Assert.True(editor.Repair(Bridge));
        Assert.True(PlayUntil(() => _snapshot.Hulls[_snapshot.OwnHull].Id != own.Id));
        Assert.Equal(0, _snapshot.Hulls[_snapshot.OwnHull].Visual.Count);
        OpenEditor();
        for (var i = 0; i < 30; i++) EditorFrame();
        Assert.False(editor.Damaged);
        Assert.True(editor.CanEdit);
    }

    [Fact]
    public void ADamagedShipOpensRepairOnlyAndRepairThroughTheServerRebuildsItThenItCanBeEdited()
    {
        var hulls = ServerHulls;
        ref var own = ref _snapshot.Hulls[_snapshot.OwnHull];
        var old = own.Id;
        var designIndex = own.Design;
        var design = _snapshot.Content.Designs[designIndex];
        var index = (int)(old & 0xFFFF);
        var lost = 0;
        while (design.Boxes[lost].Type.Core)
            lost++;
        var state = hulls.Info(index).BlockState;
        Assert.True(hulls.BlockStates.Lose(state, lost));
        hulls.State(index).Hull = (float)hulls.BlockStates.HitTotal(state);
        Assert.True(PlayUntil(() => _snapshot.Hulls[_snapshot.OwnHull].Revision > 0));

        // The draft is the whole blueprint; the lost block is in it and marked, and the hull reads against the blueprint.
        OpenEditor();
        var editor = _world.Editor;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        Assert.Equal(design.BlockCount, DraftBlocks);
        Assert.Equal(1, editor.MissingCount);
        Assert.True(editor.IsMissing(lost));
        Assert.True(editor.Damaged);
        Assert.True(editor.OwnHullPoints < design.Flight.HullPoints);
        Assert.Equal(RepairBlock.None, editor.RepairState);
        Assert.True(editor.RepairOnly);
        Assert.False(editor.CanEdit);
        Assert.False(editor.CanApply);

        // Repair only: the pointer finds blocks and a click selects one, with no ghost and nothing placed.
        var version = editor.Draft!.Version;
        EditorFrame(() => Move(HullOnScreen()));
        Assert.True(editor.HasHover);
        Assert.False(editor.HasGhost);
        EditorFrame(() => Click());
        Assert.Equal(design.BlockCount, DraftBlocks);
        Assert.NotEmpty(editor.Selection.ToArray());

        // No key, gesture or call changes the draft: delete, paste, undo, a sizing key, R, a drag, every edit.
        Chord(GlfwKey.LeftControl, GlfwKey.C);
        Chord(GlfwKey.LeftControl, GlfwKey.V);
        Chord(GlfwKey.Delete);
        Chord(GlfwKey.LeftControl, GlfwKey.Z);
        var size = editor.PartSize;
        EditorFrame(() => Key(GlfwKey.S, true));
        EditorFrame(() => Move(HullOnScreen() + new float2(200, 0)));
        Assert.False(editor.SizingPart);
        Assert.Equal(-1, _world.PartSizingFace);
        EditorFrame(() => Key(GlfwKey.S, false));
        Assert.Equal(size, editor.PartSize);
        Chord(GlfwKey.R);
        Assert.False(editor.RotatingPart);
        EditorFrame(() => Move(HullOnScreen()));
        var selected = editor.Selected;
        editor.DeleteSelected();
        editor.MergeSelected();
        editor.ResizeSelected(editor.SelectedLength + 1);
        editor.RotateSelected(0);
        editor.Undo();
        editor.Click();
        Assert.False(editor.BeginMove(selected));
        Assert.False(editor.BeginPaste());
        editor.BeginPartDrag(0);
        Assert.Equal(EditorCarry.None, editor.Carry);
        Assert.False(editor.BeginPartRotation());
        Assert.Equal(version, editor.Draft!.Version);
        Assert.False(editor.Dirty);

        // Repair: the server rebuilds the ship as built under the same design, and the editor closes as for an apply.
        Assert.True(editor.Repair(Bridge));
        Assert.Equal(EditorStage.Applying, editor.Stage);
        Assert.True(PlayUntil(() => editor.Stage == EditorStage.Closed), $"{_snapshot.Editor.Phase}/{_snapshot.Editor.Reason}");
        Assert.True(editor.NoticeApplied, $"{editor.Notice}");
        Assert.True(PlayUntil(() => _snapshot.Hulls[_snapshot.OwnHull].Id != old));
        own = ref _snapshot.Hulls[_snapshot.OwnHull];
        Assert.Equal(designIndex, own.Design);
        Assert.True(BlockSet.IsFull(own.Blocks.Span, design.BlockCount));
        Assert.Equal(design.Flight.HullPoints, own.Hull);
        Assert.False(design.NeedsRepair(own.Revision, own.Hull));

        // Whole again, the ship is edited as usual.
        Tick(3);
        OpenEditor();
        for (var i = 0; i < 30; i++)
            EditorFrame();
        Assert.True(editor.CanEdit);
        Assert.False(editor.RepairOnly);
        EditorFrame(() => Move(HullOnScreen()));
        Assert.True(editor.HasGhost);
        EditorFrame(() => Click());
        Assert.Equal(design.BlockCount + 1, DraftBlocks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangesKeptFromBeforeTheDamageHoldRepairBackUntilTheyAreDiscarded(bool loseBlock)
    {
        OpenEditor();
        var editor = _world.Editor;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        var blocks = DraftBlocks;
        EditorFrame(() => Move(HullOnScreen()));
        EditorFrame(() => Click());
        Assert.Equal(blocks + 1, DraftBlocks);

        // Both worn and missing blocks end the edit without discarding the player's draft.
        var hulls = ServerHulls;
        var own = (int)(_snapshot.Hulls[_snapshot.OwnHull].Id & 0xFFFF);
        if (loseBlock)
        {
            var design = _snapshot.Content.Designs[_snapshot.Hulls[_snapshot.OwnHull].Design];
            var lost = 0;
            while (design.Boxes[lost].Type.Core) lost++;
            var state = hulls.Info(own).BlockState;
            Assert.True(hulls.BlockStates.Lose(state, lost));
            hulls.State(own).Hull = (float)hulls.BlockStates.HitTotal(state);
        }
        else
            hulls.State(own).Hull -= 5f;
        hulls.Info(own).DamageTick = _tick;
        Assert.True(PlayUntil(() => editor.Stage == EditorStage.Closed));
        Assert.True(editor.DraftKept);
        hulls.Info(own).DamageTick = long.MinValue;

        // Opened again on the damaged ship: the kept draft is there, Repair waits and nothing can be applied.
        OpenEditor();
        for (var i = 0; i < 30; i++)
            EditorFrame();
        Assert.Equal(blocks + 1, DraftBlocks);
        Assert.True(editor.Dirty);
        Assert.True(editor.RepairOnly);
        Assert.Equal(loseBlock ? 1 : 0, editor.MissingCount);
        Assert.Equal(RepairBlock.Unsaved, editor.RepairState);
        Assert.False(editor.Repair(Bridge));
        Assert.False(editor.CanApply);
        Assert.False(editor.Apply(Bridge));
        Assert.Equal(EditorStage.Editing, editor.Stage);

        // Only the player's word throws the changes away; then Repair goes through.
        Assert.True(editor.DiscardChanges());
        Assert.False(editor.Dirty);
        Assert.Equal(blocks, DraftBlocks);
        Assert.False(editor.Draft!.CanUndo);
        Assert.Equal(RepairBlock.None, editor.RepairState);
        Assert.True(editor.Repair(Bridge));
        Assert.True(PlayUntil(() => editor.Stage == EditorStage.Closed), $"{_snapshot.Editor.Phase}/{_snapshot.Editor.Reason}");
        Assert.True(editor.NoticeApplied, $"{editor.Notice}");
    }

    /// <summary>
    /// Where the middle of side <paramref name="side"/> of the ghost shows on screen, and how far one cell out of
    /// that side moves on screen; false when either is behind the camera.
    /// </summary>
    private bool GhostFaceOnScreen(int side, out float2 at, out float2 cell)
    {
        var editor = _world.Editor;
        var size = editor.PartSize;
        var normal = EditorGeometry.Normal(side);
        var face = GhostMotion.Middle(editor.GhostAt, size) + normal * (EditorGeometry.Of(size, side >> 1) * 0.5f);
        cell = default;
        if (!_world.Project(_world.EditorPoint(face), out at) || !_world.Project(_world.EditorPoint(face + normal), out var next))
            return false;
        cell = next - at;
        return true;
    }

    /// <summary>
    /// A side of the ghost, plus or minus as asked and not the one against the hull, whose middle the pointer
    /// highlights, its axis well across the view; the pointer is left on it. While none shows, the camera is orbited
    /// with the right button, as a player would, a little at a time.
    /// </summary>
    private int SizableFace(bool plus, out float2 at, out float2 cell)
    {
        var editor = _world.Editor;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            for (var side = plus ? 0 : 1; side < 6; side += 2)
            {
                if (side == (editor.Hover.Side ^ 1) || !GhostFaceOnScreen(side, out at, out cell) || cell.Length() < 12f)
                    continue;
                var point = at;
                EditorFrame(() => Move(point));
                if (_world.PartSizingFace == side && !_world.PartSizingEdgeOn)
                    return side;
            }

            var from = new float2(640, 360);
            EditorFrame(() => Move(from));
            EditorFrame(() => Button(GlfwMouseButton.Right, true));
            EditorFrame(() => Move(from + new float2(40, attempt % 3 == 2 ? 30 : 0)));
            EditorFrame(() => Move(from + new float2(80, attempt % 3 == 2 ? 60 : 0)));
            EditorFrame(() => Button(GlfwMouseButton.Right, false));
            Assert.True(editor.SizingPart);
        }

        at = cell = default;
        Assert.Fail($"no {(plus ? "plus" : "minus")} face of the ghost to size on screen");
        return -1;
    }

    /// <summary>Presses on a face of the ghost and moves the pointer <paramref name="cells"/> cells out of it in four steps.</summary>
    private void DragFace(float2 at, float2 cell, float cells, bool release = true)
    {
        EditorFrame(() => Move(at));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        for (var i = 1; i <= 4; i++)
        {
            var point = at + cell * (cells * i / 4f);
            EditorFrame(() => Move(point));
        }

        if (release)
            EditorFrame(() => Button(GlfwMouseButton.Left, false));
    }

    [Fact]
    public void HoldingSAndDraggingAGhostFaceSizesItWithTheOppositeFaceFixedThenANewClickPlacesIt()
    {
        OpenEditor();
        var editor = _world.Editor;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        var blocks = DraftBlocks;
        var limit = editor.Content!.Blocks.Limits.MaxBoxSide;
        EditorFrame(() => Move(HullOnScreen()));
        Assert.True(editor.HasGhost);
        var face = (editor.Hover.Block, editor.Hover.Side);
        Vector3 Camera() => _world.CameraPosition.RelativeTo(_world.OwnDrawnPosition);

        // S holds the ghost still wherever the pointer goes; nothing is highlighted off the ghost.
        var held = (editor.GhostAt, editor.PartSize);
        EditorFrame(() => Key(GlfwKey.S, true));
        Assert.True(editor.SizingPart);
        EditorFrame(() => Move(EmptySpace));
        Assert.Equal(held, (editor.GhostAt, editor.PartSize));
        Assert.Equal(-1, _world.PartSizingFace);

        // A plus face moves out two cells and back one; the low corner stays.
        void Plus(int grow)
        {
            var side = SizableFace(true, out var at, out var cell);
            var axis = side >> 1;
            var (ghostAt, size) = (editor.GhostAt, editor.PartSize);
            DragFace(at, cell, grow + 0.2f * Math.Sign(grow));
            Assert.Equal(EditorGeometry.With(size, axis, EditorGeometry.Of(size, axis) + grow), editor.PartSize);
            Assert.Equal(ghostAt, editor.GhostAt);
        }

        // A minus face moves out too, and the ghost's high face stays where it was in the grid.
        void Minus(int grow)
        {
            var side = SizableFace(false, out var at, out var cell);
            var axis = side >> 1;
            var (ghostAt, size) = (editor.GhostAt, editor.PartSize);
            var high = EditorGeometry.Of(ghostAt, axis) + EditorGeometry.Of(size, axis);
            DragFace(at, cell, grow + 0.2f * Math.Sign(grow));
            Assert.Equal(EditorGeometry.Of(size, axis) + grow, EditorGeometry.Of(editor.PartSize, axis));
            Assert.Equal(high, EditorGeometry.Of(editor.GhostAt, axis) + EditorGeometry.Of(editor.PartSize, axis));
            for (var other = 0; other < 3; other++)
                if (other != axis)
                    Assert.Equal(EditorGeometry.Of(ghostAt, other), EditorGeometry.Of(editor.GhostAt, other));
        }

        Plus(2);
        Plus(-1);
        Minus(2);
        Minus(-1);
        Assert.Equal(blocks, DraftBlocks);
        Assert.Equal(face, (editor.Hover.Block, editor.Hover.Side));

        // A right drag orbits the camera through the gesture: the ghost, its size and placing all stay, and a
        // right click alone changes nothing either.
        var camera = Camera();
        var sized = (editor.GhostAt, editor.PartSize);
        EditorFrame(() => Move(EmptySpace + new float2(400, -300)));
        EditorFrame(() => Button(GlfwMouseButton.Right, true));
        EditorFrame(() => Move(EmptySpace + new float2(520, -330)));
        EditorFrame(() => Move(EmptySpace + new float2(640, -360)));
        EditorFrame(() => Button(GlfwMouseButton.Right, false));
        EditorFrame(() => Click(GlfwMouseButton.Right));
        Assert.True((Camera() - camera).Length() > 0.5f, "the camera should have orbited");
        Assert.True(editor.SizingPart);
        Assert.True(editor.Placing);
        Assert.Equal(sized, (editor.GhostAt, editor.PartSize));

        // From the turned camera the faces still move along their own axes, and stop at the limits: pushed past
        // one, turning back answers at once.
        Plus(1);
        Minus(1);
        var side = SizableFace(true, out var at, out var cell);
        var axis = side >> 1;
        DragFace(at, cell, limit + 5, release: false);
        Assert.Equal(limit, EditorGeometry.Of(editor.PartSize, axis));
        var far = at + cell * (limit + 5);
        EditorFrame(() => Move(far - cell * 1.2f));
        Assert.Equal(limit - 1, EditorGeometry.Of(editor.PartSize, axis));
        EditorFrame(() => Move(far - cell * (limit * 3)));
        Assert.Equal(1, EditorGeometry.Of(editor.PartSize, axis));
        EditorFrame(() => Move(far - cell * (limit * 3 - 1.2f)));
        Assert.Equal(2, EditorGeometry.Of(editor.PartSize, axis));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));

        // A right drag in the middle of a face drag holds the face still, and the drag then goes on from wherever
        // the pointer is let go, along the axis as the new view shows it.
        side = SizableFace(true, out at, out cell);
        axis = side >> 1;
        var length = EditorGeometry.Of(editor.PartSize, axis);
        DragFace(at, cell, 1.2f, release: false);
        Assert.Equal(length + 1, EditorGeometry.Of(editor.PartSize, axis));
        var pointer = at + cell * 1.2f;
        EditorFrame(() => Button(GlfwMouseButton.Right, true));
        EditorFrame(() => Move(pointer + new float2(-90, 60)));
        EditorFrame(() => Move(pointer + new float2(-180, 120)));
        Assert.Equal(length + 1, EditorGeometry.Of(editor.PartSize, axis));
        EditorFrame(() => Button(GlfwMouseButton.Right, false));
        EditorFrame();
        Assert.Equal(length + 1, EditorGeometry.Of(editor.PartSize, axis));
        Assert.Equal(side, _world.PartSizingFace);
        Assert.True(GhostFaceOnScreen(side, out _, out var turned));
        pointer += new float2(-180, 120);
        EditorFrame(() => Move(pointer + turned * 1.2f));
        Assert.Equal(length + 2, EditorGeometry.Of(editor.PartSize, axis));

        // Letting go of S with the button still held ends sizing; letting go of the button then places nothing.
        EditorFrame(() => Key(GlfwKey.S, false));
        Assert.False(editor.SizingPart);
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(blocks, DraftBlocks);

        // The size and place stay while the pointer is still, and a new click places exactly that.
        var ghost = editor.GhostAt;
        var final = editor.PartSize;
        EditorFrame();
        Assert.True(editor.HasGhost);
        Assert.Equal((ghost, final), (editor.GhostAt, editor.PartSize));
        if (editor.GhostCheck == DraftEdit.Done)
        {
            EditorFrame(() => Click());
            Assert.Equal(blocks + 1, DraftBlocks);
            Assert.Equal(new DesignBox(ghost, final, editor.Content.Blocks.Types[editor.Part], BlockShape.Box), editor.Draft!.Boxes[^1]);
        }
        else
        {
            // Where it does not fit nothing is placed, and the reason shows as it would for any ghost.
            EditorFrame(() => Click());
            Assert.Equal(blocks, DraftBlocks);
        }
    }

    [Fact]
    public void AGhostSizedOnItsFaceStaysForTheClickWhileThePointerIsOnItAndAWedgeStaysAWedge()
    {
        OpenEditor();
        var editor = _world.Editor;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        var blocks = DraftBlocks;
        editor.SetPartShape(BlockShape.WedgeXPlusPlus);
        EditorFrame(() => Move(HullOnScreen()));
        Assert.True(editor.HasGhost);

        // A wedge's faces size it as a box's do, the slant as the box side it stands for; it stays the same wedge.
        EditorFrame(() => Key(GlfwKey.S, true));
        var middle = GhostMotion.Middle(editor.GhostAt, editor.PartSize);
        Assert.True(_world.Project(_world.EditorPoint(middle), out var inside));
        EditorFrame(() => Move(inside));
        var read = _world.PartSizingFace;
        var ghost = new DesignBox(editor.GhostAt, editor.PartSize, null!, editor.PartShape);
        Assert.True(read >= 0, "the middle of the ghost should be on one of its faces");
        Assert.True(BlockShapes.Face(editor.PartShape, read, stackalloc int[4]) > 0 || read == EditorGeometry.SlantEntry(ghost),
            "a side with no face of its own is read only through the slant");
        var side = SizableFace(true, out var at, out var cell);
        var size = editor.PartSize;
        DragFace(at, cell, 2.2f);
        Assert.Equal(EditorGeometry.Of(size, side >> 1) + 2, EditorGeometry.Of(editor.PartSize, side >> 1));
        Assert.Equal(BlockShape.WedgeXPlusPlus, editor.PartShape);

        // The pointer is still on the sized face when S is let go: moving it over the ghost keeps the ghost where
        // it is, and a click places it there, as sized.
        var held = (editor.GhostAt, editor.PartSize);
        Assert.True(GhostFaceOnScreen(side, out var on, out _));
        EditorFrame(() => Move(on));
        EditorFrame(() => Key(GlfwKey.S, false));
        Assert.False(editor.SizingPart);
        EditorFrame(() => Move(on + new float2(1, 1)));
        Assert.True(editor.GhostPinned);
        Assert.Equal(held, (editor.GhostAt, editor.PartSize));
        Assert.Equal(DraftEdit.Done, editor.GhostCheck);
        EditorFrame(() => Click());
        Assert.Equal(blocks + 1, DraftBlocks);
        Assert.Equal(new DesignBox(held.GhostAt, held.PartSize, editor.Content!.Blocks.Types[editor.Part], BlockShape.WedgeXPlusPlus), editor.Draft!.Boxes[^1]);
        blocks++;

        // Off the ghost and back on the ship, placing follows the pointer again, at the size it was given.
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() => Move(HullOnScreen()));
        Assert.False(editor.GhostPinned);
        Assert.Equal(held.PartSize, editor.PartSize);
        Assert.Equal(blocks, DraftBlocks);
    }

    [Fact]
    public void SIsLeftToTextFieldsPanelsAndCtrlAndEscapeOrClosingPutsTheGestureBackWithNothingLeftForFlight()
    {
        OpenEditor();
        var editor = _world.Editor;
        _paletteShown = true;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        var size = editor.PartSize;

        // Over a panel S starts nothing.
        EditorFrame(() => Move(OverButton));
        EditorFrame();
        EditorFrame(() => Key(GlfwKey.S, true));
        Assert.False(editor.SizingPart);
        EditorFrame(() => Key(GlfwKey.S, false));

        // With Ctrl held, nor does it.
        EditorFrame(() => Move(HullOnScreen()));
        Assert.True(editor.HasGhost);
        EditorFrame(() => Key(GlfwKey.LeftControl, true));
        EditorFrame(() => Key(GlfwKey.S, true));
        Assert.False(editor.SizingPart);
        EditorFrame(() => Key(GlfwKey.S, false));
        EditorFrame(() => Key(GlfwKey.LeftControl, false));

        // A text field with the keyboard keeps its letters.
        EditorFrame(() => Move(OverSearch));
        EditorFrame(() => Click());
        EditorFrame();
        Assert.True(ImGui.GetIO().WantTextInput);
        EditorFrame(() => Move(HullOnScreen()));
        EditorFrame(() => Key(GlfwKey.S, true));
        Assert.False(editor.SizingPart);
        Assert.Equal(size, editor.PartSize);
        EditorFrame(() => Key(GlfwKey.S, false));
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() => Click());
        EditorFrame();
        Assert.False(ImGui.GetIO().WantTextInput);
        _paletteShown = false;

        // Escape in the middle of a face drag puts back the size and place from before S and leaves the edit open.
        EditorFrame(() => Move(HullOnScreen()));
        var before = (editor.GhostAt, editor.PartSize);
        EditorFrame(() => Key(GlfwKey.S, true));
        Assert.True(editor.SizingPart);
        var side = SizableFace(false, out var at, out var cell);
        DragFace(at, cell, 2.2f, release: false);
        Assert.NotEqual(before, (editor.GhostAt, editor.PartSize));
        EditorFrame(() => Key(GlfwKey.Escape, true));
        Assert.False(editor.SizingPart);
        Assert.Equal(EditorStage.Editing, editor.Stage);
        Assert.Equal(before, (editor.GhostAt, editor.PartSize));
        EditorFrame(() => Key(GlfwKey.Escape, false));
        Assert.Equal(-1, _world.PartSizingFace);
        // Neither the button let go nor S still held does anything more.
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        EditorFrame(() => Move(at + cell * 3f));
        Assert.False(editor.SizingPart);
        Assert.Equal(before.PartSize, editor.PartSize);

        // Escape again, with S still held, ends the edit: S does not fly until it is let go.
        EditorFrame(() => Key(GlfwKey.Escape, true));
        Assert.Equal(EditorStage.Closed, editor.Stage);
        EditorFrame(() => Key(GlfwKey.Escape, false));
        Assert.True(_world.FlightHeld);
        EditorFrame(() => Move(EmptySpace + new float2(0, -100)));
        Assert.Equal(0f, Sent().Forward);
        EditorFrame(() => Key(GlfwKey.S, false));
        EditorFrame(() => Key(GlfwKey.S, true));
        Assert.True(Sent().Forward < 0);
        EditorFrame(() => Key(GlfwKey.S, false));

        // The editor key closes the edit mid-gesture; the next edit starts with nothing held.
        Tick(3);
        OpenEditor();
        for (var i = 0; i < 30; i++)
            EditorFrame();
        EditorFrame(() => Move(HullOnScreen()));
        EditorFrame(() => Key(GlfwKey.S, true));
        Assert.True(editor.SizingPart);
        Chord(GlfwKey.B);
        Assert.Equal(EditorStage.Closed, editor.Stage);
        Assert.False(editor.SizingPart);
        Assert.False(editor.GhostPinned);
        EditorFrame(() => Key(GlfwKey.S, false));
        Tick(3);
        OpenEditor();
        Assert.False(editor.SizingPart);
        Assert.Equal(-1, _world.PartSizingFace);
        _ = side;
    }

    /// <summary>
    /// A point on screen on the rotation ring about <paramref name="axis"/>, as far from the other ring as any:
    /// where a press takes that ring and no other.
    /// </summary>
    private float2 RingAt(int axis)
    {
        Assert.True(_world.PartRings(out var middle, out var radius));
        var best = default(float2);
        var room = -1f;
        for (var i = 0; i < WorldView.RingSegments; i++)
        {
            if (!_world.Project(_world.EditorPoint(WorldView.RingPoint(middle, radius, axis, i)), out var point))
                continue;
            var nearest = float.MaxValue;
            for (var j = 0; j < WorldView.RingSegments; j++)
                if (_world.Project(_world.EditorPoint(WorldView.RingPoint(middle, radius, 1 - axis, j)), out var other))
                    nearest = Math.Min(nearest, (other - point).Length());
            if (nearest > room && point.X > 0 && point.Y > 0 && point.X < Screen.X && point.Y < Screen.Y)
            {
                room = nearest;
                best = point;
            }
        }

        Assert.True(room > 24f, $"the rings should be apart on screen, {room}");
        return best;
    }

    /// <summary>Opens the editor with a wedge's ghost on a face of the ship, ready to be turned.</summary>
    private ShipEditor WedgeOnAFace()
    {
        OpenEditor();
        var editor = _world.Editor;
        for (var i = 0; i < 30; i++)
            EditorFrame();
        editor.SetPartShape(BlockShape.WedgeXPlusPlus);
        editor.SetPartSize(0, 2);
        EditorFrame(() => Move(HullOnScreen()));
        Assert.True(editor.HasGhost);
        return editor;
    }

    [Fact]
    public void RTurnsTheGhostWithTwoRingsThroughEveryWedgeOrientationAndAClickOnTheGhostPlacesItUnconfirmed()
    {
        var editor = WedgeOnAFace();
        var blocks = DraftBlocks;
        var face = (editor.Hover.Block, editor.Hover.Side);
        var before = (editor.PartSize, editor.PartShape);

        Chord(GlfwKey.R);
        Assert.True(editor.RotatingPart);

        // Each ring by the wheel and by a click turns a quarter about its own axis, never placing, and the ghost
        // keeps its face. A walk over both rings reaches all twelve wedges.
        var seen = new HashSet<BlockShape> { editor.PartShape };
        void Turn(int axis, bool wheel)
        {
            var expected = BlockShapes.Rotate(editor.PartShape, axis);
            var size = editor.PartSize;
            var swapped = axis == 0 ? new GridSize(size.X, size.Z, size.Y) : new GridSize(size.Z, size.Y, size.X);
            var at = RingAt(axis);
            EditorFrame(() => Move(at));
            Assert.Equal(axis, _world.RotationRingHovered);
            EditorFrame(() =>
            {
                if (wheel)
                    Wheel(1);
                else
                    Click();
            });
            Assert.Equal((swapped, expected), (editor.PartSize, editor.PartShape));
            Assert.Equal(face, (editor.Hover.Block, editor.Hover.Side));
            Assert.True(editor.RotatingPart);
            seen.Add(expected);
        }

        // The walk goes to the nearest wedge not yet seen, a quarter turn of either ring at a time, until it has seen
        // all twelve; each turn is made on screen, the wheel and a click by turns.
        var turns = 0;
        while (seen.Count < 12 && turns < 60)
        {
            var from = new Dictionary<BlockShape, (BlockShape Shape, int Axis)>();
            var queue = new Queue<BlockShape>([editor.PartShape]);
            var target = editor.PartShape;
            while (queue.Count > 0 && seen.Contains(target))
            {
                var node = queue.Dequeue();
                for (var axis = 0; axis < 2; axis++)
                {
                    var next = BlockShapes.Rotate(node, axis);
                    if (next == editor.PartShape || from.ContainsKey(next))
                        continue;
                    from[next] = (node, axis);
                    queue.Enqueue(next);
                    if (!seen.Contains(next) && seen.Contains(target))
                        target = next;
                }
            }

            Assert.False(seen.Contains(target), "every wedge should be reachable with the two rings");
            var path = new Stack<int>();
            for (var node = target; node != editor.PartShape; node = from[node].Shape)
                path.Push(from[node].Axis);
            foreach (var axis in path)
                Turn(axis, turns++ % 2 == 0);
        }

        Assert.Equal(12, seen.Count);
        Assert.Equal(blocks, DraftBlocks);

        // The wheel the other way and Shift with a click turn back.
        var shape = editor.PartShape;
        var at = RingAt(0);
        EditorFrame(() => Move(at));
        EditorFrame(() => Wheel(1));
        EditorFrame(() => Wheel(-1));
        Assert.Equal(shape, editor.PartShape);
        at = RingAt(1);
        EditorFrame(() => Move(at));
        EditorFrame(() => Click());
        EditorFrame(() =>
        {
            Key(GlfwKey.LeftShift, true);
            Click();
        });
        EditorFrame(() => Key(GlfwKey.LeftShift, false));
        Assert.Equal(shape, editor.PartShape);
        EditorFrame(() => Click());
        var turned = (editor.PartSize, editor.PartShape);
        Assert.NotEqual(before, turned);

        // Nothing confirms the turn: one click on the ghost places it as drawn, where it is drawn, once.
        Assert.True(editor.RotatingPart);
        Assert.Equal(blocks, DraftBlocks);
        PlaceTheTurnedGhost(blocks);
    }

    /// <summary>
    /// A point on screen inside the ghost of the part being turned, clear of both rings: the pointer is left there.
    /// </summary>
    private float2 OnTheGhost()
    {
        var editor = _world.Editor;
        var middle = GhostMotion.Middle(editor.GhostAt, editor.PartSize);
        var size = new Vector3(editor.PartSize.X, editor.PartSize.Y, editor.PartSize.Z);
        // The middle first, then toward each corner: a wedge's solid half holds some of them.
        for (var i = 0; i < 27; i++)
        {
            var k = (i + 13) % 27;
            var offset = new Vector3(k % 3 - 1, k / 3 % 3 - 1, k / 9 - 1) * 0.3f * size;
            if (!_world.Project(_world.EditorPoint(middle + offset), out var at))
                continue;
            EditorFrame(() => Move(at));
            if (_world.RotationRingHovered < 0)
                return at;
        }

        Assert.Fail("some point inside the ghost should be clear of the rings");
        return default;
    }

    /// <summary>
    /// With a part being turned and its ghost fitting: a press on the ghost ends the turn and places nothing yet,
    /// and its release places exactly the ghost that was drawn, once.
    /// </summary>
    private void PlaceTheTurnedGhost(int blocks)
    {
        var editor = _world.Editor;
        Assert.True(editor.RotatingPart);
        Assert.Equal(DraftEdit.Done, editor.GhostCheck);
        var drawn = new DesignBox(editor.GhostAt, editor.PartSize, editor.Content!.Blocks.Types[editor.Part], editor.PartShape);
        OnTheGhost();
        Assert.Equal(drawn.At, editor.GhostAt);
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        Assert.False(editor.RotatingPart);
        Assert.Equal(blocks, DraftBlocks);
        Assert.Equal((drawn.At, drawn.Size, drawn.Shape), (editor.GhostAt, editor.PartSize, editor.PartShape));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(blocks + 1, DraftBlocks);
        Assert.Equal(drawn, editor.Draft!.Boxes[^1]);
        EditorFrame();
        EditorFrame();
        Assert.Equal(blocks + 1, DraftBlocks);
    }

    [Fact]
    public void ATurnIsKeptByRAClickElsewhereOrAPanelAndPutBackByEscapeAnotherShapeAndClosing()
    {
        // As the game sets the interface up: keyboard navigation claims the keyboard for a focused window.
        ImGui.GetIO().ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.NavEnableGamepad;
        ImGui.GetIO().ConfigNavCursorVisibleAuto = true;
        var editor = WedgeOnAFace();
        _paletteShown = true;
        _palettePart = 1;
        EditorFrame();
        var blocks = DraftBlocks;
        var version = editor.Draft!.Version;
        var before = (editor.PartSize, editor.PartShape, editor.Part);
        Vector3 Camera() => _world.CameraPosition.RelativeTo(_world.OwnDrawnPosition);
        var hull = HullOnScreen();
        (GridSize, BlockShape, int) turned = default;
        void Turned()
        {
            EditorFrame(() => Move(hull));
            Assert.True(editor.HasGhost);
            Chord(GlfwKey.R);
            Assert.True(editor.RotatingPart);
            var at = RingAt(0);
            EditorFrame(() => Move(at));
            EditorFrame(() => Wheel(1));
            turned = (editor.PartSize, editor.PartShape, editor.Part);
            Assert.NotEqual(before, turned);
        }

        void Unchanged()
        {
            Assert.Equal(version, editor.Draft!.Version);
            Assert.Equal(blocks, DraftBlocks);
        }

        void Kept()
        {
            Assert.False(editor.RotatingPart);
            Assert.Equal(turned, (editor.PartSize, editor.PartShape, editor.Part));
            Unchanged();
        }

        void PutBack()
        {
            Assert.False(editor.RotatingPart);
            Assert.Equal(before, (editor.PartSize, editor.PartShape, editor.Part));
            Unchanged();
        }

        // R again keeps the turn, and the ghost stays where it was turned for the next click.
        Turned();
        var held = editor.GhostAt;
        Chord(GlfwKey.R);
        Kept();
        Assert.True(editor.GhostPinned);
        Assert.Equal(held, editor.GhostAt);
        editor.SetPartShape(before.PartShape);

        // Escape puts the turn back and leaves the edit open.
        Turned();
        Chord(GlfwKey.Escape);
        PutBack();
        Assert.Equal(EditorStage.Editing, editor.Stage);

        // A click elsewhere in the world keeps the turn, and the press reaches neither placing, selecting nor the
        // camera. The ghost then follows the pointer, and the next click places the turned part there.
        Turned();
        var camera = Camera();
        var selection = editor.Selection.Length;
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        Kept();
        EditorFrame(() => Move(hull));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        EditorFrame();
        Kept();
        Assert.Equal(selection, editor.Selection.Length);
        Assert.True((Camera() - camera).Length() < 0.01f);
        Assert.True(editor.HasGhost);
        Assert.False(editor.GhostPinned);
        if (editor.GhostCheck != DraftEdit.Done)
            Assert.True(PointWhere(e => e.HasGhost && e.GhostCheck == DraftEdit.Done, out _));
        var drawn = (editor.GhostAt, editor.PartSize, editor.PartShape);
        EditorFrame(() => Click());
        Assert.Equal(blocks + 1, DraftBlocks);
        Assert.Equal(drawn, (editor.Draft!.Boxes[^1].At, editor.Draft.Boxes[^1].Size, editor.Draft.Boxes[^1].Shape));
        editor.Undo();
        Assert.Equal(blocks, DraftBlocks);
        version = editor.Draft!.Version;
        editor.SetPartShape(before.PartShape);

        // A middle click elsewhere keeps it too, and still places; a right press only orbits (see the orbit test).
        Turned();
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() => Click(GlfwMouseButton.Middle));
        Kept();
        Assert.True(editor.Placing);
        editor.SetPartShape(before.PartShape);

        // A press on the sidebar keeps it; releasing over the hull must not place or select.
        Turned();
        selection = editor.Selection.Length;
        EditorFrame(() => Move(PaletteMin + new float2(110, 75)));
        EditorFrame();
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        Kept();
        EditorFrame(() => Move(hull));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        EditorFrame();
        Kept();
        Assert.Equal(selection, editor.Selection.Length);
        // That press left the panel focused; a click on the empty world gives the keyboard back.
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() => Click());
        EditorFrame();
        editor.SetPartShape(before.PartShape);

        // Another shape chosen in code, as nothing but the panel's buttons does: put back first, then the shape.
        Turned();
        editor.SetPartShape(BlockShape.Box);
        Assert.False(editor.RotatingPart);
        Assert.Equal((before.PartSize, BlockShape.Box), (editor.PartSize, editor.PartShape));
        editor.SetPartShape(before.PartShape);

        // The part list's shape button and another part's card: the press on the panel keeps the turn first, then
        // the choice is made on it, and never the choice put back.
        Turned();
        EditorFrame(() => Move(OverShapeBox));
        EditorFrame();
        EditorFrame(() => Click());
        EditorFrame();
        Assert.False(editor.RotatingPart);
        Assert.Equal((turned.Item1, BlockShape.Box), (editor.PartSize, editor.PartShape));
        editor.SetPartShape(before.PartShape);
        Turned();
        EditorFrame(() => Move(OverCard));
        EditorFrame();
        EditorFrame(() => Click());
        EditorFrame();
        Assert.False(editor.RotatingPart);
        Assert.Equal((turned.Item1, turned.Item2, 1), (editor.PartSize, editor.PartShape, editor.Part));
        Unchanged();
        editor.ChoosePart(before.Part);
        editor.SetPartShape(before.PartShape);
        _paletteShown = false;

        // The editor key closes the edit mid-turn; the next edit has no turn and no ring left over.
        Turned();
        Chord(GlfwKey.B);
        Assert.Equal(EditorStage.Closed, editor.Stage);
        Assert.False(editor.RotatingPart);
        Assert.Equal(-1, _world.RotationRingHovered);
        Tick(3);
        OpenEditor();
        Assert.False(editor.RotatingPart);
        Assert.Equal(before, (editor.PartSize, editor.PartShape, editor.Part));
    }

    /// <summary>
    /// The angle along the ring about <paramref name="axis"/> (as <see cref="WorldView.RingPointAt"/> takes it) of the
    /// point <see cref="RingAt"/> would press.
    /// </summary>
    private float RingAngleAt(int axis)
    {
        var at = RingAt(axis);
        Assert.True(_world.PartRings(out var middle, out var radius));
        var best = 0f;
        var nearest = float.MaxValue;
        for (var i = 0; i < WorldView.RingSegments; i++)
        {
            var angle = i * MathF.Tau / WorldView.RingSegments;
            if (_world.Project(_world.EditorPoint(WorldView.RingPointAt(middle, radius, axis, angle)), out var point) && (point - at).Length() < nearest)
            {
                nearest = (point - at).Length();
                best = angle;
            }
        }

        return best;
    }

    /// <summary>The ring about <paramref name="axis"/> at <paramref name="angle"/>, on screen as the camera is now.</summary>
    private float2 RingOnScreen(int axis, float angle)
    {
        Assert.True(_world.PartRings(out var middle, out var radius));
        Assert.True(_world.Project(_world.EditorPoint(WorldView.RingPointAt(middle, radius, axis, angle)), out var at));
        return at;
    }

    /// <summary>Moves the pointer along the ring about <paramref name="axis"/> from one angle to another, ten degrees a frame.</summary>
    private void AlongRing(int axis, float from, float to)
    {
        var steps = (int)MathF.Ceiling(MathF.Abs(to - from) / (MathF.PI / 18f));
        for (var i = 1; i <= steps; i++)
        {
            var point = RingOnScreen(axis, from + (to - from) * i / steps);
            EditorFrame(() => Move(point));
        }
    }

    private static float Degrees(float degrees) => degrees * MathF.PI / 180f;

    private static float TurnAngle(Quaternion turn) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(turn.W), 0f, 1f));

    [Fact]
    public void DraggingARingTurnsThePartAQuarterAtATimeAcrossTheWrapTheGhostGlidesAndLettingGoAddsNothing()
    {
        var editor = WedgeOnAFace();
        var blocks = DraftBlocks;
        var version = editor.Draft!.Version;
        Chord(GlfwKey.R);
        Assert.True(editor.RotatingPart);
        var start = (editor.PartSize, editor.PartShape);
        var once = (new GridSize(start.PartSize.X, start.PartSize.Z, start.PartSize.Y), BlockShapes.Rotate(start.PartShape, 0));

        // Press on the ring about x and go round it: short of half a quarter the part stays, the ghost follows.
        var a0 = RingAngleAt(0);
        EditorFrame(() => Move(RingOnScreen(0, a0)));
        Assert.Equal(0, _world.RotationRingHovered);
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        AlongRing(0, a0, a0 + Degrees(30));
        Assert.True(_world.RotationRingDragged);
        Assert.Equal(start, (editor.PartSize, editor.PartShape));
        _world.GhostPose(out var turn, out _);
        Assert.InRange(TurnAngle(turn), Degrees(5), Degrees(31));

        // Past half a quarter it is a whole quarter turn, sizes swapped; the ghost is drawn short of it, following.
        AlongRing(0, a0 + Degrees(30), a0 + Degrees(60));
        Assert.Equal(once, (editor.PartSize, editor.PartShape));
        _world.GhostPose(out turn, out var shift);
        Assert.True(float.IsFinite(turn.W) && float.IsFinite(shift.X) && float.IsFinite(shift.Y) && float.IsFinite(shift.Z));
        Assert.InRange(TurnAngle(turn), Degrees(1), Degrees(89));

        // All the way round, through the angle where it wraps, it is four quarter turns: as it was.
        AlongRing(0, a0 + Degrees(60), a0 + Degrees(370));
        Assert.Equal(start, (editor.PartSize, editor.PartShape));
        AlongRing(0, a0 + Degrees(370), a0 + Degrees(420));
        Assert.Equal(once, (editor.PartSize, editor.PartShape));

        // Back the other way past where it started: a quarter turn back.
        AlongRing(0, a0 + Degrees(420), a0 - Degrees(60));
        var back = (new GridSize(start.PartSize.X, start.PartSize.Z, start.PartSize.Y), BlockShapes.Rotate(BlockShapes.Rotate(BlockShapes.Rotate(start.PartShape, 0), 0), 0));
        Assert.Equal(back, (editor.PartSize, editor.PartShape));

        // Let go: no click's turn, nothing placed or selected, still turning; the ghost glides onto the quarter turn
        // and ends exactly on it.
        var selection = editor.Selection.Length;
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(back, (editor.PartSize, editor.PartShape));
        Assert.True(editor.RotatingPart);
        Assert.False(_world.RotationRingDragged);
        for (var i = 0; i < 30; i++)
            EditorFrame();
        _world.GhostPose(out turn, out shift);
        Assert.Equal(Quaternion.Identity, turn);
        Assert.Equal(Vector3.Zero, shift);
        Assert.Equal(blocks, DraftBlocks);
        Assert.Equal(version, editor.Draft!.Version);
        Assert.Equal(selection, editor.Selection.Length);

        // A click on a ring, then at once a click on the ghost while it still glides: the ring's click turns and
        // places nothing; the ghost's places the part at its quarter turn, where its ghost is.
        var at = RingAt(1);
        EditorFrame(() => Move(at));
        EditorFrame(() => Click());
        var turned = (editor.PartSize, editor.PartShape);
        Assert.NotEqual(back, turned);
        Assert.True(editor.RotatingPart);
        Assert.Equal(blocks, DraftBlocks);
        _world.GhostPose(out turn, out _);
        Assert.NotEqual(Quaternion.Identity, turn);
        PlaceTheTurnedGhost(blocks);
        Assert.Equal(turned, (editor.Draft!.Boxes[^1].Size, editor.Draft.Boxes[^1].Shape));
    }

    [Fact]
    public void RThenARingDragAndOneClickOnTheGhostPlacesTheTurnedPartWithNothingToConfirm()
    {
        var editor = WedgeOnAFace();
        var blocks = DraftBlocks;
        Chord(GlfwKey.R);
        var start = editor.PartShape;

        // Drag the ring a quarter round and let go: turned, still turning, nothing placed.
        var a0 = RingAngleAt(0);
        EditorFrame(() => Move(RingOnScreen(0, a0)));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        AlongRing(0, a0, a0 + Degrees(90));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(BlockShapes.Rotate(start, 0), editor.PartShape);
        Assert.True(editor.RotatingPart);
        Assert.Equal(blocks, DraftBlocks);

        // The very next ordinary click, on the ghost, places it: no confirming click and no click used up.
        PlaceTheTurnedGhost(blocks);
        Assert.Equal(BlockShapes.Rotate(start, 0), editor.Draft!.Boxes[^1].Shape);

        // The part keeps its turn for the next placement, and the next click on the ship places it again.
        Assert.Equal(BlockShapes.Rotate(start, 0), editor.PartShape);
        Assert.True(PointWhere(e => e.HasGhost && e.GhostCheck == DraftEdit.Done && !e.GhostPinned, out _));
        var drawn = (editor.GhostAt, editor.PartSize, editor.PartShape);
        EditorFrame(() => Click());
        Assert.Equal(blocks + 2, DraftBlocks);
        Assert.Equal(drawn, (editor.Draft!.Boxes[^1].At, editor.Draft.Boxes[^1].Size, editor.Draft.Boxes[^1].Shape));
    }

    [Fact]
    public void TheRightButtonOrbitsThroughATurnAndARingDragWithoutPuttingItBackOrTurningThePart()
    {
        var editor = WedgeOnAFace();
        _paletteShown = true;
        EditorFrame();
        var blocks = DraftBlocks;
        Vector3 Camera() => _world.CameraPosition.RelativeTo(_world.OwnDrawnPosition);
        Chord(GlfwKey.R);
        var before = (editor.PartSize, editor.PartShape);

        // A right drag over space orbits and the turn stays, as it is; so does a right click alone.
        var camera = Camera();
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() => Button(GlfwMouseButton.Right, true));
        EditorFrame(() => Move(EmptySpace + new float2(80, -10)));
        EditorFrame(() => Move(EmptySpace + new float2(160, -20)));
        EditorFrame(() => Button(GlfwMouseButton.Right, false));
        Assert.True((Camera() - camera).Length() > 0.5f, "the camera should have orbited");
        Assert.True(editor.RotatingPart);
        Assert.True(editor.Placing);
        Assert.Equal(before, (editor.PartSize, editor.PartShape));
        EditorFrame(() => Click(GlfwMouseButton.Right));
        EditorFrame();
        Assert.True(editor.RotatingPart);
        Assert.True(editor.Placing);

        // With the right button held the wheel over a ring turns nothing.
        var at = RingAt(0);
        EditorFrame(() => Move(at));
        EditorFrame(() => Button(GlfwMouseButton.Right, true));
        EditorFrame(() => Wheel(1));
        EditorFrame(() => Button(GlfwMouseButton.Right, false));
        Assert.Equal(before, (editor.PartSize, editor.PartShape));
        Assert.True(editor.RotatingPart);

        // The rings were reprojected with the camera: a click on one still turns it.
        at = RingAt(0);
        EditorFrame(() => Move(at));
        EditorFrame(() => Click());
        var once = (editor.PartSize, editor.PartShape);
        Assert.Equal(BlockShapes.Rotate(before.PartShape, 0), once.PartShape);

        // A ring drag held while the camera orbits: the orbit turns nothing, and the drag goes on from where the
        // pointer is when the right button is let go.
        var a0 = RingAngleAt(0);
        EditorFrame(() => Move(RingOnScreen(0, a0)));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        AlongRing(0, a0, a0 + Degrees(20));
        Assert.Equal(once, (editor.PartSize, editor.PartShape));
        var pointer = RingOnScreen(0, a0 + Degrees(20));
        camera = Camera();
        EditorFrame(() => Button(GlfwMouseButton.Right, true));
        EditorFrame(() => Move(pointer + new float2(30, 10)));
        EditorFrame(() => Move(pointer + new float2(60, 20)));
        Assert.True((Camera() - camera).Length() > 0.1f, "the camera should have orbited");
        Assert.Equal(once, (editor.PartSize, editor.PartShape));
        // The pointer comes back to the ring further round than it left it: had the drag not started again from
        // there, the thirty degrees between would count.
        var a1 = a0 + Degrees(50);
        var resume = RingOnScreen(0, a1);
        EditorFrame(() =>
        {
            Move(resume);
            Button(GlfwMouseButton.Right, false);
        });
        Assert.Equal(once, (editor.PartSize, editor.PartShape));
        Assert.True(editor.RotatingPart);
        AlongRing(0, a1, a1 + Degrees(60));
        Assert.Equal(BlockShapes.Rotate(once.PartShape, 0), editor.PartShape);
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        Assert.Equal(BlockShapes.Rotate(once.PartShape, 0), editor.PartShape);
        Assert.True(editor.RotatingPart);
        Assert.Equal(blocks, DraftBlocks);

        // A ring drag passes over a panel and is let go there: the turn is neither put back nor clicked.
        a0 = RingAngleAt(0);
        var shape = editor.PartShape;
        EditorFrame(() => Move(RingOnScreen(0, a0)));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        AlongRing(0, a0, a0 + Degrees(20));
        EditorFrame(() => Move(PaletteMin + new float2(110, 75)));
        EditorFrame(() => Move(RingOnScreen(0, a0 + Degrees(20))));
        Assert.True(editor.RotatingPart);
        Assert.Equal(shape, editor.PartShape);
        EditorFrame(() => Move(PaletteMin + new float2(110, 75)));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        EditorFrame();
        Assert.True(editor.RotatingPart);
        Assert.Equal(blocks, DraftBlocks);

        // Escape puts back the size and shape from before R, and the ghost is drawn as it is at once.
        Chord(GlfwKey.Escape);
        Assert.False(editor.RotatingPart);
        Assert.Equal(EditorStage.Editing, editor.Stage);
        Assert.Equal(before, (editor.PartSize, editor.PartShape));
        _world.GhostPose(out var turn, out var shift);
        Assert.Equal(Quaternion.Identity, turn);
        Assert.Equal(Vector3.Zero, shift);
    }

    [Fact]
    public void InterruptedRingDragsSettleAndOrbitingAPendingClickDoesNotTurnTheBlock()
    {
        var editor = WedgeOnAFace();
        Chord(GlfwKey.R);
        var before = (editor.PartSize, editor.PartShape);
        var blocks = DraftBlocks;
        var angle = RingAngleAt(0);
        EditorFrame(() => Move(RingOnScreen(0, angle)));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        AlongRing(0, angle, angle + Degrees(20));
        for (var i = 0; i < 20; i++) EditorFrame();
        _world.GhostPose(out var partial, out _);
        Assert.NotEqual(Quaternion.Identity, partial);

        // Losing world input interrupts the drag; the eventual release cannot leave a residual visual angle.
        _surfaceOpen = true;
        EditorFrame();
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        _surfaceOpen = false;
        for (var i = 0; i < 30; i++) EditorFrame();
        _world.GhostPose(out var settled, out var shift);
        Assert.Equal(Quaternion.Identity, settled);
        Assert.Equal(Vector3.Zero, shift);
        Assert.True(editor.RotatingPart);
        Assert.Equal(before, (editor.PartSize, editor.PartShape));

        // Orbit before the left press has become a ring drag, then release left while still orbiting.
        var at = RingAt(0);
        EditorFrame(() => Move(at));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        EditorFrame(() => Button(GlfwMouseButton.Right, true));
        EditorFrame(() => Move(at + new float2(80, 20)));
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        EditorFrame(() => Button(GlfwMouseButton.Right, false));
        for (var i = 0; i < 30; i++) EditorFrame();
        Assert.True(editor.RotatingPart);
        Assert.Equal(before, (editor.PartSize, editor.PartShape));
        Assert.Equal(blocks, DraftBlocks);
        _world.GhostPose(out settled, out shift);
        Assert.Equal(Quaternion.Identity, settled);
        Assert.Equal(Vector3.Zero, shift);
    }

    [Fact]
    public void REndingAPartialRingDragSettlesTheKeptGhostWithoutPlacing()
    {
        var editor = WedgeOnAFace();
        Chord(GlfwKey.R);
        var before = (editor.PartSize, editor.PartShape);
        var blocks = DraftBlocks;
        var angle = RingAngleAt(0);
        EditorFrame(() => Move(RingOnScreen(0, angle)));
        EditorFrame(() => Button(GlfwMouseButton.Left, true));
        AlongRing(0, angle, angle + Degrees(20));
        for (var i = 0; i < 20; i++) EditorFrame();
        _world.GhostPose(out var partial, out _);
        Assert.NotEqual(Quaternion.Identity, partial);
        Chord(GlfwKey.R);
        EditorFrame(() => Button(GlfwMouseButton.Left, false));
        for (var i = 0; i < 30; i++) EditorFrame();
        Assert.False(editor.RotatingPart);
        Assert.True(editor.HasGhost);
        Assert.True(editor.GhostPinned);
        Assert.Equal(before, (editor.PartSize, editor.PartShape));
        Assert.Equal(blocks, DraftBlocks);
        _world.GhostPose(out var settled, out var shift);
        Assert.Equal(Quaternion.Identity, settled);
        Assert.Equal(Vector3.Zero, shift);
    }

    [Fact]
    public void RStartsATurnOnlyWithAGhostOnAFaceAndTheKeyboardFree()
    {
        var editor = WedgeOnAFace();
        _paletteShown = true;

        // Over empty space there is no ghost to turn.
        EditorFrame(() => Move(EmptySpace));
        Assert.False(editor.HasGhost);
        Chord(GlfwKey.R);
        Assert.False(editor.RotatingPart);

        // While selecting, not placing, there is none either.
        editor.StopPlacing();
        EditorFrame(() => Move(HullOnScreen()));
        Chord(GlfwKey.R);
        Assert.False(editor.RotatingPart);
        editor.ChoosePart(editor.Part);

        // A text field with the keyboard keeps its R.
        EditorFrame(() => Move(OverSearch));
        EditorFrame(() => Click());
        EditorFrame();
        Assert.True(ImGui.GetIO().WantTextInput);
        EditorFrame(() => Move(HullOnScreen()));
        Chord(GlfwKey.R);
        Assert.False(editor.RotatingPart);
        EditorFrame(() => Move(EmptySpace));
        EditorFrame(() => Click());
        EditorFrame();

        // With the ghost on a face and the keyboard free, it starts.
        EditorFrame(() => Move(HullOnScreen()));
        Assert.True(editor.HasGhost);
        Chord(GlfwKey.R);
        Assert.True(editor.RotatingPart);
    }

    [Fact]
    public void ADesignThatCannotPowerItselfIsWarnedAboutAndCanStillBeApplied()
    {
        OpenEditor();
        var editor = _world.Editor;
        var boxes = editor.Draft!.Boxes;
        for (var i = 0; i < boxes.Length; i++)
            if (boxes[i].Type.PowerOutput > 0f)
                editor.Select(i, editor.Selection.Length > 0);
        Assert.NotEmpty(editor.Selection.ToArray());
        editor.DeleteSelected();
        AwaitCheck();

        Assert.Equal(DraftCheck.Ready, editor.Check);
        var tuning = EditorTuning.Load(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "editor.json"));
        Span<DesignWarning> warnings = stackalloc DesignWarning[DesignWarnings.Count];
        var count = DesignWarnings.Of(editor.Checked.Design!.Flight, tuning, warnings);
        Assert.Contains(DesignWarning.NoPower, warnings[..count].ToArray());
        Assert.True(editor.CanApply);
        Assert.NotNull(editor.Latest.Surface);
        Assert.True(editor.Latest.Surface!.Value.Fits);
    }

    [Theory]
    [InlineData(960f, 540f, 1f)]
    [InlineData(1280f, 720f, 1f)]
    [InlineData(1280f, 720f, 2f)]
    [InlineData(1655f, 688f, 2f)]
    [InlineData(1920f, 1080f, 2f)]
    [InlineData(960f, 540f, 2f)]
    public void TheEditorsPanelsLeaveTheMiddleClearAndHoldTheirWordsWithTheShippedFonts(float width, float height, float scale)
    {
        var shipped = ShippedFonts();
        ImGui.GetStyle().FontScaleMain = scale;
        _ui.NewFrame();
        try
        {
            float Px(float design) => MathF.Round(design * scale);
            var display = new float2(width, height);
            var layout = EditorLayout.Of(display, scale);
            HudRect[] panels = [layout.Parts, layout.Details, layout.Bar];
            foreach (var panel in panels)
            {
                Assert.True(panel.Left >= 0 && panel.Right <= width && panel.Top >= HudLayout.TopBarBottom(scale) && panel.Bottom <= height, $"{panel}");
                Assert.True(panel.Width > 0 && panel.Height > 0, $"{panel}");
            }

            Assert.False(layout.Parts.Overlaps(layout.Details));
            Assert.False(layout.Parts.Overlaps(layout.Bar));
            Assert.False(layout.Details.Overlaps(layout.Bar));
            // At least a third of the width between the side panels is the ship's.
            Assert.True(layout.Details.Left - layout.Parts.Right >= width / 3f, $"{layout.Details.Left - layout.Parts.Right}");

            // The bar holds its buttons; their labels fit them.
            Assert.True(EditorLayout.ButtonsWidth(scale) + Px(EditorLayout.Pad) <= layout.Bar.Width);
            ReadOnlySpan<string> labels = ["Undo", "Redo", "Cancel", "Applying"];
            for (var i = 0; i < labels.Length; i++)
            {
                var label = Kit.Measure(shipped.SansMedium, Theme.FontSmall, Encoding.UTF8.GetBytes(labels[i])).X;
                Assert.InRange(label, 1f, Px(EditorLayout.ButtonWidths[Math.Min(i, 3)]) - Px(4));
            }

            // Every part's name fits its card beside the swatch, with the list's scroll bar showing, down to 640
            // design pixels across (1280 at a scale of 2); narrower than that a long name is cut with dots rather
            // than spill out of the panel. The drone tag sits on the line below, with the role.
            var tuning = EditorTuning.Load(Path.Combine(AppContext.BaseDirectory, "content", "visuals", "editor.json"));
            var inner = layout.Parts.Width - Px(EditorLayout.Pad) * 2;
            var card = inner - ImGui.GetStyle().ScrollbarSize - Px(26) - Px(6);
            if (width / scale >= 640f)
                foreach (var type in GameContent.Load().Blocks.Types)
                    Assert.InRange(Kit.Measure(shipped.SansMedium, Theme.FontSmall, Encoding.UTF8.GetBytes(type.Name)).X, 1f, card);

            // The tabs hold their names at every size: four to a row, fewer in a narrow panel, and at most four rows.
            var widest = 0f;
            foreach (var category in tuning.Categories)
                widest = Math.Max(widest, Kit.Measure(shipped.SansMedium, Theme.FontSmall, category.Label).X);
            var perRow = EditorLayout.TabsPerRow(inner, widest, Px(Theme.Space1), scale);
            Assert.InRange(widest, 1f, EditorLayout.TabWidth(inner, perRow, Px(Theme.Space1)) - Px(4));
            Assert.True((tuning.Categories.Length + perRow - 1) / perRow <= 4, $"{perRow} tabs a row");

            // The two hints for sizing and turning the part are a line each from 800 design pixels across; narrower
            // they wrap.
            if (width / scale >= 800f)
            {
                Assert.InRange(Kit.Measure(shipped.Sans, Theme.FontSmall, ScreenHost.SizeHint).X, 1f, inner);
                Assert.InRange(Kit.Measure(shipped.Sans, Theme.FontSmall, ScreenHost.TurnHint).X, 1f, inner);
            }

            // The face buttons, in two rows of three, each hold their label.
            var details = layout.Details.Width - Px(EditorLayout.Pad) * 2;
            var chip = MathF.Floor((details - Px(Theme.Space1) * 2) / 3f);
            Assert.InRange(Kit.Measure(shipped.SansMedium, Theme.FontSmall, "+X"u8).X, 1f, chip - Px(4));
        }
        finally
        {
            ImGui.Render();
        }
    }
}
