using Brutal.Concurrency.Jobs;

namespace Prospect.Engine.Jobs;

/// <summary>Identifies a scheduled job. A handle whose job has finished stays valid and reports complete.</summary>
public readonly record struct JobHandle(int Index, int Generation)
{
    public bool IsValid => Generation != 0;
}

/// <summary>A job threw. The original exception is the inner exception.</summary>
public sealed class JobFaultedException(Exception inner) : Exception("A job threw: " + inner.Message, inner);

/// <summary>
/// A dependency graph of jobs executed on the runners of a BRUTAL <see cref="JobScheduler"/>.
///
/// BRUTAL runs jobs in batches: a runner accepts work only while idle and then executes its whole queue. This class
/// adds what the game needs on top: jobs that wait for other jobs, dispatch to whichever runner is free rather than
/// one batch for all of them, and handles that can be polled or waited on. Workers only flip a job's state; all graph
/// bookkeeping happens in <see cref="Pump"/>, so a dependent job becomes runnable on the pump after its
/// dependencies finish.
///
/// No allocation happens after the node and edge pools reach their working size. Jobs are objects owned by the
/// caller and are expected to be pooled.
/// </summary>
public sealed class JobSystem : IDisposable
{
    private const int StatePending = 0;
    private const int StateDone = 1;
    private const int StateFaulted = 2;

    private struct Node
    {
        public int Generation;
        public bool Live;
        public int PendingDependencies;
        public int FirstEdge;
        public int Cost;
        public int NextFree;
    }

    private struct Edge
    {
        public int Dependent;
        public int Next;
    }

    /// <summary>
    /// The object a runner executes. Workers touch only this (never the resizable node array), so growing the pools
    /// while jobs run is safe.
    /// </summary>
    private sealed class NodeJob(JobSystem owner) : IJob
    {
        public IJob? Job;
        public volatile int State;

