using System.Numerics;
using System.Text.Json;
using Brutal.Numerics;
using Prospect.Engine.IO;
using Prospect.Game.World;

namespace Prospect.Client.Render;

/// <summary>
/// The proportions of one thrust part's model, from <c>content/visuals/propulsion.json</c>. Every length is a share
/// of something the block already has, so one entry fits the part at any size: a face is divided into nozzles, each
/// nozzle's exit follows the face it sits on, and the nozzle's length follows its exit, never the block's depth. A
/// deeper block gets a longer housing behind the same nozzle.
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

    /// <summary>The housing's width as a share of the face.</summary>
    public float HousingShare { get; init; }

    /// <summary>A nozzle's exit radius as a share of half its square of the face.</summary>
    public float ExitShare { get; init; }

    /// <summary>The throat's radius as a share of the exit's.</summary>
    public float ThroatShare { get; init; }

    /// <summary>The nozzle's length in exit radii, and the most of the block's depth it may take.</summary>
    public float NozzleLength { get; init; }
    public float MaxNozzleDepth { get; init; }

    /// <summary>How fast the bell opens: 1 is a straight cone, above 1 opens quickly from the throat, then runs straight.</summary>
    public float BellCurve { get; init; }

    /// <summary>The collar between housing and nozzle: its length in exit radii and its radius in throat radii.</summary>
    public float CollarLength { get; init; }
    public float CollarRadius { get; init; }

    /// <summary>The thickness of the nozzle's wall, in exit radii.</summary>
    public float Lip { get; init; }

    /// <summary>A mounting plate the nozzles stand on: its thickness as a share of the block's depth (0 for none) and its width as a share of the face.</summary>
    public float PlateThickness { get; init; }
    public float PlateShare { get; init; }

    /// <summary>Sides around a nozzle and rings along it.</summary>
    public int Segments { get; init; }
    public int Rings { get; init; }

    /// <summary>
    /// How light each part is against the hull's colour: 0 keeps the hull's own plating, above 0 is bare metal at that
    /// brightness. The mesh shader reads it from the length of the normal (<see cref="PropulsionModels.Tag"/>).
    /// </summary>
    public float HousingShade { get; init; }
    public float NozzleShade { get; init; }
    public float ThroatShade { get; init; }

    internal void Check(string path, string id)
    {
        static bool Share(float v) => v > 0 && v <= 1;
        static bool Shade(float v) => v is >= 0 and < 1;
        if (!(PerCell is >= 0 and <= 4) || !Share(HousingShare) || !Share(ExitShare) || !(ThroatShare > 0 && ThroatShare < 1)
            || !(NozzleLength is > 0 and <= 8) || !Share(MaxNozzleDepth) || !(BellCurve is >= 1 and <= 6)
            || !(CollarLength is >= 0 and <= 2) || !(CollarRadius is >= 1 and <= 2.5f) || !(Lip is >= 0 and <= 0.3f)
            || !(PlateThickness is >= 0 and <= 0.5f) || !(PlateShare is >= 0 and <= 1) || (PlateThickness > 0 && PlateShare <= 0)
            || MaxNozzleDepth + PlateThickness > 0.85f || Segments is < 6 or > 48 || Rings is < 1 or > 24
            || !Shade(HousingShade) || !Shade(NozzleShade) || !Shade(ThroatShade))
            throw new InvalidDataException($"{path}: model {id} is out of range");
    }
}

/// <summary>How one kind of plume looks and moves, from <c>content/visuals/propulsion.json</c>; lengths are in exit radii.</summary>
internal sealed class PlumeLook
{
    /// <summary><c>flame</c>: a hot, glowing plume; <c>puff</c>: cold gas, pale and quickly gone.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Colours of the hot core, the edge and ignition sparks, in scene light per unit of brightness.</summary>
    public float[] Core { get; init; } = [];
    public float[] Edge { get; init; } = [];
    public float[] Spark { get; init; } = [];

    /// <summary>The most scene light the plume reaches.</summary>
    public float Brightness { get; init; }

    /// <summary>Length at full throttle in exit radii, and the share of it shown at the smallest throttle.</summary>
    public float Length { get; init; }
    public float MinLength { get; init; }

    /// <summary>How much wider than the exit the plume is at its end, in exit radii.</summary>
    public float Spread { get; init; }

