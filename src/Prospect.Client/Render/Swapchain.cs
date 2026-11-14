using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Prospect.Engine.Diagnostics;

namespace Prospect.Client.Render;

/// <summary>
/// The window's swapchain with a render pass and one framebuffer per image. Rebuilt whenever the window changes
/// size or the present mode changes, which happens inside the frame loop: a rebuild takes no managed memory unless
/// the new swapchain has more images than any before it.
/// </summary>
internal sealed unsafe class Swapchain : IDisposable
{
    private readonly GraphicsDevice _gpu;
    private VkSwapchainKHR _swapchain;
    /// <summary>
    /// Room for this many images from the start. A swapchain is asked for three (or one more than the surface's
    /// minimum) and drivers hand out a few more at most, so the arrays below are not expected to grow at all.
    /// </summary>
    private const int InitialImageSlots = 16;

    // One slot per image, the first _imageCount of them in use. The arrays outlive a rebuild and are only replaced
    // by longer ones: a rebuild runs in the frame loop, where nothing may be allocated.
    private VkImage[] _images = new VkImage[InitialImageSlots];
    private VkImageView[] _views = new VkImageView[InitialImageSlots];
    private VkFramebuffer[] _framebuffers = new VkFramebuffer[InitialImageSlots];
    private int _imageCount;
    private bool _reported;

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
    public int ImageCount => _imageCount;

    /// <summary>
    /// How many images there is room for without allocating. It only grows; whoever keeps something per image
    /// sizes it by this, so that it grows on the same rare occasion and never otherwise.
    /// </summary>
    public int ImageCapacity => _images.Length;
    public int MinImageCount { get; private set; }
    public VkPresentModeKHR PresentMode { get; private set; }
    public VkSwapchainKHR Handle => _swapchain;

    /// <summary>True when swapchain images can be copied back to the CPU, which screenshots need.</summary>
    public bool CanReadBack { get; private set; }

    public VkFramebuffer Framebuffer(int imageIndex) => _framebuffers[imageIndex];

    public VkImage Image(int imageIndex) => _images[imageIndex];

    /// <summary>How many times the swapchain has been replaced since it was made.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Replaces the swapchain after a resize or a present-mode change. The device must be idle.</summary>
    public void Recreate(int2 framebufferSize, bool vsync)
    {
        Rebuilds++;
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

        var previousCount = _imageCount;
        DestroyImages();
        if (!old.IsNull())
            gpu.Device.DestroySwapchainKHR(old, null);

        var count = gpu.Device.GetSwapchainImagesKHR_Count(_swapchain);
        if (count > _images.Length)
        {
            // More images than any swapchain before: the one case in which a rebuild takes memory. The capacity
            // never shrinks, so this happens at most once for each larger count a driver ever reports.
            var capacity = Math.Max(count, _images.Length * 2);
            _images = new VkImage[capacity];
            _views = new VkImageView[capacity];
            _framebuffers = new VkFramebuffer[capacity];
        }

        var images = _images.AsSpan(0, count);
        gpu.Device.GetSwapchainImagesKHR_Write(_swapchain, images);
        _imageCount = count;
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

        // Writing a line takes memory, and a rebuild for another size is routine while a window's edge is dragged or
        // a tiling compositor rearranges: those are only reported in a verbose run. The first swapchain, and one
        // with another image count or present mode, is reported always.
        var routine = _reported && count == previousCount && presentMode == PresentMode;
        Extent = extent;
        MinImageCount = Math.Max(2, capabilities.MinImageCount);
        PresentMode = presentMode;
        _reported = true;
        if (routine)
            Logs.Render.Debug($"Swapchain {extent.Width}x{extent.Height}, {count} images, {presentMode}");
        else
            Logs.Render.Info($"Swapchain {extent.Width}x{extent.Height}, {count} images, {presentMode}");
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
        for (var i = 0; i < _imageCount; i++)
        {
            _gpu.Device.DestroyFramebuffer(_framebuffers[i], null);
            _gpu.Device.DestroyImageView(_views[i], null);
            _framebuffers[i] = default;
            _views[i] = default;
            _images[i] = default;
        }

        _imageCount = 0;
    }
}
