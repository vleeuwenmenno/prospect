using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;

namespace Prospect.Game.Persistence;

/// <summary>
/// A <see cref="WorldSnapshot"/> as bytes: every field written by name in a fixed order, little-endian. It is
/// the save's own layout, covered by <see cref="Version"/>: no structure is copied out of memory and no network
/// message is reused, so neither can change a save by changing itself.
/// <para>
/// Reading trusts nothing. The counts come first and are held to the <see cref="SaveLimits"/> and to the bytes
/// that are actually there before any memory is taken for them, every record must fit what its count promised,
/// and nothing may be left over. What only the content can judge (whether a design decodes, whether a block's hit
/// points fit its design, whether an asteroid exists) is judged when a server restores the snapshot, not here.
/// </para>
/// <para>
/// The bytes carry no checksum: the store that keeps them answers for their integrity. Two snapshots of the same
/// world state encode to the same bytes but for <see cref="WorldSnapshot.CapturedUnixMs"/>.
/// </para>
/// </summary>
public static partial class WorldSnapshotCodec
{
    /// <summary>The layout's version. A save with any other is refused; there are no migrations yet.</summary>
    public const int Version = 4;
    internal const int ChartBytes = 4 + 12 + 8;

    /// <summary>"PWS1": tells a save from any other bytes before its version is believed.</summary>
    private const uint Magic = 0x31535750;

    internal const int OwnerBytes = 4 + 1 + 8 + 16 + 1 + SavedAccount.MaxAuthorityBytes;
    internal const int DentBytes = 4 + 1 + 1 + 2 + 2;
    internal const int RockBytes = 12 + 4 + 12 + 12 + 8;

    /// <summary>A hull's record without its dents, lost blocks and hit points.</summary>
    internal const int HullFixedBytes =
        8 + 4 + 1 + 1 + 8 // entity, owner, design, flags, damage tick
        + 24 + 12 + 16 + 12 + 4 + 1 + 4 + 4 + 4 + 4 + 6 + 1 + 1 + 4 + 4 + 4 + 8 // motion state
        + 1 + 24 // order
        + 4 + 1 + 4 + 8 + DentBytes // dent version and count, spark
        + 4 + 4 + 8; // blocks, losses, hit total

    /// <summary>Everything before the catalogue's designs, at its longest.</summary>
    internal const int HeaderBound =
        4 + 4 + 4 + 4 + ContentIdentity.WireSize
        + 16 + 1 + SaveLimits.MaxNameBytes + 8 + 8 + 12
        + 1 + WorldSettings.Capacity * (1 + WorldSettingCatalog.MaxIdLength + 4)
        + 8 + 8 + 4 + 8
        + 4 + byte.MaxValue
        + 7 * 4;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>The exact number of bytes <see cref="Encode(WorldSnapshot, Span{byte})"/> writes for a snapshot.</summary>
    public static long EncodedSize(WorldSnapshot snapshot)
    {
        long size = 4 + 4 + 4 + 4 + ContentIdentity.WireSize + 16 + 1 + Utf8.GetByteCount(snapshot.Name) + 8 + 8 + 12 + 1;
        for (var i = 0; i < snapshot.SettingCount; i++)
            size += 1 + snapshot.SettingIds[i].Length + 4;
        size += 8 + 8 + 4 + 8 + 4 + snapshot.StartingFleet.Length + 7 * 4;
        for (var i = 0; i < snapshot.DesignCount; i++)
            size += 4 + SHA256.HashSizeInBytes + snapshot.Designs[i].Length;
        size += (long)snapshot.OwnerCount * OwnerBytes;
        for (var i = 0; i < snapshot.HullCount; i++)
            size += HullFixedBytes + snapshot.Hulls[i].Visual.Count * DentBytes;
        size += (long)snapshot.LossCount * sizeof(int) + (long)snapshot.BlockCount * sizeof(float);
        return size + (long)snapshot.RockCount * RockBytes + (long)snapshot.ChartCount * ChartBytes + (long)snapshot.ModuleObservationCount * ModuleObservationBytes;
    }

