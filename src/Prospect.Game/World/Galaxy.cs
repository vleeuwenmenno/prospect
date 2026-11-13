using System.Numerics;
using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>Integer address of a sector cell. Sectors only index space; nothing stops at their edges.</summary>
public readonly record struct SectorCoord(int X, int Y, int Z)
{
    public SectorCoord Offset(int dx, int dy, int dz) => new(X + dx, Y + dy, Z + dz);
}

public sealed class StarClass
{
    public required string Name { get; init; }
    public required Vector3 Color { get; init; }
    public required float Weight { get; init; }
}

/// <summary>Something that can be dug out of an asteroid.</summary>
public sealed class Resource
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Vector3 Color { get; init; }

    /// <summary>How common the resource is, relative to the others.</summary>
    public required float Weight { get; init; }

    /// <summary>How much more common it becomes toward the galaxy's core: 0 is the same everywhere.</summary>
    public required float CoreBias { get; init; }

    /// <summary>The chance, from 0 to 1, that a sector far from the core has the resource at all.</summary>
    public required float Presence { get; init; }
}

/// <summary>A word for how much ore a sector carries, and the richness at which it starts to apply.</summary>
public sealed class RichnessLevel
{
    public required string Name { get; init; }
    public required float From { get; init; }
}

/// <summary>One share per resource, in the order of <see cref="Galaxy.Resources"/>.</summary>
[System.Runtime.CompilerServices.InlineArray(Galaxy.MaxResources)]
public struct ResourceShares
{
    private float _first;
}

/// <summary>
/// What kind of place a sector is, decided by the seed: how much ore its asteroids carry and which resources can
/// occur there at all. Cheap to compute, so the map can show it for any sector without generating the sector.
/// </summary>
public struct SectorCharacter
{
    /// <summary>How much ore the sector carries, from 0 (none) to 1. Neighbouring sectors have related values.</summary>
    public float Richness;

    /// <summary>Index into <see cref="Galaxy.RichnessLevels"/>. Level 0 is barren: no ore at all.</summary>
    public int Level;

    /// <summary>One bit per resource that can occur in the sector. A resource whose bit is clear is never found there.</summary>
    public uint Resources;

    /// <summary>Of the ore in the sector, the part each present resource makes up. They add up to one.</summary>
    public ResourceShares Shares;

    /// <summary>The resource with the largest share, or -1 if the sector has none.</summary>
    public int Main;

    public readonly bool Has(int resource) => (Resources & 1u << resource) != 0;

    public readonly int ResourceCount => System.Numerics.BitOperations.PopCount(Resources);

    /// <summary>Picks one of the sector's resources by its share, from a value in [0, 1). Returns -1 if it has none.</summary>
    public readonly int Pick(float roll)
    {
        var last = -1;
        for (var i = 0; i < Galaxy.MaxResources; i++)
        {
            if (!Has(i))
                continue;
            last = i;
            roll -= Shares[i];
            if (roll < 0)
                return i;
        }

        return last;
    }
}

/// <summary>What the galaxy map shows for a sector. Cheap to compute, so the map can cover thousands of them.</summary>
public struct SectorSummary
{
    public SectorCoord Coord;
    public int StarClass;

    /// <summary>Unit vector from the sector toward its star, the direction light comes from.</summary>
    public Vector3 SunDirection;

    /// <summary>How much matter the galaxy's shape puts here, from 0 (void) to about 1 (core, arm).</summary>
    public float Density;

    /// <summary>How rich the sector is and which resources occur in it.</summary>
    public SectorCharacter Character;

    /// <summary>
    /// Written by the asteroid field module: an estimate on the map, the actual counts after generation. A field
    /// counts for every sector it has rocks in, so neighbouring sectors can count the same field.
    /// </summary>
    public int AsteroidCount;
    public int FieldCount;
    /// <summary>Number of decorative gas volumes whose centre lies in the sector, zero or one.</summary>
    public int GasCloudCount;
}

