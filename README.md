<img src="assets/icon.svg" width="64" height="64" alt="">

# PROSPECT

A multiplayer space sandbox: you command from a pod, mine asteroids, build hulls and run a fleet across star
systems. C# on .NET 10, built on the [BRUTAL](https://github.com/ahwootech/BRUTAL) framework (Vulkan, GLFW,
Dear ImGui, RakNet). Windows x64, Linux x64 and Apple Silicon macOS.

Singleplayer is a server running inside the game process; multiplayer is the same server as a separate binary,
or that local game opened to other players. There is one simulation and one code path.

## Status

Milestone 1 (skeleton) is complete. On top of it, the first slice of the world is in:

- **Sectors.** The galaxy is a grid of 40 km cells generated from the server's seed: name, star, character and
  contents. Nothing is stored or sent, and nothing stops at a sector's edge. The client keeps the sectors around
  the camera generated on worker threads.
- **World generation.** A sector is filled by a list of modules named in `content/galaxy/galaxy.json`, each
  drawing from its own random stream, so adding or retuning one never changes what another makes. Asteroid fields
  are the only module so far. The same seed gives the same world to the last bit on every machine.
- **Sector character.** Every sector has a richness, from barren to very rich, and a set of resources that can
  occur there. Rich and poor space come in regions. Some sectors have every resource, some none, most a few; the
  sector panel and the map (for a discovered sector) show both.
- **Asteroid fields.** A sector has no field, a thin scatter, a cluster or a dense belt: up to 4,096 asteroids.
  Sizes run from pebbles 6 m across to giants 6 km across; most are tiny and a giant is rare. The middle of every
  sector, where ships arrive, is kept clear.
- **Hulls from blocks.** A hull is a list of typed blocks (engine, thruster, gyro, generator, battery, shield
  emitter, cargo, quarters, plating). Mass, moments of inertia, acceleration, turn rates, top speed, hull and
  shield points and the power budget are all computed from them and shown on the ship's data sheet.
- **Flight.** A command pod with inertial flight: thrust on all three axes, roll, dampeners, and a boost that heats
  up and locks out. Thrust, boost and shield recharge draw power; the generator refills the battery with the
  surplus. Boost needs the battery above 5% to start (`boostEnergyFloor` in `content/blocks/blocks.json`), runs
  until the battery is empty, then waits for the battery to pass 5% again; a hull without a battery cannot
  boost. The energy bar marks the 5%, and the boost readout says whether boost is cooling or out of energy.
- **Collisions and damage.** Hulls collide block by block against each asteroid's real surface, and block
  against block with each other. An impact bounces and turns the hull; damage grows with the square of the speed
  over a safe limit and lands on the blocks that touched, after the shield. A block at zero is gone, and the hull
  flies on what is left. Losing the core destroys the hull; its pilot gets out in a drone.
- **Ramming.** Two hulls that meet share the impulse by mass and inertia and both take the same damage, which
  goes by the pair's reduced mass: about the lighter hull's when the other is far heavier. A drone bounces off a
  freighter and a freighter shoves a drone aside. Below the safe speed hulls only nudge each other and can rest
  in contact. A hull in a jump tunnel is not there to be hit, and
  one that has just come out does no damage and takes none. The client predicts the contact by stepping its own
  hull and the hulls near it with the server's code.
- **Asteroids with mass.** An impact pushes hull and asteroid by their share of the momentum, so a pod bounces off
  and a heavy ship shoves a boulder aside. A pushed asteroid drifts and settles. The server tells clients only
  when an asteroid is hit; its motion from then on is a formula, and new players are sent the displaced ones. The server is authoritative; the client predicts its own hull with the same
  flight code and reconciles against snapshots; other players' hulls are interpolated.
- **Faster-than-light.** A drive is a block. There are three kinds, each with its own look: hyperspace (the stars
  stretch into lines that stream back from the hull's nose and become a tunnel), wormhole (a sphere that bends
  the stars around it) and rift (an orange window torn into space). With a wormhole or a rift the pilot sees their
  hull fly into the opening, rides through the throat behind it and comes out of a second opening that closes
  behind them. Every effect is laid out around the hull's nose, so looking around during a jump shows it from the
  side or from behind. A jump is a standing order: the hull brakes and turns its nose to the destination, the drive charges
  from the hull's power (a bigger ship needs more energy, a weak or drained battery charges slower), the hull is
  in transit for three seconds, and it comes out at its destination, any distance away. Taking the controls
  before the drive fires cancels the jump and loses the charge. The client generates the destination while the
  drive charges.
- **World settings.** A world has settings, chosen when a server or a local game starts: collision damage,
  shields against collisions (full, weak, none), shield recharge, jump charge speed and hull strength. The
  server owns them and tells each client on joining; the client predicts by the server's figures, and the data
  sheet and the readouts show the numbers in force. The New game screen sets them for a local game; in a game,
  the menu's World Settings shows the ones in force. See [World settings](#world-settings).
