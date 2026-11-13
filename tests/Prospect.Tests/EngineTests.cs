using Brutal.Concurrency.Jobs;
using Prospect.Engine.IO;
using Prospect.Engine.Jobs;
using Prospect.Engine.Net;
using Prospect.Engine.Threading;
using Xunit;

namespace Prospect.Tests;

public sealed class JobSystemTests
{
    private sealed class ActionJob(Action action) : IJob
    {
        public void Run() => action();
    }

    [Fact]
    public void RunsEveryScheduledJob()
    {
        using var jobs = new JobSystem(4, "Test");
        var counter = 0;
        var job = new ActionJob(() => Interlocked.Increment(ref counter));
        for (var i = 0; i < 5000; i++)
            jobs.Schedule(job);

        jobs.CompleteAll();

        Assert.Equal(5000, counter);
        Assert.Equal(0, jobs.Pending);
        Assert.Equal(5000, jobs.Completed);
    }

    [Fact]
    public void DependentJobRunsAfterItsDependencies()
    {
        using var jobs = new JobSystem(4, "Test");
        var order = new List<string>();
        var gate = new Lock();

        void Record(string name)
        {
            lock (gate)
                order.Add(name);
        }

        var a = jobs.Schedule(new ActionJob(() =>
        {
            Thread.Sleep(20);
            Record("a");
        }));
        var b = jobs.Schedule(new ActionJob(() => Record("b")));
        var c = jobs.Schedule(new ActionJob(() => Record("c")), 1, [a, b]);
        var d = jobs.Schedule(new ActionJob(() => Record("d")), c);

        jobs.Complete(d);

        Assert.Equal(4, order.Count);
        Assert.True(order.IndexOf("c") > order.IndexOf("a"));
        Assert.True(order.IndexOf("c") > order.IndexOf("b"));
        Assert.Equal("d", order[3]);
        Assert.True(jobs.IsComplete(a) && jobs.IsComplete(b) && jobs.IsComplete(c) && jobs.IsComplete(d));
    }

    [Fact]
    public void JobsCanScheduleFollowUpJobs()
    {
        using var jobs = new JobSystem(3, "Test");
        var counter = 0;
        IJob? leaf = new ActionJob(() => Interlocked.Increment(ref counter));
        var root = new ActionJob(() =>
        {
            for (var i = 0; i < 100; i++)
                jobs.Schedule(leaf);
        });

        for (var i = 0; i < 10; i++)
            jobs.Schedule(root);
        jobs.CompleteAll();

        Assert.Equal(1000, counter);
    }

    [Fact]
    public void FaultIsRethrownOnPump()
    {
        using var jobs = new JobSystem(2, "Test");
        jobs.Schedule(new ActionJob(() => throw new InvalidOperationException("boom")));

        var thrown = Assert.Throws<JobFaultedException>(jobs.CompleteAll);
        Assert.IsType<InvalidOperationException>(thrown.InnerException);

        // The system keeps working after a fault.
        var ran = false;
        jobs.Schedule(new ActionJob(() => ran = true));
        jobs.CompleteAll();
        Assert.True(ran);
    }

    [Fact]
    public void SteadyStateSchedulingDoesNotAllocate()
    {
        using var jobs = new JobSystem(4, "Test");
        var counter = 0;
        var job = new ActionJob(() => Interlocked.Increment(ref counter));

        void Round()
        {
            for (var i = 0; i < 200; i++)
                jobs.Schedule(job);
            jobs.CompleteAll();
        }

        for (var i = 0; i < 20; i++)
            Round();

        var allocation = new AllocationProbe();
        var rounds = 0;
        for (var i = 0; i < 20; i++)
        {
            while (allocation.Measuring())
            {
                Round();
                rounds++;
            }
        }

        Assert.Equal(0, allocation.Bytes);
        Assert.Equal((20 + rounds) * 200, counter);
    }

    [Fact]
    public void FinishedHandleStaysCompleteAfterItsSlotIsReused()
    {
        using var jobs = new JobSystem(2, "Test");
        var job = new ActionJob(() => { });
        var first = jobs.Schedule(job);
        jobs.CompleteAll();

        using var block = new ManualResetEventSlim();
        var second = jobs.Schedule(new ActionJob(block.Wait));
        jobs.Pump();

        Assert.True(jobs.IsComplete(first));
        Assert.False(jobs.IsComplete(second));
        Assert.True(jobs.IsComplete(default));
        block.Set();
        jobs.CompleteAll();
    }
}

public sealed class MessageRingTests
{
    [Fact]
    public void RoundTripsMessagesAcrossTheWrapPoint()
    {
        using var ring = new MessageRing(1024);
        var payload = new byte[100];
        var read = new byte[256];

        for (var round = 0; round < 200; round++)
        {
            for (var i = 0; i < payload.Length; i++)
                payload[i] = (byte)(round + i);

            Assert.True(ring.TryWrite((byte)(round % 7), payload));
            Assert.True(ring.TryRead(read, out var tag, out var length));
            Assert.Equal(round % 7, tag);
            Assert.Equal(payload.Length, length);
            Assert.True(payload.AsSpan().SequenceEqual(read.AsSpan(0, length)));
        }

        Assert.False(ring.TryRead(read, out _, out _));
    }

