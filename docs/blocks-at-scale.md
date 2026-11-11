# Blocks at scale

How ships of thousands of blocks and stations of 150,000 stay fast. Stage 1 is built and tested; stages 2–5 remain planned.

The goal and the block model are in `direction.md` under "Decided: building".

## Decided: data

- **A design never changes and is shared.** Many hulls point at one design.
- **A hull adds only two things:** which blocks are alive (a bitset sized to its design) and, on the server, hit
  points per block.
- **Stats are running sums per hull.** They change when a block is lost. Nothing scans all blocks in the tick,
  the frame or the interface.
- **A hull keeps turning around its original centre** when it loses blocks. Its mass and inertia do change.
- **Slopes come with the editor.** The design format carries a version so shapes can be added.

## Decided: how size changes a block

- **Each block type has its own rule,** in content: how thrust, power, shield, hull points, cargo and mass grow
  with the block's size. A big engine need not be many small engines.
- **A stat with no rule grows with the cells the block fills.**
- **Materials come later** and will change mass and hull points on top of this. The rule format must leave room.

## Decided: format and limits

- Version 2 adds `"version": 2` and optional `"size": [x, y, z]` per block. Omitted size is `[1, 1, 1]`.
  Unversioned version 1 files still load. `at` names the centre of the minimum occupied cell.
- The block id is its zero-based file index. Validation never reorders that array.
- `content/blocks/blocks.json` owns the limits: 64 cells per box side, 4,096 cells across a design,
  150,000 blocks. Walls are at least one cell thick. Coordinates and exclusive upper edges stay within
  `[-4096, 4096]`, and each occupied axis span is at most 4,096.
- A 64-cell wall avoids thousands of small blocks; a 4,096-cell span leaves room for stations while bounding
  coordinates. The count meets the station target. These are content limits, not wire encodings.
- Boxes cannot overlap; touching faces are allowed. At least one core is required, matching existing rules.
  A median spatial tree validates overlap without expanding boxes into cells or scanning all pairs.

## Decided: size rule notation

- Each block type may have `"sizeRules": { "powerOutput": { "volumeExponent": 1.25 } }`.
  The result is `base * cells^volumeExponent`. Missing rules mean exponent 1; 0 is constant,
  values below 1 grow slower, values above 1 grow faster. Accepted exponents are 0 through 2.
- All twelve numeric BlockType stats support rules. Core, forward-only and drive kind remain categories.
  Crew rounds down per block. One-cell blocks return the original number directly, under every exponent.
- Content examples: constant core power draw, generator power exponent 1.25, crew exponent 0.5.
  Materials will multiply the scaled result separately; the rule object leaves room for that extension.

## Stage 1 evaluation

- `BoxDesign` owns an immutable block array. `BoxHullSums` accepts a design and optional survivor set,
  then returns `FlightParams`, `HullStats` and the survivors' centre. World multipliers apply at evaluation.
- Inertia uses solid rectangular boxes and the parallel-axis theorem about the intact centre.
  Damage changes mass, bounds and inertia, but never moves the pivot. The first surviving drive in file
  order selects the kind; only drives of that kind supply jump power.
- Running sums use doubles in a fixed-order binary tree. Losing a block clears its leaf and updates only
  ancestors: O(log n), no allocation. Reading figures is O(1). A fresh evaluation uses the same tree order.
- The 4,096-loss test compares every output field at checkpoints, including the empty hull: zero difference.
  The independent unit-cell inertia reference allows relative error `1e-6` after conversion to float;
  centre error is below `1e-12` grid cells. Legacy live figures remain bit-for-bit unchanged.
- This is a tooling implementation, separate from the live 64-bit path. Its full sum tree is memory-heavy;
  stage 2 must budget that cost before adopting it for many hulls. No production-scale memory claim yet.

## Stage 1 measurements

Debug build, Linux x64, .NET 10.0.13, seed 1234, measured during `make check`.
Times include generation, validation with spatial-tree construction, and sum construction plus evaluation.
Allocation is current-thread managed bytes, not process peak RSS; retained sums exclude headers and shared design.

