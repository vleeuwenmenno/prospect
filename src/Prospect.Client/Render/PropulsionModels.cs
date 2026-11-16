using System.Numerics;
using System.Text.Json;
using Brutal.Numerics;
using Prospect.Engine.IO;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// The proportions of one thrust part's low-poly model, from <c>content/visuals/propulsion.json</c>. Every length is
/// a share of something the part's box already has, so one entry fits the part at any size: the face is divided into
/// nozzles, each nozzle's exit follows its share of the face, and its length follows its exit, never the box's depth.
/// A deeper box gets a longer body behind the same nozzle. The body always covers the whole mounting face and runs
/// out to the box's sides, so neighbours meet it flush; only the nozzles are narrower than the box.
/// </summary>
internal sealed class PropulsionModelLook
{
    /// <summary>The key of the plume this part shows in <see cref="PropulsionLook.Plumes"/>.</summary>
    public string Plume { get; init; } = "";

    /// <summary>
    /// Nozzles per grid cell along each side of the face; 0 divides the face into squares of its shorter side, one
    /// nozzle each, so an engine of a square face has one bell and one twice as wide as tall has two.
    /// </summary>
    public float PerCell { get; init; }

    /// <summary>The square mount block at the back, as a share of the body's depth; the rest of the body is housing.</summary>
    public float MountDepth { get; init; }

    /// <summary>How far the housing's corners are cut, as a share of the smaller half side: 0 keeps it square, above 0 makes it an octagon.</summary>
    public float HousingChamfer { get; init; }

    /// <summary>The chamfer round the body's front edges, as a share of the smaller half side.</summary>
    public float Bevel { get; init; }

    /// <summary>Panel seams: shallow grooves round the housing (0 to 3).</summary>
    public int Seams { get; init; }

    /// <summary>Pipes along the housing's cut corners (0 to 4); they need a chamfer.</summary>
    public int Pipes { get; init; }

    /// <summary>Gusset brackets round each nozzle's collar (0 or 4); they need a collar.</summary>
    public int Brackets { get; init; }

    /// <summary>A nozzle's exit radius as a share of half its square of the face.</summary>
    public float ExitShare { get; init; }

    /// <summary>The throat's radius as a share of the exit's.</summary>
    public float ThroatShare { get; init; }

    /// <summary>The nozzle's length in exit radii, and the most of the box's depth it may take.</summary>
    public float NozzleLength { get; init; }
    public float MaxNozzleDepth { get; init; }

    /// <summary>How fast the bell opens: 1 is a straight cone, above 1 opens quickly from the throat, then runs straight.</summary>
    public float BellCurve { get; init; }

    /// <summary>The collar between body and nozzle: its length in exit radii and its radius in throat radii.</summary>
    public float CollarLength { get; init; }
    public float CollarRadius { get; init; }

    /// <summary>The thickness of the nozzle's wall, in exit radii.</summary>
    public float Lip { get; init; }

    /// <summary>
    /// Bars each way across an ion part's emitter: above 0 the part ends in a flat grid in a short ring instead of a
    /// bell (<see cref="PropulsionModels"/>); 0 for a bell nozzle. The ring's height is <see cref="NozzleLength"/>
    /// exit radii and its wall <see cref="Lip"/>; the collar and the bell's figures are not used.
    /// </summary>
    public int Grid { get; init; }

    /// <summary>Facets around a nozzle, and rings along its bell: kept low, the model is low poly.</summary>
    public int Sides { get; init; }
    public int Rings { get; init; }

    /// <summary>
    /// How light each part is against the hull's colour: 0 keeps the hull's own plating, above 0 is bare metal at that
    /// brightness. The mesh shader reads it from the length of the normal (<see cref="PropulsionModels.Tag"/>).
    /// </summary>
    public float HousingShade { get; init; }
    public float NozzleShade { get; init; }
    public float ThroatShade { get; init; }
    public float DetailShade { get; init; }

    /// <summary>
    /// For a part that mounts on a face: built as a pod (<see cref="PodLook"/>) on a base plate instead of a block
    /// that fills its box. Null for engines, which fill their cells.
    /// </summary>
    public PodLook? Pod { get; init; }

    /// <summary>The index of <see cref="Plume"/> in <see cref="PropulsionLook.PlumeOrder"/>, set when the look loads.</summary>
    internal int PlumeSlot { get; set; } = -1;

    internal void Check(string path, string id)
    {
        static bool Share(float v) => v > 0 && v <= 1;
        static bool Shade(float v) => v is >= 0 and < 1;
        if (!(PerCell is >= 0 and <= 8) || !(MountDepth is >= 0 and <= 1) || !(HousingChamfer is >= 0 and <= 0.45f)
            || !(Bevel is >= 0 and <= 0.3f) || Seams is < 0 or > 3 || Pipes is < 0 or > 4 || Brackets is not (0 or 4)
            || !Share(ExitShare) || !(ThroatShare > 0 && ThroatShare < 1) || !(NozzleLength is > 0 and <= 8)
            || !Share(MaxNozzleDepth) || MaxNozzleDepth > 0.9f || !(BellCurve is >= 1 and <= 6)
            || !(CollarLength is >= 0 and <= 2) || !(CollarRadius is >= 1 and <= 2.5f) || !(Lip is >= 0 and <= 0.3f)
            // The throat ring and the brackets reach 1.6 collar radii, which must stay inside the nozzle's square.
            || ThroatShare * CollarRadius * 1.6f > 1f
            || Sides is < 6 or > 24 || Rings is < 1 or > 12 || Grid is < 0 or > 6
            || !Shade(HousingShade) || !Shade(NozzleShade) || !Shade(ThroatShade) || !Shade(DetailShade)
            || (Pod is not null && !Pod.Valid))
            throw new InvalidDataException($"{path}: model {id} is out of range");
    }
}

/// <summary>
/// The pod a face-mounted thruster is built as, from <c>content/visuals/propulsion.json</c>: a faceted base plate on
/// the face, bolted down at its corners, a small housing standing on it with a valve box at its side, and one to four
/// nozzles on the housing's front (<see cref="PropulsionModelLook.PerCell"/> per cell of the footprint, at most two a
/// side). Every length is a share of the mount's box, so a pod fits any footprint.
/// </summary>
internal sealed class PodLook
{
    /// <summary>The plate's thickness as a share of the box's depth.</summary>
    public float PlateDepth { get; init; }

    /// <summary>How far the plate's corners are cut, as a share of the footprint's smaller half side.</summary>
    public float PlateChamfer { get; init; }

