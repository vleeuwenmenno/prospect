using System.Numerics;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>Whole grid coordinates or extents. The position is the centre of the minimum occupied cell.</summary>
public readonly record struct GridSize(int X, int Y, int Z);

/// <summary>
/// A whole block; its index in the design is its permanent identity. <paramref name="Facing"/> is the way it faces:
/// a thrust part pushes the hull the opposite way, and for any other block it only says how the block sits.
/// </summary>
public readonly record struct DesignBox(GridSize At, GridSize Size, BlockType Type, BlockShape Shape = BlockShape.Box, BlockFacing Facing = BlockFacing.Back);

/// <summary>Content-owned bounds on design complexity and dimensions.</summary>
public sealed record DesignLimits(int MaxBoxSide, int MaxDesignSide, int MaxBlocks)
{
    /// <summary>Damage-safe shell vertices per design.</summary>
    public int SurfaceVertices { get; init; } = GeometryAdmission.MaxVertices;
    /// <summary>Damage-safe shell indices per design, three a triangle.</summary>
    public int SurfaceIndices { get; init; } = GeometryAdmission.MaxIndices;
    /// <summary>Maximum face-neighbour checks during admission.</summary>
    public int GeometryPairTests { get; init; } = 6000000;
    /// <summary>The shape every shield shell takes, shared so that a server and its clients admit the same designs.</summary>
    public ShellShape Shield { get; init; } = ShellShape.Default;
    /// <summary>The most parts a design may have mounted on its blocks' faces.</summary>
    public int MaxMounts { get; init; } = 1024;
    /// <summary>
    /// The most tank blocks a design may have, at most <see cref="TankFuel.MaxTanks"/>: a hull's fuel is kept tank
    /// by tank, and a snapshot tells the pilot of every one.
    /// </summary>
    public int MaxTanks { get; init; } = TankFuel.MaxTanks;

    /// <summary>The most drone bays a design may have, at most <see cref="DroneBayRules.MaxBays"/>: the pilot is told of each.</summary>
    public int MaxBays { get; init; } = DroneBayRules.MaxBays;
    internal static DesignLimits Read(JsonElement e, string source)
    {
        var limits = new DesignLimits(e.GetProperty("maxBoxSide"u8).GetInt32(), e.GetProperty("maxDesignSide"u8).GetInt32(), e.GetProperty("maxBlocks"u8).GetInt32())
        {
            SurfaceVertices = e.GetProperty("surfaceVertices"u8).GetInt32(),
            SurfaceIndices = e.GetProperty("surfaceIndices"u8).GetInt32(),
            GeometryPairTests = e.GetProperty("geometryPairTests"u8).GetInt32(),
            Shield = new ShellShape(e.GetProperty("shieldRoundness"u8).GetSingle(), e.GetProperty("shieldBlend"u8).GetSingle()),
            MaxMounts = e.GetProperty("maxMounts"u8).GetInt32(),
            MaxTanks = e.TryGetProperty("maxTanks"u8, out var tanks) ? tanks.GetInt32() : TankFuel.MaxTanks,
            MaxBays = e.TryGetProperty("maxBays"u8, out var bays) ? bays.GetInt32() : DroneBayRules.MaxBays,
        };
        if (limits.MaxBays is < 0 or > DroneBayRules.MaxBays)
            throw new InvalidDataException($"{source}: maxBays must be from 0 to {DroneBayRules.MaxBays}");
        if (limits.MaxTanks is < 0 or > TankFuel.MaxTanks)
            throw new InvalidDataException($"{source}: maxTanks must be from 0 to {TankFuel.MaxTanks}");
        if (limits.MaxMounts is < 0 or > 65535)
            throw new InvalidDataException($"{source}: maxMounts must be from 0 to 65535");
        if (limits.MaxBoxSide < 1 || limits.MaxDesignSide < limits.MaxBoxSide || limits.MaxBlocks < 1 || limits.SurfaceVertices is < 36 or > GeometryAdmission.MaxVertices || limits.SurfaceIndices is < 36 or > GeometryAdmission.MaxIndices || limits.GeometryPairTests is < 6 or > 6000000 || !limits.Shield.IsValid)
            throw new InvalidDataException($"{source}: invalid design limits");
        return limits;
    }
}