    /// <summary>Encodes a snapshot into an array of exactly its size.</summary>
    public static byte[] Encode(WorldSnapshot snapshot)
    {
        var bytes = new byte[checked((int)EncodedSize(snapshot))];
        Encode(snapshot, bytes);
        return bytes;
    }

    /// <summary>
    /// Encodes a snapshot into <paramref name="destination"/>, which must hold <see cref="EncodedSize"/> bytes,
    /// and returns how many were written. Allocates nothing. Throws <see cref="InvalidDataException"/> for a
    /// snapshot that could not be read back: it would be a save nobody can load.
    /// </summary>
    public static int Encode(WorldSnapshot snapshot, Span<byte> destination)
    {
        var size = EncodedSize(snapshot);
        if (size > destination.Length)
            throw new ArgumentException("The destination is smaller than the encoded snapshot.", nameof(destination));
        if (snapshot.DesignCount is < 1 or > SaveLimits.MaxDesigns || snapshot.StartingFleet.Length > byte.MaxValue
            || snapshot.SettingCount > WorldSettings.Capacity || Utf8.GetByteCount(snapshot.Name) is < 1 or > SaveLimits.MaxNameBytes)
            throw new InvalidDataException("The snapshot does not fit the save layout.");

        var writer = new NetWriter(destination);
        writer.WriteUInt32(Magic);
        writer.WriteInt32(snapshot.Compatibility.SaveVersion);
        writer.WriteInt32(snapshot.Compatibility.GeneratorVersion);
        writer.WriteInt32(snapshot.Compatibility.DesignEncoding);
        snapshot.Compatibility.Content.Write(ref writer);

        Span<byte> scratch = stackalloc byte[SaveLimits.MaxNameBytes];
        snapshot.WorldId.TryWriteBytes(scratch);
        writer.WriteBytes(scratch[..16]);
        var nameBytes = Utf8.GetBytes(snapshot.Name, scratch);
        writer.WriteByte((byte)nameBytes);
        writer.WriteBytes(scratch[..nameBytes]);
        writer.WriteInt64(snapshot.CreatedUnixMs);
        writer.WriteUInt64(snapshot.Seed);
        Write(ref writer, snapshot.SpawnSector);

        writer.WriteByte((byte)snapshot.SettingCount);
        for (var i = 0; i < snapshot.SettingCount; i++)
        {
            var id = snapshot.SettingIds[i];
            if (id.Length is < 1 or > WorldSettingCatalog.MaxIdLength)
                throw new InvalidDataException("A setting's id does not fit the save layout.");
            writer.WriteByte((byte)id.Length);
            foreach (var c in id)
                writer.WriteByte(c is >= ' ' and <= '~' ? (byte)c : throw new InvalidDataException("A setting's id is not plain text."));
            writer.WriteSingle(snapshot.SettingValues[i]);
        }

        writer.WriteInt64(snapshot.NextWorldTick);
        writer.WriteInt64(snapshot.CapturedUnixMs);
        writer.WriteUInt32(snapshot.NextOwnerId);
        writer.WriteUInt64(snapshot.NextEntityId);

        writer.WriteByte((byte)snapshot.DesignCount);
        writer.WriteByte((byte)snapshot.PodDesign);
        writer.WriteByte((byte)snapshot.DroneDesign);
        writer.WriteByte((byte)snapshot.StartingFleet.Length);
        foreach (var design in snapshot.StartingFleet)
            writer.WriteByte((byte)design);

        writer.WriteInt32(snapshot.OwnerCount);
        writer.WriteInt32(snapshot.HullCount);
        writer.WriteInt32(snapshot.BlockCount);
        writer.WriteInt32(snapshot.LossCount);
        writer.WriteInt32(snapshot.RockCount);
        writer.WriteInt32(snapshot.ChartCount);
        writer.WriteInt32(snapshot.ModuleObservationCount);

        for (var i = 0; i < snapshot.DesignCount; i++)
        {
            writer.WriteInt32(snapshot.Designs[i].Length);
            writer.WriteBytes(snapshot.DesignHashes[i]);
            writer.WriteBytes(snapshot.Designs[i]);
        }

        if (!SavedOwner.Valid(snapshot.Owners.AsSpan(0, snapshot.OwnerCount), snapshot.NextOwnerId))
            throw new InvalidDataException("The saved owners do not hold together.");
        for (var i = 0; i < snapshot.OwnerCount; i++)
        {
            ref readonly var owner = ref snapshot.Owners[i];
            writer.WriteUInt32(owner.Id);
            writer.WriteByte((byte)owner.Kind);
            writer.WriteUInt64(owner.Controlled);
            owner.Account.Id.TryWriteBytes(scratch);
            writer.WriteBytes(scratch[..16]);
            var authority = owner.Account.Authority ?? "";
            writer.WriteByte((byte)authority.Length);
            for (var j = 0; j < SavedAccount.MaxAuthorityBytes; j++)
                writer.WriteByte(j < authority.Length ? (byte)authority[j] : (byte)0);
        }

        for (var i = 0; i < snapshot.HullCount; i++)
        {
            ref var hull = ref snapshot.Hulls[i];
            writer.WriteUInt64(hull.Entity);
            writer.WriteUInt32(hull.Owner);
            writer.WriteByte(hull.Design);
            writer.WriteByte(hull.EscapeDrone ? (byte)1 : (byte)0);
            writer.WriteInt64(hull.DamageTick);
            Write(ref writer, hull.State);
            writer.WriteByte((byte)hull.Order.Kind);
            Write(ref writer, hull.Order.Target);
            writer.WriteUInt32(hull.Visual.Version);
            writer.WriteByte(hull.Visual.Count);
            for (var d = 0; d < hull.Visual.Count; d++)
                Write(ref writer, hull.Visual.Dents[d]);
            writer.WriteUInt32(hull.Visual.Spark.Sequence);
            writer.WriteInt64(hull.Visual.Spark.Tick);
            Write(ref writer, hull.Visual.Spark.Contact);
            writer.WriteInt32(hull.Blocks);
            writer.WriteInt32(hull.LossCount);
            writer.WriteDouble(hull.HitTotal);
            foreach (var block in snapshot.Losses.AsSpan(hull.LossStart, hull.LossCount))
                writer.WriteInt32(block);
            foreach (var points in snapshot.HitPoints.AsSpan(hull.BlockStart, hull.Blocks))
                writer.WriteSingle(points);
        }

        for (var i = 0; i < snapshot.RockCount; i++)
        {
            ref readonly var rock = ref snapshot.Rocks[i];
            Write(ref writer, rock.Rock.Sector);
            writer.WriteInt32(rock.Rock.Index);
            Write(ref writer, rock.Offset);
            Write(ref writer, rock.Velocity);
            writer.WriteInt64(rock.StartTick);
        }

        foreach (var chart in snapshot.Charts.AsSpan(0, snapshot.ChartCount))
        {
            writer.WriteUInt32(chart.Owner);
            Write(ref writer, chart.Sector);
            writer.WriteInt64(chart.RecordedTick);
        }

        foreach (var record in snapshot.ModuleObservations.AsSpan(0, snapshot.ModuleObservationCount)) WriteModule(ref writer, record);

        if (writer.Length != size)
            throw new InvalidDataException("The snapshot's counts do not describe its records.");
        return writer.Length;
    }

