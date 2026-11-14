using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Prospect.Client.World;
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

    /// <summary>Deferred gas, matching the final GasData in frame.glsl.</summary>
    public GasConstants Gas;

    /// <summary>The second cloud of each overlapping cold and normal pair queued this frame; see <see cref="GasConstants.KindCode"/>.</summary>
    public GasPartners GasPartners;

    /// <summary>One vector per lightning cloud queued this frame; see <see cref="GasFlashes"/>.</summary>
    public GasFlashes GasFlashes;

    /// <summary>Three vectors per bolt drawn this frame; see <see cref="GasBoltHeads"/>.</summary>
    public GasBoltHeads GasBoltHeads;

    /// <summary>Two vectors per bolt segment; see <see cref="GasBoltSegments"/>.</summary>
    public GasBoltSegments GasBoltSegments;

    /// <summary>Cold glints: brightness, cell edge in metres, share of cells that glint, glint radius in metres.</summary>
    public float4 GasCold;

    /// <summary>Lightning flash colour, and in w the flash's reach in x half-extents.</summary>
    public float4 GasFlashLight;

    /// <summary>Lightning bolt colour, and in w the bolt's width in metres.</summary>
    public float4 GasBoltLight;

    /// <summary>Bolts: glow width in metres, glow amount, how far a bolt lights the gas in metres, tip brightness.</summary>
    public float4 GasBoltShape;

    /// <summary>X is the most a deep stroke's light reaches, in metres.</summary>
    public float4 GasFlashReach;

    /// <summary>
    /// X is the time in seconds for glints to twinkle by; y multiplies flashes at the middle and far levels; z the cold
    /// halo's strength; w how many bolts <see cref="GasBoltHeads"/> holds.
    /// </summary>
    public float4 GasKindTime;
}

/// <summary>The second cloud of each overlapping pair, <see cref="Capacity"/> at most; matches <c>frame.glsl</c>.</summary>
[InlineArray(Capacity)]
internal struct GasPartners
{
    /// <summary>The most overlapping pairs a frame draws together; a pair past it is left out whole, never split.</summary>
    public const int Capacity = 8;

    private GasConstants _element;
}

/// <summary>
/// Per lightning cloud, its deep stroke: its place in box units and its light (0: none). <see cref="Capacity"/>
/// clouds at most; matches <c>frame.glsl</c>.
/// </summary>
[InlineArray(Capacity)]
internal struct GasFlashes
{
    /// <summary>The most lightning clouds a frame flashes; any further one is drawn without its flashes.</summary>
    public const int Capacity = 8;

    private float4 _element;
}

/// <summary>
/// Per bolt, three vectors: the centre of its bounds relative to the camera and their radius; its first segment, its
/// segment count, how far along its path in metres it has grown, and its cloud's flash slot; the light of its main
/// channel, of its forks, and of what it sheds on the gas. Matches <c>frame.glsl</c>.
/// </summary>
[InlineArray(World.LightningBolts.MaxBolts * 3)]
internal struct GasBoltHeads
{
    private float4 _element;
}

/// <summary>
/// Per bolt segment, two vectors: its start relative to the camera and the path length from the bolt's root to it,
/// then its end and its fork depth (0 the main channel). Matches <c>frame.glsl</c>.
/// </summary>
[InlineArray(World.LightningBolts.MaxBolts * World.LightningBolts.MaxSegments * 2)]
internal struct GasBoltSegments
{
    private float4 _element;
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
    private readonly VkPipelineLayout _pulsePipelineLayout;
    private readonly VkPipeline _pulsePipeline;
    private ScanPulseConstants _pulseQueued;
    private ScanPulseConstants _pulse;
    private bool _pulseQueuedOn;
    private bool _pulseOn;
    private readonly DescriptorSetLayoutEx _pulseLayout;
    private readonly GpuBuffer[] _pulseWaveBuffers = new GpuBuffer[FrameRenderer.FramesInFlight];
    private readonly VkDescriptorSet[] _pulseSets = new VkDescriptorSet[FrameRenderer.FramesInFlight];

    // Shields: their own pipeline and per-frame vertex and instance memory, and the scene as it was just before
    // they were drawn, colour and depth, copied into images of their own that nothing else reads or writes.
    private readonly DescriptorSetLayoutEx _shieldLayout;
    private readonly VkPipelineLayout _shieldPipelineLayout;
    private readonly VkPipeline _gasPipeline;
    private readonly VkPipeline _gasSkyPipeline;
    private readonly VkPipeline _gasScenePipeline;
    // The scene's own depth as the middle and far gas levels read it while it is attached read-only.
    private readonly VkDescriptorSet _gasDepthSet;
    private readonly GasNoiseTexture? _gasNoise;
    private readonly GasQueue _gas = new();
    private readonly VkPipeline _shieldPipeline;
    private readonly VkPipeline _shieldMaskPipeline;
    private ShieldLookConstants _shieldLook;
    private readonly VkDescriptorSet _shieldSet;
    // At most 48 MB of triangle vertices per frame slot, independent of catalogue size.
    private readonly int DamageVertices = CollisionDetail.Load().FrameVertices;
    private readonly GpuBuffer[] _damageVertices = new GpuBuffer[FrameRenderer.FramesInFlight];
    private readonly GpuBuffer[] _damageInstances = new GpuBuffer[FrameRenderer.FramesInFlight];
    private readonly MeshRange[] _damageRanges = new MeshRange[65];
    private int _damageCount, _damageUsed;
    private readonly GpuBuffer[] _shieldVertexBuffers = new GpuBuffer[FrameRenderer.FramesInFlight];
    private readonly GpuBuffer[] _shieldIndexBuffers = new GpuBuffer[FrameRenderer.FramesInFlight];
    private readonly GpuBuffer[] _shieldInstanceBuffers = new GpuBuffer[FrameRenderer.FramesInFlight];
    private readonly ShieldDraw[] _shieldDraws = new ShieldDraw[ShieldVisuals.MaxShields];
    private int _shieldCount;
    private VkImage _behindImage;
    private VmaAllocation _behindAllocation;
    private VkImageView _behindView;
    private VkImage _behindDepthImage;
    private VmaAllocation _behindDepthAllocation;
    private VkImageView _behindDepthView;
    private bool _behindReady;
    // The scene just before the pilot's own shield, on frames where their hyperspace tunnel covers the view. The
    // tone mapper reads it (set 1, binding 5) only at the own shield's pixels on such frames; it is moved to the
    // layout the tone mapper reads before any frame uses it, so its descriptor is always valid.
    private VkImage _ownBehindImage;
    private VmaAllocation _ownBehindAllocation;
    private VkImageView _ownBehindView;
    private bool _ownBehindReady;
    // The primary cloud marched at half resolution (gas_half.frag): RGB its light, alpha its transmission. Written on
    // frames whose primary it holds, read by the ordinary gas pass (set 1, binding 2 of the shield layout) or by the tone
    // mapper (set 1, binding 8), and statically bound by both, so it is moved to the layout they read before any frame
    // uses it. Shared by both frame slots like the scene images: a frame writing it waits for the last frame's reads.
    private VkImage _gasHalfImage;
    private VmaAllocation _gasHalfAllocation;
    private VkImageView _gasHalfView;
    private VkExtent2D _gasHalfExtent;
    private bool _gasHalfReady;
    private readonly VkPipeline _gasHalfPipeline;
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
    private MeshRange[] _shipMeshes = [];
    private GpuBuffer _shipVertices;
    private GpuBuffer _shipIndices;

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

