# PROSPECT: notes for agents

A multiplayer space sandbox in C# on .NET 10, built on the BRUTAL framework (Vulkan, GLFW, Dear ImGui, RakNet).
`README.md` describes what exists, the controls and the layout. This file is how to work on it.

## docs/

Read `docs/*.md` before designing or building a feature. They say where the game is going; this file says how
to work.

- `docs/direction.md`: what the game is, what is decided, what is still open, networking goals.
- `docs/blocks-at-scale.md`: the plan for ships of thousands of blocks: data, network, collision, drawing, stages.
- `docs/listing-server.md`: the listing server: its HTTP contract for the game side, configuration, deployment.
- `docs/hosting.md`: visibility, hosting a local game, the password handshake, the game's side of the listing server.
- `docs/releasing.md`: release tags, the workflow, the container images and how to run them.
- `docs/save-foundation.md`: saved worlds: the snapshot, the store contract, limits, the world clock, closing.
- `docs/world-generation.md`: fields and clouds in world space: anchors, which sector takes a rock, the gas query.
- An item under "Open" is not decided. Ask; do not guess and build.
- When a decision is made in conversation, move it from "Open" to "Decided" in the same change.

How to write docs here. The reader has ADHD:

- Lead with the point. One idea per line. Short sections.
- Lists of five or fewer. Split longer ones.
- Separate decided from open. Never mix them.
- Leave nothing important out, and add nothing that is not needed. No essays, no history.

## Commands

Use the Makefile, not raw `dotnet` commands.

| Command | Use |
| --- | --- |
| `make setup` | First run on a machine: system packages for the OS (Vulkan; MoltenVK on macOS), the .NET SDK into `~/.dotnet`, restore. `make doctor` checks the toolchain and GPU. |
| `make check` | Formatting, warnings-as-errors build and all tests. Must pass before work is called done. Takes about two minutes. |
| `make bench` | Release benchmark, twice: an empty scene, then the same scene while the window is resized (`--bench-resize`). Fails under 144 fps or on any managed allocation in the frame loop. Run it after touching anything that executes every frame or every tick. |
| `make run ARGS="..."` | The client. `make server` is the dedicated server. |
| `make listing` | The listing server on 127.0.0.1:8080. `make listing-image` builds its container. |
| `make server-image` | The dedicated server's container image. |
| `make fmt` | Format. `make check` only verifies. |
| `make tools` | Fetches Vulkan validation layers into `.tools/` (not installed system-wide on the dev machine). |

Run one test class or method with the test binary directly:
`dotnet artifacts/bin/Prospect.Tests/debug/Prospect.Tests.dll -class Prospect.Tests.JumpTests` (or `-method <full name>`).
Always wrap long test runs in `timeout`; a session test that waits on the simulation can otherwise sit for minutes.
A test that code takes no memory measures through `AllocationProbe` (`tests/Prospect.Tests`), never
`GC.GetAllocatedBytesForCurrentThread` directly: a collection pause adds up to 8 KiB the code never took, and the
probe measures such a stretch again. Measure a loop an iteration at a time; a step that happens once goes in a
scenario run with `Run`, `Start` and `Stop`. `TEST_ENV` in the Makefile keeps collections, and so repeats, rare.

## Rules that are not negotiable

- **One simulation.** Singleplayer is a server in the game process joined by an in-process link. Never add a code
  path that only exists offline.
- **The server decides.** Damage, orders, ownership, block state, asteroid motion and jumps are computed on the
  server. The client predicts its own hull with the *same* shared code (`HullSim.Step`) and is corrected by
  snapshots. Anything the client sends is validated (`NetReader` never throws; check `IsValid`).
- **Shared code lives in `Prospect.Game`.** If the server and the client must agree on it (flight, collisions,
  galaxy generation, asteroid shapes), it is one function there, not two copies.
- **No allocation in the frame loop or the tick.** No LINQ, closures, `string` formatting, boxing or `new` on hot
  paths. Format interface text with `Kit.F($"...")`, never a bare interpolated string into ImGui.
- **The render thread never owns game state.** It reads `SessionSnapshot` and sends intent through
  `PlayerInputBridge`. It does not touch simulation objects.
- **Positions are `Double3`.** Convert to single precision only relative to the camera.
- **Content is data.** Blocks, hull designs, galaxy rules, resources, bindings and tuning numbers live under
  `content/`. Do not hard-code a figure that a designer would want to change.
- **No stubs.** A feature is wired end to end and tested, or it is left out and named as missing in `README.md`.

## When you change something

- **Social messages:** validate new kinds after admission and the receive/rate budgets; publish through `SessionSnapshot.Social` and send intent through `PlayerInputBridge`.
- **Wire format:** bump `ProtocolInfo.Version` in `Protocol/Messages.cs`, and update `MaxWireSize` constants.
- **The handshake or the password:** the scheme is written down in `Passwords` and `docs/hosting.md`; change
  both, bump `ProtocolInfo.Version`, and keep `HostingTests` passing. Keep the first field of `Hello` and of
  `Reject` where they are, so two versions can still tell each other they differ. A proof is checked once per
  connection, and never while the address is locked out.
