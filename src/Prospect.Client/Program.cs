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
          --start <screen>        Open on: menu, join, settings, newgame, credits, account (friends), or local (start a local game)
          --join <host[:port]>    Join a server after the splash. Joining needs a Discord sign-in, which the game
                                  never has at launch, so this opens Join Server filled in: sign in, then Connect
          --host <visibility>     Start a local game others can join: unlisted (by address) or public (also in the
                                  server browser); private is the host alone, as --start local. Needs a UDP port
                                  that others can reach, and a Discord sign-in: without one it opens New Game
                                  filled in. With --start newgame, fills in the New Game screen instead
          --host-name <text>      With --host: the game's name, 1 to 48 characters
          --host-players <n>      With --host: player slots, the host included (default 8)
          --host-port <n>         With --host: the UDP port to listen on (default 27420)
          --password <text>       With --host: the password others need. With --join: the server's password.
                                  Arguments show in process lists; PROSPECT_PASSWORD in the environment works too
          --listing <url|off>     The listing server for public games and the server browser (default: content/net/listing.json)
          --set <id>=<value>      A world setting for local games started in this run; repeat for more (ids and ranges: content/rules/settings.json)
          --open <view>           With --start local or --join: open the command view, the map or the ship data sheet (command, map, map-menu, map-hover, galaxy, info), start a jump (jump, jump-wormhole, jump-rift), send the rest of the fleet on short jumps 600 m along -x for another client to watch (jump-fleet), or show Controls (controls), the in-game menu (menu), the world settings in force (world-settings), the player list (players), the party
                                  panel (social), the Friends tab (friends), chat (chat) or the world with only the always-shown party and chat (hud)
          --browser-sample <s>    The server browser shows a fixed sample instead of asking the listing server, for captures:
                                  ready, loading, failed, empty, off, or password (the sample with the password prompt open)
          --account-sample <s>    With --capture or --bench: the account and friends show a fixed sample instead of the
                                  account service, and nothing signs in: signed-out, waiting, signed-in, friends (full
                                  lists of hostile names), failed or off
          --sector <x>,<y>,<z>    With --start local or --bench: start in this sector instead of the home sector
          --size <W>x<H>          Start windowed at this size
          --validation            Enable Vulkan validation layers (default in debug builds)
          --no-validation         Disable Vulkan validation layers
          --bench <seconds>       Start a local game, measure frame pacing and allocation, print a report and exit
          --capture <file.png>    Save a screenshot after --capture-after seconds, then exit
          --capture-after <sec>   Delay for --capture, counted from the end of the splash (default 1.5)
          --capture-count <n>     Take n numbered screenshots instead of one (file-00.png, file-01.png, ...)
          --capture-every <sec>   Time between numbered screenshots (default 0.25)
          --look <degrees>        Pilot view: hold the camera this many degrees to the right of the hull's nose
          --look-to <degrees>     With --look and --capture: turn the camera from --look to this angle, evenly
                                  over the time the captures take
          --target                Pilot view: target the nearest other hull once in space
          --social-run <seconds>  Run a headless client script, then exit; uses --join or a private local game
          --player <name>         Player name for the headless script
          --say <text>            Send one everyone-chat line after admission (with --social-run)
          --party-say <text>      Send one party-chat line after forming a party (with --social-run)
          --invite <name>         Invite the first matching roster name once (with --social-run); repeat for more
          --accept-invites        Accept invitations automatically (with --social-run, or with --capture)
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
        var socialScript = new SocialScript();

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
                        case "newgame":
                            options.StartScreen = Screen.NewGame;
                            break;
                        case "credits":
                            options.StartScreen = Screen.Credits;
                            break;
                        case "account":
                            options.StartScreen = Screen.Account;
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
                case "--host" when hasValue && Visibilities.TryParse(args[i + 1], out var visibility):
                    options.Hosting.Visibility = visibility;
                    options.StartLocalGame = true;
                    i++;
                    break;
                case "--host-name" when hasValue:
                    if (!ServerNames.TryNormalize(args[++i], out var hostName))
                        return Fail($"A game's name is 1 to {ServerNames.MaxLength} characters: {args[i]}");
                    options.Hosting.Name = hostName;
                    break;
                case "--host-players" when hasValue && int.TryParse(args[i + 1], out var slots) && slots is > 0 and <= 256:
                    options.Hosting.MaxPlayers = slots;
                    i++;
                    break;
                case "--host-port" when hasValue && ushort.TryParse(args[i + 1], out var hostPort) && hostPort > 0:
                    options.Hosting.Port = hostPort;
                    i++;
                    break;
                case "--password" when hasValue:
                    if (!Passwords.IsUsable(args[++i]))
                        return Fail($"A password is 1 to {Passwords.MaxChars} characters.");
                    options.Password = args[i];
                    break;
                case "--listing" when hasValue:
                    options.Listing = args[++i];
                    break;
                case "--set" when hasValue:
                    options.WorldSettingArgs.Add(args[++i]);
                    break;
                case "--open" when hasValue && args[i + 1] is "command" or "map" or "map-menu" or "map-hover" or "info" or "galaxy" or "jump" or "jump-wormhole" or "jump-rift" or "jump-fleet" or "world-settings" or "menu" or "controls" or "players" or "social" or "friends" or "chat" or "hud":
                    options.OpenView = args[++i];
                    break;
                case "--browser-sample" when hasValue:
                    options.BrowserSample = args[++i] switch
                    {
                        "ready" => BrowserSample.Ready,
                        "loading" => BrowserSample.Loading,
                        "failed" => BrowserSample.Failed,
                        "empty" => BrowserSample.Empty,
                        "off" => BrowserSample.Off,
                        "password" => BrowserSample.Password,
                        _ => BrowserSample.None,
                    };
                    if (options.BrowserSample == BrowserSample.None)
                        return Fail($"Unknown browser sample: {args[i]}");
                    break;
                case "--account-sample" when hasValue && args[i + 1] is "signed-out" or "waiting" or "signed-in" or "friends" or "failed" or "off":
                    options.AccountSample = args[++i];
                    break;
                case "--sector" when hasValue:
                    {
                        var parts = args[++i].Split(',');
                        if (parts.Length != 3 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y) || !int.TryParse(parts[2], out var z))
                            return Fail($"Invalid sector: {args[i]} (expected x,y,z)");
                        options.SpawnSector = new Prospect.Game.World.SectorCoord(x, y, z);
                        break;
                    }
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
                case "--capture-count" when hasValue && int.TryParse(args[i + 1], out var captures) && captures is > 0 and <= 400:
                    options.CaptureCount = captures;
                    i++;
                    break;
                case "--capture-every" when hasValue && float.TryParse(args[i + 1], out var every) && every > 0:
                    options.CaptureEvery = every;
                    i++;
                    break;
                case "--look" when hasValue && float.TryParse(args[i + 1], out var look):
                    options.LookDegrees = look;
                    i++;
                    break;
                case "--look-to" when hasValue && float.TryParse(args[i + 1], out var lookTo):
                    options.LookToDegrees = lookTo;
                    i++;
                    break;
                case "--social-run" when hasValue && int.TryParse(args[i + 1], out var seconds) && seconds is > 0 and <= 3600:
                    socialScript.Seconds = seconds; i++; break;
                case "--player" when hasValue:
                    socialScript.Name = args[++i]; break;
                case "--say" when hasValue:
                    socialScript.Say = args[++i]; break;
                case "--party-say" when hasValue:
                    socialScript.PartySay = args[++i]; break;
                case "--invite" when hasValue:
                    socialScript.Invite.Add(args[++i]); break;
                case "--accept-invites":
                    socialScript.Accept = true; options.AcceptInvites = true; break;
                case "--target":
                    options.TargetNearest = true;
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

        // A sample stands in for the account service only where nobody can act on it.
        if (options.AccountSample is not null && options.CapturePath is null && options.BenchSeconds <= 0)
            return Fail("--account-sample works only with --capture or --bench.");

        // The New Game screen shows the hosting --host asked for and lets the player start it from there.
        if (options.StartScreen == Screen.NewGame)
            options.StartLocalGame = false;

        // The password may come from the environment instead, where a process list does not show it.
        if (options.Password is null && Environment.GetEnvironmentVariable("PROSPECT_PASSWORD") is { Length: > 0 } fromEnvironment)
        {
            if (!Passwords.IsUsable(fromEnvironment))
                return Fail($"PROSPECT_PASSWORD: a password is 1 to {Passwords.MaxChars} characters.");
            options.Password = fromEnvironment;
        }

        Logs.Initialize(verbose ? LogLevel.Debug : LogLevel.Information);
        PreciseSleep.EnableHighResolutionTimer();

        // BRUTAL's job runners borrow thread-pool threads; make sure enough exist up front so the first burst of
        // jobs does not wait on the pool's slow thread injection.
        ThreadPool.GetMinThreads(out var workers, out var completion);
        ThreadPool.SetMinThreads(Math.Max(workers, Environment.ProcessorCount), completion);

        try
        {
            if (socialScript.Seconds > 0) return socialScript.Run(options);
            using var app = new App(options);
            app.Run();
            return app.ExitCode;
        }
        catch (InvalidDataException e)
        {
            // Content or a world setting given with --set that cannot be used: the message says which and why.
            Logs.App.Error(e.Message);
            Console.Error.WriteLine(e.Message);
            return 1;
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
