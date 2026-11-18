# Cargo, containers and tractor beams

Steps 1–3 are built: holds, free containers and automatic tractor pickup.
Temporary local debug loading is available. Mining output is not built.

## Decided

### Containers

- Cargo is carried in containers. A hold never holds loose cargo.
- A container's size is its volume, in units (u). Mass follows from what is inside.
- Eight sizes, doubling: 1, 2, 4, 8, 16, 32, 64 and 128 u (XXS, XS, S, M, L, XL, XXL, XXXL).
  128 u is for large haulers.
- 1 u is a cube half a ship cell on a side: eight of them fill one ship cell (1 m at `blockSize` 1).
- Shapes double one side at a time: 1x1x1, 2x1x1, 2x2x1, 2x2x2, 4x2x2, 4x4x2, 4x4x4, 8x4x4 units.
  So 8 u is one ship cell, 16 u two, 32 u four, 64 u a 2x2x2 cube of cells, 128 u a 4x2x2 block of cells.
- A drone cell (`blockSize` 0.25) is half a unit on a side: a drone rack needs 2x2x2 drone cells per XXS.
- The size of a unit, the sizes and their shapes are content, never code, so they can be retuned.
  The hold load rule must drop containers that no longer fit after a retune, with a log line.
  Saved cargo rules validate original bounds and overlaps first; invalid records refuse the save.
- One resource per container. A hold fills up either by volume or because it carries too many kinds at once.
- A container has its own hull points. Collisions and weapons can destroy it, and its contents go with it.
- Cargo mass counts toward flight: a full hold makes the ship heavier.
  Flight treats the payload as uniformly distributed through its rack for centre of mass and diagonal inertia.

### Hold state decisions

- Amounts are kilograms; resource ids are galaxy indices. Resource -1 means an empty shell and requires zero kilograms.
- Maximum payload is unit volume times resource density from `blocks/cargo.json` (`densityKgPerCubicMetre`).
- The initial visibility scope is the own hull only; reliable updates must leave the snapshot record budget unchanged.
- Apply refuses an edit whose remaining racks cannot retain the cargo; repair preserves it.
  A retained rack has the same type and grid anchor; resizing repacks through the shared rule.
  Moving an occupied rack is refused until unloading exists. Wrecks take containers in their surviving racks.
- Damage that destroys a rack ejects its containers intact exactly once. Wreck splits keep cargo in surviving racks.

### Racks

- A rack is a closed box with ONE open face. Top, bottom, sides and back are closed walls.
  Containers go in and come out only through the open face; tractors find the way in there.
- The open face is the block's facing, chosen at placement by the hatch rule a drone bay and an ore hopper use
  (`OpenFaces`): away from what the rack is set against; with nothing, up. Mirrored with the block.
- Old design files (before version 10) take the same rule on load. When that face is against a block or carries a mount,
  the first free face in face order; with none, the rack stays closed until edited.
- Look: panelled walls; round the opening a framed rim band with hazard stripes, light strips, orange corners and lit
  edges; rails along the depth inside. Every rack part takes the outer half of a one-unit container's clearance.
- Containers sit on the rack's internal grid, attached to it, never floating. A smaller container fills whatever empty
  space fits it. The packing rule (`CargoPacking`: largest first, fixed orientation) is the one rule.
- Each stowed container carries mag-lock jaws at the back of its slot, with lights that show its stage.
- In the hold, containers are slots kept by the server, not physics objects. The server places them by a fixed rule.

### Loading and unloading

- Animated, with clear states: Approaching (the beam brings it to the opening), Transferring (in through the opening),
  Positioning (slides to its slot), Locked (mag-locks engage, a clear light change). Unloading is the reverse:
  Unlocking, then out through the opening.
- A change of the hold that moves containers to other slots slides them there.
- Client look only: the server stows and releases as before. The states are exposed for effects and the interface.
- A released container leaves through the open face, in line with its slot, and is pushed out along the face's normal.
  It never appears inside a block.

### Container look and hover

