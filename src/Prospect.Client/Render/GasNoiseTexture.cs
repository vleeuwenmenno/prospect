using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace Prospect.Client.Render;

/// <summary>One renderer-owned, immutable 3D noise image. Independent of window size and sector lifetime.</summary>
internal sealed unsafe class GasNoiseTexture : IDisposable
{
    private const VkFormat Format = VkFormat.R8G8B8A8UNorm;
    private readonly GraphicsDevice _gpu;
    private VkImage _image;
    private VmaAllocation _allocation;
    private VkImageView _view;
    private VkSampler _sampler;

    private GasNoiseTexture(GraphicsDevice gpu) => _gpu = gpu;

    /// <summary>The uploaded 3D view, permanently in ShaderReadOnlyOptimal.</summary>
    public VkImageView View => _view;
    /// <summary>Linear filtering across all three axes, with periodic addressing and no mip levels.</summary>
    public VkSampler Sampler => _sampler;

    /// <summary>Creates and uploads once, waiting for the transfer before returning or releasing staging storage.</summary>
    public static GasNoiseTexture Create(GraphicsDevice gpu)
    {
        var required = VkFormatFeatureFlags.SampledImageBit | VkFormatFeatureFlags.SampledImageFilterLinearBit | VkFormatFeatureFlags.TransferDstBit;
        if ((gpu.PhysicalDevice.GetFormatProperties(Format).OptimalTilingFeatures & required) != required)
            throw new NotSupportedException("Gas noise needs a linearly sampled RGBA8 UNorm image.");

        var texture = new GasNoiseTexture(gpu);
        var device = gpu.Device;
        var staging = default(GpuBuffer);
        var pool = VkCommandPool.NullHandle;
        var submitted = false;
        var ready = false;
        try
        {
            // Fill coherent staging directly: no retained CPU image and no managed array for the payload.
            staging = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.TransferSrcBit, GasNoiseLattice.ByteCount);
            GasNoiseLattice.Fill(new Span<byte>((void*)staging.Mapped, GasNoiseLattice.ValueCount));
            var extent = new VkExtent3D(GasNoiseLattice.Side, GasNoiseLattice.Side, GasNoiseLattice.Side);
            var result = gpu.Allocator.CreateImage(
                new VkImageCreateInfo
                {
                    ImageType = VkImageType._3D,
                    Format = Format,
                    Extent = extent,
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = VkSampleCountFlags._1Bit,
                    Tiling = VkImageTiling.Optimal,
                    Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
                    SharingMode = VkSharingMode.Exclusive,
                    InitialLayout = VkImageLayout.Undefined,
                },
                new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto },
                out var image, out var allocation, out _
            );
            if (result != VkResult.Success)
                throw new VkResultError(result, "Could not allocate gas noise image.");
            texture._image = image;
            texture._allocation = allocation;
            var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
            texture._view = device.CreateImageView(new VkImageViewCreateInfo
            {
                Image = texture._image,
                ViewType = VkImageViewType._3D,
                Format = Format,
                SubresourceRange = range,
            }, null);
            texture._sampler = device.CreateSampler(new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                MipmapMode = VkSamplerMipmapMode.Nearest,
                AddressModeU = VkSamplerAddressMode.Repeat,
                AddressModeV = VkSamplerAddressMode.Repeat,
                AddressModeW = VkSamplerAddressMode.Repeat,
                MinLod = 0,
                MaxLod = 0,
            }, null);
            pool = device.CreateCommandPool(new VkCommandPoolCreateInfo
            {
                QueueFamilyIndex = gpu.QueueFamily,
                Flags = VkCommandPoolCreateFlags.TransientBit,
            }, null);
            var commands = device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary });
            commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
            var barrier = new VkImageMemoryBarrier
            {
                DstAccessMask = VkAccessFlags.TransferWriteBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = texture._image,
                SubresourceRange = range,
            };
            commands.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [], [], [barrier]);
            commands.CopyBufferToImage(staging.Buffer, texture._image, VkImageLayout.TransferDstOptimal,
                [new VkBufferImageCopy { ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 }, ImageExtent = extent }]);
            barrier.SrcAccessMask = VkAccessFlags.TransferWriteBit;
            barrier.DstAccessMask = VkAccessFlags.ShaderReadBit;
            barrier.OldLayout = VkImageLayout.TransferDstOptimal;
            barrier.NewLayout = VkImageLayout.ShaderReadOnlyOptimal;
            commands.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkDependencyFlags.None, [], [], [barrier]);
            commands.End();
            gpu.Queue.Submit([], [], [commands], [], VkFence.NullHandle);
            submitted = true;
            gpu.Queue.WaitIdle();
            submitted = false;
            ready = true;
            return texture;
        }
        catch (VkResultError error) when (error.Result == VkResult.ErrorDeviceLost)
        {
            submitted = false;
            throw;
        }
        finally
        {
            // Follow GpuBuffer's upload lifetime contract: submitted commands must finish before cleanup.
            if (submitted)
            {
                try { gpu.Queue.WaitIdle(); }
                catch (VkResultError error) when (error.Result == VkResult.ErrorDeviceLost) { }
            }
            if (!pool.IsNull()) device.DestroyCommandPool(pool, null);
            staging.Destroy(gpu);
            if (!ready) texture.Dispose();
        }
    }

    /// <summary>Destroys the immutable image after the owning renderer has waited for all frame work.</summary>
    public void Dispose()
    {
        if (!_sampler.IsNull()) _gpu.Device.DestroySampler(_sampler, null);
        if (!_view.IsNull()) _gpu.Device.DestroyImageView(_view, null);
        if (!_image.IsNull()) _gpu.Allocator.DestroyImage(_image, _allocation);
        _sampler = default; _view = default; _image = default; _allocation = default;
    }
}