- **Resources.** Asteroids carry iron, copper, nickel, titanium, silver or gold, or are bare rock. Which of them
  a sector has, and in what shares, is part of its character; the rarer ones grow more common toward the core, and
  a richer sector has ore in more of its rocks and at a better grade. Nothing can be mined yet.
- **Fleet.** Each player starts with the command pod (hyperspace drive), a mining vessel (wormhole) and a survey vessel (rift). Hulls nobody is flying are flown by
  the server under their standing orders.
- **Command view.** One key pulls the camera back from the pod into a free orbiting view over the same space.
  Click or drag a box to select hulls, right-click to send them somewhere in formation; a fleet list on the right
  selects and focuses too.
- **Galaxy.** A spiral disc 800 sectors across. Asteroid fields follow its arms and core; between the arms and
  outside the disc space is nearly empty.
- **Galaxy map.** Every grid cell is a sector. Zoomed out it shows the whole galaxy; closer, the sector grid; closest,
  each sector's star, asteroids and name. A sector's contents are shown only after one of your ships has been
  within a sector of it. Right-click a sector to choose how to get there: move under engines, or jump with the
  faster-than-light drive (only ships that have a working drive jump).
- **Renderer.** Floating-point scene target and tone mapping, camera-relative rendering from double-precision
  positions, a star field and sky, instanced hull and asteroid meshes. Asteroids have five levels of detail, from
  a speck in the distance to a giant seen from its surface.

