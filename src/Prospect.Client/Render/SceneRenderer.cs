using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Prospect.Game.World;

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

    /// <summary>The viewer's own jump: drive kind, stage, progress through the stage, seed.</summary>
    public float4 Ftl;

    /// <summary>The jump's axis (the hull's nose) in world space, and how far the tunnel has streamed past.</summary>
    public float4 FtlAxis;

    /// <summary>
    /// Up across the axis, for the rift's window and the tunnel's coordinates; then hyperspace tunnel cover, or how
    /// far the camera is past the current rift or wormhole opening.
    /// </summary>
    public float4 FtlUp;

    /// <summary>The opening the hull flies into: camera-relative centre and radius in metres (0 for none).</summary>
    public float4 FtlEntry;

    /// <summary>The opening the hull comes out of: camera-relative centre and radius in metres (0 for none).</summary>
    public float4 FtlExit;

    /// <summary>
    /// How far hyperspace stretches the stars; how far the entry and the exit are open, 0 to 1; how much of the
    /// tunnel's or throat's light falls on the pilot's hull.
    /// </summary>
    public float4 FtlShape;

    /// <summary>
    /// The rifts and wormholes of jumps seen from outside, in the world: other hulls', and the pilot's own from the
    /// command view.
    /// </summary>
    public FtlOpenings Openings;

    /// <summary>Per followed jump, the plane its opening cuts its own hull at; see <see cref="FtlClips"/>.</summary>
    public FtlClips Clips;
}

/// <summary>Four vectors per opening for <see cref="FrameConstants.Openings"/>; matches <c>frame.glsl</c>.</summary>
[InlineArray(CompositeConstants.MaxLenses * 4)]
internal struct FtlOpenings
{
    private float4 _element;

    /// <summary>
    /// Fills opening <paramref name="index"/>, 0 to <see cref="CompositeConstants.MaxLenses"/> - 1: where it is
    /// relative to the camera and its radius when fully open in metres, the axis it faces along (the hull's nose)
    /// and up across it, the drive kind, how far it is open from 0 to 1, and a per-hull seed.
    /// </summary>
    public void Set(int index, Vector3 centre, float radius, Vector3 axis, Vector3 up, FtlKind kind, float open, float seed)
    {
        var at = index * 4;
        this[at] = new float4(centre.X, centre.Y, centre.Z, radius);
        this[at + 1] = new float4(axis.X, axis.Y, axis.Z, 0);
        this[at + 2] = new float4(up.X, up.Y, up.Z, 0);
        this[at + 3] = new float4((float)kind, open, seed, 0);
    }
}

/// <summary>
/// Two vectors per followed jump for <see cref="FrameConstants.Clips"/>, by passage slot; matches <c>frame.glsl</c>.
/// A hull going through a rift or a wormhole is cut where it is past the opening, in the mesh pass, so neither the
/// scene nor its depth ever hold the part that is gone, and nothing else is cut with it.
/// </summary>
[InlineArray(Prospect.Client.World.HullVisuals.MaxPassages * 2)]
internal struct FtlClips
{
    private float4 _element;

    /// <summary>
    /// Fills slot <paramref name="index"/>: a point on the cutting plane relative to the camera, and the plane's
    /// normal toward the side that is hidden. A zero normal cuts nothing.
    /// </summary>
    public void Set(int index, Vector3 point, Vector3 hidden)
    {
        this[index * 2] = new float4(point.X, point.Y, point.Z, 0);
        this[index * 2 + 1] = new float4(hidden.X, hidden.Y, hidden.Z, 0);
    }
}

/// <summary>
/// The tone mapper's push constants. The openings it bends the scene around and draws are in the frame constants,
/// which it also binds, with the pilot's own jump. Matches <c>tonemap.frag</c>.
/// </summary>
internal struct CompositeConstants
{
    /// <summary>The most openings of jumps seen from outside that are drawn in one frame.</summary>
    public const int MaxLenses = 4;

