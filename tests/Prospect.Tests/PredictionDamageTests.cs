using Prospect.Engine.Net;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Damage prediction uses validated wire health and isolated, restorable block state.</summary>
public sealed class PredictionDamageTests
{
    [Fact]
    public void SpeculativeLossCanBeRestoredWithoutChangingAuthoritativeState()
    {
        var content = GameContent.Load();
        using var known = new BlockStateStore();
        using var speculative = new BlockStateStore();
        var source = known.Create(content.Pod, true);
        var copy = speculative.Create(content.Pod, true);
        var health = content.Pod.IntactHitPoints[0];
        known.SetHitPoints(source, 0, health / 2);
        speculative.CopyFrom(copy, known, source);
        speculative.Hit(copy, 0, health);
        Assert.Equal(1u, speculative.Revision(copy));
        Assert.Equal(0u, known.Revision(source));
        Assert.Equal(health / 2, known.HitPoints(source)[0]);
        speculative.CopyFrom(copy, known, source);
        Assert.Equal(0u, speculative.Revision(copy));
        Assert.Equal(known.Alive(source).ToArray(), speculative.Alive(copy).ToArray());
        Assert.Equal(known.HitTotal(source), speculative.HitTotal(copy));
        Assert.Equal(known.HitPoints(source).ToArray(), speculative.HitPoints(copy).ToArray());
    }

    [Fact]
    public void SparseHealthCannotInventUnknownDeadOrNonFiniteBlocks()
    {
        var content = GameContent.Load();
        using var replica = new BlockReplica(content);
        var record = new HullRecord { Id = 1, Design = (byte)content.PodDesign, HasHealth = true, HealthCount = 1 };
        record.DamagedBlocks[0] = 0;
        record.RemainingHealth[0] = float.NaN;
        Assert.False(replica.Accept(record, 1));
        record.RemainingHealth[0] = 1;
        record.DamagedBlocks[0] = (ushort)content.Pod.BlockCount;
        Assert.False(replica.Accept(record, 1));
        record.DamagedBlocks[0] = 0;
        record.Revision = record.LossCount = 1;
        record.Losses[0] = 0;
        Assert.False(replica.Accept(record, 1));
        record.HealthCount = 0;
        Assert.True(replica.Accept(record, 1));
        record.LossCount = 0;
        record.LossStart = 1;
        record.HealthCount = 1;
        Assert.False(replica.Accept(record, 2));
    }

    [Fact]
    public void CompleteSparseHealthIsBoundedAndRejectsInvalidValues()
    {
        var record = new HullRecord
        {
            HasHealth = true,
            HealthCount = HullRecord.MaxHealth,
            Revision = HullRecord.MaxLosses,
            LossCount = HullRecord.MaxLosses,
            Order = new HullOrder { Kind = OrderKind.Jump },
            VisualVersion = 9,
            Spark = new CollisionSpark { Sequence = 3, Tick = 1 },
            State = HullState.Spawn(Double3.Zero, GameContent.Load().Pod.Flight),
        };
        for (var i = 0; i < record.HealthCount; i++)
        {
            record.DamagedBlocks[i] = (ushort)i;
            record.RemainingHealth[i] = 1;
        }
        var bytes = new byte[HullRecord.MaxWireSize];
        var writer = new NetWriter(bytes);
        record.Write(ref writer);
        Assert.Equal(HullRecord.MaxWireSize, writer.Length);
        var reader = new NetReader(writer.Written);
        Assert.True(HullRecord.TryRead(ref reader, out var read));
        Assert.Equal(record.HealthCount, read.HealthCount);
        Assert.Equal(record.VisualVersion, read.VisualVersion);
        Assert.Equal(record.Spark.Sequence, read.Spark.Sequence);
        Assert.Equal(1, read.RemainingHealth[record.HealthCount - 1]);
        reader = new NetReader(writer.Written[..^1]);
        Assert.False(HullRecord.TryRead(ref reader, out _));
        BitConverter.TryWriteBytes(bytes.AsSpan(6, 4), float.NaN);
        reader = new NetReader(writer.Written);
        Assert.False(HullRecord.TryRead(ref reader, out _));
    }
}
