# Wreckage: ships that break apart

Built: authoritative splitting, drifting wrecks, persistence, cleanup settings and client display.

## Why

- Destroying a connector separates the parts it held together. Detached pieces drift independently.

## Decided

### Breaking apart

- When blocks are lost, the server checks which blocks still hold together. Blocks hold together through shared
  faces only.
- The piece with the core stays the ship. With no core left, the heaviest piece stays the ship.
- Every other piece becomes a wreck. It keeps the ship's velocity at its own position, and the ship's spin.
- The ship loses those blocks as it loses any destroyed block, so a repair can build them again.

### Wrecks

- A wreck is a hull with no owner, no control, no shield and no power.
- It is the ship's own design with only its blocks alive, so a wreck never adds a design to the catalogue
  (255 at most per server).
- It drifts, collides with rocks and hulls, and can be shot to pieces; it breaks apart again by the same rule.
- Fuel in a wreck's tanks stays there. Reclaiming it is a later mechanic.
- Anyone may come near it; taking it (salvage, ownership) is decided later.

### Lifetime is a world setting

- How long a wreck lasts (default about 30 minutes, fading only when no player is near) and how many a sector
  keeps (default about 64, oldest removed first) are world settings: chosen when making a new game, kept in the
  saved world, changeable in the world settings and the server's settings file.
- Wrecks are saved with the world.

## Open

- Salvage and ownership of wrecks.
- Reclaiming fuel and cargo from a wreck.
- Whether the old owner sees their wrecks on the map.
- Debris smaller than a block (sparks, shards) when a piece breaks off: visual only, later.

## Implementation

- `HullConnectivity` uses the existing box tree and reserved buffers. Exact integer bounding faces must overlap
  with positive area on both other axes. Wedges use all six bounding faces, even the empty side; slants do not join.
- The lowest-index live core wins when several core pieces remain. Without a core, evaluated dry mass including
  mounts decides; equal masses keep the piece with the lowest block index. Mounts stay with their parent block.
- Live mass moments give a COM offset and diagonal inertia about that COM. The design origin and mesh never move
  within the design. Flight rotates that origin around the live COM; collision lever arms use the live COM.
- `ServerHost.TickWreckage` checks changed loss revisions after the round. Each fragment copies HP and tank state,
  loses everything outside its piece, and uses the original design. The source loses those blocks normally.
- Owner zero on protocol 55 means wreck. No bytes were added to hull records. Wreck cleanup updates use reliable
  message 35 (9 bytes); only an admitted in-process host may change them. Other clients receive authoritative values.

## Cleanup and limits

- Lifetime counts ticks only outside the configured sensor radius of every connected player's controlled hull.
  Zero minutes never expires. The sector cap removes oldest first even when a player is nearby.
- Creating wrecks stops at 255 total hulls and the saved-world growth budget. Oldest existing wrecks are evicted
  first, excluding the fragment source. If owned hulls consume all room, excess detached pieces are discarded.
- Each wreck keeps full original-design block storage. No designs are appended. Connectivity uses bounded scratch
  for admitted designs; hull collision remains pairwise. Large debris fields still need a spatial index.
- Saved codec 9 stores creation tick and unattended age. Load rebuilds live figures and refuses invalid wreck state.
- New Game and server settings choose the defaults. World Settings lets the local host apply live cleanup changes;
  remote clients can read them. Changes are saved and broadcast. Other world settings remain fixed.

## Capture

`--cut-hull <seconds>` is restricted to scripted, unsaved private local games.
It removes the flown ship's middle Z slice and gives it spin, through authoritative block loss.
Use `--capture`, `--look 85 --look-height 10 --zoom 4`, and several captures two seconds apart.
