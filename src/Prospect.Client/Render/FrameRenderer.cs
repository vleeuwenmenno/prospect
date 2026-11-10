using System.Buffers;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace Prospect.Client.Render;

/// <summary>A captured frame: tightly packed 8-bit pixels in a pooled array that must be released after use.</summary>
internal struct Screenshot
{
    public byte[] Pixels;
    public int Width;
    public int Height;
    public bool IsBgra;

    public readonly void Release() => ArrayPool<byte>.Shared.Return(Pixels);
}

/// <summary>
/// Drives one frame: acquire a swapchain image, record into the frame's command buffer, submit, present. Keeps two
/// frames in flight so the CPU never waits on the frame the GPU is still drawing.
/// </summary>
internal sealed unsafe class FrameRenderer : IDisposable
{
    public const int FramesInFlight = 2;

    private struct Frame
    {
        public VkCommandPool Pool;
        public CommandBuffer Commands;
        public VkSemaphore ImageAvailable;
        public VkFence InFlight;
    }

    private readonly GraphicsDevice _gpu;
    private readonly Swapchain _swapchain;
    private readonly Frame[] _frames = new Frame[FramesInFlight];
    private VkSemaphore[] _renderFinished = [];
    private int _frameIndex;
    private int _imageIndex;
    private bool _needsRecreate;

    private bool _screenshotRequested;
    private bool _screenshotRecorded;
    private VkBuffer _readback;
    private VmaAllocation _readbackAllocation;
    private nint _readbackMapped;
    private VkExtent2D _readbackExtent;

    public FrameRenderer(GraphicsDevice gpu, Swapchain swapchain)
    {
        _gpu = gpu;
        _swapchain = swapchain;

        for (var i = 0; i < FramesInFlight; i++)
        {
            ref var frame = ref _frames[i];
            frame.Pool = gpu.Device.CreateCommandPool(
                new VkCommandPoolCreateInfo { QueueFamilyIndex = gpu.QueueFamily },
                null
            );
            frame.Commands = gpu.Device.AllocateCommandBuffer(
                new VkCommandBufferAllocateInfo { CommandPool = frame.Pool, Level = VkCommandBufferLevel.Primary }
            );
            frame.ImageAvailable = gpu.Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
            frame.InFlight = gpu.Device.CreateFence(VkFenceCreateFlags.SignaledBit, null);
        }

        CreateImageSemaphores();
    }

    /// <summary>Set when the swapchain no longer matches the window and must be rebuilt before the next frame.</summary>
    public bool NeedsRecreate => _needsRecreate;

    /// <summary>True from a screenshot request until its pixels have been handed out.</summary>
    public bool ScreenshotPending => _screenshotRequested || _screenshotRecorded;

    /// <summary>Asks for the next presented frame to be copied back from the GPU.</summary>
    public void RequestScreenshot() => _screenshotRequested = _swapchain.CanReadBack;

    /// <summary>
    /// Returns the frame captured by the last <see cref="End"/>, if one was requested. Waits for the GPU to finish
    /// that frame, so it costs a few milliseconds on the frame a screenshot is taken and nothing otherwise.
    /// </summary>
    public bool TakeScreenshot(out Screenshot shot)
    {
        shot = default;
        if (!_screenshotRecorded)
            return false;

        _screenshotRecorded = false;
        _gpu.Device.WaitIdle();
        var bytes = _readbackExtent.Width * _readbackExtent.Height * 4;
        shot = new Screenshot
        {
            Pixels = ArrayPool<byte>.Shared.Rent(bytes),
            Width = _readbackExtent.Width,
            Height = _readbackExtent.Height,
            IsBgra = _swapchain.SurfaceFormat.Format == VkFormat.B8G8R8A8UNorm,
        };
        new ReadOnlySpan<byte>((void*)_readbackMapped, bytes).CopyTo(shot.Pixels);
        return true;
    }

    /// <summary>Rebuilds the swapchain for a new size or present mode.</summary>
    public void Recreate(int2 framebufferSize, bool vsync)
    {
        _swapchain.Recreate(framebufferSize, vsync);
        foreach (var semaphore in _renderFinished)
            _gpu.Device.DestroySemaphore(semaphore, null);
        CreateImageSemaphores();
        _needsRecreate = false;
    }

    /// <summary>Which of the frames in flight is being recorded; selects per-frame GPU buffers.</summary>
    public int FrameIndex => _frameIndex;

    /// <summary>
    /// Starts a frame and its command buffer. Returns false when no swapchain image could be acquired, in which
    /// case the frame is skipped and the swapchain flagged for rebuild.
    /// </summary>
    public bool Begin(out CommandBuffer commands)
    {
        ref var frame = ref _frames[_frameIndex];
        commands = frame.Commands;

        var device = _gpu.Device;
        device.WaitForFence(frame.InFlight, -1);

        int imageIndex;
        var result = device.PInvoke.vkAcquireNextImageKHR.Invoke(
            device.Handle,
            _swapchain.Handle,
            -1,
            frame.ImageAvailable,
            VkFence.NullHandle,
            &imageIndex
        );
        if (result == VkResult.ErrorOutOfDateKHR)
        {
            _needsRecreate = true;
            return false;
        }

        if (result is not (VkResult.Success or VkResult.SuboptimalKHR))
            throw new InvalidOperationException($"Failed to acquire a swapchain image: {result}");
        if (result == VkResult.SuboptimalKHR)
            _needsRecreate = true;

        _imageIndex = imageIndex;
        var fence = frame.InFlight;
        device.ResetFences(new ReadOnlySpan<VkFence>(in fence));
        device.ResetCommandPool(frame.Pool, VkCommandPoolResetFlags.None);

        commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        return true;
    }

