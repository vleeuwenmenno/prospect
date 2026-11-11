using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace Prospect.Client.Render;

/// <summary>The per-frame constants every scene shader reads. Matches <c>frame.glsl</c>.</summary>
internal struct FrameConstants
{
    public float4 ViewProjection0;
    public float4 ViewProjection1;
    public float4 ViewProjection2;
    public float4 ViewProjection3;
    public float4 CameraRight;
    public float4 CameraUp;
    public float4 CameraForward;
    public float4 Lens;
    public float4 SunDirection;
    public float4 SunColor;
    public float4 Dust;
    public float4 Drift;
    public float4 Grid;
    public float4 GridPlane;

    /// <summary>The viewer's own jump: drive kind, phase, progress through the phase.</summary>
    public float4 Ftl;
}

/// <summary>
/// The tone mapper's push constants: exposure, and the openings of jumps in view that it bends the scene around and
/// draws. Matches <c>tonemap.frag</c>. 128 bytes, the push constant space every device guarantees.
/// </summary>
internal struct CompositeConstants
{
    public const int MaxLenses = 4;

    /// <summary>Exposure, aspect ratio, time in seconds, number of lenses.</summary>
    public float4 Setup;

    /// <summary>The pilot's own jump: drive kind, stage, progress through the stage.</summary>
    public float4 Own;

    // Per lens: centre in NDC, radius when fully open in half screen heights, and a code (drive kind + 4 * stage,
    // + 16 for the pilot's own passage). Progress and seed go two lenses to a vector in Extra.
    public float4 Lens0;
    public float4 Lens1;
    public float4 Lens2;
    public float4 Lens3;
    public float4 Extra0;
    public float4 Extra1;

    /// <summary>Fills lens slot <paramref name="index"/>, 0 to <see cref="MaxLenses"/> - 1.</summary>
    public void SetLens(int index, float2 centre, float radius, float code, float progress, float seed)
    {
        var lens = new float4(centre.X, centre.Y, radius, code);
        switch (index)
        {
            case 0:
                Lens0 = lens;
                (Extra0.X, Extra0.Y) = (progress, seed);
                break;
            case 1:
                Lens1 = lens;
                (Extra0.Z, Extra0.W) = (progress, seed);
                break;
            case 2:
                Lens2 = lens;
                (Extra1.X, Extra1.Y) = (progress, seed);
                break;
            default:
                Lens3 = lens;
                (Extra1.Z, Extra1.W) = (progress, seed);
                break;
        }
    }
}

/// <summary>The mark of a jump seen from outside: where, how big, and which drive at which stage.</summary>
internal struct FtlMark
{
    public float4 PositionSize;

    /// <summary>Drive kind, phase, progress through the phase, and a per-hull seed.</summary>
    public float4 Params;
}

/// <summary>One drawn mesh: a camera-relative 3x4 transform and a colour. Alpha 0 is rock, 1 is hull plating.</summary>
internal struct MeshInstance
{
    public float4 Row0;
    public float4 Row1;
    public float4 Row2;
    public float4 Tint;
}

internal struct StarInstance
{
    /// <summary>Unit direction on the sky and size in pixels.</summary>
    public float4 DirectionSize;
    public float4 Color;
}

/// <summary>A run of instances that share a mesh.</summary>
internal struct DrawBatch
{
    public int Mesh;
    public int FirstInstance;
    public int InstanceCount;
}

/// <summary>
/// Draws the world: sky, stars, dust, rocks and hulls into a floating-point target, then tone-maps that onto the
/// screen. The work per frame is fixed apart from the number of mesh instances.
/// </summary>
internal sealed unsafe class SceneRenderer : IDisposable
{
    public const int MaxInstances = 16384;
    private const int StarCount = 7000;
    private const int DustCount = 1400;
    public const int MaxFtlMarks = 64;
    private const VkFormat ColorFormat = VkFormat.R16G16B16A16SFloat;
    private const VkFormat DepthFormat = VkFormat.D32SFloat;