- One model for a container in a rack and one loose in space, built on workers and cached per size and resource.
- Detail: corner blocks, rib bands, a door seam and two lock rods on each end, four lifting eyes on top.
- Each long side has a plate in the resource's colour (`Galaxy.Resources[i].Color`) with its code as raised strokes
  (`StrokeFont`, no font texture), and a small dark plate with the size class (XXS..XXXL). An empty container says EMPTY.
- A resource's code is content: `code` in `galaxy.json`, one to four capitals or digits, unique. None named: the id's first two letters.
- The body leans toward the resource's colour (`bodyTint`). All figures are in the `rack` entry of `visuals/propulsion.json`.
- Every detail stands off the body by less than the clearance a slot leaves, so nothing reaches a neighbour.
- The cursor on a container shows a tooltip: resource, kg and fill, size class and u, HP of the shell's maximum.
  A loose container adds its claim (yours, someone else's, unclaimed) and the pickup delay left.
- The tooltip needs a shown cursor: the command view, interface mode, or `Shift` held in flight (`ReleasePointer`).
  A window under the cursor blocks it. A rock or another ship in front hides the container.
- Own hull blocks in front of a stowed container do not hide it. Open.

### Loading: only by tractor or drone

- Two ways to pick something up, and nothing else:
  1. A tractor beam (first).
  2. A drone launcher (later; mid-game's main way).
- A tractor beam is a face mount, like a thruster.
- Players place tractor mounts in the editor; there is no fixed cargo door.
- A rack is loaded by a tractor that can see it. The beam slides the container onto the rack.
- A container out of range or out of view of a free rack needs the pilot to move the ship.
- Tractor beams draw power and have a very short range.
- A tractor works by itself: a container that comes in range and in view of a free rack is pulled in.
  Picking up stays physical (a beam, a rack in view, power spent); it is never a pickup by touch.
- "In view" is a clear straight line from the mount to the rack.
- The editor shows each tractor's lines to the racks it reaches, and marks racks no tractor reaches.

### Losing cargo (step 2)

- A rack that is destroyed throws its containers into space, contents intact.
- Anyone can recover those containers with a tractor beam. Ownership does not restrict pickup.
- Dropping cargo uses the same containers as the hold.

### Jettison and claims (step 3b)

- A container leaving a hull (jettison or a destroyed rack) ignores every tractor for a short delay
  (`pickupDelaySeconds` in `blocks/cargo.json`, default 10 s). The ship that dropped it does not pull it straight back.
- Jettison has two choices:
  1. Abandon: anyone may pull it, as today.
  2. Keep claim: only the owner's own hulls' tractors may pull it.
- A claim limits pickup only. Anyone may still see it (sensor rules) and shoot it ("boom means gone").
- A claimed container ages and expires like any other. The claim is saved with the world.
- Containers thrown out by a destroyed rack are unclaimed.

### Implementation (step 3b)

- Server, simulation, wire and save support Abandon and KeepClaim; the Cargo window is separate work.
- Every release stores `PickupReadyTick`; `pickupDelaySeconds` defaults to 10 and accepts finite 0–3600 seconds.
- `ClaimOwner` uses the durable hull owner id; zero is unclaimed. Destroyed racks always release unclaimed cargo.
- `FreeContainer.MayPickup(owner, tick)` gates acquisition and client prediction. Visibility, damage and aging do not change.
- Protocol 62 sends owner and ready tick (104-byte records, 26,635-byte maximum). Codec 15 saves both.
  Unknown saved owners and invalid clocks refuse loading; transient tractor pulls restart. Older layouts are refused.

### Flight release API

- `PlayerInputBridge.JettisonCargo(uint hull, int rack, int index, long tick, JettisonMode mode)` needs no open editor.
- Pass the flown hull's id, the selected hold record's rack, its index in that hold, and `SessionSnapshot.OwnCargoTick`.
  Mode is `JettisonMode.Abandon` or `JettisonMode.KeepClaim` (`Prospect.Game.Protocol`).
- Reliable message 38 is 19 bytes. Server admission and receive budgets apply before decoding.
- Both paths share `ReleaseCargo`: owned controlled hull, current hold tick/revision, live rack/slot,
  quarter-second cooldown and no FTL transit. The editor path also retains its edit-token checks.
- Success updates the normal hold/free-container snapshots. Invalid flight requests change nothing; no separate reply.

### Cargo window

- The top bar's Inventory tab becomes Cargo. It opens the Cargo window in interface mode.
- The window shows the flown ship's hold as a grid: one tile per container, drawn by size, coloured by resource.
- Clicking a tile shows its details: size, resource, mass, hit points, rack.
- The details offer Jettison (abandon) and Jettison and keep claim.

### Tractor turret and tiers

- A tractor is a small turret on a face mount: a round base plate, a yoke that turns about the face normal,
  and an emitter head that pitches between the yoke arms, with a recessed lens and coil rings.
- The head aims at the container it holds, slewing smoothly; idle, it points straight out of the face.
  Aiming is client look only; the server rule (reach, line of sight, power) is unchanged.
- The beams start at the lens.
- Tractors come in the same six tiers as sensors (Common to Alien Tech). A tier scales range, pull,
  maximum mass and power draw. Multipliers are content, beside the sensor tiers.
- The editor card shows the tier badge, as sensor cards do. The turret base carries the tier colour.
- The editor's Storage category holds tanks, cargo racks and tractors.

### Implementation: tractor tiers

- Only `tractorBeam` (Common) ships, as only Common sensors do. Its `tractor.quality` uses the same quality
  names and `ModuleQuality` enum as modules; a higher tier is the same block at another quality.
- The editor card matches a sensor card: tier badge, then range, pull, lift and draw.
- `tractorTiers` multiplies range, acceleration, maxMass and power at load time.
  Power scales both basePower and powerPerTonne. Invalid quality, nonfinite/nonpositive multipliers and oversized results refuse content.
- Common multipliers are 1/1/1/1; Uncommon 1.2/1.25/1.5/1.1; Rare 1.4/1.5/2.5/1.2.
  Epic is 1.7/2/4/1.35; Legendary 2/2.5/6/1.5; Alien Tech 2.5/3.5/10/1.7.
- Shared reach, plan, pull, power prediction and editor preview read each mount's evaluated `TractorSpec`.
  Content and save identities cover the table; evaluator 17 rejects older arithmetic. Protocol remains 62; codec remains 15.
- All six types appear through the existing Storage palette. Existing emitter models are reused through content entries.
  The editor card draws the tier badge (`DrawTractorCard`).

### Implementation: turret and beam look

- Turret: the base plate is in the hull mesh (`BuildTractorBase`); its chamfered rim is painted the tier colour
  (`ModuleLook.Tier(spec.Quality)`). Yoke and head are worker-built by `PartModels.TryGetTractorTurret` and drawn per
  frame by `WorldView.DrawTractorTurrets`, which slews them (`turretSlewRate`) toward the held container. Idle and in
  the editor they point out of the face. Coil paint and the emissive lens colour are `tractor` figures in `propulsion.json`.
- Beam: `tractor.vert/.frag`, additive, depth-tested, after the plumes. A box per beam; the fragment shader works out
  the strands (polylines that fan out, twist and waver), a faint haze, the lens glow and a round aura about the container.
- A strand narrower than a pixel spreads its light and fades (energy kept), so a far beam never turns into a solid line.
- All look figures (strand count, width, waver, flow speed, pulse spacing, haze, aura) are `tractor` entries in
  `propulsion.json`, checked by `TractorLook.Check`.

## Open

- **Towing huge chunks** outside the hull: tractor-held mass joins the ship's flight. It changes shared flight
  and prediction, so it comes last and needs its own design pass.
- **Tractor filter:** a pickup list per ship that can be switched between allow-list and block-list.

## Working rule

Each major mechanic (cargo, mining, turrets) is designed and tuned on its own first. The glue between them is
checked afterwards. This overrides the joint mining, inventory and turret brainstorm in `docs/direction.md`.

## Built: racks with one open face and cargo motion

- `OpenFaces` (shared) is the one open-face rule; `BlockType.FacesOut` includes racks. Design file 10, evaluator 27,
  protocol 78. No wire or save layout change; old saves are refused by the content identity.
- `TractorReach.Rack` sees only the open face (`Opening`, shared with the hopper intake). `CargoEjection.MayLeave` refuses
  a release into a block or through a mounted face; a destroyed rack's containers start in their slots.
- `CargoMotion` (client, font-free, fixed buffers) matches hold records across changes by rack, size, resource,
  kilograms and hull points, the same place first. It takes over from the beamed body that was led in, and a released
  record slides out to the loose body it became, handing over at its drawn pose. The first hold of a hull is not animated.
- Durations and lock light colours: `motion` in the `rack` look of `visuals/propulsion.json`.
- The Cargo window's tiles say TRANSFERRING, POSITIONING or LOCKED while a container moves.
- Not built: sounds; a loose body on the beam has no tile yet (its stage is exposed, `CargoMotion.StageOf`).

## Proposed build order

1. **Built:** racks and containers in the hold: rack block, container sizes in content, the hold's slots on the server,
   wire, save, drawing, and a debug button that adds a container.
2. **Built:** containers in space: jettison, a rack's destruction, hull points, saved.
3. **Built:** tractor beam mount: range, power, pull, sliding onto a rack in view.
4. Mining output into containers.
5. Towing huge chunks.

Deferred: drone launcher, tractor filter.


## Built: step 1

- `blocks/cargo.json` owns the unit edge, eight named shapes and hull points; shared content identity includes it.
- `CargoPacking` uses fixed orientation, largest first, X then Z then Y first fit. No rotations or backtracking.
  Caller supplies occupancy and placement buffers; output is canonical size order, independent of input order.
- The existing `cargo` id is a box-only ship rack in Hull. Capacity is u, linearly scaled (8 u per cell).
  Retuning the grid requires matching the cargo stat; loading rejects mismatches. No drone rack yet.
- Worker-built rack decks, clamp jaws (per size and deck face) and reusable ribbed container models use the `rack` look in `visuals/propulsion.json`.
  `--open edit-rack` remains a visual sample. `--open cargo` and `edit-cargo` add real containers through the server for captures.

### Hold state, wire and save

- `BlockStateStore.Cargo` provides fixed storage for up to 255 containers on a hull with racks.
  Adds validate payload and health, and repack atomically through `CargoPacking` without allocating.
  Block-state copies preserve containers; losing a block removes only that rack's containers.
- Shared flight includes cargo mass and inertia; server and prediction use the same figures.
- Protocol 58 sends only the controlled hull's hold reliably on change, including after control changes.
  Its maximum is 6,135 bytes (255 records); the 255-hull snapshot budget is unchanged.
- Codec 12 saves hold contents and their original tuning alongside moving FTL transit from main.
  Loading refuses corrupt original contents, then repacks with current tuning, logging each dropped container.
  Only cargo tuning and its matching rack-capacity stat can change without invalidating the saved content identity.

### Editor and debug loading

- Apply preserves containers in racks with the same type and grid anchor, repacking after resize.
  Both the client and server refuse overflow or removal of an occupied rack; moving one is also refused.
- Repair preserves cargo. Wrecks take their racks' containers; damage-destroyed racks eject cargo in step 2.
- Select one applied rack in the editor, cycle size and resource (including Empty), then ADD CONTAINER (DEBUG).
  The server fills that size to its density limit, or adds an empty shell, only if it fits.
- Debug add requires an in-process local connection, `AllowDebugFill`, the current edit token and the controlled hull.
  Requests are spaced a quarter second apart. Apply and take control first when building from the drone.
- Flight and editor draw the own hold using the reusable preview model. Ship data shows used/capacity u and kg.

### Limits of step 1

- Other players' containers and unpiloted owned hulls' holds are not replicated or drawn.
- Payload kilograms affect mass, centre of mass and inertia; empty shells have no additional mass.
- Container health is stored and validated. Step 2 adds independent collision damage and jettison; pickup waits for step 3.
- No drone rack. Tractor pickup is step 3; mining output is still deferred.

### Validation

- Final merged `timeout 900 make check`: 2,550 game tests and 354 listing tests passed.
- `make bench`: 798 fps normal, 792 fps with resizing, zero managed bytes on all threads in both runs.
  The compositor kept 5096x1390; six resize requests and twelve swapchain/target rebuilds were measured.
- Real local captures use `--open cargo` and `--open edit-cargo`, `--set asteroidAmount=0` and `--capture`.
  Both showed the server-added 8 u hold (6,177 kg), with `agent.prospect` hidden on `special:agents`.

## Built: step 2

- Free containers are separate bodies, never hull records: Double3 position, velocity, orientation, spin, size,
  resource, kilograms and HP. They drift, collide with hull boxes and shared asteroid geometry, and lose HP.
  Zero HP destroys the shell and its payload: boom means gone. No weapon damage yet; pickup is step 3.
- Jettison is in the Cargo window (U): select a container's tile, then Jettison or Jettison and keep claim. The editor's
  own cargo panel is gone. Local and remote sessions use the same server request.
  The server checks owner, controlled hull, edit token, rack, selected slot and the exact hold revision seen.
- Jettison starts just outside the rack's open face, in line with the slot, pushed out along its normal with hull point velocity.
  Damage-only `CargoEjection` adds a seeded outward kick before generic rack loss. Generic `Lose` never ejects;
  a wreck split transfers surviving racks with their contents. Repeated damage cannot eject them twice.
- Existing container models draw free cargo. Middle-click targets size, resource, kilograms and HP.
  Poses interpolate authoritative sets; contact sparks infer velocity changes, with no container shield.
  Hull shields receive the existing shared contact-strength response.

### Bounds, visibility and lifetime

- `blocks/cargo.json` owns free-body tuning: 64 per sector, 64 contact-tree/rock checks per tick, restitution,
  shell collision mass, and release speeds. The global fixed pool holds 256. Caps evict the oldest by birth/id.
- Collision work rotates through the pool and generates at most one sector per tick. Dense scenes defer contacts;
  fast bodies use discrete contacts. Containers do not collide with one another. Rendering shares the bounded
  dynamic-model budget with hull details and can omit excess models under extreme load.
- Protocol 59 adds reliable message 37: an 11-byte header and 82-byte records, at most 21,003 bytes, ten sets/s.
  A client receives only containers inside its sensor coverage, including gas attenuation, with no ownership bypass.
  An empty set withdraws lost contacts. The existing 255-hull snapshot and 6,135-byte hold message stay unchanged.
- Codec 13 saves all free-body fields, birth and unattended age, plus the next durable id: 98 bytes each,
  at most 25,088 record bytes. Original cargo rules validate before current retunes; incompatible payloads log drops.
- `containerLifetimeMinutes` defaults to 60; 0 never expires. Only time with no connected pilot within the base
  sensor radius counts, as for wrecks. It is a world creation setting and survives saves; caps still apply.

### Capture fixtures

- `--open cargo --jettison 6 --capture <path>` loads the pod through the real local debug request,
  then sends the same validated jettison request as the button. The time is seconds after the editor opens.
- `--open cargo --hurt-fleet 7 --hurt-kind cargo --capture <path>` destroys the first rack through `Hit`.
  Both fixtures require unsaved private local games. Use `--set asteroidAmount=0` to inspect the release clearly.

### Step 2 validation

- Each of the six implementation parts passed foreground `timeout 900 make check` before its commit.
  Final suite: 2,570 game tests and 354 listing tests, no failures.
- `make bench`: 854 fps normal, 735 fps with resizing; zero managed bytes on all threads in both runs.
  The compositor used 5096x1390; six resize requests and twelve swapchain/target rebuilds were measured.
- Real `--capture` sequences confirm one jettisoned container and three containers from rack destruction.
  `agent.prospect` stayed on `special:agents`; no desktop input was injected.
  Captures, native crops and sequence strips: `/tmp/prospect-cargo-step2-captures/`.
- Allocation probes cover drift, rock contact, server contact stepping, damage ejection, wire/save codecs,
  interpolation and picking. The two-client session test covers ownership, stale hold rejection and remote release.


## Built: step 3

- The freely placed `tractorBeam` face mount works automatically. Starting content: 40 m range, 4 m/s² pull,
  100 t maximum, 2 MW base plus 0.5 MW/t of payload and shell. One body per mount; several mounts work in parallel.
- Both straight segments must clear the own hull's live blocks: emitter to container, emitter to a rack face.
  A rack face counts when the emitter is on its outer side, it carries no mount, and part of it lies in the mount's
  outward half-space (the plane of the block the mount sits on, and beyond). The aim is the middle of that visible
  part, else its point nearest the emitter. The nearest face with a clear line wins, so an obstructed face does not
  hide a rack that another face shows. A rack's own frame never blocks its loading; every other live block does,
  the mount's own block included. A rack wholly behind the mount's face plane is not in view.
  A fitting slot uses the existing largest-first packing rule, including other tractors' reservations.
- A pulled body stays free and collidable while approaching the emitter, then the rack's open side.
  At arrival the server adds it to the hold and removes the free body. Touch alone never stows it.
- Existing claims win; new claims use deterministic hull/mount/body order, resuming deferred mounts next tick. Anyone can recover free cargo.
  All active mounts on a hull run only when their combined demand is paid by `FlightModel.Step`.
  No power means no tractor force; existing momentum remains. The energy tooltip lists TRACTORS.
- Editor reach lines and amber unreached racks use the same shared geometry rule. The details panel reports
  reached/total racks. Beam colour, line widths and warning tint are client data in `visuals/propulsion.json`.

### Wire, save and bounds

- Protocol 60 adds ten claim bytes to each free record: hull, mount, rack, stage and powered flag.
  At most 23,563 bytes per set; sensor filtering and the separate hull/hold budgets remain unchanged.
- Codec 14 preserves the free body's pose, motion, health and payload, but does not save its claim.
  An interrupted pull restarts acquisition after loading. Stowed cargo is saved only in its hold.
- The fixed free pool supplies position-based range rejection before geometry; at most 256 new candidate
  positions are tested per tick. Existing claims are checked each tick. The scan resumes at a deferred mount,
  so crowded acquisition waits without starving later mounts or hulls.
- Editor pair checks are spread over frames without allocation, with fixed storage for admitted designs.
  Cargo-only updates reuse the frozen checked draft and packing scratch, without a new geometry export.
  Beams are soft screen-space lines; external hulls and rocks do not occlude them or block tractor acquisition.
- No drone tractor/rack, towing, allow-list/block-list filter, or replication of foreign holds. These remain deferred.

### Step 3 evidence

- Every implementation step passed foreground `timeout 900 make check` before committing.
- The shipped ship carries racks and tractor beams (the capture copy `tools/prepare_tractor_capture.py` is gone).
  `--open cargo --jettison 6` exercises the real jettison request and automatic pickup;
  `--open edit-cargo` shows the reach lines to both racks.
- Final full check: 2,597 game tests and 354 listing tests passed.
- `make bench`: normal 862 fps, resize 770 fps; editor cargo 1,264 fps, menu 1,266 fps,
  dump 1,268 fps and mirror 1,049 fps. All six runs measured zero managed bytes on all threads.
  The compositor used 5096x1390; resize measured twelve swapchain and twelve target rebuilds.
  The tractor fixture also passed `--bench 10 --open edit-cargo --jettison 5 --set asteroidAmount=0`:
  1,146 fps, zero managed bytes on all threads through release, powered pull and stow.
- Hidden real-client sequence logged held/free counts 3/0, 2/1, 3/0, with both powered approach stages.
  Crops: `/tmp/prospect-tractor-captures/pull-stow-sequence.png`,
  `/tmp/prospect-tractor-captures/editor-reach-crop.png`,
  `/tmp/prospect-tractor-captures/editor-details-crop.png`. No desktop input was injected.

### Implementation: guided loading

- The tractor leads a held container; it never aims at the emitter once a rack is chosen.
- Path: a staging point outside the rack's open face, then a slow glide straight in along the face normal to the packed slot.
- If the straight line to the staging point is blocked, it goes first to the emitter's exit point.
- While guided and powered, the container ignores contacts with its own hull. Rocks and other hulls still hit it.
- It turns onto the hull's axes and is stowed at the slot, within tolerance, or inside the slot cell and nearly still.
- No progress, or no line of sight, for `stallSeconds`: the claim is released and the container waits `retrySeconds`.
- Where a rack's open face lies against another block it is closed there, never an entrance.
- Figures: `tractor` in `content/blocks/cargo.json` (staging margin, tolerance, final speed, settle speed, align rate, progress, stall, retry).
- Server only: no wire or save change. Tests: `TractorGuideTests`.