/// <summary>
/// Immutable, validated box data: what a design file says, in file order. A <see cref="HullDesign"/> is built from
/// one, and tools read and write them.
/// </summary>
public sealed class BoxDesign
{
    private readonly DesignBox[] _blocks;
    private readonly DesignMount[] _mounts;
    private readonly int[] _mountStart;
    /// <summary>Blocks in file order, never spatially reordered.</summary>
    public ReadOnlySpan<DesignBox> Blocks => _blocks;
    /// <summary>
    /// The parts mounted on block faces, in the order of <see cref="FaceMounts.Compare"/>: by parent block, so the
    /// mounts of one block follow each other (<see cref="MountsOf"/>).
    /// </summary>
    public ReadOnlySpan<DesignMount> Mounts => _mounts;
    /// <summary>The mounts of one block, as a range of <see cref="Mounts"/>: from <paramref name="start"/> up to <paramref name="end"/>.</summary>
    public void MountsOf(int block, out int start, out int end) { start = _mountStart[block]; end = _mountStart[block + 1]; }
    /// <summary>Design display name.</summary>
    public string Name { get; }
    /// <summary>Metres per grid cell.</summary>
    public float BlockSize { get; }
    /// <summary>Design category.</summary>
    public HullClass Class { get; }
    /// <summary>Design colour.</summary>
    public Vector3 Color { get; }
    /// <summary>Original centre in grid coordinates, retained after damage.</summary>
    public Double3 Center { get; }
    internal static readonly string[] StatNames = ["mass", "hull", "thrust", "torque", "powerOutput", "powerDraw", "energyStorage", "shield", "shieldRecharge", "cargo", "crew", "ftlPower", "fuelBurn", "fuelCapacity"];

    /// <summary>
    /// Scales a numeric stat; material multiplication can be applied after this result. The power is taken with
    /// <see cref="DetMath"/>, so a server and a client on different machines get the same figure to the last bit;
    /// a one-cell block and the exponents 0 and 1 need no power and are exact, and so are a half and 2, which are
    /// a square root and a square: both are the same on every machine, and a whole number stays whole.
    /// </summary>
    public static double Scale(BlockType type, string stat, double value, long cells)
    {
        if (cells < 1) throw new ArgumentOutOfRangeException(nameof(cells));
        if (cells == 1) return value;
        var exponent = type.SizeRules.TryGetValue(stat, out var power) ? power : 1;
        return value * exponent switch
        {
            1 => cells,
            0 => 1,
            0.5 => Math.Sqrt(cells),
            2 => (double)cells * cells,
            _ => DetMath.Exp(exponent * DetMath.Log((double)cells)),
        };
    }

    /// <summary>Scales using half the bounding volume, preserving the whole-cell arithmetic for even volumes.</summary>
    public static double ScaleHalf(BlockType type, string stat, double value, long cells)
    {
        if (cells < 1) throw new ArgumentOutOfRangeException(nameof(cells));
        if (cells % 2 == 0) return Scale(type, stat, value, cells / 2);
        var volume = cells * 0.5;
        var exponent = type.SizeRules.TryGetValue(stat, out var power) ? power : 1;
        return value * exponent switch { 1 => volume, 0 => 1, 0.5 => Math.Sqrt(volume), 2 => volume * volume, _ => DetMath.Exp(exponent * DetMath.Log(volume)) };
    }

    internal static double Scaled(DesignBox b, string stat, double value) =>
        b.Shape == BlockShape.Box ? Scale(b.Type, stat, value, Volume(b)) : ScaleHalf(b.Type, stat, value, Volume(b));

    /// <summary>Mass centroid in grid coordinates; bounding centres remain unchanged.</summary>
    public static Double3 Centroid(DesignBox b)
    {
        double x = b.At.X + (b.Size.X - 1) * 0.5, y = b.At.Y + (b.Size.Y - 1) * 0.5, z = b.At.Z + (b.Size.Z - 1) * 0.5;
        if (b.Shape == BlockShape.Box) return new Double3(x, y, z);
        BlockShapes.Decode(b.Shape, out _, out var u, out var v, out var su, out var sv);
        Span<double> c = [x, y, z]; Span<int> size = [b.Size.X, b.Size.Y, b.Size.Z];
        c[u] -= su * size[u] / 6.0; c[v] -= sv * size[v] / 6.0;
        return new Double3(c[0], c[1], c[2]);
    }

