# Propulsion and fuel

## Status

- **Built:** the simulation. Blocks face one of six ways, thrust is per direction, three fuels are burnt and saved.
- **Built:** the editor turns a part's facing with `R` and warns about a missing direction or tank.
- **Built:** the fuel readout in the pilot console (a fuel row under the two groups, a tooltip, low and empty notices).
- **Built:** the models of engines and both thruster kinds, and their plumes and puffs (below).
- **Built:** thrusters mount on block faces ("Face mounts" below): data, simulation, editor, shipped ships.
- Not built yet from the second pass: low-poly models, models in the editor, vacuum plumes.
- Protocol 51, save layout 7, design encoding 4, design file version 5, evaluator 12.

## Decided: parts

- Thrusters are replaced by two kinds:
  - **Cold gas thrusters** (`coldGasThruster`): small to medium. They puff compressed nitrogen.
  - **Hot thrusters** (`hotThruster`): bigger and stronger, small rocket engines that push the hull sideways.
- **Engines** (`engine`) are the large main drives.
- Engines are **rotatable** to face any of the six directions and push only the opposite way. A thruster mounts
  on a face and points out of it ("Face mounts").
  - Engines on the front give braking power.
  - A ship needs parts facing each way it wants to move. The editor warns when a direction has none; warnings never
    block Apply.
- Gyros stay and still do the turning, on electricity.
- The drone's parts are the same kinds, small: `microthruster` burns nitrogen, `microengine` bipropellant.

## Decided: facing

- Every block has a facing: `right`, `left`, `up`, `down`, `back` or `front` in the hull's frame. The nose is `front`.
- A thrust part's facing is where its nozzle points. It pushes the hull the other way.
- For every other block the facing only says how the block sits. Models may use it later.
- A design file names it per block from version 4 (`"facing": "front"`). A block that names none faces `back`.
- Files of versions 1 to 3 still load. Every block in them faces `back`, so an engine pushes forward as before.
- Turning a part (`R` on the ghost, or Rotate on a placed block) turns its size, shape and facing together.

## Decided: fuel

- Three fuels, each in its own tank block:
  - **Nitrogen** for cold gas thrusters.
  - **Hydrazine** for hot thrusters.
  - **Bipropellant** (fuel and oxidiser in one tank) for engines.
- A part burns only its own fuel. With that fuel gone, it gives no thrust and draws no power.
- Thrusters burn far less than engines. Boost burns more. All rates are content figures.
- Tanks hold a balanced but fairly long supply.
- Fuel is the main cost of thrust. A small electric draw stays (pumps, valves, ignition), so thrust still slows shield
  recovery, more softly than before. Boost still empties the battery in seconds.
- No refuelling exists yet. Applying an edit or a repair refills every tank, as a repair restores blocks.
  - A whole ship with room in its tanks can be repaired for that alone. The editor calls it Refuel.
  - Tanks run down are no damage: they never hold back an edit.
- Fuel is simulation state: shared flight code, predicted by the client, saved with the world.
- Dampeners fire the same parts and burn the same fuel. At rest they burn nothing.
- Fuel has no mass yet: a full tank and an empty one weigh the same.

## Decided: readout

- Fuel shows like energy: each fuel's amount, capacity and net flow per second.
- Flow is negative while burning. Nothing adds fuel yet. Ship refineries may later make it positive.
- The console's fuel row sits under the two groups and spans them: one cell per fuel the hull has a tank for.
  - Each cell: name, amount / capacity, net flow in kg/s with an arrow ("—" at rest), a strip of how full.
  - A narrow cell drops the capacity, then the name.
  - Hover shows the tooltip: per fuel, amount, burn, minutes left at that burn ("—" at rest).
- Low is under `fuelLowFraction` of capacity (`content/visuals/power.json`). The cell turns red and a notice posts once.
  Empty posts its own notice. Both are own hull only, once per crossing, one kind per fuel in `notices.json`.
- The editor's stats already list tank capacity per fuel.

## Decided: models and effects

- Engines and both thruster kinds have a model of our own design. It adapts to the block's size by itself; the player
  never edits it.
- An idle engine is dark. When thrust starts it ignites with sparks, then shoots a plume, with shock diamonds if they
  can be made to look right.
- Cold gas thrusters vent a short, pale, cold puff.
- Hot thrusters show a small hot plume, a miniature engine.

## Decided: how the models and plumes are made

- Every figure is in `content/visuals/propulsion.json`, keyed by block type id. A part without an entry stays a plain
  block and shows no plume. A wedge-shaped part is always a plain block.