    /// <summary>The bevel round the plate's top edge, as a share of its thickness.</summary>
    public float PlateBevel { get; init; }

    /// <summary>The housing's width as a share of the footprint's.</summary>
    public float BodyShare { get; init; }

    /// <summary>Bolts at the plate's corners: 0 or 4.</summary>
    public int Bolts { get; init; }

    /// <summary>The valve box beside the housing, as a share of the gap between housing and plate edge; 0 for none.</summary>
    public float Valve { get; init; }

    internal bool Valid =>
        PlateDepth is >= 0.05f and <= 0.5f && PlateChamfer is >= 0 and <= 0.4f && PlateBevel is >= 0 and <= 0.8f
        && BodyShare is >= 0.4f and <= 0.9f && Bolts is 0 or 4 && Valve is >= 0 and <= 1
        // Corner bolts sit between the housing and the plate's cut corner, so the gap must clear the cut.
        && (Bolts == 0 || 1f - BodyShare >= PlateChamfer);
}

/// <summary>
/// How fuel tanks are drawn, from the <c>tanks</c> entry of <c>content/visuals/propulsion.json</c>
/// (<see cref="PropulsionModels.BuildTank"/>): an open frame of square beams along the block's edges, and inside it
/// round vessels held by clamp bands and struts, with a pipe stub on a cross brace toward the block's facing. Every
/// length is a share of the block's smallest half side or of a vessel's radius, so one entry fits a tank of any size.
/// </summary>
internal sealed class TankLook
{
    /// <summary>Shades of the bare metal: the frame's beams, the bands and struts, the pipe and the backing plates.</summary>
    public float FrameShade { get; init; }
    public float StrutShade { get; init; }
    public float PipeShade { get; init; }
    public float PlateShade { get; init; }

    /// <summary>A beam's thickness as a share of the block's smallest side.</summary>
    public float Beam { get; init; }

    /// <summary>A strut's thickness as a share of the block's smallest side; never thicker than a beam.</summary>
    public float Strut { get; init; }

    /// <summary>A vessel's radius as a share of the room inside the frame across it.</summary>
    public float TankShare { get; init; }

    /// <summary>Facets around a vessel, and latitude rings on each of its two hemispheres.</summary>
    public int Sides { get; init; }
    public int CapRings { get; init; }

    /// <summary>A clamp band's width and how far it stands proud, both in vessel radii; the most room between bands, in radii.</summary>
    public float BandWidth { get; init; }
    public float BandLift { get; init; }
    public float BandSpacing { get; init; }

    /// <summary>Half the width of the painted stripe round a vessel's middle, in radii; 0 for none.</summary>
    public float Stripe { get; init; }

    /// <summary>The pipe stub's radius, in vessel radii.</summary>
    public float Pipe { get; init; }

    /// <summary>The most vessels side by side in one block (a slab); past it they grow apart, never wider.</summary>
    public int MaxVessels { get; init; }

    /// <summary>The paint of each fuel's vessels, keyed by the fuel's name in <c>blocks.json</c>. A fuel without one stays a plain block.</summary>
    public Dictionary<string, TankFuelLook> Fuels { get; init; } = [];

    // The looks by FuelKind, filled when the look loads, so a worker never searches the dictionary.
    private readonly TankFuelLook?[] _byFuel = new TankFuelLook?[Prospect.Game.World.Fuels.Count + 1];

    /// <summary>The look of tanks of <paramref name="fuel"/>, or null when it has none.</summary>
    public TankFuelLook? Of(FuelKind fuel) => (uint)fuel < (uint)_byFuel.Length ? _byFuel[(int)fuel] : null;

    internal void Check(string path)
    {
        static bool Shade(float v) => v is >= 0 and < 1;
        if (!Shade(FrameShade) || !Shade(StrutShade) || !Shade(PipeShade) || !Shade(PlateShade)
            || !(Beam is > 0 and <= 0.2f) || !(Strut > 0 && Strut <= Beam) || !(TankShare is >= 0.5f and <= 0.95f)
            || Sides is < 6 or > 24 || CapRings is < 1 or > 6 || !(BandWidth is >= 0 and <= 0.5f) || !(BandLift is >= 0 and <= 0.2f)
            || !(BandSpacing is >= 1 and <= 20) || !(Stripe is >= 0 and <= 1) || !(Pipe is > 0 and <= 0.5f)
            || MaxVessels is < 1 or > PropulsionModels.MaxTankVessels)
            throw new InvalidDataException($"{path}: tanks are out of range");
        foreach (var (name, fuel) in Fuels)
        {
            fuel.Check(path, name);
            _byFuel[(int)Prospect.Game.World.Fuels.Parse(name, path + ": tanks")] = fuel;
        }
    }
}

/// <summary>The paint of one fuel's tank vessels: the body and the stripe round its middle, each RGB from 0 to 1.</summary>
internal sealed class TankFuelLook
{
    public float[] Body { get; init; } = [];
    public float[] Stripe { get; init; } = [];

    internal void Check(string path, string id)
    {
        static bool Colour(float[] c) => c.Length == 3 && c[0] is >= 0 and <= 1 && c[1] is >= 0 and <= 1 && c[2] is >= 0 and <= 1;
        if (!Colour(Body) || !Colour(Stripe))
            throw new InvalidDataException($"{path}: tank paint {id} is out of range");
    }
}

/// <summary>How one kind of plume looks and moves, from <c>content/visuals/propulsion.json</c>; lengths are in exit radii.</summary>
internal sealed class PlumeLook
{
    /// <summary>
    /// <c>flame</c>: hot exhaust, light of its own; <c>puff</c>: cold gas, lit only by the star; <c>ion</c>: a faint,
    /// steady glow of its own with a thin straight beam, never sparks, shock nodes or flicker.
    /// </summary>
    public string Kind { get; init; } = "";

    /// <summary>Colours of the core, the edge and ignition sparks.</summary>
    public float[] Core { get; init; } = [];
    public float[] Edge { get; init; } = [];
    public float[] Spark { get; init; } = [];

    /// <summary>The most scene light the plume reaches, however long the view runs through it: a flame never burns out to flat white.</summary>
    public float Brightness { get; init; }

    public RockExhaustLook Rock { get; init; } = new();

    public float ShieldStrength { get; init; }
    public float ShieldSpread { get; init; }
    public float[] ShieldColor { get; init; } = [];

    /// <summary>Length at full throttle in exit radii, and the share of it shown at the smallest throttle.</summary>
    public float Length { get; init; }
    public float MinLength { get; init; }