    /// <summary>
    /// The whole people a block of quarters holds. A power that should be a whole number can come out a hair
    /// under it, and rounding that down would cost a person, so what is within a billionth of the next whole
    /// number counts as it.
    /// </summary>
    public static double WholeCrew(BlockType type, long cells)
    {
        var scaled = Scale(type, "crew", type.Crew, cells);
        return Math.Floor(scaled + Math.Abs(scaled) * 1e-9);
    }

    internal static IReadOnlyDictionary<string, double> ReadSizeRules(JsonElement block, string source)
    {
        var result = new Dictionary<string, double>();
        if (block.TryGetProperty("sizeRules"u8, out var rules))
            foreach (var entry in rules.EnumerateObject())
            {
                if (!StatNames.Contains(entry.Name) || !entry.Value.TryGetProperty("volumeExponent"u8, out var e) || !e.TryGetDouble(out var power) || !double.IsFinite(power) || power < 0 || power > 2)
                    throw new InvalidDataException($"{source}: invalid size rule {entry.Name}; volumeExponent must be 0..2");
                result.Add(entry.Name, power);
            }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(result);
    }

    /// <summary>
    /// The version <see cref="Write"/> writes: 2 added sizes, 3 shapes, 4 facings, 5 face mounts for thrusters, 6 for
    /// module scanners and 7 a mount's action group (<c>"group"</c>, written only when set; older files have none).
    /// </summary>
    public const int FormatVersion = 7;