    /// <summary>How much the brightness flickers, and how strongly the plume's body is broken up, both 0 to 1.</summary>
    public float Flicker { get; init; }
    public float Turbulence { get; init; }

    /// <summary>How bright the shock diamonds along the core are (0: none) and how far apart, in exit radii.</summary>
    public float Diamonds { get; init; }
    public float DiamondSpacing { get; init; }

    /// <summary>Seconds of ignition sparks, then seconds the plume takes to grow to its length.</summary>
    public float SparkSeconds { get; init; }
    public float GrowSeconds { get; init; }

    /// <summary>How many sparks an ignition throws, and how fast they fly in exit radii a second.</summary>
    public int Sparks { get; init; }
    public float SparkSpeed { get; init; }

    /// <summary>A puff's extra strength the moment it starts, and the seconds it takes to settle to a steady jet.</summary>
    public float Burst { get; init; }
    public float BurstSeconds { get; init; }

    /// <summary>True for a cold gas puff.</summary>
    public bool IsPuff => Kind == "puff";

    internal void Check(string path, string id)
    {
        static bool Colour(float[] c) => c.Length == 3 && c[0] is >= 0 and <= 4 && c[1] is >= 0 and <= 4 && c[2] is >= 0 and <= 4;
        if (Kind is not ("flame" or "puff") || !Colour(Core) || !Colour(Edge) || !Colour(Spark)
            || !(Brightness is > 0 and <= 40) || !(Length is > 0 and <= 40) || !(MinLength is > 0 and <= 1) || !(Spread is >= 0 and <= 6)
            || !(Flicker is >= 0 and <= 1) || !(Turbulence is >= 0 and <= 1) || !(Diamonds is >= 0 and <= 4) || !(DiamondSpacing is > 0 and <= 10)
            || !(SparkSeconds is >= 0 and <= 1) || !(GrowSeconds is > 0 and <= 2) || Sparks is < 0 or > PropulsionLook.MaxSparks
            || !(SparkSpeed is >= 0 and <= 200) || (Sparks > 0 && !(SparkSeconds > 0 && SparkSpeed > 0))
            || !(Burst is >= 0 and <= 10) || !(BurstSeconds is > 0 and <= 2))
            throw new InvalidDataException($"{path}: plume {id} is out of range");
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

    /// <summary>How much longer and brighter boost makes a plume.</summary>
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
        foreach (var (id, model) in look.Models)
        {
            model.Check(path, id);
            if (!look.Plumes.ContainsKey(model.Plume))
                throw new InvalidDataException($"{path}: model {id} names plume {model.Plume}, which is not there");
        }
        if (!(look.BoostLength is >= 1 and <= 4) || !(look.BoostBrightness is >= 1 and <= 4)
            || look.MaxPlumes is < 1 or > PlumeCapacity || !(look.MinPixels is >= 0 and <= 64))
            throw new InvalidDataException($"{path}: boost is 1 to 4, maxPlumes 1 to {PlumeCapacity}, minPixels 0 to 64");
        look.PlumeOrder = [.. look.Plumes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value)];
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

/// <summary>One nozzle of a design: where its exit is in the hull's frame and what it fires. Built with the mesh.</summary>
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
/// Builds the models of thrust parts from a block's box and facing: a housing, then a collar, then bell nozzles
/// (engines and hot thrusters) or a plate of small cones (cold gas), the nozzles pointing along the facing. Runs where
/// hull meshes are built, on a worker; never in a frame.
/// </summary>
internal static class PropulsionModels
{
    /// <summary>
    /// The length a normal is given to carry a shade to the mesh shader: 1 is ordinary plating, 2 plus a shade below
    /// 1 is bare metal at that brightness. Every other mesh has unit normals.
    /// </summary>
    public static float Tag(float shade) => shade <= 0 ? 1f : 2f + shade;

    /// <summary>The model look of a block, or null for a block drawn as its plain shape.</summary>
    public static PropulsionModelLook? ModelOf(HullDesign design, int block, PropulsionLook look) =>
        design.Shapes[block] == BlockShape.Box && design.Types[block].Thrust > 0 && look.Models.TryGetValue(design.Types[block].Id, out var model) ? model : null;

