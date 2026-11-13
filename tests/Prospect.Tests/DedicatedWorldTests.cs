using System.Diagnostics;
using System.Text;
using Prospect.Game.Persistence;
using Prospect.Game.Server;
using Prospect.Game.World;
using Prospect.Server;
using Xunit;

namespace Prospect.Tests;

/// <summary>
/// The dedicated server's saved world: what its command line opens, creates and refuses, on a real disk, and
/// what it says when it stops. The last tests run the server's own binary as a process.
/// </summary>
public sealed class DedicatedWorldTests : IDisposable
{
    private static readonly GameContent Shipped = GameContent.Load();
    private static readonly SaveCompatibility Compatible = SaveCompatibility.Of(Shipped);
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "save-tests", "dedicated-" + Guid.NewGuid().ToString("N"));

    public DedicatedWorldTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A test's leftovers are not worth failing it for.
        }
    }

    private string Folder(string name = "world") => Path.Combine(_root, name);

    /// <summary>Builds the configuration of a command line; the world it opened is the caller's to give back.</summary>
    private static ServerConfig Build(out ServerCommand command, params string[] args)
    {
        command = new ServerCommand();
        var result = command.Parse(["--listing", "off", .. args], TextWriter.Null, TextWriter.Null, out var code);
        Assert.Equal(0, code);
        Assert.NotNull(result);
        return command.BuildConfig(result, Shipped, null, _ => { });
    }

    /// <summary>Closes a world as a server that stopped cleanly does, and gives its store back.</summary>
    private static void CloseCleanly(ServerConfig config)
    {
        var world = config.World!;
        Assert.Equal(CommitOutcome.Committed, world.Store.Commit(world.World, world.Generation, final: true).Outcome);
        world.Store.Dispose();
    }

    [Fact]
    public void WithoutAWorldTheServerIsAsItWasAndTheWorldOptionsNeedAWorld()
    {
        var plain = Build(out var command, "--seed", "7");
        Assert.Null(plain.World);
        Assert.Null(command.WorldFolder);
        Assert.Equal(7ul, plain.GalaxySeed);
        Assert.NotNull(plain.Settings);

        Assert.Contains("--world", Assert.Throws<InvalidDataException>(() => Build(out _, "--create-world")).Message);
        Assert.Contains("--world", Assert.Throws<InvalidDataException>(() => Build(out _, "--autosave", "10")).Message);
    }

    [Theory]
    [InlineData("--autosave", "0")]
    [InlineData("--autosave", "3601")]
    [InlineData("--autosave", "soon")]
    [InlineData("--world")]
    public void BadWorldArgumentsAreRefusedByTheParser(params string[] args)
    {
        using var error = new StringWriter();
        Assert.Null(new ServerCommand().Parse(args, TextWriter.Null, error, out var code));
        Assert.Equal(2, code);
    }

    [Fact]
    public void TheHelpSaysWhatASavedWorldIsNotYet()
    {
        using var output = new StringWriter();
        Assert.Null(new ServerCommand().Parse(["--help"], output, TextWriter.Null, out var code));
        Assert.Equal(0, code);
        Assert.Contains("--world", output.ToString());
        Assert.Contains("--create-world", output.ToString());
        Assert.Contains("--autosave", output.ToString());
        Assert.Contains("Not joinable yet", output.ToString());
    }

    [Fact]
    public void AMissingWorldIsNeverCreatedUnlessTheCommandLineSaysSo()
    {
        var folder = Folder();
        var refused = Assert.Throws<InvalidDataException>(() => Build(out _, "--world", folder));
        Assert.Contains("--create-world", refused.Message);
        Assert.Contains(folder, refused.Message);

        // Nothing was made: not the world, not its folder.
        Assert.False(Directory.Exists(folder));
        Assert.Equal(OpenOutcome.Missing, SqliteWorldStore.Summarize(folder).Outcome);
    }

    [Fact]
    public void CreatingMakesTheWorldItWasAskedForAndOpeningBringsItBack()
    {
        var folder = Folder();
        var catalog = Shipped.SettingCatalog;
        var setting = catalog.Definitions.ToArray().First(definition => definition.Kind == SettingKind.Multiplier && definition.Default != definition.Maximum);
        var value = setting.Format(setting.Maximum);
        var assignment = $"{setting.Id}={setting.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        var created = Build(out var command, "--world", folder, "--create-world", "--name", "Ceres Deep", "--seed", "99", "--set", assignment, "--autosave", "45");
        Assert.NotNull(created.World);
        Assert.True(command.WorldCreated);
        Assert.Equal(Path.GetFullPath(folder), command.WorldFolder);
        Assert.Equal((45, 1L), (created.World.AutosaveSeconds, created.World.Generation));
        Assert.Equal(("Ceres Deep", 99ul), (created.World.World.Name, created.World.World.Seed));

        // The world's own seed and settings are the server's: the configuration carries none of its own.
        Assert.Null(created.Settings);
        Assert.Equal(new ServerConfig().GalaxySeed, created.GalaxySeed);

        // Held: a second server, in this process or any other, is told so and leaves it alone.
        Assert.Contains("in use", Assert.Throws<IOException>(() => Build(out _, "--world", folder)).Message);
        Assert.Contains("in use", Assert.Throws<IOException>(() => Build(out _, "--world", folder, "--create-world")).Message);
        CloseCleanly(created);

        // Opened by its folder alone, with the default autosave; and with --create-world it is opened, not made again.
        var opened = Build(out command, "--world", folder);
        Assert.False(command.WorldCreated);
        Assert.True(command.WorldWasCleanlyClosed);
        Assert.Equal((ServerCommand.DefaultAutosaveSeconds, 2L, 99ul), (opened.World!.AutosaveSeconds, opened.World.Generation, opened.World.World.Seed));
        Assert.Equal(created.World.World.WorldId, opened.World.World.WorldId);
        opened.World.Store.Dispose();

        var again = Build(out command, "--world", folder, "--create-world", "--seed", "99", "--set", assignment, "--name", "Another name");
        Assert.False(command.WorldCreated);

        // That run was only disposed, and the world says so to the next.
        Assert.False(command.WorldWasCleanlyClosed);
        Assert.Equal((created.World.World.WorldId, "Ceres Deep"), (again.World!.World.WorldId, again.World.World.Name));

        // The server takes the world from the configuration and plays by the world's settings, not the launch's.
        using (var server = new ServerHost(again, Shipped))
        {
            Assert.True(server.IsPersistent);
            Assert.Equal(99ul, server.Galaxy.Seed);
            Assert.Equal(value, setting.Format(server.Content.Settings.Get(setting)));
        }

        Assert.Equal(OpenOutcome.Opened, SqliteWorldStore.Summarize(folder).Outcome);
    }

    [Fact]
    public void ACommandLineThatDisagreesWithTheWorldIsRefusedWithTheWorldUntouched()
    {
        var folder = Folder();
        var catalog = Shipped.SettingCatalog;
        var setting = catalog.Definitions.ToArray().First(definition => definition.Kind == SettingKind.Multiplier && definition.Default != definition.Maximum && definition.Default != definition.Minimum);
        string Set(float value) => $"{setting.Id}={value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        CloseCleanly(Build(out _, "--world", folder, "--create-world", "--seed", "99", "--set", Set(setting.Maximum)));
        var database = File.ReadAllBytes(Path.Combine(folder, SqliteWorldStore.DatabaseFile));

        void Refused(string expected, params string[] args)
        {
            var error = Assert.Throws<InvalidDataException>(() => Build(out _, ["--world", folder, .. args]));
            Assert.Contains(expected, error.Message);
            Assert.Contains("not opened", error.Message);

            // Not opened for writing, not even marked as started: closed cleanly it was, and still is, byte for byte.
            var summary = SqliteWorldStore.Summarize(folder);
            Assert.Equal((OpenOutcome.Opened, 2L, true), (summary.Outcome, summary.Generation, summary.WasCleanClose));
            Assert.Equal(database, File.ReadAllBytes(Path.Combine(folder, SqliteWorldStore.DatabaseFile)));
        }

        Refused("seed 99", "--seed", "5");
        Refused("seed 99", "--create-world", "--seed", "5");
        Refused(setting.Id, "--set", Set(setting.Minimum));

        // The setting's default, named: a wish all the same, and not the world's.
        Refused(setting.Id, "--set", Set(setting.Default));

        // The same in a settings file, where the server's own section is not the world's business.
        var file = Path.Combine(_root, "settings.json");
        File.WriteAllText(file, $$$"""{"{{{setting.Id}}}": {{{setting.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}, "server": {"name": "From a file"}}""");
        Refused(setting.Id, "--settings", file);

        // What agrees is no conflict, whoever says it; and what is not said is no wish.
        File.WriteAllText(file, $$$"""{"{{{setting.Id}}}": {{{setting.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}, "server": {"name": "From a file"}}""");
        var agreed = Build(out _, "--world", folder, "--seed", "99", "--settings", file, "--set", Set(setting.Maximum));
        Assert.Equal("From a file", agreed.Name);
        Assert.Equal(2L, agreed.World!.Generation);
        CloseCleanly(agreed);
        Build(out _, "--world", folder).World!.Store.Dispose();
    }

    [Fact]
    public void AWorldThatIsNotAWorldIsLeftAsItIs()
    {
        var folder = Folder();
        CloseCleanly(Build(out _, "--world", folder, "--create-world"));
        var path = Path.Combine(folder, SqliteWorldStore.DatabaseFile);
        var bytes = File.ReadAllBytes(path);

        File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(8192));
        var damaged = File.ReadAllBytes(path);
        Assert.Contains("left as it is", Assert.Throws<IOException>(() => Build(out _, "--world", folder)).Message);

        // Not replaced by a new one either, whatever the command line allows.
        Assert.Contains("left as it is", Assert.Throws<IOException>(() => Build(out _, "--world", folder, "--create-world")).Message);
        Assert.Equal(damaged, File.ReadAllBytes(path));
        File.WriteAllBytes(path, bytes);
        Build(out _, "--world", folder).World!.Store.Dispose();
    }

    [Fact]
    public void IncompatibleWorldIsRefusedWithoutChangingItsCleanMark()
    {
        var folder = Folder();
        var initial = WorldSnapshot.NewWorld(Shipped, Guid.NewGuid(), "Other build", DateTimeOffset.UtcNow,
            17, Shipped.SettingCatalog.Defaults, default);
        initial.Compatibility = Compatible with { GeneratorVersion = Compatible.GeneratorVersion + 1 };
        var created = SqliteWorldStore.Create(folder, initial);
        Assert.Equal(CreateOutcome.Created, created.Outcome);
        using (var store = created.Store!)
            Assert.Equal(CommitOutcome.Committed, store.Commit(initial, created.Generation, final: true).Outcome);
        var path = Path.Combine(folder, SqliteWorldStore.DatabaseFile);
        var before = File.ReadAllBytes(path);
        Assert.Contains("another build", Assert.Throws<IOException>(() => Build(out _, "--world", folder, "--create-world")).Message);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(SqliteWorldStore.Summarize(folder).WasCleanClose);
    }

    [Fact]
    public void InvalidLaunchValuesLeaveTheCleanWorldUntouched()
    {
        var folder = Folder();
        CloseCleanly(Build(out _, "--world", folder, "--create-world"));
        var path = Path.Combine(folder, SqliteWorldStore.DatabaseFile);
        var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => Build(out _, "--world", folder, "--name", ""));
        Assert.Throws<InvalidDataException>(() => Build(out _, "--world", folder, "--password", "bad\npassword"));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(SqliteWorldStore.Summarize(folder).WasCleanClose);
    }

    [Fact]
    public void FailedServerConstructionReleasesTheOpenedStore()
    {
        var folder = Folder();
        var config = Build(out _, "--world", folder, "--create-world");
        // A conflicting configuration fails inside construction, after ownership of the store transfers.
        Assert.Throws<InvalidDataException>(() => new ServerHost(new ServerConfig
        {
            World = config.World,
            GalaxySeed = config.World!.World.Seed + 1,
        }, Shipped));
        var reopened = SqliteWorldStore.Open(folder, Compatible);
        Assert.Equal(OpenOutcome.Opened, reopened.Outcome);
        reopened.Store!.Dispose();
    }

    [Fact]
    public void WhatAStopCameToIsSaidInItsOwnWordsAndItsOwnExitCode()
    {
        static CloseStatus Close(FinalCommitOutcome final, CheckpointOutcome checkpoint = CheckpointOutcome.Completed, BackupOutcome backup = BackupOutcome.Completed, string? error = null) =>
            new(ClosePhase.Completed, final, checkpoint, backup, error)
            {
                CheckpointError = checkpoint == CheckpointOutcome.Failed ? "the checkpoint was blocked" : null,
                BackupError = backup == BackupOutcome.Failed ? "no room for a copy" : null,
            };

        var wait = TimeSpan.FromSeconds(60);
        Assert.Equal((0, false), Two(ServerShutdown.Describe(false, CloseStatus.Running, wait)));
        Assert.Equal((0, false), Two(ServerShutdown.Describe(true, Close(FinalCommitOutcome.Committed), wait)));

        // Saved, and said to be saved; what failed after it is named, each by itself.
        var folded = ServerShutdown.Describe(true, Close(FinalCommitOutcome.Committed, CheckpointOutcome.Failed), wait);
        Assert.Equal((0, true), Two(folded));
        Assert.Contains("is saved", folded.Message);
        Assert.Contains("the checkpoint was blocked", folded.Message);
        Assert.DoesNotContain("recovery copy", folded.Message);
        var copied = ServerShutdown.Describe(true, Close(FinalCommitOutcome.Committed, backup: BackupOutcome.Failed), wait);
        Assert.Equal((0, true), Two(copied));
        Assert.Contains("no room for a copy", copied.Message);
        Assert.DoesNotContain("folded", copied.Message);
        var both = ServerShutdown.Describe(true, Close(FinalCommitOutcome.Committed, CheckpointOutcome.Failed, BackupOutcome.Failed), wait);
        Assert.Contains("the checkpoint was blocked", both.Message);
        Assert.Contains("no room for a copy", both.Message);

        // Not saved is one thing, not known is another, and neither is called the other.
        var failed = ServerShutdown.Describe(true, Close(FinalCommitOutcome.Failed, error: "the disk is full"), wait);
        Assert.Equal((1, true), Two(failed));
        Assert.Contains("failed: the disk is full", failed.Message);
        Assert.Contains("last completed save", failed.Message);
        var unknown = ServerShutdown.Describe(true, Close(FinalCommitOutcome.Unknown, error: "the disk went away"), wait);
        Assert.Equal((3, true), Two(unknown));
        Assert.Contains("not known", unknown.Message);
        Assert.Contains("the disk went away", unknown.Message);
        Assert.DoesNotContain("failed", unknown.Message);

        Assert.Equal((1, true), Two(ServerShutdown.Describe(true, Close(FinalCommitOutcome.SkippedAfterFault), wait)));
        Assert.Equal((3, true), Two(ServerShutdown.Describe(true, Close(FinalCommitOutcome.Abandoned), wait)));
        Assert.Equal((1, true), Two(ServerShutdown.Describe(true, Close(FinalCommitOutcome.NotRequested), wait)));
        var late = ServerShutdown.Describe(true, new CloseStatus(ClosePhase.Saving, FinalCommitOutcome.Pending, CheckpointOutcome.NotAttempted, BackupOutcome.NotAttempted, null), wait);
        Assert.Equal((3, true), Two(late));
        Assert.Contains("60 s", late.Message);

        static (int, bool) Two((int ExitCode, bool Problem, string Message) described) => (described.ExitCode, described.Problem);
    }

    /// <summary>The server's own binary, as the build put it beside the tests' output.</summary>
    private static string ServerBinary()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var binary = Path.Combine(output.Parent!.Parent!.FullName, "Prospect.Server", output.Name, "prospect-server.dll");
        Assert.True(File.Exists(binary), $"the server was not built at {binary}");
        return binary;
    }

    /// <summary>A server process with what it wrote, line by line, as it writes it.</summary>
    private sealed class Running : IDisposable
    {
        private readonly StringBuilder _text = new();
        public readonly Process Process;

        public Running(params string[] args)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(ServerBinary());
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            start.Environment.Remove("PROSPECT_PASSWORD");
            Process = new Process { StartInfo = start };
            Process.OutputDataReceived += (_, line) => Append(line.Data);
            Process.ErrorDataReceived += (_, line) => Append(line.Data);
            Process.Start();
            Process.BeginOutputReadLine();
            Process.BeginErrorReadLine();
        }

        public string Text
        {
            get { lock (_text) return _text.ToString(); }
        }

        private void Append(string? line)
        {
            if (line is null)
                return;
            lock (_text)
                _text.AppendLine(line);
        }

        /// <summary>Asks the process to stop as an operator or a container runtime does.</summary>
        public void Terminate()
        {
            using var kill = System.Diagnostics.Process.Start("kill", ["-TERM", Process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            Assert.NotNull(kill);
            Assert.True(kill.WaitForExit(5000));
            Assert.Equal(0, kill.ExitCode);
        }

        public int Exit(int seconds)
        {
            Assert.True(Process.WaitForExit(seconds * 1000), $"the server did not exit in {seconds} s:\n{Text}");
            Process.WaitForExit();
            return Process.ExitCode;
        }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(5000);
            }

            Process.Dispose();
        }
    }

    [Fact]
    public void TheServerProcessCreatesAWorldSavesItWhenStoppedAndOpensItAgain()
    {
        // Stopping a process by signal is what this is about; there is no such signal to send on Windows.
        if (OperatingSystem.IsWindows())
            return;
        var folder = Folder();
        var port = TestNet.Port(30).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Nothing there and nothing said about creating: the process says so and leaves, having made nothing.
        using (var missing = new Running("--world", folder, "--port", port, "--listing", "off"))
        {
            Assert.Equal(1, missing.Exit(60));
            Assert.Contains("--create-world", missing.Text);
            Assert.False(Directory.Exists(folder));
        }

        // Created, running, and saying what this mode is not. Stopped by SIGTERM, it saves and leaves in time.
        using (var first = new Running("--world", folder, "--create-world", "--name", "Process World", "--seed", "31", "--port", port, "--listing", "off"))
        {
            TestNet.WaitUntil(() => first.Text.Contains("Press Ctrl+C") || first.Process.HasExited, "the server to start", 60);
            Assert.False(first.Process.HasExited, first.Text);
            Assert.Contains("Saved world 'Process World'", first.Text);
            Assert.Contains("not joinable yet", first.Text);
            Assert.Equal(OpenOutcome.Busy, SqliteWorldStore.Summarize(folder).Outcome);

            var stopping = Stopwatch.StartNew();
            first.Terminate();
            Assert.Equal(0, first.Exit(30));
            Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(30));
        }

        // The process is gone and the world is whole: one file, closed cleanly, loaded by the native SQLite the
        // server's own output carries.
        var saved = SqliteWorldStore.Summarize(folder);
        Assert.Equal((OpenOutcome.Opened, "Process World", true), (saved.Outcome, saved.Name, saved.WasCleanClose));
        // Summarize can create an empty WAL; no committed state may remain outside the database.
        var wal = new FileInfo(Path.Combine(folder, SqliteWorldStore.DatabaseFile + "-wal"));
        Assert.True(!wal.Exists || wal.Length == 0);
        var database = File.ReadAllBytes(Path.Combine(folder, SqliteWorldStore.DatabaseFile));

        // A start that asks for another seed is refused before the world is touched.
        using (var conflicting = new Running("--world", folder, "--seed", "32", "--port", port, "--listing", "off"))
        {
            Assert.Equal(1, conflicting.Exit(60));
            Assert.Contains("seed 31", conflicting.Text);
        }

        Assert.Equal(database, File.ReadAllBytes(Path.Combine(folder, SqliteWorldStore.DatabaseFile)));
        Assert.True(SqliteWorldStore.Summarize(folder).WasCleanClose);

        // Opened again by its folder, and killed outright: the world is whole, and knows it was not closed.
        using (var second = new Running("--world", folder, "--port", port, "--listing", "off"))
        {
            TestNet.WaitUntil(() => second.Text.Contains("Press Ctrl+C") || second.Process.HasExited, "the server to start again", 60);
            Assert.False(second.Process.HasExited, second.Text);
            Assert.Contains("was closed cleanly", second.Text);
            second.Process.Kill();
            second.Exit(30);
        }

        var killed = SqliteWorldStore.Summarize(folder);
        Assert.Equal((OpenOutcome.Opened, saved.Generation, false), (killed.Outcome, killed.Generation, killed.WasCleanClose));
    }
}
