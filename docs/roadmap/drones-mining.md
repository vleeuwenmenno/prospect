# Drones and mining

Product direction from the brainstorm on 2026-11-16. Built so far: D1 (orders, the bay, launch and recall), D2
(fetching cargo), D3 (drone scans, see "D3 as built") and R1, the scrape foundation (see "R1 as built").

## Why

- Mining is the core loop: find a rock, learn what is inside, take it apart, haul the ore home.
- Drones do the close work a ship cannot: detailed scans, charges, fetching cargo far away.
- Co-op mining is a big part of the game idea.

## Decided

### Drones

- Drones are kept and launched from a drone bay block. Not in cargo racks.
- The drone scan replaces nothing: the asteroid scanner module keeps its long-range, rough composition.
- There is one drone scan: the detailed one. Decided 2026-11-17; the separate survey is removed.
- The drone scan is a group job:
  1. The player asks for a drone scan of a rock (one key, `;`).
  2. The game works out how many drones are needed, from the rock's size, shape and line of sight.
  3. The drones fly into position.
  4. The player orders the scan; it takes a short moment.
  5. The result: detailed composition, veins and a heat map.
- Charges: the player drags a cutting plane over the rock. Bigger cuts need more charges.
  The server decides where it really breaks, with some randomness.
- Fetching cargo: one container a trip. A container too heavy for one drone needs two (or more).
  Drones respect claims, as tractors do.
- Drone figures (speed, battery, carry mass, scan time) are upgradable. They are content, never code.
- Until there is an economy, a bay starts full and refills slowly. Buying and building them at stations comes later.

### Mining

- Three ways to mine:
  1. Scraping the surface with a laser. Slow; it cuts the rock away.
  2. Cracking with charges, which reveals richer veins inside, then scraping those.
  3. Heat cracking of small rocks with a special laser: keep the heat in a window without overheating.
- Scraping changes the rock's surface. Nearby players see the change (sent to every player who has the rock loaded).
- Heat is per rock, so several players can work one rock together.
- The heat-crack size limit is content and depends on shape: an elongated rock is judged by more than one figure.
- Raw ore never goes in cargo racks. It goes:
  1. Floating ore chunks are pulled by tractor or drone into an ore hopper.
  2. The hopper fills raw ore containers: one ore type each, capacity by size.
- Ore has its own Ore Cargo window, separate from the Cargo window.

### Ore collection (R4)

Decided 2026-11-17. The server side is built (R4a), see "R4 as built"; the markers, tooltip and chunk count are
built apart.

- Ore bins are box blocks, sized like tanks. A bin holds one ore at a time: kilograms and the mass-weighted grade.
  An empty bin takes any ore. Its mass counts for flight like cargo. A lost bin loses its ore. Selling or moving ore
  comes later.
- The ore hopper is a box block with one intake face, chosen at placement like the drone bay's hatch. Chunks are
  delivered into the intake; the hopper puts each into a bin of that ore, else an empty bin.
- Tractor beams take chunks on their own, like loose containers, but a container always comes first. A chunk is
  taken only when a bin has room for it, so a full ship stops pulling.
- Drones take chunks too: the popup gets "Pick up ores", a second flag beside "Pick up cargo".
- Highlighting in flight (built): a marker on each chunk within sensor range in the ore's colour, a hover tooltip (ore,
  grade, kg) like the container's, and a chunk count near the ship on the DRONES chip. Chunks off screen get edge arrows
  with counts. Figures: `content/visuals/ore_chunks.json`.
- While debugging, every method is unlocked. A tech tree decides unlocks later.

### Drone life and the bay

- Drones can be destroyed. Scan drones come back and are reused; charge drones are used up.
- A lost drone is gone; the bay rebuilds one slowly. Buying and building comes later.
- The bay has tiers, like sensors and tractors: capacity, launch rate (how many at once), rebuild speed.
  Figures are content.
- The bay is a box block with one hatch face, chosen like a rack's deck face. Drones leave and dock through
  the hatch, which must face open space.

### Rock interiors: why crack

- Every rock has a fixed interior from the world seed, the same for every player:
  1. Crust: rock and weathered, low-grade ore.
  2. Mantle: normal grade.
  3. Veins: twisting tubes of high-grade ore through the rock.
  4. Sometimes a core pocket of a rare mix-in.
- Scraping takes the crust: slow, low grade. Cracking cuts through veins and exposes them; the laser then
  scrapes vein faces at high grade.
- Two scans:
  1. Scanner module: surface average composition.
  2. Drone scan: true bulk composition, and a 3D hologram of veins and pockets with a heat map of easy and unstable pockets.
- Unstable pockets (ice, volatile gas) can blow when cracked: a bonus when planned, damage when not.
  A bad cut can split a vein across two pieces.

### Rock edits

Decided on 2026-11-17 from the spike (see "Spike results" below). The first build is a start: the look of cracks
and the mining rules will be refined by playing.