public struct Asteroid
{
    /// <summary>Centre, relative to the sector's centre.</summary>
    public Vector3 LocalPosition;
    public float Radius;
    public Quaternion Rotation;

    /// <summary>Which of the shared rock shapes this one uses.</summary>
    public int Shape;

    /// <summary>Index of the resource the asteroid carries, or -1 for bare rock.</summary>
    public int Resource;

    /// <summary>How rich the asteroid is in its resource, from 0 to 1.</summary>
    public float Grade;
}

/// <summary>
/// The shape of the galaxy: a spiral disc. It is a pure function of sector coordinates, used both to decide how
/// much rock a sector holds and to paint the galaxy behind the map, so the two always agree.
/// </summary>
public sealed class GalaxyShape
{
    /// <summary>Radius of the disc in sectors.</summary>
    public required float Radius { get; init; }

    /// <summary>The sector at the galaxy's core. The home sector is out in the disc, not at the centre.</summary>
    public required Vector3 Center { get; init; }
    public required int Arms { get; init; }
    public required float Winding { get; init; }

    /// <summary>How many layers above and below the plane the disc reaches before it thins out.</summary>
    public required float Thickness { get; init; }

    public static GalaxyShape Read(System.Text.Json.JsonElement shape)
    {
        var center = shape.GetProperty("centerSector"u8);
        return new GalaxyShape
        {
            Radius = shape.GetProperty("radiusSectors"u8).GetSingle(),
            Center = new Vector3(center[0].GetSingle(), center[1].GetSingle(), center[2].GetSingle()),
            Arms = shape.GetProperty("arms"u8).GetInt32(),
            Winding = shape.GetProperty("winding"u8).GetSingle(),
            Thickness = shape.GetProperty("thicknessSectors"u8).GetSingle(),
        };
    }

    /// <summary>How much matter there is at a point of the galactic plane, from 0 (void) to about 1 (core, arm).</summary>
    public float Density(float sectorX, float sectorZ)
    {
        var dx = (sectorX - Center.X) / Radius;
        var dz = (sectorZ - Center.Z) / Radius;
        var r = MathF.Sqrt(dx * dx + dz * dz);
        if (r > 1.25f)
            return 0f;

        // DetMath, not MathF: whether a sector has rock at all hangs on this value, and it must come out the same
        // on every machine.
        var core = DetMath.Exp(-(r * r) / (0.11f * 0.11f));
        var disc = DetMath.Exp(-r * 3.0f);
        var phase = DetMath.Atan2(dz, dx) * Arms - DetMath.Log(r + 0.04f) * Winding;
        var arm = 0.5f + 0.5f * DetMath.Cos(phase);
        arm *= arm * arm;
        var edge = 1f - Math.Clamp((r - 0.85f) / 0.3f, 0f, 1f);
        return Math.Clamp((core * 1.2f + disc * (0.22f + 1.5f * arm)) * edge, 0f, 1f);
    }

    /// <summary>How the disc thins with distance from its plane, from 1 on the plane toward 0.</summary>
    public float LayerFalloff(int sectorY)
    {
        var d = (sectorY - Center.Y) / Thickness;
        return DetMath.Exp(-d * d);
    }

    /// <summary>How close a sector is to the core, from 1 at the core to 0 at the rim and beyond.</summary>
    public float CoreProximity(SectorCoord coord)
    {
        var dx = (coord.X - Center.X) / Radius;
        var dz = (coord.Z - Center.Z) / Radius;
        return Math.Clamp(1f - MathF.Sqrt(dx * dx + dz * dz), 0f, 1f);
    }
}

