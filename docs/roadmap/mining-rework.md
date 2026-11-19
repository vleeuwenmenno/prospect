# Mining and ships: the rework

Started 2026-11-18, after play showed the mining loop unreliable, unpolished and too easy.
This page overrides [Drones and mining](drones-mining.md) where the two differ.

## Why

- Drones jitter and haul unreliably. The tractor beam fails when the ship moves a little. The ship itself jitters.
- Mining takes no skill: hold fire anywhere on a rock and ore falls out, then automation collects it.
- Too many bodies change hands: laser, chunk, drone, beam, hopper. Every hand-over is a place to fail.
- Ships look like boxes on boxes.

## Decided

Agreed 2026-11-18.

### Order of work

1. Freeze features.
2. Fix the ship's jitter and the tractor beam on a moving ship. Both are needed whatever is redesigned.
3. Settle the open points on this page before code is written for them.
4. Build in small steps, one at a time.

### How work is accepted

- Passing `make check` and `make bench` is not enough for anything that moves.
- A feature is seen working in motion (a moving, turning ship; a sequence of captures or a measured session test)
  before the next step starts.
- What was not seen working is said plainly.

### Ore collection

- Drones no longer haul ore chunks. "Pick up ores" is switched off and its place taken by a collector on the ship.
- Drones stay in the game: scans, and other uses to be decided. Their hauling code is disabled, not deleted, until the
  collector has proven itself.
- Drones always collide, their own ship included. Never let one pass through a hull (decided 2026-11-19).

## Proposed

Recommendations, not yet agreed. Each needs a yes before it is built.

### The collector

- A part on the ship that takes ore near the cut straight into the bins: short range, no hand-over, no body led
  round the hull.
- Range of a few tens of metres, so the ship must be flown close to its cut.
- It replaces the hopper and the tractor beam's part in ore. Bins stay as they are.

### Mining that takes decisions

- Ore sits in veins and pockets (rock interiors already describe them). Crust gives almost nothing.
- So the drone scan matters: it shows where to cut.
- Cutting along a vein yields more than blasting through it.
- Risk stays in: laser heat, unstable pockets.

### Storage: one path each

- Ore: collector to bins.
- Containers: trade goods and refined products, moved by the tractor beam.
- Chunks and containers stop sharing the tractor code.

### Ships: authored parts on a block hull

- Blocks stay as structure. Collision, shields, wreckage and the editor are kept.
- Every functional thing becomes a part of fixed sizes with its own model: cockpit, engine pod, cargo bay, mining
  rig, reactor. The turrets, engines and work drone already are.
- No part tree as in Kerbal Space Program: it would replace the editor and the block systems.

## Open

Not decided. Ask; do not guess and build.

1. Collector: a beam the player aims, a fixed scoop the ship is flown into the ore with, or automatic within range?
2. Do loose chunks still exist as bodies in space, or does ore go from the cut to the collector as an effect only?
3. What drones do besides scanning (charges, hauling as a late upgrade, mining on their own).
4. Which authored parts come first, and whether plain structure blocks get a new look too.
5. How hard mining should be: how much ore an hour for a starting ship, and what a mistake costs.