    /// <summary>
    /// Reads only what a save says it was made with, from its first bytes, so a store can refuse a save of
    /// another build without decoding it. False when the bytes are not a save at all.
    /// </summary>
    public static bool TryReadCompatibility(ReadOnlySpan<byte> bytes, out SaveCompatibility compatibility)
    {
        var reader = new NetReader(bytes);
        var magic = reader.ReadUInt32();
        var version = reader.ReadInt32();
        var generator = reader.ReadInt32();
        var encoding = reader.ReadInt32();
        var read = ContentIdentity.TryRead(ref reader, out var content);
        compatibility = new SaveCompatibility(version, generator, encoding, content);
        return read && magic == Magic;
    }

    /// <summary>
    /// Decodes a save into a snapshot of exactly its counts. False, with the reason, for bytes that are not a
    /// save of this <see cref="Version"/>, that name more than <paramref name="limits"/> allow or more than they
    /// hold, or that are malformed in any other way. Never throws for what the bytes say, and takes no memory
    /// for a count before it has been checked.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, SaveLimits limits, [NotNullWhen(true)] out WorldSnapshot? snapshot, out string error)
    {
        snapshot = null;
        if (bytes.Length > limits.MaxEncodedBytes)
        {
            error = "the save is larger than any world this build keeps";
            return false;
        }

        if (!TryReadCompatibility(bytes, out var compatibility))
        {
            error = "not a saved world";
            return false;
        }

        if (compatibility.SaveVersion != Version)
        {
            error = $"save layout {compatibility.SaveVersion}, this build reads {Version}";
            return false;
        }

        var reader = new NetReader(bytes[(16 + ContentIdentity.WireSize)..]);
        var idBytes = reader.ReadBytes(16);
        var worldId = idBytes.Length == 16 ? new Guid(idBytes) : Guid.Empty;
        string name;
        try
        {
            name = Utf8.GetString(reader.ReadBytes(reader.ReadByte()));
        }
        catch (DecoderFallbackException)
        {
            error = "the world's name is not text";
            return false;
        }

        var created = reader.ReadInt64();
        var seed = reader.ReadUInt64();
        var spawn = ReadSector(ref reader);

        int settingCount = reader.ReadByte();
        if (settingCount > WorldSettings.Capacity)
        {
            error = "too many world settings";
            return false;
        }

        var settingIds = new string[settingCount];
        Span<float> settingValues = stackalloc float[WorldSettings.Capacity];
        Span<char> idChars = stackalloc char[WorldSettingCatalog.MaxIdLength];
        for (var i = 0; i < settingCount; i++)
        {
            int length = reader.ReadByte();
            var raw = reader.ReadBytes(length);
            if (length is < 1 or > WorldSettingCatalog.MaxIdLength || raw.Length != length)
            {
                error = "a world setting's id is malformed";
                return false;
            }

            for (var c = 0; c < length; c++)
            {
                if (raw[c] is < (byte)' ' or > (byte)'~')
                {
                    error = "a world setting's id is malformed";
                    return false;
                }

                idChars[c] = (char)raw[c];
            }

            settingIds[i] = new string(idChars[..length]);
            settingValues[i] = reader.ReadSingle();
        }

        var nextTick = reader.ReadInt64();
        var captured = reader.ReadInt64();
        var nextOwner = reader.ReadUInt32();
        var nextEntity = reader.ReadUInt64();

        int designCount = reader.ReadByte();
        int pod = reader.ReadByte();
        int drone = reader.ReadByte();
        var fleetBytes = reader.ReadBytes(reader.ReadByte());

        var owners = reader.ReadInt32();
        var hulls = reader.ReadInt32();
        var blocks = reader.ReadInt32();
        var losses = reader.ReadInt32();
        var rocks = reader.ReadInt32();
        var charts = reader.ReadInt32();
        var observations = reader.ReadInt32();
        if (!reader.IsValid)
        {
            error = "the save ends inside its header";
            return false;
        }

        if (!ServerNames.TryNormalize(name, out var clean) || clean != name || worldId == Guid.Empty)
        {
            error = "the world's name or identity is malformed";
            return false;
        }

        // Every count against its limit and against the bytes left, before a single array is taken for it.
        if (designCount < 1 || pod >= designCount || drone >= designCount
            || (uint)owners > (uint)limits.MaxOwners || (uint)hulls > (uint)limits.MaxHulls || (uint)blocks > (uint)limits.MaxBlocks
            || (uint)losses > (uint)blocks || (uint)rocks > (uint)limits.MaxRocks || charts < 0 || charts > limits.MaxCharts
            || observations < 0 || observations > limits.MaxModuleObservations)
        {
            error = "the save names more than a world may hold";
            return false;
        }

        var promised = (long)designCount * (4 + SHA256.HashSizeInBytes) + (long)owners * OwnerBytes + (long)hulls * HullFixedBytes
            + (long)losses * sizeof(int) + (long)blocks * sizeof(float) + (long)rocks * RockBytes + (long)charts * ChartBytes + (long)observations * ModuleObservationBytes;
        if (promised > reader.Remaining)
        {
            error = "the save names more than it holds";
            return false;
        }

        var fleet = new int[fleetBytes.Length];
        for (var i = 0; i < fleet.Length; i++)
        {
            fleet[i] = fleetBytes[i];
            if (fleet[i] >= designCount)
            {
                error = "the starting fleet names a design the catalogue does not have";
                return false;
            }
        }

        var designs = new byte[designCount][];
        var hashes = new byte[designCount][];
        long catalogueBytes = 0;
        for (var i = 0; i < designCount; i++)
        {
            var length = reader.ReadInt32();
            var hash = reader.ReadBytes(SHA256.HashSizeInBytes);
            catalogueBytes += length;
            if (length < 1 || catalogueBytes > SaveLimits.MaxCatalogueBytes || length > reader.Remaining || hash.Length != SHA256.HashSizeInBytes)
            {
                error = "the design catalogue is malformed or too large";
                return false;
            }

            hashes[i] = hash.ToArray();
            designs[i] = reader.ReadBytes(length).ToArray();
        }

        var result = new WorldSnapshot(owners, hulls, blocks, rocks, charts, observations)
        {
            Compatibility = compatibility,
            WorldId = worldId,
            Name = name,
            CreatedUnixMs = created,
            Seed = seed,
            SpawnSector = spawn,
            SettingIds = settingIds,
            SettingCount = settingCount,
            NextWorldTick = nextTick,
            CapturedUnixMs = captured,
            NextOwnerId = nextOwner,
            NextEntityId = nextEntity,
            Designs = designs,
            DesignHashes = hashes,
            DesignCount = designCount,
            PodDesign = pod,
            DroneDesign = drone,
            StartingFleet = fleet,
            OwnerCount = owners,
            HullCount = hulls,
            RockCount = rocks,
            ChartCount = charts,
            ModuleObservationCount = observations,
        };
        settingValues[..settingCount].CopyTo(result.SettingValues);

        for (var i = 0; i < owners; i++)
        {
            ref var owner = ref result.Owners[i];
            owner.Id = reader.ReadUInt32();
            owner.Kind = (OwnerKind)reader.ReadByte();
            owner.Controlled = reader.ReadUInt64();
            var accountBytes = reader.ReadBytes(16);
            var length = reader.ReadByte();
            var authority = reader.ReadBytes(SavedAccount.MaxAuthorityBytes);
            if (!reader.IsValid || authority[length..].IndexOfAnyExcept((byte)0) >= 0)
            {
                error = "an account binding is truncated or not canonical";
                return false;
            }
            for (var j = 0; j < length; j++)
                if (authority[j] is < 33 or > 126)
                {
                    error = "an account authority is not ASCII";
                    return false;
                }
            owner.Account = new SavedAccount(length == 0 ? null : Encoding.ASCII.GetString(authority[..length]), new Guid(accountBytes));
        }
        if (!SavedOwner.Valid(result.Owners.AsSpan(0, owners), nextOwner))
        {
            error = "the saved owners do not hold together";
            return false;
        }

        for (var i = 0; i < hulls; i++)
        {
            ref var hull = ref result.Hulls[i];
            hull.Entity = reader.ReadUInt64();
            hull.Owner = reader.ReadUInt32();
            hull.Design = reader.ReadByte();
            var flags = reader.ReadByte();
            hull.EscapeDrone = flags == 1;
            hull.DamageTick = reader.ReadInt64();
            var stateRead = TryReadState(ref reader, out hull.State);
            var order = reader.ReadByte();
            hull.Order = new HullOrder { Kind = (OrderKind)order, Target = ReadDouble3(ref reader) };
            hull.Visual.Version = reader.ReadUInt32();
            hull.Visual.Count = reader.ReadByte();
            if (flags > 1 || !stateRead || order > (byte)OrderKind.Jump || hull.Visual.Count > VisualDamageState.Capacity || hull.Design >= designCount)
            {
                error = "a hull's record is malformed";
                return false;
            }

            for (var d = 0; d < hull.Visual.Count; d++)
                hull.Visual.Dents[d] = ReadDent(ref reader);
            hull.Visual.Spark.Sequence = reader.ReadUInt32();
            hull.Visual.Spark.Tick = reader.ReadInt64();
            hull.Visual.Spark.Contact = ReadDent(ref reader);
            hull.Blocks = reader.ReadInt32();
            hull.LossCount = reader.ReadInt32();
            hull.HitTotal = reader.ReadDouble();
            hull.BlockStart = result.BlockCount;
            hull.LossStart = result.LossCount;

            // Against what the header promised, so no hull can claim another's blocks or more than were allowed.
            if (hull.Blocks < 1 || hull.Blocks > blocks - result.BlockCount || (uint)hull.LossCount > (uint)hull.Blocks || hull.LossCount > losses - result.LossCount)
            {
                error = "a hull's blocks do not fit what the save promised";
                return false;
            }

            for (var b = 0; b < hull.LossCount; b++)
                result.Losses[result.LossCount++] = reader.ReadInt32();
            for (var b = 0; b < hull.Blocks; b++)
                result.HitPoints[result.BlockCount++] = reader.ReadSingle();
            if (!reader.IsValid)
            {
                error = "the save ends inside a hull";
                return false;
            }
        }

        for (var i = 0; i < rocks; i++)
        {
            ref var rock = ref result.Rocks[i];
            rock.Rock = new RockRef(ReadSector(ref reader), reader.ReadInt32());
            rock.Offset = ReadVector3(ref reader);
            rock.Velocity = ReadVector3(ref reader);
            rock.StartTick = reader.ReadInt64();
        }

        var chartKeys = new HashSet<(uint, SectorCoord)>();
        var chartCounts = new Dictionary<uint, int>();
        for (var i = 0; i < charts; i++)
        {
            var chart = new SectorChart(reader.ReadUInt32(), ReadSector(ref reader), reader.ReadInt64());
            var known = false;
            foreach (var owner in result.Owners) known |= owner.Id == chart.Owner;
            chartCounts.TryGetValue(chart.Owner, out var ownerCharts);
            if (!known || chart.RecordedTick < 0 || chart.RecordedTick >= nextTick
                || !chartKeys.Add((chart.Owner, chart.Sector)) || ownerCharts >= ushort.MaxValue)
            {
                error = "the saved charts have invalid owners, sectors or survey times";
                return false;
            }
            chartCounts[chart.Owner] = ownerCharts + 1;
            result.Charts[i] = chart;
        }

        var observationKeys = new HashSet<(uint, ModuleFamily, SectorCoord, int)>();
        var observationCounts = new Dictionary<uint, int>();
        for (var i = 0; i < observations; i++)
        {
            if (!ReadModule(ref reader, out var record))
            { error = "invalid saved module observation"; return false; }
            var known = false;
            foreach (var owner in result.Owners) known |= owner.Id == record.Owner;
            observationCounts.TryGetValue(record.Owner, out var owned);
            if (!known || record.RecordedTick >= nextTick || owned >= ModuleScanRules.KnowledgePerOwner
                || !observationKeys.Add((record.Owner, record.Family, record.Sector, record.Index)))
            { error = "invalid module observation owner, key or time"; return false; }
            observationCounts[record.Owner] = owned + 1;
            result.ModuleObservations[i] = record;
        }

        if (!reader.IsValid || reader.Remaining != 0 || result.BlockCount != blocks || result.LossCount != losses)
        {
            error = "the save's records do not add up to what it promised";
            return false;
        }

        snapshot = result;
        error = "";
        return true;
    }