    [Fact]
    public void RefusesWritesWhenFullWithoutCorruptingQueuedMessages()
    {
        using var ring = new MessageRing(1024);
        var payload = new byte[200];
        var accepted = 0;
        while (ring.TryWrite(1, payload))
            accepted++;

        Assert.Equal(1024 / 204, accepted);
        var read = new byte[256];
        for (var i = 0; i < accepted; i++)
            Assert.True(ring.TryRead(read, out _, out var length) && length == 200);
        Assert.False(ring.TryRead(read, out _, out _));
    }

    [Fact]
    public void DeliversInOrderBetweenTwoThreads()
    {
        using var ring = new MessageRing(1 << 16);
        const int count = 200_000;
        var producer = new Thread(() =>
        {
            Span<byte> message = stackalloc byte[4];
            for (var i = 0; i < count; i++)
            {
                BitConverter.TryWriteBytes(message, i);
                while (!ring.TryWrite(0, message))
                    Thread.SpinWait(8);
            }
        });
        producer.Start();

        Span<byte> read = stackalloc byte[16];
        for (var expected = 0; expected < count;)
        {
            if (!ring.TryRead(read, out _, out var length))
                continue;
            Assert.Equal(4, length);
            Assert.Equal(expected, BitConverter.ToInt32(read));
            expected++;
        }

        producer.Join();
    }
}

public sealed class SnapshotExchangeTests
{
    private sealed class Box
    {
        public int Value;
        public int Check;
    }

    [Fact]
    public void ReaderAlwaysSeesTheLatestCompleteSnapshot()
    {
        var exchange = new SnapshotExchange<Box>(() => new Box());
        Assert.False(AcquireIsNew(exchange));

        exchange.WriteBuffer.Value = 1;
        exchange.Publish();
        exchange.WriteBuffer.Value = 2;
        exchange.Publish();

        Assert.Equal(2, exchange.Acquire(out var isNew).Value);
        Assert.True(isNew);
        Assert.Equal(2, exchange.Acquire(out isNew).Value);
        Assert.False(isNew);
    }

    [Fact]
    public void NeverExposesAHalfWrittenSnapshot()
    {
        var exchange = new SnapshotExchange<Box>(() => new Box());
        using var stop = new CancellationTokenSource();
        var writer = new Thread(() =>
        {
            for (var i = 1; !stop.IsCancellationRequested; i++)
            {
                var buffer = exchange.WriteBuffer;
                buffer.Value = i;
                Thread.SpinWait(4);
                buffer.Check = -i;
                exchange.Publish();
            }
        });
        writer.Start();

        var last = 0;
        for (var i = 0; i < 2_000_000; i++)
        {
            var box = exchange.Acquire(out _);
            Assert.Equal(-box.Value, box.Check);
            Assert.True(box.Value >= last);
            last = box.Value;
        }

        stop.Cancel();
        writer.Join();
    }

    private static bool AcquireIsNew(SnapshotExchange<Box> exchange)
    {
        exchange.Acquire(out var isNew);
        return isNew;
    }
}

public sealed class NetBufferTests
{
    [Fact]
    public void RoundTripsEveryType()
    {
        Span<byte> buffer = stackalloc byte[128];
        var writer = new NetWriter(buffer);
        writer.WriteByte(7);
        writer.WriteBool(true);
        writer.WriteUInt16(65000);
        writer.WriteInt32(-123456);
        writer.WriteInt64(long.MinValue + 5);
        writer.WriteSingle(1.5f);
        writer.WriteDouble(-2.25);
        writer.WriteVarUInt(0);
        writer.WriteVarUInt(300);
        writer.WriteVarUInt(uint.MaxValue);
        writer.WriteString("Kepler-22 ☄");

        var reader = new NetReader(writer.Written);
        Span<char> text = stackalloc char[32];
        Assert.Equal(7, reader.ReadByte());
        Assert.True(reader.ReadBool());
        Assert.Equal(65000, reader.ReadUInt16());
        Assert.Equal(-123456, reader.ReadInt32());
        Assert.Equal(long.MinValue + 5, reader.ReadInt64());
        Assert.Equal(1.5f, reader.ReadSingle());
        Assert.Equal(-2.25, reader.ReadDouble());
        Assert.Equal(0u, reader.ReadVarUInt());
        Assert.Equal(300u, reader.ReadVarUInt());
        Assert.Equal(uint.MaxValue, reader.ReadVarUInt());
        Assert.Equal("Kepler-22 ☄", text[..reader.ReadString(text, 64)].ToString());
        Assert.True(reader.IsValid);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void TruncatedInputInvalidatesTheReaderInsteadOfThrowing()
    {
        var reader = new NetReader([1, 2, 3]);
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.False(reader.IsValid);
        Assert.Equal(0, reader.ReadByte());
    }

    [Fact]
    public void OversizedOrMalformedStringsAreRejected()
    {
        Span<char> text = stackalloc char[8];

        var tooLong = new NetReader([200, 1, 65, 65]);
        Assert.Equal(0, tooLong.ReadString(text, 16));
        Assert.False(tooLong.IsValid);

        var endlessVarInt = new NetReader([0x80, 0x80, 0x80, 0x80, 0x80, 0x80]);
        Assert.Equal(0u, endlessVarInt.ReadVarUInt());
        Assert.False(endlessVarInt.IsValid);
    }
}

public sealed class AtomicFileTests
{
    [Fact]
    public void ReplacesContentsAndLeavesNoTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prospect-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "nested", "save.bin");
            AtomicFile.Write(path, [1, 2, 3]);
            AtomicFile.Write(path, [9, 8]);

            Assert.Equal([9, 8], File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