    public SceneRenderer(GraphicsDevice gpu, Swapchain swapchain, IReadOnlyDictionary<string, byte[]> shaders, IReadOnlyList<MeshData> meshes, bool gasFixtures = false)
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

        // Buffers made here are freed again if one of them cannot be made: nothing has been submitted that uses them,
        // and destroying a buffer never made is a no-op.
        try
        {
            _vertexBuffer = GpuBuffer.CreateStatic<MeshVertex>(gpu, VkBufferUsageFlags.VertexBufferBit, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices));
            _indexBuffer = GpuBuffer.CreateStatic<uint>(gpu, VkBufferUsageFlags.IndexBufferBit, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
            _starBuffer = GpuBuffer.CreateStatic<StarInstance>(gpu, VkBufferUsageFlags.VertexBufferBit, GenerateStars());

            for (var i = 0; i < FrameRenderer.FramesInFlight; i++)
            {
                _frameBuffers[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.UniformBufferBit, sizeof(FrameConstants));
                _instanceBuffers[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.VertexBufferBit, sizeof(MeshInstance) * MaxInstances);
                _ftlBuffers[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.VertexBufferBit, sizeof(FtlMark) * MaxFtlMarks);
                _damageVertices[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.VertexBufferBit, sizeof(MeshVertex) * DamageVertices);
                _damageInstances[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.VertexBufferBit, sizeof(MeshInstance) * 65);
                _shieldVertexBuffers[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.VertexBufferBit, sizeof(MeshVertex) * ShieldVisuals.MaxVertices);
                _shieldIndexBuffers[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.IndexBufferBit, sizeof(ushort) * ShieldVisuals.MaxIndices);
                _shieldInstanceBuffers[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.VertexBufferBit, sizeof(ShieldInstance) * ShieldVisuals.MaxShields);
                _pulseWaveBuffers[i] = GpuBuffer.CreateMapped(gpu, VkBufferUsageFlags.UniformBufferBit, sizeof(ScanWaveObject) * ScanWaves.Capacity);
            }
        }
        catch
        {
            DestroyBuffers();
            throw;
        }

