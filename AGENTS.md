# PROSPECT: notes for agents

A multiplayer space sandbox in C# on .NET 10, built on the BRUTAL framework (Vulkan, GLFW, Dear ImGui, RakNet).
`README.md` describes what exists, the controls and the layout. This file is how to work on it.

## docs/

Read `docs/*.md` before designing or building a feature. They say where the game is going; this file says how
to work.

- `docs/direction.md`: what the game is, what is decided, what is still open, networking goals.
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
| `make check` | Formatting, warnings-as-errors build and all tests. Must pass before work is called done. Takes about a minute. |
| `make bench` | Release benchmark. Fails under 144 fps or on any managed allocation in the frame loop. Run it after touching anything that executes every frame or every tick. |
| `make run ARGS="..."` | The client. `make server` is the dedicated server. |
| `make fmt` | Format. `make check` only verifies. |
| `make tools` | Fetches Vulkan validation layers into `.tools/` (not installed system-wide on the dev machine). |

Run one test class or method with the test binary directly:
`dotnet artifacts/bin/Prospect.Tests/debug/Prospect.Tests.dll -class Prospect.Tests.JumpTests` (or `-method <full name>`).
Always wrap long test runs in `timeout`; a session test that waits on the simulation can otherwise sit for minutes.

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

- **Wire format:** bump `ProtocolInfo.Version` in `Protocol/Messages.cs`, and update `MaxWireSize` constants.
- **Shaders:** files in `assets/shaders/` are compiled at startup. Check them first with
  `glslc -I assets/shaders assets/shaders/<file> -o /dev/null`. `frame.glsl` must match `FrameConstants` field for
  field.
- **A new per-tick system:** add a unit test in `tests/Prospect.Tests/WorldTests.cs` and, if it crosses the
  network, a session test that runs a real server and client.
- **A new hull design:** add it to `content/ships/fleet.json`. Stats are derived from blocks; do not add stat
  fields to the design file.
- **README:** keep the Status and Controls sections true. Say what is not there.

## Checking the client without a person at the screen

The client opens a real window on the developer's desktop, and they are usually working on that machine.

- Only launch it with `--capture <file.png>` or `--bench <seconds>`. Both mute every input device, so the window
  cannot grab the pointer or react to their typing. Never start it any other way from an agent session.
- `make run ARGS="--start local --open <view> --capture /path/shot.png --capture-after 2.5"` saves a screenshot
  and exits. Views: `command`, `map`, `galaxy`, `info`, `jump`, `jump-wormhole`, `jump-rift`. Screens without a
  session: `--start menu|join|settings|credits`.
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

## Known limits to design around

- Hull designs are capped at 64 blocks: block state travels as one bit per block. Larger ships need chunked block
  replication first.
- Snapshots are full state, not delta-compressed, and there is no interest management yet.
- Sector discovery is kept by the client for the session; nothing is saved.
- Asteroids do not collide with each other.
- Hull against hull contact tests every hull against every other each tick, bounding spheres first. It needs a
  spatial index before there are thousands of hulls.
- The client predicts contact only with hulls near its own, and flies only its own fleet's hulls forward exactly;
  another player's hull is assumed to keep its velocity until the next snapshot.
- The client replays one step of the fleet per input. When the server simulates several of a player's inputs in
  one tick, contact between their hull and a fleet hull is mispredicted until the next snapshot.
- A hull knocked back by a hit is not checked against a third hull until the next tick, so the two can overlap
  for one tick.
- A player's hulls are removed when they disconnect.

## Who does the work

Delegate by default. The main model plans, writes the brief, reviews what comes back and integrates it; it does
not do work a cheaper model can do. Pick the lowest tier that fits the task, never a stronger one "to be safe".
This adds to the roles in the global instructions (scouting, second opinions); it does not replace them.

| Tier | Model | Effort | Use for |
| --- | --- | --- | --- |
| Scout | Pi DeepSeek Flash, else `claude-haiku-5-5` | low | Finding code, reading BRUTAL source for a signature, gathering context. Read-only. |
| Easy | `claude-sonnet-5-5` | medium (low for one-line changes) | Content JSON and tuning numbers, interface text and layout tweaks, key bindings, README and docs, tests for behaviour that already exists, a contained fix in one file. |
| Medium to hard | `claude-opus-5-5` | high (medium for the smaller end) | A new screen or HUD element, a gameplay feature that follows an existing pattern (a new order, a map feature, a new block type with its stats), shader effects, multi-file refactors that do not change threading or the wire format. |
| Tough and core | `claude-fable-5-2` | high (xhigh when hard to reverse) | Networking: protocol, prediction, reconciliation, snapshots, interest management. Engine core: threading, the job graph, lock-free hand-off, unmanaged memory. Renderer core: Vulkan resources, frame flow, synchronisation. Simulation determinism. Architecture decisions and anything where a mistake is a desync, a crash or corrupted memory. |

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