    /// <summary>How much wider than the exit the plume is at its end, in exit radii.</summary>
    public float Spread { get; init; }

    /// <summary>A flame's opening: the exit radii in which it reaches most of its spread. A vacuum plume opens at once.</summary>
    public float Opening { get; init; }

    /// <summary>How far the bright core reaches past the exit, in exit radii.</summary>
    public float CoreLength { get; init; }

    /// <summary>How much the brightness flickers, and how strongly the plume's body is broken up, both 0 to 1.</summary>
    public float Flicker { get; init; }
    public float Turbulence { get; init; }

    /// <summary>How fast the gas streams out along the plume, in exit radii a second.</summary>
    public float FlowSpeed { get; init; }

    /// <summary>How bright the first shock node is (0: none) and how far past the exit, in exit radii.</summary>
    public float Diamonds { get; init; }
    public float DiamondSpacing { get; init; }

    /// <summary>Seconds of ignition sparks, then seconds the plume takes to grow to its length.</summary>
    public float SparkSeconds { get; init; }
    public float GrowSeconds { get; init; }

    /// <summary>How many sparks an ignition throws, and how fast they fly in exit radii a second.</summary>
    public int Sparks { get; init; }
    public float SparkSpeed { get; init; }

    /// <summary>A puff's extra density the moment its valve opens, and the seconds it takes to settle to a steady jet.</summary>
    public float Burst { get; init; }
    public float BurstSeconds { get; init; }

    /// <summary>Seconds the last gas takes to clear once the part stops firing: it flies off down the plume.</summary>
    public float FadeSeconds { get; init; }

    /// <summary>True for a cold gas puff.</summary>
    public bool IsPuff => Kind == "puff";

    /// <summary>True for an ion part's glow and beam.</summary>
    public bool IsIon => Kind == "ion";

    /// <summary>What the plume shader is told the kind is: 0 a flame, 1 a puff, 2 an ion glow.</summary>
    public float ShaderKind => IsPuff ? 1f : IsIon ? 2f : 0f;

    internal void Check(string path, string id)
    {
        Rock.Check(Kind);
        static bool Colour(float[] c) => c.Length == 3 && c[0] is >= 0 and <= 4 && c[1] is >= 0 and <= 4 && c[2] is >= 0 and <= 4;
        if (!(ShieldSpread is >= 1 and <= 4) || !Colour(ShieldColor) || !(ShieldStrength is >= 0 and <= 4)
            || Kind is not ("flame" or "puff" or "ion") || !Colour(Core) || !Colour(Edge) || !Colour(Spark)
            || !(Brightness is > 0 and <= 40) || !(Length is > 0 and <= 80) || !(MinLength is > 0 and <= 1) || !(Spread is >= 0 and <= 30)
            || !(Opening is > 0 and <= 20) || !(CoreLength is > 0 and <= 10) || !(FlowSpeed is >= 0 and <= 200)
            || !(Flicker is >= 0 and <= 1) || !(Turbulence is >= 0 and <= 1) || !(Diamonds is >= 0 and <= 4) || !(DiamondSpacing is > 0 and <= 10)
            || !(SparkSeconds is >= 0 and <= 1) || !(GrowSeconds is > 0 and <= 2) || Sparks is < 0 or > PropulsionLook.MaxSparks
            || !(SparkSpeed is >= 0 and <= 200) || (Sparks > 0 && !(SparkSeconds > 0 && SparkSpeed > 0))
            || !(Burst is >= 0 and <= 10) || !(BurstSeconds is > 0 and <= 2) || !(FadeSeconds is >= 0 and <= 2))
            throw new InvalidDataException($"{path}: plume {id} is out of range");
        // An ion glow is steady: no sparks, no shock node, no flicker, no burst.
        if (IsIon && (Sparks != 0 || Diamonds != 0 || Flicker != 0 || Burst != 0))
            throw new InvalidDataException($"{path}: plume {id} is an ion glow and has no sparks, shock nodes, flicker or burst");
    }
}

/// <summary>
/// Models and plumes of thrust parts, from <c>content/visuals/propulsion.json</c>. Keyed by block type id: a part
/// without an entry keeps the plain block shape and shows no plume.
/// </summary>
internal sealed class PropulsionLook
{
    /// <summary>The most sparks one ignition throws: the plume shader's loop bound.</summary>
    public const int MaxSparks = 16;

    /// <summary>The most plumes drawn in a frame: the size of the renderer's instance buffers.</summary>
    public const int PlumeCapacity = 256;

    public Dictionary<string, PropulsionModelLook> Models { get; init; } = [];
    public Dictionary<string, PlumeLook> Plumes { get; init; } = [];

    /// <summary>How fuel tanks are drawn; null draws every tank as its plain block.</summary>
    public TankLook? Tanks { get; init; }
    public RackLook? Rack { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public CargoCatalog Cargo { get; private set; } = null!;

    /// <summary>How much longer and brighter boost makes a flame; the brightness still stays under the plume's cap times this.</summary>
    public float BoostLength { get; init; }
    public float BoostBrightness { get; init; }

    /// <summary>The most plumes drawn in a frame; past it the largest on screen are kept.</summary>
    public int MaxPlumes { get; init; }

    /// <summary>Plumes smaller on screen than this, in pixels of exit radius, are left out.</summary>
    public float MinPixels { get; init; }

    /// <summary>The plumes in a fixed order, so a nozzle can name its plume by index (<see cref="ThrustNozzle.Plume"/>).</summary>
    public PlumeLook[] PlumeOrder { get; private set; } = [];

    private static PropulsionLook? _shared;

    /// <summary>The look the game loads once; read by mesh workers and the frame alike, never changed.</summary>
    public static PropulsionLook Shared => _shared ??= Load();

    public static PropulsionLook Load() => Load(AppPaths.Content("visuals", "propulsion.json"));

    /// <summary>Reads and checks a look; anything out of range is refused.</summary>
    public static PropulsionLook Load(string path)
    {
        var look = JsonSerializer.Deserialize<PropulsionLook>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"{path}: empty");
        foreach (var (id, plume) in look.Plumes)
            plume.Check(path, id);
        look.PlumeOrder = [.. look.Plumes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value)];
        foreach (var (id, model) in look.Models)
        {
            model.Check(path, id);
            if (!look.Plumes.ContainsKey(model.Plume))
                throw new InvalidDataException($"{path}: model {id} names plume {model.Plume}, which is not there");
            model.PlumeSlot = look.PlumeIndex(model.Plume);
        }
        look.Tanks?.Check(path);
        look.Rack?.Check();
        look.Cargo = CargoCatalog.Parse(File.ReadAllBytes(AppPaths.Content("blocks", "cargo.json")));
        if (!(look.BoostLength is >= 1 and <= 4) || !(look.BoostBrightness is >= 1 and <= 4)
            || look.MaxPlumes is < 1 or > PlumeCapacity || !(look.MinPixels is >= 0 and <= 64))
            throw new InvalidDataException($"{path}: boost is 1 to 4, maxPlumes 1 to {PlumeCapacity}, minPixels 0 to 64");
        return look;
    }

    /// <summary>The index of a plume in <see cref="PlumeOrder"/>.</summary>
    public int PlumeIndex(string id)
    {
        var at = 0;
        foreach (var key in Plumes.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (key == id) return at;
            at++;
        }
        return -1;
    }
}

