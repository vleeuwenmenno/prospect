using System.Buffers.Binary;
using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// A mining laser takes no managed memory where the rules forbid it: the tick that fires one, cuts a rock, describes its
/// interior and breaks a chunk off, and the client that is told of beams and chunks. Every measurement is through
/// <see cref="AllocationProbe"/>; none warms the code more than a real session would have by the time the first beam fires.
/// </summary>
public sealed class MiningLaserAllocationTests
{
    private static readonly Vector3 Up = Vector3.UnitY;
    private static readonly GameContent Content = MiningFixtures.Content;

    /// <summary>
    /// A whole second and a half of beams from two lasers, through a real server and two real clients: the aim message,
    /// the planning of the lasers, the power, the beams' rays, each scrape (the first of them describing the rock's
    /// interior and the first chunk breaking off), the chunks' contacts, and every message sent and read. Every tick of
    /// the server and of the clients is measured, the first included.
    /// </summary>
    [Fact]
    public void EveryTickOfAFiringLaserTakesNoMemoryFromTheFirstBeam()
    {
        var probe = new AllocationProbe();
        var first = "";
        var both = MiningFixtures.Mask(1, 2);
        var scrapes = 0;
        var chunks = 0u;
        var beams = 0;
        while (probe.Run())
        {
            first = "";
            using var rig = new MiningRig();
            var (rock, _) = MiningFixtures.FindRock(rig.Rocks);
            rig.Place(0, rock, Up, 25);
            rig.Step(30);

            for (var t = 0; t < 400; t++)
            {
                var direction = Vector3.Normalize(Up + Vector3.UnitZ * (t * 0.0012f));
                var before = probe.Bytes;
                probe.Start();
                rig.Aim(0, both, direction);
                rig.Step();
                probe.Stop();
                if (probe.Bytes > before && first.Length == 0) first = $"tick {t} of firing";
                if (t == 399) beams = rig.Snap().LaserBeamCount;
            }

            // Letting go, and the beams ending: also free.
            probe.Start();
            rig.Aim(0, 0, Up);
            rig.Step();
            probe.Stop();
            for (var t = 0; t < 30; t++)
            {
                var before = probe.Bytes;
                probe.Start();
                rig.Step();
                probe.Stop();
                if (probe.Bytes > before && first.Length == 0) first = $"tick {t} after letting go";
            }

            scrapes = rig.Server.RockEdits.Log.Count;
            chunks = rig.Server.OreChunks.NextId;
        }

        Assert.True(scrapes >= 10, $"only {scrapes} scrapes were made, so little was measured");
        Assert.True(chunks > 1, "no chunk broke off, so no spawn was measured");
        Assert.True(beams > 0, "no beam was up in the last firing tick");
        Assert.True(probe.Bytes == 0, $"the ticks took {probe.Bytes} bytes, first at {first}");
    }