/// <summary>The generated contents of one sector. Reused from sector to sector; its buffers only ever grow.</summary>
public sealed class SectorContent
{
    public SectorSummary Summary;
    /// <summary>
    /// The gas cloud whose centre lies in the sector, if <see cref="SectorSummary.GasCloudCount"/> is one. It may
    /// reach into neighbouring sectors; <see cref="GasCloudModule.Intersecting(Galaxy, SectorCoord, Span{GasCloudPlacement})"/>
    /// lists every cloud that reaches a sector, also those centred elsewhere.
    /// </summary>
    public GasCloud GasCloud;

    /// <summary>
    /// The sector's asteroids, largest first; the first <see cref="AsteroidCount"/> are meaningful. An asteroid's
    /// place in this list is its name: see <see cref="RockRef"/>.
    /// </summary>
    public Asteroid[] Asteroids = [];
    public int AsteroidCount;

    /// <summary>Finds the asteroids near a point.</summary>
    public readonly RockIndex Rocks = new();

    /// <summary>A sphere around every asteroid of the sector, relative to the sector's centre.</summary>
    public Vector3 BoundsCenter;
    public float BoundsRadius;

    /// <summary>Working space for the asteroid field module: one key per candidate rock of the sector.</summary>
    internal ulong[] SortKeys = [];

    /// <summary>Working space for the asteroid field module: the fields that reach the sector, and their contours.</summary>
    internal AsteroidFieldsModule.Field[] Fields = [];
    internal AsteroidFieldsModule.RibbonKnot[] Knots = [];

    /// <summary>Working space for the asteroid field module: the share of candidates kept, on a lattice over the sector.</summary>
    internal float[] Thinning = [];

    /// <summary>When a <see cref="RockField"/> last handed this sector out, for choosing which to drop.</summary>
    internal long LastUsed;

    /// <summary>Makes room for a sector's asteroids and for the candidates they are chosen from.</summary>
    internal void Reserve(int asteroids, int candidates)
    {
        if (Asteroids.Length < asteroids)
            Asteroids = new Asteroid[Grown(asteroids)];
        if (SortKeys.Length < candidates)
            SortKeys = new ulong[Grown(candidates)];

        static int Grown(int count)
        {
            var size = 64;
            while (size < count)
                size *= 2;
            return size;
        }
    }
}

/// <summary>
/// The galaxy as a function of its seed: every sector's name, star, character and contents are computed from the
/// seed and the sector's coordinate, identically on the server and on every client, so none of it is ever stored
/// or sent. A sector is filled by a list of modules, each making one kind of thing from its own random stream.
/// Generation is pure and thread-safe.
/// </summary>
public sealed class Galaxy
{
    public const int AsteroidShapes = 12;

    /// <summary>
    /// The version of what a seed generates: which asteroids a sector has, in which order, and where. A saved
    /// world names a pushed asteroid by its index in its sector's generated list, so a save made under one
    /// version is refused under another, untouched: there is no migration. Bump it whenever the same seed,
    /// settings and content files would generate anything else: a change to a module's arithmetic, its order
    /// of draws or the order of the list. Retuning <c>galaxy.json</c> needs no bump: the file is part of the
    /// content identity, which a save is held to as well. It is not the network protocol's version, which
    /// changes for reasons a save does not care about.
    /// </summary>
    public const int GenerationVersion = 4;

    /// <summary>The most resources the content may define: a sector's resources travel as one bit each.</summary>
    public const int MaxResources = 16;

    private const uint StarStream = 1;
    private const uint NameStream = 3;
    private static readonly ulong CharacterStream = SectorRandom.Key("sector-character");
    private static readonly ulong RegionStream = SectorRandom.Key("richness-region");

    private string[] _syllables = [];
    private float _totalStarWeight;
    private SectorModule[] _modules = [];

    // How a sector's character is drawn. See content/galaxy/galaxy.json, "sectorCharacter".
    private float _regionSectors;
    private float _regionShare;
    private float _contrast;
    private float _shapeFloor;
    private float _shapeGain;
    private float _everyResourceChance;
    private float _shareEvenness;
    private float _homeMinRichness;
    private uint _homeResources;