- **Content both sides simulate from:** a new file goes through `Shared(...)` in `GameContent.Load`, which puts
  it in the content identity; list it in `docs/hosting.md` and give `ContentIdentityTests` a case. Bump
  `ContentIdentity.EvaluatorVersion` when the arithmetic that turns blocks into figures (`BoxHullSums`, size
  rules, `FlightRules.Under`) changes what comes out while the files stay the same.
- **What a server does before a player is let in:** the limits are in `ConnectionLimits` and
  `docs/hosting.md`; change both. Nothing may be read, allocated or logged for a connection before the limits
  are checked, and a refusal never allocates. `ConnectionLimitTests` runs a server by hand (`ScriptedServer`:
  the test plays the network and moves the clock), so a change must pass there without any waiting.
- **Anything that talks to the listing server:** it goes through `ListingHttp` (time-out, size limit, no
  redirects) and runs off the tick and the frame loop. Nothing the service sends is trusted. `ListingTests` runs
  the real service in-process, so a change to the contract on either side must pass there; `Prospect.Tests`
  therefore builds `Prospect.Listing` and needs the ASP.NET Core runtime.
- **Accounts and friends:** `AccountClient` owns the token in memory only; the interface reads its immutable
  `View`. The sign-in comes back through `LoopbackReceiver` on 127.0.0.1, never a polled link. Contract and rules
  in `docs/accounts.md`. `AccountClientTests` signs in through the real service and a fake Discord, browser
  steps included; a change on either side must pass there.
- **Anything sized by the window or the swapchain:** a resize, a fullscreen toggle and a swapchain that went out
  of date are rebuilt inside the frame loop (`App.Run`: `FrameRenderer.Recreate`, then `SceneRenderer.Resize`),
  so a rebuild takes no managed memory. Keep what there is one of per swapchain image in arrays that outlive the
  rebuild, sized by `Swapchain.ImageCapacity`; log nothing above debug level there; a native import first called
  there must be in an assembly `NativeBindings.Prelink` is given. `--bench-resize <n>` (the second run of
  `make bench`) resizes the window n times and rebuilds both at the same size n times while measuring; a window
  manager that keeps the window's size is reported, and the same-size rebuilds still count.
- **Shaders:** files in `assets/shaders/` are compiled at startup. Check them first with
  `glslc -I assets/shaders assets/shaders/<file> -o /dev/null`. `frame.glsl` must match `FrameConstants` field for
  field.
- **The pointer, the mouse or the order of a client frame:** `PointerRouter` decides capture, quarantines a
  poll's mouse when the owner changes, then starts the interface frame and reads ownership, all before the world
  reads input, and checks capture again after the world's update, since the command key and taking control
  change the view there; `UiPointerInput` is what Dear ImGui is told. Only the pilot view captures the pointer.
  `FrameOrderTests` runs that frame headless: real devices, router, `WorldView` and a Dear ImGui context, events fed as a poll delivers them. Dear ImGui has one
  global context, so keep every test that uses it in that class.
- **Anything that covers the whole window with opaque interface** (today only the galaxy map, once its fade-in
  ends): `ScreenHost.CoversWorld` is set by the surface itself (`WorldCover.MapHides`), and `App.Run` then skips
  `WorldView.Draw` and `SceneRenderer.Present` (`WorldCover.Draws`), so scene, gas, shields and tone mapping cost
  nothing while it is open. Only drawing is skipped: the update, prediction, streaming and snapshots run. Keep
  everything the world needs from a frame in `WorldView.Update`, never in `Draw` (shield ripples and the rock
  frame already are). A surface that dims the world instead (menus, controls, world settings) must not set it.
  A covering surface must be opaque over the whole display from the first frame it sets the flag. Check with
  `--bench 8 --open map --sector -66,1,37` (the report names the frames skipped) and
  `--open map --close-map-at 2.0 --capture f.png --capture-after 1.5 --capture-count 12 --capture-every 0.12`, which
  captures the map and then the first frames back in the world. `WorldCoverTests` holds the rule.
