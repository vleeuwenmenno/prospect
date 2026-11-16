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

### Built to be extended

- Every new mechanic is a set of parts: data in `content/`, shared rules in `Prospect.Game`, a client view.
  A new tool, drone kind or scan level is a new content entry, not new code paths.
- Orders, not input: a drone, a mining laser or a fleet ship acts on an order (target, task, limits).
  The player gives orders through the interface. Later a captain in a second ship, or an NPC miner, gives
  the same orders. One executor on the server, whoever gave the order.
- The server decides every result: what a scan reveals, where a rock breaks, how much ore comes off.

## Proposed (technical, not decided)

- Rock changes are saved as a list of edits per rock (cuts, scrapes), not the whole shape.
  Every client applies the same edits with one shared function, so shapes agree.
  A crack makes new rocks from the old one; each piece names its parent and the cut.
- Drones are small real hulls flown by the server with `HullSim`, so they collide, can be shot and use power.
  A drone that is used up (scan, charge) is removed when its job ends.
- An order is a small record: kind, target, owner, limits. The executor runs it each tick without allocation.

## Open

- What the drone bay looks like, how many it holds, and how drones leave and come back (a hatch face?).
- How a rock edit is represented exactly (carved volumes on the mesh, a voxel field, or something else).
  This is the biggest technical choice; it decides scraping, cracking, collision and the wire.
- How veins and the heat map are generated per rock, and how much a scan reveals at each level.
- The heat game's figures: heat rate, cooling, the window, what overheating does.
- The Ore Cargo window's layout.
- Automated mining by NPCs and captains: out of scope now; only the order model above is designed for it.
