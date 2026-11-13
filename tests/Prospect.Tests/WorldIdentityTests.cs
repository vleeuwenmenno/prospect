using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Prospect.Game.Persistence;
using Prospect.Game.World;
using Xunit;

namespace Prospect.Tests;

public sealed class WorldIdentityTests : IDisposable
{
    private static readonly GameContent Content = GameContent.Load();
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "identity-tests", Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, "world");
    private string Key => Path.Combine(Folder, WorldIdentity.KeyFile);
    private static WorldSnapshot Initial() => WorldSnapshot.NewWorld(Content, Guid.NewGuid(), "Identity", DateTimeOffset.UtcNow, 42, Content.SettingCatalog.Defaults, default);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private SqliteWorldStore Create()
    {
        var result = SqliteWorldStore.Create(Folder, Initial());
        Assert.True(result.Outcome == CreateOutcome.Created, result.Error);
        return (SqliteWorldStore)result.Store!;
    }

    private SqliteWorldStore Open(string? folder = null)
    {
        var result = SqliteWorldStore.Open(folder ?? Folder, SaveCompatibility.Of(Content));
        Assert.True(result.Outcome == OpenOutcome.Opened, result.Error);
        Assert.NotNull(result.Snapshot);
        return (SqliteWorldStore)result.Store!;
    }

    [Fact]
    public void PrivateCreationAndReopenWorkOnEveryPlatform()
    {
        WorldIdentity identity;
        using (var store = Create()) identity = store.Identity;
        using var opened = Open();
        Assert.Equal(identity, opened.Identity);
        if (!OperatingSystem.IsLinux())
        {
            Assert.Equal(WorldKeyStatus.Unsafe, identity.KeyStatus);
            Assert.False(File.Exists(Key));
        }
    }

    [Fact]
    public void SharedParentAllowsPrivateCreationWithoutExposingAKey()
    {
        if (!OperatingSystem.IsLinux()) return;
        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        WorldIdentity identity;
        using (var store = Create()) identity = store.Identity;
        Assert.Equal(WorldKeyStatus.Unsafe, identity.KeyStatus);
        Assert.False(File.Exists(Key));
        using (var reopened = Open()) Assert.Equal(identity, reopened.Identity);
        // Tightening permissions later must not silently mint a new signing identity.
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var protectedPath = Open();
        Assert.Equal(WorldKeyStatus.Missing, protectedPath.Identity.KeyStatus);
        Assert.Equal(identity.PublicKey, protectedPath.Identity.PublicKey);
        Assert.False(File.Exists(Key));
    }

    [Fact]
    public void SymlinkedParentRetainsProtectedIdentity()
    {
        if (!OperatingSystem.IsLinux()) return;
        DurableFiles.CreateDirectory(Path.Combine(_root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "real"));
        var folder = Path.Combine(_root, "link", "world");
        var created = SqliteWorldStore.Create(folder, Initial());
        Assert.Equal(CreateOutcome.Created, created.Outcome);
        WorldIdentity identity;
        using (var store = (SqliteWorldStore)created.Store!) identity = store.Identity;
        Assert.Equal(WorldKeyStatus.Present, identity.KeyStatus);
        using var opened = Open(Path.Combine(_root, "real", "world"));
        Assert.Equal(identity, opened.Identity);
    }

    [Fact]
    public void SymlinkCannotHideASharedWritableTargetAncestor()
    {
        if (!OperatingSystem.IsLinux()) return;
        var shared = Path.Combine(_root, "shared");
        var target = Path.Combine(shared, "target");
        DurableFiles.CreateDirectory(target);
        File.SetUnixFileMode(shared, File.GetUnixFileMode(shared) | UnixFileMode.GroupWrite);
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), target);
        Assert.False(WorldIdentity.SafeDirectory(Path.Combine(_root, "link")));
    }

    [Fact]
    public void FailedPrivateOnlyCreationCleansItsDatabaseAndCanRetry()
    {
        if (!OperatingSystem.IsLinux()) return;
        DurableFiles.CreateDirectory(Folder);
        File.SetUnixFileMode(Folder, File.GetUnixFileMode(Folder) | UnixFileMode.GroupWrite);
        try
        {
            DurableFiles.Trace = (step, path) =>
            {
                if (step == "sync-file" && path == Path.Combine(Folder, "world.db.creating")) throw new IOException("injected");
            };
            Assert.Equal(CreateOutcome.Failed, SqliteWorldStore.Create(Folder, Initial()).Outcome);
        }
        finally { DurableFiles.Trace = null; }
        Assert.False(File.Exists(Key));
        Assert.False(File.Exists(Path.Combine(Folder, "world.db.creating")));
        using var created = Create();
        Assert.Equal(WorldKeyStatus.Unsafe, created.Identity.KeyStatus);
    }

    [Fact]
    public void NewDirectoriesAreOwnerOnlyRegardlessOfUmask()
    {
        if (OperatingSystem.IsWindows()) return;
        DurableFiles.CreateDirectory(Folder);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_root));
    }

    [Fact]
    public void IdentityIsStableProtectedAndMovesWithStoppedFolder()
    {
        if (!OperatingSystem.IsLinux()) return;
        WorldIdentity identity;
        using (var store = Create())
        {
            identity = store.Identity;
            Assert.Equal(WorldKeyStatus.Present, identity.KeyStatus);
            Assert.Equal(91, Convert.FromBase64String(identity.PublicKey).Length);
            Assert.Equal(64, identity.Fingerprint.Length);
            Assert.Equal(WorldIdentity.SecretMode, File.GetUnixFileMode(Key));
            Assert.InRange(new FileInfo(Key).Length, 1, 4096);
            Assert.Equal(OpenOutcome.Busy, SqliteWorldStore.Open(Folder, SaveCompatibility.Of(Content)).Outcome);
            Assert.Equal(BackupOutcome.Completed, store.Backup(BackupReason.Initial).Outcome);
        }
        using (var store = Open()) Assert.Equal(identity, store.Identity);
        var moved = Path.Combine(_root, "moved");
        DurableFiles.CreateDirectory(moved);
        foreach (var file in Directory.GetFiles(Folder)) File.Copy(file, Path.Combine(moved, Path.GetFileName(file)));
        File.SetUnixFileMode(Path.Combine(moved, WorldIdentity.KeyFile), WorldIdentity.SecretMode);
        using (var store = Open(moved)) Assert.Equal(identity, store.Identity);
        var recovered = Path.Combine(_root, "gameplay-only");
        DurableFiles.CreateDirectory(recovered);
        File.Copy(Directory.GetFiles(Path.Combine(Folder, "backups"), "*.db").Single(), Path.Combine(recovered, "world.db"));
        using var backup = Open(recovered);
        Assert.Equal(identity.Fingerprint, backup.Identity.Fingerprint);
        Assert.Equal(WorldKeyStatus.Missing, backup.Identity.KeyStatus);
        Assert.False(File.Exists(Path.Combine(recovered, WorldIdentity.KeyFile)));
    }

    [Theory]
    [InlineData("missing", WorldKeyStatus.Missing)]
    [InlineData("empty", WorldKeyStatus.Unreadable)]
    [InlineData("oversize", WorldKeyStatus.Unreadable)]
    [InlineData("multiple", WorldKeyStatus.Unreadable)]
    [InlineData("junk", WorldKeyStatus.Unreadable)]
    [InlineData("der-junk", WorldKeyStatus.Unreadable)]
    [InlineData("unsafe-parent", WorldKeyStatus.Unsafe)]
    [InlineData("rsa", WorldKeyStatus.Unreadable)]
    [InlineData("p384", WorldKeyStatus.Unreadable)]
    [InlineData("wrong", WorldKeyStatus.Mismatch)]
    [InlineData("unsafe", WorldKeyStatus.Unsafe)]
    public void BrokenKeysRemainUntouchedAndPrivateWorldStillOpens(string kind, WorldKeyStatus expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        using (Create()) { }
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        var original = File.ReadAllText(Key);
        switch (kind)
        {
            case "missing": File.Delete(Key); break;
            case "empty": File.WriteAllText(Key, ""); break;
            case "oversize": File.WriteAllText(Key, new string('x', 4097)); break;
            case "multiple": File.WriteAllText(Key, original + original); break;
            case "junk": File.WriteAllText(Key, original + "junk"); break;
            case "der-junk":
                File.WriteAllText(Key, PemEncoding.WriteString("PRIVATE KEY", [.. wrong.ExportPkcs8PrivateKey(), 0]));
                break;
            case "unsafe-parent":
                File.SetUnixFileMode(Folder, File.GetUnixFileMode(Folder) | UnixFileMode.OtherWrite);
                break;
            case "rsa": File.WriteAllText(Key, rsa.ExportPkcs8PrivateKeyPem()); break;
            case "p384": File.WriteAllText(Key, p384.ExportPkcs8PrivateKeyPem()); break;
            case "wrong": File.WriteAllText(Key, wrong.ExportPkcs8PrivateKeyPem()); break;
            case "unsafe": File.SetUnixFileMode(Key, WorldIdentity.SecretMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead); break;
        }
        var before = File.Exists(Key) ? File.ReadAllBytes(Key) : null;
        using (var store = Open()) Assert.Equal(expected, store.Identity.KeyStatus);
        Assert.Equal(before, File.Exists(Key) ? File.ReadAllBytes(Key) : null);
    }

    [Fact]
    public void FailedKeyPublicationReleasesLeaseAndRetryDoesNotReplaceUnknownFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var initial = Initial();
        try
        {
            DurableFiles.Trace = (step, path) =>
            {
                if (step == "sync-directory" && path == Folder && File.Exists(Key)) throw new IOException("injected");
            };
            Assert.Equal(CreateOutcome.Failed, SqliteWorldStore.Create(Folder, initial).Outcome);
            Assert.False(File.Exists(Path.Combine(Folder, "world.db")));
            Assert.True(File.Exists(Key));
        }
        finally { DurableFiles.Trace = null; }
        var bytes = File.ReadAllBytes(Key);
        for (var i = 0; i < 2; i++)
        {
            var retry = SqliteWorldStore.Create(Folder, initial);
            Assert.Equal(CreateOutcome.Failed, retry.Outcome);
            Assert.Equal("An existing identity file prevents creation; nothing was replaced.", retry.Error);
            Assert.Equal(bytes, File.ReadAllBytes(Key));
            Assert.False(File.Exists(Path.Combine(Folder, "world.db")));
            using var lease = new FileStream(Path.Combine(Folder, SqliteWorldStore.LockFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    [Fact]
    public void KeyPublicationCollisionNeverDeletesTheOtherWritersKey()
    {
        if (!OperatingSystem.IsLinux()) return;
        byte[] bytes = [1, 2, 3, 4];
        try
        {
            DurableFiles.Trace = (step, path) =>
            {
                if (step == "sync-file" && path == Path.Combine(Folder, "world.db.creating"))
                    File.WriteAllBytes(Key, bytes);
            };
            Assert.Equal(CreateOutcome.Failed, SqliteWorldStore.Create(Folder, Initial()).Outcome);
        }
        finally { DurableFiles.Trace = null; }
        Assert.Equal(bytes, File.ReadAllBytes(Key));
        Assert.False(File.Exists(Path.Combine(Folder, "world.db")));
        Assert.False(File.Exists(Key + ".creating"));
        using var lease = new FileStream(Path.Combine(Folder, SqliteWorldStore.LockFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void UnknownKeyStagingIsNeverDeletedAndFailureLeavesLeaseFree()
    {
        if (!OperatingSystem.IsLinux()) return;
        DurableFiles.CreateDirectory(Folder);
        var staging = Key + ".creating";
        File.WriteAllText(staging, "not ours");
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(CreateOutcome.Failed, SqliteWorldStore.Create(Folder, Initial()).Outcome);
            Assert.Equal("not ours", File.ReadAllText(staging));
        }
        File.Delete(staging);
        using var created = Create();
        Assert.Equal(WorldKeyStatus.Present, created.Identity.KeyStatus);
    }

    [Fact]
    public void PublishedDatabaseSurvivesFailedNameSyncWithItsOriginalKey()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            DurableFiles.Trace = (step, path) =>
            {
                if (step == "sync-directory" && path == Folder && File.Exists(Path.Combine(Folder, "world.db")))
                    throw new IOException("injected after database publication");
            };
            Assert.Equal(CreateOutcome.Failed, SqliteWorldStore.Create(Folder, Initial()).Outcome);
        }
        finally { DurableFiles.Trace = null; }
        var original = File.ReadAllBytes(Key);
        Assert.Equal(CreateOutcome.Exists, SqliteWorldStore.Create(Folder, Initial()).Outcome);
        using var opened = Open();
        Assert.Equal(WorldKeyStatus.Present, opened.Identity.KeyStatus);
        Assert.Equal(original, File.ReadAllBytes(Key));
    }

    [Fact]
    public void EarlierDatabaseLayoutIsRefusedWithoutTouchingDatabaseOrKey()
    {
        if (!OperatingSystem.IsLinux()) return;
        using (Create()) { }
        var database = Path.Combine(Folder, "world.db");
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=1";
            command.ExecuteNonQuery();
        }
        var bytes = File.ReadAllBytes(database);
        var secret = File.ReadAllBytes(Key);
        Assert.Equal(OpenOutcome.Incompatible, SqliteWorldStore.Open(Folder, SaveCompatibility.Of(Content)).Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(database));
        Assert.Equal(secret, File.ReadAllBytes(Key));
    }
}