- **The galaxy map and exploration display:** it shows only what `SessionSnapshot.Exploration` and the owned
  hulls say; never draw an uncharted sector's name, star, fields or resources. Its rules live in font-free
  helpers (`MapExploration.cs`: `MapCardState`, `MapSectorCard`, `MapLayerBar`, `MapNavigation`,
  `MapProjection`), which `FrameOrderTests` drives with the production `MapPointer`; a window over the map
  blocks its rectangle before `MapPointer.Begin` and calls `Claim`. A hover preview card is never a window.
  Figures are in `content/visuals/map.json` (`MapTuning`). Field and cloud outlines come from `MapFootprint`
  (shared) through `MapFeatureCache` (worker, cached; the frame only projects and draws, clipped to the charted
  sector's cell); the legend is `MapLegend`, a surface the map blocks; the hull name label is `MapUnitHover`;
  the pinned card's X is `MapSectorCard.Close`. Rules in `docs/roadmap/exploration.md`. Check with `--open map-card`, `map-card-near`, `map-card-far`, `map-tilt`, `scan`
  and `command-fog`.
- **The ship editor:** the limits are in `EditorRules`, `content/net/editor.json` and `docs/hosting.md`;
  change all three. A design reaches the catalogue only through `DesignBytes.Decode` on a worker, the catalogue
  only grows (`DesignCatalogue.Append`, `GameContent.WithDesign`), and a hull never changes design: it is
  replaced. A client is sent hulls of a new design only after `DesignReady`; never use `CatalogueReady` for
  that, it stops everything else the client sends. A repair (`RepairEdit`) replaces the hull by the design it
  already has: same number, nothing appended; `HullDesign.NeedsRepair` is the one test of damage for both
  sides. A design need not fly: `DesignBytes.Decode` checks structure and finite figures, not capability, so
  shared flight code must stay right at zero thrust, torque and power. `EditorSessionTests` ticks a server and clients by hand and
  says a mesh is installed through `PlayerInputBridge.ReportInstalledDesigns`, as the renderer does. The
  client side is `ShipEditor` (state, worker check of a frozen `DraftCopy`, never the live draft), its keys in
  the shortcut phase (`ShipEditor.HandleKeys`) and the view in `WorldView`; `FrameOrderTests` drives it through
  a real server, headless. The draft opens as the full blueprint (`BuildDraft.FromBlueprint`); lost blocks are
  matched to draft boxes by value, not index. Selection, move, paste, merge and delete go through
  `ShipEditor` and the draft's group edits, one undo step each; any edit that shifts indices resets the
  selection itself. A press begun on a panel never reaches the world, except a part card's drag
  (`EditorPalette.Card` hands it over, `WorldView.HoldsInterfacePress`, released by the raw button over
  `PointerRouter.PointerOverWindow`); add no other. A mouse choice on the part list gives the keyboard back to
  the world (`EditorPalette.HandBack`), so the ghost shows without a click away. A turn with `R` is never
  confirmed: a press on the ghost ends it and becomes placing's press (`WorldView.UpdatePartRotation`). Warning thresholds and the part tabs are client data in
  `content/visuals/editor.json`; warnings never block Apply.
- **The New ship dialog and the drone's keys:** `NewShipDialog.HandleKeys` is the one place the editor key is
  read in the shortcut phase (`ScreenHost.HandleEditorShortcut` only calls it): in the escape drone
  (`SessionSnapshot.OwnIsDrone`, from the snapshot header's flag, protocol 42) the plain key opens the dialog and
  fires `TipTriggers.DroneEditHint` every time, `Ctrl` with it edits the drone through `ShipEditor.Request`; in a
  ship, and with an edit open, it is `ShipEditor.HandleKeys` as before. The dialog is a surface:
  `WorldInputEnabled` is false while it is open, so the pointer is shown and no click or flight key reaches the
  world, and it keeps the caret in the name field (`NewShipPanel.DrawBody`). Enter and Escape are read in the
  shortcut phase, never from the field; a tip's Escape comes first (`_tipBack`). The name is checked as typed by
  `NewShipDialog.Check`, the server's own rules (`ShipName`); a server refusal comes back through
  `SessionSnapshot.NewShip` (`Serial` above the sent one) and is shown in the dialog. Cancel and Escape do nothing
  while the server works (`Busy`), so a grant is never left unclaimed. A created ship is handed to the editor
  (`ShipEditor.AwaitCreated`), which opens only when that hull is in the snapshot, and then tracks the *granted*
  hull, not the flown one: `WorldView.EditedPosition`, `EditedOrientation` and `EditedDesign` are the pose and
  design the camera, the pointer ray and the draft work from; never read `Own*` in editor code.
  Closing the edit of a ship just built, by Apply or Cancel only, hands control to it (`ShipEditor.FollowHull`,
  taken by `WorldView.FollowBuiltShip` with the usual control request; a refusal is `ShipEditor.FollowNotice`).
  No other edit, and no edit the server ended, ever does. A new ship's
  draft is never kept for "press the key to go on". `FrameOrderTests` runs the dialog headless with the
  production keys and panel helper through a real server, and `EditorSessionTests` has the flag through a session.
  Check with `--open new-ship`, `new-ship-refused` and `new-ship-editor`.
- **Saved worlds:** the contract is in `docs/save-foundation.md`; change it with the code. State the server
  must not lose goes into `WorldSnapshot`, its codec (bump `WorldSnapshotCodec.Version`), `ServerHost.Capture`
  and `RestoreWorld`, with a load rule for it; `SaveLifecycleTests` saves a world with one of everything and
  loads it back through an in-memory store. Anything that makes a saved world grow asks `HasRoom` first. The
  tick never touches the store and takes no memory to capture; the store is used by `SaveWorker` alone. A hull
  created on the server goes through `CreateHull`, and a connection is seated with `SetHull`. Never let a
  remote connection in as an owner by a name, an address or an account the server did not verify. The store on
  disk is `SqliteWorldStore`: the lease before anything is opened, a read-only look before the writing
  connection, `Failed` only when it knows nothing changed, and nothing after a commit may report it undone.
  A new SQLite package means reading what changed and updating `AuditedSqliteVersion` and its source id
  together; `SqliteWorldStoreTests` runs on a real disk and in a second process, so keep it passing there.
- **Deleting saved worlds:** only `WorldLibrary.Delete` (via `WorldShelf.Delete`, on a worker); the contract is
  "Deleting worlds" in `docs/save-foundation.md`. Take the lease, rename `world.db` to its tombstone, then remove
  files, so a crash never leaves a world the library lists; `RecoverDeletions` finishes the rest. Never follow a
  link, never delete a folder without a `world.db` or tombstone, never delete a leased world. `WorldDeletionTests`
  holds the rules; its `WorldDeletion.Trace` hook makes a step fail.
- **Opening, leaving or replacing a session:** only through `SessionLifecycle` (`App.Lifecycle`): close with
  `BeginClose`, stop reading the session, dispose it on a worker, and open the next only after that, so a world's
  lease is free. Disk and network work for it runs on workers (`LocalWorlds`, `WorldShelf`), never on the frame.
  A close that is not clean is reported (`CloseReport`) and holds back what was to follow, quitting too. An open
  takes a cancellation token and stops before the disk and before the server starts; whatever an abandoned open
  still makes is closed by its own cleanup. `--save-fault slow|fail|unknown|degraded` with `--open leave`,
  `--capture` and a scratch `--worlds` captures the saving screen and the reports. `SavedWorldPlayTests`
  runs it against real servers on a temporary folder.
- **A jump offered anywhere in the interface:** the command view's right-click menu (`WorldView.CourseMenu`),
  the map's course menu and the pinned sector card all read one `JumpPlan` (`WorldView.PlanJump`, which calls
  `JumpRules.Evaluate` per selected ship) and one set of rows (`ScreenHost.DrawCourseRows`); never work out range or
  risk a second way. The ship being flown is only plotted (`PlotJump`, answered through
  `Exploration.Navigation`); other owned ships get `IssueFleetOrder`. A right click in the command view opens the
  menu and orders nothing; Escape (`ScreenHost.NextBackStep`) and a press elsewhere close it. `FrameOrderTests` runs
  it. Do not build text inside another `Kit.F`: build the inner figures first.
- **In-game tips:** text, anchor and trigger of every tip are data in `content/visuals/tips.json` (`TipCatalogue`:
  refuses unknown triggers, actions, repeated ids and a trigger with no tip). Key names in text are `{InputAction}`
  placeholders, resolved from the loaded bindings. A tip belongs to its surface: `TipSurfaces` fires the map's, command view's and editor's tips when each opens and
  withdraws them (`TipBoard.Withdraw`, nothing hidden) when it closes, so a map tip never waits behind the command view's. Code only fires a trigger from `TipTriggers`: one call,
  `ScreenHost.FireTip(TipTriggers.X)` (or `TipBoard.Fire`); new moment = new constant, new tip entry. The rules
  (stack, Next, Close, tick) are `TipBoard`, font-free; the panel is `TipPanel`, placed by `TipPlacement` before the
  map and panels are laid out. Hidden ids are `ClientSettings.HiddenTips`, saved by the client when they change.
  The panel is one window without keyboard navigation and takes no input while the pilot pointer is captured; it
  must never change `PointerFrame`. A surface under it that does its own pointer routing blocks its rectangle (the
  map: `BlockMapForTip`, `Claim`). Escape goes to `HandleTipShortcut` first; every other Escape reads
  `BackPressed()`, which stays quiet on that press, and the editor's shortcut skips the frame. A scripted run shows
  no tips unless given `--tips-sample`. `TipTests` and the tip tests in `FrameOrderTests` hold the rules.
- **A new per-tick system:** add a unit test in `tests/Prospect.Tests/WorldTests.cs` and, if it crosses the
  network, a session test that runs a real server and client.
- **A new sector module** (stations): derive from `SectorModule` in `Prospect.Game/World`, name its
  type in `SectorModule.Read`, and list it under `sectorModules` in `content/galaxy/galaxy.json` with its own
  `id`. Draw only from `Stream(sector, purpose)`. Give `SectorSummary` and `SectorContent` the fields it fills.
- **Anything that lies across sector walls** (fields, clouds): anchor it in the sector its centre lies in, keep it
  within one sector of that anchor, and draw it from the anchor's streams (`Stream(galaxy, coord, purpose)`),
  so a sector finds it among its 27 without generating a neighbour. Work positions out in the anchor's frame,
  and give each thing a stream of its own (`SectorRandom.Numbered`): two sectors must get the same numbers and
  exactly one takes each thing. A rock stays wholly inside its sector. `WorldSpaceGenerationTests` holds the
  rules; `docs/world-generation.md` has the design.
- **World generation:** it must come out the same to the last bit on every machine. Plain arithmetic in a fixed
  order and `DetMath`; no `MathF.Exp`, `Pow`, `Sin` or `Vector3.Length` where a discrete choice hangs on the
  result. If what a seed generates changes, bump `ProtocolInfo.Version` and `Galaxy.GenerationVersion` (saved worlds
  name asteroids by index) and record the new hash in
  `GalaxyTests.GenerationMatchesTheRecordedGalaxyOnEveryMachine`. Retuning `galaxy.json` needs neither: that test
  runs on a frozen copy in `tests/Prospect.Tests/fixtures`.
- **A new hull design:** add it to `content/ships/fleet.json`. Stats are derived from blocks; do not add stat
  fields to the design file.
- **A new world setting:** one entry in `content/rules/settings.json`, its number applied in `FlightRules.Under` (flight) or `Galaxy.Under` / `AsteroidFieldsModule.Under` (generation),
  a unit test in `WorldSettingsTests.cs`, a row in the README table. Read setting ids only in those shared application paths, never in generation or simulation loops.
- **README:** keep the Status and Controls sections true. Say what is not there.

## Checking the client without a person at the screen

The client opens a real window on the developer's desktop, and they are usually working on that machine.

- Only launch it with `--capture <file.png>` or `--bench <seconds>`. Both mute every input device, so the window
  cannot grab the pointer or react to their typing. Never start it any other way from an agent session.
- Such a run gives its window the Wayland app id and X11 class `agent.prospect` (`App.ScriptedAppId`) and the
  title `PROSPECT capture`; a manual run is `prospect` / `PROSPECT`. Keep them: the developer's compositor routes
  `agent.*` windows to a hidden workspace. The hints are set before the window maps; never rename it later.
- **Before the first capture on a machine, check the window is hidden.** On Hyprland the rule lives in the
  developer's own config (`~/.config/hypr/windows.lua`: class `^agent\\..*` to `special:agents silent`); check it
  is there, and smoke-test with `foot --app-id agent.test sleep 5` and `hyprctl clients -j`. A window on a hidden
  special workspace still renders, and a capture still saves its image (scripted runs present without vsync).
  Do not create a headless output: a monitor manager such as hyprmoncfg adopts it and moves the developer's
  workspaces. Without such a rule (GNOME, macOS, Windows), tell the developer windows will appear first.
- `make run ARGS="--start local --open <view> --capture /path/shot.png --capture-after 2.5"` saves a screenshot
  and exits. Views: `command`, `edit` (asks to edit the own ship; the editor opens on the grant), `drone` (steps out of the hull into the escape drone), `map`, `map-hover` (the map with the pointer on your own sector, a preview card), `map-card` (your own sector's card pinned), `map-card-near` (a sector 144 km away: a blind jump with risk) and `map-card-far` (460 km: out of drive range), `command-menu` (the command view's right-click menu on a blind jump 60 km ahead for the ship being flown: Plot jump), `command-menu-fleet` (the same for the other ships: Jump here), `command-menu-close` (3 km, too close) and `command-menu-far` (450 km, too far), `new-ship` (leaves the pod in the drone and opens the New ship dialog empty; `--tips-sample on` shows the drone-edit tip beside it), `new-ship-refused` (the same with a refusal shown, put there by the hook rather than sent by a server) `new-ship-editor` (builds a ship through the real dialog and server: the editor opens on its single block, drone still flown; capture after 4 s) and `new-ship-closed` (the same, then cancels that edit: you fly the new ship; capture after 6 s), `map-tilt` (the same, tilted), `map-reach` and `map-reach-tilt` (zoomed in on the own position to show the shape of sensor reach), `scan` (pinned card and a scan started: capture after 2.5 s for progress, after 7 s for the charted result; add `--map-cell 190` to see field and cloud outlines, `--map-tilt`, `--map-hover` to rest the pointer on the own ship), `command-fog` (the command view pulled back to show sensor rings and fog, all owned ships selected), `command-fog-one` (only the flown ship selected), `command-fog-pair` and `command-fog-far` (it and another ship that jumps 6 km or 14 km away along -x, selected; capture after 28 s), `galaxy`, `info`, `jump`, `jump-wormhole`, `jump-rift`, `world-settings`, `menu`,
  `players`, `social`, `friends`, `chat`, `hud` (the world with the party list and recent chat only). The social views and
  `hud` leave the performance overlay off. Screens
  without a session: `--start menu|join|settings|newgame|credits|account|load`; `--set id=value` shows on `newgame`, and so
  does `--host public --host-name ... --password ...` (with `--start newgame` it fills the hosting section
  instead of starting a game; the password shows masked). `--open menu` opens the in-game menu, which says
  whether a hosted public game is listed.
- Tips in a capture: a scripted run hides them. `--tips-sample on` lets them fire as in play (`--open map`,
  `--open command`, `--open edit`, `scan`, show their tips); `stack` also fires the command view's three at once
  (use with `--open command`); `reset` with `--start settings` opens the Reset tips question, scrolled into view. Point
  `XDG_CONFIG_HOME` at a scratch copy: hiding a tip saves settings.
- Accounts without the network or a browser: `--account-sample <state>` (`signed-out`, `waiting`, `signed-in`,
  `friends` with full lists of hostile names, `failed`, `off`), with `--start account` for the Friends screen or
  `--open friends` in a game. Only with `--capture` or `--bench`; a scripted run never opens a browser.
- The server browser without the network: `--start join --browser-sample <state>`, with `ready` (a fixed list:
  a name with `##`, `###` and `%`, a 48-character name, one in Japanese with a symbol the fonts lack, a password, a full game
  and one entry the browser drops), `loading`, `failed`, `empty`, `off` or `password` (the list with the
  password prompt open). The list goes through `ServerBrowser.Parse`; its addresses are documentation ranges.
- The password prompt after a join: run a dedicated server with a password
  (`PROSPECT_PASSWORD=... make server ARGS="--port 27441 --listing off"`), then
  `--join 127.0.0.1:27441 --capture ...` ("password required"), the same with a wrong `PROSPECT_PASSWORD`
  ("wrong password"), and four or more wrong ones within a few seconds for the countdown.
- A short window: point `XDG_CONFIG_HOME` at a copy of the settings with `"UiScale": 2`; every layout must still
  hold.
- A sequence: add `--capture-count <n> --capture-every <sec>` for numbered stills (`shot-00.png`, ...); each one
  prints the own hull's jump stage and progress. Preparation takes at least 12 s, the default transit about 10.2 s,
  then 1.5 s of arrival. `--open jump --capture-after 6 --capture-count 70 --capture-every 0.3` covers the default jump.
- Another player's jump: run a dedicated server with `--listing off`, join it with a capturing client, then join a
  second client with `--open jump-fleet` (and a late `--capture` to keep it connected): its mining vessel and
  survey vessel jump 6 km along -x. An observer needs sensor coverage at each endpoint to see departure and arrival;
  the default 5 km sensor at the starting pod cannot see the arrival. The second player starts 160 m along -x
  of the first; `--look` points the watcher at them.
- Social data for a capture: run a dedicated server with `--listing off`, join it with the capturing client,
  and run headless clients against it (`--social-run 40 --join ... --player <name>` with `--invite <name>`
  (repeatable), `--accept-invites`, `--say`, `--party-say`). `--accept-invites` on the capturing client makes it
  accept, so the party list fills. Point `XDG_CONFIG_HOME` at a scratch copy of the settings: a capture saves
  them on exit.
- Own scripted jumps aim at the centre of the current sector plus (6,0,-4), about 288 km from its centre,
  within the production 5–400 km range even with `--sector`. `--jump-to x,y,z` selects an absolute sector centre;
  use `--sector` to start within range. An invalid explicit destination is logged and not ordered, never silently replaced.
  `--open jump-fleet-ahead` targets points 6 km and 10 km ahead of and above the camera; `jump-fleet` targets 6 km along -x.
  These replace the old sub-minimum-distance capture jumps. Their openings are farther from the watcher.
- `--look <degrees>` holds the pilot camera that far to the right of the hull's nose, as free look would (`0`
  looks along the nose, `150` nearly back). Use it to check that an effect follows the hull, not the view.
