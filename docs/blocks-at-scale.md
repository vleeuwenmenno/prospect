# Blocks at scale

How ships of thousands of blocks and stations of 150,000 stay fast. Plan only: nothing here is built yet.

The goal and the block model are in `direction.md` under "Decided: building".

## Decided: data

- **A design never changes and is shared.** Many hulls point at one design.
- **A hull adds only two things:** which blocks are alive (a bitset sized to its design) and, on the server, hit
  points per block.
- **Stats are running sums per hull.** They change when a block is lost. Nothing scans all blocks in the tick,
  the frame or the interface.
- **A hull keeps turning around its original centre** when it loses blocks. Its mass and inertia do change.
- **Slopes come with the editor.** The design format carries a version so shapes can be added.

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

- How a block's size scales its stats: by the number of grid cells it fills, or by rules per block type.
- The slowest machine the full-load scene must run on, and at what frame rate.
- Limits on a build: the largest box, the largest ship or station across, the thinnest wall.
