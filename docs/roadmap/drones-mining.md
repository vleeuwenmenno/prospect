# Drones and mining

Product direction from the brainstorm on 2026-11-16. Nothing here is built yet.

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
   Done: the shared query. The yield rules themselves come with R3 and R4. See "I1: rock interiors (built)".
2. I2, heat cracking: the special laser and the heat window.

## Open

- The drone bay's model (hatch, doors, lights).
- The figures for crust, veins and pockets. First guesses are in `content/galaxy/interiors.json`; tune from
  `--open rock-interior` captures.
- The heat game's figures: heat rate, cooling, the window, what overheating does.
- The Ore Cargo window's layout.
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
