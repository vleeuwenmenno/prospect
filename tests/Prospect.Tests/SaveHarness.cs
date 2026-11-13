using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Persistence;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// What a world folder is to <see cref="MemoryWorldStore"/>: the committed bytes, their generation, the
/// clean-close mark and the lease, plus the switches a test uses to make the store slow or make it fail.
/// </summary>
internal sealed class MemoryWorld
{
    private readonly object _lock = new();

    public byte[]? Bytes;
    public long Generation;
    public bool Clean;
    public bool Leased;

    /// <summary>Every commit that went through, in order: its generation, whether it was final, and its bytes.</summary>
    public readonly List<(long Generation, bool Final, byte[] Bytes)> History = [];
    public readonly List<BackupReason> Backups = [];

    /// <summary>Commit calls, successful or not, and how long the last one took to encode.</summary>
    public int Attempts;
    public double LastEncodeMs;

    /// <summary>The next this-many commits fail with <see cref="FailWith"/>, or throw if <see cref="Throw"/> is set.</summary>
    public int FailCommits;
    public CommitOutcome FailWith = CommitOutcome.Failed;
    public bool Throw;
    public bool FailBackups;
    public CheckpointOutcome Checkpoint = CheckpointOutcome.Completed;

    /// <summary>While set and not signalled, a commit waits here, inside the store, holding the buffer.</summary>
    public ManualResetEventSlim? Gate;

    /// <summary>Signalled each time a commit has entered the store.</summary>
    public readonly ManualResetEventSlim Entered = new();

    public object Lock => _lock;

    public int Commits
    {
        get { lock (_lock) return History.Count; }
    }

    public int FinalCommits
    {
        get { lock (_lock) return History.Count(entry => entry.Final); }
    }

    /// <summary>The newest committed state, decoded.</summary>
    public WorldSnapshot Decode()
    {
        byte[] bytes;
        lock (_lock)
            bytes = Bytes!;
        Assert.True(WorldSnapshotCodec.TryDecode(bytes, SaveLimits.Default, out var snapshot, out var error), error);
        return snapshot!;
    }
}

/// <summary>
/// A world store that keeps the world in memory, for the tests of the lifecycle around a store. It keeps the
/// store's contract in every respect a server can see: the lease, the clean-close mark that opening clears, the
/// generation check, one transaction per commit, and a snapshot it never holds on to. It encodes with the real
/// codec, so what a test loads back went through the same bytes a real store would keep.
/// </summary>
internal sealed class MemoryWorldStore : IWorldStore
{
    private readonly MemoryWorld _world;
    private bool _disposed;

    private MemoryWorldStore(MemoryWorld world) => _world = world;

    public static CreateResult Create(MemoryWorld folder, WorldSnapshot initial)
    {
        lock (folder.Lock)
        {
            if (folder.Bytes is not null || folder.Leased)
                return new CreateResult(CreateOutcome.Exists, null, 0, "the folder already holds a world");
            folder.Bytes = WorldSnapshotCodec.Encode(initial);
            folder.Generation = 1;
            folder.Clean = false;
            folder.Leased = true;
            return new CreateResult(CreateOutcome.Created, new MemoryWorldStore(folder), 1);
        }
    }

    public static OpenResult Open(MemoryWorld folder, SaveCompatibility expected)
    {
        lock (folder.Lock)
        {
            if (folder.Leased)
                return new OpenResult(OpenOutcome.Busy, null, null, 0, false, "the world is in use");
            if (folder.Bytes is null)
                return new OpenResult(OpenOutcome.Missing, null, null, 0, false, "no world here");
            if (!WorldSnapshotCodec.TryReadCompatibility(folder.Bytes, out var saved))
                return new OpenResult(OpenOutcome.Damaged, null, null, 0, false, "not a saved world");
            if (saved != expected)
                return new OpenResult(OpenOutcome.Incompatible, null, null, 0, false, "saved with other content");
            if (!WorldSnapshotCodec.TryDecode(folder.Bytes, SaveLimits.Default, out var snapshot, out var error))
                return new OpenResult(OpenOutcome.Damaged, null, null, 0, false, error);

            // The lease first, then the mark: from here the world reads as running until a final commit.
            var wasClean = folder.Clean;
            folder.Leased = true;
            folder.Clean = false;
            return new OpenResult(OpenOutcome.Opened, new MemoryWorldStore(folder), snapshot, folder.Generation, wasClean);
        }
    }

