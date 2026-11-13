using Prospect.Game.Persistence;
using Prospect.Game.Server;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

/// <summary>Trusted local claims settle only against their exact durable operation.</summary>
public sealed class OwnerClaimTests
{
    private static readonly GameContent Content = GameContent.Load();
    private static readonly SaveLimits Limits = new() { MaxOwners = 4, MaxHulls = 32, MaxBlocks = 4096, MaxRocks = 16, MaxMovedSectors = 2 };
    private static readonly SavedAccount Account = new("https://accounts.example/", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"u8));

    [Theory]
    [InlineData(Prospect.Game.Protocol.ServerVisibility.Unlisted)]
    [InlineData(Prospect.Game.Protocol.ServerVisibility.Public)]
    public void HostedWorldRefusesLocalClaim(Prospect.Game.Protocol.ServerVisibility visibility)
    {
        using var rig = new SaveRig(SaveRig.NewWorld(Content), Content, limits: Limits,
            config: new ServerConfig { Visibility = visibility });
        rig.Join();
        Assert.Equal(ServerHost.ClaimState.Refused, rig.Server.StageVerifiedLocalClaim(Account));
        Assert.Equal(default, rig.Owners[0].Account);
        Assert.Equal(0, rig.Server.Claim.OperationId);
    }

    [Fact]
    public void LinkedLocalOwnerRoundTripsAndReconnectsWithoutAnotherFleet()
    {
        var folder = SaveRig.NewWorld(Content);
        int hulls;
        ulong controlled;
        using (var rig = new SaveRig(folder, Content, limits: Limits))
        {
            rig.Join();
            hulls = rig.Hulls.Count;
            controlled = rig.Owners[0].Controlled;
            Assert.Equal(ServerHost.ClaimState.Pending, rig.Server.StageVerifiedLocalClaim(Account));
            rig.Tick();
            rig.WaitIdle();
            rig.Tick();
            Assert.Equal(ServerHost.ClaimState.Committed, rig.Server.Claim.State);
            Assert.Equal(ServerHost.ClaimState.AlreadyLinked, rig.Server.StageVerifiedLocalClaim(Account));
            rig.Leave();
            rig.Join();
            Assert.Equal(hulls, rig.Hulls.Count);
            Assert.Equal(controlled, rig.Owners[0].Controlled);
            Assert.True(rig.Close().Clean);
        }
        Assert.Equal(Account, folder.Decode().Owners[0].Account);
        using var reopened = new SaveRig(folder, Content, limits: Limits);
        reopened.Join();
        Assert.Equal(OwnerKind.Local, reopened.Owners[0].Kind);
        Assert.Equal(controlled, reopened.Owners[0].Controlled);
        Assert.Equal(hulls, reopened.Hulls.Count);
    }

    [Fact]
    public void OldFailedSaveCannotSettleQueuedClaimAndSecondClaimIsRefused()
    {
        var folder = SaveRig.NewWorld(Content);
        using var rig = new SaveRig(folder, Content, limits: Limits);
        using var gate = new ManualResetEventSlim(false);
        rig.Join();
        folder.Gate = gate;
        folder.FailCommits = 1;
        rig.Autosave(false);
        Assert.True(folder.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            Assert.Equal(ServerHost.ClaimState.Pending, rig.Server.StageVerifiedLocalClaim(Account));
            Assert.Equal(ServerHost.ClaimState.Refused, rig.Server.StageVerifiedLocalClaim(Account with { Id = Guid.NewGuid() }));
            rig.Tick();
            Assert.Equal(default, rig.Owners[0].Account);
            Assert.Equal(0, rig.Server.Claim.OperationId);
        }
        finally { gate.Set(); }
        rig.WaitIdle();
        gate.Reset();
        folder.Entered.Reset();
        rig.Tick();
        Assert.True(folder.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            rig.Tick();
            Assert.Equal(ServerHost.ClaimState.Pending, rig.Server.Claim.State);
            Assert.NotEqual(0, rig.Server.Claim.OperationId);
            Assert.Equal(default, rig.Owners[0].Account);
        }
        finally { gate.Set(); }
        rig.WaitIdle();
        rig.Tick();
        Assert.Equal(Account, rig.Owners[0].Account);
    }

    [Theory]
    [InlineData(CommitOutcome.Failed, (int)ServerHost.ClaimState.Failed)]
    [InlineData(CommitOutcome.Conflict, (int)ServerHost.ClaimState.Conflict)]
    [InlineData(CommitOutcome.Rejected, (int)ServerHost.ClaimState.Rejected)]
    [InlineData(CommitOutcome.Unknown, (int)ServerHost.ClaimState.Unknown)]
    public void ExactFailureControlsRetryAndFinalCapture(CommitOutcome outcome, int expected)
    {
        var folder = SaveRig.NewWorld(Content);
        using var rig = new SaveRig(folder, Content, limits: Limits);
        rig.Join();
        folder.FailCommits = 1;
        folder.FailWith = outcome;
        Assert.Equal(ServerHost.ClaimState.Pending, rig.Server.StageVerifiedLocalClaim(Account));
        rig.Tick();
        TestNet.WaitUntil(() => rig.Get<SaveWorker>("_save").Latest.Outcome == outcome, "claim result");
        rig.Tick();
        Assert.Equal((ServerHost.ClaimState)expected, rig.Server.Claim.State);
        Assert.Equal(default, rig.Owners[0].Account);
        if (outcome == CommitOutcome.Failed)
        {
            Assert.Equal(ServerHost.ClaimState.Pending, rig.Server.StageVerifiedLocalClaim(Account));
            rig.Tick();
            rig.WaitIdle();
            rig.Tick();
            Assert.Equal(Account, rig.Owners[0].Account);
            Assert.True(rig.Close().Clean);
        }
        else
        {
            var attempts = folder.Attempts;
            Assert.Equal(ServerHost.ClaimState.Refused, rig.Server.StageVerifiedLocalClaim(Account));
            Assert.Equal(outcome == CommitOutcome.Unknown ? FinalCommitOutcome.Unknown : FinalCommitOutcome.Failed, rig.Close().FinalCommit);
            Assert.Equal(attempts, folder.Attempts);
        }
    }

    [Fact]
    public void CommitIsVisibleBeforeBackupButBufferIsStillOwnedAndCloseWaits()
    {
        var folder = SaveRig.NewWorld(Content);
        using var backupGate = new ManualResetEventSlim(false);
        StoreProbe? probe = null;
        using var rig = new SaveRig(folder, Content, limits: Limits, wrapStore: store => probe = new StoreProbe(store) { BackupGate = backupGate });
        rig.Join();
        rig.Server.StageVerifiedLocalClaim(Account);
        rig.Tick();
        try
        {
            Assert.True(probe!.BackupEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var worker = rig.Get<SaveWorker>("_save");
            Assert.Equal(CommitOutcome.Committed, worker.Latest.Outcome);
            Assert.Equal(SaveWorker.Owner.Worker, worker.BufferOwner);
            Assert.False(worker.TryBeginCapture(out _));
            Assert.Equal(default, rig.Owners[0].Account);
            rig.Server.BeginClose();
            rig.Tick();
            Assert.Equal(Account, rig.Owners[0].Account);
            Assert.False(rig.Server.Close.Completed);
            Assert.Equal(Account, probe.Captured!.Owners[0].Account);
            Assert.Equal(1, folder.Commits);
        }
        finally { backupGate.Set(); }
        Assert.True(rig.Close().Clean);
    }

    [Fact]
    public void CloseDiscardsUnsubmittedClaim()
    {
        var folder = SaveRig.NewWorld(Content);
        using var rig = new SaveRig(folder, Content, limits: Limits);
        rig.Join();
        rig.Server.StageVerifiedLocalClaim(Account);
        Assert.True(rig.Close().Clean);
        Assert.Equal(ServerHost.ClaimState.Cancelled, rig.Server.Claim.State);
        Assert.Equal(default, folder.Decode().Owners[0].Account);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownAfterCommitNeverOverwritesCandidateAndReopenUsesIt(bool throws)
    {
        var folder = SaveRig.NewWorld(Content);
        using (var rig = new SaveRig(folder, Content, limits: Limits, wrapStore: store => new StoreProbe(store) { UnknownAfterCommit = true, ThrowAfterCommit = throws }))
        {
            rig.Join();
            rig.Server.StageVerifiedLocalClaim(Account);
            rig.Tick();
            TestNet.WaitUntil(() => rig.Get<SaveWorker>("_save").Latest.Unknown, "unknown result");
            rig.Tick();
            Assert.Equal(default, rig.Owners[0].Account);
            Assert.Equal(FinalCommitOutcome.Unknown, rig.Close().FinalCommit);
            Assert.Equal(1, folder.Attempts);
        }
        using var reopened = new SaveRig(folder, Content, limits: Limits);
        reopened.Join();
        Assert.Equal(Account, reopened.Owners[0].Account);
        Assert.Equal(ServerHost.ClaimState.AlreadyLinked, reopened.Server.StageVerifiedLocalClaim(Account));
    }

    [Fact]
    public void TickFaultNeverCapturesPendingCandidate()
    {
        var folder = SaveRig.NewWorld(Content);
        using var rig = new SaveRig(folder, Content, limits: Limits);
        rig.Join();
        rig.Server.StageVerifiedLocalClaim(Account);
        rig.Link.Fault = true;
        Assert.Throws<InvalidOperationException>(() => rig.Tick());
        Assert.Equal(FinalCommitOutcome.SkippedAfterFault, rig.Close().FinalCommit);
        Assert.Equal(0, folder.Attempts);
    }

    [Fact]
    public void OwnerValidationRejectsDuplicateAccountsAndLocalsAcrossKinds()
    {
        SavedOwner[] owners = [new() { Id = 1, Kind = OwnerKind.Local, Account = Account }, new() { Id = 2, Kind = OwnerKind.Account, Account = Account }];
        Assert.False(SavedOwner.Valid(owners, 3));
        owners[1].Account = Account with { Id = Guid.NewGuid() };
        Assert.True(SavedOwner.Valid(owners, 3));
        owners[1].Kind = OwnerKind.Local;
        Assert.False(SavedOwner.Valid(owners, 3));
        owners[1].Kind = OwnerKind.Account;
        owners[1].Account = default;
        Assert.False(SavedOwner.Valid(owners, 3));
        Assert.False((Account with { Authority = "https://accounts.example/path" }).IsValid);
    }

    [Fact]
    public void CloseWaitsForSubmittedClaimBeforeFinalCapture()
    {
        var folder = SaveRig.NewWorld(Content);
        using var rig = new SaveRig(folder, Content, limits: Limits);
        using var gate = new ManualResetEventSlim(false);
        rig.Join();
        folder.Gate = gate;
        rig.Server.StageVerifiedLocalClaim(Account);
        rig.Tick();
        try
        {
            Assert.True(folder.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            rig.Server.BeginClose();
            rig.Tick(3);
            Assert.False(rig.Server.Close.Completed);
            Assert.Equal(default, rig.Owners[0].Account);
            Assert.Equal(1, folder.Attempts);
        }
        finally { gate.Set(); }
        Assert.True(rig.Close().Clean);
        Assert.Equal(Account, folder.Decode().Owners[0].Account);
        Assert.Equal(2, folder.Attempts);
    }

    [Fact]
    public void CodecRoundTripsAccountOwnersAndRefusesOldLayout()
    {
        var folder = SaveRig.NewWorld(Content);
        using (var rig = new SaveRig(folder, Content, limits: Limits))
        {
            rig.Join();
            rig.Server.StageVerifiedLocalClaim(Account);
            rig.Tick();
            rig.WaitIdle();
            Assert.True(rig.Close().Clean);
        }
        var snapshot = folder.Decode();
        var original = WorldSnapshotCodec.Encode(snapshot);
        // The owner follows the header and catalogue and precedes every hull record.
        var ownerOffset = original.Length - WorldSnapshotCodec.OwnerBytes;
        for (var i = 0; i < snapshot.HullCount; i++)
            ownerOffset -= WorldSnapshotCodec.HullFixedBytes + snapshot.Hulls[i].Visual.Count * WorldSnapshotCodec.DentBytes;
        ownerOffset -= snapshot.BlockCount * 4 + snapshot.LossCount * 4 + snapshot.RockCount * WorldSnapshotCodec.RockBytes;
        var duplicate = new byte[original.Length + WorldSnapshotCodec.OwnerBytes];
        original.AsSpan(0, ownerOffset + WorldSnapshotCodec.OwnerBytes).CopyTo(duplicate);
        original.AsSpan(ownerOffset).CopyTo(duplicate.AsSpan(ownerOffset + WorldSnapshotCodec.OwnerBytes));
        var countsOffset = ownerOffset;
        for (var i = 0; i < snapshot.DesignCount; i++)
            countsOffset -= 4 + 32 + snapshot.Designs[i].Length;
        countsOffset -= 20;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(duplicate.AsSpan(countsOffset), 2);
        var nextOwnerOffset = countsOffset - 4 - snapshot.StartingFleet.Length - 8 - 4;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(nextOwnerOffset), 3);
        var second = ownerOffset + WorldSnapshotCodec.OwnerBytes;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(second), 2);
        duplicate[second + 4] = (byte)OwnerKind.Account;
        Assert.False(WorldSnapshotCodec.TryDecode(duplicate, SaveLimits.Default, out _, out _));
        Guid.NewGuid().TryWriteBytes(duplicate.AsSpan(second + 13, 16));
        Assert.True(WorldSnapshotCodec.TryDecode(duplicate, SaveLimits.Default, out _, out var uniqueError), uniqueError);
        duplicate[second + 4] = (byte)OwnerKind.Local;
        Assert.False(WorldSnapshotCodec.TryDecode(duplicate, SaveLimits.Default, out _, out _));
        var bad = (byte[])original.Clone();
        bad[ownerOffset + 4] = (byte)OwnerKind.Account;
        Assert.True(WorldSnapshotCodec.TryDecode(bad, SaveLimits.Default, out var accountOwner, out var error), error);
        Assert.Equal(Account, accountOwner!.Owners[0].Account);
        bad[4] = 1;
        Assert.False(WorldSnapshotCodec.TryDecode(bad, SaveLimits.Default, out _, out _));
        Assert.Equal(SaveCompatibility.Of(Content).GeneratorVersion, snapshot.Compatibility.GeneratorVersion);
    }

    private sealed class StoreProbe(IWorldStore inner) : IWorldStore
    {
        public ManualResetEventSlim? BackupGate;
        public readonly ManualResetEventSlim BackupEntered = new();
        public bool UnknownAfterCommit;
        public bool ThrowAfterCommit;
        public WorldSnapshot? Captured;

        public CommitResult Commit(WorldSnapshot snapshot, long expectedGeneration, bool final)
        {
            Captured = snapshot;
            var result = inner.Commit(snapshot, expectedGeneration, final);
            if (ThrowAfterCommit)
                throw new IOException("Commit response was lost.");
            return UnknownAfterCommit && result.Outcome == CommitOutcome.Committed ? result with { Outcome = CommitOutcome.Unknown } : result;
        }

        public BackupResult Backup(BackupReason reason)
        {
            BackupEntered.Set();
            BackupGate?.Wait();
            return inner.Backup(reason);
        }

        public void Dispose()
        {
            inner.Dispose();
            BackupEntered.Dispose();
        }
    }
}