- `--target` targets the nearest other hull once in space (in a local game the Prospector, about 120 degrees to
  the right of the nose). `--open menu` opens the in-game Esc menu. `--look-to <degrees>` with `--look` turns the
  camera evenly from one angle to the other over the captures: `--target --look 40 --look-to 130
  --capture-count 16 --capture-every 0.12` brings the target into view from the right edge, and `--look -90
  --look-to -30` sweeps it past straight behind.
- Deleting worlds in a capture: `--start load --worlds <scratch copy> --capture ...` with `--open delete-world`
  (confirmation for the first world listed), `--open delete-incompatible` (confirmation for every OTHER VERSION
  world) or `--open deleted-incompatible` (confirms it, shows the list after). They need `--capture` and
  `--worlds`, and really delete: always use a scratch copy. Make an incompatible world by playing one into the
  scratch folder, then `sqlite3 <folder>/world.db "update world set generator_version=generator_version+1"`
  (or `pragma user_version=999`); a damaged one is a folder whose `world.db` holds garbage.
- Saved worlds in a capture: a capture or a benchmark saves nothing and lists no world unless given
  `--worlds <scratch folder>`; never point it at the player's own. With one, `--start local` creates a world there
  (named by `--host-name`), `--start continue` opens the one played last and `--start load` shows the list. A
  capture's quit closes and saves the world first, as the player's would.
