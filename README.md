<img src="assets/icon.svg" width="64" height="64" alt="">

# PROSPECT

A multiplayer space sandbox: you command from a pod, mine asteroids, build hulls and run a fleet across star
systems. C# on .NET 10, built on the [BRUTAL](https://github.com/ahwootech/BRUTAL) framework (Vulkan, GLFW,
Dear ImGui, RakNet). Windows x64 and Linux x64.

Singleplayer is a server running inside the game process; multiplayer is the same server as a separate binary.
There is one simulation and one code path.

## Status

Milestone 1 of 8, **Skeleton**, is complete:

- Window, Vulkan 1.3 device and swapchain, validation layers in debug builds
- Fixed 60 Hz simulation on its own threads, separated from rendering by lock-free snapshots
- Job graph on BRUTAL's job runners
- Keyboard, mouse and gamepad input with data-driven bindings
- In-house interface skin on Dear ImGui: splash, main menu, join, settings, credits, session HUD, performance overlay
- Client and server sessions over an in-process link (local game) or UDP (dedicated server)

There is no gameplay yet: starting a game puts you in an empty scene connected to a running server.

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

Keys: `Esc` menu or back, `F3` performance overlay, `F11` fullscreen, `F12` screenshot. Menus work with the
keyboard, the mouse and a gamepad. Bindings live in `content/input/bindings.json`.

## Layout

| Path | Contents |
| --- | --- |
| `src/Prospect.Engine` | Fixed-step loop, job graph, thread hand-off, transports (in-process and RakNet), atomic files |
| `src/Prospect.Game` | The simulation shared by client and server: protocol, server host, client session |
| `src/Prospect.Server` | Headless dedicated server |
| `src/Prospect.Client` | Window, Vulkan renderer, input, interface |
| `tests/Prospect.Tests` | Unit and integration tests, including real client and server sessions over UDP |
| `content/` | Game data, loaded at run time |
| `assets/` | Fonts and branding |

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
