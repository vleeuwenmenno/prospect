# Blocks at scale

How ships of thousands of blocks and stations of 150,000 stay fast. Stage 1 is built and tested; stage 2 is under way; stages 3–5 remain planned.

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

- `BoxDesign` owns an immutable block array. `BoxHullSums` is the design's evaluator: it turns the blocks a
  hull has left into `FlightParams` and `HullStats`. World multipliers apply when figures are read.
- Inertia uses solid rectangular boxes and the parallel-axis theorem about the intact centre.
  Damage changes mass, bounds and inertia, but never moves the pivot. The first surviving drive in file
  order selects the kind; only drives of that kind supply jump power.
- Running sums use doubles in a binary tree of fixed shape, one leaf per group of 32 blocks. Losing a block
  adds its group up again and redoes the leaf's ancestors: no allocation, no full scan. Reading figures is O(1).
- Figures are a pure function of the blocks left: any order of losses, and sums built afresh, agree to the
  last bit. An independent scan in the tests agrees within `1e-6`.
- This is the live path, on the server and the predicting client. Size rules take powers with `DetMath`:
  the same on every machine, within `1e-12` of the true power. Exponents 0, 0.5, 1 and 2 are exact.
- Crew within a billionth of a whole number counts as that number, so a power never costs a person.

## Sum memory

A node is 200 B. A hull has two nodes per 32 blocks, less one. Its design keeps the same amount once.

| Blocks | Sums per hull | Block state, client | Block state, server |
| --- | --- | --- | --- |
| 64 | 600 B | 1,404 B | 1,660 B |
| 300 | 3,800 B | 7,468 B | 8,668 B |
| 4,096 | 51,000 B | 100,692 B | 117,076 B |
| 150,000 | 1,875,000 B | 3,693,780 B | 4,293,780 B |

- A block state is a header, alive words, sums, a 4-B-per-block loss journal and live collision branch counts. The server and client authority baseline add hit points (4 B/block). Prediction owns another restorable block state.
- The client keeps a receive state and a published state separately while recovery can be incomplete.
  The table is per state; a known client hull can therefore use twice its client column.
- The store rounds a state up to a power of two, and past 64 KiB to a multiple of 64 KiB.
- What a block adds is kept once per kind of block (type and size), not per block: the design holds 4 B per
  block for it. Keeping it per block is no faster (0.38 against 0.41 microseconds a group) and costs 200 B a block.
- A loss with its read costs about 0.55 microseconds in a release build, on the 150,000-box fixture.

## Stage 1 measurements

Debug build, Linux x64, .NET 10.0.13, seed 1234, measured during `make check` on a busy machine.
Times include generation, validation with spatial-tree construction, and sum construction plus evaluation.
Allocation is current-thread managed bytes, not process peak RSS; sums per hull exclude the design's own copy.

| Fixture | Generate | Validate | Evaluate | Allocated | Sums per hull |
| --- | --- | --- | --- | --- | --- |
| 4,096 boxes | 0.25 ms | 119.03 ms | 6.12 ms | 1,857,696 B | 51,000 B |
| 150,000 boxes | 10.09 ms | 3,787.50 ms | 108.79 ms | 66,764,256 B | 1,875,000 B |

- `BoxDesignTests.Generate` uses integer LCG arithmetic and nonoverlapping slots, with mixed-size plating,
  engines, generators, shields, cargo, quarters and one core. No large fixture files are stored.
- Each fixture asserts a 15-second combined tooling budget and less than 256 MiB allocated.
  These budgets are not tick or frame budgets. Cross-machine timings have not been measured.

## Stage 2 handoff

- Keep file-order block ids and the fixed original pivot; replace the live alive mask and hit-point storage.
- Send versioned box designs and block losses; update the wire version only when that protocol changes.
- Implement box collision and drawing before admitting sized boxes or more than 64 blocks into the live loader.
- Reuse the seeded fixtures and reference tests. The validation tree is temporary, not the future capped
  collision tree.

## Stage 2 plan

Five slices, one after another. Each leaves `make check` and `make bench` passing.
The loader and network now admit sized boxes and multiple block words within shared resource budgets.
Slice 1, slice 2a (content admission) and slice 2b (block-state replication) are built.
Slice 2c catalogue synchronization and runtime GPU meshes and slice 3 shared box collision are built.
Slices 4 and 5 are implemented; focused headless checks cover normal loading and network admission.
The parent final make check and review remain the completion gate.
Slice 2b was checked with targeted headless tests; the parent integration gate is still required.

