using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Prospect.Engine.Diagnostics;

namespace Prospect.Client.Render;

/// <summary>
/// The window's swapchain with a render pass and one framebuffer per image. Rebuilt whenever the window changes
/// size or the present mode changes.
/// </summary>
internal sealed unsafe class Swapchain : IDisposable
{
    private readonly GraphicsDevice _gpu;
    private VkSwapchainKHR _swapchain;
    private VkImage[] _images = [];
    private VkImageView[] _views = [];
    private VkFramebuffer[] _framebuffers = [];

    public Swapchain(GraphicsDevice gpu, int2 framebufferSize, bool vsync)
    {
        _gpu = gpu;

        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[
            gpu.PhysicalDevice.GetSurfaceFormatsKHR_Count(gpu.Surface)
        ];
        gpu.PhysicalDevice.GetSurfaceFormatsKHR_Write(gpu.Surface, formats);
        SurfaceFormat = formats[0];
        foreach (var format in formats)
        {
            // An 8-bit UNORM target: the UI and the tone mapper both write display-encoded colour themselves.
            if (
                format.Format is VkFormat.B8G8R8A8UNorm or VkFormat.R8G8B8A8UNorm
                && format.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR
            )
            {
                SurfaceFormat = format;
                break;
            }
        }

        RenderPass = CreateRenderPass();
        Create(framebufferSize, vsync);
    }

    public VkSurfaceFormatKHR SurfaceFormat { get; }
    public VkRenderPass RenderPass { get; }
    public VkExtent2D Extent { get; private set; }
    public int ImageCount => _views.Length;
    public int MinImageCount { get; private set; }
    public VkPresentModeKHR PresentMode { get; private set; }
    public VkSwapchainKHR Handle => _swapchain;

    /// <summary>True when swapchain images can be copied back to the CPU, which screenshots need.</summary>
    public bool CanReadBack { get; private set; }

    public VkFramebuffer Framebuffer(int imageIndex) => _framebuffers[imageIndex];

    public VkImage Image(int imageIndex) => _images[imageIndex];

    /// <summary>Replaces the swapchain after a resize or a present-mode change. The device must be idle.</summary>
    public void Recreate(int2 framebufferSize, bool vsync)
    {
        _gpu.Device.WaitIdle();
        Create(framebufferSize, vsync);
    }

    public void Dispose()
    {
        DestroyImages();
        if (!_swapchain.IsNull())
            _gpu.Device.DestroySwapchainKHR(_swapchain, null);
        _gpu.Device.DestroyRenderPass(RenderPass, null);
    }

