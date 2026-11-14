# Propulsion and fuel

## Status

- **Built:** the simulation. Blocks face one of six ways, thrust is per direction, three fuels are burnt and saved.
- **Built:** the editor turns a part's facing with `R` and warns about a missing direction or tank.
- **Built:** the fuel readout in the pilot console (a fuel row under the two groups, a tooltip, low and empty notices).
- **Built:** low-poly models of engines and both thruster kinds, in flight and in the editor, and their vacuum plumes
  and cold gas puffs (below).
- Protocol 50, save layout 6, design encoding 3, design file version 4, evaluator 11.

## Decided: parts

- Thrusters are replaced by two kinds:
  - **Cold gas thrusters** (`coldGasThruster`): small to medium. They puff compressed nitrogen.
  - **Hot thrusters** (`hotThruster`): bigger and stronger, small rocket engines that push the hull sideways.
- **Engines** (`engine`) are the large main drives.
- All three are **rotatable** to face any of the six directions and push only the opposite way.
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
- An idle engine is dark. When thrust starts it ignites with subtle sparks, then opens a vacuum plume: wide, soft,
  faint, bell-shaped, hot only at the exit, at most one faint shock node. Bipropellant is pale blue-violet.
- Cold gas thrusters vent a fast, short-lived jet of expanding vapour, lit by the star, never glowing.
- Hot thrusters show a small version of the engine's plume with a faint orange core.

## Decided: how the models and plumes are made

- Every figure is in `content/visuals/propulsion.json`, keyed by block type id. A part without an entry stays a plain
  block and shows no plume. A wedge-shaped part is always a plain block.
- **Models** (`PropulsionModels`): low poly, built on a worker, never in a frame.
  - A pure function of the part's box (`PartBox`), its facing and the cell size. A thin face mount works the same way.
  - A square mount block covers the whole mounting face and the box's sides: neighbours meet it flush.
  - Engines and hot thrusters: an octagonal housing with panel seams, corner pipes and a bevelled front, then a collar
    with four gusset brackets, a throat ring and a faceted bell (10 to 12 sides, 3 to 4 rings).
  - Cold gas thrusters: a bevelled RCS block with small faceted nozzles, two per cell a side.
  - The face is split into nozzles: an engine or hot thruster into squares of its shorter side (a 2 by 1 face has two
    bells). A nozzle's exit follows its share of the face; its length follows its exit, capped by a share of the
    depth. A deeper box grows its body, never its bell.
  - Every facet has vertices of its own: flat shading. Bare metal rides in the length of the normal (`mesh.vert`).
- **Where models show:** a design's mesh; a near damaged hull's mesh; and from `PartModels` (single part models,
  worker-built, a fixed table) the editor's placed parts, its placement ghost and far hulls drawn block by block. A
  part model not built yet shows its plain block for a frame or two.
- **Plumes** (`ThrustEffects`, `plume.vert`, `plume.frag`): one instance per firing nozzle, at most `maxPlumes` (256 in
  the buffer); past it the largest on screen stay. Drawn after hulls and rocks, tested against their depth.
  - Each plume marches 16 steps through a box around it. Nothing fires, nothing is drawn.
  - Flicker and the gas's flow come from the snapshot clock and a per-nozzle seed: smooth, no shimmer.
  - Ignition is when a part group (fuel and facing) goes from idle to firing: a few faint sparks for `sparkSeconds`,
    then the plume grows over `growSeconds`. A hull first seen while it fires shows no ignition.
  - A flame is a vacuum plume: it opens within `opening` exit radii into a wide bell, its gas thinning as the square
    of its width. The bright core reaches `coreLength` exit radii. At most one faint shock node (`diamonds`).
  - A flame's colour is a density-weighted mix of core and edge; its density is compressed against `brightness`, so
    a view straight into it (boost included) stays tinted and graded, never flat white.
  - A cold gas puff has no light of its own: lit by the star. A tight jet opens into a cone of turbulent vapour that
    streams out at `flowSpeed`. A valve opening bursts denser and wider (`burst`, `burstSeconds`).
  - When a group stops, its last gas flies off down the plume and clears over `fadeSeconds`.
- Check with `--thrust` and `--zoom` captures (`AGENTS.md`); `PropulsionVisualTests` holds the rules.

## Decided: second pass (look and mounting)

- **Thrusters mount on faces.** Cold gas and hot thrusters clip onto the face of any block and take no grid cell.
  - They stick out about 0.2 block. A hot thruster is a bit bigger than a cold gas one.
  - Several can share one face. The ghost shows only the thruster, on the face under the pointer.
  - Engines stay sized blocks that scale with their size.
- **Models are low poly.** Faceted, chunky panels, flat shading. Our own shapes, no other game's art. Built.
- **No gaps.** A model fills the cells it sits in and meets its neighbours flush. Built.
- **The editor shows the models.** The ghost and every placed part show the real model at once. Built.
- **Plumes are vacuum-realistic.** Wide, soft, faint and bell-shaped; few or no shock diamonds. Built.
- Still to do here: face-mounted thrusters (another change).

## Decided: provisional figures

All in `content/blocks/blocks.json`. Tunable; change them there, never in code.

| Part | Thrust per cell | Fuel | Burn per cell at full thrust | Power at full thrust | Mass | Largest side |
| --- | --- | --- | --- | --- | --- | --- |
| `engine` | 800 kN | bipropellant | 0.2 kg/s | 1.0 MW | 3.5 t | 64 cells (no limit of its own) |
| `hotThruster` | 640 kN | hydrazine | 0.06 kg/s | 0.4 MW | 3.0 t | 4 cells |
| `coldGasThruster` | 400 kN | nitrogen | 0.03 kg/s | 0.15 MW | 1.8 t | 2 cells |
| `microengine` | 60 kN | bipropellant | 0.02 kg/s | 0.12 MW | 0.3 t | no limit of its own |
| `microthruster` | 30 kN | nitrogen | 0.003 kg/s | 0.03 MW | 0.15 t | 2 cells |

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
- Why engines stay the main drive: best thrust per cell and per tonne, and a bipropellant tank carries twice
  as many thrust-seconds as a nitrogen tank of the same size. Thrusters are cheap and light, and limited in size.
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
- A nozzle with a block in front of it still pushes. Nothing checks for it.
- The electric draw follows the largest throttle the pilot asks, over all fuelled parts, not each part's own use.
- A node of the running sums grew from 216 to 528 bytes (thrust and burn for six directions and three fuels). See
  "Sum memory" in `docs/blocks-at-scale.md`.

## Open

- Whether thrusters placed away from the centre also give torque, alongside or instead of gyros.
- Fuel transfer, refineries and stations.
- Whether fuel should weigh something.
- Whether the figures above are right. They are a first balance, not an approved one.
