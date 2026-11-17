using Brutal.Concurrency.Jobs;
using Prospect.Client.Render;
using Prospect.Engine.Jobs;
using Prospect.Game.World;
using Prospect.Game.World.RockEditing;

namespace Prospect.Client.World;

/// <summary>What the frame draws of one scraped rock: a range of the shared patch vertices and the runs of its base mesh still drawn.</summary>
internal readonly record struct RockPatchView(int FirstVertex, int VertexCount, int[] Runs, int RunCount);

/// <summary>
/// The render side of rock edits. It reads the session's edit log as it grows, keeps bricks of its own (touched only
/// by its worker job) and has the patch of each scraped rock near the camera meshed on a worker, one at a time, the
/// nearest that lacks one first. A finished patch is copied once into a range of the scene's shared patch buffer; a
/// range is reused only after the frames that drew it are done. The frame itself looks rocks up, copies a finished
/// patch in and nothing else: no memory is taken in it.
/// <para>
/// Level of detail: a rock is drawn with its patch while a pixel at its surface is smaller than the look's limit, and
/// as its plain facets beyond that, so a far groove costs nothing. A patch not drawn for a while gives its memory
/// back; so does the least recently drawn one when the buffer is full.
/// </para>
/// </summary>
internal sealed class RockPatches(JobSystem jobs, RockPatchLook look)
{
    private struct Patch
    {
        public int Slot;
        public int Edits;
        public int First, Count;
        public int[] Runs, Spare;
        public int RunCount;
        public long Used, RetryAt;
        public int Retries;
        public bool Ready, Building;
    }

    private readonly Patch[] _patches = NewPatches(look.MaxPatches);
    private readonly Build _build = new(look.MaxPatchVertices);
    private bool _stale;
    private JobHandle _handle;
    private bool _pending;
    private int _pendingPatch;
    private RockEditLog? _log;
    private Dictionary<RockRef, int> _slotOf = [];
    private int[] _editCount = [];
    private int[] _patchOf = [];
    private int _seenEdits, _seenRocks;
    private long _frame;

    // The shared buffer as free ranges in order of place, and ranges waiting for the frames that drew them.
    // There is at most one free range more than there are ranges in use or waiting.
    private readonly int[] _freeFirst = new int[look.MaxPatches * 3 + 4], _freeCount = new int[look.MaxPatches * 3 + 4];
    private int _freeRanges;
    private readonly int[] _waitFirst = new int[look.MaxPatches * 2 + 2], _waitCount = new int[look.MaxPatches * 2 + 2];
    private readonly long[] _waitUntil = new long[look.MaxPatches * 2 + 2];
    private int _waiting;

    private int _wantSlot = -1, _wantShape;
    private float _wantDistance, _wantRadius;

    /// <summary>Edits of the session's log taken in so far.</summary>
    public int Edits => _seenEdits;

    /// <summary>Patches in memory now.</summary>
    public int Ready { get; private set; }

    /// <summary>Patches built since the session began, and the worker time of the last one in milliseconds.</summary>
    public int Built { get; private set; }

    /// <inheritdoc cref="Built" />
    public double LastBuildMs { get; private set; }

    /// <summary>Vertices of the last patch built.</summary>
    public int LastVertices { get; private set; }

    private static Patch[] NewPatches(int count)
    {
        var patches = new Patch[count];
        for (var i = 0; i < count; i++) patches[i] = new Patch { Slot = -1, Runs = new int[4096], Spare = new int[4096] };
        return patches;
    }