- **Models** (`PropulsionModels`): built with the hull's mesh on a worker, never in a frame.
  - A housing at the back, a collar, then the nozzles at the face the part faces.
  - The face is split into nozzles: an engine or hot thruster into squares of its shorter side (a 2 by 1 face has two
    bells), a cold gas thruster two per cell a side (a quad on a plate).
  - A nozzle's exit follows its share of the face; its length follows its exit, capped by a share of the depth. A
    deeper block grows its housing, never its bell.
  - Nozzles are bare metal: the shade rides in the length of the mesh normal, which `mesh.vert` reads.
  - A hull drawn block by block (far and damaged) still shows plain blocks; near damaged hulls show the models.
- **Plumes** (`ThrustEffects`, `plume.vert`, `plume.frag`): one instance per firing nozzle, at most `maxPlumes` (256 in
  the buffer); past it the largest on screen stay. Drawn after hulls and rocks, tested against their depth.
  - Each plume marches 16 steps through a box around it. Nothing fires, nothing is drawn.
  - Flicker and the gas's flow come from the snapshot clock and a per-nozzle seed: smooth, no shimmer.
  - Ignition is when a part group (fuel and facing) goes from idle to firing: sparks for `sparkSeconds`, then the
    plume grows over `growSeconds`. A hull first seen while it fires shows no ignition.
  - Shock diamonds: bright nodes in the core over the first half of the plume. Kept; `diamonds` 0 turns them off.
  - A cold gas puff starts with a burst that settles to a faint steady jet.
- Check with `--thrust` and `--zoom` captures (`AGENTS.md`); `PropulsionVisualTests` holds the rules.

## Decided: second pass (look and mounting)

- **Thrusters mount on faces.** Cold gas and hot thrusters clip onto the face of any block and take no grid cell.
  - They stick out about 0.2 block. A hot thruster is a bit bigger than a cold gas one.
  - Several can share one face. The ghost shows only the thruster, on the face under the pointer.
  - Engines stay sized blocks that scale with their size.
- **Models are low poly.** Faceted, chunky panels, flat shading. Our own shapes, no other game's art.
- **No gaps.** A model fills the cells it sits in and meets its neighbours flush.
- **The editor shows the models.** The ghost and every placed part show the real model at once.
- **Plumes are vacuum-realistic.** Wide, soft, faint and bell-shaped; few or no shock diamonds.

## Decided: face mounts

Built. Rules live in `FaceMounts`; `PropulsionTests`, `BuildDraftTests` and `EditorSessionTests` hold them.

### What a mount is

- A thruster (`coldGasThruster`, `hotThruster`, `microthruster`) is a **mount**: it sits on a block's face and
  fills no grid cell. A block type with `"mount"` in `blocks.json` is a mount and never a block; every other
  type is a block and never a mount.
- A mount names **its block** (design index), **the face** (one of six) and **where on the face** it is.
- Positions are whole **tenths of a cell**, from the face's low corner: `at: [u, v]` along the axes after the
  face's own (`(axis + 1) % 3`, then `(axis + 2) % 3`).
- Its footprint is a square: `side` and `depth` in tenths, per type. Cold gas 0.4 x 0.4, hot 0.5 x 0.5, both
  0.2 deep. Four of either fit a one-cell face.
- A mount is never deeper than 0.2 cell: the shield stands 0.3 off the blocks, so a mount is always inside it.

### It points out of its face, and no other way

- The nozzle points along the face's normal. The thruster pushes the hull the other way.
- No facing along the face. Why:
  - Every hull has an open face each of the six ways, so nothing is lost.
  - A nozzle along a face would fire across the hull's own plating.
  - The face you pick is the direction: no turn step, and `R` does nothing for a thruster.

### What a design must hold to

`DesignBytes.Decode`, design files and the editor's drafts all ask `FaceMounts`.

Where a mount may sit:

- The block is there, and the face is a whole flat side: any side of a box, the two full sides of a wedge.
- The footprint lies on the face.
- No block shares volume with the mount's box. That is what "the face is open" means, per mount, so a long wall
  can carry thrusters beside a neighbour.
- No two mounts share volume, on one face or across an inside corner. Touching is fine.

How many, and of what:

- At most `limits.maxMounts` a design (1,024).
- A design also has to fit a transfer: 32,768 bytes. A mount is about 25.
- A mount type is a thrust part with no core, sensor, tank, drive or module, no size rules and no `maxSide`.

### Simulation

- A hull has a mount **exactly while it has the mount's block**. A mount has no state of its own.
  - No alive bit, no hit points, nothing on the wire, nothing in a save. The block's loss is all there is.
  - Its thrust, burn, power and mass are added to the running sums with its block (`BoxHullSums.Add`), so losing
    the block takes them in the same step. No tick ever looks at a mount.
