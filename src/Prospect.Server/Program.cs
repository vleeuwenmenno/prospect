using Microsoft.Extensions.Logging;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Time;
using Prospect.Game.Persistence;
using Prospect.Game.Server;
using Prospect.Game.World;

namespace Prospect.Server;

/// <summary>The headless dedicated server: the same <see cref="ServerHost"/> a local game runs, listening on UDP.</summary>
internal static class Program
{
    /// <summary>How long a stop waits for the world to be saved and closed before the process leaves anyway.</summary>
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(60);

    private static int Main(string[] args)
    {
        var exitCode = Prepare(args, out var server);
        Array.Clear(args);
        return server is null ? exitCode : Run(server);
    }

    private static int Prepare(string[] args, out ServerHost? server)
    {
        server = null;
        var command = new ServerCommand();
        var result = command.Parse(args, Console.Out, Console.Error, out var exitCode);
        if (result is null)
            return exitCode;
        var verbose = result.GetValue(command.Verbose);
        Logs.Initialize(verbose ? LogLevel.Debug : LogLevel.Information);
        PreciseSleep.EnableHighResolutionTimer();

        try
        {
            var content = GameContent.Load();
            var config = command.BuildConfig(result, content, Environment.GetEnvironmentVariable("PROSPECT_PASSWORD"), message => Logs.App.Warning(message));

            // The server owns the world's store from here, also when its construction refuses the world.
            server = new ServerHost(config, content);
            if (config.World is { } world)
            {
                var how = command.WorldCreated
                    ? "created"
                    : $"opened at save {world.Generation}, interval {world.World.NextWorldTick}; its last run {(command.WorldWasCleanlyClosed ? "was closed cleanly" : "did not end with a final save")}";
                Logs.App.Info($"Saved world '{world.World.Name}' in {command.WorldFolder}: {how}. It is saved every {world.AutosaveSeconds} s while it changes, and when the server stops.");

                // Said plainly, every start: this mode keeps a world, it does not yet host one.
                Logs.App.Warning("A saved world is not joinable yet. The server cannot verify who owns what in it, so it lets no player in; joining comes with verified accounts.");
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            // The console logger writes from a background queue that the exit can cut off, so what stopped the
            // start also goes straight to stderr.
            Logs.App.Error(e.Message);
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        return 0;
    }

    private static int Run(ServerHost server)
    {
        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                stop.Set();
            }
        );

        using (server)
        {
            server.Start();
            Logs.App.Info("Press Ctrl+C to stop");

            // Report load once a minute; leave as soon as a stop is requested or the simulation dies.
            var lastReport = 0L;
            while (!stop.Wait(250))
            {
                if (!server.IsRunning)
                {
                    Logs.App.Error($"Simulation stopped unexpectedly: {server.Fault}");
                    if (server.IsPersistent)
                    {
                        var (_, _, lost) = ServerShutdown.Describe(true, server.Close, CloseWait);
                        Logs.App.Error(lost);
                        Console.Error.WriteLine(lost);
                    }

                    return 1;
                }

                var status = server.Status;
                if (status.Tick - lastReport >= TickLoop.SimulationHz * 60)
                {
                    lastReport = status.Tick;
                    Logs.App.Info(
                        $"tick {status.Tick}, {status.PlayerCount}/{server.MaxPlayers} players, "
                            + $"{status.Loop.AverageMs:F2} ms avg, {status.Loop.MaxMs:F2} ms max, {status.Loop.Overruns} overruns"
                    );
                }
            }

            Logs.App.Info("Shutting down");

            // Every server is closed the same way: asked to, waited for, then disposed. One without a world
            // store has nothing to save and is closed at once. One with a store freezes its world, saves it
            // and only then lets its players go; the wait is bounded, so a store that never answers cannot
            // keep the process from exiting.
            server.BeginClose();
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!server.Close.Completed && server.IsRunning && waited.Elapsed < CloseWait)
                Thread.Sleep(50);

            var (exitCode, problem, message) = ServerShutdown.Describe(server.IsPersistent, server.Close, CloseWait);
            if (problem)
            {
                // The console logger writes from a background queue that the exit can cut off.
                Logs.App.Error(message);
                Console.Error.WriteLine(message);
            }
            else
            {
                Logs.App.Info(message);
            }

            if (exitCode != 0)
                return exitCode;
        }

        return 0;
    }
}

/// <summary>
/// What a server's close came to, in the words and the exit code whoever runs it gets. The cases are kept
/// apart because they are different things to act on: a world that is saved, one that is saved but whose folder
/// needs care, one whose last save stands, and one of which nobody can yet say which state it is in.
/// </summary>
internal static class ServerShutdown
{
    /// <summary>The world's final save was committed, or the server had no world to save.</summary>
    internal const int Saved = 0;

    /// <summary>The final save was not made; the world is as its last completed save left it.</summary>
    internal const int NotSaved = 1;

    /// <summary>Whether the final save was committed is not known. Distinct, so a script can tell it from a plain failure.</summary>
    internal const int Unknown = 3;

    /// <summary>
    /// The exit code, whether it is a problem to be shown on the error stream, and one sentence or two about it.
    /// <paramref name="waited"/> is how long the close was given before the server stopped waiting for it.
    /// </summary>
    internal static (int ExitCode, bool Problem, string Message) Describe(bool persistent, CloseStatus close, TimeSpan waited)
    {
        if (!persistent)
            return (Saved, false, "Stopped. This server keeps no world: nothing was saved.");
        if (!close.Completed)
            return (Unknown, true, $"The world's final save did not finish within {waited.TotalSeconds:F0} s. Whether it was committed is not known; the next start says whether the world was closed cleanly.");
        switch (close.FinalCommit)
        {
            case FinalCommitOutcome.Committed when !close.Degraded:
                return (Saved, false, "The world is saved and closed cleanly.");

            case FinalCommitOutcome.Committed:
                // The save stands. What failed after it is said by name, each on its own.
                var checkpoint = close.Checkpoint == CheckpointOutcome.Failed
                    ? $" Its log could not be folded into its database ({close.CheckpointError ?? "no reason given"}): the folder holds the world in more than one file, so do not copy it until the world has been started and stopped again."
                    : "";
                var backup = close.Backup == BackupOutcome.Failed
                    ? $" No recovery copy of the final state was made ({close.BackupError ?? "no reason given"}); the earlier copies are untouched."
                    : "";
                return (Saved, true, $"The world is saved and marked as closed cleanly, but not everything after the save went through.{checkpoint}{backup}");

            case FinalCommitOutcome.Failed:
                return (NotSaved, true, $"The world's final save failed: {close.Error ?? "no reason given"}. The world is as its last completed save left it, and is not marked as closed cleanly.");

            case FinalCommitOutcome.Unknown:
                return (Unknown, true, $"It is not known whether the world's final save was committed: {close.Error ?? "no reason given"}. The world is either as its last completed save left it or as it was when the server stopped; the next start says which.");

            case FinalCommitOutcome.SkippedAfterFault:
                return (NotSaved, true, "The simulation stopped inside an interval, so the world was not saved in that state. It is as its last completed save left it, and is not marked as closed cleanly.");

            case FinalCommitOutcome.Abandoned:
                return (Unknown, true, "The server stopped before its final save was known to be complete. Whether it was committed is not known; the next start says whether the world was closed cleanly.");

            default:
                return (NotSaved, true, "The server stopped without a final save. The world is as its last completed save left it, and is not marked as closed cleanly.");
        }
    }
}