    /// <summary>
    /// Starts a frame: takes in the session's edit log (null outside a game), what was added to it, and a patch the
    /// worker finished. <paramref name="scene"/> owns the buffer patches are copied into.
    /// </summary>
    public void Begin(RockEditLog? log, SceneRenderer scene)
    {
        _frame++;
        _wantSlot = -1;
        if (!ReferenceEquals(log, _log))
        {
            // The worker may still be on the old session's rock: nothing is drawn or started until it is done.
            _stale = true;
            if (_pending && !jobs.IsComplete(_handle)) return;
            _pending = false;
            Adopt(log, scene);
            _stale = false;
        }

        if (_log is null) return;

        // New rocks and edits, in the order they were made; a rock's count is what its patch is compared against.
        var rocks = _log.RockCount;
        var edits = _log.Count;
        for (; _seenRocks < rocks; _seenRocks++) _slotOf[_log.RockAt(_seenRocks)] = _seenRocks;
        for (; _seenEdits < edits; _seenEdits++) _editCount[_log.RockOf(_seenEdits)]++;

        for (var i = 0; i < _waiting;)
        {
            if (_waitUntil[i] > _frame)
            {
                i++;
                continue;
            }

            Free(_waitFirst[i], _waitCount[i]);
            _waiting--;
            (_waitFirst[i], _waitCount[i], _waitUntil[i]) = (_waitFirst[_waiting], _waitCount[_waiting], _waitUntil[_waiting]);
        }

        if (_pending && jobs.IsComplete(_handle))
        {
            _pending = false;
            if (ReferenceEquals(_build.Log, _log)) Install(scene);
        }

        // A patch nobody has drawn for a while gives its range back.
        for (var i = 0; i < _patches.Length; i++)
            if (_patches[i].Slot >= 0 && !_patches[i].Building && _frame - _patches[i].Used > look.IdleFrames)
                Drop(i);
    }

    /// <summary>
    /// Asks for a rock's patch while its instance is being made. <paramref name="metresPerPixel"/> is the size of a
    /// pixel at its surface and <paramref name="distance"/> how far that surface is. True with the patch to draw in
    /// place of the rock's mesh; false to draw the mesh as it is, which is also the answer for a rock never scraped,
    /// one too far for its grooves to show, and one whose patch is still on the worker.
    /// </summary>
    public bool Request(RockRef rock, int shape, float radius, float metresPerPixel, float distance, out RockPatchView view)
    {
        view = default;
        if (_stale || _seenRocks == 0 || !_slotOf.TryGetValue(rock, out var slot)) return false;
        var index = _patchOf[slot];
        var drawn = index >= 0 && _patches[index].Ready;
        if (metresPerPixel > look.DrawMetresPerPixel * (drawn ? look.KeepFactor : 1f)) return false;
        if (index >= 0) _patches[index].Used = _frame;
        if ((index < 0 || _patches[index].Edits != _editCount[slot] && _frame >= _patches[index].RetryAt) && (_wantSlot < 0 || distance < _wantDistance))
        {
            _wantSlot = slot;
            _wantDistance = distance;
            _wantShape = shape;
            _wantRadius = radius;
        }

        if (!drawn) return false;
        ref readonly var patch = ref _patches[index];
        view = new RockPatchView(patch.First, patch.Count, patch.Runs, patch.RunCount);
        return true;
    }

    /// <summary>Ends a frame: hands the worker the nearest rock that lacks an up-to-date patch, if it is free.</summary>
    public void End()
    {
        if (_pending || _stale || _wantSlot < 0 || _log is null) return;
        var index = _patchOf[_wantSlot];
        if (index < 0)
        {
            // A free entry, else the one drawn longest ago, but never one drawn this frame.
            var oldest = -1;
            for (var i = 0; i < _patches.Length && index < 0; i++)
            {
                if (_patches[i].Slot < 0) index = i;
                else if (_patches[i].Used < _frame && (oldest < 0 || _patches[i].Used < _patches[oldest].Used)) oldest = i;
            }

            if (index < 0)
            {
                if (oldest < 0) return;
                Drop(oldest);
                index = oldest;
            }

            _patches[index].Slot = _wantSlot;
            _patches[index].Retries = 0;
            _patches[index].RetryAt = 0;
            _patches[index].Edits = -1;
            _patches[index].Used = _frame;
            _patchOf[_wantSlot] = index;
        }

        _patches[index].Building = true;
        _pendingPatch = index;
        _build.Log = _log;
        _build.Slot = _wantSlot;
        _build.Shape = _wantShape;
        _build.Radius = _wantRadius;
        _build.Limit = _seenEdits;
        _build.MaxVertices = look.MaxPatchVertices;
        _build.MergeGap = look.MergeGap;
        _build.Runs = _patches[index].Spare;
        _handle = jobs.Schedule(_build, 2);
        _pending = true;
    }