    /// <summary>Exposure, aspect ratio, time in seconds, number of openings.</summary>
    public float4 Setup;
}

/// <summary>The mark of a jump seen from outside: where, how big, and which drive at which stage.</summary>
internal struct FtlMark
{
    public float4 PositionSize;

    /// <summary>Drive kind, phase, progress through the phase, and a per-hull seed.</summary>
    public float4 Params;

    /// <summary>
    /// The hull's nose in world space, for light that runs along the jump; w is an arrival's half size in hull radii,
    /// which grows its pulse, or 0 for every other mark.
    /// </summary>
    public float4 Axis;
}

/// <summary>
/// One drawn mesh: a camera-relative 3x4 transform and a colour. Alpha 0 is rock, 1 is hull plating, 2 the pilot's
/// own hull; a hull cut by a jump's opening adds four times its passage slot plus one.
/// </summary>
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
    /// <summary>
    /// The most hulls, blocks and asteroids drawn in one frame. A sector can hold thousands of asteroids, but most
    /// of those in view are too far to draw, so this is several dense sectors on screen at once.
    /// </summary>
    public const int MaxInstances = 32768;
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
    private readonly VkPipeline _hullPipeline;
    private readonly VkPipeline _gridPipeline;
    private readonly VkPipeline _tonemapPipeline;
    private readonly VkPipeline _ftlMarkPipeline;
    private readonly GpuBuffer[] _ftlBuffers = new GpuBuffer[FrameRenderer.FramesInFlight];
    private readonly VkSampler _sampler;
    private readonly VkSampler _pointSampler;
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
    // On frames with a jump lens, the scene is copied as it was before anything solid was drawn (the sky) and before
    // any hull was (the sky and the asteroids, with their depth). A lens bends the view toward what is behind its
    // opening; where something solid in front of the opening hides that, the bent ray reads what was there before it
    // was drawn, instead of imaging the thing in front. The scene's own depth is kept on those frames too, so the
    // composite pass can tell, pixel by pixel, what is in front of an opening and what is behind it.
    private VkImage _skyImage;
    private VmaAllocation _skyAllocation;
    private VkImageView _skyView;
    private VkImage _rocksImage;
    private VmaAllocation _rocksAllocation;
    private VkImageView _rocksView;
    private VkImage _rocksDepthImage;
    private VmaAllocation _rocksDepthAllocation;
    private VkImageView _rocksDepthView;
    private bool _fallbackReady;
    private VkImage _depthImage;
    private VmaAllocation _depthAllocation;
    private VkImageView _depthView;
    private VkExtent2D _extent;
    private CompositeConstants _composite;
    private int _frameIndex;

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

        // Descriptors: set 0 of every shader is the frame constants; set 1 of the tone mapper is the scene image, the
        // scene before anything solid and before any hull was drawn, the scene's depth and the depth before hulls.
        Span<VkDescriptorPoolSize> poolSizes =
        [
            new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = FrameRenderer.FramesInFlight },
            new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 5 },
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
            new VkDescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
            new VkDescriptorSetLayoutBinding
            {
                Binding = 2,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
            new VkDescriptorSetLayoutBinding
            {
                Binding = 3,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
            new VkDescriptorSetLayoutBinding
            {
                Binding = 4,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
        ];
        _sceneLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = sceneBindings }, null);

        _scenePipelineLayout = device.CreatePipelineLayout([_frameLayout.Handle], [], null);
        _tonemapPipelineLayout = device.CreatePipelineLayout(
            [_frameLayout.Handle, _sceneLayout.Handle],
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
        _pointSampler = device.CreateSampler(Presets.Sampler.SamplerPointClamped, null);

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

        // The scene's alpha channel says what is there for the jump lenses, not coverage: 0 sky, 0.25 an asteroid,
        // 0.375 a hull, 0.5 the pilot's own hull. Light added on top leaves alpha alone. The presets' additive blend
        // scales by destination alpha, so these passes use their own blend states.
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

        var noInput = new VertexInput(0, 0);
        var starInput = new VertexInput(1, 2).AddBindingWithAttributes<StarInstance>(0, 0, VkVertexInputRate.Instance);
        var meshInput = VertexInput.For<MeshVertex, MeshInstance>();

        _backgroundPipeline = ScenePipeline(Module(shaders, "fullscreen.vert"), Module(shaders, "background.frag"), noInput, Presets.Rasterization.Fill.CullNone, &depthOff, Presets.BlendState.BlendNone);
        _starPipeline = ScenePipeline(Module(shaders, "stars.vert"), Module(shaders, "stars.frag"), starInput, Presets.Rasterization.Fill.CullNone, &depthOff, &additive);
        _dustPipeline = ScenePipeline(Module(shaders, "dust.vert"), Module(shaders, "dust.frag"), noInput, Presets.Rasterization.Fill.CullNone, &depthRead, &additive);
        _meshPipeline = ScenePipeline(Module(shaders, "mesh.vert"), Module(shaders, "mesh.frag"), meshInput, &cullBack, &depthWrite, Presets.BlendState.BlendNone);
        // Hulls are drawn with the cut of a jump's opening switched on. Asteroids are not, so the discard it needs
        // never costs them their early depth test.
        var cutHulls = 1u;
        var cutEntry = new VkSpecializationMapEntry { ConstantID = 0, Offset = (ByteSize32)0, Size = new ByteSize64(sizeof(uint)) };
        var cutInfo = new VkSpecializationInfo { MapEntryCount = 1, MapEntries = &cutEntry, DataSize = new ByteSize64(sizeof(uint)), Data = &cutHulls };
        _hullPipeline = ScenePipeline(Module(shaders, "mesh.vert"), Module(shaders, "mesh.frag"), meshInput, &cullBack, &depthWrite, Presets.BlendState.BlendNone, &cutInfo);
        _gridPipeline = ScenePipeline(Module(shaders, "grid.vert"), Module(shaders, "grid.frag"), noInput, Presets.Rasterization.Fill.CullNone, &depthRead, &translucent);
        _tonemapPipeline = Pipeline(Module(shaders, "fullscreen.vert"), Module(shaders, "tonemap.frag"), noInput, Presets.Rasterization.Fill.CullNone, &depthOff, Presets.BlendState.BlendNone, _tonemapPipelineLayout, swapchain.RenderPass);

        var markInput = new VertexInput(1, 3).AddBindingWithAttributes<FtlMark>(0, 0, VkVertexInputRate.Instance);
        _ftlMarkPipeline = ScenePipeline(Module(shaders, "ftl_mark.vert"), Module(shaders, "ftl_mark.frag"), markInput, Presets.Rasterization.Fill.CullNone, &depthRead, &additive);

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

    /// <summary>
    /// Records the scene into the floating-point target: asteroids, then hulls. Call before the present pass is
    /// opened. With <paramref name="lensing"/> (a rift or wormhole is open this frame) what was there before anything
    /// solid, and before any hull, is kept for the lenses as well.
    /// </summary>
    public void Render(
        CommandBuffer commands,
        int frameIndex,
        in FrameConstants constants,
        in CompositeConstants composite,
        ReadOnlySpan<DrawBatch> rocks,
        ReadOnlySpan<DrawBatch> hulls,
        bool drawGrid,
        int ftlMarks,
        bool lensing
    )
    {
        _composite = composite;
        _frameIndex = frameIndex;
        *(FrameConstants*)_frameBuffers[frameIndex].Mapped = constants;

        // The scene's colour and depth are shared by both frame slots: last frame's tone mapper read them, and its
        // reads must be done before they are written again, whatever is in them now.
        Barrier(commands, _colorImage, VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal, VkAccessFlags.ShaderReadBit, VkAccessFlags.ColorAttachmentWriteBit, VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.ColorAttachmentOutputBit);
        Barrier(
            commands,
            _depthImage,
            VkImageLayout.Undefined,
            VkImageLayout.DepthAttachmentOptimal,
            VkAccessFlags.ShaderReadBit,
            VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
            VkPipelineStageFlags.FragmentShaderBit,
            VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            VkImageAspectFlags.DepthBit
        );
        if (!lensing && !_fallbackReady)
        {
            Transition(commands, _skyImage, VkImageAspectFlags.ColorBit, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.FragmentShaderBit);
            Transition(commands, _rocksImage, VkImageAspectFlags.ColorBit, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.FragmentShaderBit);
            Transition(commands, _rocksDepthImage, VkImageAspectFlags.DepthBit, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.FragmentShaderBit);
        }

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
            StoreOp = lensing ? VkAttachmentStoreOp.Store : VkAttachmentStoreOp.DontCare,
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

        if (lensing)
            KeepScene(commands, _skyImage, false, ref color, ref depth);
        DrawMeshes(commands, frameIndex, rocks, _meshPipeline);
        if (lensing)
            KeepScene(commands, _rocksImage, true, ref color, ref depth);
        DrawMeshes(commands, frameIndex, hulls, _hullPipeline);

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
        // The tone mapper reads the depth on frames with a lens; on others it is bound but not read.
        Barrier(
            commands,
            _depthImage,
            VkImageLayout.DepthAttachmentOptimal,
            VkImageLayout.ShaderReadOnlyOptimal,
            VkAccessFlags.DepthStencilAttachmentWriteBit,
            VkAccessFlags.ShaderReadBit,
            VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit,
            VkPipelineStageFlags.FragmentShaderBit,
            VkImageAspectFlags.DepthBit
        );
        _fallbackReady = true;
    }

    private void DrawMeshes(CommandBuffer commands, int frameIndex, ReadOnlySpan<DrawBatch> batches, VkPipeline pipeline)
    {
        if (batches.IsEmpty)
            return;
        var zero = new ByteSize64(0);
        Span<VkBuffer> buffers = [_vertexBuffer.Buffer, _instanceBuffers[frameIndex].Buffer];
        Span<ByteSize64> offsets = [zero, zero];
        commands.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        commands.BindVertexBuffers(0, buffers, offsets);
        commands.BindIndexBuffer(_indexBuffer.Buffer, zero, VkIndexType.UInt32);
        foreach (var batch in batches)
        {
            var mesh = _meshes[batch.Mesh];
            commands.DrawIndexed(mesh.IndexCount, batch.InstanceCount, mesh.FirstIndex, mesh.VertexOffset, batch.FirstInstance);
        }
    }

    /// <summary>
    /// Copies the scene as drawn so far into <paramref name="target"/> for the lenses, and its depth too when asked,
    /// then carries on drawing into it with its colour and depth kept.
    /// </summary>
    private void KeepScene(CommandBuffer commands, VkImage target, bool withDepth, ref VkRenderingAttachmentInfo color, ref VkRenderingAttachmentInfo depth)
    {
        commands.EndRendering();
        Barrier(commands, _colorImage, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.TransferSrcOptimal, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.TransferBit);
        // These images are shared by both frame slots, so reuse must wait for the previous tone mapper's reads.
        Barrier(commands, target, _fallbackReady ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
            _fallbackReady ? VkAccessFlags.ShaderReadBit : VkAccessFlags.None, VkAccessFlags.TransferWriteBit,
            _fallbackReady ? VkPipelineStageFlags.FragmentShaderBit : VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit);
        var layers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
        var region = new VkImageCopy
        {
            SrcSubresource = layers,
            DstSubresource = layers,
            Extent = new VkExtent3D(_extent.Width, _extent.Height, 1),
        };
        commands.CopyImage(_colorImage, VkImageLayout.TransferSrcOptimal, target, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageCopy>(in region));
        Barrier(commands, _colorImage, VkImageLayout.TransferSrcOptimal, VkImageLayout.ColorAttachmentOptimal, VkAccessFlags.TransferReadBit, VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit, VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ColorAttachmentOutputBit);
        Barrier(commands, target, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit);
        const VkPipelineStageFlags depthTests = VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit;
        const VkAccessFlags depthAccess = VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit;
        if (withDepth)
        {
            Barrier(commands, _depthImage, VkImageLayout.DepthAttachmentOptimal, VkImageLayout.TransferSrcOptimal, VkAccessFlags.DepthStencilAttachmentWriteBit, VkAccessFlags.TransferReadBit, depthTests, VkPipelineStageFlags.TransferBit, VkImageAspectFlags.DepthBit);
            Barrier(commands, _rocksDepthImage, _fallbackReady ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
                _fallbackReady ? VkAccessFlags.ShaderReadBit : VkAccessFlags.None, VkAccessFlags.TransferWriteBit,
                _fallbackReady ? VkPipelineStageFlags.FragmentShaderBit : VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, VkImageAspectFlags.DepthBit);
            var depthLayers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.DepthBit, LayerCount = 1 };
            var depthRegion = region with { SrcSubresource = depthLayers, DstSubresource = depthLayers };
            commands.CopyImage(_depthImage, VkImageLayout.TransferSrcOptimal, _rocksDepthImage, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageCopy>(in depthRegion));
            Barrier(commands, _rocksDepthImage, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkImageAspectFlags.DepthBit);
            Barrier(commands, _depthImage, VkImageLayout.TransferSrcOptimal, VkImageLayout.DepthAttachmentOptimal, VkAccessFlags.TransferReadBit, depthAccess, VkPipelineStageFlags.TransferBit, depthTests, VkImageAspectFlags.DepthBit);
        }
        else
        {
            // The depth written so far is read and written again by the rest of the scene.
            Barrier(commands, _depthImage, VkImageLayout.DepthAttachmentOptimal, VkImageLayout.DepthAttachmentOptimal, VkAccessFlags.DepthStencilAttachmentWriteBit, depthAccess, depthTests, depthTests, VkImageAspectFlags.DepthBit);
        }

        color.LoadOp = VkAttachmentLoadOp.Load;
        depth.LoadOp = VkAttachmentLoadOp.Load;
        var colorInfo = color;
        var depthInfo = depth;
        commands.BeginRendering(
            new VkRenderingInfo
            {
                RenderArea = new VkRect2D(_extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                ColorAttachments = &colorInfo,
                DepthAttachment = &depthInfo,
            }
        );
        SetViewport(commands, _extent);
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
        Span<VkDescriptorSet> sets = [_frameSets[_frameIndex], _sceneSet];
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _tonemapPipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _tonemapPipelineLayout, 0, sets, []);
        commands.PushConstants(_tonemapPipelineLayout, VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(&push, sizeof(CompositeConstants)));
        commands.Draw(3, 1, 0, 0);
    }

    public void Dispose()
    {
        var device = _gpu.Device;
        device.WaitIdle();
        DestroyTargets();
        device.DestroySampler(_sampler, null);
        device.DestroySampler(_pointSampler, null);
        foreach (var pipeline in (ReadOnlySpan<VkPipeline>)[_backgroundPipeline, _starPipeline, _dustPipeline, _meshPipeline, _hullPipeline, _gridPipeline, _tonemapPipeline, _ftlMarkPipeline])
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
        VkPipelineColorBlendStateCreateInfo* blend,
        VkSpecializationInfo* fragmentConstants = null
    ) => Pipeline(vertex, fragment, input, rasterization, depth, blend, _scenePipelineLayout, VkRenderPass.NullHandle, fragmentConstants);

    private VkPipeline Pipeline(
        VkShaderModule vertex,
        VkShaderModule fragment,
        VertexInput input,
        VkPipelineRasterizationStateCreateInfo* rasterization,
        VkPipelineDepthStencilStateCreateInfo* depth,
        VkPipelineColorBlendStateCreateInfo* blend,
        VkPipelineLayout layout,
        VkRenderPass renderPass,
        VkSpecializationInfo* fragmentConstants = null
    )
    {
        // The stages object owns both modules and destroys them once the pipeline exists.
        var fragmentStage = Presets.ShaderStage.Fragment(fragment);
        fragmentStage.SpecializationInfo = fragmentConstants;
        using var stages = new ShaderStages(_gpu.Device, [Presets.ShaderStage.Vertex(vertex), fragmentStage], null);

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
        _fallbackReady = false;
        (_colorImage, _colorAllocation, _colorView) = CreateTarget(ColorFormat, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferSrcBit, VkImageAspectFlags.ColorBit);
        (_depthImage, _depthAllocation, _depthView) = CreateTarget(DepthFormat, VkImageUsageFlags.DepthStencilAttachmentBit | VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferSrcBit, VkImageAspectFlags.DepthBit);

        (_skyImage, _skyAllocation, _skyView) = CreateTarget(ColorFormat, VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        (_rocksImage, _rocksAllocation, _rocksView) = CreateTarget(ColorFormat, VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        (_rocksDepthImage, _rocksDepthAllocation, _rocksDepthView) = CreateTarget(DepthFormat, VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.DepthBit);

        var imageInfo = new VkDescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = _colorView,
            ImageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };
        var skyInfo = imageInfo with { ImageView = _skyView };
        var rocksInfo = imageInfo with { ImageView = _rocksView };
        // Depth is read exactly, never filtered.
        var depthInfo = imageInfo with { ImageView = _depthView, Sampler = _pointSampler };
        var rocksDepthInfo = depthInfo with { ImageView = _rocksDepthView };
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
                new VkWriteDescriptorSet
                {
                    DstSet = _sceneSet,
                    DstBinding = 1,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &skyInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _sceneSet,
                    DstBinding = 2,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &rocksInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _sceneSet,
                    DstBinding = 3,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &depthInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _sceneSet,
                    DstBinding = 4,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &rocksDepthInfo,
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
        _gpu.Device.DestroyImageView(_skyView, null);
        _gpu.Device.DestroyImageView(_rocksView, null);
        _gpu.Device.DestroyImageView(_rocksDepthView, null);
        _gpu.Allocator.DestroyImage(_colorImage, _colorAllocation);
        _gpu.Allocator.DestroyImage(_skyImage, _skyAllocation);
        _gpu.Allocator.DestroyImage(_rocksImage, _rocksAllocation);
        _gpu.Allocator.DestroyImage(_rocksDepthImage, _rocksDepthAllocation);
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
    ) =>
        Barrier(
            commands,
            image,
            from,
            to,
            from == VkImageLayout.Undefined ? VkAccessFlags.None : VkAccessFlags.ColorAttachmentWriteBit,
            access,
            from == VkImageLayout.Undefined ? VkPipelineStageFlags.TopOfPipeBit : VkPipelineStageFlags.ColorAttachmentOutputBit,
            stage,
            aspect
        );

    private static void Barrier(
        CommandBuffer commands,
        VkImage image,
        VkImageLayout from,
        VkImageLayout to,
        VkAccessFlags fromAccess,
        VkAccessFlags toAccess,
        VkPipelineStageFlags fromStage,
        VkPipelineStageFlags toStage,
        VkImageAspectFlags aspect = VkImageAspectFlags.ColorBit
    )
    {
        var barrier = new VkImageMemoryBarrier
        {
            SrcAccessMask = fromAccess,
            DstAccessMask = toAccess,
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
        commands.PipelineBarrier(fromStage, toStage, VkDependencyFlags.None, [], [], new ReadOnlySpan<VkImageMemoryBarrier>(in barrier));
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
