# Propulsion and fuel

## Status

Decided, not built. Today an `engine` pushes forward only, a `thruster` pushes every way, gyros turn the hull and
all of them draw electricity. No block has a direction and there is no fuel.

## Decided: parts

- Thrusters are replaced by two kinds:
  - **Cold gas thrusters:** small to medium. They puff compressed nitrogen, as real cold gas thrusters do.
  - **Hot thrusters:** bigger and stronger, small rocket engines that push the hull sideways.
- **Engines** are the large main drives.
- All three are **rotatable** to face any of the six directions and push only the opposite way.
  - Engines on the front give braking power.
  - A ship needs parts facing each way it wants to move. The editor warns when a direction has none; warnings never
    block Apply.
- Gyros stay and still do the turning, on electricity.

## Decided: fuel

- Three fuels, each in its own tank block:
  - **Nitrogen** for cold gas thrusters.
  - **Hydrazine** for hot thrusters.
  - **Bipropellant** (fuel and oxidiser in one tank) for engines.
- A part burns only its own fuel. With that fuel gone, it gives no thrust.
- Thrusters burn far less than engines. Boost burns more. All rates are content figures.
- Tanks hold a balanced but fairly long supply.
- Fuel is the main cost of thrust. A small electric draw stays (pumps, valves, ignition), so thrust still slows shield
  recovery, more softly than today.
- No refuelling exists yet. Applying an edit or a repair refills every tank, as a repair restores blocks.
- Fuel is simulation state: shared flight code, predicted by the client, saved with the world.

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

## Open

- Whether thrusters placed away from the centre also give torque, alongside or instead of gyros.
- Size limits per kind (what "small to medium" and "bigger" mean in cells).
- Fuel figures: tank capacity per cell, burn per part, boost multiplier.
- Fuel transfer, refineries and stations.
