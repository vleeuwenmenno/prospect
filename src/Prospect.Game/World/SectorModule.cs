using System.Text.Json;

namespace Prospect.Game.World;

/// <summary>The sector a module is asked about: where it is and how much matter the galaxy puts there.</summary>
public readonly struct SectorContext(Galaxy galaxy, SectorCoord coord, float density)
{
    public Galaxy Galaxy { get; } = galaxy;
    public SectorCoord Coord { get; } = coord;

    /// <summary>How much matter the galaxy's shape puts here, from 0 (void) to about 1 (core, arm).</summary>
    public float Density { get; } = density;

    /// <summary>The sector new players arrive in.</summary>
    public bool IsHome => Coord == default;

    /// <summary>Half the sector's edge, in metres: how far its contents may reach from its centre.</summary>
    public float HalfSize => (float)(Galaxy.SectorSize * 0.5);
}

/// <summary>
/// Makes one kind of thing in a sector: asteroid fields now, stations and gas fields later. The content lists
/// the modules in order under <c>sectorModules</c>, each with its own parameters.
///
/// A module has two steps. <see cref="Summarize"/> is cheap and writes the figures the map and the sector panel
/// show; <see cref="Generate"/> makes the things themselves. Both must be pure functions of the sector and the
/// seed, and both draw only from <see cref="Stream(in SectorContext, uint)"/>, which is keyed by the module's id:
/// adding, removing or retuning one module never changes what another draws.
///
/// Things live in world space, not in a sector: a field or a cloud is anchored in the sector its centre lies in
/// and may reach into the sectors around it. A module therefore asks the streams of the neighbouring sectors
/// for what they anchor, and never needs their generated contents. See <c>docs/world-generation.md</c>.
///
/// To add a module: derive from this, give <see cref="SectorSummary"/> and <see cref="SectorContent"/> the
/// fields it fills, and name its type in <see cref="Read"/>.
/// </summary>
public abstract class SectorModule
{
    private readonly ulong _key;

    protected SectorModule(string id)
    {
        Id = id;
        _key = SectorRandom.Key(id);
    }

    /// <summary>The module's name in the content. It keys the module's random streams, so renaming it reshuffles what it makes.</summary>
    public string Id { get; }

    /// <summary>
    /// One of the module's own random streams for a sector. Use a different <paramref name="purpose"/> for each
    /// independent decision (where, how big, what of), so that retuning one does not disturb the others.
    /// </summary>
    protected SectorRandom Stream(in SectorContext sector, uint purpose) => Stream(sector.Galaxy, sector.Coord, purpose);

    /// <summary>The same stream for any sector of a galaxy: what a neighbouring sector anchors is drawn from its own.</summary>
    protected SectorRandom Stream(Galaxy galaxy, SectorCoord coord, uint purpose) => new(galaxy.Seed, coord, _key + purpose * 0x9E3779B97F4A7C15);

    /// <summary>A key for the module's own use across the whole galaxy, such as a noise lattice.</summary>
    protected ulong GalaxyKey(Galaxy galaxy, uint purpose) => new SectorRandom(galaxy.Seed, default, _key + purpose * 0x9E3779B97F4A7C15).NextUInt64();

    /// <summary>Writes this module's headline figures for a sector without generating it.</summary>
    public abstract void Summarize(in SectorContext sector, ref SectorSummary summary);

    /// <summary>Generates this module's part of a sector. <see cref="SectorContent.Summary"/> is already filled in.</summary>
    public abstract void Generate(in SectorContext sector, SectorContent content);

    /// <summary>Builds the module a <c>sectorModules</c> entry describes.</summary>
    public static SectorModule Read(JsonElement element, string path)
    {
        var id = element.GetProperty("id"u8).GetString()!;
        var type = element.GetProperty("type"u8).GetString();
        return type switch
        {
            GasCloudModule.TypeName => GasCloudModule.Read(id, element, path),
            AsteroidFieldsModule.TypeName => AsteroidFieldsModule.Read(id, element, path),
            _ => throw new InvalidDataException($"{path}: unknown sector module type '{type}'"),
        };
    }
}