- A mount weighs something: it counts in mass, inertia and the centre the hull turns about.
- It has **no hit points**. A hull's hull points are its blocks'. The shipped ships have fewer than before.
- It is **left out of collision**. Why: it stands 0.2 cell off a face, inside the shield, and putting up to
  1,024 more boxes in every contact tree would cost every hull pair for a scrape that changes nothing.
  - So a mount can sink into a rock or another hull by 0.2 cell before the blocks touch.
  - The collision sphere is still the blocks' own.
- Target picking takes blocks only. A mount is never the picked block.

### Files and bytes

- Design file version 5: `"mounts": [{ "on": 8, "face": "right", "at": [1, 5], "type": "coldGasThruster" }]`.
- Canonical bytes version 4: after the blocks a count, then per mount its type, block, face and two positions
  (`u`, `v` as 16 bits), in the order block, face, `v`, `u`. Another order is refused as noncanonical.
- **Old files still load** (versions 1 to 4), converted:
  - A thruster block stays as plating (`mount.legacyBlock`: `hull`, or `microhull` for the drone) in its cell.
  - It carries four thrusters on every cell of the face it pointed from. Four cold gas thrusters push what a
    one-cell block of them did.
  - A thruster whose nozzle had a block in front of it is left out: it pushes no more.
  - The ship is heavier by the plating. A thruster more than one cell deep pushes less: thrust follows the face.
- Old saves and old servers are refused untouched: protocol 51, save layout 7, encoding 4, evaluator 12.

### Editor

Placing and picking:

- A thruster card places a mount. The ghost is only the thruster's box, on the face under the pointer, snapped to
  tenths, with its exhaust line. Green where it has room, red with the reason where not.
- `S` and `R` do nothing for it. The part list says so.
- Select it with a middle click (Shift adds), delete it with Delete, undo with Ctrl+Z.
- Copying a block copies what is mounted on it. Copying a thruster alone picks it as the part to place.

Mounts belong to their block:

- Moved, the block takes them along.
- Deleted, it takes them with it, as one undo step.
- Merged blocks keep theirs. A block resized from its low side keeps them where they are in the ship.
- A turned block drops its mounts: its faces are other faces.

And the rest:

- An edit never fails because of a mount. A mount the edit leaves without room (a block put in front of it, a
  face shrunk under it) is taken away in the same undo step.
- Warnings (no thrust a way, no tank) read the checked design, so they count mounts.
- Not built: the editor draws a placed mount as a plain small box, not as its model.

### Drawing

- A mount is drawn with its type's model from `propulsion.json`, built in the mount's small box
  (`PropulsionModels.AddMount`), on whole and on damaged hulls. With no model it is that box.
- Its nozzle is in the hull's nozzle list under its block, so plumes are lit and lost with the block.
- Check with `--open edit-mount` and `--thrust` captures (`AGENTS.md`).

## Decided: provisional figures

All in `content/blocks/blocks.json`. Tunable; change them there, never in code.

| Block | Thrust per cell | Fuel | Burn per cell at full thrust | Power at full thrust | Mass | Largest side |
| --- | --- | --- | --- | --- | --- | --- |
| `engine` | 800 kN | bipropellant | 0.2 kg/s | 1.0 MW | 3.5 t | 64 cells (no limit of its own) |
| `microengine` | 60 kN | bipropellant | 0.02 kg/s | 0.12 MW | 0.3 t | no limit of its own |

| Mount | Thrust each | Fuel | Burn each at full thrust | Power at full thrust | Mass | Footprint |
| --- | --- | --- | --- | --- | --- | --- |
| `hotThruster` | 160 kN | hydrazine | 0.015 kg/s | 0.1 MW | 0.75 t | 0.5 x 0.5 x 0.2 cell |
| `coldGasThruster` | 100 kN | nitrogen | 0.0075 kg/s | 0.0375 MW | 0.45 t | 0.4 x 0.4 x 0.2 cell |
| `microthruster` | 7.5 kN | nitrogen | 0.00075 kg/s | 0.0075 MW | 0.0375 t | 0.4 x 0.4 x 0.2 cell |

- A mount is a quarter of what a one-cell thruster block was, in every figure: four on a face are that block.
- No shipped block has a `maxSide` now. The field stays.

| Tank | Holds per cell | Mass |
| --- | --- | --- |
| `bipropellantTank` | 1,000 kg | 3.0 t |
| `hydrazineTank` | 300 kg | 2.5 t |
| `nitrogenTank` | 150 kg | 2.0 t |
| `microBipropellantTank` | 90 kg | 0.15 t |
| `microNitrogenTank` | 24 kg | 0.15 t |