    private Galaxy() { }

    public ulong Seed { get; private set; }

    /// <summary>Everything asteroids can carry, in the order their indices refer to.</summary>
    public Resource[] Resources { get; private set; } = [];

    /// <summary>The words for a sector's richness, poorest first. The first is barren: no ore at all.</summary>
    public RichnessLevel[] RichnessLevels { get; private set; } = [];

    /// <summary>The spiral the galaxy's matter follows: where sectors are rich and where they are empty.</summary>
    public GalaxyShape Shape { get; private set; } = null!;

    /// <summary>What fills a sector, in the order the content lists them.</summary>
    public ReadOnlySpan<SectorModule> Modules => _modules;

    /// <summary>Tonnes per cubic metre of rock.</summary>
    public float AsteroidDensity { get; private set; }

    /// <summary>
    /// How quickly a pushed asteroid loses its drift, as an exponential rate per second. Real rock would coast for
    /// ever; fields would slowly empty themselves, so a disturbed asteroid settles instead.
    /// </summary>
    public float DriftDecay { get; private set; }

    /// <summary>Edge length of a sector cell in metres.</summary>
    public double SectorSize { get; private set; }
    public StarClass[] StarClasses { get; private set; } = [];

    /// <summary>The most asteroids any sector can hold.</summary>
    public int MaxAsteroidsPerSector { get; private set; }

    /// <summary>The module that makes the asteroid fields, or null in a galaxy without one.</summary>
    public AsteroidFieldsModule? AsteroidFields { get; private set; }
    /// <summary>The optional decorative gas module.</summary>
    public GasCloudModule? GasClouds { get; private set; }

    /// <summary>The asteroid count at which a sector shows at full strength on the map.</summary>
    public float MapFullSignalAsteroids { get; private set; }

    /// <summary>The same galaxy rules under a different seed.</summary>
    public Galaxy WithSeed(ulong seed)
    {
        var copy = (Galaxy)MemberwiseClone();
        copy.Seed = seed;
        return copy;
    }

    /// <summary>The same galaxy filled by a different list of modules.</summary>
    public Galaxy WithModules(params SectorModule[] modules)
    {
        var copy = (Galaxy)MemberwiseClone();
        copy.SetModules(modules);
        return copy;
    }

    private float _richnessMin, _richnessMax = 1;
    private float[] _materialAbundance = [];

    /// <summary>Applies world generation settings without changing the seed or other modules.</summary>
    public Galaxy Under(WorldSettingCatalog catalog, in WorldSettings settings)
    {
        var clean = catalog.Sanitize(settings);
        var modules = new SectorModule[_modules.Length];
        for (var i = 0; i < modules.Length; i++)
            modules[i] = _modules[i] is AsteroidFieldsModule fields ? fields.Under(catalog, clean, Resources) : _modules[i];
        var copy = WithModules(modules);
        copy._richnessMin = catalog.Number(clean, "oreRichnessMin");
        copy._richnessMax = catalog.Number(clean, "oreRichnessMax");
        copy._materialAbundance = new float[Resources.Length];
        for (var i = 0; i < Resources.Length; i++)
            copy._materialAbundance[i] = catalog.Number(clean, Resources[i].Id + "Abundance");
        return copy;
    }

    private void SetModules(SectorModule[] modules)
    {
        for (var i = 0; i < modules.Length; i++)
            for (var j = 0; j < i; j++)
            {
                // The id keys a module's random stream; two modules sharing one would draw the same numbers.
                if (modules[i].Id == modules[j].Id)
                    throw new InvalidDataException($"Two sector modules share the id '{modules[i].Id}'");
            }

        _modules = modules;
        MaxAsteroidsPerSector = 0;
        AsteroidFields = null;
        GasClouds = null;
        foreach (var module in modules)
        {
            if (module is GasCloudModule gas)
            {
                if (GasClouds is not null) throw new InvalidDataException("A galaxy takes one gas module");
                GasClouds = gas;
            }
            if (module is not AsteroidFieldsModule fields)
                continue;

            // One list of asteroids per sector: a second field module would write over the first.
            if (AsteroidFields is not null)
                throw new InvalidDataException("A galaxy takes one asteroid field module");
            AsteroidFields = fields;
            MaxAsteroidsPerSector = fields.MaxPerSector;
        }
    }