    public CommitResult Commit(WorldSnapshot snapshot, long expectedGeneration, bool final)
    {
        Assert.False(_disposed);
        Interlocked.Increment(ref _world.Attempts);
        _world.Entered.Set();
        _world.Gate?.Wait();

        var start = Stopwatch.GetTimestamp();
        var bytes = WorldSnapshotCodec.Encode(snapshot);
        _world.LastEncodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        lock (_world.Lock)
        {
            if (_world.FailCommits > 0)
            {
                _world.FailCommits--;
                if (_world.Throw)
                    throw new IOException("the disk is full");
                return new CommitResult(_world.FailWith, _world.Generation, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: "the disk is full");
            }

            if (expectedGeneration != _world.Generation)
                return new CommitResult(CommitOutcome.Conflict, _world.Generation, snapshot.NextWorldTick, DateTimeOffset.UtcNow, Error: "the world was written by something else");
            _world.Bytes = bytes;
            _world.Generation++;
            if (final)
                _world.Clean = true;
            _world.History.Add((_world.Generation, final, bytes));
            return new CommitResult(CommitOutcome.Committed, _world.Generation, snapshot.NextWorldTick, DateTimeOffset.UtcNow,
                final ? _world.Checkpoint : CheckpointOutcome.NotAttempted,
                final && _world.Checkpoint == CheckpointOutcome.Failed ? "checkpoint could not complete" : null);
        }
    }

    public BackupResult Backup(BackupReason reason)
    {
        Assert.False(_disposed);
        lock (_world.Lock)
        {
            if (_world.FailBackups)
                return new BackupResult(BackupOutcome.Failed, 0, "no room for a recovery copy");
            _world.Backups.Add(reason);
            return new BackupResult(BackupOutcome.Completed, _world.Generation);
        }
    }

    public void Dispose()
    {
        lock (_world.Lock)
        {
            _disposed = true;
            _world.Leased = false;
        }
    }
}

/// <summary>
/// One end of a connection a test plays by hand, like <see cref="ScriptedTransport"/> but for a single peer that
/// may be the host's own client: it says whether it is in-process, and it reads what the server sends back.
/// </summary>
internal sealed class OwnerLink(GameContent content, bool inProcess = true) : ITransport
{
    private readonly Queue<(byte Kind, NetChannel Channel, byte[] Payload)> _events = new();

    public bool Open;
    public bool ClosedByServer;
    public int Welcomes;
    public int Rejects;
    public int Snapshots;
    public int RockMessages;
    public WelcomeMessage Welcome;
    public SnapshotHeader LastSnapshot;
    public EditorStateMessage Editor;

    /// <summary>True if two snapshots in a row ever named the same interval, or one went back.</summary>
    public bool SnapshotRepeated;

    /// <summary>Set from a test's thread to make the next poll throw, as a tick that fails halfway does.</summary>
    public volatile bool Fault;

    public string Description => "owner link";
    public int MaxPayloadSize => 1 << 20;
    public bool IsInProcess => inProcess;

    public void Connect()
    {
        Open = true;
        ClosedByServer = false;
        _events.Enqueue((0, default, []));
    }

    public void Deliver(byte[] payload, NetChannel channel = NetChannel.Reliable) => _events.Enqueue((1, channel, payload));

    public void HangUp() => _events.Enqueue((2, default, []));