    private void Adopt(RockEditLog? log, SceneRenderer scene)
    {
        // A new session: nothing of the old one's rocks is kept. Its ranges wait out the frames in flight as any other.
        for (var i = 0; i < _patches.Length; i++)
        {
            if (_patches[i].Slot >= 0) Drop(i);
            _patches[i].Building = false;
        }

        _log = log;
        _seenEdits = _seenRocks = 0;
        Ready = 0;
        if (log is null) return;
        var rocks = log.Rules.MaxEditedRocks;
        if (_editCount.Length != rocks)
        {
            _editCount = new int[rocks];
            _patchOf = new int[rocks];
            _slotOf = new Dictionary<RockRef, int>(rocks, EqualityComparer<RockRef>.Default);
        }

        Array.Clear(_editCount);
        Array.Fill(_patchOf, -1);
        _slotOf.Clear();

        // The worker's own bricks, taken here once a session: a patch job takes no memory of its own.
        _build.Mirror = new RockBricks(log, log.Rules.MaxBricks);
        if (_freeRanges == 0 && _waiting == 0)
        {
            _freeFirst[0] = 0;
            _freeCount[0] = scene.RockPatchCapacity;
            _freeRanges = 1;
        }
    }

    private void Install(SceneRenderer scene)
    {
        ref var patch = ref _patches[_pendingPatch];
        patch.Building = false;
        if (patch.Slot != _build.Slot) return;
        Built++;
        LastBuildMs = _build.Milliseconds;
        LastVertices = _build.Mesher.VertexCount;
        if (!_build.Ok)
        {
            // More vertices than one patch may have: this state of the rock is drawn as its plain facets.
            if (patch.Ready)
            {
                Later(patch.First, patch.Count);
                Ready--;
            }

            patch.Ready = false;
            patch.Count = 0;
            patch.Edits = _build.Edits;
            return;
        }

        var count = _build.Mesher.VertexCount;
        var first = count > 0 ? Take(count) : 0;
        if (first < 0)
        {
            // No room yet. The patch drawn longest ago gives its range back, but a range only returns once the frames
            // that drew it are done: the rock keeps what it is drawn with and the build is asked for again, later
            // each time, so a patch the buffer can never hold does not keep the worker busy.
            var oldest = -1;
            for (var i = 0; i < _patches.Length; i++)
                if (i != _pendingPatch && _patches[i].Ready && _patches[i].Used < _frame - 1 && (oldest < 0 || _patches[i].Used < _patches[oldest].Used))
                    oldest = i;
            if (oldest >= 0) Drop(oldest);
            patch.Retries = Math.Min(patch.Retries + 1, 8);
            patch.RetryAt = _frame + (FrameRenderer.FramesInFlight + 2 << patch.Retries);
            return;
        }

        // What the rock was drawn with until now waits for the frames that drew it.
        if (patch.Ready)
        {
            Later(patch.First, patch.Count);
            Ready--;
        }

        patch.Edits = _build.Edits;
        patch.Ready = true;
        patch.Retries = 0;
        patch.RetryAt = 0;
        patch.First = first;
        patch.Count = count;
        Ready++;
        if (count > 0) _build.Mesher.Vertices.AsSpan(0, count).CopyTo(scene.RockPatchVertices(first, count));
        (patch.Runs, patch.Spare) = (_build.Runs, patch.Runs);
        patch.RunCount = _build.RunCount;
    }

