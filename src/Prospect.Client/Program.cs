using Brutal.Numerics;
using Microsoft.Extensions.Logging;
using Prospect.Client.Screens;
using Prospect.Engine.Diagnostics;
using Prospect.Engine.Time;
using Prospect.Game.Protocol;

namespace Prospect.Client;

internal static class Program
{
    private const string Usage = """
        prospect [options]
          --start <screen>        Open on: menu, join, settings, credits, or local (start a local game)
          --join <host[:port]>    Connect to a server straight after the splash
          --open <view>           With --start local or --join: open the command view, the map or the ship data sheet (command, map, map-menu, galaxy, info), or start a jump (jump, jump-wormhole, jump-rift)
          --size <W>x<H>          Start windowed at this size
          --validation            Enable Vulkan validation layers (default in debug builds)
          --no-validation         Disable Vulkan validation layers
          --bench <seconds>       Start a local game, measure frame pacing and allocation, print a report and exit
          --capture <file.png>    Save a screenshot after --capture-after seconds, then exit
          --capture-after <sec>   Delay for --capture, counted from the end of the splash (default 1.5)
          --verbose               Log debug messages
          --help                  Show this text
        """;

    private static int Main(string[] args)
    {
        var options = new AppOptions();
#if DEBUG
        options.Validation = true;
#endif
        var verbose = false;

        for (var i = 0; i < args.Length; i++)
        {
            var hasValue = i + 1 < args.Length;
            switch (args[i])
            {
                case "--start" when hasValue:
                    switch (args[++i])
                    {
                        case "menu":
                            options.StartScreen = Screen.MainMenu;
                            break;
                        case "join":
                            options.StartScreen = Screen.Join;
                            break;
                        case "settings":
                            options.StartScreen = Screen.Settings;
                            break;
                        case "credits":
                            options.StartScreen = Screen.Credits;
                            break;
                        case "local":
                            options.StartLocalGame = true;
                            break;
                        default:
                            return Fail($"Unknown screen: {args[i]}");
                    }

                    break;
                case "--join" when hasValue:
                    {
                        var target = args[++i];
                        var colon = target.LastIndexOf(':');
                        options.JoinPort = ProtocolInfo.DefaultPort;
                        if (colon > 0)
                        {
                            if (!ushort.TryParse(target.AsSpan(colon + 1), out var port) || port == 0)
                                return Fail($"Invalid port in: {target}");
                            options.JoinPort = port;
                            target = target[..colon];
                        }

                        options.JoinHost = target;
                        break;
                    }
                case "--open" when hasValue && args[i + 1] is "command" or "map" or "map-menu" or "info" or "galaxy" or "jump" or "jump-wormhole" or "jump-rift":
                    options.OpenView = args[++i];
                    break;
                case "--size" when hasValue:
                    {
                        var parts = args[++i].Split('x');
                        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) || w < 960 || h < 540)
                            return Fail($"Invalid size: {args[i]} (minimum 960x540)");
                        options.WindowSize = new int2(w, h);
                        break;
                    }
                case "--validation":
                    options.Validation = true;
                    break;
                case "--no-validation":
                    options.Validation = false;
                    break;
                case "--bench" when hasValue && float.TryParse(args[i + 1], out var seconds) && seconds is > 0 and <= 600:
                    options.BenchSeconds = seconds;
                    i++;
                    break;
                case "--capture" when hasValue:
                    options.CapturePath = args[++i];
                    break;
                case "--capture-after" when hasValue && float.TryParse(args[i + 1], out var after) && after >= 0:
                    options.CaptureAfter = after;
                    i++;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    return Fail($"Unknown or incomplete option: {args[i]}");
            }
        }

        Logs.Initialize(verbose ? LogLevel.Debug : LogLevel.Information);
        PreciseSleep.EnableHighResolutionTimer();

        // BRUTAL's job runners borrow thread-pool threads; make sure enough exist up front so the first burst of
        // jobs does not wait on the pool's slow thread injection.
        ThreadPool.GetMinThreads(out var workers, out var completion);
        ThreadPool.SetMinThreads(Math.Max(workers, Environment.ProcessorCount), completion);

        try
        {
            using var app = new App(options);
            app.Run();
            return app.ExitCode;
        }
        catch (Exception e)
        {
            // The console logger writes from a background queue that the exit can cut off, so a fatal error also
            // goes straight to stderr.
            Logs.App.Error($"Fatal: {e}");
            Console.Error.WriteLine($"Fatal: {e}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"{message}\n\n{Usage}");
        return 2;
    }
}
