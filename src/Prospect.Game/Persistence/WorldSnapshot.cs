using System.Security.Cryptography;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Persistence;

/// <summary>
/// What a save must agree with the running game on before any of it is used. A save is refused, whole and
/// untouched, unless all four are the same: nothing here is migrated yet.
/// </summary>
/// <param name="SaveVersion">The layout of the save itself; see <see cref="WorldSnapshotCodec.Version"/>.</param>
/// <param name="GeneratorVersion">
/// What a seed generates; see <see cref="Galaxy.GenerationVersion"/>. A saved asteroid is named by its index in
/// its sector's generated list, which means nothing against another list.
/// </param>
/// <param name="DesignEncoding">The layout of a design's canonical bytes.</param>
/// <param name="Content">
/// The shared content, the evaluator's version included: designs are rebuilt from their bytes under the current
/// blocks and rules, so saved damage only fits hulls that evaluate as they did.
/// </param>
public readonly record struct SaveCompatibility(int SaveVersion, int GeneratorVersion, int DesignEncoding, ContentIdentity Content)
{
    /// <summary>What a save must say to be loaded by this build with this content.</summary>
    public static SaveCompatibility Of(GameContent content) =>
        new(WorldSnapshotCodec.Version, Galaxy.GenerationVersion, DesignBytes.Version, content.Identity);
}

/// <summary>What kind of proof binds a connection to an owner.</summary>
public enum OwnerKind : byte
{
    /// <summary>
    /// The player at the machine that runs the world, reached over the in-process link and nothing else. A world
    /// has at most one. No name, address or account a remote connection sends can stand in for it.
    /// </summary>
    Local = 1,

    /// <summary>An account-owned character; network admission is not implemented.</summary>
    Account = 2,
}

/// <summary>A durable owner of hulls: it outlives every connection and every run of the server.</summary>
public struct SavedOwner
{
    /// <summary>The owner's number, as hulls carry it. Never zero, never given out twice in a world.</summary>
    public uint Id;
    public OwnerKind Kind;

    /// <summary>Optional account binding, independent of the local designation. Never a credential.</summary>
    public SavedAccount Account;

    internal static bool Valid(ReadOnlySpan<SavedOwner> owners, uint nextId)
    {
        var locals = 0;
        for (var i = 0; i < owners.Length; i++)
        {
            var owner = owners[i];
            if (owner.Id == 0 || owner.Id >= nextId || !owner.Account.IsValid
                || owner.Kind is not (OwnerKind.Local or OwnerKind.Account)
                || (owner.Kind == OwnerKind.Local && ++locals > 1)
                || (owner.Kind == OwnerKind.Account && owner.Account == default))
                return false;
            for (var j = 0; j < i; j++)
                if (owners[j].Id == owner.Id || (owner.Account != default && owners[j].Account == owner.Account))
                    return false;
        }
        return true;
    }

    /// <summary>
    /// The <see cref="HullInfo.Entity"/> of the hull the owner flies, or zero when it is gone: the owner then
    /// gets a drone the next time they are let in.
    /// </summary>
    public ulong Controlled;
}

/// <summary>A canonical HTTPS service origin and its account UUID, without login secrets.</summary>
public readonly record struct SavedAccount(string? Authority, Guid Id)
{
    internal const int MaxAuthorityBytes = 255;

    internal bool IsValid
    {
        get
        {
            if (this == default)
                return true;
            if (Id == Guid.Empty || Authority is not { Length: > 8 and <= MaxAuthorityBytes }
                || !Authority.StartsWith("https://", StringComparison.Ordinal) || Authority[^1] != '/')
                return false;
            var host = Authority.AsSpan(8, Authority.Length - 9);
            var colon = host.IndexOf(':');
            if (colon >= 0)
            {
                var port = host[(colon + 1)..];
                if (port.IsEmpty || port[0] == '0')
                    return false;
                var number = 0;
                foreach (var c in port)
                {
                    if (c is < '0' or > '9' || number > 6553)
                        return false;
                    number = number * 10 + c - '0';
                }
                if (number is < 1 or > 65535 or 443)
                    return false;
                host = host[..colon];
            }
            if (host.IsEmpty)
                return false;
            var label = 0;
            var previous = '.';
            foreach (var c in host)
            {
                if (c == '.')
                {
                    if (label == 0 || previous == '-')
                        return false;
                    label = 0;
                }
                else
                {
                    if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-') || (label == 0 && c == '-') || ++label > 63)
                        return false;
                }
                previous = c;
            }
            return label > 0 && previous != '-';
        }
    }
}