    private static byte[] BeamsBytes()
    {
        var beams = Enumerable.Range(0, LaserBeamsMessage.MaxBeams).Select(i => new LaserBeamView
        {
            Hull = 0x00010000u + (uint)i + 1,
            Part = (byte)i,
            State = i % 2 == 0 ? LaserBeamState.Cutting : LaserBeamState.Firing,
            End = new Vector3(i, -i, 60),
        }).ToArray();
        var buffer = new byte[LaserBeamsMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        LaserBeamsMessage.Write(ref writer, 1, beams);
        return buffer[..writer.Length];
    }

    private static byte[] ChunkBytes()
    {
        var chunks = Enumerable.Range(0, OreChunks.Capacity).Select(i => new OreChunk
        {
            Id = (uint)i + 1,
            Position = new Double3(1e6 + i, 2, 3),
            Orientation = Quaternion.Identity,
            Resource = i % Content.Galaxy.Resources.Length,
            Grade = 0.5f,
            Mass = 100,
        }).ToArray();
        var buffer = new byte[OreChunkMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        OreChunkMessage.Write(ref writer, 1, chunks);
        return buffer[..writer.Length];
    }

    private static byte[] GroupBytes()
    {
        var groups = new byte[ActionGroupsMessage.MaxParts];
        Array.Fill(groups, (byte)3);
        var buffer = new byte[ActionGroupsMessage.MaxWireSize];
        var writer = new NetWriter(buffer);
        ActionGroupsMessage.Write(ref writer, 0x00010001u, groups);
        return buffer[..writer.Length];
    }

    [Fact]
    public void TheClientTakesNoMemoryReadingBeamsChunksAndGroupsOrPublishingThemAtTheirFullSize()
    {
        using var world = new ClientWorld(Content);
        var snapshot = new SessionSnapshot(Content);
        var beams = BeamsBytes();
        var chunks = ChunkBytes();
        var groups = GroupBytes();
        var tick = 10L;

        // The full sets are taken once before anything is measured, which is what a client's first message of a kind does
        // to its tables; the point here is the steady state of the thousandth message.
        var probe = new AllocationProbe();
        while (probe.Measuring())
        {
            tick++;
            BinaryPrimitives.WriteInt64LittleEndian(beams.AsSpan(1, 8), tick);
            var reader = new NetReader(beams.AsSpan(1));
            world.OnLaserBeams(ref reader, tick);
        }

        Assert.True(probe.Bytes == 0, $"reading a full set of beams took {probe.Bytes} bytes");

        while (probe.Measuring())
        {
            tick++;
            BinaryPrimitives.WriteInt64LittleEndian(chunks.AsSpan(1, 8), tick);
            var reader = new NetReader(chunks.AsSpan(1));
            world.OnOreChunks(ref reader);
        }

        Assert.True(probe.Bytes == 0, $"reading a full set of chunks took {probe.Bytes} bytes");

        while (probe.Measuring())
        {
            var reader = new NetReader(groups.AsSpan(1));
            world.OnActionGroups(ref reader);
        }

        Assert.True(probe.Bytes == 0, $"reading the groups took {probe.Bytes} bytes");

        while (probe.Measuring())
        {
            tick++;
            world.PublishLasers(snapshot, tick);
        }

        Assert.True(probe.Bytes == 0, $"publishing took {probe.Bytes} bytes");

        // What was read is what is published: the chunks, the groups, and beams only while they are fresh.
        world.PublishLasers(snapshot, tick);
        Assert.Equal(OreChunks.Capacity, snapshot.OreChunkCount);
        Assert.Equal(3, snapshot.ActionGroupOverrides[0]);
        Assert.Equal(ActionGroupsMessage.MaxParts, snapshot.ActionGroupCount);
        world.PublishLasers(snapshot, tick + 100);
        Assert.Equal(0, snapshot.LaserBeamCount);
    }

    [Fact]
    public void AClientDropsABeamNotRefreshedForAFewTicksAndIgnoresOldOrBadMessages()
    {
        using var world = new ClientWorld(Content);
        var snapshot = new SessionSnapshot(Content);
        var beams = BeamsBytes();
        BinaryPrimitives.WriteInt64LittleEndian(beams.AsSpan(1, 8), 50);
        var reader = new NetReader(beams.AsSpan(1));
        world.OnLaserBeams(ref reader, 100);
        world.PublishLasers(snapshot, 100);
        Assert.Equal(LaserBeamsMessage.MaxBeams, snapshot.LaserBeamCount);
        Assert.Equal(50, snapshot.LaserBeamTick);
        world.PublishLasers(snapshot, 106);
        Assert.Equal(LaserBeamsMessage.MaxBeams, snapshot.LaserBeamCount);
        world.PublishLasers(snapshot, 107);
        Assert.Equal(0, snapshot.LaserBeamCount);

        // An older set, or a malformed one, changes nothing.
        BinaryPrimitives.WriteInt64LittleEndian(beams.AsSpan(1, 8), 40);
        reader = new NetReader(beams.AsSpan(1));
        world.OnLaserBeams(ref reader, 200);
        world.PublishLasers(snapshot, 200);
        Assert.Equal(0, snapshot.LaserBeamCount);
        Assert.Equal(50, snapshot.LaserBeamTick);
        var bad = beams[..^3];
        BinaryPrimitives.WriteInt64LittleEndian(bad.AsSpan(1, 8), 60);
        reader = new NetReader(bad.AsSpan(1));
        world.OnLaserBeams(ref reader, 200);
        world.PublishLasers(snapshot, 200);
        Assert.Equal(0, snapshot.LaserBeamCount);
        Assert.Equal(50, snapshot.LaserBeamTick);

        // Chunks and groups the same: a set older than the held one, and one that is not valid, are ignored.
        var chunks = ChunkBytes();
        BinaryPrimitives.WriteInt64LittleEndian(chunks.AsSpan(1, 8), 20);
        reader = new NetReader(chunks.AsSpan(1));
        world.OnOreChunks(ref reader);
        BinaryPrimitives.WriteInt64LittleEndian(chunks.AsSpan(1, 8), 10);
        reader = new NetReader(chunks.AsSpan(1));
        world.OnOreChunks(ref reader);
        world.PublishLasers(snapshot, 300);
        Assert.Equal((OreChunks.Capacity, 20L), (snapshot.OreChunkCount, snapshot.OreChunkTick));
        BinaryPrimitives.WriteInt64LittleEndian(chunks.AsSpan(1, 8), 30);
        // A grade that is no number: grade and mass are followed by the eight bytes of the tractor's claim.
        BitConverter.GetBytes(float.NaN).CopyTo(chunks, OreChunkMessage.HeaderBytes + OreChunkMessage.RecordBytes - 16);
        reader = new NetReader(chunks.AsSpan(1));
        world.OnOreChunks(ref reader);
        world.PublishLasers(snapshot, 300);
        Assert.Equal(20L, snapshot.OreChunkTick);
        var revision = snapshot.ActionGroupRevision;
        var group = GroupBytes();
        group[^1] = 77;
        reader = new NetReader(group.AsSpan(1));
        world.OnActionGroups(ref reader);
        world.PublishLasers(snapshot, 300);
        Assert.Equal(revision, snapshot.ActionGroupRevision);
    }
}