    /// <summary>
    /// Loads legacy unit cells, version 2 boxes, version 3 shapes, version 4 facings, version 5 face mounts or version
    /// 6, where module scanners mount too, with source-labelled validation. A block of an older version, or one that
    /// names no facing, faces <see cref="BlockFacing.Back"/>. A part that now mounts on a face was a block up to the
    /// version its <see cref="MountSpec.LastBlockVersion"/> names: such a block is kept as its type's
    /// <see cref="MountSpec.LegacyBlock"/> and carries the part on the face it pointed from
    /// (<see cref="ConvertLegacy"/>); a scanner that has no face to go on is refused, never dropped.
    /// </summary>
    public static BoxDesign Load(string path, BlockCatalog catalog)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            return Parse(json.RootElement, catalog, path);
        }
        catch (JsonException e) { throw new InvalidDataException($"{path}: invalid JSON: {e.Message}", e); }
    }

    /// <summary>
    /// Reads a design file's bytes, for a file nobody here wrote (a design a player saved): the same reader and the
    /// same checks as <see cref="Load"/>, with the JSON's nesting kept shallow. Anything wrong with the file,
    /// whatever shape it takes, comes out as an <see cref="InvalidDataException"/> naming <paramref name="source"/>.
    /// </summary>
    public static BoxDesign Read(ReadOnlyMemory<byte> utf8, BlockCatalog catalog, string source)
    {
        try
        {
            using var json = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 8 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: not a design");
            return Parse(json.RootElement, catalog, source);
        }
        catch (JsonException e) { throw new InvalidDataException($"{source}: invalid JSON: {e.Message}", e); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        { throw new InvalidDataException($"{source}: invalid design: {e.Message}", e); }
    }

    internal static BoxDesign Parse(JsonElement root, BlockCatalog catalog, string source)
    {
        var index = -1;
        try
        {
            var version = root.TryGetProperty("version"u8, out var v) ? v.GetInt32() : 1;
            if (version is < 1 or > FormatVersion) throw new InvalidDataException($"{source}: unsupported design version {version}");
            var list = new List<DesignBox>();
            var mounts = new List<DesignMount>();
            var legacy = new List<(int Block, BlockFacing Facing, BlockType Type)>();
            foreach (var entry in root.GetProperty("blocks"u8).EnumerateArray())
            {
                index++;
                if (index >= catalog.Limits.MaxBlocks) throw new InvalidDataException($"{source}: block {index}: too many blocks (limit {catalog.Limits.MaxBlocks})");
                var size = new GridSize(1, 1, 1);
                if (entry.TryGetProperty("size"u8, out var z))
                {
                    if (version < 2) throw new InvalidDataException($"{source}: block {index}: size requires version 2");
                    size = Grid(z);
                }
                BlockType type;
                try { type = catalog.Get(entry.GetProperty("type"u8).GetString()!); }
                catch (InvalidDataException e) { throw new InvalidDataException($"{source}: block {index}: {e.Message}", e); }
                var shape = BlockShape.Box;
                if (entry.TryGetProperty("shape"u8, out var sh))
                {
                    if (version < 3) throw new InvalidDataException($"{source}: block {index}: Shape requires version 3.");
                    try { shape = BlockShapes.Parse(sh.GetString()); }
                    catch (InvalidDataException e) { throw new InvalidDataException($"{source}: block {index}: {e.Message}", e); }
                }
                var facing = BlockFacing.Back;
                if (entry.TryGetProperty("facing"u8, out var fc))
                {
                    if (version < 4) throw new InvalidDataException($"{source}: block {index}: Facing requires version 4.");
                    try { facing = Facings.Parse(fc.GetString()); }
                    catch (InvalidDataException e) { throw new InvalidDataException($"{source}: block {index}: {e.Message}", e); }
                }
                if (type.Mount is { } spec)
                {
                    if (version > spec.LastBlockVersion) throw new InvalidDataException($"{source}: block {index}: a {type.Name} mounts on a face; list it under \"mounts\"");
                    legacy.Add((index, facing, type));
                    type = catalog.Get(spec.LegacyBlock);
                    facing = BlockFacing.Back;
                }
                list.Add(new DesignBox(Grid(entry.GetProperty("at"u8)), size, type, shape, facing));
            }
            if (root.TryGetProperty("mounts"u8, out var listed))
            {
                if (version < 5) throw new InvalidDataException($"{source}: mounts require version 5");
                var at = -1;
                foreach (var entry in listed.EnumerateArray())
                {
                    at++;
                    if (at >= catalog.Limits.MaxMounts) throw new InvalidDataException($"{source}: mount {at}: too many mounted parts (limit {catalog.Limits.MaxMounts})");
                    try
                    {
                        var place = entry.GetProperty("at"u8);
                        if (place.GetArrayLength() != 2) throw new FormatException("expected two face positions");
                        var group = 0;
                        if (entry.TryGetProperty("group"u8, out var listedGroup))
                        {
                            if (version < 7) throw new FormatException("an action group requires version 7");
                            group = listedGroup.GetInt32();
                            if (!ActionGroups.Valid(group)) throw new FormatException($"action group {group} is not 0 to {ActionGroups.Count}");
                        }
                        mounts.Add(new DesignMount(entry.GetProperty("on"u8).GetInt32(), Facings.Parse(entry.GetProperty("face"u8).GetString()), place[0].GetInt32(), place[1].GetInt32(), catalog.Get(entry.GetProperty("type"u8).GetString()!), (byte)group));
                    }
                    catch (Exception e) when (e is InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
                    { throw new InvalidDataException($"{source}: mount {at}: {e.Message}", e); }
                }
            }
            ConvertLegacy(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), legacy, mounts, catalog.Limits, source);
            RequireOneDriveKind(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), source);
            var c = root.GetProperty("color"u8);
            var kind = root.GetProperty("class"u8).GetString() switch { "ship" => HullClass.Ship, "fighter" => HullClass.Fighter, "station" => HullClass.Station, _ => throw new InvalidDataException($"{source}: unknown class") };
            return new BoxDesign(root.GetProperty("name"u8).GetString()!, root.GetProperty("blockSize"u8).GetSingle(), list.ToArray(), catalog.Limits, source, kind, new Vector3(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle()), mounts.ToArray());
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or IndexOutOfRangeException)
        { throw new InvalidDataException($"{source}: block {index}: invalid design: {e.Message}", e); }
    }

    /// <summary>
    /// Gives the blocks of an old file that were thrusters their thrusters back as mounts. Each such block, by now
    /// plating, gets as many of the part as fit on every cell of the face it pointed from, in rows from the face's
    /// low corner: four cold gas thrusters a cell, which push what a one-cell block of them did. A part with no room
    /// (a block or an earlier part in front of that face, or the face being a wedge's slope) is left out, as is
    /// everything past the content's count: an old thruster whose nozzle was covered pushes no more.
    /// A module scanner is one part however large its block was (its figures never grew with size): it takes the
    /// middle of the face it pointed from, or of the first face with room (<see cref="ScannerFaces"/>). With no
    /// face that has room the file is refused, since a scanner dropped silently would leave a ship that cannot scan.
    /// </summary>
    private static void ConvertLegacy(ReadOnlySpan<DesignBox> blocks, List<(int Block, BlockFacing Facing, BlockType Type)> legacy, List<DesignMount> mounts, DesignLimits limits, string source)
    {
        foreach (var (block, facing, type) in legacy)
        {
            var spec = type.Mount!;
            if (type.Module.Family != ModuleFamily.None)
            {
                if (mounts.Count >= limits.MaxMounts)
                    throw new InvalidDataException($"{source}: block {block}: a {type.Name} cannot be converted to a mounted part: the design has too many mounted parts (limit {limits.MaxMounts})");
                var placed = false;
                foreach (var face in ScannerFaces)
                {
                    var width = FaceMounts.Of(blocks[block].Size, (Facings.Axis(face) + 1) % 3) * FaceMounts.PerCell;
                    var height = FaceMounts.Of(blocks[block].Size, (Facings.Axis(face) + 2) % 3) * FaceMounts.PerCell;
                    var mount = new DesignMount(block, face, (width - spec.Side) / 2, (height - spec.Side) / 2, type);
                    if (FaceMounts.Check(blocks, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mounts), mount) != DraftEdit.Done)
                        continue;
                    mounts.Add(mount);
                    placed = true;
                    break;
                }

                if (!placed)
                    throw new InvalidDataException($"{source}: block {block}: a {type.Name} cannot be converted to a mounted part: every face of the block is covered. Free a face and load the design again");
                continue;
            }

            var n = Facings.Axis(facing);
            var across = FaceMounts.PerCell / spec.Side;
            var pitch = FaceMounts.PerCell / across;
            var inset = (pitch - spec.Side) / 2;
            var columns = FaceMounts.Of(blocks[block].Size, (n + 1) % 3) * across;
            var rows = FaceMounts.Of(blocks[block].Size, (n + 2) % 3) * across;
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns && mounts.Count < limits.MaxMounts; column++)
                {
                    var mount = new DesignMount(block, facing, column * pitch + inset, row * pitch + inset, type);
                    if (FaceMounts.Check(blocks, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mounts), mount) == DraftEdit.Done)
                        mounts.Add(mount);
                }
            }
        }
    }

    /// <summary>
    /// The faces a converted scanner tries, in order: top first, where an antenna or dish sees most of the sky, then
    /// the sides and the ends, and the underside last. A scanner block's facing never meant anything (it only said
    /// how the block sat), so it does not choose.
    /// </summary>
    private static readonly BlockFacing[] ScannerFaces = [BlockFacing.Up, BlockFacing.Right, BlockFacing.Left, BlockFacing.Back, BlockFacing.Front, BlockFacing.Down];

    /// <summary>
    /// The first block whose kind of FTL drive differs from an earlier drive's, or -1 when every drive is of one
    /// kind; <paramref name="first"/> is then the first drive, or -1 with none. Any number of drives may be fitted,
    /// normal and compact alike, as long as they are of one <see cref="FtlKind"/>. Allocates nothing.
    /// </summary>
    public static int MixedDrive(ReadOnlySpan<DesignBox> blocks, out int first)
    {
        first = -1;
        for (var i = 0; i < blocks.Length; i++)
        {
            var kind = blocks[i].Type.Ftl;
            if (kind == FtlKind.None)
                continue;
            if (first < 0)
                first = i;
            else if (kind != blocks[first].Type.Ftl)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Refuses a design with FTL drives of more than one kind. The design files, received designs and the editor's
    /// drafts are all held to it, before any geometry is built. The constructor does not ask it, so arithmetic
    /// tests of drive precedence can still build such a set of blocks; no such design is ever admitted.
    /// </summary>
    public static void RequireOneDriveKind(ReadOnlySpan<DesignBox> blocks, string source)
    {
        var mixed = MixedDrive(blocks, out var first);
        if (mixed >= 0)
            throw new InvalidDataException($"{source}: blocks: a ship takes FTL drives of one kind only, but block {mixed} is a {DriveName(blocks[mixed].Type.Ftl)} drive and block {first} a {DriveName(blocks[first].Type.Ftl)} drive. Remove one kind");
    }

    private static string DriveName(FtlKind kind) => kind switch { FtlKind.Hyperspace => "hyperspace", FtlKind.Wormhole => "wormhole", _ => "rift" };

    private static GridSize Grid(JsonElement e)
    {
        if (e.GetArrayLength() != 3) throw new FormatException("expected three grid integers");
        return new GridSize(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32());
    }

    /// <summary>
    /// Validates and owns a copy of generated boxes and of the parts mounted on them, which it puts in the order of
    /// <see cref="FaceMounts.Compare"/>. At least one core is required, as in legacy designs.
    /// </summary>
    public BoxDesign(string name, float blockSize, DesignBox[] blocks, DesignLimits limits, string source, HullClass kind = HullClass.Ship, Vector3 color = default, DesignMount[]? mounts = null)
    {
        Name = name; BlockSize = blockSize; Class = kind; Color = color;
        _blocks = (DesignBox[])blocks.Clone();
        _mounts = mounts is null ? [] : (DesignMount[])mounts.Clone();
        _mountStart = new int[blocks.Length + 1];
        if (!float.IsFinite(blockSize) || blockSize <= 0 || blocks.Length == 0 || blocks.Length > limits.MaxBlocks)
            throw new InvalidDataException($"{source}: block count or blockSize outside limits");
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        double mass = 0, x = 0, y = 0, z = 0;
        var cores = 0;
        var tanks = 0;
        var bays = 0;
        double crew = 0;
        for (var i = 0; i < blocks.Length; i++)
        {
            var b = blocks[i];

            // Held to the wire's bound as well as the content's: limits made in code are not read from a file.
            if (b.Type.Tank != FuelKind.None && ++tanks > Math.Min(limits.MaxTanks, TankFuel.MaxTanks))
                throw new InvalidDataException($"{source}: block {i}: too many tanks (limit {Math.Min(limits.MaxTanks, TankFuel.MaxTanks)})");
            if (b.Type.Bay is not null && ++bays > Math.Min(limits.MaxBays, DroneBayRules.MaxBays))
                throw new InvalidDataException($"{source}: block {i}: too many drone bays (limit {Math.Min(limits.MaxBays, DroneBayRules.MaxBays)})");
            if (!BlockShapes.IsValid(b.Shape)) throw new InvalidDataException($"{source}: block {i}: Unknown block shape.");
            if (!Facings.IsValid(b.Facing)) throw new InvalidDataException($"{source}: block {i}: Unknown block facing.");
            if (b.Type.Mount is not null) throw new InvalidDataException($"{source}: block {i}: a {b.Type.Name} mounts on a face and cannot be a block");
            if (!ShapeAllowed(b.Type, b.Shape)) throw new InvalidDataException($"{source}: block {i}: a {b.Type.Name} is never slanted: it is a box only");
            var side = MaxSide(b.Type, limits);
            foreach (var (at, size) in new[] { (b.At.X, b.Size.X), (b.At.Y, b.Size.Y), (b.At.Z, b.Size.Z) })
            {
                if (size < 1 || size > limits.MaxBoxSide || at < -limits.MaxDesignSide || (long)at + size > limits.MaxDesignSide)
                    throw new InvalidDataException($"{source}: block {i}: position or size outside limits");
                if (size > side)
                    throw new InvalidDataException($"{source}: block {i}: a {b.Type.Name} is at most {side} cells a side");
            }
            min = Vector3.Min(min, Low(b)); max = Vector3.Max(max, High(b));
            if (Vector3.Max(max - min, Vector3.Zero) is var extent && (extent.X > limits.MaxDesignSide || extent.Y > limits.MaxDesignSide || extent.Z > limits.MaxDesignSide))
                throw new InvalidDataException($"{source}: block {i}: design extent exceeds {limits.MaxDesignSide}");
            var m = Scaled(b, "mass", b.Type.Mass);
            var people = Scaled(b, "crew", b.Type.Crew);
            crew += Math.Floor(people + Math.Abs(people) * 1e-9);
            if (!double.IsFinite(m) || m <= 0 || crew > int.MaxValue)
                throw new InvalidDataException($"{source}: block {i}: mass or crew outside numeric range");
            var centre = b.Shape == BlockShape.Box ? new Double3(Mid(b).X, Mid(b).Y, Mid(b).Z) : Centroid(b);
            mass += m; x += m * centre.X; y += m * centre.Y; z += m * centre.Z;
            if (b.Type.Core) cores++;
        }
        if (cores == 0) throw new InvalidDataException($"{source}: blocks: a hull needs a core");
        var spatial = new Spatial(_blocks);
        for (var i = 0; i < blocks.Length; i++)
        {
            var other = spatial.Overlap(i);
            if (other >= 0) throw new InvalidDataException($"{source}: block {i} overlaps block {other}");
        }

        // The mounts, once the blocks they sit on are known to be sound. They weigh something, so they move the
        // centre the hull turns about like any block.
        foreach (var mount in _mounts)
            if (mount.Type is null) throw new InvalidDataException($"{source}: mounts: a mounted part names no type");
        Array.Sort(_mounts, FaceMounts.Compare);
        FaceMounts.Validate(_blocks, _mounts, limits, source);
        foreach (var mount in _mounts)
        {
            _mountStart[mount.Parent + 1]++;
            double m = mount.Type.Mass;
            if (!double.IsFinite(m) || m <= 0) throw new InvalidDataException($"{source}: mounts: mass outside numeric range");
            var centre = MountMiddle(mount);
            mass += m; x += m * centre.X; y += m * centre.Y; z += m * centre.Z;
        }

        for (var i = 0; i < blocks.Length; i++)
            _mountStart[i + 1] += _mountStart[i];
        var reach = (double)limits.MaxDesignSide * blockSize;
        if (mass * 2 * (reach * reach) > float.MaxValue)
            throw new InvalidDataException($"{source}: blocks: inertia exceeds numeric range");
        Center = new Double3(x / mass, y / mass, z / mass);
    }

    /// <summary>
    /// The middle of the box a mount fills, in grid coordinates: those of <see cref="Mid"/>, where cell <c>i</c> has
    /// its centre at <c>i</c> and its edges half a cell either side.
    /// </summary>
    public Double3 MountMiddle(in DesignMount mount)
    {
        Span<int> low = stackalloc int[3];
        Span<int> high = stackalloc int[3];
        FaceMounts.Box(_blocks[mount.Parent], mount, low, high);
        const double Tenths = 2.0 * FaceMounts.PerCell;
        return new Double3((low[0] + high[0]) / Tenths - 0.5, (low[1] + high[1]) / Tenths - 0.5, (low[2] + high[2]) / Tenths - 0.5);
    }

    /// <summary>The width, height and length of the box a mount fills, in cells.</summary>
    public static Vector3 MountSize(in DesignMount mount)
    {
        var spec = mount.Type.Mount!;
        var size = new Vector3(spec.Side / (float)FaceMounts.PerCell);
        size[Facings.Axis(mount.Face)] = spec.Depth / (float)FaceMounts.PerCell;
        return size;
    }

    /// <summary>A mount's centre from the design's original centre, in metres, in the hull's own frame.</summary>
    internal Vector3 MountOffset(in DesignMount mount)
    {
        var mid = MountMiddle(mount);
        return new Vector3((float)((mid.X - Center.X) * BlockSize), (float)((mid.Y - Center.Y) * BlockSize), (float)((mid.Z - Center.Z) * BlockSize));
    }

    /// <summary>
    /// Whether a block of <paramref name="type"/> may have <paramref name="shape"/>: every type may be a box, and only a
    /// type that is not <see cref="BlockType.BoxOnly"/> a wedge. The one rule, asked by design files, received
    /// designs (both through this constructor) and the editor's draft.
    /// </summary>
    public static bool ShapeAllowed(BlockType? type, BlockShape shape) => shape == BlockShape.Box || type is not { BoxOnly: true };

    /// <summary>The most cells a block of a type may measure along a side: the type's own limit, or the content's.</summary>
    public static int MaxSide(BlockType? type, DesignLimits limits) =>
        type is { MaxSide: > 0 } ? Math.Min(type.MaxSide, limits.MaxBoxSide) : limits.MaxBoxSide;

    /// <summary>The blocks as the array they are kept in, for the evaluator's inner loops. Never written to.</summary>
    internal DesignBox[] Boxes => _blocks;

    /// <summary>A block's centre from the design's original centre, in metres, in the hull's own frame.</summary>
    internal Vector3 Offset(in DesignBox b)
    {
        var mid = Mid(b);
        return new Vector3((float)((mid.X - Center.X) * BlockSize), (float)((mid.Y - Center.Y) * BlockSize), (float)((mid.Z - Center.Z) * BlockSize));
    }

    /// <summary>Half of a block's width, height and length, in metres.</summary>
    internal Vector3 HalfExtents(in DesignBox b) => new Vector3(b.Size.X, b.Size.Y, b.Size.Z) * (BlockSize * 0.5f);

    /// <summary>The mounts as the array they are kept in, for the evaluator's inner loops. Never written to.</summary>
    internal DesignMount[] MountArray => _mounts;

    /// <summary>Where each block's mounts start in <see cref="MountArray"/>; one entry more than there are blocks.</summary>
    internal int[] MountStart => _mountStart;

    /// <summary>Writes the current version, preserving block identity and metadata.</summary>
    public void Write(Stream stream)
    {
        using var w = new Utf8JsonWriter(stream);
        w.WriteStartObject(); w.WriteNumber("version"u8, FormatVersion); w.WriteString("name"u8, Name); w.WriteString("class"u8, Class.ToString().ToLowerInvariant()); w.WriteNumber("blockSize"u8, BlockSize);
        w.WriteStartArray("color"u8); w.WriteNumberValue(Color.X); w.WriteNumberValue(Color.Y); w.WriteNumberValue(Color.Z); w.WriteEndArray();
        w.WriteStartArray("blocks"u8);
        foreach (var b in _blocks)
        {
            w.WriteStartObject(); w.WriteString("type"u8, b.Type.Id);
            w.WriteString("shape"u8, BlockShapes.Name(b.Shape));
            w.WriteString("facing"u8, Facings.Name(b.Facing));
            WriteGrid(w, "at", b.At); WriteGrid(w, "size", b.Size); w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("mounts"u8);
        foreach (var m in _mounts)
        {
            w.WriteStartObject(); w.WriteString("type"u8, m.Type.Id); w.WriteNumber("on"u8, m.Parent); w.WriteString("face"u8, Facings.Name(m.Face));
            w.WriteStartArray("at"u8); w.WriteNumberValue(m.U); w.WriteNumberValue(m.V); w.WriteEndArray();
            if (m.Group != ActionGroups.None) w.WriteNumber("group"u8, m.Group);
            w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject();
    }
    private static void WriteGrid(Utf8JsonWriter w, string name, GridSize g)
    { w.WriteStartArray(name); w.WriteNumberValue(g.X); w.WriteNumberValue(g.Y); w.WriteNumberValue(g.Z); w.WriteEndArray(); }
    internal static long Volume(DesignBox b) => (long)b.Size.X * b.Size.Y * b.Size.Z;
    internal static Vector3 Low(DesignBox b) => new(b.At.X, b.At.Y, b.At.Z);
    internal static Vector3 High(DesignBox b) => Low(b) + new Vector3(b.Size.X, b.Size.Y, b.Size.Z);
    internal static Vector3 Mid(DesignBox b) => (Low(b) + High(b) - Vector3.One) * 0.5f;

    // A median tree bounds overlap queries without expanding large boxes into occupied cells.
    private sealed class Spatial
    {
        private readonly DesignBox[] _boxes;
        private readonly int[] _ids;
        private readonly (Vector3 Min, Vector3 Max)[] _bounds;
        internal Spatial(DesignBox[] boxes)
        { _boxes = boxes; _ids = Enumerable.Range(0, boxes.Length).ToArray(); _bounds = new (Vector3, Vector3)[boxes.Length * 4]; Build(1, 0, boxes.Length); }
        private void Build(int node, int start, int count)
        {
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            for (var i = start; i < start + count; i++) { min = Vector3.Min(min, Low(_boxes[_ids[i]])); max = Vector3.Max(max, High(_boxes[_ids[i]])); }
            _bounds[node] = (min, max);
            if (count == 1) return;
            var span = max - min; var axis = span.X >= span.Y && span.X >= span.Z ? 0 : span.Y >= span.Z ? 1 : 2;
            Array.Sort(_ids, start, count, Comparer<int>.Create((a, b) => { var c = Mid(_boxes[a])[axis].CompareTo(Mid(_boxes[b])[axis]); return c != 0 ? c : a.CompareTo(b); }));
            Build(node * 2, start, count / 2); Build(node * 2 + 1, start + count / 2, count - count / 2);
        }
        internal int Overlap(int id) => Find(1, 0, _ids.Length, id);
        private int Find(int node, int start, int count, int id)
        {
            var (min, max) = _bounds[node]; var a = Low(_boxes[id]); var b = High(_boxes[id]);
            if (a.X >= max.X || a.Y >= max.Y || a.Z >= max.Z || b.X <= min.X || b.Y <= min.Y || b.Z <= min.Z) return -1;
            if (count == 1) return _ids[start] == id ? -1 : _ids[start];
            var hit = Find(node * 2, start, count / 2, id);
            return hit >= 0 ? hit : Find(node * 2 + 1, start + count / 2, count - count / 2, id);
        }
    }
}