    public static Galaxy Load(string path, ulong seed = 0) => Parse(File.ReadAllBytes(path), path, seed);

    /// <summary>Reads the galaxy's rules from a file's bytes. <paramref name="path"/> names where they came from in error messages.</summary>
    public static Galaxy Parse(ReadOnlyMemory<byte> json, string path, ulong seed = 0)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var stars = new List<StarClass>();
        foreach (var star in root.GetProperty("starClasses"u8).EnumerateArray())
        {
            var color = star.GetProperty("color"u8);
            stars.Add(
                new StarClass
                {
                    Name = star.GetProperty("name"u8).GetString()!,
                    Color = new Vector3(color[0].GetSingle(), color[1].GetSingle(), color[2].GetSingle()),
                    Weight = star.GetProperty("weight"u8).GetSingle(),
                }
            );
        }

        var syllables = new List<string>();
        foreach (var syllable in root.GetProperty("nameSyllables"u8).EnumerateArray())
            syllables.Add(syllable.GetString()!);

        var resources = new List<Resource>();
        foreach (var resource in root.GetProperty("resources"u8).EnumerateArray())
        {
            var tint = resource.GetProperty("color"u8);
            resources.Add(
                new Resource
                {
                    Id = resource.GetProperty("id"u8).GetString()!,
                    Name = resource.GetProperty("name"u8).GetString()!,
                    Color = new Vector3(tint[0].GetSingle(), tint[1].GetSingle(), tint[2].GetSingle()),
                    Weight = resource.GetProperty("weight"u8).GetSingle(),
                    CoreBias = resource.GetProperty("coreBias"u8).GetSingle(),
                    Presence = resource.GetProperty("presence"u8).GetSingle(),
                }
            );
        }

        if (stars.Count == 0 || syllables.Count == 0)
            throw new InvalidDataException($"{path}: needs at least one star class and one name syllable");
        if (resources.Count > MaxResources)
            throw new InvalidDataException($"{path}: at most {MaxResources} resources");

        var sectorSize = root.GetProperty("sectorSize"u8).GetDouble();
        if (sectorSize > RockIndex.MaxSectorSize)
            throw new InvalidDataException($"{path}: sectors can be at most {RockIndex.MaxSectorSize:0} m across");

        var character = root.GetProperty("sectorCharacter"u8);
        var levels = new List<RichnessLevel>();
        foreach (var level in character.GetProperty("levels"u8).EnumerateArray())
            levels.Add(new RichnessLevel { Name = level.GetProperty("name"u8).GetString()!, From = level.GetProperty("from"u8).GetSingle() });
        if (levels.Count < 2 || levels[0].From != 0f)
            throw new InvalidDataException($"{path}: richness levels need a first level from 0 and at least one above it");
        for (var i = 1; i < levels.Count; i++)
        {
            if (levels[i].From <= levels[i - 1].From)
                throw new InvalidDataException($"{path}: richness levels must rise");
        }

        var home = character.GetProperty("home"u8);
        var homeResources = 0u;
        foreach (var id in home.GetProperty("resources"u8).EnumerateArray())
        {
            var index = resources.FindIndex(r => r.Id == id.GetString());
            if (index < 0)
                throw new InvalidDataException($"{path}: the home sector names an unknown resource '{id.GetString()}'");
            homeResources |= 1u << index;
        }