/// <summary>
/// One nozzle of a design: where its exit is in the hull's frame and what it fires. Built with the mesh. An ion part's
/// emitter is a nozzle whose fuel is <see cref="FuelKind.None"/>, the only thrust part that burns none.
/// </summary>
internal readonly record struct ThrustNozzle(
    int Block,
    FuelKind Fuel,
    BlockFacing Facing,
    Vector3 Exit,
    float ExitRadius,
    float Depth,
    int Plume,
    int Index
);

/// <summary>
/// The box a thrust part's model fills: its centre and half extents in the hull's (or the draft's) frame, the way its
/// nozzles point and the size of one grid cell. A part in a grid cell and a thin part mounted on a face are both just
/// a box here, so the model is the same function of the box for either.
/// </summary>
internal readonly record struct PartBox(Vector3 Center, Vector3 Half, BlockFacing Facing, float CellSize);

/// <summary>
/// Builds low-poly models of thrust parts from their box and facing: a square mount block covering the whole mounting
/// face, an octagonal housing with panel seams, pipes and a bevelled front (engines, hot thrusters), or a bevelled RCS
/// block (cold gas), then a collar with gusset brackets, a throat ring and faceted bell nozzles pointing along the
/// facing. Every facet is flat shaded: it has vertices of its own. Runs where hull meshes are built, on a worker;
/// never in a frame.
/// </summary>
internal static partial class PropulsionModels
{
    /// <summary>
    /// The length a normal is given to carry a shade to the mesh shader: 1 is ordinary plating, 2 plus a shade below
    /// 1 is bare metal at that brightness. Every other mesh has unit normals.
    /// </summary>
    public static float Tag(float shade) => shade <= 0 ? 1f : 2f + shade;

    /// <summary>The model look of a block type, or null for a type drawn as its plain shape.</summary>
    public static PropulsionModelLook? ModelOf(BlockType type, BlockShape shape, PropulsionLook look) =>
        shape == BlockShape.Box && (type.Thrust > 0 || type.Tractor is not null) && look.Models.TryGetValue(type.Id, out var model) ? model : null;

    /// <summary>The model look of a block, or null for a block drawn as its plain shape.</summary>
    public static PropulsionModelLook? ModelOf(HullDesign design, int block, PropulsionLook look) =>
        ModelOf(design.Types[block], design.Shapes[block], look);

    /// <summary>
    /// Adds the model of <paramref name="block"/> to the triangle lists, and its nozzles to <paramref name="nozzles"/>
    /// when given: a thrust part's, or a tank's (<see cref="BuildTank"/>, which has no nozzles). Returns false,
    /// adding nothing, for a block drawn as its plain shape.
    /// </summary>
    public static bool Add(HullDesign design, int block, PropulsionLook look, List<MeshVertex> vertices, List<uint> indices, List<ThrustNozzle>? nozzles)
    {
        var model = ModelOf(design, block, look);
        if (model is null)
            return AddRack(design, block, look, vertices, indices) || AddTank(design, block, look, vertices, indices);
        var facing = design.Boxes[block].Facing;
        // The back face is left out where a neighbour covers it.
        var back = (design.ExteriorFaces[block] & (1 << (byte)Facings.Opposite(facing))) != 0;
        var box = new PartBox(design.BlockOffsets[block], design.BlockHalfExtents[block], facing, design.BlockSize > 0 ? design.BlockSize : 1f);
        Build(model, box, design.Types[block].Fuel, block, back, vertices, indices, nozzles);
        return true;
    }

    /// <summary>
    /// Adds the model of a part mounted on a block face (<see cref="HullDesign.Mounts"/>): the same model a block of
    /// its type gets, built in the thin box the mount fills and pointing out of its face. Its back is the face it sits
    /// on, so it is left open. Its nozzles name the block it sits on, since a hull has a mount exactly while it has
    /// that block. Returns false, adding nothing, for a part with no model in <paramref name="look"/>.
    /// </summary>
    public static bool AddMount(HullDesign design, int mount, PropulsionLook look, List<MeshVertex> vertices, List<uint> indices, List<ThrustNozzle>? nozzles)
    {
        var part = design.Mounts[mount];
        var model = ModelOf(part.Type, BlockShape.Box, look);
        if (model is null)
            return false;
        var box = new PartBox(design.MountOffsets[mount], design.MountHalfExtents[mount], part.Face, design.BlockSize > 0 ? design.BlockSize : 1f);
        Build(model, box, part.Type.Fuel, part.Parent, false, vertices, indices, part.Type.Tractor is null ? nozzles : null);
        return true;
    }

