# Wreckage: ships that break apart

Product direction from the brainstorm on 2026-11-16. Not built yet.

## Why

- Today a ship stays rigid when the block holding two parts together is destroyed. The cut-off part floats along
  as if still attached.

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
