using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.ImGuiApi.FreeType;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Prospect.Client.Input;
using Prospect.Client.Render;
using Prospect.Engine.IO;

namespace Prospect.Client.Ui;

/// <summary>Owns Dear ImGui: its context, the platform and Vulkan layers, the game's fonts and the base style.</summary>
internal sealed class UiContext : IDisposable
{
    private readonly ImGuiContextPtr _context;
    private readonly UiPlatform _platform;
    private readonly VulkanBackend _renderer;
    private readonly GlfwWindow _window;
    private float _userScale;

    public UiContext(GlfwWindow window, InputDevices devices, GraphicsDevice gpu, Swapchain swapchain, float userScale)
    {
        _window = window;
        _userScale = userScale;
        _context = ImGui.CreateContext();

        var io = ImGui.GetIO();
        io.IniFilename = null;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.NavEnableGamepad;
        io.ConfigNavCursorVisibleAuto = true;

        var fonts = io.Fonts;
        fonts.SetFontLoader(ImGuiFreeType.GetFontLoader());
        Sans = fonts.AddFontFromFileTTF(AppPaths.Asset("fonts", "SpaceGrotesk-Regular.ttf"), Theme.FontBody);
        SansMedium = fonts.AddFontFromFileTTF(AppPaths.Asset("fonts", "SpaceGrotesk-Medium.ttf"), Theme.FontBody);
        SansBold = fonts.AddFontFromFileTTF(AppPaths.Asset("fonts", "SpaceGrotesk-Bold.ttf"), Theme.FontBody);
        Mono = fonts.AddFontFromFileTTF(AppPaths.Asset("fonts", "JetBrainsMono-Regular.ttf"), Theme.FontBody);
        io.FontDefault = Sans;

        _platform = new UiPlatform(window, devices);
        _renderer = new VulkanBackend(
            new VulkanBackend.CreateInfo
            {
                Device = gpu.Device,
                GraphicsQueue = gpu.Queue,
                RenderPass = swapchain.RenderPass,
                MinImageCount = FrameRenderer.FramesInFlight,
                ImageCount = FrameRenderer.FramesInFlight + 1,
                SampleCount = VkSampleCountFlags._1Bit,
                DescriptorPoolSize = 64,
            }
        );

        ApplyStyle();
    }

    public ImFontPtr Sans { get; }
    public ImFontPtr SansMedium { get; }
    public ImFontPtr SansBold { get; }
    public ImFontPtr Mono { get; }

    /// <summary>
    /// Window units per design unit: the monitor's content scale times the player's UI scale setting. Where the window is
    /// measured in points rather than pixels (macOS, Wayland), the framebuffer scale already covers the content scale.
    /// </summary>
    public float Scale { get; private set; } = 1f;

    /// <summary>Seconds since the previous frame, clamped so a hitch cannot make animations jump.</summary>
    public float DeltaTime { get; private set; }

    /// <summary>Seconds of UI time since startup.</summary>
    public float Time { get; private set; }

    public float2 DisplaySize => ImGui.GetIO().DisplaySize;

    /// <summary>True while the pointer is over an interface element that takes it, so the world must not react.</summary>
    public bool PointerOverInterface => ImGui.GetIO().WantCaptureMouse;

    /// <summary>True while a text field has keyboard focus, so game key bindings must not fire.</summary>
    public bool WantsTextInput => ImGui.GetIO().WantTextInput;

    public void SetUserScale(float scale) => _userScale = scale;

    /// <summary>Hands the pointer to flight controls or back to the interface.</summary>
    public void SetPointerCaptured(bool captured) => _platform.SetPointerCaptured(captured);

    /// <summary>Makes a texture drawable by UI image calls.</summary>
    public ImTextureRef Register(Texture2D texture) =>
        _renderer.AddTexture(texture.Sampler, texture.View, VkImageLayout.ShaderReadOnlyOptimal);

    public void NewFrame()
    {
        _platform.NewFrame();

        var framebufferScale = Math.Max(1f, ImGui.GetIO().DisplayFramebufferScale.X);
        Scale = Math.Max(1f, _window.ContentScale.X / framebufferScale) * _userScale;
        ImGui.GetStyle().FontScaleMain = Scale;

        _renderer.NewFrame();
        ImGui.NewFrame();
        ImString.SharedStorage.Reset();

        DeltaTime = Math.Min(ImGui.GetIO().DeltaTime, 0.1f);
        Time += DeltaTime;
        Kit.BeginFrame(this);
    }

    /// <summary>Finishes the UI frame and records its draw commands into the open render pass.</summary>
    public void Render(CommandBuffer commands)
    {
        ImGui.Render();
        _renderer.RenderDrawData(ImGui.GetDrawData(), commands);
    }

    /// <summary>Finishes the UI frame without drawing it, for frames that could not acquire a swapchain image.</summary>
    public void Discard() => ImGui.EndFrame();

    public void Dispose()
    {
        _renderer.Dispose();
        _platform.Dispose();
        ImGui.DestroyContext(_context);
    }

    private static void ApplyStyle()
    {
        var style = ImGui.GetStyle();
        style.WindowPadding = float2.Zero;
        style.WindowBorderSize = 0;
        style.WindowRounding = 0;
        style.ChildBorderSize = 0;
        style.FrameBorderSize = 1;
        style.FrameRounding = 0;
        style.FramePadding = new float2(Theme.Space3, 10f);
        style.ItemSpacing = float2.Zero;
        style.ScrollbarSize = 10;
        style.ScrollbarRounding = 0;

        ImGui.PushStyleColor(ImGuiCol.Text, Kit.Opaque(Theme.Text));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Kit.Opaque(Theme.TextMute));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Kit.Opaque(Theme.Void));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Kit.Opaque(Theme.Void));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Kit.Opaque(Theme.Backdrop));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Kit.Opaque(Theme.Backdrop));
        ImGui.PushStyleColor(ImGuiCol.Border, Kit.Opaque(Theme.Line));
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, Kit.Opaque(Theme.Accent, 0.35f));
        ImGui.PushStyleColor(ImGuiCol.InputTextCursor, Kit.Opaque(Theme.Accent));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Kit.Opaque(Theme.Void, 0f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Kit.Opaque(Theme.Line));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Kit.Opaque(Theme.LineStrong));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Kit.Opaque(Theme.Accent));
        // Focus is drawn by each widget; the stock highlight rectangle never appears.
        ImGui.PushStyleColor(ImGuiCol.NavCursor, Kit.Opaque(Theme.Void, 0f));
    }
}