    /// <summary>
    /// Adds the model of one part filling <paramref name="box"/> to the triangle lists, and its nozzles, numbered on
    /// from what <paramref name="nozzles"/> holds, when given. A pure function of the box, the look and the cell size.
    /// </summary>
    public static void Build(PropulsionModelLook model, in PartBox box, FuelKind fuel, int block, bool back, List<MeshVertex> vertices, List<uint> indices, List<ThrustNozzle>? nozzles)
    {
        var facing = box.Facing;
        var axis = Facings.Axis(facing);
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        var half = box.Half;
        var g = new Geometry(vertices, indices, new Frame(box.Center, Facings.Vector(facing), Unit(u), Unit(v), half[axis]));
        var hu = half[u];
        var hv = half[v];
        var depth = 2f * half[axis];
        var cells = box.CellSize > 0 ? box.CellSize : 1f;
        var small = MathF.Min(hu, hv);
        if (model.Pod is { } pod)
        {
            BuildPod(ref g, model, pod, box, fuel, block, back, nozzles);
            return;
        }

        // The face is split into nozzles: by cells for a cluster, otherwise into squares of the shorter side.
        int nu, nv;
        if (model.PerCell > 0)
        {
            nu = Math.Clamp((int)MathF.Round(2f * hu / cells * model.PerCell), 1, 8);
            nv = Math.Clamp((int)MathF.Round(2f * hv / cells * model.PerCell), 1, 8);
        }
        else
        {
            nu = Math.Clamp((int)MathF.Round(hu / small), 1, 8);
            nv = Math.Clamp((int)MathF.Round(hv / small), 1, 8);
        }
        var pitchU = 2f * hu / nu;
        var pitchV = 2f * hv / nv;
        var exit = 0.5f * MathF.Min(pitchU, pitchV) * model.ExitShare / (1f + model.Lip);
        var nozzleLength = MathF.Min(model.NozzleLength * exit, model.MaxNozzleDepth * depth);
        var collar = model.Grid > 0 ? 0f : MathF.Min(model.CollarLength * exit, 0.12f * depth);
        // Everything behind the nozzles is body, so a deeper box grows its body and keeps its nozzle.
        var nozzleStart = depth - nozzleLength;
        var bodyEnd = MathF.Max(nozzleStart - collar, 0.05f * depth);
        collar = nozzleStart - bodyEnd;
        var mountEnd = MathF.Min(model.MountDepth * bodyEnd, bodyEnd);
        // A housing too short to read is left out: the mount block runs on to the front.
        var housing = model.HousingChamfer > 0 && bodyEnd - mountEnd > 0.12f * small;
        if (!housing)
            mountEnd = bodyEnd;
        var bevel = MathF.Min(model.Bevel * small, 0.45f * (bodyEnd - (housing ? mountEnd : 0f)));

        var plating = Tag(model.HousingShade);
        var metal = Tag(model.NozzleShade);
        var dark = Tag(model.ThroatShade);
        var detail = Tag(model.DetailShade);

        // The mount block: the whole face, so it meets every neighbour flush.
        Span<Vector2> rect = stackalloc Vector2[4];
        Span<Vector2> inset = stackalloc Vector2[4];
        Rectangle(rect, hu, hv);
        if (back)
            g.Cap(rect, Vector2.Zero, 0f, -1f, plating);
        if (housing)
        {
            g.Loft(rect, 0f, rect, mountEnd, Vector2.Zero, plating, 1f);
            g.Cap(rect, Vector2.Zero, mountEnd, 1f, plating);
            AddHousing(ref g, model, hu, hv, small, mountEnd, bodyEnd, bevel, cells, plating, detail);
        }
        else
        {
            g.Loft(rect, 0f, rect, bodyEnd - bevel, Vector2.Zero, plating, 1f);
            Rectangle(inset, hu - bevel, hv - bevel);
            if (bevel > 0)
                g.Loft(rect, bodyEnd - bevel, inset, bodyEnd, Vector2.Zero, plating, 1f);
            g.Cap(inset, Vector2.Zero, bodyEnd, 1f, plating);
        }

        var throat = exit * model.ThroatShare;
        var wall = exit * model.Lip;
        var collarRadius = throat * model.CollarRadius;
        for (var i = 0; i < nu; i++)
        {
            for (var j = 0; j < nv; j++)
            {
                var at = new Vector2((i + 0.5f) * pitchU - hu, (j + 0.5f) * pitchV - hv);
                if (model.Grid > 0)
                {
                    AddGrid(ref g, model, at, nozzleStart, depth, exit, wall, metal, dark, detail);
                }
                else
                {
                    if (collar > 1e-5f)
                        AddCollar(ref g, model, at, bodyEnd, nozzleStart, collarRadius, throat, metal, detail);
                    AddNozzle(ref g, model, at, nozzleStart, depth, throat, exit, wall, collarRadius, collar > 1e-5f, metal, dark, detail);
                }
                nozzles?.Add(new ThrustNozzle(block, fuel, facing, g.F.At(at.X, at.Y, depth), exit, nozzleLength, model.PlumeSlot, nozzles.Count));
            }
        }
    }