- `boostFuelMultiplier` 2.5: boost doubles thrust for two and a half times the fuel.
- `boostPowerMultiplier` 4 → 9.4, so boost draws what it drew before (pod 16.5 MW) while plain thrust draws far less.
- Size limits are a per-block field, `maxSide`: the most cells along any side. Left out, the content's limit applies.
- `fuelBurn` and `fuelCapacity` take size rules like any other figure.
- Why engines stay the main drive: best thrust per tonne, they scale with their size, and a bipropellant tank
  carries twice as many thrust-seconds as a nitrogen tank of the same size. Thrusters are cheap and light, take
  no cell, and are limited by the faces a ship has.
- Gyro torque 520 → 680 and drone gyro 0.3 → 0.46: the tanks and extra thrusters added mass, and the ships turn about
  as fast as before.
- `ftlEnergyPerTonne` 2.0 → 1.5 for the same reason: a jump costs a ship about what it did (pod 62 MJ, vessels
  118 MJ), so the fleet still charges within the 12 s calculation and jumps together.

Shipped designs, tanks full, measured by `PropulsionTests`:

| Design | Forward | Braking, sideways, up, down | Engines at full thrust | Boosting whenever the drive allows | One thruster direction at full |
| --- | --- | --- | --- | --- | --- |
| Command Pod | 19.3 m/s² | 9.6 m/s² | 83 min | 52 min | 83 min |
| Prospector | 20.3 m/s² | 10.1 m/s² | 83 min | 52 min | 83 min |
| Surveyor | 20.4 m/s² | 10.2 m/s² | 83 min | 52 min | 83 min |
| Drone | 16.7 m/s² | braking 16.7, others 8.3 m/s² | 75 min | 47 min | 133 min (braking 67 min) |

With mounts, same mass, thrust and burn as with thruster blocks, so the table above did not move:

| Design | Blocks | Mounts | Where | Hull points |
| --- | --- | --- | --- | --- |
| Command Pod | 13 (was 18) | 20 | four each on the shield (right), cargo (left), quarters (up), core (down), charting (front) | 2,760 (was 3,510) |
| Prospector, Surveyor | 28 (was 38) | 40 | eight each way: two cargo holds a side, two on top, two below, two plates at the front | 5,900 and 5,910 (were 7,400 and 7,410) |
| Drone | 9 (was 15) | 24 | four each on the tanks, gyro (up) and generator (down), eight on the front of gyro and generator | 310 (was 490) |

- The ships are more compact, so they turn a little differently (drone 38, 45, 40 deg/s; was 39, 43, 45).

- Before: pod 20.5 and 11.5 m/s², vessels 21.9 and 12.4 m/s², drone 16.6 and 8.0 m/s².
- Electric draw at full thrust: pod 4.1 → 1.75 MW, vessels 8.2 → 3.5 MW, drone 0.6 → 0.3 MW.

## What the effects and the readout read

- **Per hull in view:** `HullView.Thrust` (`ThrustState`), for the own hull and every other hull.
  - Three throttles, -127 to 127, along the hull's right, up and back axes: which way it is pushed and how hard.
  - A bit per fuel while parts of that fuel fire, and a bit while boost runs.
  - `Thrust.Firing(fuel, facing)` answers for one part: 0 to 1. Zero when idle or out of fuel.
  - Dampener braking is in it. A hull in the jump tunnel fires nothing.
- **Per part:** its facing is `DesignBox.Facing`, its fuel `BlockType.Fuel`, both in `HullDesign.Boxes`.
- **Fuel, own hull only:** `HullView.Fuel` (`FuelFlow`): amount, capacity and burn per fuel in kg and kg/s. The burn
  is smoothed like `HullView.Power`. No other hull's tanks are sent to anyone.
- It is simulation state (`HullState.Thrust`), written by `FlightModel.Step`. Nothing is worked out a second way.
- Wire: 4 bytes in every hull record; the own tanks are 12 bytes in the snapshot header.
- Nothing here allocates.

## Known limits

- The client flies its other hulls forward as if their tanks were full. Wrong only for one that has run dry.
- An engine's nozzle with a block in front of it still pushes. Nothing checks for it. A mounted thruster cannot
  have one: its box must be clear.
- A mounted thruster is not hit, not picked and not collided with ("Face mounts").
- The electric draw follows the largest throttle the pilot asks, over all fuelled parts, not each part's own use.
- A node of the running sums grew from 216 to 528 bytes (thrust and burn for six directions and three fuels). See
  "Sum memory" in `docs/blocks-at-scale.md`.

## Open

- Whether thrusters placed away from the centre also give torque, alongside or instead of gyros.
- Fuel transfer, refineries and stations.
- Whether fuel should weigh something.
- Whether the figures above are right. They are a first balance, not an approved one.