1. **Storage and stats.** Block sets, the block state store, box data and running sums in the live path. No wire change.
2. **Wire.** Designs by hash, losses as changes, the bulk lane. A received design gets its mesh at run time.
3. **Collision.** Box against box and box against the asteroid surface, over a tree per design.
4. **Client.** Drawing, picking and the shield shell on boxes and block sets. The shell's vertex budget is per
   block and tied to the 64 limit: it is reworked for boxes here, not just raised.
5. **Admit.** The limits go. A mixed design of a few hundred boxes is flown, damaged and seen in session tests.

## Slice 2b: implemented contract

- Protocol 22 poses carry a required revision and up to 16 contiguous file-order loss ids.
  Per-hull applied acknowledgements drive retries; failed sends never advance the acknowledged revision.
- Gaps request a complete alive set. Ordered bulk channel 2 uses RakNet ordering stream 1;
  gameplay reliability remains on stream 0. Bounds are in `content/net/blocks.json`, hashed at admission.
- The client holds at most 256 latest incomplete poses. Only matching block revisions reach prediction or views;
  interpolation history resets on a loss. Recovery chunks never mutate a published state directly.
- Focused tests cover >64 ids, late join over loopback and UDP, lost acknowledgements, duplicates, reordered losses,
  gap recovery, refused bulk sends, bounded chunk assembly, timeout, revision validation and warm allocation.
- Next integration: keep stable design ids and file-order block ids when replacing the shared design table.
  Design transfer must validate designs before allocating hull state, and share the bulk budgets.
  Shared collision, client geometry and final resource admission are now implemented.

## Decided: stage 2 storage

- **The live design is box data.** `HullDesign` holds boxes; the file index stays the block id.
- **Alive blocks are a block set:** 64-bit words, sized to the design. No single `ulong` mask is left in the live path.
- **Block state has its own store,** addressed by handle: alive words, hit points and running sums.
  The client keeps an authoritative baseline and an isolated prediction copy, restored before replay.
  Predicted losses never alter published authoritative blocks or revision acknowledgements.
- **Memory is taken when a hull appears, never at a loss.** Sums start as a copy of the design's intact tree.
  A loss is signalled explicitly; nothing compares block sets to find one.
- **One sum leaf covers a group of blocks** (32 to start). A loss rescans one group, then its ancestors.

## Decided: stage 2 network

- **Content must match to join.** Block catalogue, rules and evaluator version are hashed and compared at
  admission. The same design bytes on other block numbers would fly differently.
- **A design is named by the hash of its canonical bytes.** The receiver validates, then hashes again itself.
  Its cache starts with its own `content/` designs.
- **A loss message says where it starts and how many.** Only the next contiguous losses are applied; repeats do
  nothing; a gap asks for the full block set on the bulk lane.
- **The client confirms what it applied, per hull.** Nothing is inferred from which snapshot arrived.
- **A hull's pose is used only with the block set it belongs to.** A record the client cannot complete is held.

Bulk lane limits, all in content: transfer size, transfers at once, bytes kept, request rate, time to finish.
No compression in stage 2.

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

## Slice 2c renderer handoff

Network 2c synchronizes the immutable server catalogue at admission, including cache misses and block recovery.
Runtime meshes are wired through App, CatalogueMeshes, SceneRenderer and WorldView. Shared resource admission replaces the 64-block/unit-cell guard.

- Observe `SessionSnapshot.Content` by reference. On `SessionState.Active`, it contains server-ordered designs,
  `PodDesign`, `DroneDesign` and `StartingFleet`; the loaded content and earlier snapshots retain their arrays.
- Build CPU meshes from `snapshot.Content.Designs[index].Boxes`, preserving array order as block ids.
  Queue GPU preparation/upload on the renderer's existing lifecycle; the network does not create GPU resources.
- Hull views and social design indices name this catalogue exclusively. Do not index the original local design table.
- Only admission replaces the catalogue. Runtime editing/design registration is separate future work.
- `CatalogueMeshes` builds the acquired designs on `JobSystem`. Only the current array can be installed;
  abandoned jobs finish without publishing. Preparation errors keep the connecting panel open with a reason.