    // A pod on a face: a faceted plate covering the footprint, corner bolts, a small housing with a valve box, and its
    // nozzles. Its back is the face it sits on, so the plate's underside is left out unless asked for.
    private static void BuildPod(ref Geometry g, PropulsionModelLook model, PodLook pod, in PartBox box, FuelKind fuel, int block, bool back, List<ThrustNozzle>? nozzles)
    {
        var axis = Facings.Axis(box.Facing);
        var hu = box.Half[(axis + 1) % 3];
        var hv = box.Half[(axis + 2) % 3];
        var depth = 2f * box.Half[axis];
        var cells = box.CellSize > 0 ? box.CellSize : 1f;
        var small = MathF.Min(hu, hv);
        var plating = Tag(model.HousingShade);
        var plateTag = Tag(model.HousingShade * 0.8f);
        var metal = Tag(model.NozzleShade);
        var dark = Tag(model.ThroatShade);
        var detail = Tag(model.DetailShade);

        // The plate: an octagon over the whole footprint, its top edge bevelled.
        var plateTop = pod.PlateDepth * depth;
        var cut = pod.PlateChamfer * small;
        var bevel = MathF.Min(pod.PlateBevel * plateTop, 0.4f * cut + 0.25f * plateTop);
        Span<Vector2> plate = stackalloc Vector2[8];
        Span<Vector2> inset = stackalloc Vector2[8];
        Octagon(plate, hu, hv, MathF.Max(cut, 1e-4f));
        Octagon(inset, hu - bevel, hv - bevel, MathF.Max(cut - bevel * (2f - MathF.Sqrt(2f)), 1e-4f));
        if (back)
            g.Cap(plate, Vector2.Zero, 0f, -1f, plateTag);
        g.Loft(plate, 0f, plate, plateTop - bevel, Vector2.Zero, plateTag, 1f);
        if (bevel > 0)
            g.Loft(plate, plateTop - bevel, inset, plateTop, Vector2.Zero, plateTag, 1f);
        g.Cap(inset, Vector2.Zero, plateTop, 1f, plateTag);

        // The housing's front carries the nozzles: one to two a side, by the footprint's cells, else one square each.
        var bu = hu * pod.BodyShare;
        var bv = hv * pod.BodyShare;
        var bodySmall = MathF.Min(bu, bv);
        int nu, nv;
        if (model.PerCell > 0)
        {
            nu = Math.Clamp((int)MathF.Round(2f * hu / cells * model.PerCell), 1, 2);
            nv = Math.Clamp((int)MathF.Round(2f * hv / cells * model.PerCell), 1, 2);
        }
        else
        {
            nu = Math.Clamp((int)MathF.Round(bu / bodySmall), 1, 2);
            nv = Math.Clamp((int)MathF.Round(bv / bodySmall), 1, 2);
        }
        var pitchU = 2f * bu / nu;
        var pitchV = 2f * bv / nv;
        var exit = 0.5f * MathF.Min(pitchU, pitchV) * model.ExitShare / (1f + model.Lip);
        var nozzleLength = MathF.Min(model.NozzleLength * exit, model.MaxNozzleDepth * depth);
        var collar = model.Grid > 0 ? 0f : MathF.Min(model.CollarLength * exit, 0.12f * depth);
        // The housing keeps at least a sliver of height above the plate; the nozzle gives way first, then the collar.
        var bodyEnd = MathF.Max(depth - nozzleLength - collar, plateTop + 0.12f * depth);
        collar = MathF.Max(MathF.Min(collar, depth - nozzleLength - bodyEnd), 0f);
        var nozzleStart = bodyEnd + collar;
        nozzleLength = depth - nozzleStart;
        var front = MathF.Min(model.Bevel * bodySmall, 0.45f * (bodyEnd - plateTop));
        AddHousing(ref g, model, bu, bv, bodySmall, plateTop, bodyEnd, front, cells, plating, detail);

        // Bolts in the plate's corners, between the housing and the cut.
        var gap = hu - bu;
        var gapV = hv - bv;
        Span<Vector2> ring = stackalloc Vector2[6];
        if (pod.Bolts == 4)
        {
            var radius = 0.22f * MathF.Min(gap, gapV);
            var top = plateTop + MathF.Min(0.5f * plateTop, 0.3f * (bodyEnd - plateTop));
            for (var k = 0; k < 4; k++)
            {
                var centre = new Vector2((k is 0 or 3 ? 1f : -1f) * (hu - 0.5f * gap - 0.25f * cut), (k is 0 or 1 ? 1f : -1f) * (hv - 0.5f * gapV - 0.25f * cut));
                Polygon(ring, centre, radius, MathF.PI / 6f);
                g.Loft(ring, plateTop, ring, top, centre, detail, 1f);
                g.Cap(ring, centre, top, 1f, detail);
            }
        }

        // The valve box: a small block against the housing's side, where the feed comes in.
        if (pod.Valve > 0)
        {
            Span<Vector2> valve = stackalloc Vector2[4];
            var reach = pod.Valve * gap;
            var centre = new Vector2(bu + 0.5f * reach, 0f);
            Rectangle(valve, 0.5f * reach, 0.3f * bv);
            for (var k = 0; k < 4; k++)
                valve[k] += centre;
            var top = plateTop + 0.6f * (bodyEnd - plateTop);
            g.Loft(valve, plateTop, valve, top, centre, detail, 1f);
            g.Cap(valve, centre, top, 1f, detail);
        }

        var throat = exit * model.ThroatShare;
        var wall = exit * model.Lip;
        var collarRadius = throat * model.CollarRadius;
        for (var i = 0; i < nu; i++)
        {
            for (var j = 0; j < nv; j++)
            {
                var at = new Vector2((i + 0.5f) * pitchU - bu, (j + 0.5f) * pitchV - bv);
                if (model.Grid > 0)
                {
                    AddGrid(ref g, model, at, nozzleStart, depth, exit, wall, metal, dark, detail);
                }
                else
                {
                    if (collar > 1e-5f)
                        AddCollar(ref g, model, at, bodyEnd, nozzleStart, collarRadius, throat, metal, detail);
                    AddNozzle(ref g, model, at, nozzleStart, depth, throat, exit, wall, collarRadius, collar > 1e-5f, metal, dark, detail);
                }
                nozzles?.Add(new ThrustNozzle(block, fuel, box.Facing, g.F.At(at.X, at.Y, depth), exit, nozzleLength, model.PlumeSlot, nozzles.Count));
            }
        }
    }

    // The housing: an octagon from the mount block to the front, split by shallow seams, with pipes along its cut
    // corners and a bevelled front edge.
    private static void AddHousing(ref Geometry g, PropulsionModelLook model, float hu, float hv, float small, float z0, float z1, float bevel, float cells, float plating, float detail)
    {
        var chamfer = model.HousingChamfer * small;
        Span<Vector2> outer = stackalloc Vector2[8];
        Span<Vector2> groove = stackalloc Vector2[8];
        Span<Vector2> front = stackalloc Vector2[8];
        Octagon(outer, hu, hv, chamfer);
        var depth = MathF.Min(0.03f * cells, 0.05f * small);
        Octagon(groove, hu - depth, hv - depth, MathF.Max(chamfer - depth * (2f - MathF.Sqrt(2f)), 1e-4f));
        Octagon(front, hu - bevel, hv - bevel, MathF.Max(chamfer - bevel * (2f - MathF.Sqrt(2f)), 1e-4f));

        var end = z1 - bevel;
        var length = end - z0;
        var seams = model.Seams;
        var width = MathF.Min(0.035f * cells, length / (4f * (seams + 1)));
        var panel = (length - seams * width) / (seams + 1);
        var z = z0;
        for (var k = 0; k <= seams; k++)
        {
            g.Loft(outer, z, outer, z + panel, Vector2.Zero, plating, 1f);
            z += panel;
            if (k == seams)
                break;
            g.Annulus(outer, groove, Vector2.Zero, z, 1f, plating);
            g.Loft(groove, z, groove, z + width, Vector2.Zero, plating, 1f);
            g.Annulus(outer, groove, Vector2.Zero, z + width, -1f, plating);
            z += width;
        }
        if (bevel > 0)
            g.Loft(outer, end, front, z1, Vector2.Zero, plating, 1f);
        g.Cap(front, Vector2.Zero, z1, 1f, plating);

        // Pipes lie in the cut corners, on the chamfer, so they fill some of the corner and stay inside the box.
        var pipe = 0.26f * chamfer;
        var inward = 0.5f * (chamfer - pipe * MathF.Sqrt(2f));
        Span<Vector2> ring = stackalloc Vector2[6];
        for (var k = 0; k < model.Pipes; k++)
        {
            var sx = k is 0 or 2 ? 1f : -1f;
            var sy = k is 0 or 3 ? 1f : -1f;
            var centre = new Vector2(sx * (hu - inward), sy * (hv - inward));
            Polygon(ring, centre, pipe, 0f);
            g.Loft(ring, z0, ring, end, centre, detail, 1f);
            g.Cap(ring, centre, end, 1f, detail);
        }
    }

