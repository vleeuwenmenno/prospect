# Cargo, containers and tractor beams

Racks, shared container sizes and deterministic packing are built.
There is no cargo inventory, mining yield or pickup yet.

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
- Until step 2, destroyed racks lose their containers instead of creating containers in space.

### Racks

- The cargo block becomes a rack: an open frame, mostly empty space.
- Containers placed in a rack are drawn there. The rack's size decides which containers fit.
- In the hold, containers are slots kept by the server, not physics objects. The server places them by a fixed rule.

### Loading: never automatic

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

### Losing cargo

- A rack that is destroyed throws its containers into space, contents intact.
- Anyone can pick those containers up.
- Dropping cargo uses the same containers as the hold.

## Open

- **Tractor figures:** range, power per tonne, the most mass one beam can move, how several beams add up.
- **Towing huge chunks** outside the hull: tractor-held mass joins the ship's flight. It changes shared flight
  and prediction, so it comes last and needs its own design pass.
- **Tractor filter:** a pickup list per ship that can be switched between allow-list and block-list.

## Working rule

Each major mechanic (cargo, mining, turrets) is designed and tuned on its own first. The glue between them is
checked afterwards. This overrides the joint mining, inventory and turret brainstorm in `docs/direction.md`.

## Proposed build order

1. Racks and containers in the hold: rack block, container sizes in content, the hold's slots on the server,
   wire, save, drawing, and a debug button that adds a container.
2. Containers in space: jettison, a rack's destruction, hull points, saved.
3. Tractor beam mount: range, power, pull, sliding onto a rack in view.
4. Mining output into containers.
5. Towing huge chunks.

Deferred: drone launcher, tractor filter.


## Built: step 1 foundation

- `blocks/cargo.json` owns the unit edge, eight named shapes and hull points; shared content identity includes it.
- `CargoPacking` uses fixed orientation, largest first, X then Z then Y first fit. No rotations or backtracking.
  Caller supplies occupancy and placement buffers; output is canonical size order, independent of input order.
- The existing `cargo` id is a box-only ship rack in Hull. Capacity is u, linearly scaled (8 u per cell).
  Retuning the grid requires matching the cargo stat; loading rejects mismatches. No drone rack yet.
- Worker-built rack frames and reusable ribbed container models use the `rack` look in `visuals/propulsion.json`.
  `--open edit-rack` previews XXS through L in a 2x2x2 ghost only; real hull racks stay empty.
- `BlockStateStore.Cargo` provides fixed storage for up to 255 containers on a hull with racks.
  Adds validate payload and health, and repack atomically through `CargoPacking` without allocating.
  Block-state copies preserve containers; losing a block removes only that rack's containers.
- Shared flight includes cargo mass and inertia; server and prediction use the same figures.
- This storage has no gameplay loading caller yet. Debug loading and live drawing remain unbuilt. Protocol 58 sends the own hold reliably on change; snapshots keep their existing record budget.
  Codec 12 saves the hold and its old tuning, validates it, and logs containers dropped during a retune.