- Captures time themselves by the wall clock and present without vsync, so they also run while the display is
  asleep.
- `--host unlisted|public` hosts the local game for others and opens UDP 27420 on the developer's machine; add
  `--host-port <n>` to stay clear of a server running there, and `--listing off` unless the capture is about
  the list. `--bench` is always a private game.
- Joining (`--join`) and hosting unlisted or public (`--host`) need a real Discord sign-in, which a capture
  cannot make: such a capture stops on Join Server or New Game with the sign-in shown. `--account-sample
  signed-in` never counts as one. Private local games are unaffected.
- `--sector x,y,z` starts the local game in that sector instead of the home sector; it works with `--capture`
  and `--bench`. With the default seed: `-40,0,-29` is dense (about 6,800 rocks of five fields), `8,0,-8`
  is very rich with five resources, a large field lies across the wall between `2,0,1` and `3,0,1`, and `3,0,1`
  has a giant 8 km across. Gas: `127,-1,-127` (ordinary cloud around arrival, rocks in it), `133,0,-77` (huge
  cloud around arrival), `-66,1,37` (huge, with rocks).
- The window size is whatever the tiling compositor gives it; check with `magick identify` before cropping.
- Debug builds run with validation layers when `.tools/` exists. Any validation message is a bug.
- What cannot be checked this way and needs the developer: how flight feels, mouse and keyboard interaction,
  gamepad, interactive resize, Windows.