    /// <summary>A hello as a client of this build with this content sends it.</summary>
    public static byte[] HelloBytes(GameContent content, string name = "Host")
    {
        var bytes = new byte[HelloMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        new HelloMessage { ProtocolVersion = ProtocolInfo.Version, PlayerName = name, Content = content.Identity }.Write(ref writer);
        return bytes[..writer.Length];
    }

    /// <summary>Connects and says hello: the server lets the client in, and waits for it to have the catalogue.</summary>
    public void Hello()
    {
        Connect();
        Deliver(HelloBytes(content));
    }

    /// <summary>Says the catalogue is here, as a client that already had every design does.</summary>
    public void Ready() => Deliver([(byte)MessageId.CatalogueReady]);

    /// <summary>Connects, says hello and that the catalogue is here.</summary>
    public void Join()
    {
        Hello();
        Ready();
    }

    public void Control(uint hull)
    {
        var bytes = new byte[5];
        var writer = new NetWriter(bytes);
        writer.WriteByte((byte)MessageId.Control);
        writer.WriteUInt32(hull);
        Deliver(bytes);
    }

    public void Edit(EditorRequestKind kind, uint value)
    {
        var bytes = new byte[EditorRequestMessage.MaxWireSize];
        var writer = new NetWriter(bytes);
        new EditorRequestMessage { Kind = kind, Hull = value, Token = value }.Write(ref writer, default);
        Deliver(bytes[..writer.Length]);
    }

    public bool Poll(ITransportHandler handler, ReceiveBudget budget)
    {
        if (Fault)
            throw new InvalidOperationException("a scripted fault inside a tick");
        while (_events.TryDequeue(out var e))
        {
            switch (e.Kind)
            {
                case 0:
                    handler.OnConnected(this, 0);
                    break;
                case 2 when Open:
                    Open = false;
                    handler.OnDisconnected(this, 0, DisconnectReason.Requested);
                    break;
                case 1 when Open:
                    handler.OnMessage(this, 0, e.Channel, e.Payload);
                    break;
            }
        }

        return true;
    }

    public bool Send(int peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        if (!Open || payload.IsEmpty)
            return false;
        var reader = new NetReader(payload[1..]);
        switch ((MessageId)payload[0])
        {
            case MessageId.Welcome when WelcomeMessage.TryRead(ref reader, content.SettingCatalog, out var welcome):
                Welcomes++;
                Welcome = welcome;
                break;
            case MessageId.Reject:
                Rejects++;
                break;
            case MessageId.RockDeltas:
                RockMessages++;
                break;
            case MessageId.EditorState when EditorStateMessage.TryRead(ref reader, out var state):
                Editor = state;
                break;
            case MessageId.Snapshot when SnapshotHeader.TryRead(ref reader, out var header):
                SnapshotRepeated |= Snapshots > 0 && header.ServerTick != LastSnapshot.ServerTick + 1;
                Snapshots++;
                LastSnapshot = header;
                break;
        }

        return true;
    }

    public void Disconnect(int peer)
    {
        if (!Open)
            return;
        Open = false;
        ClosedByServer = true;
    }

    public int GetRoundTripMs(int peer) => 0;

    public ulong GetPeerOrigin(int peer) => 0;

    public void Dispose() { }
}

/// <summary>
/// A saved world's server that is not started: the test calls its tick, moves its clock and plays the host's own
/// client on an in-process link, and a stranger on a network transport beside it.
/// </summary>
internal sealed class SaveRig : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private long _callback;

    public readonly ManualTime Clock = new();
    public readonly MemoryWorld Folder;
    public readonly OwnerLink Link;
    public readonly ScriptedTransport Remote = new(64);
    public readonly ServerHost Server;

    /// <summary>Whether the world was closed cleanly before this rig opened it.</summary>
    public readonly bool WasClean;

    /// <summary>Opens a world a rig or a test saved before. <paramref name="edit"/> may hand back another snapshot to restore.</summary>
    public SaveRig(
        MemoryWorld folder,
        GameContent content,
        Func<WorldSnapshot, WorldSnapshot>? edit = null,
        SaveLimits? limits = null,
        int autosaveSeconds = 30,
        int stopTimeoutMilliseconds = 15000,
        double backupSeconds = 600,
        ServerConfig? config = null,
        Func<IWorldStore, IWorldStore>? wrapStore = null
    )
    {
        Folder = folder;
        Link = new OwnerLink(content);
        var open = MemoryWorldStore.Open(folder, SaveCompatibility.Of(content));
        Assert.Equal(OpenOutcome.Opened, open.Outcome);
        WasClean = open.WasCleanClose;
        var world = new WorldPersistence(wrapStore?.Invoke(open.Store!) ?? open.Store!, edit?.Invoke(open.Snapshot!) ?? open.Snapshot!, open.Generation)
        {
            AutosaveSeconds = autosaveSeconds,
            Limits = limits ?? SaveLimits.Default,
            FinalRetryMilliseconds = 1,
            StopTimeoutMilliseconds = stopTimeoutMilliseconds,
            BackupSeconds = backupSeconds,
        };
        config ??= new ServerConfig();
        Server = new ServerHost(
            new ServerConfig
            {
                Name = "Saved",
                Visibility = config.Visibility,
                Port = config.Port,
                AccountAuthority = config.AccountAuthority,
                Listing = config.Listing,
                Clock = Clock,
                World = world,
                Settings = config.Settings,
                GalaxySeed = config.GalaxySeed,
                SpawnSector = config.SpawnSector,
            },
            content,
            Link,
            Remote
        );
    }

    /// <summary>A new world in a new folder, stored through the store's own create, and left closed for a rig to open.</summary>
    public static MemoryWorld NewWorld(GameContent content, ulong seed = ServerConfig.DefaultSeed)
    {
        var folder = new MemoryWorld();
        var initial = WorldSnapshot.NewWorld(content, Guid.NewGuid(), "Test World", DateTimeOffset.UnixEpoch.AddDays(20000), seed, content.SettingCatalog.Defaults, default);
        var created = MemoryWorldStore.Create(folder, initial);
        Assert.Equal(CreateOutcome.Created, created.Outcome);
        created.Store!.Dispose();
        return folder;
    }

    public HullStore Hulls => Get<HullStore>("_hulls");

