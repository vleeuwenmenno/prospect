<img src="assets/icon.svg" width="64" height="64" alt="">

# PROSPECT

A multiplayer space sandbox: you command from a pod, mine asteroids, build hulls and run a fleet across star
systems. C# on .NET 10, built on the [BRUTAL](https://github.com/ahwootech/BRUTAL) framework (Vulkan, GLFW,
Dear ImGui, RakNet). Windows x64 and Linux x64.

Singleplayer is a server running inside the game process; multiplayer is the same server as a separate binary.
There is one simulation and one code path.

## Status

Milestone 1 (skeleton) is complete. On top of it, the first slice of the world is in:

- **Sectors.** The galaxy is a grid of 20 km cells generated from the server's seed: name, star, asteroid fields.
  Nothing is stored or sent, and nothing stops at a sector's edge. The client keeps the sectors around the camera
  generated on worker threads.
- **Hulls from blocks.** A hull is a list of typed blocks (engine, thruster, gyro, generator, battery, shield
  emitter, cargo, quarters, plating). Mass, moments of inertia, acceleration, turn rates, top speed, hull and
  shield points and the power budget are all computed from them and shown on the ship's data sheet.
- **Flight.** A command pod with inertial flight: thrust on all three axes, roll, dampeners, and a boost that heats
  up and locks out. Thrust, boost and shield recharge draw power; the generator refills the battery with the
  surplus. Running into an asteroid bounces the hull and damages shield, then hull.
- **Asteroids with mass.** An impact pushes hull and asteroid by their share of the momentum, so a pod bounces off
  and a heavy ship shoves a boulder aside. A pushed asteroid drifts and settles. The server tells clients only
  when an asteroid is hit; its motion from then on is a formula, and new players are sent the displaced ones. The server is authoritative; the client predicts its own hull with the same
  flight code and reconciles against snapshots; other players' hulls are interpolated.
- **Fleet.** Each player starts with the command pod and two mining vessels. Hulls nobody is flying are flown by
  the server under their standing orders.
- **Command view.** One key pulls the camera back from the pod into a free orbiting view over the same space.
  Click or drag a box to select hulls, right-click to send them somewhere in formation; a fleet list on the right
  selects and focuses too.
- **Galaxy map.** Sectors by layer, with their stars and asteroid counts, and set-course by right-click.
- **Renderer.** Floating-point scene target and tone mapping, camera-relative rendering from double-precision
  positions, a star field and sky, instanced hull and asteroid meshes with two levels of detail.

Not there yet: mining, taking control of ships owned by other players (permissions), gates and jumps,
saving. Asteroids are drawn from a shared set of shapes and collide as spheres until mining gives each one its own body.

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
| Target the ship under the reticle | Middle mouse button | |
| Take control of the target, or leave in a drone | `T` | D-pad down |
| Menu or back | `Esc` | `Start` |

The pointer turns the view, not the hull. The hull swings its nose onto the view line whenever it is under
thrust, so looking around while coasting costs nothing; hold the right mouse button to look around under thrust
without turning.

A ship near the reticle is bracketed; the middle mouse button targets it and shows its shield and hull. With one
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

Where the command plane cuts through an asteroid or a hull, the cut is drawn as a bright line and the part below
the plane is dimmed, so it is clear what sits on the plane. `I` shows the data sheet of the first selected hull or
asteroid.

`F3` shows the
performance overlay, `F11` toggles fullscreen, `F12` takes a screenshot. Bindings live in
`content/input/bindings.json`; block types and flight rules in `content/blocks`, hull designs in `content/ships`,
galaxy rules in `content/galaxy`.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A GPU and driver with Vulkan 1.3
- `make`
- Optional: Vulkan validation layers for debug builds. Install the Vulkan SDK, or on x86-64 Linux run `make tools`
  to fetch them into `.tools/`. Without them a debug build logs a warning and runs unvalidated.

## Running

```sh
make run                                   # the game
make run ARGS="--start local"              # straight into a local game
make server                                # headless dedicated server on UDP 27420
make run ARGS="--join 127.0.0.1"           # join it
make check                                 # formatting, warnings-as-errors build, tests
make bench                                 # release benchmark; fails under 144 fps or on any per-frame allocation
make help                                  # every target
```

`prospect --help` and `prospect-server --help` list the command-line options.

## Layout

| Path | Contents |
| --- | --- |
| `src/Prospect.Engine` | Fixed-step loop, job graph, thread hand-off, transports (in-process and RakNet), atomic files |
| `src/Prospect.Game` | The simulation shared by client and server: galaxy, flight model, hull storage, protocol, server host, client prediction |
| `src/Prospect.Server` | Headless dedicated server |
| `src/Prospect.Client` | Window, Vulkan renderer, cameras, input, interface |
| `tests/Prospect.Tests` | Unit and integration tests, including real client and server sessions over UDP |
| `content/` | Game data, loaded at run time |
| `assets/` | Fonts, branding and GLSL shaders (compiled at startup) |

## Threads

| Thread | Owns |
| --- | --- |
| Main | Window events, input, interface, Vulkan. Reads simulation snapshots; never touches simulation state. |
| ClientSim | The client's 60 Hz simulation and its connection to the server. Publishes a snapshot every tick. |
| Server | The authoritative 60 Hz simulation (in a local game, or in `prospect-server`). |
| Job workers | Work scheduled on the job graph. |

## Licences

PROSPECT is built with BRUTAL, a game development framework by Ahwoo Limited, used under the BRUTAL License v1.0.
`NOTICE.txt` ships beside the binaries, the BRUTAL logo is shown at startup and the copyright notice is on the
credits screen. Third-party licence texts, including those of the bundled fonts, are in `LICENSES/`.