    private readonly GraphicsDevice _gpu;
    private readonly DescriptorPoolEx _descriptorPool;
    private readonly DescriptorSetLayoutEx _frameLayout;
    private readonly DescriptorSetLayoutEx _sceneLayout;
    private readonly VkPipelineLayout _scenePipelineLayout;
    private readonly VkPipelineLayout _tonemapPipelineLayout;
    private readonly VkPipeline _backgroundPipeline;
    private readonly VkPipeline _starPipeline;
    private readonly VkPipeline _dustPipeline;
    private readonly VkPipeline _meshPipeline;
    private readonly VkPipeline _gridPipeline;
    private readonly VkPipeline _tonemapPipeline;
    private readonly VkPipeline _ftlMarkPipeline;
    private readonly GpuBuffer[] _ftlBuffers = new GpuBuffer[FrameRenderer.FramesInFlight];
    private readonly VkSampler _sampler;
    private readonly VkDescriptorSet _sceneSet;
    private readonly VkDescriptorSet[] _frameSets = new VkDescriptorSet[FrameRenderer.FramesInFlight];
    private readonly GpuBuffer[] _frameBuffers = new GpuBuffer[FrameRenderer.FramesInFlight];
    private readonly GpuBuffer[] _instanceBuffers = new GpuBuffer[FrameRenderer.FramesInFlight];
    private readonly GpuBuffer _starBuffer;
    private readonly GpuBuffer _vertexBuffer;
    private readonly GpuBuffer _indexBuffer;
    private readonly MeshRange[] _meshes;

    private VkImage _colorImage;
    private VmaAllocation _colorAllocation;
    private VkImageView _colorView;
    private VkImage _depthImage;
    private VmaAllocation _depthAllocation;
    private VkImageView _depthView;
    private VkExtent2D _extent;
    private CompositeConstants _composite;