    private static void Write(ref NetWriter writer, in HullState state)
    {
        Write(ref writer, state.Position);
        Write(ref writer, state.Velocity);
        writer.WriteSingle(state.Orientation.X);
        writer.WriteSingle(state.Orientation.Y);
        writer.WriteSingle(state.Orientation.Z);
        writer.WriteSingle(state.Orientation.W);
        Write(ref writer, state.AngularVelocity);
        writer.WriteSingle(state.Heat);
        writer.WriteByte((byte)((state.BoostLocked ? 1 : 0) | (state.Boosting ? 2 : 0) | (state.ShieldOffline ? 4 : 0)));
        writer.WriteSingle(state.Energy);
        writer.WriteSingle(state.Shield);
        writer.WriteSingle(state.Hull);
        writer.WriteSingle(state.ShieldCooldown);
        writer.WriteByte(state.ShieldHit.Serial);
        writer.WriteByte(state.ShieldHit.Strength);
        writer.WriteByte((byte)state.ShieldHit.X);
        writer.WriteByte((byte)state.ShieldHit.Y);
        writer.WriteByte((byte)state.ShieldHit.Z);
        writer.WriteByte(state.ShieldHit.AgeTicks);
        writer.WriteByte(state.ShieldChangeTicks);
        writer.WriteByte((byte)state.Jump);
        writer.WriteSingle(state.JumpTimer);
        writer.WriteSingle(state.JumpCalculation);
        writer.WriteSingle(state.JumpTunnelSeconds);
        writer.WriteDouble(state.JumpTunnelElapsed);
    }