    /// <summary>
    /// Adds the model of <paramref name="block"/> to the triangle lists, and its nozzles to <paramref name="nozzles"/>
    /// when given. Returns false, adding nothing, for a block drawn as its plain shape.
    /// </summary>
    public static bool Add(HullDesign design, int block, PropulsionLook look, List<MeshVertex> vertices, List<uint> indices, List<ThrustNozzle>? nozzles)
    {
        var model = ModelOf(design, block, look);
        if (model is null)
            return false;
        var box = design.Boxes[block];
        var facing = box.Facing;
        var axis = Facings.Axis(facing);
        var u = (axis + 1) % 3;
        var v = (axis + 2) % 3;
        var half = design.BlockHalfExtents[block];
        var frame = new Frame(design.BlockOffsets[block], Facings.Vector(facing), Unit(u), Unit(v), half[axis]);
        var hu = half[u];
        var hv = half[v];
        var depth = 2f * half[axis];
        var cells = design.BlockSize > 0 ? design.BlockSize : 1f;

        // The face is split into nozzles: by cells for a cluster, otherwise into squares of the shorter side.
        int nu, nv;
        if (model.PerCell > 0)
        {
            nu = Math.Clamp((int)MathF.Round(2f * hu / cells * model.PerCell), 1, 8);
            nv = Math.Clamp((int)MathF.Round(2f * hv / cells * model.PerCell), 1, 8);
        }
        else
        {
            var side = MathF.Min(hu, hv);
            nu = Math.Clamp((int)MathF.Round(hu / side), 1, 8);
            nv = Math.Clamp((int)MathF.Round(hv / side), 1, 8);
        }
        var share = model.PlateThickness > 0 ? MathF.Max(model.PlateShare, model.HousingShare) : model.HousingShare;
        var pitchU = 2f * hu * share / nu;
        var pitchV = 2f * hv * share / nv;
        var exit = 0.5f * MathF.Min(pitchU, pitchV) * model.ExitShare / (1f + model.Lip);
        var nozzleLength = MathF.Min(model.NozzleLength * exit, model.MaxNozzleDepth * depth);
        var collar = MathF.Min(model.CollarLength * exit, 0.1f * depth);
        var plate = model.PlateThickness * depth;
        // Everything behind the nozzles is housing, so a deeper block grows its housing and keeps its nozzle.
        var nozzleStart = depth - nozzleLength;
        var plateStart = nozzleStart - collar - plate;
        var housingEnd = MathF.Max(plateStart, 0.05f * depth);

        var housing = PropulsionModels.Tag(model.HousingShade);
        var metal = PropulsionModels.Tag(model.NozzleShade);
        var throat = PropulsionModels.Tag(model.ThroatShade);
        var backCovered = (design.ExteriorFaces[block] & (1 << (byte)Facings.Opposite(facing))) == 0;
        AddBox(vertices, indices, frame, hu * model.HousingShare, hv * model.HousingShare, 0, housingEnd, housing, !backCovered);
        if (plate > 0)
            AddBox(vertices, indices, frame, hu * model.PlateShare, hv * model.PlateShare, housingEnd, housingEnd + plate, housing, true);

        var throatRadius = exit * model.ThroatShare;
        var wall = exit * model.Lip;
        var plume = look.PlumeIndex(model.Plume);
        var fuel = design.Types[block].Fuel;
        for (var i = 0; i < nu; i++)
        {
            for (var j = 0; j < nv; j++)
            {
                var x = (i + 0.5f) * pitchU - hu * share;
                var y = (j + 0.5f) * pitchV - hv * share;
                if (collar > 0)
                    AddCollar(vertices, indices, frame, x, y, housingEnd + plate, nozzleStart, throatRadius * model.CollarRadius, metal, model.Segments);
                AddNozzle(vertices, indices, frame, x, y, nozzleStart, depth, throatRadius, exit, wall, model, metal, throat);
                nozzles?.Add(new ThrustNozzle(block, fuel, facing, frame.At(x, y, depth), exit, nozzleLength, plume, nozzles.Count));
            }
        }
        return true;
    }

    /// <summary>A block's own frame: x and y across its nozzle face, z from its back face (0) out along its facing.</summary>
    private readonly struct Frame(Vector3 center, Vector3 n, Vector3 u, Vector3 v, float halfDepth)
    {
        public readonly Vector3 N = n, U = u, V = v;
        public Vector3 At(float x, float y, float z) => center + U * x + V * y + N * (z - halfDepth);
        public Vector3 Dir(float x, float y, float z) => U * x + V * y + N * z;
    }

    private static Vector3 Unit(int axis)
    {
        var vector = Vector3.Zero;
        vector[axis] = 1f;
        return vector;
    }

