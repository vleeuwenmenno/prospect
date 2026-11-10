using Microsoft.Extensions.Logging;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Net;
using Prospect.Engine.Time;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;

namespace Prospect.Server;

/// <summary>The headless dedicated server: the same <see cref="ServerHost"/> a local game runs, listening on UDP.</summary>
internal static class Program
{
    private const string Usage = """
        prospect-server [options]
          --port <n>          UDP port to listen on (default 27420)
          --name <text>       Server name shown to players
          --max-players <n>   Player slots (default 16)
          --seed <n>          Galaxy seed
          --verbose           Log debug messages
          --help              Show this text
        """;

    private static int Main(string[] args)
    {
        var port = ProtocolInfo.DefaultPort;
        var name = "PROSPECT server";
        var maxPlayers = 16;
        var seed = new ServerConfig().GalaxySeed;
        var verbose = false;

        for (var i = 0; i < args.Length; i++)
        {
            var hasValue = i + 1 < args.Length;
            switch (args[i])
            {
                case "--port" when hasValue && ushort.TryParse(args[i + 1], out var value) && value > 0:
                    port = value;
                    i++;
                    break;
                case "--name" when hasValue:
                    name = PlayerNames.Sanitize(args[++i]);
                    break;
                case "--max-players" when hasValue && int.TryParse(args[i + 1], out var value) && value is > 0 and <= 256:
                    maxPlayers = value;
                    i++;
                    break;
                case "--seed" when hasValue && ulong.TryParse(args[i + 1], out var value):
                    seed = value;
                    i++;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete option: {args[i]}\n\n{Usage}");
                    return 2;
            }
        }

        Logs.Initialize(verbose ? LogLevel.Debug : LogLevel.Information);
        PreciseSleep.EnableHighResolutionTimer();

        RakNetTransport transport;
        GameContent content;
        try
        {
            content = GameContent.Load();
            transport = RakNetTransport.Listen(port, maxPlayers + 8);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Logs.App.Error(e.Message);
            return 1;
        }

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

        var config = new ServerConfig
        {
            Name = name,
            MaxPlayers = maxPlayers,
            GalaxySeed = seed,
        };
        using var server = new ServerHost(config, content, transport);
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
                    $"tick {status.Tick}, {status.PlayerCount}/{maxPlayers} players, "
                        + $"{status.Loop.AverageMs:F2} ms avg, {status.Loop.MaxMs:F2} ms max, {status.Loop.Overruns} overruns"
                );
            }
        }

        Logs.App.Info("Shutting down");
        return 0;
    }
}