- Ship slots 0–254 are reserved. Asteroid and cube slots and GPU buffers stay fixed across catalogues.
  The render thread waits for device idle before replacing ship buffers, before recording the next frame;
  synchronous uploads finish before the old buffers are retired and matching world input/drawing is enabled.
- Disposal joins CPU jobs before destroying GPU resources. Ready catalogues allocate nothing per frame.
  `CatalogueMeshTests` covers reorder, new vertices, stale completion, failure recovery and warm allocation.
- Targeted headless tests are the integration evidence; full `make check`, benchmarks and GPU verification
  belong to the parent. No client window or capture was used for this renderer change.

## Slice 3: shared collision

- Both `HullSim` paths use box extents about the original pivot. Hull pairs use fifteen-axis oriented-box SAT;
  the same `HullField` serves the server and prediction. IDs remain file indices; boxes are never expanded.
- Each design owns a median bounds tree. Each block state adds `4 * (2 * blocks - 1)` bytes of live branch
  counts, initialized at preparation and updated up the tree on loss. Dead subtrees are skipped.
- `blocks.json` sets 8,192 tree visits, 2,048 box tests and 256 retained contacts per query, and 32 translation
  strides per hull pair step. Each stride has its own query budgets. Exhaustion resolves collected contacts;
  only tested touching blocks take damage. The existing 64 other-hull impulse limit still applies.
- Asteroid contacts test the nearest point and eight corners of the oriented box against `AsteroidShape`.
  This bounded approximation catches off-centre faces, edges and corners, but can miss a narrow ridge between
  samples. Radial push directions and block-centre impulse arms approximate the contact manifold.
- SAT contacts use penetration-weighted normals and points, then clear the collected pairs along that normal.
  Tiny overlaps at or below 0.00001 m are treated as roundoff. Capped strides can miss motion beyond their
  total coverage; rotation is sampled at the tick's final orientation. Asteroid motion is not swept.

Stage 3 still needs large-hull performance work. There is no 150,000-block smooth-tick claim.
The memory table includes collision branch counts on both server and client.
The loader and `DesignBytes.Decode` accept sized boxes within the shared resource budgets.

Renderer/picker geometry remains `HullDesign.Boxes`, `BlockOffsets`, `BlockHalfExtents` and `Pivot`.
Offsets and extents are metres in hull space; keep the original pivot after damage and use file-order ids.
Protocol 24 and evaluator identity 3 separate the changed simulation from older builds.


## Decided: stage 2 client geometry

- Meshes use box extents and the fixed pivot; fully covered faces are omitted, including tiled coverage.
  Partial adjacency retains the whole face in both drawing and picking. This costs hidden triangles until stage 4.
- Damaged hull instances scale independently on all three axes. Picking, near-grab, stretching, cuts and own
  apertures use the same half extents. Colour remains the design tint; the format has no per-block colour field.
- Shields: one smooth builder for every design (`ShieldSurface`); see "Smooth shields" below. The square union,
  the unit-cell rounded shell and the per-face rectangle bound are gone.
- Shared admission budgets live in blocks/blocks.json: 32,768 surface vertices, 196,608 surface indices,
  6,000,000 face-neighbour checks, and the shield's `shieldRoundness` and `shieldBlend`. At most 910 blocks a
  design keep per-block caches bounded.
- Shell storage: 64 hulls, each 32,768 vertices (768 KiB) and 196,608 16-bit indices (384 KiB): 72 MiB per copy.

## Smooth shields

### Decided

- The target: the surviving blocks grown square by q = m(1 - roundness), then round by R = m·roundness + blend,
  then shrunk back by the blend (a closing); m = 0.3 block. Defaults: roundness 1, blend 0.3.
- It depends only on the union of the blocks: any split, order or centre of mass gives the same samples.
- Opposing faces under 2R apart close; a one-cell gap closes into a neck; a four-cell opening stays open.
- Sampled on a grid of a third of a block, aligned with block faces, over the fixed 53-point cover of the blend ball.
  The sampled field Fc is 1-Lipschitz and F - δ ≤ Fc ≤ F, δ ≤ 0.4766 blend. The shell is not the exact target:
  where Fc falls short it stands further out.