    public RockField Rocks => Get<RockField>("_rocks");

    public HullField Field => Get<HullField>("_field");

    public SavedOwner[] Owners => Get<SavedOwner[]>("_owners");

    public long WorldTick => Server.Status.Tick;

    public T Get<T>(string field) => (T)typeof(ServerHost).GetField(field, Private)!.GetValue(Server)!;

    /// <summary>Runs callbacks, the clock moving one tick's time before each. The scheduler's count starts at zero, as a real loop's does.</summary>
    public void Tick(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Clock.Advance(ManualTime.Tick);
            ((ITickHandler)Server).Tick(_callback++);
        }
    }

    /// <summary>The host's client joins and is let in.</summary>
    public void Join()
    {
        var before = Link.Welcomes;
        Link.Join();
        Tick();
        Assert.Equal(before + 1, Link.Welcomes);
    }

    /// <summary>The host's client leaves; the server hears of it in the next callback.</summary>
    public void Leave()
    {
        Link.HangUp();
        Tick();
        Assert.Equal(0, Server.Status.PlayerCount);
    }

    public void WaitIdle() => TestNet.WaitUntil(() => Server.Save.State is SaveState.Idle or SaveState.Halted, "the save worker to give the buffer back");

    /// <summary>Lets the autosave time pass and runs one callback, which starts a save if the world has changed.</summary>
    public void Autosave(bool wait = true)
    {
        Clock.AdvanceSeconds(31);
        Tick();
        if (wait)
            WaitIdle();
    }

    /// <summary>Asks for a close and ticks until it is over, as whoever owns a server that is not started must.</summary>
    public CloseStatus Close()
    {
        Server.BeginClose();
        for (var i = 0; i < 20000 && !Server.Close.Completed; i++)
        {
            Tick();
            Thread.Sleep(1);
        }

        Assert.True(Server.Close.Completed);
        return Server.Close;
    }

    /// <summary>The connection the server holds for the host's client, and a private method called with it.</summary>
    public void WithConnection(string method)
    {
        foreach (var connection in (Array)typeof(ServerHost).GetField("_connections", Private)!.GetValue(Server)!)
        {
            if (connection!.GetType().GetField("State")!.GetValue(connection)!.ToString() != "Active")
                continue;
            typeof(ServerHost).GetMethod(method, Private)!.Invoke(Server, [connection]);
            return;
        }

        Assert.Fail("Nobody is let in.");
    }

    public void Dispose() => Server.Dispose();

    /// <summary>A sector near home that has asteroids, for pushing one.</summary>
    public static SectorCoord RockySector(RockField rocks)
    {
        for (var x = -6; x <= 6; x++)
            for (var z = -6; z <= 6; z++)
                if (rocks.Sector(new SectorCoord(x, 0, z)).AsteroidCount > 4) return new SectorCoord(x, 0, z);
        throw new InvalidOperationException("No asteroids near home.");
    }

    /// <summary>
    /// The shipped content with one more design, which no file has, and a starting fleet that includes it: a
    /// world created from it has a catalogue and roles that the files alone cannot give back. The design is the
    /// mining vessel with one block of plain hull eight times the size of a cell beside it, so its blocks'
    /// hit points differ by a large factor.
    /// </summary>
    public static GameContent WithCustomDesign(GameContent content)
    {
        var mining = content.Designs[1];
        var boxes = mining.Boxes.ToArray();
        var far = boxes[0];
        foreach (var box in boxes)
            if (box.At.X + box.Size.X > far.At.X + far.Size.X) far = box;
        var big = new DesignBox(new GridSize(far.At.X + far.Size.X, far.At.Y, far.At.Z), new GridSize(2, 2, 2), content.Blocks.Get("hull"));
        var bytes = DesignBytes.Encode(HullDesign.Of(new BoxDesign("Custom Hauler", mining.BlockSize, [.. boxes, big], content.Blocks.Limits, "test", mining.Class, mining.Color), content.Rules));
        var custom = DesignBytes.Decode(bytes, System.Security.Cryptography.SHA256.HashData(bytes), content);
        return content.WithCatalogue([.. content.Designs, custom], content.PodDesign, content.DroneDesign, [1, 2, content.Designs.Length, 1, 2]);
    }

    /// <summary>A block of a design that is not a core, other than <paramref name="except"/>.</summary>
    public static int PlainBlock(HullDesign design, int except = -1, int skip = 0)
    {
        for (var block = 0; block < design.BlockCount; block++)
        {
            if (block != except && !BlockSet.Has(design.CoreSet, block) && skip-- == 0)
                return block;
        }

        throw new InvalidOperationException("The design has no such block.");
    }

    public static Vector3 Forward => -Vector3.UnitZ;
}
