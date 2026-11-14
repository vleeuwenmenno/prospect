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
          --start <screen>        Open on: menu, join, settings, newgame, credits, account (friends), load (Load World),
                                  continue (the world played last), or local (start a new local game: a saved world
                                  when saved worlds are on, see --worlds)
          --worlds <folder>       The folder of saved worlds instead of the player's own. Captures and benchmarks
                                  save nothing and list no world unless given one
          --save-fault <kind>     With --capture and --worlds: make a world's final save slow (8 s), fail, end
                                  unknown or degraded, to capture the saving screen and the close report. Pair with
                                  --open leave, which leaves the game once in space
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
          --account-authority <url|off>  Account service (defaults to --listing); local claims require an HTTPS origin
          --listing <url|off>     The listing server for public games and the server browser (default: content/net/listing.json)
          --set <id>=<value>      A world setting for local games started in this run; repeat for more (ids and ranges: content/rules/settings.json)
          --open <view>           With --start local or --join: open the command view, the map or the ship data sheet (command, map, map-menu, map-hover, galaxy, info), the map with the own sector's card pinned (map-card), a sector 144 km away pinned (map-card-near) or one 460 km away, out of drive range (map-card-far), the command view's right-click menu open on a blind jump 60 km ahead for the ship being flown (command-menu), for the rest of the fleet (command-menu-fleet), on a point 3 km away (command-menu-close) or 450 km away (command-menu-far), tilted (map-tilt), zoomed in on the own position to show the shape of sensor reach (map-reach, map-reach-tilt) or with a scan of the own sector started (scan), the command view pulled back to show sensor rings and fog (command-fog), the same with only the flown ship selected (command-fog-one), with it and another ship that jumps 6 km away selected (command-fog-pair, capture after 28 s) or 14 km away (command-fog-far, capture after 28 s), start a jump (jump, jump-wormhole, jump-rift), send the rest of the fleet on short jumps 6 km along -x for another client to watch (jump-fleet) or straight ahead of the camera so their openings overlap (jump-fleet-ahead), or show Controls (controls), the ship editor on its Modules tab (modules), the command view pulled back to see locator marks (module-command), the Gas window (gas, pinned-gas), ask to edit the own ship in the ship editor (edit), leave in the drone and open the New ship dialog empty (new-ship), showing a refusal (new-ship-refused) or built, the editor open on the new ship (new-ship-editor) or that edit cancelled so you fly the new ship (new-ship-closed), the in-game menu (menu), the world settings in force (world-settings), the player list (players), the party
                                  panel (social), the Friends tab (friends), chat (chat) or the world with only the always-shown party and chat (hud).
                                  With --start load, for captures with --worlds: the confirmation before deleting the first world (delete-world) or every
                                  incompatible one (delete-incompatible), or the list after the incompatible ones are deleted (deleted-incompatible)
          --browser-sample <s>    The server browser shows a fixed sample instead of asking the listing server, for captures:
                                  ready, loading, failed, empty, off, or password (the sample with the password prompt open)
          --account-sample <s>    With --capture or --bench: the account and friends show a fixed sample instead of the
                                  account service, and nothing signs in: signed-out, waiting, signed-in, friends (full
                                  lists of hostile names), failed or off
          --tips-sample <s>       With --capture or --bench: show the in-game tips, which a scripted run hides otherwise.
                                  on lets them fire as in play (--open map shows the map's tips), stack fires the command
                                  view's three at once, and reset (with --start settings) opens the Reset tips question
          --gas-fixture <1..13>    With --capture only: controlled production-shader regression inputs (13: real-source overlay check)
          --gas-view <view>       Muted cloud camera: outside, inside, edge, offscreen, absent, inside-absent, pilot-absent, pilot
          --gas-distance <radii>  With --gas-view outside or absent: the camera's distance in largest half-extents of the cloud
          --gas-distance-to <radii>  Move from --gas-distance to this distance over the captures, in equal ratios
          --sector <x>,<y>,<z>    With --start local or --bench: start in this sector instead of the home sector
          --charted <n>           With --capture or --bench and a new local game: every sector within n sectors (0 to 4) of the start is charted, to look at the Charts window
          --jump-to <x>,<y>,<z>   With --open jump, jump-wormhole or jump-rift: jump to this sector's centre instead of current sector + (6,0,-4); normal jump limits apply
          --size <W>x<H>          Start windowed at this size
          --validation            Enable Vulkan validation layers (default in debug builds)
          --no-validation         Disable Vulkan validation layers
          --visual-scene <spec>  With --bench/--capture: 30|50,clean|dents|impacts|repair|jump,near|far|behind[,large]
          --bench <seconds>       Start a local game, measure frame pacing and allocation, print a report and exit
          --bench-resize <n>      With --bench: resize the window n times while measuring, and rebuild the swapchain
                                  and the render targets at the same size after each, so rebuilding them is
                                  measured too; fails unless they were rebuilt n times
          --capture <file.png>    Save a screenshot after --capture-after seconds, then exit
          --capture-after <sec>   Delay for --capture, counted from the end of the splash (default 1.5)
          --capture-count <n>     Take n numbered screenshots instead of one (file-00.png, file-01.png, ...)
          --capture-every <sec>   Time between numbered screenshots (default 0.25)
          --map-cell <pixels>     With --capture or --bench and a map view: open the map at this many pixels per sector (1.4 to 190)
          --close-map-at <sec>    With --capture or --bench and a map view: close the map at this time after the splash (captures of the first frames back in the world)
          --cancel-scan-at <sec>  With --capture or --bench and a scan view (scan, scan-pilot): cancel the scan at this time after the splash, as the scan key pressed again would
          --map-tilt, --map-hover With --open scan and --capture or --bench: tilt the map, or rest the pointer on its centre
          --target-rock           With --capture or --bench: target the asteroid nearest the ship once its sector has loaded
          --module <gas|asteroid|locator> With --capture or --bench: run that module as its key would (asteroid needs --target-rock); --module-at <sec> says when (default 1)
          --look <degrees>        Pilot view: hold the camera this many degrees to the right of the hull's nose
          --look-to <degrees>     With --look and --capture: turn the camera from --look to this angle, evenly
                                  over the time the captures take
          --rts-script <json>     Private muted capture poses; --rts-seed <n> selects the diagnostic seed
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
                        case "load":
                            options.StartScreen = Screen.LoadWorld;
                            break;
                        case "continue":
                            options.ContinueWorld = true;
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
                case "--save-fault" when hasValue && Enum.TryParse<StoreFault>(args[i + 1], true, out var fault) && fault != StoreFault.None:
                    options.SaveFault = fault;
                    i++;
                    break;
                case "--worlds" when hasValue:
                    options.WorldsRoot = args[++i];
                    break;
                case "--account-authority" when hasValue:
                    options.AccountAuthority = args[++i];
                    break;
                case "--listing" when hasValue:
                    options.Listing = args[++i];
                    break;
                case "--set" when hasValue:
                    options.WorldSettingArgs.Add(args[++i]);
                    break;
                case "--open" when hasValue && args[i + 1] is "command" or "drone" or "map" or "map-menu" or "map-hover" or "map-card" or "map-card-near" or "map-card-far" or "map-tilt" or "map-reach" or "map-reach-tilt" or "scan" or "command-fog" or "command-fog-one" or "command-fog-pair" or "command-fog-far" or "command-menu" or "command-menu-fleet" or "command-menu-close" or "command-menu-far" or "info" or "galaxy" or "jump" or "jump-wormhole" or "jump-rift" or "jump-fleet" or "jump-fleet-ahead" or "world-settings" or "menu" or "controls" or "players" or "social" or "friends" or "chat" or "hud" or "edit" or "new-ship" or "new-ship-refused" or "new-ship-editor" or "new-ship-closed" or "scan-pilot" or "scan-command" or "interface" or "charts" or "pinned-charts" or "inventory" or "modules" or "module-command" or "gas" or "pinned-gas" or "leave" or "delete-world" or "delete-incompatible" or "deleted-incompatible":
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
                case "--controls-search" when hasValue:
                    options.ControlsSearch = args[++i];
                    break;
                case "--controls-tab" when hasValue:
                    options.ControlsTab = args[++i];
                    break;
                case "--controls-sample" when hasValue && args[i + 1] is "capture" or "conflict":
                    options.ControlsSample = args[++i];
                    break;
                case "--tips-sample" when hasValue && args[i + 1] is "on" or "stack" or "reset":
                    options.TipsSample = args[++i];
                    break;
                case "--account-sample" when hasValue && args[i + 1] is "signed-out" or "waiting" or "signed-in" or "friends" or "failed" or "off":
                    options.AccountSample = args[++i];
                    break;
                case "--gas-fixture" when hasValue:
                    if (!int.TryParse(args[++i], out var gasFixture) || gasFixture < 1 || gasFixture > Prospect.Client.Render.GasShaderFixture.OverlayCheck)
                        return Fail("Invalid --gas-fixture: expected 1..13.");
                    options.GasFixture = gasFixture;
                    break;
                case "--gas-view" when hasValue:
                    options.GasView = args[++i];
                    if (options.GasView is not ("outside" or "inside" or "edge" or "offscreen" or "absent" or "inside-absent" or "pilot-absent" or "pilot"))
                        return Fail("Invalid --gas-view: outside, inside, edge, offscreen, absent, inside-absent, pilot-absent, pilot.");
                    break;
                case "--gas-distance" when hasValue:
                case "--gas-distance-to" when hasValue:
                    {
                        var option = args[i];
                        var to = option == "--gas-distance-to";
                        if (!float.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var radii) || !(radii is >= 0.1f and <= 200f))
                            return Fail($"Invalid {option}: a distance of 0.1 to 200 cloud radii.");
                        if (to) options.GasDistanceTo = radii;
                        else options.GasDistance = radii;
                        break;
                    }
                case "--jump-to" when hasValue:
                    {
                        var parts = args[++i].Split(',');
                        if (parts.Length != 3 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y) || !int.TryParse(parts[2], out var z))
                            return Fail($"Invalid jump target: {args[i]} (expected x,y,z)");
                        options.JumpTo = new Prospect.Game.World.SectorCoord(x, y, z);
                        break;
                    }
                case "--charted" when hasValue:
                    if (!int.TryParse(args[++i], out var charted) || charted is < 0 or > 4)
                        return Fail($"Invalid --charted: {args[i]} (expected 0 to 4)");
                    options.ChartedRadius = charted;
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
                case "--visual-scene" when hasValue:
                    var scene = args[++i].Split(',');
                    if (scene.Length is not (3 or 4) || scene.Length == 4 && scene[3] != "large" || !int.TryParse(scene[0], out var hulls)) return Fail("Expected hulls,activity,view");
                    options.VisualBenchmark = new Prospect.Game.Server.VisualBenchmarkScene(hulls, scene[1], scene[2], scene.Length == 4 && scene[3] == "large");
                    try { options.VisualBenchmark.Validate(); } catch (InvalidDataException e) { return Fail(e.Message); }
                    options.StartLocalGame = true;
                    break;
                case "--bench" when hasValue && float.TryParse(args[i + 1], out var seconds) && seconds is > 0 and <= 600:
                    options.BenchSeconds = seconds;
                    i++;
                    break;
                case "--bench-resize" when hasValue && int.TryParse(args[i + 1], out var resizes) && resizes is > 0 and <= 64:
                    options.BenchResizes = resizes;
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
                case "--rts-seed" when hasValue && ulong.TryParse(args[i + 1], out var rtsSeed):
                    options.RtsSeed = rtsSeed; i++; break;
                case "--rts-script" when hasValue:
                    options.RtsScript = args[++i]; break;
                case "--map-cell" when hasValue && float.TryParse(args[i + 1], out var mapCell) && mapCell is >= 1.4f and <= 190f:
                    options.MapCell = mapCell;
                    i++;
                    break;
                case "--close-map-at" when hasValue && float.TryParse(args[i + 1], out var closeMapAt) && closeMapAt >= 0:
                    options.CloseMapAt = closeMapAt;
                    i++;
                    break;
                case "--cancel-scan-at" when hasValue && float.TryParse(args[i + 1], out var cancelScanAt) && cancelScanAt >= 0:
                    options.CancelScanAt = cancelScanAt;
                    i++;
                    break;
                case "--map-tilt":
                    options.MapTilt = true;
                    break;
                case "--map-hover":
                    options.MapHover = true;
                    break;
                case "--look" when hasValue && float.TryParse(args[i + 1], out var look):
                    options.LookDegrees = look;
                    i++;
                    break;
                case "--break-shield" when hasValue && float.TryParse(args[i + 1], out var breakAt) && breakAt is >= 0f and <= 600f:
                    options.BreakShieldSeconds = breakAt;
                    i++;
                    break;
                case "--hurt-fleet" when hasValue && float.TryParse(args[i + 1], out var hurtAt) && hurtAt is >= 0f and <= 600f:
                    options.HurtFleetSeconds = hurtAt;
                    i++;
                    break;
                case "--hurt-kind" when hasValue && (args[i + 1] is "damaged" or "broken"):
                    options.HurtFleetBroken = args[i + 1] == "broken";
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
                case "--target-rock":
                    options.TargetRock = true;
                    break;
                case "--module" when hasValue && args[i + 1] is "gas" or "asteroid" or "locator":
                    options.ModuleRun = args[++i];
                    break;
                case "--module-at" when hasValue && float.TryParse(args[i + 1], out var moduleAt) && moduleAt >= 0:
                    options.ModuleAt = moduleAt;
                    i++;
                    break;
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

        if ((options.JumpTo is not null || options.OpenView == "jump-fleet-ahead") && options.CapturePath is null && options.BenchSeconds <= 0)
            return Fail("--jump-to and --open jump-fleet-ahead work only with --capture or --bench.");

        if ((options.MapTilt || options.MapHover) && (options.OpenView != "scan" || (options.CapturePath is null && options.BenchSeconds <= 0)))
            return Fail("--map-tilt and --map-hover work only with --open scan and --capture or --bench.");

        if (options.MapCell is not null && options.CapturePath is null && options.BenchSeconds <= 0)
            return Fail("--map-cell works only with --capture or --bench.");

        if (options.CloseMapAt is not null && options.CapturePath is null && options.BenchSeconds <= 0)
            return Fail("--close-map-at works only with --capture or --bench.");

        if (options.CancelScanAt is not null && options.CapturePath is null && options.BenchSeconds <= 0)
            return Fail("--cancel-scan-at works only with --capture or --bench.");

        if ((options.TargetRock || options.ModuleRun is not null) && options.CapturePath is null && options.BenchSeconds <= 0)
            return Fail("--target-rock and --module work only with --capture or --bench.");

        if (options.GasFixture > 0 && (options.CapturePath is null || options.BenchSeconds > 0))
            return Fail("--gas-fixture works only with --capture, without --bench.");

        if ((options.GasDistance is not null || options.GasDistanceTo is not null) && options.GasView is not ("outside" or "absent"))
            return Fail("--gas-distance works only with --gas-view outside or absent.");
        if (options.GasDistanceTo is not null && options.GasDistance is null)
            return Fail("--gas-distance-to needs --gas-distance.");
        if (options.GasView is not null && (options.CapturePath is null && options.BenchSeconds <= 0))
            return Fail("--gas-view works only with --capture or --bench.");

        if (options.BenchResizes > 0 && (options.BenchSeconds <= 0 || options.VisualBenchmark is not null))
            return Fail("--bench-resize works only with --bench, without --visual-scene.");

        if (options.BreakShieldSeconds is not null && (options.CapturePath is null && options.BenchSeconds <= 0 || options.JoinHost is not null || options.Hosting.Visibility != ServerVisibility.Private || options.WorldsRoot is not null))
            return Fail("--break-shield works only with --capture or --bench, in an unsaved private local game.");

        if (options.HurtFleetSeconds is not null && (options.CapturePath is null && options.BenchSeconds <= 0 || options.JoinHost is not null || options.Hosting.Visibility != ServerVisibility.Private || options.WorldsRoot is not null))
            return Fail("--hurt-fleet works only with --capture or --bench, in an unsaved private local game.");

        if (options.RtsSeed is not null && options.RtsScript is null) return Fail("--rts-seed requires --rts-script.");
        if (options.RtsScript is not null && (options.CapturePath is null || options.BenchSeconds > 0 || options.JoinHost is not null || options.Hosting.Visibility != ServerVisibility.Private))
            return Fail("--rts-script requires --capture and private local play, without --bench.");

        if (options.VisualBenchmark is not null && (options.BenchSeconds <= 0 && options.CapturePath is null || options.JoinHost is not null || options.Hosting.Visibility != ServerVisibility.Private))
            return Fail("--visual-scene requires --bench or --capture and private local play.");

        // A sample stands in for the account service only where nobody can act on it.
        if (options.AccountSample is not null && options.CapturePath is null && options.BenchSeconds <= 0)
            return Fail("--account-sample works only with --capture or --bench.");

        // The Controls screens take their search, tab and sample from the command line only in a scripted run.
        if ((options.ControlsSearch is not null || options.ControlsTab is not null || options.ControlsSample is not null)
            && (options.OpenView != "controls" || (options.CapturePath is null && options.BenchSeconds <= 0)))
            return Fail("--controls-search, --controls-tab and --controls-sample work only with --open controls and --capture or --bench.");
        if (options.ControlsSample is not null && options.StartScreen != Screen.Settings)
            return Fail("--controls-sample works only with --start settings.");

        // Tips show in a scripted run only where the run asks for them; the reset question lives on Settings.
        if (options.TipsSample is not null && options.CapturePath is null && options.BenchSeconds <= 0)
            return Fail("--tips-sample works only with --capture or --bench.");
        if (options.TipsSample == "reset" && options.StartScreen != Screen.Settings)
            return Fail("--tips-sample reset works only with --start settings.");

        // The delete views act on a folder of worlds of the capture's own, never on the player's.
        if (options.OpenView is "delete-world" or "delete-incompatible" or "deleted-incompatible"
            && (options.StartScreen != Screen.LoadWorld || options.CapturePath is null || options.WorldsRoot is null))
            return Fail("--open delete-world, delete-incompatible and deleted-incompatible work only with --start load, --capture and --worlds.");

        // A broken store is for captures of what the player is told, in a folder of the capture's own.
        if (options.SaveFault != StoreFault.None && (options.CapturePath is null || options.WorldsRoot is null))
            return Fail("--save-fault works only with --capture and --worlds.");

        // The New Game screen shows the hosting --host asked for and lets the player start it from there.
        if (options.StartScreen == Screen.NewGame)
            options.StartLocalGame = false;

        // A diagnostic run is an unsaved private local game started at launch: it never lists, opens, creates or
        // saves a world, and never leaves to the menu, so its measurement ends with its own world.
        var diagnostic = options.RtsScript is not null || options.VisualBenchmark is not null;
        if (diagnostic && (options.WorldsRoot is not null || options.ContinueWorld || options.SaveFault != StoreFault.None || options.OpenView == "leave"
            || options.StartScreen != Screen.MainMenu || (!options.StartLocalGame && options.BenchSeconds <= 0)))
            return Fail("--rts-script and --visual-scene need a new private local game (--start local or --bench), without --worlds, --save-fault, --start continue, load or newgame, or --open leave.");

        if (options.ChartedRadius > 0 && ((options.CapturePath is null && options.BenchSeconds <= 0) || options.WorldsRoot is not null))
            return Fail("--charted works only with --capture or --bench, without --worlds.");

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