    private void Drop(int index)
    {
        ref var patch = ref _patches[index];
        if (patch.Ready)
        {
            Later(patch.First, patch.Count);
            Ready--;
        }

        if (patch.Slot >= 0 && patch.Slot < _patchOf.Length && _patchOf[patch.Slot] == index) _patchOf[patch.Slot] = -1;
        patch.Slot = -1;
        patch.Ready = false;
        patch.Count = 0;
    }

    // A range that frames in flight may still draw: free once they are done.
    private void Later(int first, int count)
    {
        // Never more than a patch a frame is replaced, so the list cannot fill; if it ever did, the range is lost
        // rather than handed out while a frame may still draw it.
        if (count == 0 || _waiting == _waitFirst.Length) return;
        _waitFirst[_waiting] = first;
        _waitCount[_waiting] = count;
        _waitUntil[_waiting++] = _frame + FrameRenderer.FramesInFlight + 1;
    }

    // First fit. Returns the first vertex, or -1.
    private int Take(int count)
    {
        for (var i = 0; i < _freeRanges; i++)
        {
            if (_freeCount[i] < count) continue;
            var first = _freeFirst[i];
            _freeFirst[i] += count;
            _freeCount[i] -= count;
            if (_freeCount[i] == 0)
            {
                _freeRanges--;
                for (var j = i; j < _freeRanges; j++) (_freeFirst[j], _freeCount[j]) = (_freeFirst[j + 1], _freeCount[j + 1]);
            }

            return first;
        }

        return -1;
    }

    // Gives a range back, joined to the free ranges next to it.
    private void Free(int first, int count)
    {
        var at = 0;
        while (at < _freeRanges && _freeFirst[at] < first) at++;
        if (at > 0 && _freeFirst[at - 1] + _freeCount[at - 1] == first)
        {
            _freeCount[at - 1] += count;
            if (at < _freeRanges && _freeFirst[at - 1] + _freeCount[at - 1] == _freeFirst[at])
            {
                _freeCount[at - 1] += _freeCount[at];
                _freeRanges--;
                for (var j = at; j < _freeRanges; j++) (_freeFirst[j], _freeCount[j]) = (_freeFirst[j + 1], _freeCount[j + 1]);
            }

            return;
        }

        if (at < _freeRanges && first + count == _freeFirst[at])
        {
            _freeFirst[at] = first;
            _freeCount[at] += count;
            return;
        }

        for (var j = _freeRanges; j > at; j--) (_freeFirst[j], _freeCount[j]) = (_freeFirst[j - 1], _freeCount[j - 1]);
        _freeFirst[at] = first;
        _freeCount[at] = count;
        _freeRanges++;
    }

    // One rock's patch, start to finish, on a worker: its new edits into the mirror bricks, then the mesh.
    private sealed class Build(int vertices) : IJob
    {
        public RockEditLog Log = null!;
        public int Slot, Shape, Limit, MaxVertices, MergeGap;
        public float Radius;
        public int[] Runs = [];
        public int RunCount, Edits;
        public bool Ok;
        public double Milliseconds;
        public readonly RockPatchMesher Mesher = new(vertices);
        public RockBricks Mirror = null!;

        public void Run()
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var next = Mirror.NextPending(Slot, Limit); next >= 0; next = Mirror.NextPending(Slot, Limit))
                Mirror.Apply(next);
            Edits = Mirror.Applied(Slot);
            Ok = Mesher.Build(Mirror, Slot, Shape, Radius, MaxVertices, MergeGap);
            RunCount = Ok ? Mesher.RunCount : 0;
            if (Ok)
            {
                // The patch keeps its own copy of the runs: the mesher's are overwritten by the next rock's.
                if (Runs.Length < RunCount * 2) Runs = new int[Math.Max(RunCount * 2, Runs.Length * 2)];
                Mesher.Runs.AsSpan(0, RunCount * 2).CopyTo(Runs);
            }

            Milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }
}