## BRUTAL pitfalls already hit

- `Brutal.ImGuiApi.Backends.GlfwBackend` allocates every frame. The client uses its own `UiPlatform`.
- `ImString` interpolation (`ImGui.Text($"...")`) allocates. Use `Kit.F`.
- `ShaderStages` destroys its modules when disposed: create a fresh module per pipeline.
- `JobScheduler` only accepts work while idle. `JobSystem` in `Prospect.Engine` adds dependencies and
  per-runner dispatch on top; schedule through that.
- The framework source is the documentation. Clone `https://github.com/ahwootech/BRUTAL` and grep it rather than
  guessing a signature.
- Licence: the BRUTAL logo shows before gameplay, its copyright is on the credits screen and `NOTICE.txt` ships
  beside the binaries. Do not remove any of the three.

## Gas clouds

- See `docs/gas-clouds.md` for the bounded visual pass and muted capture scenarios. Gas has no wire field. Some clouds hold asteroids, so a change to where clouds are changes asteroid lists: bump as for any generation change.
- A cloud is anchored in the sector its centre lies in and may reach the sectors around it. `SectorContent.GasCloud` is the anchored one; `GasCloudModule.Intersecting` lists every cloud that reaches a sector or a sphere.
- Generation keeps clouds apart (`GasCloudModule.SeparationMargin`), so none overlap and a camera is never in two. Many show at once from afar, but composition with shields and openings takes only the nearest. Lift the rule in `GasCloudModule.TryGetCloud` only together with the renderer's ordering of overlapping clouds.
- Gas uses its RGB-only pass without portals or shields; frames with either defer bounded gas segments to HDR composition. Preserve the gas-free shield copies, frame.glsl layout, and per-sample own-passage checks. The fixed 1 MiB noise lattice is renderer-owned, immutable, and independent of resize.
- Far gas: three levels of detail by distance in cloud radii, figures in `content/visuals/gas.json` (`GasTiers`). Which clouds exist comes from `IGasCloudSource` only (today `GeneratedGasClouds`: `GasCloudQuery.Collect` over `GasCloudModule.TryGetCloud`, by anchor sector); never read clouds from sector slots. Composition takes one cloud, the primary (`GasQueue`); every other draw is in the scene or, at the far level, in the sky. A new level or draw order needs a case in `GasLevelsTests` and a `--gas-distance` sweep capture. Large clouds (x half-extent over 7 km) gain strands, bent steps and clearing near the camera (`closeUp` in the same file, `gasStrands` in `gas.glsl`); an ordinary cloud must keep taking the unbent expressions exactly, so check fixtures 1–13 after touching the march.

