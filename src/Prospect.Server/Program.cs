using Microsoft.Extensions.Logging;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Time;
using Prospect.Game.Server;
using Prospect.Game.World;

namespace Prospect.Server;

/// <summary>The headless dedicated server: the same <see cref="ServerHost"/> a local game runs, listening on UDP.</summary>
internal static class Program
{
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
            server = new ServerHost(config, content);
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
        }

        return 0;
    }
}