/// <summary>One hull as a save holds it. What can be worked out again from these is not here.</summary>
public struct SavedHull
{
    /// <summary>The hull's durable name; see <see cref="HullInfo.Entity"/>.</summary>
    public ulong Entity;

    /// <summary>The <see cref="SavedOwner.Id"/> of its owner.</summary>
    public uint Owner;

    /// <summary>The hull's design, by its number in the saved catalogue.</summary>
    public byte Design;
    public bool EscapeDrone;

    /// <summary>The tick the hull last took damage in; <see cref="long.MinValue"/> if it never did.</summary>
    public long DamageTick;

    /// <summary>The whole motion state: place, speed, charge, shield marks and where it is in a jump.</summary>
    public HullState State;

    /// <summary>The standing order. It is kept through a load: a jump that was charging goes on charging.</summary>
    public HullOrder Order;

    /// <summary>The dents and the newest spark.</summary>
    public VisualDamageState Visual;

    /// <summary>The hit total exactly as it was kept, which a fresh sum of the hit points need not equal.</summary>
    public double HitTotal;

    /// <summary>Where this hull's hit points begin in <see cref="WorldSnapshot.HitPoints"/>, and how many blocks its design has.</summary>
    public int BlockStart;
    public int Blocks;

    /// <summary>Where this hull's lost blocks begin in <see cref="WorldSnapshot.Losses"/>, and how many it has lost, in order.</summary>
    public int LossStart;
    public int LossCount;
}

/// <summary>
/// One coherent state of a world: everything a server needs to go on exactly where another one stopped, taken
/// between two intervals of the simulation, and nothing that only meant something to that run (handles,
/// connections, input queues, edits on their way, per-round scratch).
/// <para>
/// It is a plain bounded buffer, not live simulation memory. Its arrays are taken once, by the counts it is
/// built for, and only the counted prefixes mean anything. One party owns it at a time: the tick thread while it
/// fills it, the save worker while it writes it, a store while it decodes one. The catalogue arrays are the one
/// exception: they are shared with the server's design catalogue, which replaces them and never writes to them,
/// so holding them is holding that moment's catalogue.
/// </para>
/// <para>
/// What a save does not hold and a load makes again: block sets, revisions, lost cores, collision counts and
/// sums (by replaying the losses), flight figures (read off the sums), which hull is piloted (from the owners),
/// contact ticks, order counters, control epochs and rounds (they start over).
/// </para>
/// </summary>
public sealed class WorldSnapshot
{
    /// <summary>A snapshot with room for exactly these counts.</summary>
    public WorldSnapshot(int owners, int hulls, int blocks, int rocks, int charts = 0)
    {
        Owners = new SavedOwner[owners];
        Hulls = new SavedHull[hulls];
        HitPoints = new float[blocks];
        Losses = new int[blocks];
        Rocks = new RockDelta[rocks];
        Charts = new SectorChart[charts];
    }

    /// <summary>A snapshot with room for the most a world under <paramref name="limits"/> can hold: a capture buffer.</summary>
    public WorldSnapshot(SaveLimits limits, int charts = 0)
        : this(limits.MaxOwners, limits.MaxHulls, limits.MaxBlocks, limits.MaxRocks, charts) { }

    /// <summary>What the save was made with; compared before anything else is believed.</summary>
    public SaveCompatibility Compatibility;

    /// <summary>The world's own identity, given when it is created and never changed: not its folder, not its name.</summary>
    public Guid WorldId;

    /// <summary>The world's name as its creator chose it; a name <see cref="ServerNames.TryNormalize"/> leaves as it is.</summary>
    public string Name = "";

    /// <summary>When the world was created, in milliseconds since the Unix epoch.</summary>
    public long CreatedUnixMs;
    public ulong Seed;

    /// <summary>
    /// The world's settings by id, each with its value: the first <see cref="SettingCount"/> of both arrays. They
    /// are the world's for good: nothing given at launch replaces them. The ids are shared with the content's
    /// setting catalogue and never written to; the values are this snapshot's own.
    /// </summary>
    public string[] SettingIds = [];
    public readonly float[] SettingValues = new float[WorldSettings.Capacity];
    public int SettingCount;

    /// <summary>The sector new owners arrive in.</summary>
    public SectorCoord SpawnSector;

    /// <summary>
    /// The next interval of the world to be simulated: every interval before it is in this state, none from it
    /// on. A server that loads the save begins there, whatever its own scheduler counts.
    /// </summary>
    public long NextWorldTick;