    /// <summary>Opens the render pass on the swapchain image; everything drawn after this goes to the screen.</summary>
    public void BeginPresentPass(in float4 clearColor)
    {
        var clear = new VkClearValue { Color = new VkClearColorValue { Float32 = clearColor } };
        _frames[_frameIndex].Commands.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = _swapchain.RenderPass,
                Framebuffer = _swapchain.Framebuffer(_imageIndex),
                RenderArea = new VkRect2D(_swapchain.Extent),
                ClearValueCount = 1,
                ClearValues = &clear,
            },
            VkSubpassContents.Inline
        );
    }

    /// <summary>Closes the render pass, submits the frame and presents it.</summary>
    public void End()
    {
        ref var frame = ref _frames[_frameIndex];
        var commands = frame.Commands;
        commands.EndRenderPass();
        if (_screenshotRequested)
        {
            _screenshotRequested = false;
            _screenshotRecorded = true;
            RecordReadback(commands, _swapchain.Image(_imageIndex));
        }

        commands.End();

        var waitSemaphore = frame.ImageAvailable;
        var waitStage = VkPipelineStageFlags.ColorAttachmentOutputBit;
        var commandBuffer = commands.Handle;
        var signalSemaphore = _renderFinished[_imageIndex];
        var submit = new VkSubmitInfo
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &waitSemaphore,
            WaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            CommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            SignalSemaphores = &signalSemaphore,
        };
        _gpu.Queue.Submit(new Span<VkSubmitInfo>(ref submit), frame.InFlight);

        var swapchain = _swapchain.Handle;
        var imageIndex = _imageIndex;
        var presentResult = VkResult.Success;
        var present = new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            Swapchains = &swapchain,
            ImageIndices = &imageIndex,
            Results = &presentResult,
        };

        var queue = _gpu.Queue;
        var result = queue.PInvoke.vkQueuePresentKHR.Invoke(queue.Handle, &present);
        if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            _needsRecreate = true;
        else if (result != VkResult.Success)
            throw new InvalidOperationException($"Failed to present: {result}");

        _frameIndex = (_frameIndex + 1) % FramesInFlight;
    }

    public void Dispose()
    {
        _gpu.Device.WaitIdle();
        if (!_readback.IsNull())
            _gpu.Allocator.DestroyBuffer(_readback, _readbackAllocation);
        foreach (var semaphore in _renderFinished)
            _gpu.Device.DestroySemaphore(semaphore, null);
        foreach (var frame in _frames)
        {
            _gpu.Device.DestroyFence(frame.InFlight, null);
            _gpu.Device.DestroySemaphore(frame.ImageAvailable, null);
            _gpu.Device.DestroyCommandPool(frame.Pool, null);
        }
    }

    private void RecordReadback(CommandBuffer commands, VkImage image)
    {
        var extent = _swapchain.Extent;
        if (_readback.IsNull() || extent.Width != _readbackExtent.Width || extent.Height != _readbackExtent.Height)
        {
            if (!_readback.IsNull())
            {
                _gpu.Device.WaitIdle();
                _gpu.Allocator.DestroyBuffer(_readback, _readbackAllocation);
            }

            _gpu.Allocator.CreateBuffer(
                new VkBufferCreateInfo
                {
                    Size = new ByteSize64((ulong)(extent.Width * extent.Height * 4)),
                    Usage = VkBufferUsageFlags.TransferDstBit,
                },
                new VmaAllocationCreateInfo
                {
                    Usage = VmaMemoryUsage.Auto,
                    Flags = VmaAllocationCreateFlags.HostAccessRandomBit | VmaAllocationCreateFlags.MappedBit,
                },
                out _readback,
                out _readbackAllocation,
                out var info
            );
            _readbackMapped = (nint)info.PMappedData;
            _readbackExtent = extent;
        }

        var range = new VkImageSubresourceRange
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            LevelCount = 1,
            LayerCount = 1,
        };
        commands.PipelineBarrier(
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkPipelineStageFlags.TransferBit,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
                    DstAccessMask = VkAccessFlags.TransferReadBit,
                    OldLayout = VkImageLayout.PresentSrcKHR,
                    NewLayout = VkImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = range,
                },
            ]
        );
        commands.CopyImageToBuffer(
            image,
            VkImageLayout.TransferSrcOptimal,
            _readback,
            [
                new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageExtent = new VkExtent3D(extent.Width, extent.Height, 1),
                },
            ]
        );
        commands.PipelineBarrier(
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.BottomOfPipeBit,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.TransferReadBit,
                    DstAccessMask = VkAccessFlags.None,
                    OldLayout = VkImageLayout.TransferSrcOptimal,
                    NewLayout = VkImageLayout.PresentSrcKHR,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = range,
                },
            ]
        );
    }

    private void CreateImageSemaphores()
    {
        _renderFinished = new VkSemaphore[_swapchain.ImageCount];
        for (var i = 0; i < _renderFinished.Length; i++)
            _renderFinished[i] = _gpu.Device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
    }
}