- The edit list is the truth. The wire and the save carry edits; geometry is a cache rebuilt from them.
- A crack makes new rocks. Each piece is a radial field (a radius per direction, from the piece's own centre).
  Each piece names its parent and the cut, and takes a rock index past the generated list.
- A scrape is local detail in sparse bricks (fixed size in metres), only where a laser touched.
- A crack's cut face is never voxelized. Crack size is limited in content (charges per square metre of cut);
  a giant is never cracked whole, a piece is cracked off it.
- Cracks should look natural: rough, uneven break faces, not flat planes. Refined after the first build.
- Anything discrete (which bricks exist, ore counts) uses `DetMath` or comes from the server.

### Drone settings

Built: the popup, "Pick up cargo" and "Pick up ores" (R4a).

- Clicking the DRONES chip opens a small popup over it: one Launch/Recall button, the checkbox "Pick up cargo", and
  status lines (drones in, out, fetching; rebuild timer; the server's last answer). It closes on a click outside, on
  Escape (`BackStep.DronePopup`) or on the chip again.
- "Pick up ores" is the second checkbox, a second flag beside auto pickup: with it on, the drones bring loose ore
  chunks to the tractor beam, which leads them into the hopper. See "R4 as built".
- Decided: the setting is per hull, not per bay. The chip, the popup and a fetch all speak for the whole hull, and
  a fetch already picks bays itself. It is saved (codec 19) and carried over an edit or repair.
- Auto pickup is the manual fetch, not a second path: `TickAutoPickup` picks a container and calls `StartFetch`, the
  same code as the `/` key, so claims, pickup delay, range, carriers and refusals are the same. Each tick it looks at
  a rotating window of `autoPickupWindow` (16) of the 256-body pool.
- It skips: containers someone has (tractor, other drones), ones the owner may not take, out of range or sensor
  sight, and ones the hull's own tractor reaches (`TractorSimulation.Reaches`) since the beam takes those itself.
- One fetch at a time per hull. A refused container is left alone for `autoPickupRetrySeconds` (20; four are
  remembered per hull), so an impossible container never nags. The refusal is told to the owner like a manual one.
- A manual recall holds auto pickup off for `autoPickupPauseSeconds` (30), or until the next manual launch.
- Turning it off stops new fetches; a fetch under way finishes (recall cancels it).
- Wire: protocol 67, reliable message 43 `DroneSettingsMessage` (hull, flag; own rate limit, `commandSeconds`),
  `BayView.AutoPickup` in message 40, which the checkbox reads back, so a lost click shows as an unchanged box.
  Since protocol 70 the message carries both flags and the view answers with both.
- The keys (`.` launch, `,` recall, `/` fetch) keep working.

### Mining lasers and action groups

- A mining laser is aimed and fired like a weapon: the pointer aims, left mouse fires.
- Lasers and weapons belong to action groups 1 to 9 and 0. A group shows only when a part is assigned to it.
- The number keys toggle groups on and off; several can be on at once.
- While any group is on, left mouse fires every group that is on. With all off, left mouse aligns to view as today.
- A part's group is set per mount in the build menu and saved with the design. The Ship window (`I`) lists the
  groups with their keys and parts, and can change a group without a rebuild.

### Drone scans

Built in D3 (2026-11-17); see "D3 as built".

- One scan, one key. Decided 2026-11-17: the drone scan is the detailed one; the group survey (bulk composition and a
  vein hint) is gone, since the detailed scan already gives all it said.
- The player orders the scan. Drones fly into place, the chip says READY, the player presses SCAN. Nothing starts by itself.
- One scan per ship at a time. A recall gives it up.
- Scan drones are not used up: they come home and are used again.
- What a scan found is the player's own, kept and saved per player, like module scans.

### Built to be extended

- Every new mechanic is a set of parts: data in `content/`, shared rules in `Prospect.Game`, a client view.
  A new tool, drone kind or scan level is a new content entry, not new code paths.
- Orders, not input: a drone, a mining laser or a fleet ship acts on an order (target, task, limits).
  The player gives orders through the interface. Later a captain in a second ship, or an NPC miner, gives
  the same orders. One executor on the server, whoever gave the order.
- The server decides every result: what a scan reveals, where a rock breaks, how much ore comes off.

### Drones as hulls and orders (built in D1)

- Drones are small real hulls flown by the server with `HullSim`, so they collide, can be shot and use power.
  A drone that is used up (scan, charge) is removed when its job ends.
- An order is a small record (`TaskOrder`): task, phase, issuer, actor, base hull and bay, point, subject, until.
  One executor on the server (`ServerHost.TickDrones`) runs a fixed table of them each tick without allocation.
- The executor turns an order into the drone's `HullOrder` (move to a point), so a client predicts the drone with
  the same autopilot. A new task is an `OrderTask` value and one case in the executor.
- A saved world docks every drone: drones out are counted back into their bays, and no order survives a load.

## Build plan

Three tracks. A track's steps run in order; the tracks run side by side, each in its own worktree.

### Track R: rock edits and mining lasers

1. R1, scrape foundation: the edit list per rock, scrape bricks, wire, save, collision, drawing, brick seams
   and level of detail. A temporary debug scrape (local games only) drives it until the laser exists.
   **Done** (2026-11-17), see "R1 as built".
2. R2, cracks: radial pieces as new rocks, rough natural break faces, wire and save of the pieces.
3. R3, mining laser: a face mount the pilot aims and fires in action groups; it scrapes through `ScrapeRock` and
   frees floating ore chunks. **Done** (2026-11-17), see "R3 as built".
4. R4, ore: chunks pulled by tractor into an ore hopper and kept in ore bins. R4a, the server side with the bins in
   the Ship window, is **done** (2026-11-17), see "R4 as built". Still to do: the Ore Cargo window, selling or
   moving ore.

### Track D: drones

1. D1, orders and the bay: the order record and its one executor on the server, the drone bay block with tiers,
   drones as small server-flown hulls, launch and return. **Done.** Notes:
   - Bay: `droneBay` (box only, one cell), `bay` figures and `bayTiers` in `blocks.json`; Common keeps 4,
     launches 2 at once, rebuilds one in 90 s. At most 16 bays a design (`limits.maxBays`).
   - Hatch: the block's facing, chosen in the editor as the face opposite the one the bay is set against.
     A launch and a docking need the hatch clear for 3 m of the ship's own blocks and mounts.
   - Drones: the `work_drone` design (fleet role `workDrone`), all ion, no shield, no drive. Figures and caps in
     `blocks/drones.json`: 12 out per player, 16 hulls kept free below the 255-hull ceiling.
   - Orders in D1: Hold (a point in the base's frame; by default 14 m out from each hatch, spread on a grid) and
     Recall (line up 6 m out, fly in, dock). A jump recalls; drones still out when the ship leaves are lost.
   - Interface: `.` launches all, `,` recalls all, a DRONES chip over the console. No drone panel yet (D2).
   - Gaps: no avoidance on the way home; the hold point is not chosen in the interface yet; drones out of a hull
     that is not the one flown show nowhere but in the world.
2. D2, fetching cargo: drones bring containers back; two or more lift a heavy one; claims respected. **Done.** Notes:
   - Hand-over, not stowing: drones bring a container 10 m out in front of a tractor beam and set the beam's claim on
     it; the beam stows it by its own path and packing. Chosen because it adds no second stow rule. A fetch is refused
     without a beam that sees the hand-over point, or without rack room.
   - Carry: 5 t a Common drone (`carryKg`, scaled by `bayTiers.carry`). A heavier container takes as many drones as
     its mass needs, at most 8 (`maxCarriers`). They lift only together; the pull is 1.5 m/s² times their carry over
     the mass, at most 12 m/s. Drones holding station go first, then the bays launch the rest.
   - Range 3 km from the ship (`fetchRangeMetres`). Claims, pickup delay and keep-claim are the tractor's rules.
   - A lost carrier, a ship that jumps, a recall or a 600 s time-out drops the container where it is.
   - Interface: `/` on the hovered or targeted container, the target panel's button, "N FETCHING" on the chip.
   - Gaps: drones have no battery limit beyond D1's power use (no endurance figure); no avoidance; a ship with no
     tractor beam cannot be fetched to; two fetches for one rack slot are both accepted and the second is dropped
     with "no rack room" at the hand-over; a beam busy with another container makes the drones wait at the point.
3. D3, scans: the drone scan. **Done** (2026-11-17), see "D3 as built".
4. D4, charges: drones place charges along the player's cutting plane (needs R2).

### Track I: rock interiors and heat

1. I1, interiors: crust, mantle, veins and pockets from the seed; what scraping and cracking yield.
   Done: the shared query. The yield rules themselves come with R3 and R4. See "I1: rock interiors (built)".
2. I2, heat cracking: the special laser and the heat window.

## D3 as built

How to work on it is in `AGENTS.md`, "Drone scans (D3)". This is what exists and what does not.

### How a scan goes

1. The player targets a rock and asks for a drone scan (target panel button or key).
2. The server checks it: rock in sensor sight, surface within 3 km, enough drones holding station or in bays.
3. It sends that many drones to places spread evenly around the rock, off its surface by the standoff.
4. Drones on the far side fly around the rock on its bounding shell, never through it.
5. When every drone holds its place the scan is READY. The player presses SCAN; it runs a few seconds.
6. The server works the result out of the rock's interior and keeps it for the player. Drones fly around the rock
   to the ship's side and dock.

### What the scan shows

- The true bulk composition to 0.1 % (a scanner module resolves 1 % to 10 %).
- The exact veins (polylines with a radius at each point, ore and grade), pockets (ore, ice or volatile gas;
  unstable flagged) and a heat map.
- Heat map: 64 cells over the surface. A cell is unstable when an unstable pocket lies under it; it is easy as far as
  vein or ore pocket ore lies near the surface under it.
- The hologram draws veins in their ore's colour, pockets, and the heat map (red unstable, green easy) over the rock.

### How many drones

- Each drone covers the smaller of what it sees from the standoff (a cap of the rock) and its sensors' coverage.
- Together they cover the rock `cover` times: all of it, once.
- A stretched rock has more surface: its area grows with the square root of farthest over nearest radius.
- Standoff: 0.6 of the radius, 12 to 300 m.

| Rock radius | Drones |
| --- | --- |
| 10 m | 4 |
| 100 m | 6 |
| 200 m | too large (15 to 18) |
| 1 km | too large (about 400) |

- Shape matters: a stretched rock of 200 m takes up to a fifth more than a round one.
- A result is worked out in about 1.7 ms (Debug build) when the scan ends; nothing runs per tick but flying.

### Figures

The `scans` entry of `content/blocks/drones.json`, shared content:

| Figure | Value |
| --- | --- |
| Coverage a drone | 20,000 m² |
| Cover | 1.0 |
| Drones | 3 to 12 |
| Scan time | 6 s |

- Range 3 km to the surface; place tolerance 2.5 m; travel step 25°.
- Drones get 180 s to reach their places and wait 120 s for the order, then come home.
- Resolution 0.1 % of the rock's mass.

### Wire and save

- Protocol 71: message 49 (scan command: request, start, cancel; no kind), message 50 (one result, at most 1,279 bytes, always with its map).
- The bay view (message 40) carries the scan: phase, rock, drones, in place, start and end ticks.
- Save codec 23, after codec 20's mining section: every player's results. A scan under way is not saved; its drones dock as all drones do.
- Codec 23 dropped each result's kind and vein hint. Older saves are refused by layout, as with every codec bump; no survey record is ever read.

### Not built, known limits

- A scan fits rocks up to about 150 m radius. Giants need far more than a player may have out. Tiers may raise coverage later.
- No drone battery or endurance: a scan costs no power.
- Places are not checked against other rocks or ships nearby.
- A player keeps 32 drone scans; the oldest gives way to a new rock.
- The result describes the rock as generated. Scrapes (R1) do not change it; cracks (R2) will need their parent's map.
- A ship that moves over 6 km from the rock gives the scan up.

## R1 as built

How to work on it is in `AGENTS.md`, "Rock edits (scrapes)". This is what exists and what does not.

### What exists

- The edit list per rock (`RockEditLog`), server-owned, append-only. A scrape is a capsule in whole numbers.
- One way to make an edit: `ServerHost.ScrapeRock`. The mining laser (R3) will call it.
- Bricks of 8³ cells at 0.25 m, one signed byte a sample, only where a scrape cut or came within a cell (`RockBricks`).
- Wire (protocol 65), save (codec 18), contact for hulls and loose containers, drawing with level of detail.
- The temporary debug scrape: `F10` in the pilot view and `--open rock-scrape` for captures.

### What changed from the spike

- Bricks hold the cut only, never the base shape. The edited rock is "inside the base shape, outside every cut".
- So bricks depend on the edits alone, in exact arithmetic: the same bytes on every machine. The spike's
  cross-machine risk is gone for which bricks exist and what they hold.
- The unedited surface is never drawn from bricks. Reached facets are clipped on their own planes with their own
  normals; the cut's walls are clipped to the base shape; both by the same planes. No seam, no normal jump.
- Level of detail: beyond half a metre a pixel the rock is its plain facets (`content/visuals/rock_edits.json`).
- A stream-in replay runs on a worker. A scrape costs under 0.2 ms to apply, not 10 to 30 ms.

### Figures

`content/rules/rock_edits.json`, shared content:

| Figure | Value |
| --- | --- |
| Cell, brick | 0.25 m, 2 m |
| Scrape radius | 0.25 to 2 m |
| Longest stroke of one scrape | 4 m |
| Edits and bricks a rock | 2,048 and 1,024 |
| Edited rocks, edits and bricks a world | 512, 65,536 and 16,384 |
| Debug scrape | 1 m radius, 0.4 m bite a pass, 600 m range, 0.1 s apart |

Measured (this machine, Release, one thread, a three-pass groove of 48 scrapes, 8 m long, 1 m radius):

| Figure | 20 m rock | 200 m rock | 2 km rock |
| --- | --- | --- | --- |
| Applying a scrape to the bricks | under 0.2 ms; 0.05 ms once the code is warm | same | same |
| Bricks the groove made (of which hold a cut) | 28 (24) | 39 (31) | 42 (34) |
| Base facets the patch replaces | 3,381 | 113 | 4 |
| Patch vertices | 42,240 | 23,277 | 27,894 |
| Patch build | 22 ms | 3.3 ms | 2.6 ms |
| Runs the shared mesh is drawn in | 45 | 7 | 2 |
| Wire, the whole groove | 468 B | 468 B | 468 B |

- The spike's bricks for the same groove: 80, 128 and 632; 29, 8.8 and 17 ms a scrape.
- A brick is 729 bytes on the server, on each client's simulation and on its render side.
- Memory reserved at start: about 14 MB a simulation side, 29 MB on the render side, 48 MB of vertex buffer.
- `make bench` scrapes a 199 m rock thirty passes: no managed allocation on any thread.

### Not built, known limits

- Ice shards cannot be scraped: their eight faces are not facets of the shared mesh.
- A patch is rebuilt whole when its rock changes. Cost grows with the rock's bricks; a rock near its limits lags.
- A small rock is slow to patch (22 ms for a 20 m one, 76 ms for an 8 m one): its facets are smaller than a cell,
  so thousands are reached.
- A patch over 600,000 vertices is not drawn; the rock shows its plain facets. Collision still has the groove.
- Where passes cross, the wall's shading is faceted: a sampled field cannot hold a crease.
- The pointer, the target outline, exhaust marks' ray and the shield's contact patch still use the unscraped surface.
- A rock's edits reach a client by sensor range to its surface, gas not asked; farther rocks show no grooves.
- Contact samples a box at its centre, corners and nearest point, as before: a groove narrower than a block can
  be bridged or missed.
- A block pressed more than a cell (0.25 m) into a groove's wall in one tick reads no wall there and is pushed
  out by the base shape instead: fast impacts into a groove can bounce out of it.
- A full patch buffer (2,000,000 vertices) drops the patch drawn longest ago and tries again later.
- Yield came with R3 (see "R3 as built").

## R3 as built

How to work on it is in `AGENTS.md`, "Mining lasers, action groups and ore chunks". This is what exists.

### What exists

- `miningLaser`: a face mount with `laser` figures and `laserTiers` in `blocks.json`. Only Common is in the palette.
- Action groups 1 to 10 (key `0` is group 10). A mount's group is in the design (file version 7, wire version 5);
  a hull can change it without a rebuild (`SetActionGroup`, saved).
- Firing: the client sends the groups that fire and the aim ray when they change. The server plans the lasers
  before the hull round (group, block there, gimbal, own hull clear, power asked), fires after it and cuts through
  `ScrapeRock`.
- Ore: each scrape's volume times the rock's density, shared out by the interior sample at the cut's deepest point.
  Each ore adds up per laser; at the mark it breaks off as a chunk.
- Chunks: a fixed pool of 256 apart from loose containers, drifting, bouncing off rocks and hull blocks, replicated
  by sensor cover and saved.
- Wire: protocol 68, messages 44 to 48. Save: codec 20.

### Why a pool of its own for chunks

- Containers carry a payload, claims, tractor and fetch state; every tractor and drone path reads that pool.
- Chunks carry resource, grade and mass only; since R4 also the transient claim of the beam or drones that have one.
- Apart, no container path had to learn to skip them: a beam looks at chunks only after it found no container.

### Figures

| Figure | Value |
| --- | --- |
| Laser range, gimbal | 60 m, 40° off its face |
| Cut radius, rate | 0.5 m, 0.25 m deep a second |
| Power while firing | 3 MW, paid all or nothing after the tractors |
| Scrape | 0.25 m bite, at least 0.15 s apart; a new stroke after 0.5 s off the rock |
| Chunk | 300 kg of one ore; thrown out from the rock's centre at 2 m/s |
| Chunks | 64 a sector, 256 a world, 30 minutes unattended |
| Lasers fired a hull | 16; 64 can have a group changed without a rebuild |

`make bench` fires one laser at a 199 m rock for 16 s: 15 scrapes, chunks freed, no managed allocation.

### Not built, known limits

- Beams pass through other ships; only the firing hull's own blocks block them.
- A beam is drawn from the server's end point, sent every tick it fires (unreliable); a lost packet holds the beam.
- Ore is an estimate from the scrape's volume, not the volume the bricks really lost. Rock that holds no ore is lost.
- Each ore of a cut makes its own chunk with the rock it came in, so two ores of one cut count that rock twice.
- Partial ore is forgotten when the laser moves to another rock or stops for good.
- A rock fills up: 2,048 scrapes a rock, 65,536 a world. A full rock still shows the beam and yields nothing.
- No heat (I2), no weapons in groups yet, no turret models: the emitter is fixed, the beam turns within its gimbal.
- Power is paid all or nothing each tick: a laser on a flat battery fires on the ticks the generator's trickle pays
  for, and a beam cut short loses the cut it had not yet made into a scrape.
- Chunk contacts generate one sector at a time, as loose containers do: chunks spread over two sectors take turns.
- Group changes are not mirrored in the editor (placing is).
- The F10 debug scrape stays: `make bench` and the `--open rock-scrape` captures still use it.

## R4 as built

How to work on it is in `AGENTS.md`, "Ore collection (R4)". This is what exists and what does not.

### What exists

- `oreBin`: a box block of any size up to 8 a side. It holds 2,400 kg of raw ore a cubic metre, one ore at a time,
  with the mass-weighted grade. Its ore weighs the ship down, on the server and in the pilot's prediction.
- `oreHopper`: a box block (up to 2 a side) with one intake face, chosen at placement by the bay's hatch rule.
- A delivery goes into the bins that hold that ore, in design order, then into empty ones; what one bin cannot take
  spills into the next.
- Tractor beams take chunks by themselves. A free beam looks at loose containers first and at chunks only when it
  took none. It takes a chunk when it sees the chunk, sees a hopper's intake and the bins have room for all of it.
- The path: to a point outside the intake (clear of it by the chunk's half edge and 0.15 m), then straight in at
  0.6 m/s. The chunk is gone when its centre is a half edge inside the face. Power, stall and retry are the
  containers' figures (`tractor` in `blocks/cargo.json`).
- Drones: "Pick up ores" in the popup, per ship, saved. The drones carry a chunk to the hand-over point in front of
  a beam; the beam leads it in. One fetch a ship at a time, containers and chunks together.
- The pilot is told the bins of the hull flown (message 51) and sees them in the Ship window under ORE.
- A lost bin loses its ore. A wreck piece keeps the ore in the bins it has.
- An edit or repair keeps ore by overlap of old and new bins first, then puts the rest into any bin that takes it.
- Wire: protocol 70. Save: codec 22.
- The Prospector has a hopper on the stern deck and four 1 x 1 x 2 bins under the keel: 19.2 t of raw ore.

### Figures

| Figure | Value |
| --- | --- |
| Bin capacity | 2,400 kg a cubic metre (`oreKgPerCubicMetre`) |
| Bins, hoppers a design | 64, 16 |
| Chunks a beam looks at | 256 a tick, after 256 containers |
| Bins message | 847 bytes at most |
| Chunk record | 85 bytes (was 77): hull, mount, stage, powered added |

`make bench` runs `--open ore` for 20 s: lasers, chunks, a beam's pull into the hopper, ore in a bin, no managed
allocation.

### Not built, known limits

- The editor does not preview ore an edit would dump. The server dumps it and logs the kilograms.
- No way to dump, move or sell ore. A bin that holds a little of one ore is closed to every other ore.
- No key or button sends drones for one chunk by hand; only "Pick up ores" does.
- Drone answers for a chunk use the container's words ("fetching", "delivered", "no rack room").
- A chunk is taken whole or not at all. A chunk heavier than the bins' room, or than a beam's limit (100 t), is
  never taken. A lean chunk is large: 300 kg of ore at a 5 % grade is 6 t.
- The bins fill fast on the Prospector: two lasers fill 19.2 t in about 40 s on a lean rock.
- The room check counts every chunk the ship's beams hold against the ore asked for, whatever ore they are. It can
  refuse a chunk that would have fitted. A beam can also take a chunk and let it go again, when a chunk of another
  ore reached the last empty bin first.
- A hopper has no capacity or rate of its own: any number of beams feed one intake at once.
- Chunks do not collide with each other, and a held chunk passes its own ship's blocks, as a held container does.
- A beam to a chunk held by another player's ship is drawn at the own ship's instant, not that ship's.
- Bins have a plain banded model; what a bin holds does not show on it.
- A drone on a hull that is not the one flown picks up ores, but its bins are told only to the pilot of that hull.

## Open

- The drone bay's final look. A first model is built (frame, plating, a hatch rim, two orange doors, a light);
  its figures are in `visuals/propulsion.json`.
- The figures for crust, veins and pockets. First guesses are in `content/galaxy/interiors.json`; tune from
  `--open rock-interior` captures.
- The heat game's figures: heat rate, cooling, the window, what overheating does.
- The Ore Cargo window's layout.
- Where markers fade out, and whether the command view shows chunks as icons too.
- Automated mining by NPCs and captains: out of scope now; only the order model above is designed for it.

## I1: rock interiors (built)

### What it is

- `RockInterior` in `Prospect.Game/World`: a query-only description of every rock's inside.
- From the seed, the sector and the rock's index. The same on every machine, for every player.
- Its own streams (`rock-interior` key, then vein, pocket and unstable draws). No existing stream is touched.
- No rock, list or composition changed. No `GenerationVersion` or protocol bump.

### How it is laid out

- Normalised space: a point's distance from the centre divided by the surface's distance that way.
  The rock is a unit ball there; the crust follows the surface.
- The surface comes from `RockInteriorSurface`: a 32-cell cube map per shape, built with `DetMath`.
  It follows `AsteroidShape` within about a percent of the radius.
- Order of a point: crust, then pockets, then veins, else mantle. Outside the rock reads as crust.
- Core pocket: the rarer mix-in (`Mix2`, else `Mix1`). Only rocks with a mix-in have one.
- Unstable pockets: ice or volatile gas, no ore. Flagged (`RockSample.Unstable`) for cracking later.
- Ice shards (`AsteroidShape.Icicle`) have an ice body.

### How ore adds up

- The core pocket takes `pocket.holds` of its mix-in, at most `maxLocalOre` per point.
- The rest is spread over crust, mantle and veins by their factors (`crust.ore`, `veins.ore`).
- Layer volumes are worked out, not counted. Crust is exact for the table; veins and pockets are close.
- A rich rock is evened out until no point holds more than `maxLocalOre`.
- Measured: over 93 sample rocks the integrated shares are within 3.4 % of the published ones
  (`bulkTolerance` is 10 %).

### The API

- `RockInterior.Describe(galaxy, rockRef, asteroid, out interior)`: once per rock.
- `interior.Sample(local, out sample)`: layer, material, pocket kind, depth, shares, strongest ore and grade.
- `interior.Average(centre, radius, perAxis, out volume)`: a lattice of up to 16³ points, for a scan.
- `RockInterior.ToLocal(asteroid, offset)`: world offset into the rock's own frame.
- No memory taken. Debug build: about 0.7 µs a sample, 19 µs a describe.

### First-guess figures (`content/galaxy/interiors.json`)

| Figure | Value |
| --- | --- |
| Crust depth | 0.14 of the surface distance, at most 60 m, wobble ±35 % |
| Crust ore | 0.45 × mantle |
| Veins | 3 to 7, radius 0.04–0.09, taper ±40 %, 14 segments of 0.08–0.13, twist 0.55 |
| Vein ore | 3 × mantle |
| Core pocket | 45 % of rocks with a mix-in, radius 0.12–0.24, within 0.35 of the centre, holds half the mix-in |
| Unstable pockets | two tries at 25 %, radius 0.06–0.12, ice or gas equally |
| Most ore at a point | 92 % |

### Check it

- `make run ARGS="--start local --open rock-interior --rock-interior-size small|medium|giant --rock-interior-resource iron --capture f.png --capture-after 4"`.
- Captures only. A real rock near home, cut through its centre; a legend lists its layers.
- Colours: crust light brown, mantle tinted by its ore, veins and core pocket in their ore's colour, ice pale blue,
  gas orange.
- Captures from the first build: `artifacts/captures/rock-interior/` (not committed).

### Limits

- Veins are polylines of capsules: they bend at their points.
- Crack pieces (R2) take indices past the generated list; they will need their parent's interior, not their own.
- The crust wobble pattern is per shape, not per rock (rocks are turned, so it does not repeat visibly).

## Spike results: how a rock holds edits

A prototype on branch `rock-edit-spike`, not merged. It compares two ways to hold scrapes and cracks.
Code: `Prospect.Game/World/RockEditing` (shared), `RockEditCapture` (client), `RockEditSpikeTests`.

### Recommendation (now decided, see "Rock edits")

- The edit list is the truth. It is what the wire and the save carry. Geometry is a cache rebuilt from it.
- Split the two jobs:
  1. A crack makes new rocks. Each piece is a radial field (candidate A), sampled from its own centre.
     Cheap at any size, contact exactly matches the drawn mesh.
  2. A scrape is local detail. It goes in sparse bricks (candidate B), at a fixed size in metres, only where a
     laser touched. It is the only way a groove shows on a 200 m or 2 km rock.
- Never voxelize a crack's cut face. On a 200 m rock that alone is 13,500 bricks, 10 MB and 6 million triangles.
- Limit cracks by size in content (charges per square metre of cut). Do not crack a giant whole; crack a piece off it.
- Before building it for real: stitch bricks to the facets (see risks), and give bricks a level of detail.

### What was built

- Edits: `RockEdit` and `RockEditCodec`. A scrape is a capsule between two points; a crack is a rough plane with
  the server's seed. Every edit is quantized when made, so all sides apply the decoded values.
- A, radial: `RadialRock`. A radius per vertex of the shared finest sphere (40,962 directions). A scrape lowers
  radii to where the ray enters the capsule. A crack cuts the centre's piece in place and samples the other piece
  again from its own centre.
- B, bricks: `BrickRock`. Bricks of 8³ cells at 0.25 m, one signed byte a sample, made only where an edit changed
  something. The surface is marching tetrahedra on the Freudenthal split; contact reads the same interpolant.
  Base triangles whose cone a changed brick reaches are drawn by the bricks instead.
- Script: `RockEditScript` on shape 0. A groove of three passes, 0.3, 0.8 and 1.3 m deep, 8 m long, 1 m laser
  radius, 16 segments each. A crack 15 % of the radius off centre, tilted.
- Capture: `--open rock-edit --rock-edit-spike scrape|crack|both --rock-edit-size small|medium|giant
  --rock-edit-show base|a|b|all --rock-edit-view whole|groove`, with `--capture` only. Render-only; nothing in the
  simulation changes.
- Bench: `ROCK_SPIKE_SIZES=10,100,1000` (radii) with `-method Prospect.Tests.RockEditSpikeTests.Measure`, Release.

### Numbers

Release build, one thread, this machine. Rock diameters 20 m, 200 m, 2 km.

| Figure | A radial | B bricks |
| --- | --- | --- |
| Scrape segment, 20 m rock | 0.28 ms avg | 29 ms avg, 321 ms first |
| Scrape segment, 200 m | 0.10 ms | 8.8 ms avg, 61 ms max |
| Scrape segment, 2 km | 0.23 ms, but moves 0 vertices | 17 ms avg, 448 ms first |
| Vertices or bricks the groove changed | 10,914 / 112 / 0 vertices | 80 / 128 / 632 bricks |
| State per edited rock | 160 KiB a piece, any size | 79 / 113 / 476 KiB for the groove |
| Crack, 20 m | 0.47 s | 2.3 s, 325 bricks, 276 KiB |
| Crack, 200 m | 0.39 s | 42 s, 13,560 bricks, 9.8 MB, 3.0 M triangles a piece |
| Crack, 2 km | 0.39 s | not finished in 10 min; about 100x the 200 m figures |
| Volume lost by a crack | 0.38 / 0.08 / 0.05 % (far piece re-centred) | none (exact cut, quantized) |
| Contact vs drawn surface, worst | 0.012 / 0.023 / 0.18 mm | 0.002 / 0.023 / 0.18 mm |
| Seam between bricks and base facets | none (one mesh) | 57 / 53 / 67 mm overlap at the edge |
| Same bits on two threads | yes | yes |

The wire, both candidates (same edit list):

- A scrape that starts a stroke: 21 B. Each next segment of the stroke: 9 B. A crack: 13 B.
- The scripted groove, 48 segments in 3 strokes: 468 B.
- A heavily mined rock, 2,000 segments in 100 strokes and 3 cracks: 19,239 B.

### Captures (native pixels)

In `docs/roadmap/rock-edit-spike/`. Left to right: base, A, B (or A, B for grooves).

- `groove-small-a-b.jpg`: 20 m rock. Both cut the groove. A's walls fan out radially; B is clean, with blocky
  shading where its normals meet the facets'.
- `groove-medium-a-b.jpg`: 200 m rock. A dents two facets; the groove is gone. B shows it, with a sawtooth seam
  along one side.
- `groove-giant-a-b.jpg`: 2 km rock. A shows nothing. B shows the groove; a faint seam line and a shading change
  where the bricks take over.
- `crack-small-base-a-b.jpg`, `both-small-base-a-b.jpg`: 20 m rock cracked, pieces pulled apart. Both work.
- `crack-medium-base-a-b.jpg`: 200 m rock. B's pieces are missing: 6.3 M triangles do not fit the frame's
  48 MB of dynamic vertices.
- `both-giant-a.jpg`: 2 km rock, A only. The crack and rough break look right in 0.7 s.

### Risks

How R1 answered them is in "R1 as built". Cracks (R2) still face the radial and giant-rock ones.

- Seams (B): bricks overlap base facets by up to 6 cm, and normals jump there. Needs clipping to the cones and a
  skirt, or one surface for the whole rock.
- Level of detail: A is free (the coarser spheres are prefixes of the finest, so take the first radii). Bricks have
  none; far away they must fall back to the facets, which hides small grooves (fine) but not deep pits.
- Giant rocks: brick count grows with the area mined. A long session on a giant is megabytes per rock, on every
  client that has it loaded. Needs a cap per rock, or merging old bricks into coarser ones.
- Streaming: a client that loads a mined rock replays its edit list on a worker. B at 10-30 ms a segment means a
  2,000-segment rock takes 20-60 s; cache the brick bytes or send them instead of replaying.
- Saves: an edit list per rock in `WorldSnapshot`, keyed by sector and rock index. Crack pieces are new rocks
  that name their parent; they need indices past the generated list.
- Cross-machine bits: both use `MathF` (`Squash` already does, `Acos` in B's cone test). Same machine is
  bit-exact; across machines a sample can differ by one step. Drawing tolerates it; anything discrete
  (which bricks exist, ore counts) must use `DetMath` or come from the server.
- Overhangs and tunnels: A cannot hold them; a cut is projected along the ray from the centre. Bricks can.
- Cost of B's crack is in sampling the base shape (`Facet` per sample); a real build would cache facet planes.