- Extraction: the level set of the linear interpolation over Kuhn tetrahedra, error e = √3h/2 = 0.2887 block.
  Every block stays inside: a block point has a cube of half side q inside the grown blocks around it, so its
  value is at most blend + √(3K), K = (h/2 - q)² for q ≤ h/4, else 4(h - q)³/(27h). That stays under R for every
  admitted roundness (0.1 to 1), by 0.011 block at full roundness. Crossings are clamped 1e-4 of an edge in.
- Merging: cells whose samples are equal to the bit along an axis are drawn as one swept strip, the level set
  of their interpolation over triangular prisms (same bound e).
- Flat middles: found plane by plane over the union of coplanar box faces, so a box and the same box of cubes
  find the same ones. A point qualifies when, within a square of half side reach + a cell about it, the plane's
  faces cover everything below and nothing is above out to the skipped layers plus that reach. Only the face's own
  band of layers is skipped. The region is cut canonically into rectangles, each drawn as one fan whose middle
  sits on the layer's sampled crossing, as its edges do. Every strip and fan edge is cut where a neighbouring
  piece has a vertex: no T-junctions.
- Separate box-shaped parts use triangulated rounded boxes with exact surface vertices and analytic normals.
  Blocks are one part when their boxes come within
  2(q + R + blend) of each other along every axis; parts further apart than that are never joined by the closing.
  A part with no wedge whose blocks fill its bounding box exactly (volume equality; blocks never overlap) is a box,
  and its target is that box grown by q, then round by ρ. It is drawn with three arc segments a quarter turn: 80
  vertices and 468 indices, every vertex on the target with its exact normal, edges at most 0.0514 ρ and corners
  at most 0.0751 ρ inside it. The union decides, so cubes filling a box take it as the box does.
- Output is indexed: each distinct vertex once, 16-bit indices, three a triangle.
- Normals: each distinct vertex sums its triangles' normals weighted by angle, normalised once; the same for any
  split, since the mesh is.
- Work: every distance, tree visit, cell, sample, vertex and index is counted before it is used; a build past a
  limit stops, says which, and returns no shell. Scratch: 43.2 MiB (45,283,096 bytes of arrays), held once per
  process (`ShieldSurface.WithShared`), plus admission's 1.125 MiB output.
- Admission (`GeometryAdmission.Measure`) builds the intact shell off the tick and keeps its vertices and indices
  together with the design (`HullDesign.IntactShell`, `IntactIndices`). A design whose intact shell does not fit
  either limit, or runs out of work, is refused, naming which.
- Damaged hulls: built one at a time on a client worker, newest losses first. Until ready, or when it would not
  fit, the hull keeps its earlier shell (same design, a superset of its blocks) or its intact one.
- Memory: intact shells up to 1.125 MiB each, at most 255 designs: 286.875 MiB worst case on each side, plus the
  43.2 MiB builder and 1.125 MiB admission output; on the client also the 72 MiB shell cache and the worker's
  1.125 MiB output. The GPU keeps 48 MiB of shield vertices and 24 MiB of indices per frame in flight.

### Measured (debug and release builds of the tests)

- Rounded boxes: the dense ~90-box fixture (one solid box) and a 64 cube, 80 vertices and 468 indices each;
  130 separate 2 x 1 x 1 boxes 10,400 and 60,840; 300 separate boxes 24,000 and 140,400.
- Sampled: the 65-block generated ship 11,960 and 71,220 (35 of its parts rounded boxes); a 64 x 3 x 2 beam with a
  cube a cell off it, and a plate with a block on its edge, keep the flat-middle path. Cells grow with edges.
- Admitted: 64 cubes stepped on a diagonal (6,467 vertices, 38,790 indices), touching at corners (14,657 and
  87,930), 64 separate cubes (rounded boxes).
- Refused, past the vertices: 800 separate cubes (64,000). Each limit is tested at its exact count.

## Final admission evidence and limits

- A generated 300-box mixed-size JSON ship loads through GameContent, transfers to a client with a different
  cache, flies in the real server field and loses block 299 in a collision. Recovery publishes the loss;
  received stats, CPU mesh extents, picking and damaged shield rebuilding are checked on that design.