        // Set 0 shares frame constants and immutable gas noise; set 1 of the tone mapper is the scene image, the
        // scene before anything solid and before any hull was drawn, the scene's depth and the depth before hulls.
        Span<VkDescriptorPoolSize> poolSizes =
        [
            new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 2 * FrameRenderer.FramesInFlight },
            new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 9 + 3 + 3 + FrameRenderer.FramesInFlight },
        ];
        _descriptorPool = device.CreateDescriptorPool(
            new DescriptorPoolEx.CreateInfo { MaxSets = 2 * FrameRenderer.FramesInFlight + 3, PoolSizes = poolSizes },
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
            new VkDescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
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
            new VkDescriptorSetLayoutBinding
            {
                Binding = 5,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
            new VkDescriptorSetLayoutBinding
            {
                Binding = 6,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
            new VkDescriptorSetLayoutBinding
            {
                Binding = 7,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
            new VkDescriptorSetLayoutBinding
            {
                Binding = 8,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
        ];
        _sceneLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = sceneBindings }, null);

        _scenePipelineLayout = device.CreatePipelineLayout([_frameLayout.Handle], [], null);

        // Set 1 of the shield shader: what was behind the shields, colour and depth. The gas passes share the layout;
        // binding 2, the half-resolution march of the primary cloud, is read by the ordinary gas pass alone.
        Span<VkDescriptorSetLayoutBinding> shieldBindings =
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
        ];
        _shieldLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = shieldBindings }, null);
        _shieldPipelineLayout = device.CreatePipelineLayout(
            [_frameLayout.Handle, _shieldLayout.Handle],
            // Shared by the shield passes (ShieldLookConstants) and the ordinary gas pass (GasConstants).
            [new VkPushConstantRange { StageFlags = VkShaderStageFlags.FragmentBit, Offset = (ByteSize32)0, Size = (ByteSize32)Math.Max(sizeof(ShieldLookConstants), sizeof(GasConstants)) }],
            null
        );
        _tonemapPipelineLayout = device.CreatePipelineLayout(
            [_frameLayout.Handle, _sceneLayout.Handle],
            [new VkPushConstantRange { StageFlags = VkShaderStageFlags.FragmentBit, Offset = (ByteSize32)0, Size = (ByteSize32)sizeof(CompositeConstants) }],
            null
        );
        // The scan pulse reads the tone mapper's sets (the camera, and of the scene only its depth) with its own push range.
        // Set 2 of the scan pulse: this frame's surface ripples (ScanWaves).
        Span<VkDescriptorSetLayoutBinding> pulseBindings =
        [
            new VkDescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = VkDescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            },
        ];
        _pulseLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = pulseBindings }, null);
        _pulsePipelineLayout = device.CreatePipelineLayout(
            [_frameLayout.Handle, _sceneLayout.Handle, _pulseLayout.Handle],
            [new VkPushConstantRange { StageFlags = VkShaderStageFlags.FragmentBit, Offset = (ByteSize32)0, Size = (ByteSize32)sizeof(ScanPulseConstants) }],
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
        for (var i = 0; i < FrameRenderer.FramesInFlight; i++)
        {
            _pulseSets[i] = device.AllocateDescriptorSet(_descriptorPool, _pulseLayout);
            var waveInfo = new VkDescriptorBufferInfo
            {
                Buffer = _pulseWaveBuffers[i].Buffer,
                Offset = new ByteSize64(0),
                Range = new ByteSize64((ulong)(sizeof(ScanWaveObject) * ScanWaves.Capacity)),
            };
            device.UpdateDescriptorSets(
                [new VkWriteDescriptorSet { DstSet = _pulseSets[i], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &waveInfo }],
                []
            );
        }
        _shieldSet = device.AllocateDescriptorSet(_descriptorPool, _shieldLayout);
        _gasDepthSet = device.AllocateDescriptorSet(_descriptorPool, _shieldLayout);
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
        // The gas fixtures run a second copy of composition; only a muted --gas-fixture run compiles them in, which
        // halves the work the driver does on the tone mapper the first time a build starts.
        var fixtures = gasFixtures ? 1u : 0u;
        var fixtureEntry = new VkSpecializationMapEntry { ConstantID = 2, Offset = (ByteSize32)0, Size = new ByteSize64(sizeof(uint)) };
        var fixtureInfo = new VkSpecializationInfo { MapEntryCount = 1, MapEntries = &fixtureEntry, DataSize = new ByteSize64(sizeof(uint)), Data = &fixtures };
        _tonemapPipeline = Pipeline(Module(shaders, "fullscreen.vert"), Module(shaders, "tonemap.frag"), noInput, Presets.Rasterization.Fill.CullNone, &depthOff, Presets.BlendState.BlendNone, _tonemapPipelineLayout, swapchain.RenderPass, &fixtureInfo);

        // Shields are drawn last, in two passes whose results do not depend on the order of the shells. The first adds
        // each shell's light and bend, tested against the scene's depth but writing none, so overlapping shells all
        // add up. The second writes only depth and the lens mask, raised by maximum: the jump lenses then treat a
        // shield as a solid thing in front, and the pilot's own hull under it keeps its higher mark.
        var shieldAttachment = new VkPipelineColorBlendAttachmentState
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
        var shieldBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &shieldAttachment };
        var maskAttachment = shieldAttachment with
        {
            SrcAlphaBlendFactor = VkBlendFactor.One,
            DstAlphaBlendFactor = VkBlendFactor.One,
            AlphaBlendOp = VkBlendOp.Max,
            ColorWriteMask = VkColorComponentFlags.ABit,
        };
        var maskBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &maskAttachment };
        var shieldInput = VertexInput.For<MeshVertex, ShieldInstance>();
        _shieldPipeline = Pipeline(Module(shaders, "shield.vert"), Module(shaders, "shield.frag"), shieldInput, &cullBack, &depthRead, &shieldBlend, _shieldPipelineLayout, VkRenderPass.NullHandle);
        var maskOnly = 1u;
        var maskEntry = new VkSpecializationMapEntry { ConstantID = 1, Offset = (ByteSize32)0, Size = new ByteSize64(sizeof(uint)) };
        var maskInfo = new VkSpecializationInfo { MapEntryCount = 1, MapEntries = &maskEntry, DataSize = new ByteSize64(sizeof(uint)), Data = &maskOnly };
        _shieldMaskPipeline = Pipeline(Module(shaders, "shield.vert"), Module(shaders, "shield.frag"), shieldInput, &cullBack, &depthWrite, &maskBlend, _shieldPipelineLayout, VkRenderPass.NullHandle, &maskInfo);

        var markInput = new VertexInput(1, 3).AddBindingWithAttributes<FtlMark>(0, 0, VkVertexInputRate.Instance);
        _ftlMarkPipeline = ScenePipeline(Module(shaders, "ftl_mark.vert"), Module(shaders, "ftl_mark.frag"), markInput, Presets.Rasterization.Fill.CullNone, &depthRead, &additive);

        // The scan pulse is light added to the displayed image after tone mapping, in the present pass, under the
        // interface. Its alpha is left as the tone mapper wrote it.
        var pulseAttachment = additiveAttachment with { SrcAlphaBlendFactor = VkBlendFactor.Zero, DstAlphaBlendFactor = VkBlendFactor.One };
        var pulseBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &pulseAttachment };
        _pulsePipeline = Pipeline(Module(shaders, "fullscreen.vert"), Module(shaders, "scan_pulse.frag"), noInput, Presets.Rasterization.Fill.CullNone, &depthOff, &pulseBlend, _pulsePipelineLayout, swapchain.RenderPass);

        CreateTargets(swapchain.Extent);
        // The immutable gas lattice is created last so every later failure can retire the completed renderer.
        try
        {
            _gasNoise = GasNoiseTexture.Create(gpu);
            var noiseInfo = new VkDescriptorImageInfo { Sampler = _gasNoise.Sampler, ImageView = _gasNoise.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            for (var i = 0; i < FrameRenderer.FramesInFlight; i++)
                device.UpdateDescriptorSets([new VkWriteDescriptorSet
                {
                    DstSet = _frameSets[i], DstBinding = 1, DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &noiseInfo,
                }], []);
            var gasAttachment = translucentAttachment with { SrcColorBlendFactor = VkBlendFactor.One };
            var gasBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &gasAttachment };
            var gasVertex = Module(shaders, "fullscreen.vert");
            VkShaderModule gasFragment;
            try
            {
                gasFragment = Module(shaders, "gas.frag");
            }
            catch
            {
                // Pipeline owns both modules only after both have been acquired.
                _gpu.Device.DestroyShaderModule(gasVertex, null);
                throw;
            }
            _gasPipeline = Pipeline(gasVertex, gasFragment, noInput, Presets.Rasterization.Fill.CullNone, &depthOff, &gasBlend, _shieldPipelineLayout, VkRenderPass.NullHandle);
            // Far clouds in the sky: the same blend and push range, and of the layout's sets only the frame's.
            var skyVertex = Module(shaders, "fullscreen.vert");
            VkShaderModule skyFragment;
            try
            {
                skyFragment = Module(shaders, "gas_sky.frag");
            }
            catch
            {
                _gpu.Device.DestroyShaderModule(skyVertex, null);
                throw;
            }
            _gasSkyPipeline = Pipeline(skyVertex, skyFragment, noInput, Presets.Rasterization.Fill.CullNone, &depthOff, &gasBlend, _shieldPipelineLayout, VkRenderPass.NullHandle);
            // Middle and far clouds in the scene: depth is neither tested nor written, only read by the shader.
            var sceneVertex = Module(shaders, "fullscreen.vert");
            VkShaderModule sceneFragment;
            try
            {
                sceneFragment = Module(shaders, "gas_scene.frag");
            }
            catch
            {
                _gpu.Device.DestroyShaderModule(sceneVertex, null);
                throw;
            }
            _gasScenePipeline = Pipeline(sceneVertex, sceneFragment, noInput, Presets.Rasterization.Fill.CullNone, &depthOff, &gasBlend, _shieldPipelineLayout, VkRenderPass.NullHandle);
            // The primary cloud at half resolution: its own colour target, no depth, written over.
            var halfVertex = Module(shaders, "fullscreen.vert");
            VkShaderModule halfFragment;
            try
            {
                halfFragment = Module(shaders, "gas_half.frag");
            }
            catch
            {
                _gpu.Device.DestroyShaderModule(halfVertex, null);
                throw;
            }
            _gasHalfPipeline = Pipeline(halfVertex, halfFragment, noInput, Presets.Rasterization.Fill.CullNone, &depthOff, Presets.BlendState.BlendNone, _shieldPipelineLayout, VkRenderPass.NullHandle, depthAttachment: false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The jump-mark memory for this frame; say how many were filled when calling <see cref="Render"/>.</summary>
    public Span<FtlMark> FtlMarks(int frameIndex) => new((void*)_ftlBuffers[frameIndex].Mapped, MaxFtlMarks);

    /// <summary>How many times the render targets have been rebuilt since they were made.</summary>
    public int TargetRebuilds { get; private set; }

    /// <summary>
    /// Recreates the render targets after the window changed size, or, with <paramref name="always"/>, at the size
    /// they have too (the resizing benchmark does, where a window manager will not resize the window). The device
    /// must be idle. Runs in the frame loop: nothing here may take managed memory.
    /// </summary>
    public void Resize(VkExtent2D extent, bool always = false)
    {
        if (!always && extent.Width == _extent.Width && extent.Height == _extent.Height)
            return;
        TargetRebuilds++;
        DestroyTargets();
        CreateTargets(extent);
    }

    /// <summary>This frame's shield vertex and instance memory, and where each shell's vertices start.</summary>
    public Span<MeshVertex> ShieldVertices(int frameIndex) => new((void*)_shieldVertexBuffers[frameIndex].Mapped, ShieldVisuals.MaxVertices);

    /// <summary>This frame's shield indices, 16 bits each, counted from each shell's first vertex.</summary>
    public Span<ushort> ShieldIndices(int frameIndex) => new((void*)_shieldIndexBuffers[frameIndex].Mapped, ShieldVisuals.MaxIndices);

    public Span<ShieldInstance> ShieldInstances(int frameIndex) => new((void*)_shieldInstanceBuffers[frameIndex].Mapped, ShieldVisuals.MaxShields);

    public ShieldDraw[] ShieldDraws => _shieldDraws;

    /// <summary>
    /// How many of <see cref="ShieldDraws"/> the next <see cref="Render"/> draws, after the hulls. Applies to that
    /// one frame only.
    /// </summary>
    public void QueueShields(int count, in ShieldLookConstants look)
    {
        _shieldCount = Math.Clamp(count, 0, _shieldDraws.Length);
        _shieldLook = look;
    }

    /// <summary>
    /// The scan pulse the next <see cref="Render"/> and <see cref="Present"/> draw, for that one frame only. A frame
    /// without one draws no pulse pass and keeps no depth for it.
    /// </summary>
    public void QueuePulse(in ScanPulseConstants pulse)
    {
        _pulseQueued = pulse;
        _pulseQueuedOn = true;
    }

    /// <summary>
    /// This frame's surface ripples for the scan pulse; fill the first <c>Gas.Z</c> of the queued constants before
    /// <see cref="Render"/>. Each frame slot has its own, so this frame's writes never reach a frame still drawing.
    /// </summary>
    public Span<ScanWaveObject> PulseWaves(int frameIndex) => new((void*)_pulseWaveBuffers[frameIndex].Mapped, ScanWaves.Capacity);

    /// <summary>For muted capture diagnostics: whether the last frame drew the scan pulse.</summary>
    public bool LastPulse => _pulseOn;

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
        _pulse = _pulseQueued;
        _pulseOn = _pulseQueuedOn;
        _pulseQueuedOn = false;
        // Composition takes over one cloud, the primary. Every other draw is in the scene before the shields and
        // the lenses see it, as part of what is behind them.
        var deferredGas = _gas.HasPrimary && (lensing || _shieldCount > 0);
        var skyGas = _gas.SkyCount;
        var sceneGas = _gas.Count > skyGas || (_gas.HasPrimary && !deferredGas);
        LastGasMode = (deferredGas, _shieldCount, _gas.HasPrimary);
        var halfGas = HalfMarches(constants);
        LastGasHalf = halfGas;
        var frame = constants;
        frame.Gas = deferredGas ? _gas.Primary : default;
        if (deferredGas && halfGas)
            frame.Gas.Budget.W = GasConstants.HalfMarched;
        if (constants.Gas.Budget.Z > 0)
        {
            frame.Gas.Budget.Z = constants.Gas.Budget.Z;
            frame.Gas.Budget.W = _shieldCount;
        }
        *(FrameConstants*)_frameBuffers[frameIndex].Mapped = frame;

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
        // These views are statically bound by the tone mapper even on frames that do not sample gas.
        if (!_behindReady)
        {
            Transition(commands, _behindImage, VkImageAspectFlags.ColorBit, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.FragmentShaderBit);
            Transition(commands, _behindDepthImage, VkImageAspectFlags.DepthBit, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.FragmentShaderBit);
            _behindReady = true;
        }
        if (!_gasHalfReady)
        {
            Transition(commands, _gasHalfImage, VkImageAspectFlags.ColorBit, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.FragmentShaderBit);
            _gasHalfReady = true;
        }
        if (!_ownBehindReady)
        {
            Transition(commands, _ownBehindImage, VkImageAspectFlags.ColorBit, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.FragmentShaderBit);
            _ownBehindReady = true;
        }

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
            // Kept when the lenses read it, when the shields copy it before they are drawn, or for the scan pulse.
            StoreOp = lensing || _shieldCount > 0 || sceneGas || deferredGas || _pulseOn ? VkAttachmentStoreOp.Store : VkAttachmentStoreOp.DontCare,
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

        // Far clouds are part of the sky: over the stars, under everything solid, and in the sky the lenses keep.
        if (skyGas > 0)
        {
            DrawSkyGas(commands, frameIndex, skyGas);
            commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _scenePipelineLayout, 0, new ReadOnlySpan<VkDescriptorSet>(in set), []);
        }

        if (lensing)
            KeepScene(commands, _skyImage, false, ref color, ref depth);
        DrawMeshes(commands, frameIndex, rocks, _meshPipeline);
        if (lensing)
            KeepScene(commands, _rocksImage, true, ref color, ref depth);
        DrawMeshes(commands, frameIndex, hulls, _hullPipeline);
        if (_damageCount > 0)
        {
            Span<VkBuffer> damageBuffers = [_damageVertices[frameIndex].Buffer, _damageInstances[frameIndex].Buffer];
            Span<ByteSize64> damageOffsets = [zero, zero];
            commands.BindPipeline(VkPipelineBindPoint.Graphics, _hullPipeline);
            commands.BindVertexBuffers(0, damageBuffers, damageOffsets);
            for (var i = 0; i < _damageCount; i++)
                commands.Draw(_damageRanges[i].IndexCount, 1, _damageRanges[i].FirstIndex, i);
        }

        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _scenePipelineLayout, 0, new ReadOnlySpan<VkDescriptorSet>(in set), []);
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

        // Both gas paths include these overlays, so unrelated portals cannot change their attenuation.
        if (sceneGas) DrawGas(commands, frameIndex, skyGas, !deferredGas, halfGas, ref color, ref depth);

        // Last, so the dust, the command grid and the jump marks are in what the shields bend and stay seen
        // through their transparent margins.
        // Deferred composition needs the opaque scene without the primary cloud even when no shield is drawn.
        if (deferredGas && _shieldCount == 0) KeepBehindShields(commands, ref color, ref depth);
        DrawShields(commands, frameIndex, constants, ref color, ref depth);
        var primary = _gas.Primary;
        _gas.Begin();

        commands.EndRendering();
        // Composition reads the primary at half resolution; the opaque scene it marches to was kept above.
        if (deferredGas && halfGas) MarchHalf(commands, frameIndex, primary);
        Transition(commands, _colorImage, VkImageAspectFlags.ColorBit, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.FragmentShaderBit);
        // The tone mapper reads depth for lenses and deferred gas/shields; otherwise it is only bound.
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

    /// <summary>Installs an admission catalogue before frame recording. Both old frame slots finish before retirement.</summary>
    public void ReplaceShipMeshes(IReadOnlyList<MeshData> meshes)
    {
        var vertices = new List<MeshVertex>();
        var indices = new List<uint>();
        var ranges = new MeshRange[meshes.Count];
        for (var i = 0; i < meshes.Count; i++)
        {
            ranges[i] = new MeshRange(indices.Count, meshes[i].Indices.Count, vertices.Count);
            vertices.AddRange(meshes[i].Vertices);
            indices.AddRange(meshes[i].Indices);
        }
        // Admission is the only swap boundary. No recorded or submitted frame can still borrow old buffers.
        _gpu.Device.WaitIdle();
        var newVertices = GpuBuffer.CreateStatic<MeshVertex>(_gpu, VkBufferUsageFlags.VertexBufferBit, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(vertices));
        GpuBuffer newIndices;
        try
        {
            newIndices = GpuBuffer.CreateStatic<uint>(_gpu, VkBufferUsageFlags.IndexBufferBit, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
        }
        catch
        {
            newVertices.Destroy(_gpu);
            throw;
        }
        _shipVertices.Destroy(_gpu);
        _shipIndices.Destroy(_gpu);
        _shipVertices = newVertices;
        _shipIndices = newIndices;
        _shipMeshes = ranges;
    }

    /// <summary>Called after the current frame fence, before any dynamic triangle uploads.</summary>
    public int DamageDraws => _damageCount - (SparkVertices > 0 ? 1 : 0);
    public int SparkVertices { get; private set; }
    public int DynamicBytes => _damageUsed * sizeof(MeshVertex) + _damageCount * sizeof(MeshInstance);
    public void BeginDamage() { _damageCount = 0; _damageUsed = 0; SparkVertices = 0; }

    /// <summary>Refuses the whole mesh before touching mapped memory when its budget is exhausted.</summary>
    public bool AddDamage(int frame, DamageMesh mesh, in MeshInstance instance, float fade)
    {
        var n = mesh.Dented.Length;
        if (_damageCount >= 64 || n > DamageVertices - _damageUsed) return false;
        var destination = new Span<MeshVertex>((void*)_damageVertices[frame].Mapped, DamageVertices).Slice(_damageUsed, n);
        mesh.CopyTo(destination, fade);
        FinishDamage(frame, n, instance);
        return true;
    }

    public void AddSparks(int frame, ReadOnlySpan<MeshVertex> vertices)
    {
        if (vertices.IsEmpty || vertices.Length > DamageVertices - _damageUsed || _damageCount == 65) return;
        SparkVertices = vertices.Length;
        vertices.CopyTo(new Span<MeshVertex>((void*)_damageVertices[frame].Mapped, DamageVertices).Slice(_damageUsed));
        var instance = WorldView.MeshTransform(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One, new System.Numerics.Vector3(7, 2, 0.2f), -1, 1);
        FinishDamage(frame, vertices.Length, instance);
    }

    private void FinishDamage(int frame, int count, in MeshInstance instance)
    {
        new Span<MeshInstance>((void*)_damageInstances[frame].Mapped, 65)[_damageCount] = instance;
        _damageRanges[_damageCount++] = new MeshRange(_damageUsed, count, 0);
        _damageUsed += count;
    }

    private void DrawMeshes(CommandBuffer commands, int frameIndex, ReadOnlySpan<DrawBatch> batches, VkPipeline pipeline)
    {
        if (batches.IsEmpty)
            return;
        var zero = new ByteSize64(0);
        Span<VkBuffer> buffers = [_vertexBuffer.Buffer, _instanceBuffers[frameIndex].Buffer];
        Span<ByteSize64> offsets = [zero, zero];
        commands.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        int bound = -1;
        foreach (var batch in batches)
        {
            var ship = batch.Mesh < WorldView.ShipMeshSlots;
            var kind = ship ? 1 : 0;
            if (bound != kind)
            {
                buffers[0] = ship ? _shipVertices.Buffer : _vertexBuffer.Buffer;
                commands.BindVertexBuffers(0, buffers, offsets);
                commands.BindIndexBuffer(ship ? _shipIndices.Buffer : _indexBuffer.Buffer, zero, VkIndexType.UInt32);
                bound = kind;
            }
            var mesh = ship ? _shipMeshes[batch.Mesh] : _meshes[batch.Mesh];
            commands.DrawIndexed(mesh.IndexCount, batch.InstanceCount, mesh.FirstIndex, mesh.VertexOffset, batch.FirstInstance);
        }
    }

    /// <summary>
    /// Draws this frame's shields over everything else in the scene. What is behind them is copied once first, so
    /// each can bend it; where two overlap, both bend that one copy, an approximation that holds for faint shells.
    /// </summary>
    private void DrawShields(CommandBuffer commands, int frameIndex, in FrameConstants constants, ref VkRenderingAttachmentInfo color, ref VkRenderingAttachmentInfo depth)
    {
        var count = _shieldCount;
        _shieldCount = 0;
        if (count == 0)
            return;

        Span<int> order = stackalloc int[ShieldVisuals.MaxShields];
        var others = ShieldComposite.Order(_shieldDraws.AsSpan(0, count), order);
        var ownCopy = ShieldComposite.NeedsOwnCopy(constants, others < count);

        KeepBehindShields(commands, ref color, ref depth);
        BindShields(commands, frameIndex);
        if (!ownCopy)
        {
            DrawShieldPass(commands, _shieldPipeline, order[..count]);
            DrawShieldPass(commands, _shieldMaskPipeline, order[..count]);
            return;
        }

        // The pilot's own hyperspace: everything but their own shield first, then the copy the tone mapper tunnels,
        // then their own shield, whose light alone is what the scene gains over that copy.
        DrawShieldPass(commands, _shieldPipeline, order[..others]);
        DrawShieldPass(commands, _shieldMaskPipeline, order[..others]);
        KeepBeforeOwnShield(commands, ref color, ref depth);
        BindShields(commands, frameIndex);
        DrawShieldPass(commands, _shieldPipeline, order[others..count]);
        DrawShieldPass(commands, _shieldMaskPipeline, order[others..count]);
    }

    /// <summary>Starts the bounded cloud list for this frame, holding at most <paramref name="limit"/> draws.</summary>
    public void BeginGas(int limit = GasQueue.Capacity) => _gas.Begin(limit);

    /// <summary>For muted capture diagnostics: whether the last frame deferred gas, and how many shields it queued.</summary>
    public (bool Deferred, int Shields, bool HasGas) LastGasMode { get; private set; }

    /// <summary>The whole view, as the bounds of a draw that may show anywhere.</summary>
    public static readonly float4 WholeView = new(0, 0, 1, 1);

    /// <summary>
    /// Queues one level of detail of a visible cloud; see <see cref="GasQueue"/> for the order. Nothing of it shows
    /// outside <paramref name="bounds"/>: left, top, right and bottom as shares of the view.
    /// </summary>
    public void AddGas(in GasConstants gas, in float4 bounds, float distance) => _gas.Add(gas, bounds, distance);

    /// <summary>Limits drawing to a draw's bounds, a pixel wider; false when they are wholly off screen.</summary>
    private bool GasScissor(CommandBuffer commands, in float4 bounds)
    {
        var left = Math.Clamp((int)MathF.Floor(bounds.X * _extent.Width) - 1, 0, (int)_extent.Width);
        var top = Math.Clamp((int)MathF.Floor(bounds.Y * _extent.Height) - 1, 0, (int)_extent.Height);
        var right = Math.Clamp((int)MathF.Ceiling(bounds.Z * _extent.Width) + 1, 0, (int)_extent.Width);
        var bottom = Math.Clamp((int)MathF.Ceiling(bounds.W * _extent.Height) + 1, 0, (int)_extent.Height);
        if (right <= left || bottom <= top) return false;
        var scissor = new VkRect2D { Offset = new VkOffset2D { X = left, Y = top }, Extent = new VkExtent2D(right - left, bottom - top) };
        commands.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
        return true;
    }

    private void DrawGasQuad(CommandBuffer commands, in GasConstants gas, in float4 bounds)
    {
        if (!GasScissor(commands, bounds)) return;
        var pushed = gas;
        commands.PushConstants(_shieldPipelineLayout, VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(&pushed, sizeof(GasConstants)));
        commands.Draw(3, 1, 0, 0);
    }

    /// <summary>The first <paramref name="count"/> draws, all at the far level, straight over the sky and the stars.</summary>
    private void DrawSkyGas(CommandBuffer commands, int frameIndex, int count)
    {
        var set = _frameSets[frameIndex];
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _gasSkyPipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _shieldPipelineLayout, 0, new ReadOnlySpan<VkDescriptorSet>(in set), []);
        for (var i = 0; i < count; i++)
            DrawGasQuad(commands, _gas[i], _gas.Bounds(i));
        SetViewport(commands, _extent);
    }

    /// <summary>
    /// The draws from <paramref name="first"/> on, each stopping at what is solid in front of it, and last the
    /// primary when composition does not take it. A cloud at the full level reads the pilot's own hull's mask and
    /// depth from a copy of the scene, taken once; the other levels read the scene's own depth and copy nothing.
    /// </summary>
    private void DrawGas(CommandBuffer commands, int frameIndex, int first, bool primary, bool half, ref VkRenderingAttachmentInfo color, ref VkRenderingAttachmentInfo depth)
    {
        var copied = false;
        var reading = false;
        var count = _gas.Count + (primary && _gas.HasPrimary ? 1 : 0);
        for (var i = first; i < count; i++)
        {
            ref readonly var gas = ref (i < _gas.Count ? ref _gas[i] : ref _gas.Primary);
            ref readonly var bounds = ref (i < _gas.Count ? ref _gas.Bounds(i) : ref _gas.PrimaryBounds);
            var masked = gas.Budget.Y == GasConstants.Full;
            if (i >= _gas.Count && half)
            {
                // The primary, marched at half resolution once the opaque scene is kept, then read back here.
                if (reading) EndDepthRead(commands, ref color, ref depth);
                reading = false;
                if (!copied) KeepBehindShields(commands, ref color, ref depth);
                copied = true;
                commands.EndRendering();
                MarchHalf(commands, frameIndex, gas);
                ResumeScene(commands, ref color, ref depth);
                Span<VkDescriptorSet> halfSets = [_frameSets[frameIndex], _shieldSet];
                commands.BindPipeline(VkPipelineBindPoint.Graphics, _gasPipeline);
                commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _shieldPipelineLayout, 0, halfSets, []);
                var marked = gas;
                marked.Budget.W = GasConstants.HalfMarched;
                DrawGasQuad(commands, marked, bounds);
                continue;
            }
            if (masked && (reading || !copied))
            {
                if (reading) EndDepthRead(commands, ref color, ref depth);
                reading = false;
                if (!copied) KeepBehindShields(commands, ref color, ref depth);
                copied = true;
                Span<VkDescriptorSet> sets = [_frameSets[frameIndex], _shieldSet];
                commands.BindPipeline(VkPipelineBindPoint.Graphics, _gasPipeline);
                commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _shieldPipelineLayout, 0, sets, []);
            }
            else if (!masked && !reading)
            {
                BeginDepthRead(commands, ref color, ref depth);
                reading = true;
                Span<VkDescriptorSet> sets = [_frameSets[frameIndex], _gasDepthSet];
                commands.BindPipeline(VkPipelineBindPoint.Graphics, _gasScenePipeline);
                commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _shieldPipelineLayout, 0, sets, []);
            }
            DrawGasQuad(commands, gas, bounds);
        }

        if (reading) EndDepthRead(commands, ref color, ref depth);
        SetViewport(commands, _extent);
    }

    /// <summary>
    /// Whether this frame's primary cloud is marched at half resolution (gas_half.glsl). Not for the gas fixtures, which
    /// must take the exact march; not for a cold cloud or a cold and normal pair, whose ice glints are dots a pixel
    /// across; not while the pilot's own rift or wormhole is open, whose passage is tested at every sample and would
    /// be blurred along its outline.
    /// </summary>
    private bool HalfMarches(in FrameConstants constants)
    {
        if (!_gas.HasPrimary || constants.Gas.Budget.Z > 0 || _gas.Primary.Budget.Z > 0)
            return false;
        var code = (int)(_gas.Primary.Motion.W + 0.5f);
        var kind = code & 3;
        var paired = (code >> 2) > 0 && kind != (int)Prospect.Game.World.GasCloudKind.Lightning;
        if (kind == (int)Prospect.Game.World.GasCloudKind.Cold || paired)
            return false;
        var ownKind = (int)(constants.Ftl.X + 0.5f);
        return !(constants.Ftl.Y > 0 && (ownKind == 2 || ownKind == 3));
    }

    /// <summary>For muted capture diagnostics: whether the last frame marched its primary cloud at half resolution.</summary>
    public bool LastGasHalf { get; private set; }

    /// <summary>
    /// Marches <paramref name="gas"/> at half resolution into its own target, to the opaque depth kept behind the
    /// shields, and leaves the target for the fragment shaders to read. Call outside any rendering, after that copy.
    /// </summary>
    private void MarchHalf(CommandBuffer commands, int frameIndex, in GasConstants gas)
    {
        // Every texel is written, so what was there is dropped; the last frame's reads finish first.
        Barrier(commands, _gasHalfImage, VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal, VkAccessFlags.ShaderReadBit, VkAccessFlags.ColorAttachmentWriteBit,
            VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.ColorAttachmentOutputBit);
        var target = new VkRenderingAttachmentInfo
        {
            ImageView = _gasHalfView,
            ImageLayout = VkImageLayout.ColorAttachmentOptimal,
            LoadOp = VkAttachmentLoadOp.DontCare,
            StoreOp = VkAttachmentStoreOp.Store,
        };
        commands.BeginRendering(new VkRenderingInfo { RenderArea = new VkRect2D(_gasHalfExtent), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &target });
        // The whole target, not the cloud's outline: a pixel at the outline reads the texels beside it, and a texel
        // whose ray misses the box costs only the box test.
        SetViewport(commands, _gasHalfExtent);
        Span<VkDescriptorSet> sets = [_frameSets[frameIndex], _shieldSet];
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _gasHalfPipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _shieldPipelineLayout, 0, sets, []);
        var pushed = gas;
        commands.PushConstants(_shieldPipelineLayout, VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(&pushed, sizeof(GasConstants)));
        commands.Draw(3, 1, 0, 0);
        commands.EndRendering();
        Barrier(commands, _gasHalfImage, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.ShaderReadBit,
            VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.FragmentShaderBit);
    }

    /// <summary>Carries on drawing the scene, its colour and depth kept, after a pass outside it.</summary>
    private void ResumeScene(CommandBuffer commands, ref VkRenderingAttachmentInfo color, ref VkRenderingAttachmentInfo depth)
    {
        color.LoadOp = VkAttachmentLoadOp.Load;
        depth.LoadOp = VkAttachmentLoadOp.Load;
        var colorInfo = color;
        var depthInfo = depth;
        commands.BeginRendering(new VkRenderingInfo { RenderArea = new VkRect2D(_extent), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &colorInfo, DepthAttachment = &depthInfo });
        SetViewport(commands, _extent);
    }

    /// <summary>
    /// Carries on drawing with the scene's depth attached read-only, so a shader can read it meanwhile. Nothing
    /// drawn until <see cref="EndDepthRead"/> may test or write depth.
    /// </summary>
    private void BeginDepthRead(CommandBuffer commands, ref VkRenderingAttachmentInfo color, ref VkRenderingAttachmentInfo depth)
    {
        const VkPipelineStageFlags depthTests = VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit;
        commands.EndRendering();
        Barrier(commands, _depthImage, VkImageLayout.DepthAttachmentOptimal, VkImageLayout.DepthStencilReadOnlyOptimal, VkAccessFlags.DepthStencilAttachmentWriteBit,
            VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.ShaderReadBit, depthTests, depthTests | VkPipelineStageFlags.FragmentShaderBit, VkImageAspectFlags.DepthBit);
        color.LoadOp = VkAttachmentLoadOp.Load;
        depth.LoadOp = VkAttachmentLoadOp.Load;
        var colorInfo = color;
        var depthInfo = depth with { ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        commands.BeginRendering(new VkRenderingInfo { RenderArea = new VkRect2D(_extent), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &colorInfo, DepthAttachment = &depthInfo });
        SetViewport(commands, _extent);
    }

    /// <summary>Gives the scene's depth back to the passes that test and write it.</summary>
    private void EndDepthRead(CommandBuffer commands, ref VkRenderingAttachmentInfo color, ref VkRenderingAttachmentInfo depth)
    {
        const VkPipelineStageFlags depthTests = VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit;
        commands.EndRendering();
        Barrier(commands, _depthImage, VkImageLayout.DepthStencilReadOnlyOptimal, VkImageLayout.DepthAttachmentOptimal, VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.ShaderReadBit,
            VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit, depthTests | VkPipelineStageFlags.FragmentShaderBit, depthTests, VkImageAspectFlags.DepthBit);
        var colorInfo = color;
        var depthInfo = depth;
        commands.BeginRendering(new VkRenderingInfo { RenderArea = new VkRect2D(_extent), LayerCount = 1, ColorAttachmentCount = 1, ColorAttachments = &colorInfo, DepthAttachment = &depthInfo });
        SetViewport(commands, _extent);
    }

    private void BindShields(CommandBuffer commands, int frameIndex)
    {
        var zero = new ByteSize64(0);
        Span<VkBuffer> buffers = [_shieldVertexBuffers[frameIndex].Buffer, _shieldInstanceBuffers[frameIndex].Buffer];
        Span<ByteSize64> offsets = [zero, zero];
        Span<VkDescriptorSet> sets = [_frameSets[frameIndex], _shieldSet];
        var look = _shieldLook;
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _shieldPipelineLayout, 0, sets, []);
        commands.PushConstants(_shieldPipelineLayout, VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(&look, sizeof(ShieldLookConstants)));
        commands.BindVertexBuffers(0, buffers, offsets);
        commands.BindIndexBuffer(_shieldIndexBuffers[frameIndex].Buffer, zero, VkIndexType.UInt16);
    }

    private void DrawShieldPass(CommandBuffer commands, VkPipeline pipeline, ReadOnlySpan<int> order)
    {
        if (order.IsEmpty)
            return;
        commands.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        // A shell's indices count from its own first vertex, which the vertex offset adds.
        foreach (var i in order)
            commands.DrawIndexed(_shieldDraws[i].IndexCount, 1, _shieldDraws[i].FirstIndex, _shieldDraws[i].FirstVertex, i);
    }

    /// <summary>
    /// Copies the scene's colour as it is just before the pilot's own shield into the tone mapper's copy of it,
    /// then carries on drawing. The last frame's tone mapper read that copy; this waits for that read first.
    /// </summary>
    private void KeepBeforeOwnShield(CommandBuffer commands, ref VkRenderingAttachmentInfo color, ref VkRenderingAttachmentInfo depth)
    {
        commands.EndRendering();
        Barrier(commands, _colorImage, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.TransferSrcOptimal, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.TransferBit);
        Barrier(commands, _ownBehindImage, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal, VkAccessFlags.ShaderReadBit, VkAccessFlags.TransferWriteBit, VkPipelineStageFlags.FragmentShaderBit, VkPipelineStageFlags.TransferBit);
        var layers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
        var region = new VkImageCopy { SrcSubresource = layers, DstSubresource = layers, Extent = new VkExtent3D(_extent.Width, _extent.Height, 1) };
        commands.CopyImage(_colorImage, VkImageLayout.TransferSrcOptimal, _ownBehindImage, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageCopy>(in region));
        Barrier(commands, _colorImage, VkImageLayout.TransferSrcOptimal, VkImageLayout.ColorAttachmentOptimal, VkAccessFlags.TransferReadBit, VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit, VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ColorAttachmentOutputBit);
        Barrier(commands, _ownBehindImage, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit);

        // The depth written so far stays: it is read and written again by the own shield.
        const VkPipelineStageFlags depthTests = VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit;
        const VkAccessFlags depthAccess = VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit;
        Barrier(commands, _depthImage, VkImageLayout.DepthAttachmentOptimal, VkImageLayout.DepthAttachmentOptimal, VkAccessFlags.DepthStencilAttachmentWriteBit, depthAccess, depthTests, depthTests, VkImageAspectFlags.DepthBit);

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
    /// Copies the scene's colour and depth as they are before the shields into the shields' own images, then carries
    /// on drawing with both kept. Those images are read only by the shields of the frame that wrote them; the next
    /// frame that writes them waits for those reads first.
    /// </summary>
    private void KeepBehindShields(CommandBuffer commands, ref VkRenderingAttachmentInfo color, ref VkRenderingAttachmentInfo depth)
    {
        const VkPipelineStageFlags depthTests = VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit;
        const VkAccessFlags depthAccess = VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit;
        var fromLayout = _behindReady ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.Undefined;
        var fromAccess = _behindReady ? VkAccessFlags.ShaderReadBit : VkAccessFlags.None;
        var fromStage = _behindReady ? VkPipelineStageFlags.FragmentShaderBit : VkPipelineStageFlags.TopOfPipeBit;

        commands.EndRendering();
        Barrier(commands, _colorImage, VkImageLayout.ColorAttachmentOptimal, VkImageLayout.TransferSrcOptimal, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit, VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.TransferBit);
        Barrier(commands, _depthImage, VkImageLayout.DepthAttachmentOptimal, VkImageLayout.TransferSrcOptimal, VkAccessFlags.DepthStencilAttachmentWriteBit, VkAccessFlags.TransferReadBit, depthTests, VkPipelineStageFlags.TransferBit, VkImageAspectFlags.DepthBit);
        Barrier(commands, _behindImage, fromLayout, VkImageLayout.TransferDstOptimal, fromAccess, VkAccessFlags.TransferWriteBit, fromStage, VkPipelineStageFlags.TransferBit);
        Barrier(commands, _behindDepthImage, fromLayout, VkImageLayout.TransferDstOptimal, fromAccess, VkAccessFlags.TransferWriteBit, fromStage, VkPipelineStageFlags.TransferBit, VkImageAspectFlags.DepthBit);

        var colorLayers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
        var region = new VkImageCopy { SrcSubresource = colorLayers, DstSubresource = colorLayers, Extent = new VkExtent3D(_extent.Width, _extent.Height, 1) };
        commands.CopyImage(_colorImage, VkImageLayout.TransferSrcOptimal, _behindImage, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageCopy>(in region));
        var depthLayers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.DepthBit, LayerCount = 1 };
        var depthRegion = region with { SrcSubresource = depthLayers, DstSubresource = depthLayers };
        commands.CopyImage(_depthImage, VkImageLayout.TransferSrcOptimal, _behindDepthImage, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageCopy>(in depthRegion));

        Barrier(commands, _colorImage, VkImageLayout.TransferSrcOptimal, VkImageLayout.ColorAttachmentOptimal, VkAccessFlags.TransferReadBit, VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit, VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ColorAttachmentOutputBit);
        Barrier(commands, _depthImage, VkImageLayout.TransferSrcOptimal, VkImageLayout.DepthAttachmentOptimal, VkAccessFlags.TransferReadBit, depthAccess, VkPipelineStageFlags.TransferBit, depthTests, VkImageAspectFlags.DepthBit);
        Barrier(commands, _behindImage, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit);
        Barrier(commands, _behindDepthImage, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, VkImageAspectFlags.DepthBit);
        _behindReady = true;

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
    /// Draws the tone-mapped scene to the screen, bent by the jump lenses given to the last <see cref="Render"/>, and
    /// over it the scan pulse when one was queued. Call inside the present pass, before the interface.
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
        if (!_pulseOn)
            return;
        var pulse = _pulse;
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _pulsePipeline);
        Span<VkDescriptorSet> pulseSets = [_frameSets[_frameIndex], _sceneSet, _pulseSets[_frameIndex]];
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _pulsePipelineLayout, 0, pulseSets, []);
        commands.PushConstants(_pulsePipelineLayout, VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(&pulse, sizeof(ScanPulseConstants)));
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
        device.DestroyPipeline(_gasPipeline, null);
        device.DestroyPipeline(_gasSkyPipeline, null);
        device.DestroyPipeline(_gasScenePipeline, null);
        device.DestroyPipeline(_gasHalfPipeline, null);
        device.DestroyPipeline(_shieldPipeline, null);
        device.DestroyPipeline(_shieldMaskPipeline, null);
        device.DestroyPipeline(_pulsePipeline, null);
        device.DestroyPipelineLayout(_pulsePipelineLayout, null);
        _pulseLayout.Dispose();
        device.DestroyPipelineLayout(_shieldPipelineLayout, null);
        _shieldLayout.Dispose();
        device.DestroyPipelineLayout(_scenePipelineLayout, null);
        device.DestroyPipelineLayout(_tonemapPipelineLayout, null);
        _descriptorPool.Dispose();
        _gasNoise?.Dispose();
        _frameLayout.Dispose();
        _sceneLayout.Dispose();
        DestroyBuffers();
        _shipVertices.Destroy(_gpu);
        _shipIndices.Destroy(_gpu);
    }

    /// <summary>
    /// Frees the buffers the constructor makes, whichever of them were made. Only once the device is idle, or before
    /// anything was submitted.
    /// </summary>
    private void DestroyBuffers()
    {
        for (var i = 0; i < FrameRenderer.FramesInFlight; i++)
        {
            _frameBuffers[i].Destroy(_gpu);
            _instanceBuffers[i].Destroy(_gpu);
            _ftlBuffers[i].Destroy(_gpu);
            _damageVertices[i].Destroy(_gpu);
            _damageInstances[i].Destroy(_gpu);
            _shieldVertexBuffers[i].Destroy(_gpu);
            _shieldIndexBuffers[i].Destroy(_gpu);
            _shieldInstanceBuffers[i].Destroy(_gpu);
            _pulseWaveBuffers[i].Destroy(_gpu);
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
        VkSpecializationInfo* fragmentConstants = null,
        bool depthAttachment = true
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
            DepthAttachmentFormat = depthAttachment ? DepthFormat : VkFormat.Undefined,
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
        (_behindImage, _behindAllocation, _behindView) = CreateTarget(ColorFormat, VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        (_behindDepthImage, _behindDepthAllocation, _behindDepthView) = CreateTarget(DepthFormat, VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.DepthBit);
        (_ownBehindImage, _ownBehindAllocation, _ownBehindView) = CreateTarget(ColorFormat, VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit);
        _ownBehindReady = false;
        _behindReady = false;
        // Half the size, rounded up, so every pixel has a texel.
        _gasHalfExtent = new VkExtent2D((extent.Width + 1) / 2, (extent.Height + 1) / 2);
        (_gasHalfImage, _gasHalfAllocation, _gasHalfView) = CreateTarget(ColorFormat, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.SampledBit, VkImageAspectFlags.ColorBit, _gasHalfExtent);
        _gasHalfReady = false;

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
        // Read exactly, texel for texel, against the scene.
        var ownBehindInfo = depthInfo with { ImageView = _ownBehindView };
        var behindInfo = imageInfo with { ImageView = _behindView };
        var behindDepthInfo = depthInfo with { ImageView = _behindDepthView };
        var attachedDepthInfo = depthInfo with { ImageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        var halfInfo = depthInfo with { ImageView = _gasHalfView };
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
                new VkWriteDescriptorSet
                {
                    DstSet = _sceneSet,
                    DstBinding = 5,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &ownBehindInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _sceneSet,
                    DstBinding = 6,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &behindInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _sceneSet,
                    DstBinding = 7,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &behindDepthInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _sceneSet,
                    DstBinding = 8,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &halfInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _shieldSet,
                    DstBinding = 2,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &halfInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _gasDepthSet,
                    DstBinding = 2,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &halfInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _shieldSet,
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &behindInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _shieldSet,
                    DstBinding = 1,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &behindDepthInfo,
                },
                // The middle and far gas levels read the scene's depth itself; their shader never reads binding 0.
                new VkWriteDescriptorSet
                {
                    DstSet = _gasDepthSet,
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &behindInfo,
                },
                new VkWriteDescriptorSet
                {
                    DstSet = _gasDepthSet,
                    DstBinding = 1,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = &attachedDepthInfo,
                },
            ],
            []
        );
    }

    private (VkImage, VmaAllocation, VkImageView) CreateTarget(VkFormat format, VkImageUsageFlags usage, VkImageAspectFlags aspect) => CreateTarget(format, usage, aspect, _extent);

    private (VkImage, VmaAllocation, VkImageView) CreateTarget(VkFormat format, VkImageUsageFlags usage, VkImageAspectFlags aspect, VkExtent2D extent)
    {
        _gpu.Allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D(extent.Width, extent.Height, 1),
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
        _gpu.Device.DestroyImageView(_behindView, null);
        _gpu.Device.DestroyImageView(_behindDepthView, null);
        _gpu.Allocator.DestroyImage(_behindImage, _behindAllocation);
        _gpu.Allocator.DestroyImage(_behindDepthImage, _behindDepthAllocation);
        _gpu.Device.DestroyImageView(_ownBehindView, null);
        _gpu.Device.DestroyImageView(_gasHalfView, null);
        _gpu.Allocator.DestroyImage(_gasHalfImage, _gasHalfAllocation);
        _gpu.Allocator.DestroyImage(_ownBehindImage, _ownBehindAllocation);
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
