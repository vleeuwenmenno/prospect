using System.Numerics;

namespace Prospect.Game.World;

/// <summary>
/// Names one asteroid: its sector and its index in that sector's generated list. The list is a pure function of
/// the seed, so the name means the same asteroid to the server and to every client, and stays valid for good.
/// </summary>
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

/// <summary>The asteroids of one sector that have been pushed from where the seed put them.</summary>
public sealed class MovedRocks(int capacity)
{
    /// <summary>From asteroid index to its entry in <see cref="RockField.Deltas"/> plus one; zero if it never moved.</summary>
    public readonly int[] Entries = new int[capacity];

    /// <summary>The indices of the moved asteroids; the first <see cref="Count"/> are meaningful.</summary>
    public int[] Indices = new int[16];
    public int Count;
}

/// <summary>
/// The asteroids as the simulation sees them: generated from the seed, plus the deltas for those that have been
/// pushed. Persistence of the world is exactly this: the seed and the deltas. Used from one thread.
/// </summary>
public sealed class RockField(Galaxy galaxy)
{
    /// <summary>The share of an asteroid's nominal radius that is solid all the way round.</summary>
    public const float SolidFraction = 0.88f;

    /// <summary>
    /// How many generated sectors are kept. A hull touches up to eight at a corner; beyond this many the sector
    /// unused for longest is dropped and made again when next needed.
    /// </summary>
    private const int CacheSlots = 128;

    private readonly Dictionary<SectorCoord, SectorContent> _sectors = new(CacheSlots);
    private readonly Dictionary<SectorCoord, MovedRocks> _moved = [];
    private RockDelta[] _deltas = new RockDelta[64];
    private float[] _reach = new float[64];
    private int _deltaCount;
    private SectorContent? _last;
    private long _clock;

    public Galaxy Galaxy => galaxy;

    /// <summary>Every asteroid that has ever been pushed.</summary>
    public ReadOnlySpan<RockDelta> Deltas => _deltas.AsSpan(0, _deltaCount);

    /// <summary>The generated contents of a sector, made on first use.</summary>
    public SectorContent Sector(SectorCoord coord)
    {
        // Nearly every call asks for the sector the last one asked for.
        if (_last is { } last && last.Summary.Coord == coord)
        {
            last.LastUsed = ++_clock;
            return last;
        }

        if (!_sectors.TryGetValue(coord, out var content))
        {
            if (_sectors.Count < CacheSlots)
            {
                content = new SectorContent();
            }
            else
            {
                // Full: the sector that has gone unused for longest gives up its buffers.
                content = null;
                foreach (var cached in _sectors.Values)
                {
                    if (content is null || cached.LastUsed < content.LastUsed)
                        content = cached;
                }

                _sectors.Remove(content!.Summary.Coord);
            }

            galaxy.Generate(coord, content);
            _sectors.Add(coord, content);
        }

        content.LastUsed = ++_clock;
        _last = content;
        return content;
    }

    /// <summary>Mass of an asteroid of the given radius, in tonnes.</summary>
    public float Mass(float radius)
    {
        var solid = radius * SolidFraction;
        return galaxy.AsteroidDensity * 4.18879f * solid * solid * solid;
    }

    /// <summary>The asteroids of a sector that have moved, or null if none has.</summary>
    public MovedRocks? MovedIn(SectorCoord coord) => _moved.GetValueOrDefault(coord);

    public ref readonly RockDelta DeltaAt(int entry) => ref _deltas[entry];

    /// <summary>
    /// The furthest the asteroid of a delta can ever be from its generated position, in metres: where it was
    /// pushed from plus all the way its drift will carry it. Lets a search skip it without working out where it
    /// is right now.
    /// </summary>
    public float ReachAt(int entry) => _reach[entry];

    /// <summary>Where an asteroid is relative to its generated position at a tick, and how fast it is drifting.</summary>
    public void Displacement(RockRef rock, double tick, out Vector3 offset, out Vector3 velocity)
    {
        offset = default;
        velocity = default;
        var moved = MovedIn(rock.Sector);
        if (moved is not null && (uint)rock.Index < (uint)moved.Entries.Length && moved.Entries[rock.Index] > 0)
            _deltas[moved.Entries[rock.Index] - 1].Evaluate(tick, galaxy.DriftDecay, out offset, out velocity);
    }

    /// <summary>Records an asteroid's new motion, replacing any earlier record for it.</summary>
    public void Set(in RockDelta delta)
    {
        if ((uint)delta.Rock.Index >= (uint)galaxy.MaxAsteroidsPerSector)
            return;

        if (!_moved.TryGetValue(delta.Rock.Sector, out var moved))
            _moved[delta.Rock.Sector] = moved = new MovedRocks(galaxy.MaxAsteroidsPerSector);

        var entry = moved.Entries[delta.Rock.Index] - 1;
        if (entry < 0)
        {
            if (_deltaCount == _deltas.Length)
            {
                Array.Resize(ref _deltas, _deltas.Length * 2);
                Array.Resize(ref _reach, _reach.Length * 2);
            }

            if (moved.Count == moved.Indices.Length)
                Array.Resize(ref moved.Indices, moved.Indices.Length * 2);
            entry = _deltaCount++;
            moved.Entries[delta.Rock.Index] = entry + 1;
            moved.Indices[moved.Count++] = delta.Rock.Index;
        }

        _deltas[entry] = delta;
        _reach[entry] = delta.Offset.Length() + delta.Velocity.Length() / galaxy.DriftDecay;
    }
}

/// <summary>Asteroid motion changes produced by collisions during a tick, for the server to apply and announce.</summary>
public sealed class RockImpulses
{
    public readonly RockDelta[] Items = new RockDelta[64];
    public int Count;
}