## Known limits to design around

- Runtime designs obey shared surface/work and catalogue budgets; see docs/blocks-at-scale.md.
  The old 64-block and unit-cell restrictions are removed; 150,000-block play remains future work.
- A server's design catalogue only grows: an edited ship takes a design number (255 at most) and catalogue
  bytes for as long as the server runs, and a full catalogue refuses every new design. The editor screen has no
  shapes other than boxes and wedges, mirror, stock parts or drag handle on a placed block yet; a placed block's resizing is numeric.
- Snapshots are full state, not delta-compressed. Personal sensor filtering is the first interest management; see docs/hosting.md for disclosure and scale limits.
- Server charts persist per owner. The existing map discovery UI remains session-local until it consumes SessionSnapshot.Exploration.
- Asteroids do not collide with each other.
- A shield shell is the smooth closing of the blocks (`ShieldSurface`), indexed, at most 32,768 vertices and
  196,608 indices; separate box-shaped parts use triangulated rounded boxes with analytic normals. Admission builds
  the intact shell off the tick and keeps it with the design; a design whose intact shell does not fit is refused.
  Damaged shells are built one at a time on a client worker and fall back to the hull's earlier or intact shell;
  nothing builds a shell in a frame or a tick. Some four hundred or more separate parts do not fit.
- A shield's ripple shows the newest hit a snapshot reports; hits between two snapshots of a remote hull (three
  ticks) show as the last of them, and impacts in one tick are one event. A client keeps four ripples per hull.
- Shields copy the scene's colour and depth once a frame whenever one is drawn, to bend what is behind them, and
  are drawn last in the scene. Overlapping shields each bend that one copy; dust, the command grid and jump marks
  have no depth, so one in front of a shield is bent as if it were behind it. In the pilot's own hyperspace
  tunnel a second copy is taken just before their own shield; where another jump's lens bends that pixel, the own
  shield's light is lifted off and put back at the unbent pixel.
- Hull against hull contact tests every hull against every other each tick, bounding spheres first. It needs a
  spatial index before there are thousands of hulls.