    // A faceted collar from the body to the nozzle's throat, closed round the throat, with four gusset brackets.
    private static void AddCollar(ref Geometry g, PropulsionModelLook model, Vector2 at, float z0, float z1, float radius, float throat, float metal, float detail)
    {
        var sides = model.Sides;
        Span<Vector2> outer = stackalloc Vector2[sides];
        Span<Vector2> inner = stackalloc Vector2[sides];
        Polygon(outer, at, radius, MathF.PI / sides);
        Polygon(inner, at, throat, MathF.PI / sides);
        g.Loft(outer, z0, outer, z1, at, metal, 1f);
        g.Annulus(outer, inner, at, z1, 1f, metal);
        if (model.Brackets == 0)
            return;
        // A gusset: a wedge standing on the body against the collar, deep at its foot, thin at its top.
        Span<Vector2> foot = stackalloc Vector2[4];
        Span<Vector2> top = stackalloc Vector2[4];
        var w = 0.2f * radius;
        var height = 0.8f * (z1 - z0);
        for (var k = 0; k < 4; k++)
        {
            var dir = k switch { 0 => Vector2.UnitX, 1 => Vector2.UnitY, 2 => -Vector2.UnitX, _ => -Vector2.UnitY };
            var across = new Vector2(-dir.Y, dir.X);
            var near = at + dir * (0.9f * radius);
            Gusset(foot, near, dir, across, 0.7f * radius, w);
            Gusset(top, near, dir, across, 0.12f * radius, w);
            var centre = near + dir * (0.35f * radius);
            g.Loft(foot, z0, top, z0 + height, centre, detail, 1f);
            g.Cap(top, centre, z0 + height, 1f, detail);
        }
    }

    // A faceted bell: a throat ring round its narrow end, the outer and inner walls in a few rings, the lip, and the
    // dark throat deep inside.
    private static void AddNozzle(ref Geometry g, PropulsionModelLook model, Vector2 at, float z0, float z1, float throat, float exit, float wall, float collarRadius, bool onCollar, float metal, float dark, float detail)
    {
        var sides = model.Sides;
        var rings = model.Rings;
        var turn = MathF.PI / sides;
        Span<Vector2> a = stackalloc Vector2[sides];
        Span<Vector2> b = stackalloc Vector2[sides];
        Span<Vector2> c = stackalloc Vector2[sides];
        var length = z1 - z0;
        for (var r = 0; r < rings; r++)
        {
            var t0 = (float)r / rings;
            var t1 = (float)(r + 1) / rings;
            var r0 = Bell(throat, exit, model.BellCurve, t0);
            var r1 = Bell(throat, exit, model.BellCurve, t1);
            Polygon(a, at, r0 + wall, turn);
            Polygon(b, at, r1 + wall, turn);
            g.Loft(a, z0 + length * t0, b, z0 + length * t1, at, metal, 1f);
            Polygon(a, at, r0, turn);
            Polygon(b, at, r1, turn);
            // The inside of the bell is darker, scorched.
            g.Loft(a, z0 + length * t0, b, z0 + length * t1, at, Tag(model.NozzleShade * 0.55f), -1f);
        }
        Polygon(a, at, exit + wall, turn);
        Polygon(b, at, exit, turn);
        g.Annulus(a, b, at, z1, 1f, metal);

        // The throat: dark, a little way into the bell so it never meets the body's face.
        var deep = 0.03f;
        Polygon(a, at, Bell(throat, exit, model.BellCurve, deep), turn);
        g.Cap(a, at, z0 + length * deep, 1f, dark);

        // The throat ring: a heavier band round the bell's narrow end.
        var reach = 0.1f;
        var ring = MathF.Max(collarRadius * 1.12f, (Bell(throat, exit, model.BellCurve, reach) + wall) * 1.08f);
        var ringStart = onCollar ? z0 - 0.3f * MathF.Min(length * reach * 2f, collarRadius) : z0;
        var ringEnd = z0 + length * reach;
        Polygon(a, at, ring, turn);
        Polygon(b, at, Bell(throat, exit, model.BellCurve, reach) + wall, turn);
        Polygon(c, at, onCollar ? collarRadius : throat + wall, turn);
        g.Loft(a, ringStart, a, ringEnd, at, detail, 1f);
        g.Annulus(a, b, at, ringEnd, 1f, detail);
        if (onCollar)
            g.Annulus(a, c, at, ringStart, -1f, detail);
    }

    // An ion emitter: a short faceted ring standing on the body, and inside it, a little below its rim, a flat dark
    // disc crossed by a grid of thin bars each way. No bell: the beam leaves the grid straight.
    private static void AddGrid(ref Geometry g, PropulsionModelLook model, Vector2 at, float z0, float z1, float exit, float wall, float metal, float dark, float detail)
    {
        var sides = model.Sides;
        var turn = MathF.PI / sides;
        Span<Vector2> outer = stackalloc Vector2[sides];
        Span<Vector2> inner = stackalloc Vector2[sides];
        Polygon(outer, at, exit + wall, turn);
        Polygon(inner, at, exit, turn);
        g.Loft(outer, z0, outer, z1, at, metal, 1f);
        g.Annulus(outer, inner, at, z1, 1f, metal);
        var recess = z1 - 0.45f * (z1 - z0);
        g.Loft(inner, recess, inner, z1, at, Tag(model.NozzleShade * 0.7f), -1f);
        g.Cap(inner, at, recess, 1f, dark);

        // The bars stand a little proud of the disc and stop at the ring's inside, its flats included.
        var reach = exit * MathF.Cos(turn);
        var half = 0.045f * exit;
        var height = 0.3f * (z1 - recess);
        Span<Vector2> bar = stackalloc Vector2[4];
        for (var across = 0; across < 2; across++)
        {
            for (var k = 0; k < model.Grid; k++)
            {
                var offset = ((k + 1f) / (model.Grid + 1f) * 2f - 1f) * reach;
                var chord = MathF.Sqrt(MathF.Max(reach * reach - offset * offset, 0f)) - half;
                if (chord <= half)
                    continue;
                var centre = at + (across == 0 ? new Vector2(offset, 0f) : new Vector2(0f, offset));
                Rectangle(bar, across == 0 ? half : chord, across == 0 ? chord : half);
                for (var c = 0; c < 4; c++)
                    bar[c] += centre;
                g.Loft(bar, recess, bar, recess + height, centre, detail, 1f);
                g.Cap(bar, centre, recess + height, 1f, detail);
            }
        }
    }

