using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Brutal.Concurrency.Jobs;
using Prospect.Engine.Jobs;
using Prospect.Game.Client;
using Prospect.Game.Protocol;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// A thread that has to wait for a lock must take no managed memory for it. The first wait on a
/// <see cref="System.Threading.Lock"/> does: the runtime makes the lock's wait event then (56 bytes, and some 340 more
/// the first time in a process), on whichever thread happens to wait. That needs the holder to be descheduled while
/// it holds the lock, so it shows only on a busy machine: it was the rare "280 bytes in 1 frame" of the benchmark.
/// <c>lock</c> on a plain object waits on native state and takes nothing. So the game's own locks are plain objects.
/// </summary>
public sealed class LockContentionTests
{
    private sealed class Nothing : IJob
    {
        public void Run()
        {
        }
    }

    // Holds a lock on another thread for forty milliseconds, far beyond any spinning, while this thread calls
    // into the code that takes it, and measures what that call took.
    private static void Contended(AllocationProbe probe, object gate, Action call)
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            if (gate is Lock exclusive)
            {
                exclusive.Enter();
                held.Set();
                release.Wait();
                exclusive.Exit();
                return;
            }

            lock (gate)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait(TestContext.Current.CancellationToken);
        var timer = new Thread(() =>
        {
            Thread.Sleep(40);
            release.Set();
        });
        timer.Start();
        var started = Stopwatch.GetTimestamp();
        probe.Start();
        call();
        probe.Stop();
        var waited = Stopwatch.GetElapsedTime(started);
        holder.Join();
        timer.Join();
        Assert.True(waited > TimeSpan.FromMilliseconds(20), $"the call did not wait for the lock ({waited.TotalMilliseconds:F1} ms): the test holds the wrong one");
    }

    private static object GateOf(object owner, string field) =>
        owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    [Fact]
    public void TheRenderThreadWaitingForTheInputBridgeTakesNoMemory()
    {
        var request = new RockScrapeRequest { Rock = new RockRef(new SectorCoord(0, 0, 0), 1), RadiusMm = 1000, Rays = 1 };
        request.Directions[0] = Vector3.UnitX;
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            // A new bridge each run: only a lock's first wait is the one that costs.
            var bridge = new PlayerInputBridge();
            Action call = () => bridge.DebugScrape(request);
            call();
            Contended(probe, GateOf(bridge, "_lock"), call);
        }

        Assert.Equal(0, probe.Bytes);
    }

    [Fact]
    public void AThreadWaitingForTheJobSystemTakesNoMemory()
    {
        var probe = new AllocationProbe();
        while (probe.Run())
        {
            using var jobs = new JobSystem(1, "LockTest");
            var job = new Nothing();
            var handle = jobs.Schedule(job);
            jobs.Complete(handle);
            Action schedule = () => handle = jobs.Schedule(job);
            Action poll = () => jobs.IsComplete(handle);
            Action pump = jobs.Pump;
            schedule();
            poll();
            pump();
            jobs.Complete(handle);
            var gate = GateOf(jobs, "_lock");
            Contended(probe, gate, schedule);
            Contended(probe, gate, poll);
            Contended(probe, gate, pump);
            jobs.Complete(handle);
        }

        Assert.Equal(0, probe.Bytes);
    }

    // A long list of edits is worked into bricks off the simulation thread. Handing the work over and collecting it
    // must take nothing there, and the work must not go to the thread pool: the pool makes a worker the first time it
    // needs one more, which on a machine of four cores took about a kilobyte in the middle of the benchmark.
    [Fact]
    public void HandingARockToTheReplayThreadTakesNoMemory()
    {
        Assert.False(typeof(IThreadPoolWorkItem).IsAssignableFrom(typeof(RockEditReplay)));
        var content = GameContent.Load();
        var rock = new RockRef(new SectorCoord(1, 2, 3), 7);
        var log = new RockEditLog(content.RockEdits);
        var inline = new RockBricks(log, content.RockEdits.MaxBricksPerRock);
        foreach (var edit in RockEditTests.Groove(100))
            inline.Apply(log.Append(rock, edit));
        using var replay = new RockEditReplay(log);

        bool Replayed()
        {
            replay.Start(0, log.Count);
            var deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (replay.TakeDone(out var slot)) return slot == 0;
                Thread.Yield();
            }

            return false;
        }

        // Once before measuring, as the first rock of a session comes before anything is measured.
        Assert.True(Replayed());
        var probe = new AllocationProbe();
        for (var i = 0; i < 20; i++)
            while (probe.Measuring())
                Assert.True(Replayed());
        Assert.Equal(0, probe.Bytes);
        Assert.Equal(inline.Hash(0), replay.Bricks.Hash(0));
        Assert.Equal(inline.BrickCount(0), replay.Bricks.BrickCount(0));
    }

    // Every lock of the game is held to it: a new one of the allocating kind fails here, not once in a hundred
    // benchmark runs on a busy machine. The listing server is a web service with no frame or tick and is left out.
    [Fact]
    public void NoLockOfTheGameIsOfTheKindWhoseFirstWaitAllocates()
    {
        Assembly[] assemblies = [typeof(JobSystem).Assembly, typeof(PlayerInputBridge).Assembly, typeof(Prospect.Client.Render.MeshVertex).Assembly, typeof(Prospect.Game.Server.ServerHost).Assembly];
        var found = new List<string>();
        foreach (var assembly in assemblies.Distinct())
            foreach (var type in assembly.GetTypes())
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (field.FieldType == typeof(Lock))
                        found.Add($"{type.FullName}.{field.Name}");
        Assert.True(found.Count == 0, "System.Threading.Lock fields (use a plain object and lock on it): " + string.Join(", ", found));
    }
}