        public void Run()
        {
            try
            {
                Job!.Run();
                State = StateDone;
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref owner._fault, e, null);
                State = StateFaulted;
            }
        }
    }

    private readonly JobScheduler _scheduler;
    private readonly Lock _lock = new();

    private Node[] _nodes;
    private NodeJob[] _nodeJobs;
    private int _freeNode = -1;
    private int _liveNodes;

    private Edge[] _edges;
    private int _freeEdge = -1;

    private int[] _ready;
    private int _readyCount;

    private readonly int[][] _inFlight;
    private readonly int[] _inFlightCount;
    private readonly int[] _assignedCost;

    private Exception? _fault;
    private long _completed;

    public JobSystem(int workerCount, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        WorkerCount = workerCount;
        _scheduler = new JobScheduler((uint)workerCount, name);

        _nodes = new Node[256];
        _nodeJobs = new NodeJob[256];
        _edges = new Edge[256];
        _ready = new int[256];
        for (var i = _nodes.Length - 1; i >= 0; i--)
            ReleaseNodeSlot(i, true);
        for (var i = _edges.Length - 1; i >= 0; i--)
        {
            _edges[i].Next = _freeEdge;
            _freeEdge = i;
        }

        _inFlight = new int[workerCount][];
        for (var i = 0; i < workerCount; i++)
            _inFlight[i] = new int[JobRunner.DEFAULT_CAPACITY];
        _inFlightCount = new int[workerCount];
        _assignedCost = new int[workerCount];
    }

    public int WorkerCount { get; }

    /// <summary>Jobs scheduled and not yet finished.</summary>
    public int Pending => Volatile.Read(ref _liveNodes);

    /// <summary>Jobs finished since creation.</summary>
    public long Completed => Volatile.Read(ref _completed);

    /// <summary>Copies BRUTAL's per-runner timing metrics; the span needs <see cref="WorkerCount"/> entries.</summary>
    public void GetRunnerMetrics(Span<JobRunnerMetrics> metrics) => _scheduler.GetMetricsUnsafe(metrics);

    /// <summary>Schedules a job with no dependencies. Safe from any thread, including from inside a job.</summary>
    public JobHandle Schedule(IJob job, int cost = 1) => Schedule(job, cost, default);

    /// <summary>Schedules a job that runs after <paramref name="dependsOn"/> has finished.</summary>
    public JobHandle Schedule(IJob job, JobHandle dependsOn, int cost = 1) =>
        Schedule(job, cost, new ReadOnlySpan<JobHandle>(in dependsOn));

    /// <summary>
    /// Schedules a job that runs after every handle in <paramref name="dependsOn"/> has finished.
    /// <paramref name="cost"/> is a relative time estimate used to balance work across runners.
    /// </summary>
    public JobHandle Schedule(IJob job, int cost, ReadOnlySpan<JobHandle> dependsOn)
    {
        lock (_lock)
        {
            if (_freeNode < 0)
                GrowNodes();

            var index = _freeNode;
            ref var node = ref _nodes[index];
            _freeNode = node.NextFree;
            node.Live = true;
            node.Cost = Math.Max(1, cost);
            _nodeJobs[index].Job = job;
            _nodeJobs[index].State = StatePending;
            node.FirstEdge = -1;
            node.PendingDependencies = 0;
            _liveNodes++;

            foreach (var dependency in dependsOn)
            {
                if (!IsLive(dependency))
                    continue;

                if (_freeEdge < 0)
                    GrowEdges();
                var edgeIndex = _freeEdge;
                ref var edge = ref _edges[edgeIndex];
                _freeEdge = edge.Next;
                edge.Dependent = index;
                ref var source = ref _nodes[dependency.Index];
                edge.Next = source.FirstEdge;
                source.FirstEdge = edgeIndex;
                node.PendingDependencies++;
            }

            if (node.PendingDependencies == 0)
                PushReady(index);

            return new JobHandle(index, node.Generation);
        }
    }

    /// <summary>True once the job has finished. Finished jobs are only observed by <see cref="Pump"/>.</summary>
    public bool IsComplete(JobHandle handle)
    {
        lock (_lock)
            return !IsLive(handle);
    }

    /// <summary>
    /// Collects finished jobs, releases their dependents and hands runnable jobs to idle runners. Call once per tick
    /// from the thread that owns this system. Rethrows the first exception a job threw.
    /// </summary>
    public void Pump()
    {
        lock (_lock)
        {
            var runners = _scheduler.GetRunners();

            for (var r = 0; r < runners.Length; r++)
            {
                var list = _inFlight[r];
                for (var i = _inFlightCount[r] - 1; i >= 0; i--)
                {
                    var index = list[i];
                    if (_nodeJobs[index].State == StatePending)
                        continue;

                    FinishNode(index);
                    list[i] = list[--_inFlightCount[r]];
                }
            }

            if (_readyCount > 0)
                Dispatch(runners);
        }

        var fault = Interlocked.Exchange(ref _fault, null);
        if (fault is not null)
            throw new JobFaultedException(fault);
    }

    /// <summary>Blocks until the job has finished, pumping the graph while waiting.</summary>
    public void Complete(JobHandle handle)
    {
        var spin = new SpinWait();
        while (true)
        {
            Pump();
            if (IsComplete(handle))
                return;
            spin.SpinOnce(-1);
        }
    }

    /// <summary>Blocks until every scheduled job has finished.</summary>
    public void CompleteAll()
    {
        var spin = new SpinWait();
        while (true)
        {
            Pump();
            if (Pending == 0)
                return;
            spin.SpinOnce(-1);
        }
    }

    public void Dispose()
    {
        _scheduler.AbortJobs(waitForCompletion: true);
    }

    private void Dispatch(ReadOnlySpan<JobRunner> runners)
    {
        var anyIdle = false;
        for (var r = 0; r < runners.Length; r++)
        {
            // A runner goes idle a moment after its last job flips to done; only an idle runner accepts work.
            var idle = _inFlightCount[r] == 0 && runners[r].State == JobState.Idle;
            _assignedCost[r] = idle ? 0 : -1;
            anyIdle |= idle;
        }

        if (!anyIdle)
            return;

        // A runner executes its whole queue before it accepts more, so an idle runner takes no more than its share
        // of what is ready; the rest waits for the runners that are still busy.
        var share = Math.Min(JobRunner.DEFAULT_CAPACITY, (_readyCount + runners.Length - 1) / runners.Length);

        for (var i = 0; i < _readyCount; i++)
        {
            var index = _ready[i];
            var best = -1;
            for (var r = 0; r < runners.Length; r++)
            {
                if (_assignedCost[r] < 0 || _inFlightCount[r] >= share)
                    continue;
                if (best < 0 || _assignedCost[r] < _assignedCost[best])
                    best = r;
            }

            if (best < 0)
            {
                // Every idle runner is full: keep the remainder for the next pump.
                Array.Copy(_ready, i, _ready, 0, _readyCount - i);
                _readyCount -= i;
                StartAssigned(runners);
                return;
            }

            _assignedCost[best] += _nodes[index].Cost;
            _inFlight[best][_inFlightCount[best]++] = index;
            runners[best].Queue(_nodeJobs[index]);
        }

        _readyCount = 0;
        StartAssigned(runners);
    }

    private void StartAssigned(ReadOnlySpan<JobRunner> runners)
    {
        for (var r = 0; r < runners.Length; r++)
        {
            if (_assignedCost[r] > 0)
                runners[r].ExecuteJobs();
            _assignedCost[r] = 0;
        }
    }

    private void FinishNode(int index)
    {
        ref var node = ref _nodes[index];
        var edgeIndex = node.FirstEdge;
        while (edgeIndex >= 0)
        {
            ref var edge = ref _edges[edgeIndex];
            ref var dependent = ref _nodes[edge.Dependent];
            if (--dependent.PendingDependencies == 0)
                PushReady(edge.Dependent);

            var next = edge.Next;
            edge.Next = _freeEdge;
            _freeEdge = edgeIndex;
            edgeIndex = next;
        }

        ReleaseNodeSlot(index, false);
        _liveNodes--;
        _completed++;
    }

    private void PushReady(int index)
    {
        if (_readyCount == _ready.Length)
            Array.Resize(ref _ready, _ready.Length * 2);
        _ready[_readyCount++] = index;
    }

    private bool IsLive(JobHandle handle) =>
        handle.IsValid
        && (uint)handle.Index < (uint)_nodes.Length
        && _nodes[handle.Index].Generation == handle.Generation
        && _nodes[handle.Index].Live;

    private void ReleaseNodeSlot(int index, bool initial)
    {
        ref var node = ref _nodes[index];
        node.Live = false;
        node.FirstEdge = -1;
        // Generation 0 is reserved for the default (invalid) handle.
        node.Generation = initial ? 1 : node.Generation == int.MaxValue ? 1 : node.Generation + 1;
        node.NextFree = _freeNode;
        _freeNode = index;
        _nodeJobs[index] ??= new NodeJob(this);
        _nodeJobs[index].Job = null;
    }

    private void GrowNodes()
    {
        var old = _nodes.Length;
        Array.Resize(ref _nodes, old * 2);
        Array.Resize(ref _nodeJobs, old * 2);
        for (var i = _nodes.Length - 1; i >= old; i--)
            ReleaseNodeSlot(i, true);
    }

    private void GrowEdges()
    {
        var old = _edges.Length;
        Array.Resize(ref _edges, old * 2);
        for (var i = _edges.Length - 1; i >= old; i--)
        {
            _edges[i].Next = _freeEdge;
            _freeEdge = i;
        }
    }
}