        var asteroids = root.GetProperty("asteroids"u8);
        var galaxy = new Galaxy
        {
            Seed = seed,
            SectorSize = sectorSize,
            StarClasses = [.. stars],
            _syllables = [.. syllables],
            Resources = [.. resources],
            RichnessLevels = [.. levels],
            Shape = GalaxyShape.Read(root.GetProperty("shape"u8)),
            AsteroidDensity = asteroids.GetProperty("densityTonnesPerCubicMetre"u8).GetSingle(),
            DriftDecay = MathF.Log(2f) / asteroids.GetProperty("driftHalfLifeSeconds"u8).GetSingle(),
            MapFullSignalAsteroids = root.GetProperty("map"u8).GetProperty("fullSignalAsteroids"u8).GetSingle(),
            _regionSectors = character.GetProperty("regionSectors"u8).GetSingle(),
            _regionShare = character.GetProperty("regionShare"u8).GetSingle(),
            _contrast = character.GetProperty("contrast"u8).GetSingle(),
            _shapeFloor = character.GetProperty("shapeFloor"u8).GetSingle(),
            _shapeGain = character.GetProperty("shapeGain"u8).GetSingle(),
            _everyResourceChance = character.GetProperty("everyResourceChance"u8).GetSingle(),
            _shareEvenness = character.GetProperty("shareEvenness"u8).GetSingle(),
            _homeMinRichness = home.GetProperty("minRichness"u8).GetSingle(),
            _homeResources = homeResources,
        };
        foreach (var star in stars)
            galaxy._totalStarWeight += star.Weight;