    // A housing or plate: an axis-aligned box in the block's frame, from z0 to z1. Its back face is left out where a
    // neighbour covers the block's back.
    private static void AddBox(List<MeshVertex> vertices, List<uint> indices, in Frame f, float hx, float hy, float z0, float z1, float tag, bool back)
    {
        var zc = 0.5f * (z0 + z1);
        var hz = 0.5f * (z1 - z0);
        Quad(vertices, indices, f, tag, new(0, 0, 1), new(hx, 0, 0), new(0, hy, 0), new(0, 0, zc + hz));
        if (back)
            Quad(vertices, indices, f, tag, new(0, 0, -1), new(hx, 0, 0), new(0, hy, 0), new(0, 0, zc - hz));
        Quad(vertices, indices, f, tag, new(1, 0, 0), new(0, hy, 0), new(0, 0, hz), new(hx, 0, zc));
        Quad(vertices, indices, f, tag, new(-1, 0, 0), new(0, hy, 0), new(0, 0, hz), new(-hx, 0, zc));
        Quad(vertices, indices, f, tag, new(0, 1, 0), new(hx, 0, 0), new(0, 0, hz), new(0, hy, zc));
        Quad(vertices, indices, f, tag, new(0, -1, 0), new(hx, 0, 0), new(0, 0, hz), new(0, -hy, zc));
    }

    private static void Quad(List<MeshVertex> vertices, List<uint> indices, in Frame f, float tag, Vector3 normal, Vector3 a, Vector3 b, Vector3 center)
    {
        var first = (uint)vertices.Count;
        var n = f.Dir(normal.X, normal.Y, normal.Z);
        Put(vertices, f.At(center.X - a.X - b.X, center.Y - a.Y - b.Y, center.Z - a.Z - b.Z), n, tag);
        Put(vertices, f.At(center.X + a.X - b.X, center.Y + a.Y - b.Y, center.Z + a.Z - b.Z), n, tag);
        Put(vertices, f.At(center.X + a.X + b.X, center.Y + a.Y + b.Y, center.Z + a.Z + b.Z), n, tag);
        Put(vertices, f.At(center.X - a.X + b.X, center.Y - a.Y + b.Y, center.Z - a.Z + b.Z), n, tag);
        Triangle(vertices, indices, first, first + 1, first + 2, n);
        Triangle(vertices, indices, first, first + 2, first + 3, n);
    }

    // A short cylinder between the housing and the nozzle's throat, closed at its outer end.
    private static void AddCollar(List<MeshVertex> vertices, List<uint> indices, in Frame f, float x, float y, float z0, float z1, float radius, float tag, int segments)
    {
        var first = (uint)vertices.Count;
        for (var s = 0; s <= segments; s++)
        {
            var (c, sn) = Around(s, segments);
            var n = f.Dir(c, sn, 0);
            Put(vertices, f.At(x + c * radius, y + sn * radius, z0), n, tag);
            Put(vertices, f.At(x + c * radius, y + sn * radius, z1), n, tag);
        }
        for (var s = 0; s < segments; s++)
        {
            var a = first + (uint)(2 * s);
            Triangle(vertices, indices, a, a + 2, a + 3, vertices[(int)a].Normal);
            Triangle(vertices, indices, a, a + 3, a + 1, vertices[(int)a].Normal);
        }
        Disc(vertices, indices, f, x, y, z1, 0, radius, tag, segments, 1);
    }

    // A nozzle: the outer wall, the lip at the exit, the inner wall facing the axis, and the dark plate at the throat.
    private static void AddNozzle(List<MeshVertex> vertices, List<uint> indices, in Frame f, float x, float y, float z0, float z1, float throat, float exit, float wall, PropulsionModelLook model, float metal, float dark)
    {
        var rings = model.Rings;
        var segments = model.Segments;
        Span<float> radius = stackalloc float[rings + 1];
        Span<float> z = stackalloc float[rings + 1];
        for (var r = 0; r <= rings; r++)
        {
            var t = (float)r / rings;
            // A bell opens quickly from the throat and runs nearly straight at the exit; a curve of 1 is a cone.
            radius[r] = throat + (exit - throat) * (1f - MathF.Pow(1f - t, model.BellCurve));
            z[r] = z0 + (z1 - z0) * t;
        }
        Surface(vertices, indices, f, x, y, radius, z, wall, 1f, metal, segments);
        Surface(vertices, indices, f, x, y, radius, z, 0, -1f, metal, segments);
        Disc(vertices, indices, f, x, y, z1, exit, exit + wall, metal, segments, 1);
        Disc(vertices, indices, f, x, y, z0, 0, throat, dark, segments, 1);
    }