    private static float Bell(float throat, float exit, float curve, float t) =>
        throat + (exit - throat) * (1f - MathF.Pow(1f - t, curve));

    private static void Rectangle(Span<Vector2> points, float hu, float hv)
    {
        points[0] = new(hu, hv);
        points[1] = new(-hu, hv);
        points[2] = new(-hu, -hv);
        points[3] = new(hu, -hv);
    }

    // An octagon: a rectangle with its corners cut by c, counter-clockwise from the right side's top.
    private static void Octagon(Span<Vector2> points, float hu, float hv, float c)
    {
        points[0] = new(hu, hv - c);
        points[1] = new(hu - c, hv);
        points[2] = new(-hu + c, hv);
        points[3] = new(-hu, hv - c);
        points[4] = new(-hu, -hv + c);
        points[5] = new(-hu + c, -hv);
        points[6] = new(hu - c, -hv);
        points[7] = new(hu, -hv + c);
    }

    private static void Polygon(Span<Vector2> points, Vector2 centre, float radius, float turn)
    {
        for (var s = 0; s < points.Length; s++)
        {
            var angle = turn + s * (MathF.Tau / points.Length);
            points[s] = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
    }

    // A gusset's section: from near the collar out along dir by reach, w across.
    private static void Gusset(Span<Vector2> points, Vector2 near, Vector2 dir, Vector2 across, float reach, float w)
    {
        points[0] = near - across * w;
        points[1] = near + dir * reach - across * w;
        points[2] = near + dir * reach + across * w;
        points[3] = near + across * w;
    }

    private static Vector3 Unit(int axis)
    {
        var vector = Vector3.Zero;
        vector[axis] = 1f;
        return vector;
    }

    /// <summary>A part's own frame: x and y across its nozzle face, z from its back face (0) out along its facing.</summary>
    private readonly struct Frame(Vector3 center, Vector3 n, Vector3 u, Vector3 v, float halfDepth)
    {
        public readonly Vector3 N = n, U = u, V = v;
        public Vector3 At(float x, float y, float z) => center + U * x + V * y + N * (z - halfDepth);
        public Vector3 Dir(float x, float y, float z) => U * x + V * y + N * z;
    }

    // Flat-shaded triangles in a part's frame: every facet has vertices of its own and the facet's normal.
    private struct Geometry(List<MeshVertex> vertices, List<uint> indices, Frame frame)
    {
        public readonly Frame F = frame;

        // The side walls between two outlines of the same count, at za and zb: outward (side 1) or toward the
        // centre line (side -1).
        public readonly void Loft(ReadOnlySpan<Vector2> a, float za, ReadOnlySpan<Vector2> b, float zb, Vector2 centre, float tag, float side)
        {
            var n = a.Length;
            for (var k = 0; k < n; k++)
            {
                var k1 = (k + 1) % n;
                var p0 = new Vector3(a[k], za);
                var p1 = new Vector3(a[k1], za);
                var p2 = new Vector3(b[k1], zb);
                var p3 = new Vector3(b[k], zb);
                var normal = Vector3.Cross(p1 - p0, p3 - p0);
                if (normal.LengthSquared() < 1e-14f)
                    normal = Vector3.Cross(p2 - p3, p2 - p1);
                if (normal.LengthSquared() < 1e-14f)
                    continue;
                var middle = 0.25f * (p0 + p1 + p2 + p3);
                var radial = new Vector2(middle.X - centre.X, middle.Y - centre.Y);
                if ((normal.X * radial.X + normal.Y * radial.Y) * side < 0)
                    normal = -normal;
                Quad(p0, p1, p2, p3, normal, tag);
            }
        }

        // A flat outline across the axis at z, as a fan from its centre, facing +z (side 1) or -z.
        public readonly void Cap(ReadOnlySpan<Vector2> outline, Vector2 centre, float z, float side, float tag)
        {
            var normal = F.Dir(0, 0, side);
            var first = (uint)vertices.Count;
            Put(F.At(centre.X, centre.Y, z), normal, tag);
            foreach (var p in outline)
                Put(F.At(p.X, p.Y, z), normal, tag);
            var n = (uint)outline.Length;
            for (uint k = 0; k < n; k++)
                Triangle(first, first + 1 + k, first + 1 + (k + 1) % n, normal);
        }

        // The flat ring between two outlines of the same count at z, facing +z (side 1) or -z.
        public readonly void Annulus(ReadOnlySpan<Vector2> outer, ReadOnlySpan<Vector2> inner, Vector2 centre, float z, float side, float tag)
        {
            var n = outer.Length;
            for (var k = 0; k < n; k++)
            {
                var k1 = (k + 1) % n;
                Quad(new Vector3(outer[k], z), new Vector3(outer[k1], z), new Vector3(inner[k1], z), new Vector3(inner[k], z), new Vector3(0, 0, side), tag);
            }
        }

        private readonly void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal, float tag)
        {
            var n = F.Dir(normal.X, normal.Y, normal.Z);
            var first = (uint)vertices.Count;
            Put(F.At(p0.X, p0.Y, p0.Z), n, tag);
            Put(F.At(p1.X, p1.Y, p1.Z), n, tag);
            Put(F.At(p2.X, p2.Y, p2.Z), n, tag);
            Put(F.At(p3.X, p3.Y, p3.Z), n, tag);
            Triangle(first, first + 1, first + 2, n);
            Triangle(first, first + 2, first + 3, n);
        }

        private readonly void Put(Vector3 position, Vector3 normal, float tag)
        {
            var n = Vector3.Normalize(normal) * tag;
            vertices.Add(new MeshVertex { Position = new float3(position.X, position.Y, position.Z), Normal = new float3(n.X, n.Y, n.Z) });
        }

        // Winds every triangle counter-clockwise seen from the side its normal points to, whichever way the part's
        // frame turns: a facing against its axis mirrors the frame, which would otherwise turn half the faces inside out.
        private readonly void Triangle(uint a, uint b, uint c, Vector3 normal)
        {
            var pa = vertices[(int)a].Position;
            var pb = vertices[(int)b].Position;
            var pc = vertices[(int)c].Position;
            var cross = Vector3.Cross(new Vector3(pb.X - pa.X, pb.Y - pa.Y, pb.Z - pa.Z), new Vector3(pc.X - pa.X, pc.Y - pa.Y, pc.Z - pa.Z));
            if (cross.LengthSquared() < 1e-14f)
                return;
            if (Vector3.Dot(cross, normal) < 0)
                (b, c) = (c, b);
            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
        }
    }
}