        var modules = new List<SectorModule>();
        foreach (var module in root.GetProperty("sectorModules"u8).EnumerateArray())
            modules.Add(SectorModule.Read(module, path));
        galaxy.SetModules([.. modules]);
        return galaxy;
    }

    /// <summary>
    /// True when both positions lie in the same sector on every axis. This is the range of taking direct control of
    /// another hull: the server and the client ask it of the same positions. It is not a range for orders.
    /// </summary>
    public bool InSameSector(Double3 a, Double3 b) => SectorOf(a) == SectorOf(b);

    public SectorCoord SectorOf(Double3 position) =>
        new(
            (int)Math.Floor(position.X / SectorSize + 0.5),
            (int)Math.Floor(position.Y / SectorSize + 0.5),
            (int)Math.Floor(position.Z / SectorSize + 0.5)
        );

    public Double3 CenterOf(SectorCoord coord) => new(coord.X * SectorSize, coord.Y * SectorSize, coord.Z * SectorSize);

    /// <summary>The sector's name, for display. Allocates a string; ask only for sectors that are shown by name.</summary>
    public string NameOf(SectorCoord coord)
    {
        var random = new SectorRandom(Seed, coord, NameStream);
        var first = _syllables[random.NextInt(_syllables.Length)];
        var second = _syllables[random.NextInt(_syllables.Length)].ToLowerInvariant();
        return $"{first}{second} {random.NextInt(900) + 100}";
    }

    /// <summary>
    /// What the map and the sector panel need to know about a sector: its star, its character and each module's
    /// headline figures. Cheap: nothing in the sector is generated.
    /// </summary>
    public SectorSummary Summarize(SectorCoord coord)
    {
        var random = new SectorRandom(Seed, coord, StarStream);
        var pick = random.NextSingle() * _totalStarWeight;
        var starClass = 0;
        for (; starClass < StarClasses.Length - 1; starClass++)
        {
            pick -= StarClasses[starClass].Weight;
            if (pick < 0)
                break;
        }

        var sun = Vector3.Normalize(new Vector3(random.NextSigned(), random.NextSigned() * 0.6f + 0.25f, random.NextSigned()) + new Vector3(0, 0, 0.001f));

        var density = MatterAt(coord);
        var summary = new SectorSummary
        {
            Coord = coord,
            StarClass = starClass,
            SunDirection = sun,
            Density = density,
            Character = Characterize(coord, density),
        };

        var sector = new SectorContext(this, coord, density);
        foreach (var module in _modules)
            module.Summarize(sector, ref summary);
        return summary;
    }

    /// <summary>
    /// How much matter the galaxy's shape puts in a sector, from 0 (void) to about 1 (core, arm). Matter follows
    /// the galaxy's spiral: dense in the arms and the core, thin between them, almost none outside the disc.
    /// </summary>
    public float MatterAt(SectorCoord coord) => Shape.Density(coord.X, coord.Z) * Shape.LayerFalloff(coord.Y);

    /// <summary>Generates a sector's contents into <paramref name="content"/>, reusing its buffers.</summary>
    public void Generate(SectorCoord coord, SectorContent content)
    {
        content.Summary = Summarize(coord);
        content.AsteroidCount = 0;
        content.GasCloud = default;
        content.BoundsCenter = default;
        content.BoundsRadius = 0;
        content.Rocks.Reset(0, SectorSize);

        var sector = new SectorContext(this, coord, content.Summary.Density);
        foreach (var module in _modules)
            module.Generate(sector, content);
    }

    /// <summary>The word for a richness value.</summary>
    public int RichnessLevelOf(float richness)
    {
        var level = 0;
        while (level + 1 < RichnessLevels.Length && richness >= RichnessLevels[level + 1].From)
            level++;
        return level;
    }

    /// <summary>
    /// Draws a sector's character. Richness is part region, part the sector's own luck, scaled down where the
    /// galaxy is thin; each resource is then present or absent by its own chance, which grows toward the core for
    /// those with a core bias. A sector that comes out barren, or with no resource at all, carries no ore.
    /// </summary>
    private SectorCharacter Characterize(SectorCoord coord, float density)
    {
        var random = new SectorRandom(Seed, coord, CharacterStream);
        var isHome = coord == default;

        var raw = _regionShare * Region(coord) + (1f - _regionShare) * random.NextSingle();
        var richness = Math.Clamp((raw - 0.5f) * _contrast + 0.5f, 0f, 1f);
        richness *= _shapeFloor + (1f - _shapeFloor) * Math.Min(1f, density * _shapeGain);
        if (isHome)
            richness = Math.Max(richness, _homeMinRichness);

        richness = Math.Clamp(richness, _richnessMin, _richnessMax);
        var character = new SectorCharacter { Main = -1 };
        var nearCore = Shape.CoreProximity(coord);
        var every = random.NextSingle() < _everyResourceChance;
        var total = 0f;
        var largest = 0f;
        for (var i = 0; i < Resources.Length; i++)
        {
            // Both rolls are always drawn, so that one resource's chance never shifts the numbers of the next.
            var roll = random.NextSingle();
            var spread = random.NextSingle();
            var common = 1f + Resources[i].CoreBias * nearCore;
            var present = every || roll < Resources[i].Presence * common || (isHome && (_homeResources & 1u << i) != 0);
            if (!present)
                continue;

            var share = Resources[i].Weight * common * (_shareEvenness + (1f - _shareEvenness) * spread * spread);
            if (_materialAbundance.Length != 0) share *= _materialAbundance[i];
            if (share <= 0) continue;
            character.Resources |= 1u << i;
            character.Shares[i] = share;
            total += share;
            if (share > largest)
            {
                largest = share;
                character.Main = i;
            }
        }

        character.Level = RichnessLevelOf(richness);
        if (character.Level == 0 || character.Resources == 0 || total <= 0f)
            return new SectorCharacter { Main = -1 };

        for (var i = 0; i < Resources.Length; i++)
            character.Shares[i] /= total;
        character.Richness = richness;
        return character;
    }

    /// <summary>
    /// Smooth noise over sector coordinates, from 0 to 1: the regions of rich and poor space. Random values sit
    /// on a lattice <c>regionSectors</c> apart and are blended between, so neighbouring sectors get nearly the
    /// same value. Plain arithmetic only: it must come out the same on every machine.
    /// </summary>
    private float Region(SectorCoord coord)
    {
        var x = coord.X / _regionSectors + 0.37f;
        var y = coord.Y / _regionSectors + 0.11f;
        var z = coord.Z / _regionSectors + 0.71f;
        var cellX = MathF.Floor(x);
        var cellY = MathF.Floor(y);
        var cellZ = MathF.Floor(z);
        var fx = Smooth(x - cellX);
        var fy = Smooth(y - cellY);
        var fz = Smooth(z - cellZ);
        var (ix, iy, iz) = ((int)cellX, (int)cellY, (int)cellZ);

        var near = Blend(Blend(Lattice(ix, iy, iz), Lattice(ix + 1, iy, iz), fx), Blend(Lattice(ix, iy + 1, iz), Lattice(ix + 1, iy + 1, iz), fx), fy);
        var far = Blend(
            Blend(Lattice(ix, iy, iz + 1), Lattice(ix + 1, iy, iz + 1), fx),
            Blend(Lattice(ix, iy + 1, iz + 1), Lattice(ix + 1, iy + 1, iz + 1), fx),
            fy
        );
        return Blend(near, far, fz);

        static float Smooth(float t) => t * t * (3f - 2f * t);
        static float Blend(float a, float b, float t) => a + (b - a) * t;
    }

    private float Lattice(int x, int y, int z) => new SectorRandom(Seed, new SectorCoord(x, y, z), RegionStream).NextSingle();
}