    private void Create(int2 framebufferSize, bool vsync)
    {
        var gpu = _gpu;
        var capabilities = gpu.PhysicalDevice.GetSurfaceCapabilitiesKHR(gpu.Surface);

        var extent = capabilities.CurrentExtent;
        if (extent.Width == -1 && extent.Height == -1)
            extent = new VkExtent2D(
                Math.Clamp(framebufferSize.X, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(framebufferSize.Y, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
            );

        var imageCount = Math.Max(capabilities.MinImageCount + 1, 3);
        if (capabilities.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);

        var compositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR;
        if ((capabilities.SupportedCompositeAlpha & compositeAlpha) == 0)
        {
            ReadOnlySpan<VkCompositeAlphaFlagsKHR> fallbacks =
            [
                VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR,
                VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR,
                VkCompositeAlphaFlagsKHR.InheritBitKHR,
            ];
            foreach (var mode in fallbacks)
            {
                if ((capabilities.SupportedCompositeAlpha & mode) == 0)
                    continue;
                compositeAlpha = mode;
                break;
            }
        }

        var usage = VkImageUsageFlags.ColorAttachmentBit;
        CanReadBack = (capabilities.SupportedUsageFlags & VkImageUsageFlags.TransferSrcBit) != 0;
        if (CanReadBack)
            usage |= VkImageUsageFlags.TransferSrcBit;

        var presentMode = ChoosePresentMode(vsync);
        var old = _swapchain;
        _swapchain = gpu.Device.CreateSwapchainKHR(
            new VkSwapchainCreateInfoKHR
            {
                Surface = gpu.Surface,
                MinImageCount = imageCount,
                ImageFormat = SurfaceFormat.Format,
                ImageColorSpace = SurfaceFormat.ColorSpace,
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = usage,
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = compositeAlpha,
                PresentMode = presentMode,
                Clipped = true,
                OldSwapchain = old,
            },
            null
        );

        DestroyImages();
        if (!old.IsNull())
            gpu.Device.DestroySwapchainKHR(old, null);

        var images = new VkImage[gpu.Device.GetSwapchainImagesKHR_Count(_swapchain)];
        gpu.Device.GetSwapchainImagesKHR_Write(_swapchain, images);

        _images = images;
        _views = new VkImageView[images.Length];
        _framebuffers = new VkFramebuffer[images.Length];
        for (var i = 0; i < images.Length; i++)
        {
            _views[i] = gpu.Device.CreateImageView(
                new VkImageViewCreateInfo
                {
                    Image = images[i],
                    ViewType = VkImageViewType._2D,
                    Format = SurfaceFormat.Format,
                    SubresourceRange = new VkImageSubresourceRange
                    {
                        AspectMask = VkImageAspectFlags.ColorBit,
                        LevelCount = 1,
                        LayerCount = 1,
                    },
                },
                null
            );

            var view = _views[i];
            _framebuffers[i] = gpu.Device.CreateFramebuffer(
                new VkFramebufferCreateInfo
                {
                    RenderPass = RenderPass,
                    AttachmentCount = 1,
                    Attachments = &view,
                    Width = extent.Width,
                    Height = extent.Height,
                    Layers = 1,
                },
                null
            );
        }

        Extent = extent;
        MinImageCount = Math.Max(2, capabilities.MinImageCount);
        PresentMode = presentMode;
        Logs.Render.Info($"Swapchain {extent.Width}x{extent.Height}, {images.Length} images, {presentMode}");
    }

    private VkPresentModeKHR ChoosePresentMode(bool vsync)
    {
        if (vsync)
            return VkPresentModeKHR.FifoKHR;

        Span<VkPresentModeKHR> modes = stackalloc VkPresentModeKHR[
            _gpu.PhysicalDevice.GetSurfacePresentModesKHR_Count(_gpu.Surface)
        ];
        _gpu.PhysicalDevice.GetSurfacePresentModesKHR_Write(_gpu.Surface, modes);

        // Without vsync: newest-frame-wins if the driver has it, else tearing, else fall back to vsync.
        if (modes.Contains(VkPresentModeKHR.MailboxKHR))
            return VkPresentModeKHR.MailboxKHR;
        if (modes.Contains(VkPresentModeKHR.ImmediateKHR))
            return VkPresentModeKHR.ImmediateKHR;
        return VkPresentModeKHR.FifoKHR;
    }

    private VkRenderPass CreateRenderPass()
    {
        var attachment = new VkAttachmentDescription
        {
            Format = SurfaceFormat.Format,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.PresentSrcKHR,
        };
        var reference = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &reference,
        };
        var dependency = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = VkAccessFlags.None,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
        };

        return _gpu.Device.CreateRenderPass(
            new VkRenderPassCreateInfo
            {
                AttachmentCount = 1,
                Attachments = &attachment,
                SubpassCount = 1,
                Subpasses = &subpass,
                DependencyCount = 1,
                Dependencies = &dependency,
            },
            null
        );
    }

    private void DestroyImages()
    {
        foreach (var framebuffer in _framebuffers)
            _gpu.Device.DestroyFramebuffer(framebuffer, null);
        foreach (var view in _views)
            _gpu.Device.DestroyImageView(view, null);
        _framebuffers = [];
        _views = [];
    }
}
