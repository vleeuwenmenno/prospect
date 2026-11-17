using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Protocol 68: the five mining messages, one by one: what they carry, what they refuse and how big they get.</summary>
public sealed class MiningLaserWireTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Fact]
    public void TheProtocolIsAtVersion71AndTheNewMessagesHaveTheirOwnIds()
    {
        Assert.Equal(71u, ProtocolInfo.Version);
        Assert.Equal(44, (byte)MessageId.LaserAim);
        Assert.Equal(45, (byte)MessageId.SetActionGroup);
        Assert.Equal(46, (byte)MessageId.LaserBeams);
        Assert.Equal(47, (byte)MessageId.OreChunks);
        Assert.Equal(48, (byte)MessageId.ActionGroups);
        var ids = Enum.GetValues<MessageId>();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    // ------------------------------------------------------------------ LaserAimMessage

    private static byte[] Bytes(LaserAimMessage message)
    {
        var buffer = new byte[LaserAimMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        message.Write(ref writer);
        Assert.Equal(LaserAimMessage.MaxWireSize, writer.Length);
        Assert.Equal((byte)MessageId.LaserAim, buffer[0]);
        return buffer;
    }

    private static bool ReadAim(byte[] bytes, int length, out LaserAimMessage message)
    {
        var reader = new NetReader(bytes.AsSpan(1, length - 1));
        return LaserAimMessage.TryRead(ref reader, out message);
    }

    private static readonly LaserAimMessage Good = new(0x00010007u, 0b10_0000_0101, new Vector3(1, -2, 3), Vector3.Normalize(new Vector3(0.3f, -0.4f, 0.5f)));

    [Fact]
    public void AnAimMessageRoundTripsExactly()
    {
        var bytes = Bytes(Good);
        Assert.True(ReadAim(bytes, bytes.Length, out var read));
        Assert.Equal(Good, read);
        Assert.True(ReadAim(Bytes(Good with { Groups = 0 }), LaserAimMessage.MaxWireSize, out var off));
        Assert.Equal(0, off.Groups);
        Assert.True(ReadAim(Bytes(Good with { Groups = ActionGroups.AllMask }), LaserAimMessage.MaxWireSize, out var all));
        Assert.Equal(ActionGroups.AllMask, all.Groups);
    }

    [Fact]
    public void AnAimMessageWithAnUnknownGroupBitANonUnitDirectionOrNoNumbersIsRefused()
    {
        foreach (var groups in new ushort[] { 1 << 10, 1 << 15, 0xFFFF, 0x0400 | 1 })
            Assert.False(ReadAim(Bytes(Good with { Groups = groups }), LaserAimMessage.MaxWireSize, out _), $"groups {groups:X}");
        Assert.False(ReadAim(Bytes(Good with { Hull = 0 }), LaserAimMessage.MaxWireSize, out _));
        foreach (var direction in new[] { Vector3.Zero, new Vector3(2, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(0.9f, 0.9f, 0), new Vector3(float.NaN, 0, 1), new Vector3(float.PositiveInfinity, 0, 0), new Vector3(1.01f, 0, 0) })
            Assert.False(ReadAim(Bytes(Good with { Direction = direction }), LaserAimMessage.MaxWireSize, out _), $"direction {direction}");
        foreach (var offset in new[] { new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0), new Vector3(0, 0, float.NegativeInfinity), new Vector3(1e30f, 0, 0) })
            Assert.False(ReadAim(Bytes(Good with { Offset = offset }), LaserAimMessage.MaxWireSize, out _), $"offset {offset}");

        // A direction a hair off one is the rounding of a float, and is read.
        Assert.True(ReadAim(Bytes(Good with { Direction = new Vector3(1.0004f, 0, 0) }), LaserAimMessage.MaxWireSize, out _));
    }

    [Fact]
    public void AnAimMessageCutShortOrWithBytesLeftOverIsRefused()
    {
        var bytes = Bytes(Good);
        for (var length = 1; length < bytes.Length; length++)
            Assert.False(ReadAim(bytes, length, out _), $"cut at {length}");
        var extra = new byte[bytes.Length + 1];
        bytes.CopyTo(extra, 0);
        Assert.False(ReadAim(extra, extra.Length, out _));
    }

    // ------------------------------------------------------------------ SetActionGroupMessage

    private static byte[] Bytes(SetActionGroupMessage message)
    {
        var buffer = new byte[SetActionGroupMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        message.Write(ref writer);
        Assert.Equal(SetActionGroupMessage.MaxWireSize, writer.Length);
        Assert.Equal((byte)MessageId.SetActionGroup, buffer[0]);
        return buffer;
    }

    private static bool ReadSet(byte[] bytes, int length, out SetActionGroupMessage message)
    {
        var reader = new NetReader(bytes.AsSpan(1, length - 1));
        return SetActionGroupMessage.TryRead(ref reader, out message);
    }

    [Fact]
    public void ASetGroupMessageRoundTripsAndRefusesWhatIsNoGroupOrNoPart()
    {
        foreach (var group in new byte[] { 0, 1, 7, 10, ActionGroups.NotOverridden })
        {
            var message = new SetActionGroupMessage(0x00020009u, 63, group);
            Assert.True(ReadSet(Bytes(message), SetActionGroupMessage.MaxWireSize, out var read));
            Assert.Equal(message, read);
        }

        foreach (var group in new byte[] { 11, 12, 100, 254 })
            Assert.False(ReadSet(Bytes(new SetActionGroupMessage(5, 0, group)), SetActionGroupMessage.MaxWireSize, out _), $"group {group}");
        Assert.False(ReadSet(Bytes(new SetActionGroupMessage(5, ActionGroupsMessage.MaxParts, 1)), SetActionGroupMessage.MaxWireSize, out _));
        Assert.False(ReadSet(Bytes(new SetActionGroupMessage(5, 255, 1)), SetActionGroupMessage.MaxWireSize, out _));
        Assert.False(ReadSet(Bytes(new SetActionGroupMessage(0, 0, 1)), SetActionGroupMessage.MaxWireSize, out _));
    }

    [Fact]
    public void ASetGroupMessageCutShortOrWithBytesLeftOverIsRefused()
    {
        var bytes = Bytes(new SetActionGroupMessage(5, 1, 2));
        for (var length = 1; length < bytes.Length; length++)
            Assert.False(ReadSet(bytes, length, out _), $"cut at {length}");
        var extra = new byte[bytes.Length + 1];
        bytes.CopyTo(extra, 0);
        Assert.False(ReadSet(extra, extra.Length, out _));
    }

    // ------------------------------------------------------------------ ActionGroupsMessage

    private static byte[] Bytes(uint hull, ReadOnlySpan<byte> groups)
    {
        var buffer = new byte[ActionGroupsMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        ActionGroupsMessage.Write(ref writer, hull, groups);
        Assert.Equal((byte)MessageId.ActionGroups, buffer[0]);
        return buffer[..writer.Length];
    }

    private static bool ReadGroups(byte[] bytes, int length, Span<byte> into, out uint hull, out int count)
    {
        var reader = new NetReader(bytes.AsSpan(1, length - 1));
        return ActionGroupsMessage.TryRead(ref reader, into, out hull, out count);
    }

    [Fact]
    public void AnActionGroupsMessageRoundTripsAndTheFullOneIsExactlyItsBound()
    {
        Span<byte> into = stackalloc byte[ActionGroupsMessage.MaxParts];
        var groups = new byte[] { 1, 0, ActionGroups.NotOverridden, 10 };
        var bytes = Bytes(0x00030004u, groups);
        Assert.True(ReadGroups(bytes, bytes.Length, into, out var hull, out var count));
        Assert.Equal((0x00030004u, 4), (hull, count));
        Assert.Equal(groups, into[..4].ToArray());

        var full = new byte[ActionGroupsMessage.MaxParts];
        for (var i = 0; i < full.Length; i++) full[i] = (byte)(i % 11);
        var big = Bytes(7, full);
        Assert.Equal(ActionGroupsMessage.MaxWireSize, big.Length);
        Assert.True(ReadGroups(big, big.Length, into, out _, out count));
        Assert.Equal(full, into[..count].ToArray());

        // No parts at all is a message: the hull has no laser to change.
        var empty = Bytes(9, []);
        Assert.True(ReadGroups(empty, empty.Length, into, out _, out count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void AnActionGroupsMessageWithABadValueTooManyPartsOrTheWrongLengthIsRefused()
    {
        Span<byte> into = stackalloc byte[ActionGroupsMessage.MaxParts];
        var bytes = Bytes(7, [1, 2, 3]);
        for (var length = 1; length < bytes.Length; length++)
            Assert.False(ReadGroups(bytes, length, into, out _, out _), $"cut at {length}");
        var extra = new byte[bytes.Length + 1];
        bytes.CopyTo(extra, 0);
        Assert.False(ReadGroups(extra, extra.Length, into, out _, out _));

        foreach (var bad in new byte[] { 11, 50, 254 })
        {
            var changed = (byte[])bytes.Clone();
            changed[^1] = bad;
            Assert.False(ReadGroups(changed, changed.Length, into, out _, out _), $"value {bad}");
        }

        // A count of sixty-five, with all sixty-five bytes behind it.
        var many = new byte[1 + 4 + 1 + 65];
        many[0] = (byte)MessageId.ActionGroups;
        many[5] = 65;
        Assert.False(ReadGroups(many, many.Length, into, out _, out _));

        // A count that names more than the caller holds room for.
        Assert.False(ReadGroups(bytes, bytes.Length, stackalloc byte[2], out _, out _));
    }

    // ------------------------------------------------------------------ LaserBeamsMessage

    private static LaserBeamView Beam(int i) => new()
    {
        Hull = 0x00010000u + (uint)i + 1,
        Part = (byte)(i % ActionGroupsMessage.MaxParts),
        State = i % 2 == 0 ? LaserBeamState.Cutting : LaserBeamState.Firing,
        End = new Vector3(i, -i * 2, 60 - i),
    };

    private static byte[] Bytes(long tick, LaserBeamView[] beams)
    {
        var buffer = new byte[LaserBeamsMessage.MaxWireSize + 8];
        var writer = new NetWriter(buffer);
        LaserBeamsMessage.Write(ref writer, tick, beams);
        Assert.Equal((byte)MessageId.LaserBeams, buffer[0]);
        return buffer[..writer.Length];
    }

    private static bool ReadBeams(byte[] bytes, int length, LaserBeamView[] into, out long tick, out int count)
    {
        var reader = new NetReader(bytes.AsSpan(1, length - 1));
        return LaserBeamsMessage.TryRead(ref reader, into, out tick, out count);
    }

    [Fact]
    public void ABeamsMessageRoundTripsAndTheFullOneIsExactlyItsBound()
    {
        var into = new LaserBeamView[LaserBeamsMessage.MaxBeams];
        var some = Enumerable.Range(0, 3).Select(Beam).ToArray();
        var bytes = Bytes(1234, some);
        Assert.True(ReadBeams(bytes, bytes.Length, into, out var tick, out var count));
        Assert.Equal((1234L, 3), (tick, count));
        for (var i = 0; i < 3; i++) Assert.Equal((some[i].Hull, some[i].Part, some[i].State, some[i].End), (into[i].Hull, into[i].Part, into[i].State, into[i].End));

        var full = Enumerable.Range(0, LaserBeamsMessage.MaxBeams).Select(Beam).ToArray();
        var big = Bytes(long.MaxValue, full);
        Assert.Equal(LaserBeamsMessage.MaxWireSize, big.Length);
        Assert.True(ReadBeams(big, big.Length, into, out tick, out count));
        Assert.Equal((long.MaxValue, LaserBeamsMessage.MaxBeams), (tick, count));
        Assert.Equal(full[63].End, into[63].End);

        // An empty set ends the last beam.
        var none = Bytes(5, Array.Empty<LaserBeamView>());
        Assert.True(ReadBeams(none, none.Length, into, out _, out count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void ABeamsMessageWithAnUnknownStateNoHullNoNumbersOrTheWrongLengthIsRefused()
    {
        var into = new LaserBeamView[LaserBeamsMessage.MaxBeams];
        foreach (var state in new byte[] { 0, 3, 4, 255 })
        {
            var beam = Beam(0);
            beam.State = (LaserBeamState)state;
            var bytes = Bytes(1, [beam]);
            Assert.False(ReadBeams(bytes, bytes.Length, into, out _, out _), $"state {state}");
        }

        var noHull = Beam(0);
        noHull.Hull = 0;
        Assert.False(ReadBeams(Bytes(1, [noHull]), 1 + 8 + 1 + LaserBeamsMessage.RecordBytes, into, out _, out _));
        var badPart = Beam(0);
        badPart.Part = (byte)ActionGroupsMessage.MaxParts;
        Assert.False(ReadBeams(Bytes(1, [badPart]), 1 + 8 + 1 + LaserBeamsMessage.RecordBytes, into, out _, out _));
        foreach (var end in new[] { new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0), new Vector3(20_000, 0, 0) })
        {
            var beam = Beam(0);
            beam.End = end;
            Assert.False(ReadBeams(Bytes(1, [beam]), 1 + 8 + 1 + LaserBeamsMessage.RecordBytes, into, out _, out _), $"end {end}");
        }

        Assert.False(ReadBeams(Bytes(-1, [Beam(0)]), 1 + 8 + 1 + LaserBeamsMessage.RecordBytes, into, out _, out _));

        var good = Bytes(1, [Beam(0), Beam(1)]);
        for (var length = 1; length < good.Length; length++)
            Assert.False(ReadBeams(good, length, into, out _, out _), $"cut at {length}");
        var extra = new byte[good.Length + 1];
        good.CopyTo(extra, 0);
        Assert.False(ReadBeams(extra, extra.Length, into, out _, out _));

        // Sixty-five records, all there: more than a message holds.
        var many = new byte[1 + 8 + 1 + 65 * LaserBeamsMessage.RecordBytes];
        many[0] = (byte)MessageId.LaserBeams;
        many[9] = 65;
        Assert.False(ReadBeams(many, many.Length, new LaserBeamView[100], out _, out _));

        // More beams than the caller holds room for.
        Assert.False(ReadBeams(good, good.Length, new LaserBeamView[1], out _, out _));
    }

    // ------------------------------------------------------------------ OreChunkMessage

    private static OreChunk Chunk(int i) => new()
    {
        Id = (uint)i + 1,
        Position = new Double3(1000.5 + i, -20.25, 3e6),
        Velocity = new Vector3(0.5f, -i * .01f, 2),
        AngularVelocity = new Vector3(0.1f, 0.2f, 0.3f),
        Orientation = Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)),
        Resource = i % Content.Galaxy.Resources.Length,
        Grade = 0.05f + (i % 10) * 0.1f,
        Mass = 10f + i,
    };

    private static byte[] Bytes(long tick, OreChunk[] chunks)
    {
        var buffer = new byte[OreChunkMessage.MaxWireSize + 8];
        var writer = new NetWriter(buffer);
        OreChunkMessage.Write(ref writer, tick, chunks);
        Assert.Equal((byte)MessageId.OreChunks, buffer[0]);
        return buffer[..writer.Length];
    }

    private static bool ReadChunks(byte[] bytes, int length, OreChunk[] into, out long tick, out int count)
    {
        var reader = new NetReader(bytes.AsSpan(1, length - 1));
        return OreChunkMessage.TryRead(ref reader, Content, into, out tick, out count);
    }

    [Fact]
    public void AnOreChunkMessageRoundTripsAndTheFullOneIsExactlyItsBound()
    {
        var into = new OreChunk[OreChunks.Capacity];
        var some = Enumerable.Range(0, 5).Select(Chunk).ToArray();
        var bytes = Bytes(99, some);
        Assert.Equal(OreChunkMessage.HeaderBytes + 5 * OreChunkMessage.RecordBytes, bytes.Length);
        Assert.True(ReadChunks(bytes, bytes.Length, into, out var tick, out var count));
        Assert.Equal((99L, 5), (tick, count));
        for (var i = 0; i < 5; i++) Assert.Equal(some[i], into[i]);

        var full = Enumerable.Range(0, OreChunks.Capacity).Select(Chunk).ToArray();
        var big = Bytes(1, full);
        Assert.Equal(OreChunkMessage.MaxWireSize, big.Length);
        Assert.True(ReadChunks(big, big.Length, into, out _, out count));
        Assert.Equal(OreChunks.Capacity, count);
        Assert.Equal(full[255], into[255]);

        // An empty set withdraws every chunk.
        var none = Bytes(3, Array.Empty<OreChunk>());
        Assert.True(ReadChunks(none, none.Length, into, out _, out count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void AnOreChunkMessageWithARepeatedIdentityOrAnInvalidChunkIsRefused()
    {
        var into = new OreChunk[OreChunks.Capacity];
        var a = Chunk(0);
        var b = Chunk(1);
        b.Id = a.Id;
        Assert.False(ReadChunks(Bytes(1, [a, b]), OreChunkMessage.HeaderBytes + 2 * OreChunkMessage.RecordBytes, into, out _, out _));
        var zero = Chunk(0);
        zero.Id = 0;
        Assert.False(ReadChunks(Bytes(1, [zero]), OreChunkMessage.HeaderBytes + OreChunkMessage.RecordBytes, into, out _, out _));

        var cases = new (string Name, Func<OreChunk, OreChunk> Break)[]
        {
            ("no such resource", c => { c.Resource = Content.Galaxy.Resources.Length; return c; }),
            ("resource 255", c => { c.Resource = 255; return c; }),
            ("no grade", c => { c.Grade = 0; return c; }),
            ("grade above one", c => { c.Grade = 1.01f; return c; }),
            ("grade NaN", c => { c.Grade = float.NaN; return c; }),
            ("no mass", c => { c.Mass = 0; return c; }),
            ("negative mass", c => { c.Mass = -3; return c; }),
            ("huge mass", c => { c.Mass = 2e7f; return c; }),
            ("mass NaN", c => { c.Mass = float.NaN; return c; }),
            ("orientation not a unit", c => { c.Orientation = new Quaternion(1, 1, 1, 1); return c; }),
            ("orientation zero", c => { c.Orientation = default; return c; }),
            ("position NaN", c => { c.Position = new Double3(double.NaN, 0, 0); return c; }),
            ("position infinite", c => { c.Position = new Double3(0, double.PositiveInfinity, 0); return c; }),
            ("velocity infinite", c => { c.Velocity = new Vector3(float.PositiveInfinity, 0, 0); return c; }),
            ("spin NaN", c => { c.AngularVelocity = new Vector3(0, float.NaN, 0); return c; }),
        };
        foreach (var (name, change) in cases)
            Assert.False(ReadChunks(Bytes(1, [Chunk(0), change(Chunk(1))]), OreChunkMessage.HeaderBytes + 2 * OreChunkMessage.RecordBytes, into, out _, out _), name);
        Assert.False(ReadChunks(Bytes(-1, [Chunk(0)]), OreChunkMessage.HeaderBytes + OreChunkMessage.RecordBytes, into, out _, out _));
    }

    [Fact]
    public void AnOreChunkMessageCutShortWithBytesLeftOverOrTooLongIsRefused()
    {
        var into = new OreChunk[OreChunks.Capacity];
        var good = Bytes(1, [Chunk(0), Chunk(1)]);
        for (var length = 1; length < good.Length; length++)
            Assert.False(ReadChunks(good, length, into, out _, out _), $"cut at {length}");
        var extra = new byte[good.Length + 1];
        good.CopyTo(extra, 0);
        Assert.False(ReadChunks(extra, extra.Length, into, out _, out _));

        // A count of 257 with every record there, and a count the caller has no room for.
        var many = new byte[OreChunkMessage.HeaderBytes + 257 * OreChunkMessage.RecordBytes];
        many[0] = (byte)MessageId.OreChunks;
        many[9] = 1;
        many[10] = 1;
        Assert.False(ReadChunks(many, many.Length, new OreChunk[300], out _, out _));
        Assert.False(ReadChunks(good, good.Length, new OreChunk[1], out _, out _));
    }

    [Fact]
    public void TheSavedChunkRecordIsTheWireRecordWithItsTwoClocks()
    {
        Assert.Equal(OreChunkMessage.RecordBytes, 4 + 24 + 12 + 16 + 12 + 1 + 4 + 4 + 4 + 2 + 1 + 1);
        Assert.Equal(OreChunkMessage.RecordBytes + 16, Prospect.Game.Persistence.WorldSnapshotCodec.OreChunkBytes);
        var buffer = new byte[OreChunkMessage.RecordBytes];
        var writer = new NetWriter(buffer);
        OreChunkMessage.WriteRecord(ref writer, Chunk(7));
        Assert.Equal(OreChunkMessage.RecordBytes, writer.Length);
        var reader = new NetReader(buffer);
        Assert.Equal(Chunk(7), OreChunkMessage.ReadRecord(ref reader));
    }
}
