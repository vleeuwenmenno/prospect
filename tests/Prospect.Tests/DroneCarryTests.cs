using System.Numerics;
using System.Reflection;
using Prospect.Client.Render;
using Prospect.Client.World;
using Prospect.Engine.IO;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// Which drones carry what: the message that tells a client, what a carry looks like drawn as a beam from a drone's
/// belly array to the body it pulls, and that a real client sees the carriers of the body its drones are fetching.
/// </summary>
public sealed class DroneCarryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly GameContent Shipped = GameContent.Load();
    private static readonly WorkDroneLook Look = Assert.IsType<WorkDroneLook>(PropulsionLook.Load().WorkDrone);

    [Fact]
    public void TheMessageRoundTripsAndRefusesBadInput()
    {
        var carries = new DroneCarry[]
        {
            new() { Drone = 0x0003_0007, Body = 12, Kind = DroneCarryKind.Container },
            new() { Drone = 0x0001_0009, Body = 12, Kind = DroneCarryKind.Container },
            new() { Drone = 0x0002_0004, Body = 5, Kind = DroneCarryKind.Ore },
        };
        var bytes = new byte[DroneCarryMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        DroneCarryMessage.Write(ref writer, 41, carries);
        Assert.Equal(DroneCarryMessage.HeaderBytes + carries.Length * DroneCarryMessage.RecordBytes, writer.Written.Length);
        Assert.Equal((byte)MessageId.DroneCarries, writer.Written[0]);

        var reader = new NetReader(writer.Written[1..]);
        Span<DroneCarry> read = stackalloc DroneCarry[DroneCarryMessage.MaxCarries];
        Assert.True(DroneCarryMessage.TryRead(ref reader, read, out var tick, out var count));
        Assert.Equal(41, tick);
        Assert.Equal(carries.Length, count);
        Assert.Equal(carries, read[..count].ToArray());

        // Truncation, surplus bytes, a body or drone with no identity, a drone handle without its generation, a pool
        // that does not exist and a drone claimed twice are all refused.
        var good = writer.Written.ToArray();
        var first = DroneCarryMessage.HeaderBytes;
        const int Second = DroneCarryMessage.HeaderBytes + DroneCarryMessage.RecordBytes;
        foreach (var broken in new[]
        {
            good[..^1],
            [.. good, 0],
            Poke(good, first, 0),
            Drone(good, first + 5, 0),
            Drone(good, first + 5, 7),
            Poke(good, first + 4, 2),
            Drone(good, Second + 5, 0x0003_0007),
        })
        {
            reader = new NetReader(broken.AsSpan(1));
            Assert.False(DroneCarryMessage.TryRead(ref reader, read, out _, out _));
        }

        // An empty set withdraws every carry: a header and nothing else.
        writer = new NetWriter(bytes);
        DroneCarryMessage.Write(ref writer, 42, []);
        Assert.Equal(DroneCarryMessage.HeaderBytes, writer.Written.Length);
        reader = new NetReader(writer.Written[1..]);
        Assert.True(DroneCarryMessage.TryRead(ref reader, read, out tick, out count));
        Assert.Equal(42, tick);
        Assert.Equal(0, count);
        Assert.Equal(78u, ProtocolInfo.Version);
        Assert.Equal(52, (int)MessageId.DroneCarries);

        // One byte of a record, and one record's drone, changed by hand.
        static byte[] Poke(byte[] bytes, int at, int value)
        {
            var copy = bytes.ToArray();
            copy[at] = (byte)value;
            return copy;
        }
        static byte[] Drone(byte[] bytes, int at, uint drone)
        {
            var copy = bytes.ToArray();
            BitConverter.TryWriteBytes(copy.AsSpan(at), drone);
            return copy;
        }
    }

    [Fact]
    public void ARealClientSeesTheDronesCarryingTheBodyItFetches()
    {
        var content = DroneFetchTests.Content();
        using var link = new LoopbackLink();
        using var server = new ServerHost(new ServerConfig(), content, link.Server);
        var clock = new ManualTime();
        using var client = new ClientSession(link.Client, content, "Pilot", clock: clock);
        var tick = 0L;
        for (; tick < 4000 && client.AcquireSnapshot().HullCount == 0; tick++)
        {
            Step();
            Thread.Sleep(1);
        }
        var own = client.AcquireSnapshot().Hulls[client.AcquireSnapshot().OwnHull].Id;
        Assert.NotEqual(0u, own);
        Assert.Equal(0, client.AcquireSnapshot().DroneCarryCount);

        // A full M container well beyond the beam's reach: only the drones may take it, and none of them holds it yet.
        var containers = Field<FreeContainers>(server, "_containers");
        Assert.True(containers.Add(content, new FreeContainer
        {
            Position = Hulls(server).State((int)(own & 0xFFFF)).Position + new Vector3(-150, 20, 40),
            Velocity = Hulls(server).State((int)(own & 0xFFFF)).Velocity,
            Orientation = Quaternion.Identity,
            Size = 2,
            Resource = Iron(content),
            Amount = content.Cargo.Sizes[2].Units * Math.Pow(content.Cargo.UnitSize, 3) * content.Cargo.Density("iron"),
            Hull = content.Cargo.Sizes[2].Hull,
        }, out var container));
        Step();
        client.Input.FetchContainer(own, container);
        Assert.Equal(0, client.AcquireSnapshot().DroneCarryCount);
        for (var i = 0; i < 180 && client.AcquireSnapshot().Bays.Reply != DroneReply.Fetching; i++) Step();
        Assert.Equal(DroneReply.Fetching, client.AcquireSnapshot().Bays.Reply);
        Assert.Equal(1, client.AcquireSnapshot().Bays.ReplyCount);

        // On its way out to the container the drone pulls nothing, so nothing is told as carrying.
        var orders = Field<TaskOrder[]>(server, "_orders");
        for (var i = 0; i < 600 && !Attached(orders); i++) Step();
        var carriers = Carriers(orders);
        Assert.Single(carriers);

        // Held on, the drone is told as carrying that container and nothing else.
        for (var i = 0; i < 60 && client.AcquireSnapshot().DroneCarryCount != carriers.Count; i++) Step();
        var snapshot = client.AcquireSnapshot();
        Assert.Equal(carriers.Count, snapshot.DroneCarryCount);
        for (var i = 0; i < snapshot.DroneCarryCount; i++)
        {
            ref readonly var carry = ref snapshot.DroneCarries[i];
            Assert.Equal(container, carry.Body);
            Assert.Equal(DroneCarryKind.Container, carry.Kind);
            Assert.Contains(carry.Drone, carriers);
            Assert.True(snapshot.IsCarrying(carry.Drone));
            Assert.Contains(carry.Drone, WorkDrones(server));
        }
        Assert.False(snapshot.IsCarrying(0x0001_FFFF));

        // Once the beam has it, no drone carries it any more: the carries are withdrawn.
        for (var i = 0; i < 60 * 180 && containers.Find(container) >= 0; i++) Step();
        Assert.True(containers.Find(container) < 0);
        Assert.Empty(Carriers(orders));
        for (var i = 0; i < 30 && client.AcquireSnapshot().DroneCarryCount != 0; i++) Step();
        Assert.Equal(0, client.AcquireSnapshot().DroneCarryCount);

        void Step()
        {
            clock.Advance(ManualTime.Tick);
            ((ITickHandler)server).Tick(tick);
            ((ITickHandler)client).Tick(tick);
            tick++;
        }
    }

    [Fact]
    public void TheBellyBeamsAreDrawnFromTheArrayToTheBodyAndTakeNoMemory()
    {
        var content = Shipped;
        var snapshot = new SessionSnapshot(content)
        {
            ServerTickPrecise = 120.5,
            OwnHull = -1,
            HullCount = 2,
        };
        var words = BlockSet.WordCount(content.Designs[content.WorkDroneDesign].BlockCount);
        var blocks = new ulong[words];
        Array.Fill(blocks, ulong.MaxValue);
        var position = new Double3(1000, 500, -700);
        for (var i = 0; i < 2; i++)
            snapshot.Hulls[i] = new HullView
            {
                Id = (uint)(0x0003_0000 + i + 1),
                Owner = 7,
                Design = (byte)content.WorkDroneDesign,
                Blocks = blocks,
                Position = position + new Double3(0, 0, i * 6),
            };
        var drone = snapshot.Hulls[0].Id;
        var other = snapshot.Hulls[1].Id;

        var containers = new FreeContainerVisuals();
        var container = new FreeContainer { Id = 91, Position = position + new Vector3(3, -4, 0), Orientation = Quaternion.Identity, Size = 2, Resource = Iron(content) };
        containers.Update([container], 100);
        var chunks = new OreChunkVisuals();
        var chunk = new OreChunk { Id = 92, Position = position + new Vector3(-2, -5, 6), Orientation = Quaternion.Identity, Mass = 120 };
        chunks.Update([chunk], 100);

        snapshot.DroneCarries[0] = new DroneCarry { Drone = drone, Body = container.Id, Kind = DroneCarryKind.Container };
        snapshot.DroneCarries[1] = new DroneCarry { Drone = other, Body = chunk.Id, Kind = DroneCarryKind.Ore };
        snapshot.DroneCarryCount = 2;

        var camera = new Double3(990, 505, -690);
        var output = new TractorInstance[8];
        var designs = content.Designs;
        var drawn = new HullVisuals.HullVisual[2];
        drawn[0].Position = position;
        drawn[1].Position = position + new Double3(0, 0, 6);
        Assert.Equal(2, DroneCarryBeams.Fill(snapshot, drawn, designs, Look, containers, chunks, 0.5f, camera, output));

        // The container's beam leaves the first drone's array and ends on the container as it is drawn.
        var array = PropulsionModels.WorkDroneArrayPoint(designs[content.WorkDroneDesign], Look);
        var from = (position + array).RelativeTo(camera);
        var to = container.Position.RelativeTo(camera);
        Assert.Equal(from.X, output[0].FromSeed.X, 3);
        Assert.Equal(from.Y, output[0].FromSeed.Y, 3);
        Assert.Equal(from.Z, output[0].FromSeed.Z, 3);
        Assert.Equal(to.X, output[0].ToLength.X, 3);
        Assert.Equal(to.Z, output[0].ToLength.Z, 3);
        var half = FreeContainers.Half(content.Cargo, container.Size);
        Assert.Equal(half.Length(), output[0].ContainerHalf.W, 3);
        Assert.Equal((to - from).Length(), output[0].ToLength.W, 3);
        // The chunk's is the other drone's, ending on the lump where it is drawn.
        Assert.Equal(1f, output[0].ContainerQuat.W);
        var chunkTo = chunk.Position.RelativeTo(camera);
        Assert.Equal(chunkTo.Y, output[1].ToLength.Y, 3);
        Assert.Equal(OreChunks.Half(content.Mining, chunk.Mass) * 1.7320508f, output[1].ContainerHalf.W, 3);
        // Two different beams never share their light's seed.
        Assert.NotEqual(output[0].FromSeed.W, output[1].FromSeed.W);

        // A carry whose drone or body is not drawn makes no beam.
        snapshot.DroneCarries[0] = new DroneCarry { Drone = 0x0009_FFFF, Body = container.Id, Kind = DroneCarryKind.Container };
        snapshot.DroneCarries[1] = new DroneCarry { Drone = other, Body = 404, Kind = DroneCarryKind.Ore };
        Assert.Equal(0, DroneCarryBeams.Fill(snapshot, drawn, designs, Look, containers, chunks, 0.5f, camera, output));
        snapshot.DroneCarries[0] = new DroneCarry { Drone = drone, Body = container.Id, Kind = DroneCarryKind.Container };
        snapshot.DroneCarries[1] = new DroneCarry { Drone = other, Body = chunk.Id, Kind = DroneCarryKind.Ore };
        Assert.Equal(0, DroneCarryBeams.Fill(snapshot, [], designs, Look, containers, chunks, 0.5f, camera, output));

        var allocation = new AllocationProbe();
        while (allocation.Measuring())
            DroneCarryBeams.Fill(snapshot, drawn, designs, Look, containers, chunks, 0.5f, camera, output);
        Assert.Equal(0, allocation.Bytes);
    }

    private static int Iron(GameContent content)
    {
        for (var i = 0; i < content.Galaxy.Resources.Length; i++) if (content.Galaxy.Resources[i].Id == "iron") return i;
        throw new InvalidOperationException("iron");
    }

    private static T Field<T>(ServerHost server, string name) => (T)typeof(ServerHost).GetField(name, Private)!.GetValue(server)!;

    private static HullStore Hulls(ServerHost server) => Field<HullStore>(server, "_hulls");

    /// <summary>The drones holding on to something: the orders in their pull phase, with the hull flying each.</summary>
    private static List<uint> Carriers(TaskOrder[] orders)
    {
        var found = new List<uint>();
        foreach (var order in orders)
            if (order.Phase == 1 && order.Task is OrderTask.Fetch or OrderTask.FetchOre && order.Subject != 0)
                found.Add(order.Actor.Packed);
        return found;
    }

    private static bool Attached(TaskOrder[] orders)
    {
        foreach (var order in orders) if (order.Phase == 1) return true;
        return false;
    }

    private static List<uint> WorkDrones(ServerHost server)
    {
        var hulls = Hulls(server);
        var found = new List<uint>();
        for (var i = 0; i < hulls.Slots; i++)
            if (hulls.IsAliveAt(i) && hulls.Info(i).WorkDrone) found.Add(hulls.HandleAt(i).Packed);
        return found;
    }
}