    /// <summary>Reads a motion state. False for flags or a jump stage that no state has; numbers are judged at restore.</summary>
    private static bool TryReadState(ref NetReader reader, out HullState state)
    {
        state = default;
        state.Position = ReadDouble3(ref reader);
        state.Velocity = ReadVector3(ref reader);
        state.Orientation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        state.AngularVelocity = ReadVector3(ref reader);
        state.Heat = reader.ReadSingle();
        var flags = reader.ReadByte();
        state.BoostLocked = (flags & 1) != 0;
        state.Boosting = (flags & 2) != 0;
        state.ShieldOffline = (flags & 4) != 0;
        state.Energy = reader.ReadSingle();
        state.Shield = reader.ReadSingle();
        state.Hull = reader.ReadSingle();
        state.ShieldCooldown = reader.ReadSingle();
        state.ShieldHit.Serial = reader.ReadByte();
        state.ShieldHit.Strength = reader.ReadByte();
        state.ShieldHit.X = (sbyte)reader.ReadByte();
        state.ShieldHit.Y = (sbyte)reader.ReadByte();
        state.ShieldHit.Z = (sbyte)reader.ReadByte();
        state.ShieldHit.AgeTicks = reader.ReadByte();
        state.ShieldChangeTicks = reader.ReadByte();
        var jump = reader.ReadByte();
        state.Jump = (JumpPhase)jump;
        state.JumpTimer = reader.ReadSingle();
        state.JumpCalculation = reader.ReadSingle();
        state.JumpTunnelSeconds = reader.ReadSingle();
        state.JumpTunnelElapsed = reader.ReadDouble();
        return flags <= 7 && jump <= (byte)JumpPhase.Arrive;
    }

    private static void Write(ref NetWriter writer, in DentProfile dent)
    {
        writer.WriteInt32(dent.Block);
        writer.WriteByte(dent.Face);
        writer.WriteByte(dent.Depth);
        writer.WriteUInt16(dent.U);
        writer.WriteUInt16(dent.V);
    }

    private static DentProfile ReadDent(ref NetReader reader) =>
        new()
        {
            Block = reader.ReadInt32(),
            Face = reader.ReadByte(),
            Depth = reader.ReadByte(),
            U = reader.ReadUInt16(),
            V = reader.ReadUInt16(),
        };

    private static void Write(ref NetWriter writer, SectorCoord sector)
    {
        writer.WriteInt32(sector.X);
        writer.WriteInt32(sector.Y);
        writer.WriteInt32(sector.Z);
    }

    private static SectorCoord ReadSector(ref NetReader reader) => new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

    private static void Write(ref NetWriter writer, Double3 value)
    {
        writer.WriteDouble(value.X);
        writer.WriteDouble(value.Y);
        writer.WriteDouble(value.Z);
    }

    private static Double3 ReadDouble3(ref NetReader reader) => new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

    private static void Write(ref NetWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
    }

    private static Vector3 ReadVector3(ref NetReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