| Fixture | Generate | Validate | Evaluate | Allocated | Retained sums |
| --- | --- | --- | --- | --- | --- |
| 4,096 boxes | 0.22 ms | 58.58 ms | 8.29 ms | 3,785,000 B | 2,031,616 B |
| 150,000 boxes | 5.85 ms | 3,501.16 ms | 263.38 ms | 194,223,720 B | 130,023,424 B |

- `BoxDesignTests.Generate` uses integer LCG arithmetic and nonoverlapping slots, with mixed-size plating,
  engines, generators, shields, cargo, quarters and one core. No large fixture files are stored.
- Each fixture asserts a 15-second combined tooling budget and less than 256 MiB allocated.
  These budgets are not tick or frame budgets. Cross-machine timings have not been measured.

## Stage 2 handoff

- Keep file-order block ids and the fixed original pivot; replace the live alive mask and hit-point storage.
- Send versioned box designs and block losses; update the wire version only when that protocol changes.
- Implement box collision and drawing before admitting sized boxes or more than 64 blocks into the live loader.
- Reuse the seeded fixtures and reference tests. The validation tree is temporary, not the future capped
  collision tree. Budget per-hull sum memory and cross-platform size-rule evaluation before production use.

## Decided: network

- **A design is sent once per client,** found by a hash of its content. Hull records carry a short design id.
- **Lost blocks ride in the snapshot,** next to the hull's position, and repeat until the client confirms them.
  Position and damage always arrive together, as they do today.
- **Big transfers have their own lane:** designs and full block sets use a separate ordered channel and a byte
  budget per client, so they never hold up gameplay messages.
- **A send that is refused is retried.** Damage must never be lost.
- **Heavy work stays off the simulation thread:** compressing, unpacking and building search trees run as jobs.

## Decided: collision

- **Each design has a search tree over its boxes,** built once. Each hull knows which branches still have
  anything alive.
- **A block is a box, not a ball.** Box against box between hulls; box against the asteroid surface.
- **Work per contact is capped and the order is fixed:** tree visits, box tests and contacts kept. Server and
  client must pick the same contacts, so the cap is never a time limit.
- **When the cap is hit,** the push comes from a small fixed set of contacts, and only tested blocks take damage.

## Decided: drawing

- **A design is drawn as chunks of merged mesh,** hidden faces removed. Undamaged hulls share them.
- **Losing a block rebuilds its chunk and the chunks that touch it,** on a worker, uploaded without stalling a
  frame.
- **Far hulls use a coarser mesh.**
- **Nothing scans all blocks per frame.** Draw and upload work per frame is capped.

## Stages

Each stage leaves the game running, with `make check` and `make bench` passing.

1. **Rules and test designs.** Limits, block ids, how size scales stats. Seeded test designs: a ship of a few
   thousand boxes, a station of 150,000.
2. **Small slice, end to end.** A design of a few hundred boxes of mixed sizes: stored, sent, damaged, drawn and
   collided correctly. The 64-block cap is gone.
3. **Collision at scale.** The search tree and the caps. A tick budget is asserted for two big hulls fighting.
4. **Drawing at scale.** Chunks, worker rebuilds, coarse meshes.
5. **Full load.** 10 stations of 150,000 boxes and 20 big ships, different designs, some damaged, on a real
   connection with 100 ms round trip and 1% loss.

## Not part of this work

- **128 players.** That needs interest management and delta snapshots, which are their own step in
  `direction.md`. This work must not claim it.
- **The editor and the ship generator.** They build on this.

## Known costs, estimated, not measured

- A 150,000-box design is about 4 MB before compression. Ten different ones are about 40 MB to download on join.
- Ten different 150,000-box stations can need about 1 GB of mesh if faces are not removed and far ones are not
  simplified. Stage 4 has to bring that down.

## Open

- The slowest machine the full-load scene must run on. Working target until tested there: a MacBook Air M4
  at 60 frames per second.
