# Propulsion and fuel

## Status

- **Built:** the simulation. Blocks face one of six ways, thrust is per direction, three fuels are burnt and saved.
- **Built:** the editor turns a part's facing with `R` and warns about a missing direction or tank.
- **Not built:** the models and plumes, and the fuel readout in the pilot console. Both have what they need (below).
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

## Decided: models and effects

- Engines and both thruster kinds have a model of our own design. It adapts to the block's size by itself; the player
  never edits it.
- An idle engine is dark. When thrust starts it ignites with sparks, then shoots a plume, with shock diamonds if they
  can be made to look right.
- Cold gas thrusters vent a short, pale, cold puff.
- Hot thrusters show a small hot plume, a miniature engine.

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