- Whole-catalogue limits in net/blocks.json are 196,608 encoded bytes and 8,192 conservatively decoded boxes.
  Every encoded block needs at least 27 bytes, so the byte budget actually limits the default catalogue to
  fewer than 7,282 boxes. Counts include duplicate entries and cache hits before decoding starts.
  Per-box caches and trees therefore scale with this bounded total, never 255 transfers of 8 MiB.
  At 24 vertices and 36 four-byte indices per box, the configured 8,192-box budget bounds final hull mesh
  payload to 5.625 MiB per copy; temporary growing lists can reserve additional capacity.
- Designs the editor adds count against the same catalogue budgets, pass the same admission, and stay
  until the server stops. `HullField` keeps contact memory for the largest admissible design (910 blocks)
  from the start, so a new design costs the tick none.
- Shared admission runs before server advertisement and before client catalogue publication. Local visual
  settings cannot lower geometry capacity after simulation admission. No volume-cell expansion is used.
- Admission and transfer deadlines derive from bounded bytes, header allowance and request spacing, with
  fixed retry slack. Thirty real cache misses complete beyond the old 600-tick deadline; stalled peers expire.
- Final independent review found no code blockers. `make check TEST_ARGS="-parallel none"` passed formatting,
  a build with zero warnings/errors, 1,093 game tests and 320 listing tests. Parallel runs exposed timing and
  allocation-sensitive failures; serial verification does not establish parallel test reliability.
  No windows, captures or benchmarks were run. Stages 3–5 remain necessary for 150,000-block stations and
  measured large-scene tick/frame performance.


## Wedges (protocol 27)

- Protocol 27, evaluator identity 4 and canonical design bytes 2 carry a shape byte after the six grid integers.
  JSON 3 writes canonical shape names; JSON 1 and 2 remain box-only. Minimum canonical block record: 27 bytes.
- `DesignBox.Shape` defaults to Box. Codes 1–12 are triangular prisms:
  `1 + 4*e + (su<0 ? 1 : 0) + (sv<0 ? 2 : 0)`, with u=(e+1)%3 and v=(e+2)%3.
  Names are `wedge-x++` through `wedge-z--`; signs identify the removed transverse corner.
- `BlockShapes` exposes names, decoding, slant normals, vertices, support, closest points, quarter turns, and the
  corners of each face (`Face`, `FaceNormal`, `SlantSide`). Client meshes, picking and outlines all use `Face`.
- `BoxDesign.Centroid` is the mass centroid; `HullDesign.BlockOffsets/BlockHalfExtents` stay bounding centres/extents.
  `HullDesign.Shapes` lists each block's shape. Wedges contribute half volume, triangle-axis variance S²/18 and
  extrusion variance S²/12. Diagonal inertia omits covariance, as before.
- Shared hull collision uses face normals and edge crosses (at most 24 axes), costing four pair-test units.
  Asteroids sample the exact prism closest point and six real vertices. Narrow unsampled ridges remain a limit.
  Pushout uses shape support; impulse arms retain the existing midpoint approximation.
- `BuildDraft.Add(..., shape)`, clipboard/group edits and resize preserve shape.
  `Rotate(block, axis)` makes one positive right-handed quarter turn with fixed low corner and swapped dimensions;
  overlap refuses the entire edit, and undo is atomic. Merge accepts boxes only.

### Client

- Mesh slots: the cube, then the twelve wedge prisms (`WorldView.BlockMesh`), each a unit bounding box scaled per
  axis. Damaged hulls and the editor draft draw each block in its own shape.
- `mesh.vert` and `shield.vert` turn normals by the cofactor of the model matrix, so per-axis scaling and the jump
  stretch keep normals right.
- A wedge's exterior-face bit (u, +su) is its slant; bit (v, +sv) has no face. Wedges keep every face, and boxes
  are only culled by box neighbours: conservative hidden faces, never holes.
- Picking (`TargetPicking`, including the aperture and grab paths) meets a wedge only on its real faces. The editor
  ray enters a wedge only through its solid half; the slant counts as the larger box side it replaces, so a part
  placed on a slope snaps to whole cells against the wedge's box. Footprints stay rectangular.
- Part cards count a wedge as half its box (`ScaleHalf`, crew rounded down the same way); all twelve wedges share
  one cached set of figures.
- Shields: wedges take the smooth shell like every block; its slope follows theirs.