    public SceneRenderer(GraphicsDevice gpu, Swapchain swapchain, IReadOnlyDictionary<string, byte[]> shaders, IReadOnlyList<MeshData> meshes)
    {
        _gpu = gpu;
        var device = gpu.Device;

        // Geometry: every mesh packed into one vertex buffer and one index buffer.
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        _meshes = new MeshRange[meshes.Count];
        for (var i = 0; i < meshes.Count; i++)
        {
            _meshes[i] = new MeshRange(indices.Count, meshes[i].Indices.Count, vertices.Count);
            vertices.AddRange(meshes[i].Vertices);
            indices.AddRange(meshes[i].Indices);
        }

        _vertexBuffer = GpuBuffer.CreateStatic<MeshVertex>(gpu, VkBufferUsageFlags.VertexBufferBit, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices));
        _indexBuffer = GpuBuffer.CreateStatic<uint>(gpu, VkBufferUsageFlags.IndexBufferBit, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
        _starBuffer = GpuBuffer.CreateStatic<StarInstance>(gpu, VkBufferUsageFlags.VertexBufferBit, GenerateStars());

        for (var i = 0; i < FrameRenderer.FramesInFlight; i++)
        {
            _frameBuffers[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.UniformBufferBit, sizeof(FrameConstants));
            _instanceBuffers[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.VertexBufferBit, sizeof(MeshInstance) * MaxInstances);
            _ftlBuffers[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.VertexBufferBit, sizeof(FtlMark) * MaxFtlMarks);
        }

        // Descriptors: set 0 of scene shaders is the frame constants; set 0 of the tone mapper is the scene image.
        Span<VkDescriptorPoolSize> poolSizes =
        [
            new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = FrameRenderer.FramesInFlight },
            new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1 },
        ];
        _descriptorPool = device.CreateDescriptorPool(
            new DescriptorPoolEx.CreateInfo { MaxSets = FrameRenderer.FramesInFlight + 1, PoolSizes = poolSizes },
            null
        );

        Span<VkDescriptorSetLayoutBinding> frameBindings =
        [
            new VkDescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = VkDescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            },
        ];
        _frameLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = frameBindings }, null);

        Span<VkDescriptorSetLayoutBinding> sceneBindings =
        [
            new VkDescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
        ];
        _sceneLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = sceneBindings }, null);

        _scenePipelineLayout = device.CreatePipelineLayout([_frameLayout.Handle], [], null);
        _tonemapPipelineLayout = device.CreatePipelineLayout(
            [_sceneLayout.Handle],
            [new VkPushConstantRange { StageFlags = VkShaderStageFlags.FragmentBit, Offset = (ByteSize32)0, Size = (ByteSize32)sizeof(CompositeConstants) }],
            null
        );

        for (var i = 0; i < FrameRenderer.FramesInFlight; i++)
        {
            _frameSets[i] = device.AllocateDescriptorSet(_descriptorPool, _frameLayout);
            var bufferInfo = new VkDescriptorBufferInfo
            {
                Buffer = _frameBuffers[i].Buffer,
                Offset = new ByteSize64(0),
                Range = new ByteSize64((ulong)sizeof(FrameConstants)),
            };
            device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet
                    {
                        DstSet = _frameSets[i],
                        DstBinding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.UniformBuffer,
                        BufferInfo = &bufferInfo,
                    },
                ],
                []
            );
        }

        _sceneSet = device.AllocateDescriptorSet(_descriptorPool, _sceneLayout);
        _sampler = device.CreateSampler(Presets.Sampler.SamplerLinearClamped, null);

        // Pipelines.
        var depthOff = new VkPipelineDepthStencilStateCreateInfo();
        // Reverse depth: nearer is greater. It keeps precision usable from a metre to the far side of a sector.
        var depthWrite = new VkPipelineDepthStencilStateCreateInfo
        {
            DepthTestEnable = true,
            DepthWriteEnable = true,
            DepthCompareOp = VkCompareOp.Greater,
        };
        var depthRead = new VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = true, DepthCompareOp = VkCompareOp.GreaterOrEqual };
        // Meshes wind counter-clockwise seen from outside, and the projection keeps the image upright, so that is
        // also how front faces appear on screen.
        var cullBack = new VkPipelineRasterizationStateCreateInfo
        {
            PolygonMode = VkPolygonMode.Fill,
            CullMode = VkCullModeFlags.BackBit,
            FrontFace = VkFrontFace.CounterClockwise,
            LineWidth = 1f,
        };

        // The scene's alpha channel is a mask for the jump lenses, not coverage. The sky and solid surfaces write
        // zero, light added on top leaves alpha alone, and a rift's or wormhole's mark writes one where the
        // composite pass may bend the view. The presets' additive blend scales by destination alpha, so these
        // passes use their own blend states.
        const VkColorComponentFlags rgb = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit;
        var additiveAttachment = new VkPipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = VkBlendFactor.One,
            DstColorBlendFactor = VkBlendFactor.One,
            ColorBlendOp = VkBlendOp.Add,
            SrcAlphaBlendFactor = VkBlendFactor.Zero,
            DstAlphaBlendFactor = VkBlendFactor.One,
            AlphaBlendOp = VkBlendOp.Add,
            ColorWriteMask = rgb,
        };
        var additive = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &additiveAttachment };
        var translucentAttachment = additiveAttachment with
        {
            SrcColorBlendFactor = VkBlendFactor.SrcAlpha,
            DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
        };
        var translucent = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &translucentAttachment };
        var markAttachment = additiveAttachment with
        {
            SrcAlphaBlendFactor = VkBlendFactor.One,
            ColorWriteMask = rgb | VkColorComponentFlags.ABit,
        };
        var markBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &markAttachment };

        var noInput = new VertexInput(0, 0);
        var starInput = new VertexInput(1, 2).AddBindingWithAttributes<StarInstance>(0, 0, VkVertexInputRate.Instance);
        var meshInput = VertexInput.For<MeshVertex, MeshInstance>();

        _backgroundPipeline = ScenePipeline(Module(shaders, "fullscreen.vert"), Module(shaders, "background.frag"), noInput, Presets.Rasterization.Fill.CullNone, &depthOff, Presets.BlendState.BlendNone);
        _starPipeline = ScenePipeline(Module(shaders, "stars.vert"), Module(shaders, "stars.frag"), starInput, Presets.Rasterization.Fill.CullNone, &depthOff, &additive);
        _dustPipeline = ScenePipeline(Module(shaders, "dust.vert"), Module(shaders, "dust.frag"), noInput, Presets.Rasterization.Fill.CullNone, &depthRead, &additive);
        _meshPipeline = ScenePipeline(Module(shaders, "mesh.vert"), Module(shaders, "mesh.frag"), meshInput, &cullBack, &depthWrite, Presets.BlendState.BlendNone);
        _gridPipeline = ScenePipeline(Module(shaders, "grid.vert"), Module(shaders, "grid.frag"), noInput, Presets.Rasterization.Fill.CullNone, &depthRead, &translucent);
        _tonemapPipeline = Pipeline(Module(shaders, "fullscreen.vert"), Module(shaders, "tonemap.frag"), noInput, Presets.Rasterization.Fill.CullNone, &depthOff, Presets.BlendState.BlendNone, _tonemapPipelineLayout, swapchain.RenderPass);

        var markInput = new VertexInput(1, 2).AddBindingWithAttributes<FtlMark>(0, 0, VkVertexInputRate.Instance);
        _ftlMarkPipeline = ScenePipeline(Module(shaders, "ftl_mark.vert"), Module(shaders, "ftl_mark.frag"), markInput, Presets.Rasterization.Fill.CullNone, &depthRead, &markBlend);

        CreateTargets(swapchain.Extent);
    }

    /// <summary>The jump-mark memory for this frame; say how many were filled when calling <see cref="Render"/>.</summary>
    public Span<FtlMark> FtlMarks(int frameIndex) => new((void*)_ftlBuffers[frameIndex].Mapped, MaxFtlMarks);

    /// <summary>Recreates the render targets after the window changed size. The device must be idle.</summary>
    public void Resize(VkExtent2D extent)
    {
        if (extent.Width == _extent.Width && extent.Height == _extent.Height)
            return;
        DestroyTargets();
        CreateTargets(extent);
    }

    /// <summary>The instance memory for this frame. Fill it, then describe it to <see cref="Render"/> as batches.</summary>
    public Span<MeshInstance> Instances(int frameIndex) => new((void*)_instanceBuffers[frameIndex].Mapped, MaxInstances);

    /// <summary>Records the scene into the floating-point target. Call before the present pass is opened.</summary>
    public void Render(CommandBuffer commands, int frameIndex, in FrameConstants constants, in CompositeConstants composite, ReadOnlySpan<DrawBatch> batches, bool drawGrid, int ftlMarks)
    {
        _composite = composite;
        *(FrameConstants*)_frameBuffers[frameIndex].Mapped = constants;

        Transition(commands, _colorImage, VkImageAspectFlags.ColorBit, VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal, VkAccessFlags.ColorAttachmentWriteBit, VkPipelineStageFlags.ColorAttachmentOutputBit);
        Transition(commands, _depthImage, VkImageAspectFlags.DepthBit, VkImageLayout.Undefined, VkImageLayout.DepthAttachmentOptimal, VkAccessFlags.DepthStencilAttachmentWriteBit, VkPipelineStageFlags.EarlyFragmentTestsBit);

        var color = new VkRenderingAttachmentInfo
        {
            ImageView = _colorView,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.DontCare,
            StoreOp = VkAttachmentStoreOp.Store,
        };
        var depth = new VkRenderingAttachmentInfo
        {
            ImageView = _depthView,
            ImageLayout = VkImageLayout.DepthAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.DontCare,
            ClearValue = new VkClearValue { DepthStencil = new VkClearDepthStencilValue { Depth = 0f } },
        };
        commands.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D(_extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &color,
                DepthAttachment = &depth,
            }
        );
        SetViewport(commands, _extent);

        var set = _frameSets[frameIndex];
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _scenePipelineLayout, 0, new ReadOnlySpan<VkDescriptorSet>(in set), []);

        commands.BindPipeline(VkPipelineBindPoint.Graphics, _backgroundPipeline);
        commands.Draw(3, 1, 0, 0);

        var zero = new ByteSize64(0);
        var stars = _starBuffer.Buffer;
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _starPipeline);
        commands.BindVertexBuffers(0, new ReadOnlySpan<VkBuffer>(in stars), new ReadOnlySpan<ByteSize64>(in zero));
        commands.Draw(6, StarCount, 0, 0);

        if (!batches.IsEmpty)
        {
            Span<VkBuffer> buffers = [_vertexBuffer.Buffer, _instanceBuffers[frameIndex].Buffer];
            Span<ByteSize64> offsets = [zero, zero];
            commands.BindPipeline(VkPipelineBindPoint.Graphics, _meshPipeline);
            commands.BindVertexBuffers(0, buffers, offsets);
            commands.BindIndexBuffer(_indexBuffer.Buffer, zero, VkIndexType.UInt32);
            foreach (var batch in batches)
            {
                var mesh = _meshes[batch.Mesh];
                commands.DrawIndexed(mesh.IndexCount, batch.InstanceCount, mesh.FirstIndex, mesh.VertexOffset, batch.FirstInstance);
            }
        }

        commands.BindPipeline(VkPipelineBindPoint.Graphics, _dustPipeline);
        commands.Draw(6, DustCount, 0, 0);

        if (drawGrid)
        {
            commands.BindPipeline(VkPipelineBindPoint.Graphics, _gridPipeline);
            commands.Draw(6, 1, 0, 0);
        }

        if (ftlMarks > 0)
        {
            var marks = _ftlBuffers[frameIndex].Buffer;
            commands.BindPipeline(VkPipelineBindPoint.Graphics, _ftlMarkPipeline);
            commands.BindVertexBuffers(0, new ReadOnlySpan<VkBuffer>(in marks), new ReadOnlySpan<ByteSize64>(in zero));
            commands.Draw(6, ftlMarks, 0, 0);
        }

        commands.EndRendering();
        Transition(commands, _colorImage, VkImageAspectFlags.ColorBit, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.FragmentShaderBit);
    }

    /// <summary>
    /// Draws the tone-mapped scene to the screen, bent by the jump lenses given to the last <see cref="Render"/>.
    /// Call inside the present pass, before the interface.
    /// </summary>
    public void Present(CommandBuffer commands, float exposure)
    {
        SetViewport(commands, _extent);
        var push = _composite;
        push.Setup.X = exposure;
        var set = _sceneSet;
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _tonemapPipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _tonemapPipelineLayout, 0, new ReadOnlySpan<VkDescriptorSet>(in set), []);
        commands.PushConstants(_tonemapPipelineLayout, VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(&push, sizeof(CompositeConstants)));
        commands.Draw(3, 1, 0, 0);
    }

    public void Dispose()
    {
        var device = _gpu.Device;
        device.WaitIdle();
        DestroyTargets();
        device.DestroySampler(_sampler, null);
        foreach (var pipeline in (ReadOnlySpan<VkPipeline>)[_backgroundPipeline, _starPipeline, _dustPipeline, _meshPipeline, _gridPipeline, _tonemapPipeline, _ftlMarkPipeline])
            device.DestroyPipeline(pipeline, null);
        device.DestroyPipelineLayout(_scenePipelineLayout, null);
        device.DestroyPipelineLayout(_tonemapPipelineLayout, null);
        _descriptorPool.Dispose();
        _frameLayout.Dispose();
        _sceneLayout.Dispose();
        for (var i = 0; i < FrameRenderer.FramesInFlight; i++)
        {
            _frameBuffers[i].Destroy(_gpu);
            _instanceBuffers[i].Destroy(_gpu);
            _ftlBuffers[i].Destroy(_gpu);
        }

        var (stars, vertices, indices) = (_starBuffer, _vertexBuffer, _indexBuffer);
        stars.Destroy(_gpu);
        vertices.Destroy(_gpu);
        indices.Destroy(_gpu);
    }

    private VkShaderModule Module(IReadOnlyDictionary<string, byte[]> shaders, string name) =>
        ShaderModuleExtensions.FromSpirv(_gpu.Device, shaders[name], null);

    private VkPipeline ScenePipeline(
        VkShaderModule vertex,
        VkShaderModule fragment,
        VertexInput input,
        VkPipelineRasterizationStateCreateInfo* rasterization,
        VkPipelineDepthStencilStateCreateInfo* depth,
        VkPipelineColorBlendStateCreateInfo* blend
    ) => Pipeline(vertex, fragment, input, rasterization, depth, blend, _scenePipelineLayout, VkRenderPass.NullHandle);

    private VkPipeline Pipeline(
        VkShaderModule vertex,
        VkShaderModule fragment,
        VertexInput input,
        VkPipelineRasterizationStateCreateInfo* rasterization,
        VkPipelineDepthStencilStateCreateInfo* depth,
        VkPipelineColorBlendStateCreateInfo* blend,
        VkPipelineLayout layout,
        VkRenderPass renderPass
    )
    {
        // The stages object owns both modules and destroys them once the pipeline exists.
        using var stages = new ShaderStages(_gpu.Device, [Presets.ShaderStage.Vertex(vertex), Presets.ShaderStage.Fragment(fragment)], null);

        var viewportState = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
        var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };
        var colorFormat = ColorFormat;
        var rendering = new VkPipelineRenderingCreateInfo
        {
            ColorAttachmentCount = 1,
            ColorAttachmentFormats = &colorFormat,
            DepthAttachmentFormat = DepthFormat,
        };

        return _gpu.Device.CreateGraphicsPipeline(
            VkPipelineCache.NullHandle,
            new VkGraphicsPipelineCreateInfo
            {
                Next = renderPass.IsNull() ? &rendering : null,
                StageCount = stages.Count,
                Stages = stages,
                VertexInputState = input,
                InputAssemblyState = Presets.InputAssembly.TriangleList,
                ViewportState = &viewportState,
                RasterizationState = rasterization,
                MultisampleState = Presets.Multisample.None,
                DepthStencilState = depth,
                ColorBlendState = blend,
                DynamicState = &dynamicState,
                Layout = layout,
                RenderPass = renderPass,
            },
            null
        );
    }

    private void CreateTargets(VkExtent2D extent)
    {
        _extent = extent;
        (_colorImage, _colorAllocation, _colorView) = CreateTarget(ColorFormat, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        (_depthImage, _depthAllocation, _depthView) = CreateTarget(DepthFormat, VkImageUsageFlags.DepthStencilAttachmentBit, VkImageAspectFlags.DepthBit);

        var imageInfo = new VkDescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = _colorView,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        _gpu.Device.UpdateDescriptorSets(
            [
                new VkWriteDescriptorSet
                {
                    DstSet = _sceneSet,
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &imageInfo,
                },
            ],
            []
        );
    }

    private (VkImage, VmaAllocation, VkImageView) CreateTarget(VkFormat format, VkImageUsageFlags usage, VkImageAspectFlags aspect)
    {
        _gpu.Allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D(_extent.Width, _extent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = usage,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto },
            out var image,
            out var allocation,
            out _
        );
        var view = _gpu.Device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = aspect,
                    LevelCount = 1,
                    LayerCount = 1,
                },
            },
            null
        );
        return (image, allocation, view);
    }

    private void DestroyTargets()
    {
        _gpu.Device.DestroyImageView(_colorView, null);
        _gpu.Device.DestroyImageView(_depthView, null);
        _gpu.Allocator.DestroyImage(_colorImage, _colorAllocation);
        _gpu.Allocator.DestroyImage(_depthImage, _depthAllocation);
    }

    private static void SetViewport(CommandBuffer commands, VkExtent2D extent)
    {
        var viewport = new VkViewport
        {
            Width = extent.Width,
            Height = extent.Height,
            MaxDepth = 1,
        };
        var scissor = new VkRect2D(extent);
        commands.SetViewport(0, new ReadOnlySpan<VkViewport>(in viewport));
        commands.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
    }

    private static void Transition(
        CommandBuffer commands,
        VkImage image,
        VkImageAspectFlags aspect,
        VkImageLayout from,
        VkImageLayout to,
        VkAccessFlags access,
        VkPipelineStageFlags stage
    )
    {
        var barrier = new VkImageMemoryBarrier
        {
            SrcAccessMask = from == VkImageLayout.Undefined ? VkAccessFlags.None : VkAccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = access,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = image,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = aspect,
                LevelCount = 1,
                LayerCount = 1,
            },
        };
        commands.PipelineBarrier(
            from == VkImageLayout.Undefined ? VkPipelineStageFlags.TopOfPipeBit : VkPipelineStageFlags.ColorAttachmentOutputBit,
            stage,
            VkDependencyFlags.None,
            [],
            [],
            new ReadOnlySpan<VkImageMemoryBarrier>(in barrier)
        );
    }

    /// <summary>The fixed star field: the same sky everywhere in the galaxy, since the stars are far beyond it.</summary>
    private static StarInstance[] GenerateStars()
    {
        var random = new Random(0x5741525A);
        var stars = new StarInstance[StarCount];
        for (var i = 0; i < stars.Length; i++)
        {
            // Uniform on the sphere, then pulled toward the galactic plane so the band reads as dense.
            var z = (float)random.NextDouble() * 2f - 1f;
            var angle = (float)random.NextDouble() * MathF.Tau;
            var r = MathF.Sqrt(1f - z * z);
            var direction = new float3(r * MathF.Cos(angle), z * (random.NextDouble() < 0.55 ? 0.35f : 1f), r * MathF.Sin(angle));
            var length = MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);

            // Most stars are faint; a few are bright. Colour runs from red through white to blue.
            var magnitude = MathF.Pow((float)random.NextDouble(), 7f);
            var brightness = 0.25f + 6f * magnitude;
            var temperature = (float)random.NextDouble();
            var color = temperature < 0.5f
                ? new float3(1f, 0.72f + 0.5f * temperature, 0.55f + 0.8f * temperature)
                : new float3(1.45f - 0.9f * temperature, 1.2f - 0.4f * temperature, 1f);

            stars[i] = new StarInstance
            {
                DirectionSize = new float4(direction.X / length, direction.Y / length, direction.Z / length, 1.6f + 2.4f * magnitude),
                Color = new float4(color.X * brightness, color.Y * brightness, color.Z * brightness, 1f),
            };
        }

        return stars;
    }
}
