using Microsoft.Extensions.Logging;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Time;
using Prospect.Game.Lobby;
using Prospect.Game.Protocol;
using Prospect.Game.Server;
using Prospect.Game.World;

namespace Prospect.Server;

/// <summary>The headless dedicated server: the same <see cref="ServerHost"/> a local game runs, listening on UDP.</summary>
internal static class Program
{
    /// <summary>The environment variable a server's password is read from, so it need not be on the command line.</summary>
    private const string PasswordVariable = "PROSPECT_PASSWORD";

    private const string Usage = """
        prospect-server [options]
          --name <text>           Server name shown to players and on the list, 1 to 48 characters
          --visibility <v>        unlisted: joined by address only (default)
                                  public: also announced to the listing server, so it shows in the server browser
          --port <n>              UDP port to listen on (default 27420)
          --max-players <n>       Player slots (default 16)
          --password <text>       Players must know this to join. Arguments show in process lists: prefer the
                                  PROSPECT_PASSWORD environment variable or the settings file
          --listing <url|off>     The listing server a public server announces to (default: content/net/listing.json)
          --seed <n>              Galaxy seed
          --settings <file>       A JSON object of world setting ids and values. An object under "server" may hold
                                  name, visibility, port, maxPlayers, password and listing
          --set <id>=<value>      One world setting, after the file; repeat for more
                                  (the ids and ranges are in content/rules/settings.json)
          --verbose               Log debug messages
          --help                  Show this text

        An argument wins over PROSPECT_PASSWORD, which wins over the settings file. The password protects itself,
        not the traffic: game packets are not encrypted.
        """;

    private static int Main(string[] args)
    {
        ushort? port = null;
        string? name = null;
        int? maxPlayers = null;
        ServerVisibility? visibility = null;
        string? password = null;
        string? listing = null;
        var seed = new ServerConfig().GalaxySeed;
        var verbose = false;
        string? settingsFile = null;
        var assignments = new List<string>();

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
                    name = args[++i];
                    break;
                case "--visibility" when hasValue && Visibilities.TryParse(args[i + 1], out var value):
                    visibility = value;
                    i++;
                    break;
                case "--password" when hasValue:
                    password = args[++i];
                    break;
                case "--listing" when hasValue:
                    listing = args[++i];
                    break;
                case "--max-players" when hasValue && int.TryParse(args[i + 1], out var value) && value is > 0 and <= 256:
                    maxPlayers = value;
                    i++;
                    break;
                case "--seed" when hasValue && ulong.TryParse(args[i + 1], out var value):
                    seed = value;
                    i++;
                    break;
                case "--settings" when hasValue:
                    settingsFile = args[++i];
                    break;
                case "--set" when hasValue:
                    assignments.Add(args[++i]);
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

        ServerHost server;
        ServerConfig config;
        try
        {
            var content = GameContent.Load();
            var settings = content.SettingCatalog.Build(settingsFile, assignments, message => Logs.App.Warning(message));
            var file = settingsFile is null ? ServerFile.Empty : ServerFile.Read(settingsFile, message => Logs.App.Warning(message));

            // An empty variable is how a container says "not set".
            var fromEnvironment = Environment.GetEnvironmentVariable(PasswordVariable);
            password ??= string.IsNullOrEmpty(fromEnvironment) ? file.Password : fromEnvironment;
            if (password is { Length: 0 })
                password = null;

            visibility ??= file.Visibility ?? ServerVisibility.Unlisted;
            if (visibility == ServerVisibility.Private)
                throw new InvalidDataException("A dedicated server cannot be private: nobody could join it. Use unlisted or public.");

            // The listing address is checked even for an unlisted server, so a typing error shows at once.
            var listingConfig = ListingConfig.Resolve(listing ?? file.Listing);
            if (visibility == ServerVisibility.Public && listingConfig is null)
                Logs.App.Warning("The server is public but the listing server is turned off: it will not show in any server browser");

            config = new ServerConfig
            {
                Name = name ?? file.Name ?? ServerNames.Default,
                MaxPlayers = maxPlayers ?? file.MaxPlayers ?? 16,
                GalaxySeed = seed,
                Settings = settings,
                Visibility = visibility.Value,
                Password = password,
                Port = port ?? file.Port ?? ProtocolInfo.DefaultPort,
                Listing = listingConfig,
            };
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
                        $"tick {status.Tick}, {status.PlayerCount}/{config.MaxPlayers} players, "
                            + $"{status.Loop.AverageMs:F2} ms avg, {status.Loop.MaxMs:F2} ms max, {status.Loop.Overruns} overruns"
                    );
                }
            }

            Logs.App.Info("Shutting down");
        }

        return 0;
    }
}
