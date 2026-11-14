using System.Security.Cryptography;
using Prospect.Game.World;

namespace Prospect.Game.Persistence;

/// <summary>
/// How much of a world one save holds. These bound the whole save and not one record of it: a capture buffer of
/// exactly this size is taken once, before the server starts, and never grows; a world that is saved refuses to
/// grow past them before it grows, so a save is never found too big when it is made; and a save that is read
/// names its counts first and is refused before any memory is taken for more. They are engineering defaults for
/// the first saved worlds, not tuning a player or a host sets.
/// </summary>
public sealed record SaveLimits
{
    /// <summary>The limits every saved world has unless a test builds a smaller one.</summary>
    public static SaveLimits Default { get; } = new();

    /// <summary>Durable owners of hulls. Independent of how many players a server lets in at once.</summary>
    public int MaxOwners { get; internal init; } = 64;

    /// <summary>Live hulls, every owner's together.</summary>
    public int MaxHulls { get; internal init; } = 4096;

    /// <summary>The blocks of every live hull's design added up, lost blocks included.</summary>
    public int MaxBlocks { get; internal init; } = 1 << 20;

    /// <summary>Asteroids that have been pushed from where the seed put them.</summary>
    public int MaxRocks { get; internal init; } = 16384;

    /// <summary>
    /// Sectors with a pushed asteroid. Bounded apart from the asteroids: a restored <see cref="RockField"/> keeps
    /// a table as long as a sector can have asteroids for each, about 80 KiB, whatever the count in it.
    /// </summary>
    public int MaxMovedSectors { get; internal init; } = 256;

    /// <summary>Designs in a catalogue: a design's number is one byte, and 255 names the manifest.</summary>
    public const int MaxDesigns = byte.MaxValue;

    /// <summary>The canonical bytes of every design in a catalogue together, as <see cref="BlockTransferRules"/> caps them.</summary>
    public const int MaxCatalogueBytes = 1 << 20;

    /// <summary>The bytes of a world's name in a save.</summary>
    public const int MaxNameBytes = 255;

    /// <summary>The most bytes an encoded save can be: every count at its limit, every hull with every dent.</summary>
    public long MaxCharts => (long)MaxOwners * ushort.MaxValue;

    public long MaxEncodedBytes =>
        WorldSnapshotCodec.HeaderBound
        + MaxCharts * WorldSnapshotCodec.ChartBytes
        + MaxCatalogueBytes
        + (long)MaxDesigns * (4 + SHA256.HashSizeInBytes)
        + (long)MaxOwners * WorldSnapshotCodec.OwnerBytes
        + (long)MaxHulls * (WorldSnapshotCodec.HullFixedBytes + VisualDamageState.Capacity * WorldSnapshotCodec.DentBytes)
        + (long)MaxBlocks * (sizeof(float) + sizeof(int))
        + (long)MaxRocks * WorldSnapshotCodec.RockBytes;

    /// <summary>
    /// The most memory a decoded save takes as a <see cref="WorldSnapshot"/>, in bytes, to within the size of the
    /// arrays' own headers. What restoring it into a running world takes on top is bounded by the same counts:
    /// the hull and block stores, and <see cref="MaxMovedSectors"/> tables of a sector's asteroid limit.
    /// </summary>
    public long MaxDecodedBytes =>
        MaxCharts * System.Runtime.CompilerServices.Unsafe.SizeOf<SectorChart>()
        + MaxCatalogueBytes
        + (long)MaxDesigns * (SHA256.HashSizeInBytes + 64)
        + (long)MaxOwners * System.Runtime.CompilerServices.Unsafe.SizeOf<SavedOwner>()
        + (long)MaxHulls * System.Runtime.CompilerServices.Unsafe.SizeOf<SavedHull>()
        + (long)MaxBlocks * (sizeof(float) + sizeof(int))
        + (long)MaxRocks * System.Runtime.CompilerServices.Unsafe.SizeOf<RockDelta>();
}
