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

/// <summary>What the galaxy map shows for a sector. Cheap to compute, so the map can cover thousands of them.</summary>
public struct SectorSummary
{
    public SectorCoord Coord;
    public int StarClass;

    /// <summary>Unit vector from the sector toward its star, the direction light comes from.</summary>
    public Vector3 SunDirection;
    public int AsteroidCount;
}

public struct Asteroid
{
    /// <summary>Centre, relative to the sector's centre.</summary>
    public Vector3 LocalPosition;
    public float Radius;
    public Quaternion Rotation;

    /// <summary>Which of the shared rock shapes this one uses.</summary>
    public int Shape;
}

/// <summary>The generated contents of one sector.</summary>
public sealed class SectorContent
{
    public SectorSummary Summary;
    public Asteroid[] Asteroids = [];
    public int AsteroidCount;
}

/// <summary>
/// The galaxy as a function of its seed: every sector's name, star and rocks are computed from the seed and the
/// sector's coordinate, identically on the server and on every client, so none of it is ever stored or sent.
/// Generation is pure and thread-safe.
/// </summary>
public sealed class Galaxy
{
    public const int AsteroidShapes = 12;

    private readonly string[] _syllables;
    private readonly float _emptyChance;
    private readonly int _maxAsteroids;
    private readonly float _minRadius;
    private readonly float _maxRadius;
    private readonly float _totalWeight;

    private Galaxy(ulong seed, double sectorSize, StarClass[] starClasses, string[] syllables, float emptyChance, int maxAsteroids, float minRadius, float maxRadius)
    {
        Seed = seed;
        SectorSize = sectorSize;
        StarClasses = starClasses;
        _syllables = syllables;
        _emptyChance = emptyChance;
        _maxAsteroids = maxAsteroids;
        _minRadius = minRadius;
        _maxRadius = maxRadius;
        foreach (var star in starClasses)
            _totalWeight += star.Weight;
    }

    public ulong Seed { get; }

    /// <summary>The same galaxy rules under a different seed.</summary>
    public Galaxy WithSeed(ulong seed) =>
        new(seed, SectorSize, StarClasses, _syllables, _emptyChance, _maxAsteroids, _minRadius, _maxRadius);

    /// <summary>Edge length of a sector cell in metres.</summary>
    public double SectorSize { get; }
    public StarClass[] StarClasses { get; }
    public int MaxAsteroidsPerSector => _maxAsteroids;

    /// <summary>Where new players appear: the middle of the home sector.</summary>
    public static Double3 SpawnPoint => Double3.Zero;

    public static Galaxy Load(string path, ulong seed = 0)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
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

        var asteroids = root.GetProperty("asteroids"u8);
        if (stars.Count == 0 || syllables.Count == 0)
            throw new InvalidDataException($"{path}: needs at least one star class and one name syllable");

        return new Galaxy(
            seed,
            root.GetProperty("sectorSize"u8).GetDouble(),
            [.. stars],
            [.. syllables],
            asteroids.GetProperty("emptySectorChance"u8).GetSingle(),
            asteroids.GetProperty("maxPerSector"u8).GetInt32(),
            asteroids.GetProperty("minRadius"u8).GetSingle(),
            asteroids.GetProperty("maxRadius"u8).GetSingle()
        );
    }

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
        var random = new SectorRandom(Seed, coord, 3);
        var first = _syllables[random.NextInt(_syllables.Length)];
        var second = _syllables[random.NextInt(_syllables.Length)].ToLowerInvariant();
        return $"{first}{second} {random.NextInt(900) + 100}";
    }

    public SectorSummary Summarize(SectorCoord coord)
    {
        var random = new SectorRandom(Seed, coord, 1);
        var isHome = coord == default;

        var pick = random.NextSingle() * _totalWeight;
        var starClass = 0;
        for (; starClass < StarClasses.Length - 1; starClass++)
        {
            pick -= StarClasses[starClass].Weight;
            if (pick < 0)
                break;
        }

        var sun = Vector3.Normalize(new Vector3(random.NextSigned(), random.NextSigned() * 0.6f + 0.25f, random.NextSigned()) + new Vector3(0, 0, 0.001f));

        // The home sector always has a field to work; elsewhere some sectors are empty space.
        var count = isHome
            ? Math.Max(_maxAsteroids * 2 / 3, 1)
            : random.NextSingle() < _emptyChance
                ? 0
                : 1 + (int)(MathF.Pow(random.NextSingle(), 1.6f) * (_maxAsteroids - 1));

        return new SectorSummary
        {
            Coord = coord,
            StarClass = starClass,
            SunDirection = sun,
            AsteroidCount = count,
        };
    }

    /// <summary>Generates a sector's contents into <paramref name="content"/>, reusing its arrays.</summary>
    public void Generate(SectorCoord coord, SectorContent content)
    {
        content.Summary = Summarize(coord);
        var count = content.Summary.AsteroidCount;
        if (content.Asteroids.Length < _maxAsteroids)
            content.Asteroids = new Asteroid[_maxAsteroids];
        content.AsteroidCount = count;
        if (count == 0)
            return;

        var random = new SectorRandom(Seed, coord, 2);
        var half = (float)(SectorSize * 0.5);
        var isHome = coord == default;

        // Rocks gather in a few loose fields rather than filling the cell evenly.
        Span<Vector3> fields = stackalloc Vector3[1 + random.NextInt(3)];
        for (var i = 0; i < fields.Length; i++)
            fields[i] = new Vector3(random.NextSigned(), random.NextSigned() * 0.35f, random.NextSigned()) * half * 0.7f;
        if (isHome)
            fields[0] = new Vector3(0, -40, -1400);

        for (var i = 0; i < count; i++)
        {
            var field = fields[i % fields.Length];
            var radius = _minRadius + (_maxRadius - _minRadius) * MathF.Pow(random.NextSingle(), 3.2f);
            var spread = half * (0.06f + 0.16f * random.NextSingle());
            var offset = new Vector3(random.NextSigned(), random.NextSigned() * 0.45f, random.NextSigned()) * spread;
            var position = Vector3.Clamp(field + offset, new Vector3(-half + radius), new Vector3(half - radius));

            // Keep the arrival point of the home sector clear.
            if (isHome && position.Length() < radius + 150f)
                position += Vector3.Normalize(position + new Vector3(0, 0, -1)) * (radius + 150f);

            content.Asteroids[i] = new Asteroid
            {
                LocalPosition = position,
                Radius = radius,
                Rotation = Quaternion.Normalize(new Quaternion(random.NextSigned(), random.NextSigned(), random.NextSigned(), random.NextSigned() + 0.01f)),
                Shape = random.NextInt(AsteroidShapes),
            };
        }
    }
}

/// <summary>A small deterministic random stream keyed by galaxy seed, sector and purpose.</summary>
public struct SectorRandom
{
    private ulong _state;

    public SectorRandom(ulong seed, SectorCoord coord, uint stream)
    {
        _state = seed;
        _state = Mix(_state ^ (uint)coord.X);
        _state = Mix(_state ^ (uint)coord.Y);
        _state = Mix(_state ^ (uint)coord.Z);
        _state = Mix(_state ^ stream);
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