- **Hosting.** A game is private (the host alone, no network listener), unlisted (joined by address) or public
  (also on the list). A local game can be hosted for others: the same in-process server, listening on UDP as
  well. A server has a name its host chooses. From the command line for now; see
  [Hosting and joining](#hosting-and-joining) and [docs/hosting.md](docs/hosting.md).
- **Passwords.** An unlisted or public game can have one. It is never sent: the server challenges, the client
  answers with a keyed hash, and wrong tries are slowed down per address. It protects the password, not the
  traffic: game packets are not encrypted.
- **Listing server.** A small HTTP service (and its container image) that keeps the list of public games. A
  public server announces itself to it, refreshes every 30 seconds and removes itself on shutdown; the client
  fetches and checks the list. Ours is `https://mvl.sh`, the default in `content/net/listing.json`. See
  [docs/listing-server.md](docs/listing-server.md).
- **Releases.** Pushing a tag (`server-v…`, `list-v…` or `v…`) publishes the game server and listing server container
  images to GitHub Container Registry. See [docs/releasing.md](docs/releasing.md).

Not there yet: the server browser screen and the hosting screen (both work underneath and from the command
line; the join screen still takes an address only and cannot ask for a password), NAT traversal (a host needs
a UDP port others can reach), encryption of game traffic, IPv6, mining, taking control of ships owned by other players (permissions), jump range and cost, gates,
saving. Stations and gas fields are not generated yet; asteroids do not crack into pieces, and ore shows only as
a tint. Asteroids come in a shared set of shapes until mining gives each one its own body. Designs are limited to 64
blocks for now, because block state is sent as one bit per block.

## Controls

| | Keyboard and mouse | Gamepad |
| --- | --- | --- |
| Forward / back, strafe | `W` `S`, `A` `D` | Left stick |
| Up / down | `Space`, `Left Ctrl` | Right / left trigger |
| Look around | Mouse, or arrow keys | Right stick |
| Free look (hull holds its heading) | Hold right mouse button | |
| Roll | `Q` `E` | Bumpers |
| Boost | `Left Shift` | Left stick click |
| Dampeners on / off | `Z` | `X` |
| Command view | `N` or `F9` | `Back` |
| Galaxy map | `M` | `Y` |
| Halt (cancel order) | `H` or `Backspace` | `B` |
| Ship data sheet | `I` | D-pad up |
| Target the ship under the reticle, or the asteroid if there is no ship | Middle mouse button | |
| Take control of the target, or leave in a drone | `T` | D-pad down |
| Menu or back | `Esc` | `Start` |

On menu screens the arrow keys or the D-pad move between rows, left and right change the focused setting, and
`Enter` or `A` selects. The mouse works too: click a row's arrows, or drag a slider.

The pointer turns the view, not the hull. The hull swings its nose onto the view line whenever it is under
thrust, so looking around while coasting costs nothing; hold the right mouse button to look around under thrust
without turning.

A ship near the reticle is bracketed; the middle mouse button targets it and shows its shield and hull. A target
out of view, or behind you, shows as an arrow near the screen's edge pointing the way to turn; it slides back into
the bracket as the target comes into view. With one
of your own ships targeted, `T` puts you at its controls and the ship you left is flown by the server. With nothing
targeted, `T` leaves your ship in a drone: a tiny craft with little hull and shield that is removed when you take
a ship again. In the command view `T` takes control of the first selected ship.

In the pilot view, hold the right mouse button and turn the wheel to move the camera closer or further.

Command view:

| | |
| --- | --- |
| Select a hull or an asteroid | Left-click it; hulls also by their row in the fleet list |
| Select several | Drag a box (your hulls first; asteroids if the box holds no hull); `Ctrl` + drag adds to the selection; `Shift` + click adds or removes one; `Ctrl` + `A` selects all |
| Move the selection | Right-click the destination |
| Rotate the view | Right-drag |
| Zoom | Mouse wheel |
| Pan | `W` `A` `S` `D`, arrow keys, or middle-drag |
| Lower / raise the plane | `Q` / `E`, or `Shift` + left-drag |
| Focus the camera on the selection | `Space` or `F`; double-click a row in the fleet list |
| Halt the selection | `H` or `Backspace` |

Galaxy map:

| | |
| --- | --- |
| Select ships | Left-click a ship, or drag a box; `Shift` or `Ctrl` adds to the selection |
| Set course | Right-click a sector, then pick **Move here** or **Jump here** in the menu |
| Close the menu | Left-click elsewhere, or `Esc` (a second `Esc` closes the map) |
| Pan / zoom | `W` `A` `S` `D`, arrow keys or middle-drag / mouse wheel |
| Change layer, centre on your ship | `Page Up` / `Page Down`, `Space` |

Orders go to the selected ships, or to the ship you fly if none is selected. **Jump here** is greyed out when none of
them has a working jump drive, or all are already in that sector; ships without a drive stay where they are.

Where the command plane cuts through an asteroid or a hull, the cut is drawn as a bright line and the part below
the plane is dimmed, so it is clear what sits on the plane. `I` shows the data sheet of the first selected hull or
asteroid.

`F3` shows the
performance overlay, `F11` toggles fullscreen, `F12` takes a screenshot. Bindings live in
`content/input/bindings.json`; block types and flight rules in `content/blocks`, hull designs in `content/ships`,
galaxy rules in `content/galaxy`, world settings in `content/rules`, the listing server's address in `content/net`.

## World settings

Settings are fixed for the life of a server or a local game. Defaults and ranges live in
`content/rules/settings.json`.

| Id | Values | Default | What it does |
| --- | --- | --- | --- |
| `collisionDamage` | 0 to 4 | 1 | Multiplies collision damage. At 0 collisions push but never damage. |
| `collisionShields` | `Full`, `Weak`, `None` | `Full` | `Full`: the shield absorbs a collision. `Weak`: it absorbs it but drains four times as fast. `None`: collisions pass the shield and hit the blocks. |
| `shieldRecharge` | 0.25 to 4 | 1 | Multiplies how fast shields recharge. |
| `jumpChargeSpeed` | 0.25 to 4 | 1 | Multiplies how fast a jump drive charges. A jump takes the same energy. |
| `hullStrength` | 0.25 to 4 | 1 | Multiplies every block's hit points. |

How to set them:

```sh
make run ARGS="--start local --set collisionShields=None --set hullStrength=2"    # a local game
make run ARGS="--start newgame --set hullStrength=2"                             # the New game screen, preset
make server ARGS="--set collisionDamage=0.5 --set jumpChargeSpeed=2"              # a server
make server ARGS="--settings my-world.json"                                       # a server, from a file
```

- `--set <id>=<value>` can be repeated; the last one for an id wins. On a server it is applied after the file.
- The file is one JSON object: `{ "collisionDamage": 0.5, "collisionShields": "Weak" }`.
- An unknown id is ignored with a warning. A value out of range is brought into range with a warning. A value
  of the wrong kind, or a file that is not valid JSON, stops the start with a message that says what is wrong.
- A server logs every setting that is not at its default when it starts.
- A client that joins plays by the server's settings; its own `--set` only applies to local games.
- In the client, New Game opens a screen with every setting, starting from the defaults plus any `--set`.
  Changed ones are marked and can be reset one at a time or all at once. Choices are kept until the game exits;
  there are no saved presets.
- In a game, `Esc` then World Settings shows the settings in force, read-only. On someone else's server these
  are the server's.

## Hosting and joining

| Visibility | Listens on UDP | On the list | Who can join |
| --- | --- | --- | --- |
| `private` | no | no | Nobody: the host alone. A local game's default. |
| `unlisted` | yes | no | Anyone with the address. A dedicated server's default. |
| `public` | yes | yes | Anyone: it is announced to the listing server. |

```sh
make run ARGS="--host unlisted --host-name 'Ceres Station'"                # host a local game, joined by address
make run ARGS="--host public --host-players 8 --password 'open sesame'"    # on the list, with a password
make run ARGS="--join 203.0.113.7 --password 'open sesame'"                # join by address
make server ARGS="--name 'Ceres Station' --visibility public"              # a dedicated public server
PROSPECT_PASSWORD='open sesame' make server ARGS="--visibility public"     # its password, not on the command line
make run ARGS="--host public --listing http://127.0.0.1:8080"              # against `make listing` on this machine
```

- **A host needs a UDP port others can reach** (default 27420; `--host-port`, or `--port` on a server): a public
  address or a forwarded port. There is no NAT traversal, and the list does not check that a game is reachable.
- The host of a local game is never asked for its password. Everyone else is.
- A name is 1 to 48 characters. A password is 1 to 64.
- The password is never sent and a captured answer opens nothing. Game traffic itself is not encrypted.
- `--listing <url>` picks another listing server; `--listing off` turns it off. HTTPS only, except on this machine.
- A server's settings file can hold `name`, `visibility`, `port`, `maxPlayers`, `password` and `listing` under
  `"server"`. An argument wins over `PROSPECT_PASSWORD`, which wins over the file.
- In a game, the sector panel shows the server's name, its visibility and the player slots.
- Not there yet: a screen to host from, the server browser, and a password prompt on the join screen.

How the password and the list work: [docs/hosting.md](docs/hosting.md).

## Requirements

- `make`, and a GPU and driver with Vulkan 1.3
- Everything else comes from `make setup`: it detects the OS, installs the system packages (Linux: the Vulkan loader,
  `vulkaninfo` and validation layers through apt-get, dnf, pacman or zypper), installs the .NET SDK named in
  `global.json` into `~/.dotnet` without root, and restores packages. `make doctor` checks the result.
- Vulkan validation layers are optional for debug builds. Without root, `make tools` fetches them into `.tools/` on
  x86-64 Linux. Without them a debug build logs a warning and runs unvalidated.
- Apple Silicon Macs build for arm64 and render through MoltenVK; `make setup` installs it, the Vulkan loader and the
  validation layers with Homebrew.

## Running

```sh
make setup                                 # first time: SDK, system packages, restore
make run                                   # the game
make run ARGS="--start local"              # straight into a local game
make server                                # headless dedicated server on UDP 27420
make server ARGS="--set hullStrength=2"    # the same, in a world with its own settings
make run ARGS="--join 127.0.0.1"           # join it
make run ARGS="--host unlisted"            # a local game others can join by address
make check                                 # formatting, warnings-as-errors build, tests
make bench                                 # release benchmark; fails under 144 fps or on any per-frame allocation
make listing                               # the listing server on http://127.0.0.1:8080
make listing-image                         # its container image (docker/listing.Dockerfile)
make server-image                          # the dedicated server's container image (docker/server.Dockerfile)
make help                                  # every target
```

`prospect --help` and `prospect-server --help` list the command-line options.

## Layout

| Path | Contents |
| --- | --- |
| `src/Prospect.Engine` | Fixed-step loop, job graph, thread hand-off, transports (in-process and RakNet), atomic files |
| `src/Prospect.Game` | The simulation shared by client and server: galaxy, flight model, hull storage, protocol, server host, client prediction, and the game's side of the listing server (announcer, server browser) |
| `src/Prospect.Server` | Headless dedicated server |
| `src/Prospect.Client` | Window, Vulkan renderer, cameras, input, interface |
| `src/Prospect.Listing` | Listing server: the HTTP list of public games (ASP.NET Core) |
| `tests/Prospect.Tests` | Unit and integration tests, including real client and server sessions over UDP and the real listing server in-process |
| `tests/Prospect.Listing.Tests` | The listing server run in-process and driven over HTTP |
| `docker/` | Container images |
| `content/` | Game data, loaded at run time |
| `assets/` | Fonts, branding and GLSL shaders (compiled at startup) |

## Threads

| Thread | Owns |
| --- | --- |
| Main | Window events, input, interface, Vulkan. Reads simulation snapshots; never touches simulation state. |
| ClientSim | The client's 60 Hz simulation and its connection to the server. Publishes a snapshot every tick. |
| Server | The authoritative 60 Hz simulation (in a local game, or in `prospect-server`). |
| Job workers | Work scheduled on the job graph. |
| Thread pool | Requests to the listing server: a public server's announcements and the server browser's fetch. Never the simulation or the frame loop. |

## Licences

PROSPECT is built with BRUTAL, a game development framework by Ahwoo Limited, used under the BRUTAL License v1.0.
`NOTICE.txt` ships beside the binaries, the BRUTAL logo is shown at startup and the copyright notice is on the
credits screen. Third-party licence texts, including those of the bundled fonts, are in `LICENSES/`.