    // A surface of revolution about the nozzle's axis, its normals outward (side 1) or toward the axis (side -1).
    private static void Surface(List<MeshVertex> vertices, List<uint> indices, in Frame f, float x, float y, ReadOnlySpan<float> radius, ReadOnlySpan<float> z, float offset, float side, float tag, int segments)
    {
        var first = (uint)vertices.Count;
        var rings = radius.Length - 1;
        for (var r = 0; r <= rings; r++)
        {
            var a = Math.Max(r - 1, 0);
            var b = Math.Min(r + 1, rings);
            // The profile's slope tilts the normal back toward the throat as the bell widens.
            var slope = (radius[b] - radius[a]) / MathF.Max(z[b] - z[a], 1e-6f);
            for (var s = 0; s <= segments; s++)
            {
                var (c, sn) = Around(s, segments);
                var n = Vector3.Normalize(f.Dir(c, sn, -slope)) * side;
                var rr = radius[r] + offset;
                Put(vertices, f.At(x + c * rr, y + sn * rr, z[r]), n, tag);
            }
        }
        var row = (uint)(segments + 1);
        for (var r = 0; r < rings; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                var a = first + (uint)r * row + (uint)s;
                var n = vertices[(int)a].Normal;
                Triangle(vertices, indices, a, a + 1, a + row + 1, n);
                Triangle(vertices, indices, a, a + row + 1, a + row, n);
            }
        }
    }

    // A flat ring (or a disc, inner radius 0) across the axis at z, facing along it.
    private static void Disc(List<MeshVertex> vertices, List<uint> indices, in Frame f, float x, float y, float z, float inner, float outer, float tag, int segments, float side)
    {
        var first = (uint)vertices.Count;
        var n = f.Dir(0, 0, side);
        for (var s = 0; s <= segments; s++)
        {
            var (c, sn) = Around(s, segments);
            Put(vertices, f.At(x + c * inner, y + sn * inner, z), n, tag);
            Put(vertices, f.At(x + c * outer, y + sn * outer, z), n, tag);
        }
        for (var s = 0; s < segments; s++)
        {
            var a = first + (uint)(2 * s);
            Triangle(vertices, indices, a, a + 1, a + 3, n);
            if (inner > 0)
                Triangle(vertices, indices, a, a + 3, a + 2, n);
        }
    }

    private static (float Cos, float Sin) Around(int s, int segments)
    {
        var angle = s % segments * (MathF.Tau / segments);
        return (MathF.Cos(angle), MathF.Sin(angle));
    }

    private static void Put(List<MeshVertex> vertices, Vector3 position, Vector3 normal, float tag)
    {
        var n = Vector3.Normalize(normal) * tag;
        vertices.Add(new MeshVertex { Position = new float3(position.X, position.Y, position.Z), Normal = new float3(n.X, n.Y, n.Z) });
    }

    // Winds every triangle counter-clockwise seen from the side its normal points to, whichever way the block's frame
    // turns: a facing against its axis mirrors the frame, which would otherwise turn half the faces inside out.
    private static void Triangle(List<MeshVertex> vertices, List<uint> indices, uint a, uint b, uint c, float3 normal)
    {
        var pa = vertices[(int)a].Position;
        var pb = vertices[(int)b].Position;
        var pc = vertices[(int)c].Position;
        var cross = Vector3.Cross(new Vector3(pb.X - pa.X, pb.Y - pa.Y, pb.Z - pa.Z), new Vector3(pc.X - pa.X, pc.Y - pa.Y, pc.Z - pa.Z));
        if (cross.LengthSquared() < 1e-14f)
            return;
        if (Vector3.Dot(cross, new Vector3(normal.X, normal.Y, normal.Z)) < 0)
            (b, c) = (c, b);
        indices.Add(a);
        indices.Add(b);
        indices.Add(c);
    }

    private static void Triangle(List<MeshVertex> vertices, List<uint> indices, uint a, uint b, uint c, Vector3 normal) =>
        Triangle(vertices, indices, a, b, c, new float3(normal.X, normal.Y, normal.Z));
}