    /// <summary>When the state was captured, in milliseconds since the Unix epoch. Not part of the world: two captures of one state differ in it.</summary>
    public long CapturedUnixMs;

    /// <summary>The next owner number and the next entity number to give out. Neither is ever given out twice.</summary>
    public uint NextOwnerId;
    public ulong NextEntityId;

    /// <summary>
    /// The whole design catalogue in order: each design's canonical bytes and their SHA-256, by design number.
    /// The first <see cref="DesignCount"/> of each are meaningful. Never written to.
    /// </summary>
    public byte[][] Designs = [];
    public byte[][] DesignHashes = [];
    public int DesignCount;

    /// <summary>The catalogue's roles: the pod and the drone, and the hulls a new owner is given besides the pod. Never written to.</summary>
    public int PodDesign;
    public int DroneDesign;
    public int[] StartingFleet = [];

    /// <summary>The owners, in the order they were created. The first <see cref="OwnerCount"/> are meaningful.</summary>
    public readonly SavedOwner[] Owners;
    public int OwnerCount;

    /// <summary>
    /// The live hulls in the order the server steps them, which decides who strikes whom first. The first
    /// <see cref="HullCount"/> are meaningful. A load keeps this order; the gaps between them are not kept, so
    /// the slot a later hull is given may differ from the run that was saved.
    /// </summary>
    public readonly SavedHull[] Hulls;
    public int HullCount;

    /// <summary>Every hull's hit points, block by block, hull after hull. The first <see cref="BlockCount"/> are meaningful.</summary>
    public readonly float[] HitPoints;
    public int BlockCount;

    /// <summary>Every hull's lost blocks in the order they went, hull after hull. The first <see cref="LossCount"/> are meaningful.</summary>
    public readonly int[] Losses;
    public int LossCount;

    /// <summary>
    /// The asteroids that were pushed, in the order they first were, with the tick each motion starts at. The
    /// first <see cref="RockCount"/> are meaningful.
    /// </summary>
    public readonly RockDelta[] Rocks;
    public int RockCount;
    public readonly SectorChart[] Charts;
    public int ChartCount;

    /// <summary>
    /// The first state of a new world: the content's own catalogue and roles, no owner, no hull, no pushed
    /// asteroid, the first interval still to come. A store's <c>Create</c> takes it as the world's first save, and
    /// a server starts from it as from any other. Throws <see cref="InvalidDataException"/> for a name that is
    /// not clean, see <see cref="ServerNames.TryNormalize"/>. Run it off the frame loop: it encodes every design.
    /// </summary>
    public static WorldSnapshot NewWorld(GameContent content, Guid worldId, string name, DateTimeOffset created, ulong seed, in WorldSettings settings, SectorCoord spawnSector)
    {
        if (worldId == Guid.Empty)
            throw new InvalidDataException("A world needs an identity.");
        if (!ServerNames.TryNormalize(name, out var clean) || clean != name)
            throw new InvalidDataException($"A world's name must be 1 to {ServerNames.MaxLength} characters.");

        var designs = new byte[content.Designs.Length][];
        var hashes = new byte[designs.Length][];
        for (var i = 0; i < designs.Length; i++)
        {
            designs[i] = DesignBytes.Encode(content.Designs[i]);
            hashes[i] = SHA256.HashData(designs[i]);
        }

        var clamped = content.SettingCatalog.Sanitize(settings);
        var definitions = content.SettingCatalog.Definitions;
        var ids = new string[definitions.Length];
        var snapshot = new WorldSnapshot(0, 0, 0, 0)
        {
            Compatibility = SaveCompatibility.Of(content),
            WorldId = worldId,
            Name = name,
            CreatedUnixMs = created.ToUnixTimeMilliseconds(),
            Seed = seed,
            SettingIds = ids,
            SettingCount = ids.Length,
            SpawnSector = spawnSector,
            CapturedUnixMs = created.ToUnixTimeMilliseconds(),
            NextOwnerId = 1,
            NextEntityId = 1,
            Designs = designs,
            DesignHashes = hashes,
            DesignCount = designs.Length,
            PodDesign = content.PodDesign,
            DroneDesign = content.DroneDesign,
            StartingFleet = (int[])content.StartingFleet.Clone(),
        };
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = definitions[i].Id;
            snapshot.SettingValues[i] = clamped.Get(definitions[i]);
        }

        return snapshot;
    }
}
