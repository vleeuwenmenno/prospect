using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace Prospect.Client.Render;

/// <summary>An RGBA8 texture on the GPU with a linear sampler.</summary>
internal sealed unsafe class Texture2D : IDisposable
{
    private readonly GraphicsDevice _gpu;
    private readonly VkImage _image;
    private readonly VmaAllocation _allocation;

    private Texture2D(GraphicsDevice gpu, VkImage image, VmaAllocation allocation, VkImageView view, VkSampler sampler, int width, int height)
    {
        _gpu = gpu;
        _image = image;
        _allocation = allocation;
        View = view;
        Sampler = sampler;
        Width = width;
        Height = height;
    }

    public VkImageView View { get; }
    public VkSampler Sampler { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Uploads tightly packed RGBA8 pixels. Blocks until the copy has finished; meant for load time.</summary>
    public static Texture2D FromRgba(GraphicsDevice gpu, int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != width * height * 4)
            throw new ArgumentException("Pixel data does not match the texture size", nameof(pixels));

        var device = gpu.Device;
        var allocator = gpu.Allocator;
        var size = new ByteSize64((ulong)pixels.Length);

        allocator.CreateBuffer(
            new VkBufferCreateInfo { Size = size, Usage = VkBufferUsageFlags.TransferSrcBit },
            new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.Auto,
                Flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit,
            },
            out var staging,
            out var stagingAllocation,
            out var stagingInfo
        );
        pixels.CopyTo(new Span<byte>((void*)stagingInfo.PMappedData, pixels.Length));
        allocator.FlushAllocation(stagingAllocation, ByteSize.Zero, new ByteSize((nuint)pixels.Length));

        allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = VkFormat.R8G8B8A8UNorm,
                Extent = new VkExtent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto },
            out var image,
            out var allocation,
            out _
        );

        var range = new VkImageSubresourceRange
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            LevelCount = 1,
            LayerCount = 1,
        };

        var pool = device.CreateCommandPool(
            new VkCommandPoolCreateInfo { QueueFamilyIndex = gpu.QueueFamily, Flags = VkCommandPoolCreateFlags.TransientBit },
            null
        );
        var commands = device.AllocateCommandBuffer(
            new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary }
        );
        commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        commands.PipelineBarrier(
            VkPipelineStageFlags.TopOfPipeBit,
            VkPipelineStageFlags.TransferBit,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.None,
                    DstAccessMask = VkAccessFlags.TransferWriteBit,
                    OldLayout = VkImageLayout.Undefined,
                    NewLayout = VkImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = range,
                },
            ]
        );
        commands.CopyBufferToImage(
            staging,
            image,
            VkImageLayout.TransferDstOptimal,
            [
                new VkBufferImageCopy
                {
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                    ImageExtent = new VkExtent3D(width, height, 1),
                },
            ]
        );
        commands.PipelineBarrier(
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.FragmentShaderBit,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.TransferWriteBit,
                    DstAccessMask = VkAccessFlags.ShaderReadBit,
                    OldLayout = VkImageLayout.TransferDstOptimal,
                    NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = range,
                },
            ]
        );
        commands.End();

        gpu.Queue.Submit([], [], [commands], [], VkFence.NullHandle);
        gpu.Queue.WaitIdle();
        device.DestroyCommandPool(pool, null);
        allocator.DestroyBuffer(staging, stagingAllocation);

        var view = device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = VkFormat.R8G8B8A8UNorm,
                SubresourceRange = range,
            },
            null
        );
        var sampler = device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                MipmapMode = VkSamplerMipmapMode.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
            },
            null
        );

        return new Texture2D(gpu, image, allocation, view, sampler, width, height);
    }

    public void Dispose()
    {
        _gpu.Device.DestroySampler(Sampler, null);
        _gpu.Device.DestroyImageView(View, null);
        _gpu.Allocator.DestroyImage(_image, _allocation);
    }
}
