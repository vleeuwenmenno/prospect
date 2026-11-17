using System.Numerics;
using Prospect.Engine.Net;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Loose ore chunks and per-hull action group changes in a saved world: codec 20, the capture and the load rule.</summary>
public sealed class MiningSaveTests
{
    private static readonly GameContent Content = MiningFixtures.Content;

    private static HullHandle Pod(SaveRig rig) { var own = rig.Link.LastSnapshot.OwnHull; return new((int)(own & 0xFFFF), (int)(own >> 16)); }

    private static OreChunk Chunk(Double3 at, long born, int resource = 0, float grade = 0.4f, float mass = 250) => new()
    {
        Position = at,
        Velocity = new Vector3(0.5f, -1f, 2f),
        AngularVelocity = new Vector3(0, 0.3f, 0),
        Orientation = Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)),
        Resource = resource,
        Grade = grade,
        Mass = mass,
        Born = born,
    };

    private static void SetGroup(SaveRig rig, int part, byte group)
    {
        var bytes = new byte[SetActionGroupMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        new SetActionGroupMessage(rig.Link.LastSnapshot.OwnHull, (byte)part, group).Write(ref writer);
        rig.Link.Deliver(bytes[..writer.Length]);
        rig.Tick(3);
    }

    /// <summary>A world with a few chunks of its own (one aged) and two changed groups on the pod, saved and closed.</summary>
    private static (MemoryWorld Folder, WorldSnapshot Saved, uint Next, int PodIndex) Saved()
    {
        var folder = SaveRig.NewWorld(Content);
        using var rig = new SaveRig(folder, Content);
        rig.Join();
        var chunks = rig.Get<OreChunks>("_chunks");
        var hull = rig.Hulls.State(Pod(rig).Index).Position;
        Assert.True(chunks.Add(Content, Chunk(hull + new Vector3(0, 30, 0), rig.WorldTick, 0, 0.4f, 250), out var first));
        Assert.True(chunks.Add(Content, Chunk(hull + new Vector3(10, 30, 0), rig.WorldTick, 1, 0.9f, 80), out var second));
        Assert.True(chunks.Add(Content, Chunk(hull + new Double3(0, 0, 60_000), rig.WorldTick, 2, 1f, 5000), out var far));
        Assert.Equal(new[] { 1u, 2u, 3u }, new[] { first, second, far });
        chunks.Remove(chunks.Find(second));
        var design = Content.Pod;
        SetGroup(rig, MiningFixtures.PartA(design), 6);
        SetGroup(rig, MiningFixtures.PartB(design), ActionGroups.None);
        rig.Tick(250);
        Assert.True(rig.Close().Clean);
        var snapshot = folder.Decode();
        var index = Enumerable.Range(0, snapshot.HullCount).First(i => snapshot.Hulls[i].Design == Content.PodDesign);
        return (folder, snapshot, chunks.NextId, index);
    }

    [Fact]
    public void TheSaveLayoutIsVersion20()
    {
        Assert.Equal(20, WorldSnapshotCodec.Version);
        Assert.Equal(WorldSnapshotCodec.OreChunkBytes, OreChunkMessage.RecordBytes + 8 + 8);
        Assert.Equal(6, WorldSnapshotCodec.ActionGroupBytes);
    }

    [Fact]
    public void ACloseSavesEveryChunkAndEveryChangedGroupExactly()
    {
        var (_, saved, next, podIndex) = Saved();
        Assert.Equal(2, saved.OreChunkCount);
        Assert.Equal(4u, saved.NextOreChunkId);
        Assert.Equal(next, saved.NextOreChunkId);
        Assert.Equal(new[] { 1u, 3u }, saved.OreChunks.AsSpan(0, 2).ToArray().Select(c => c.Id).ToArray());
        var near = saved.OreChunks[0];
        var far = saved.OreChunks[1];
        Assert.Equal((0, 0.4f, 250f), (near.Resource, near.Grade, near.Mass));
        Assert.Equal((2, 1f, 5000f), (far.Resource, far.Grade, far.Mass));
        Assert.Equal(0, near.Age);
        Assert.True(far.Age is > 200 and <= 300, $"a chunk out of every sensor's reach had aged {far.Age} ticks");
        Assert.True(far.Born + far.Age <= saved.NextWorldTick);

        var design = Content.Pod;
        Assert.Equal(2, saved.ActionGroupCount);
        var groups = saved.ActionGroups.AsSpan(0, 2).ToArray().OrderBy(g => g.Part).ToArray();
        Assert.Equal(new[] { new SavedActionGroup(podIndex, (byte)MiningFixtures.PartA(design), 6), new SavedActionGroup(podIndex, (byte)MiningFixtures.PartB(design), 0) }.OrderBy(g => g.Part),
            groups);
    }

    [Fact]
    public void AReopenedWorldHoldsTheSameChunksIdentitiesCountersAndGroups()
    {
        var (folder, saved, _, _) = Saved();
        using var rig = new SaveRig(folder, Content);
        var chunks = rig.Get<OreChunks>("_chunks");
        Assert.Equal(saved.OreChunks.AsSpan(0, 2).ToArray(), chunks.Items.ToArray());
        Assert.Equal(saved.NextOreChunkId, chunks.NextId);
        Assert.Equal(-1, chunks.Find(2));

        // The hull of the saved pod has the saved changes, and nothing else changed.
        var design = Content.Pod;
        var slot = Enumerable.Range(0, rig.Hulls.Slots).First(i => rig.Hulls.IsAliveAt(i) && rig.Hulls.Info(i).Design == Content.PodDesign);
        Assert.Equal(6, rig.Server.GroupOf(slot, design, MiningFixtures.PartA(design)));
        Assert.Equal(0, rig.Server.GroupOf(slot, design, MiningFixtures.PartB(design)));
        Assert.Equal(0, rig.Server.GroupOf(slot, design, MiningFixtures.PartC(design)));
        Assert.Equal(6, rig.Get<byte[]>("_groupOverrides")[slot * ActionGroupsMessage.MaxParts + MiningFixtures.PartA(design)]);
        Assert.Equal(ActionGroups.NotOverridden, rig.Get<byte[]>("_groupOverrides")[slot * ActionGroupsMessage.MaxParts + MiningFixtures.PartC(design)]);

        // Identities carry on: the next chunk is the next number, not a reused one.
        Assert.True(chunks.Add(Content, Chunk(Content.Galaxy.CenterOf(default), 1), out var id));
        Assert.Equal(saved.NextOreChunkId, id);

        // A second close and reopen keeps them all.
        Assert.True(rig.Close().Clean);
        var again = folder.Decode();
        Assert.Equal(3, again.OreChunkCount);
        Assert.Equal(2, again.ActionGroupCount);
    }

    [Fact]
    public void TheCodecRoundTripsTheMiningSectionAndItsSizeIsExact()
    {
        var (_, saved, _, _) = Saved();
        var bytes = WorldSnapshotCodec.Encode(saved);
        Assert.Equal(WorldSnapshotCodec.EncodedSize(saved), bytes.Length);
        Assert.True(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default, out var copy, out var error), error);
        Assert.Equal(saved.OreChunks.AsSpan(0, 2).ToArray(), copy!.OreChunks.AsSpan(0, 2).ToArray());
        Assert.Equal(saved.ActionGroups.AsSpan(0, 2).ToArray(), copy.ActionGroups.AsSpan(0, 2).ToArray());
        Assert.Equal((saved.OreChunkCount, saved.NextOreChunkId, saved.ActionGroupCount), (copy.OreChunkCount, copy.NextOreChunkId, copy.ActionGroupCount));
        Assert.True(SaveLimits.Default.MaxEncodedBytes >= WorldSnapshotCodec.EncodedSize(saved));
        Assert.True(SaveLimits.Default.MaxEncodedBytes - WorldSnapshotCodec.MiningHeaderBytes >= OreChunks.Capacity * WorldSnapshotCodec.OreChunkBytes);

        // The mining section is the end of the save: cut anywhere inside it, or lengthened, and the save is damaged.
        var section = bytes.Length - WorldSnapshotCodec.MiningHeaderBytes - 2 * WorldSnapshotCodec.OreChunkBytes - 2 * WorldSnapshotCodec.ActionGroupBytes;
        for (var length = section; length < bytes.Length; length++)
            Assert.False(WorldSnapshotCodec.TryDecode(bytes.AsSpan(0, length), SaveLimits.Default, out _, out _), $"cut at {length - section} bytes into the mining section");
        Assert.False(WorldSnapshotCodec.TryDecode([.. bytes, 0], SaveLimits.Default, out _, out _));
    }

    [Fact]
    public void TheCodecRefusesWhatNoServerCouldHaveSaved()
    {
        var (_, saved, _, podIndex) = Saved();
        bool Decodes(Action<WorldSnapshot> change)
        {
            var copy = Copy(saved);
            change(copy);
            return WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(copy), SaveLimits.Default, out _, out _);
        }

        Assert.True(Decodes(_ => { }), "the unchanged world decodes");
        Assert.False(Decodes(w => w.OreChunks[1].Id = w.OreChunks[0].Id), "a chunk identity twice");
        Assert.False(Decodes(w => w.OreChunks[0].Id = 0), "identity zero");
        Assert.False(Decodes(w => w.OreChunks[0].Id = w.NextOreChunkId), "an identity the counter has not reached");
        Assert.False(Decodes(w => w.OreChunks[0].Born = w.NextWorldTick + 1), "born in the future");
        Assert.False(Decodes(w => w.OreChunks[0].Born = -1), "born before the world");
        Assert.False(Decodes(w => w.OreChunks[0].Age = w.NextWorldTick + 5), "older than the world");
        Assert.False(Decodes(w => w.OreChunks[0].Age = -1), "a negative age");
        Assert.False(Decodes(w => w.OreChunks[0].Position = new Double3(double.NaN, 0, 0)), "no position");
        Assert.False(Decodes(w => w.OreChunks[0].Velocity = new Vector3(float.PositiveInfinity, 0, 0)), "no velocity");
        Assert.False(Decodes(w => w.OreChunks[0].Orientation = new Quaternion(1, 1, 1, 1)), "no orientation");
        Assert.False(Decodes(w => w.OreChunks[0].Grade = 0), "no grade");
        Assert.False(Decodes(w => w.OreChunks[0].Grade = 1.5f), "more than all of it");
        Assert.False(Decodes(w => w.OreChunks[0].Mass = 0), "no mass");
        Assert.False(Decodes(w => w.OreChunks[0].Mass = 2e7f), "too much mass");
        Assert.False(Decodes(w => w.OreChunks[0].Resource = Galaxy.MaxResources), "a resource no galaxy has");
        Assert.False(Decodes(w => w.ActionGroups[0] = new SavedActionGroup(w.HullCount, 0, 3)), "a hull that is not saved");
        Assert.False(Decodes(w => w.ActionGroups[0] = new SavedActionGroup(-1, 0, 3)), "a negative hull");
        Assert.False(Decodes(w => w.ActionGroups[0] = new SavedActionGroup(podIndex, ActionGroupsMessage.MaxParts, 3)), "a part past the most");
        Assert.False(Decodes(w => w.ActionGroups[0] = new SavedActionGroup(podIndex, 1, 11)), "a group that is no group");
        Assert.False(Decodes(w => w.ActionGroups[1] = w.ActionGroups[0]), "one part changed twice");
    }

    private static WorldSnapshot Copy(WorldSnapshot world) =>
        WorldSnapshotCodec.TryDecode(WorldSnapshotCodec.Encode(world), SaveLimits.Default, out var copy, out var error) ? copy! : throw new InvalidDataException(error);

    [Fact]
    public void AServerRefusesASaveWhoseChunksOrGroupsItsRulesDoNotAllowAndReleasesTheStore()
    {
        var (folder, _, _, podIndex) = Saved();
        var cases = new (string What, Action<WorldSnapshot> Change)[]
        {
            ("a resource this galaxy does not have", w => w.OreChunks[0].Resource = Content.Galaxy.Resources.Length),
            ("a chunk with no mass", w => w.OreChunks[0].Mass = 0),
            ("a chunk with no grade", w => w.OreChunks[0].Grade = 0),
            ("a chunk with no identity", w => w.OreChunks[0].Id = 0),
            ("an identity at or past the counter", w => w.OreChunks[0].Id = w.NextOreChunkId),
            ("a chunk that is not a unit turn", w => w.OreChunks[0].Orientation = new Quaternion(2, 0, 0, 0)),
            ("a chunk beyond the pool", w => w.OreChunkCount = OreChunks.Capacity + 1),
            ("a group of a hull that is not there", w => w.ActionGroups[0] = new SavedActionGroup(w.HullCount + 3, 0, 3)),
            ("a group of a part the design has not", w => w.ActionGroups[0] = new SavedActionGroup(podIndex, 3, 3)),
            ("a group of a part far past the design", w => w.ActionGroups[0] = new SavedActionGroup(podIndex, 60, 3)),
            ("a group that is no group", w => w.ActionGroups[0] = new SavedActionGroup(podIndex, 0, 11)),
            ("a group on a hull with no lasers", w => w.ActionGroups[0] = new SavedActionGroup(Enumerable.Range(0, w.HullCount).First(i => w.Hulls[i].Design != Content.PodDesign), 0, 3)),
        };
        foreach (var (what, change) in cases)
        {
            Assert.Throws<InvalidDataException>(() => new SaveRig(folder, Content, world => { change(world); return world; }).Dispose());
            Assert.False(folder.Leased, $"the world is still held after refusing {what}");
        }

        // And with nothing changed it opens, so none of the refusals was the setup's.
        using var ok = new SaveRig(folder, Content, world => world);
        Assert.Equal(2, ok.Get<OreChunks>("_chunks").Items.Length);
    }

    [Fact]
    public void AWorldSavedUnderOtherMiningFiguresIsNotOfThisContent()
    {
        var (folder, _, _, _) = Saved();
        var other = MiningFixtures.Make(other: copy => copy.Replace("rules/mining.json", "\"oreKg\": 40", "\"oreKg\": 41"));
        Assert.NotEqual(Content.SaveIdentity, other.SaveIdentity);
        var opened = MemoryWorldStore.Open(folder, SaveCompatibility.Of(other));
        Assert.Equal(OpenOutcome.Incompatible, opened.Outcome);
    }
}