- A sector holds at most 20,480 asteroids (`maxPerSector`; the wire format allows 65,535). An asteroid is named by
  its index in its sector's generated list, so that list can never change for a given seed and build.
- A field runs through a sector wall, a rock does not: its surface stays inside its sector, so giants thin out
  toward walls. Spacing is kept within a sector only, and a sector over its cap drops its smallest rocks while
  its neighbour keeps them.
- A sector's summary estimates its asteroids without placing one. It knows nothing of crowding; the map says
  "ABOUT". Clouds never overlap one another.
- The client draws at most 32,768 instances a frame and shows at most 512 pushed asteroids out of place.
- A pushed asteroid still belongs to the sector it was generated in. One that drifts across the sector's edge is
  not hit by a hull that is wholly in the next sector.
- Box collision prunes a per-design tree with live branch counts and content budgets. Asteroids use nearest-point
  and corner samples against the seeded surface; narrow ridges between samples can be missed. Large-hull tick
  performance is not yet established.
- The client predicts contact only with hulls near its own, and flies only its own fleet's hulls forward exactly;
  another player's hull is assumed to keep its velocity until the next snapshot.
- Snapshot T is state after interval T. Pilot records name their target interval and control epoch.
  Every eligible hull steps once per interval in authoritative slot order; missing controls use dampeners.
  Replay advances intervals, including missing ones. Sparse snapshot health restores isolated prediction block
  states; speculative losses must never mutate published blocks or revision acknowledgements.
  See docs/hosting.md for the wire and scheduling limits.
- A hull knocked back by a hit is not checked against a third hull until the next tick, so the two can overlap
  for one tick.
- A player's hulls are removed when they disconnect.
- Networking is IPv4 only (BRUTAL's RakNet binding), there is no NAT traversal, and game traffic is not
  encrypted: a password protects itself, nothing else.
- A server holds one address to 8 pending connections and to 20 new ones plus 1 a second. Many addresses, or
  forged ones, can still fill the 32 pending slots and RakNet's own connection table, which the game cannot
  see into. Network input over the tick's budget is read a tick late, not dropped.

## Who does the work

Delegate by default. The main model plans, writes the brief, reviews what comes back and integrates it; it does
not do work a cheaper model can do. Pick the lowest tier that fits the task, never a stronger one "to be safe".
Be conservative: Sonnet is very capable and is the default; reach for Opus or Fable only when the table says so.
This adds to the roles in the global instructions (scouting, second opinions); it does not replace them.

| Tier | Model | Effort | Use for |
| --- | --- | --- | --- |
| Scout | Pi DeepSeek Flash, else `claude-haiku-5-5` | low | Finding code, reading BRUTAL source for a signature, gathering context. Read-only. |
| Default | `claude-sonnet-5-5` | medium; high when the task has safety rules (deleting files, saved worlds) | Most work: content JSON and tuning numbers, interface text and layouts, a new screen, HUD element or menu, a gameplay feature that follows an existing pattern (a new order, a map feature, a new block type with its stats), key bindings, README and docs, tests, contained fixes. |
| Hard | `claude-opus-5-5` | high | Only what Sonnet cannot do well: shader effects judged by subtle visual quality, large multi-file refactors that do not change threading or the wire format, or a task Sonnet failed twice. |
| Tough and core | `claude-fable-5-2` | high (xhigh when hard to reverse) | Networking: protocol, prediction, reconciliation, snapshots, interest management. Engine core: threading, the job graph, lock-free hand-off, unmanaged memory. Renderer core: Vulkan resources, frame flow, synchronisation. Simulation determinism. Architecture decisions and anything where a mistake is a desync, a crash or corrupted memory. |

Codex `gpt-6-astra` may take work from any tier that is not graphics or interface, and should when Claude is
short on limits:

- **Yes:** simulation, networking, the server, the listing server, generation, content, tools, CI, tests.
- **Never:** shaders, rendering, screens, HUD, anything judged by looking at it.
- **Effort:** `reasoningEffort` medium for the easy and medium tiers, high for tough and core.
- **Its second opinion comes from Claude** (Opus at high), not from Codex: the reviewer is never the same model
  family as the author.

How to apply it:

- Decide the tier from what can go wrong, not from how long the task is. A long list of JSON edits is easy; a
  three-line change to reconciliation is not.
- If a task is mixed, split it: the tough part to the tough tier, the rest down a tier.
- Escalate one tier when a model fails the same task twice or its result does not pass `make check`. Do not
  start at the top.
- Tasks that run at the same time each get their own worktree, started from a commit.
- Every brief states the goal, the files to read, what is already ruled out, and the checks that must pass
  (`make check`; `make bench` for per-frame or per-tick code). The subagent reports what it verified and what it
  did not.
- The main model verifies before accepting: read the diff, run the checks itself, and for anything visual take a
  screenshot with `--capture`.
- The only work the main model does inline is an edit of a few lines in a file it already has open, where
  writing the brief would cost more than the change.

## Style

- Comments explain why, in full sentences. No commented-out code, no TODO markers.
- Public types and members get a `<summary>`.
- Report results plainly: what passed, what failed, what was not verified.
- Commit and push only when asked.