/// <summary>A small deterministic random stream keyed by galaxy seed, sector and purpose.</summary>
public struct SectorRandom
{
    private ulong _state;

    public SectorRandom(ulong seed, SectorCoord coord, ulong stream)
    {
        _state = seed;
        _state = Mix(_state ^ (uint)coord.X);
        _state = Mix(_state ^ (uint)coord.Y);
        _state = Mix(_state ^ (uint)coord.Z);
        _state = Mix(_state ^ stream);
    }

    /// <summary>
    /// A stream of its own for one numbered thing under a key, such as one rock of a field. Whatever is drawn
    /// for one number never changes what another draws, so the things can be made in any order and by anyone.
    /// </summary>
    internal static SectorRandom Numbered(ulong key, uint number) => new() { _state = Mix(key + number * 0xD1342543DE82EF95) };

    /// <summary>A value in [0, 1) for a point of an integer lattice under a key.</summary>
    internal static float Lattice(ulong key, int x, int y, int z) =>
        (Mix(key + (ulong)x * 0xA0761D6478BD642F + (ulong)y * 0xE7037ED1A0B428DB + (ulong)z * 0x8EBC6AF09C88C6E3) >> 40) * (1f / (1 << 24));

    /// <summary>
    /// A stream number from a name, the same on every machine and in every build. Streams named differently are
    /// unrelated, so whatever draws from one never changes what another produces.
    /// </summary>
    public static ulong Key(string name)
    {
        var hash = 0xCBF29CE484222325;
        foreach (var c in name)
            hash = (hash ^ c) * 0x100000001B3;
        return Mix(hash);
    }

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15;
        return Mix(_state);
    }

    /// <summary>A value in [0, 1).</summary>
    public float NextSingle() => (NextUInt64() >> 40) * (1f / (1 << 24));

    /// <summary>A value in [-1, 1).</summary>
    public float NextSigned() => NextSingle() * 2f - 1f;

    /// <summary>An integer in [0, <paramref name="exclusiveMax"/>).</summary>
    public int NextInt(int exclusiveMax) => (int)((NextUInt64() >> 33) % (uint)exclusiveMax);

    private static ulong Mix(ulong z)
    {
        z = (z ^ z >> 30) * 0xBF58476D1CE4E5B9;
        z = (z ^ z >> 27) * 0x94D049BB133111EB;
        return z ^ z >> 31;
    }
}
