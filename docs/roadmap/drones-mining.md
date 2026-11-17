# Drones and mining

Product direction from the brainstorm on 2026-11-16. Built so far: R1, the scrape foundation (see "R1 as built").

## Why

- Mining is the core loop: find a rock, learn what is inside, take it apart, haul the ore home.
- Drones do the close work a ship cannot: detailed scans, charges, fetching cargo far away.
- Co-op mining is a big part of the game idea.

## Decided

### Drones

- Drones are kept and launched from a drone bay block. Not in cargo racks.
- The drone scan replaces nothing: the asteroid scanner module keeps its long-range, rough composition.
- A detailed map of a rock is a group job:
  1. The player asks for a detailed map of a rock.
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
- While debugging, every method is unlocked. A tech tree decides unlocks later.

### Drone life and the bay

- Drones can be destroyed. Scan drones come back and are reused; charge drones are used up.
- A lost drone is gone; the bay rebuilds one slowly. Buying and building comes later.
- The bay has tiers, like sensors and tractors: capacity, launch rate (how many at once), rebuild speed.
  Figures are content.

### Rock interiors: why crack

- Every rock has a fixed interior from the world seed, the same for every player:
  1. Crust: rock and weathered, low-grade ore.
  2. Mantle: normal grade.
  3. Veins: twisting tubes of high-grade ore through the rock.
  4. Sometimes a core pocket of a rare mix-in.
- Scraping takes the crust: slow, low grade. Cracking cuts through veins and exposes them; the laser then
  scrapes vein faces at high grade.
- Three scan levels:
  1. Scanner module: surface average composition.
  2. Drone group scan: true bulk composition and a hint that veins exist.
  3. Detailed drone scan: a 3D hologram of veins and pockets, and a heat map of easy and unstable pockets.
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

### Built to be extended

- Every new mechanic is a set of parts: data in `content/`, shared rules in `Prospect.Game`, a client view.
  A new tool, drone kind or scan level is a new content entry, not new code paths.
- Orders, not input: a drone, a mining laser or a fleet ship acts on an order (target, task, limits).
  The player gives orders through the interface. Later a captain in a second ship, or an NPC miner, gives
  the same orders. One executor on the server, whoever gave the order.
- The server decides every result: what a scan reveals, where a rock breaks, how much ore comes off.

## Proposed (technical, not decided)

- Drones are small real hulls flown by the server with `HullSim`, so they collide, can be shot and use power.
  A drone that is used up (scan, charge) is removed when its job ends.
- An order is a small record: kind, target, owner, limits. The executor runs it each tick without allocation.

## Build plan

Three tracks. A track's steps run in order; the tracks run side by side, each in its own worktree.

### Track R: rock edits and mining lasers

1. R1, scrape foundation: the edit list per rock, scrape bricks, wire, save, collision, drawing, brick seams
   and level of detail. A temporary debug scrape (local games only) drives it until the laser exists.
   **Done** (2026-11-17), see "R1 as built".
2. R2, cracks: radial pieces as new rocks, rough natural break faces, wire and save of the pieces.
3. R3, mining laser: a face mount that scrapes on an order; it frees floating ore chunks.
4. R4, ore: chunks pulled by tractor into an ore hopper, raw ore containers, the Ore Cargo window.

### Track D: drones

1. D1, orders and the bay: the order record and its one executor on the server, the drone bay block with tiers,
   drones as small server-flown hulls, launch and return.
2. D2, fetching cargo: drones bring containers back; two or more lift a heavy one; claims respected.
3. D3, scans: group scan and detailed scan (needs rock interiors, track I).
4. D4, charges: drones place charges along the player's cutting plane (needs R2).

### Track I: rock interiors and heat

1. I1, interiors: crust, mantle, veins and pockets from the seed; what scraping and cracking yield.
2. I2, heat cracking: the special laser and the heat window.

## R1 as built

How to work on it is in `AGENTS.md`, "Rock edits (scrapes)". This is what exists and what does not.

### What exists

- The edit list per rock (`RockEditLog`), server-owned, append-only. A scrape is a capsule in whole numbers.
- One way to make an edit: `ServerHost.ScrapeRock`. The mining laser (R3) will call it.
- Bricks of 8³ cells at 0.25 m, one signed byte a sample, only where a scrape cut or came within a cell (`RockBricks`).
- Wire (protocol 64), save (codec 17), contact for hulls and loose containers, drawing with level of detail.
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
- Nothing is yielded: no ore, no chunks. Scraped volume is not counted.

## Open

- What the drone bay looks like and how drones leave and come back (a hatch face?).
- The exact generation of crust, veins and pockets, and their figures.
- The heat game's figures: heat rate, cooling, the window, what overheating does.
- The Ore Cargo window's layout.
- Automated mining by NPCs and captains: out of scope now; only the order model above is designed for it.

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
