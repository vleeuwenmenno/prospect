using System.Numerics;

namespace Prospect.Game.World;

/// <summary>Names one asteroid: its sector and its index in that sector's generated list.</summary>
public readonly record struct RockRef(SectorCoord Sector, int Index);

/// <summary>
/// How an asteroid has been knocked from where the galaxy's seed put it: its displacement and velocity at a given
/// tick. From these its position at any later tick follows exactly, so the server only has to tell clients when
/// an asteroid is hit, never to stream its motion.
/// </summary>
public struct RockDelta
{
    public RockRef Rock;
    public Vector3 Offset;
    public Vector3 Velocity;
    public long StartTick;

    /// <summary>The asteroid's displacement and velocity at a tick, with the drift decaying toward rest.</summary>
    public readonly void Evaluate(double tick, float decay, out Vector3 offset, out Vector3 velocity)
    {
        var seconds = (float)Math.Max(0.0, (tick - StartTick) * HullSim.Dt);
        var remaining = MathF.Exp(-decay * seconds);
        offset = Offset + Velocity * ((1f - remaining) / decay);
        velocity = Velocity * remaining;
    }
}

/// <summary>
/// The asteroids as the simulation sees them: generated from the seed, plus the deltas for those that have been
/// pushed. Persistence of the world is exactly this: the seed and the deltas. Used from one thread.
/// </summary>
public sealed class RockField(Galaxy galaxy)
{
    /// <summary>The share of an asteroid's nominal radius that is solid all the way round.</summary>
    public const float SolidFraction = 0.88f;
    private const int CacheSlots = 64;

    private readonly SectorContent?[] _cache = new SectorContent?[CacheSlots];
    private readonly bool[] _cacheValid = new bool[CacheSlots];
    private readonly Dictionary<SectorCoord, int[]> _deltaIndex = [];
    private RockDelta[] _deltas = new RockDelta[64];
    private int _deltaCount;

    public Galaxy Galaxy => galaxy;

    /// <summary>Every asteroid that has ever been pushed.</summary>
    public ReadOnlySpan<RockDelta> Deltas => _deltas.AsSpan(0, _deltaCount);

    /// <summary>The generated contents of a sector, made on first use.</summary>
    public SectorContent Sector(SectorCoord coord)
    {
        var index = (int)((uint)(coord.X * 73856093 ^ coord.Y * 19349663 ^ coord.Z * 83492791) % CacheSlots);
        var content = _cache[index] ??= new SectorContent();
        if (!_cacheValid[index] || content.Summary.Coord != coord)
        {
            galaxy.Generate(coord, content);
            _cacheValid[index] = true;
        }

        return content;
    }

    /// <summary>Mass of an asteroid of the given radius, in tonnes.</summary>
    public float Mass(float radius)
    {
        var solid = radius * SolidFraction;
        return galaxy.AsteroidDensity * 4.18879f * solid * solid * solid;
    }

    /// <summary>
    /// For a sector, a table from asteroid index to its entry in <see cref="Deltas"/> plus one, or null if no
    /// asteroid in the sector has moved.
    /// </summary>
    public int[]? MovedIn(SectorCoord coord) => _deltaIndex.GetValueOrDefault(coord);

    public ref readonly RockDelta DeltaAt(int entry) => ref _deltas[entry];

    /// <summary>Where an asteroid is relative to its generated position at a tick, and how fast it is drifting.</summary>
    public void Displacement(RockRef rock, double tick, out Vector3 offset, out Vector3 velocity)
    {
        offset = default;
        velocity = default;
        var moved = MovedIn(rock.Sector);
        if (moved is not null && (uint)rock.Index < (uint)moved.Length && moved[rock.Index] > 0)
            _deltas[moved[rock.Index] - 1].Evaluate(tick, galaxy.DriftDecay, out offset, out velocity);
    }

    /// <summary>Records an asteroid's new motion, replacing any earlier record for it.</summary>
    public void Set(in RockDelta delta)
    {
        if ((uint)delta.Rock.Index >= (uint)galaxy.MaxAsteroidsPerSector)
            return;

        if (!_deltaIndex.TryGetValue(delta.Rock.Sector, out var moved))
            _deltaIndex[delta.Rock.Sector] = moved = new int[galaxy.MaxAsteroidsPerSector];

        var entry = moved[delta.Rock.Index] - 1;
        if (entry < 0)
        {
            if (_deltaCount == _deltas.Length)
                Array.Resize(ref _deltas, _deltas.Length * 2);
            entry = _deltaCount++;
            moved[delta.Rock.Index] = entry + 1;
        }

        _deltas[entry] = delta;
    }
}

/// <summary>Asteroid motion changes produced by collisions during a tick, for the server to apply and announce.</summary>
public sealed class RockImpulses
{
    public readonly RockDelta[] Items = new RockDelta[64];
    public int Count;
}
