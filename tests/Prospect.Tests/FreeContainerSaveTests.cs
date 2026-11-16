using System.Numerics;
using Prospect.Game.Persistence;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class FreeContainerSaveTests
{
    private static readonly GameContent Content = GameContent.Load();

    [Fact]
    public void ServerCaptureRestoresContainersAndTheirNextIdentity()
    {
        var folder = SaveRig.NewWorld(Content);
        FreeContainer expected;
        uint next;
        using (var rig = new SaveRig(folder, Content))
        {
            rig.Join();
            var store = rig.Get<FreeContainers>("_containers");
            Assert.True(store.Add(Content, new FreeContainer
            {
                Position = rig.Hulls.State(0).Position + new Vector3(0, 20, 0),
                Orientation = Quaternion.Identity,
                Velocity = new(1, 2, 3),
                AngularVelocity = new(0, .3f, 0),
                Resource = 0,
                Amount = 100,
                Hull = 30
            }, out _));
            expected = store.Items[0]; next = store.NextId;
            Assert.True(rig.Close().Clean);
        }
        using var loaded = new SaveRig(folder, Content);
        var restored = loaded.Get<FreeContainers>("_containers");
        Assert.Single(restored.Items.ToArray());
        Assert.Equal(expected, restored.Items[0]);
        Assert.Equal(next, restored.NextId);
    }

    [Fact]
    public void CodecBoundsAndValidatesLooseRecordsWithoutEncodingAllocations()
    {
        var world = WorldSnapshot.NewWorld(Content, Guid.NewGuid(), "Containers", DateTimeOffset.UnixEpoch,
            1, Content.SettingCatalog.Defaults, default);
        world.NextWorldTick = 20;
        world.FreeContainerCount = 1; world.NextContainerId = 2;
        world.FreeContainers[0] = new FreeContainer
        {
            Id = 1,
            Position = new(1e12, 2, 3),
            Orientation = Quaternion.Identity,
            Resource = -1,
            Hull = 50,
            Born = 3,
            Age = 4
        };
        var bytes = WorldSnapshotCodec.Encode(world);
        Assert.Equal(WorldSnapshotCodec.EncodedSize(world), bytes.Length);
        Assert.True(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default, out var copy, out _));
        Assert.Equal(world.FreeContainers[0], copy!.FreeContainers[0]);
        Assert.Equal(2u, copy.NextContainerId);
        var probe = new AllocationProbe();
        while (probe.Measuring()) WorldSnapshotCodec.Encode(world, bytes);
        Assert.Equal(0, probe.Bytes);
        for (var n = bytes.Length - WorldSnapshotCodec.FreeContainerBytes; n < bytes.Length; n++)
            Assert.False(WorldSnapshotCodec.TryDecode(bytes.AsSpan(0, n), SaveLimits.Default, out _, out _));
        world.FreeContainers[0].Age = 18;
        Assert.False(WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(world), SaveLimits.Default, out _, out _));
        world.FreeContainers[0].Age = 4;
        world.FreeContainers[0].Amount = double.NaN;
        Assert.False(WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(world), SaveLimits.Default, out _, out _));
        world.FreeContainers[0].Amount = 0;
        world.FreeContainerCount = 2; world.FreeContainers[1] = world.FreeContainers[0];
        Assert.False(WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(world), SaveLimits.Default, out _, out _));
    }

    [Fact]
    public void CleanupPausesNearPlayersAndZeroMeansNever()
    {
        var store = new FreeContainers();
        Assert.True(store.Add(Content, new FreeContainer { Orientation = Quaternion.Identity, Resource = -1, Hull = 50 }, out _));
        store.Cleanup(2, [Double3.Zero], 5000);
        Assert.Equal(0, store.Items[0].Age);
        store.Cleanup(0, [], 5000);
        store.Cleanup(0, [], 5000);
        Assert.Single(store.Items.ToArray());
        store.Cleanup(2, [], 5000);
        Assert.Empty(store.Items.ToArray());
    }
}
