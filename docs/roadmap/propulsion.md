# Propulsion and fuel

## Status

- **Built:** the simulation. Blocks face one of six ways, thrust is per direction, three fuels are burnt and saved.
- **Built:** the editor turns a part's facing with `R` and warns about a missing direction or tank.
- **Built:** the fuel readout in the pilot console (a fuel row under the two groups, a tooltip, low and empty notices).
- **Built:** low-poly models of engines and both thruster kinds, in flight and in the editor, and their vacuum plumes
  and cold gas puffs (below).
- **Built:** thrusters mount on block faces ("Face mounts" below): data, simulation, editor, shipped ships.
- **Built:** fuel is kept tank by tank ("Fuel per tank" below): simulation, wire, save, and the fuel callout
  ("Decided: readout").
- **Built:** electric ion parts that burn no fuel ("Decided: ion propulsion" below). The escape drone is all ion.
- **Built:** tank models: round vessels in an open frame ("Decided: tank models" below).
- **Built:** tanks, sensors and scanners are never slanted ("Decided: parts never slanted" below).
- **Built:** the module scanners mount on faces too ("Decided: scanners mount" below).
- **Built:** the cold gas thruster is an array placed in three sizes ("Decided: cold gas arrays" below).
- Protocol 74, save layout 23, design encoding 6, design file version 8, evaluator 22.

## Decided: parts

- Thrusters are replaced by two kinds:
  - **Cold gas thruster arrays** (`coldGasThruster`): small to medium, in three sizes. They puff compressed nitrogen.
  - **Hot thrusters** (`hotThruster`): bigger and stronger, small rocket engines that push the hull sideways.
- **Engines** (`engine`) are the large main drives.
- Engines are **rotatable** to face any of the six directions and push only the opposite way. A thruster mounts
  on a face and points out of it ("Face mounts").
  - Engines on the front give braking power.
  - A ship needs parts facing each way it wants to move. The editor warns when a direction has none; warnings never
    block Apply.
- Gyros stay and still do the turning, on electricity.
- **Ion parts** burn no fuel, only power ("Decided: ion propulsion"): `ionDrive` (an engine block) and
  `ionThruster` (a mount).
- The drone is electric: `microIonDrive` and `microIonThruster`. The fuelled small parts (`microthruster` on
  nitrogen, `microengine` on bipropellant) stay in the part list for ships built from the drone.

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
- The one exception is an ion part: it burns no fuel and needs no tank ("Decided: ion propulsion").
- Thrusters burn far less than engines. Boost burns more. All rates are content figures.
- Tanks hold a balanced but fairly long supply.
- Fuel is the main cost of thrust. A small electric draw stays (pumps, valves, ignition), so thrust still slows shield
  recovery, more softly than before. Boost costs fuel more than electricity: it drains the battery over tens of seconds, not
  seconds, and burns far more fuel for its extra speed.
- No refuelling exists yet. Only a ship's first spawn starts with full tanks.
  - An edit keeps overlapping fuel, then pumps spill into remaining same-fuel room. A new tank adds no fuel.
  - A repair restores blocks, not fuel. A tank it builds again is empty. There is no Refuel button.
  - A ship built new from the drone starts with empty tanks.
  - The escape drone is made new each time a pilot steps out or loses a hull. It has no tanks: it is electric, so
    fuel can never strand it. The rule that it starts with full tanks stays in the code for a drone with tanks.
  - Tanks run down are no damage: they never hold back an edit.
- Fuel is simulation state: shared flight code, predicted by the client, saved with the world.
- Dampeners fire the same parts and burn the same fuel. At rest they burn nothing.
  - Rest is exact: flight turns the velocity with `HullRotation`, which keeps zero at zero whatever way the hull
    faces. `Vector3.Transform` does not (about 7.5e-9 m/s on .NET 10), and parts fired at rest for ever.
  - A drift under 1e-4 m/s along an axis (`FlightModel.RestSpeed`) is taken to zero without firing a part.
- Fuel has no mass yet: a full tank and an empty one weigh the same.

## Decided: fuel per tank

Built. Why: gas mining will later fill tanks to different levels. Rules live in `TankFuel`; `PropulsionTests`,
`EditorSessionTests` and `SaveLifecycleTests` hold them.

### What is kept

- Every tank block holds its own kilograms. Its capacity comes from its type and size, as before.
- A design lists its tanks in block order (`HullDesign.Tanks`). A tank is named by its index there.
- A hull keeps the kilograms with its block state (`BlockStateStore.Tanks`), one figure a tank.
- What a hull has of a fuel is its tanks of that fuel added up (`HullState.Fuel`). It is never a figure of its own.
- Kilograms are kept in double precision. Why: one thruster burns 0.000125 kg a tick; in single precision that
  rounds the same way every tick, and a large tank at a low throttle would not fall at all.

### Burning

- A burn draws from every tank of that fuel at once, each in proportion to what it holds.
  - Tanks that are equally full stay equally full, to the bit.
  - No tank of a fuel runs dry before another: they reach nothing in the same tick.
- `FlightModel.Step` is still the one place that spends fuel. Plain arithmetic in tank order.
- A fuel with nothing left gives no thrust, as before.
- A hull stepped without its tanks (a client flying a hull it only knows the motion of) is stepped as if each fuel
  sat in one tank: the same code on three figures.

### Losing, repairing, editing

- A tank that is destroyed loses what it held. The other tanks keep theirs.
- A repair builds a lost tank again, empty.
- An edit preserves the design grid: replacement position compensates for the changed pivot.
- `EditFuel.Transfer` pools fuel in connected groups of overlapping old and new same-fuel tanks, in tank order.
  Shares follow overlap volume; capped shares are offered again to remaining overlapping tanks.
  Grow keeps fuel, shrink keeps what fits, split shares it and merge sums it up to capacity.
- Surplus, deleted, fuel-switched and moved tanks spill into any remaining same-fuel capacity, in tank order.
  Only what cannot fit is dumped. Untouched levels only change when receiving spill.
  Repairs leave lost tanks empty; there is no refuelling.
- The editor worker uses that same rule on a frozen draft and copied snapshot tank levels.
  FUEL ON APPLY lists current/after kilograms, pumped kilograms and dumped kilograms per fuel. Apply asks twice; an edit or Escape clears confirmation.
  Non-flown hulls have unknown levels: capacities and a warning are shown, without blocking Apply.
  Fuel/Delete menu hints show the extra pumping or loss from removing the selected tanks.
- A first spawn and an escape drone start with every tank full. A ship built new from the drone starts with
  every tank empty.
- Nothing refuels. Pumping between tanks happens only when a refit is applied, by the shared rule above.

### Limit

- A design has at most 255 tanks: `limits.maxTanks` in `blocks.json`, never above 255 (`TankFuel.MaxTanks`).
  - Why: a snapshot tells the pilot of every tank, its count is one byte, and 255 records must still fit beside it.
  - A tank can be any size, so this bounds the number of separate tanks, not the fuel.
- Design files, `DesignBytes.Decode` and the editor's draft all refuse one more (`DraftEdit.TooManyTanks`).

### Wire (protocol 52)

- The snapshot header carries the own hull's tanks: one count byte, then four bytes a tank (kilograms, single
  precision), in tank order. A lost tank is sent as nothing.
- 26 bytes with no tank, 34 for the pod's two, at most 1,046 (255 tanks). It replaces the three per-fuel floats.
- 255 records without health (252 bytes each) and the largest header are 65,306 bytes: under the 65,536 a
  snapshot has. One more byte a record would not fit.
- Not quantised to a fill scale: a float is nearer than one part in ten million to what the server keeps, and is
  never nothing while the tank holds something. So the client's "has fuel" always agrees with the server's.
- The client brings each figure into its tank: no number or less than nothing is nothing, more than the capacity
  is the capacity. A count that is not the design's is ignored. A figure that is no number or below nothing
  refuses the snapshot.
- Nobody is sent another hull's tanks.

### Save (layout 8)

- Each hull saves a count and eight bytes a tank (kilograms, double precision), in tank order.
- The three per-fuel figures are no longer saved: a load adds the tanks up.
- Load rules: one figure for each tank of the design; each a number from nothing to the tank's capacity; nothing
  in a tank that was lost. Anything else refuses the whole save.
- A tank is a block, so the room a save keeps for blocks (`HasRoom`) is room for every tank.

### For the interface

- `HullView.Tanks`, own hull only: the kilograms in each tank as prediction has them, entry `i` for tank `i` of
  the design's `HullDesign.Tanks` (block, fuel, capacity). Empty for every other hull.
- It reads `SessionSnapshot.OwnTanks`, the snapshot's own memory.
- `HullView.Fuel` (`FuelFlow`) still has amount, capacity and burn per fuel. The fuel row reads only that.

## Decided: ion propulsion

Built. Rules live in `FlightModel.Step`; `PropulsionTests`, `PowerFlowTests` and `EditorSessionTests` hold them.

### What an ion part is

- A thrust part that burns **no fuel** and runs on **power alone**: `"ion": true` in `blocks.json`.
  - It names no fuel and no `fuelBurn`, and needs a positive `powerDraw`. Loading refuses anything else.
  - It needs no tank. The editor's "no tank" warning never counts it.
- It pushes the hull away from where it faces, like any thrust part.
- Low thrust, high power per thrust: only light craft gain from it.

### Simulation

- Its thrust is a fourth column of `ThrustTable`, after the three fuels (`ThrustTable.IonIndex`).
  - A fuel running out never takes it away: `Sum(push, fuel)` always adds it.
- Its power is kept **per push direction** (`FlightParams.IonPower`).
  - Why: power is all it spends, so a pilot pushing one way pays for that way only.
  - The pilot's throttle in each direction is billed, and so is what the dampeners use to brake.
  - Fuelled parts still draw by the largest throttle asked, over all of them (pumps and valves).
- With the battery flat, all thrust is cut back to what the generator feeds beyond idle: ion parts like the rest.
  - With no power at all an ion part gives nothing, and the dampeners brake no more than they can push.
  - The dampeners' cut is new for every hull, fuelled ones too, but only shows with a flat battery.
- Boost multiplies its draw like any thrust (`boostPowerMultiplier`). It burns nothing more: there is nothing to burn.
- `ThrustState` has a bit for it (bit 4). `Firing(FuelKind.None, facing)` asks for ion parts.
- `ThrustPower`, the most a hull's thrust can draw at once, counts ion parts along three axes: the larger of each
  axis's two directions. The editor's thrust overload warning reads it.
- Nothing on the wire or in a save is about fuel for it. The `ThrustState` bit is the only wire change.

### Figures

| Part | Kind | Thrust | Power at full thrust | MW per kN | Mass |
| --- | --- | --- | --- | --- | --- |
| `ionDrive` | block, per cell | 100 kN | 3.0 MW | 0.03 | 2.0 t |
| `ionThruster` | mount | 12.5 kN | 0.375 MW | 0.03 | 0.3 t |
| `microIonDrive` | block | 30 kN | 0.9 MW | 0.03 | 0.25 t |
| `microIonThruster` | mount | 3.75 kN | 0.1125 MW | 0.03 | 0.03 t |

- Against fuelled parts: half the thrust of the matching part, at about 8 times (drive) to 15 times (thrusters) its
  power. An `engine` is 800 kN for 1.0 MW and 0.2 kg/s; a cold gas thruster 100 kN for 0.0375 MW.
- Why ion is pointless as a main drive: two engines give 1,600 kN (20.3 m/s² on a 79 t ship).
  - Two ion drives in their place give 200 kN: 2.6 m/s², a top speed of 97 m/s against 270.
  - Matching the engines takes sixteen ion drives: 48 MW, four times its 12 MW of generators, and 32 t of drives.

### The electric drone

- Fourteen blocks: core, shield, three gyros (up and to each side), two generators, two batteries, the rift drive,
  three plating blocks and a 1 x 2 x 1 ion drive behind the hull; twenty-four ion thrusters on faces. It is the
  design the developer built in the editor (`content/ships/drone.json`).
- Mass 5.17 t (was 3.52). Forward 11.6 m/s² (60 N from the double drive), braking 5.8, sideways and up and down 2.9
  (were 8.5, 8.5 and 4.3). Hull 500 points (was 340). Turns at 51, 52 and 59 deg/s (three gyros).
- Power: generators 2.4 MW, core and gyros 0.2 MW, batteries 16 MJ. The drone shield draws 0.7 MW (was 0.4): with
  two generators 0.4 MW left a restart (1.8 MW) below the 2.2 MW surplus, so the battery never fell, and the shield
  took 0.18 of the surplus. At 0.7 MW a restart draws 3.15 MW and ordinary recharge 0.32 of the surplus.
  - Full forward draws 1.8 MW: the generators carry it with 0.4 MW to spare. The battery stays full.
  - Forward and sideways: 1.45 MW, the battery runs down in about 32 s.
  - Forward, sideways and the shield recharging: about 12 s. All three axes at once: about 11 s.
  - Boosting forward: 4.05 MW, the battery is empty in about 2.7 s.
- It turns a little faster: 36, 43, 42 deg/s (were 35, 40, 40). Hull points stay 340.
- The editor warns it of thrust overload: all three axes need 1.8 MW, more than it makes. That is the design.

## Decided: readout

- Fuel shows like energy: each fuel's amount, capacity and net flow per second.
- Flow is negative while burning. Nothing adds fuel yet. Ship refineries may later make it positive.
- The console's fuel row sits under the two groups and spans them: one cell per fuel the hull has a tank for.
  - A hull with no tank (the electric drone) shows one cell, "FUEL NO TANKS", and posts no fuel notices.
  - Each cell: name, amount / capacity, net flow in kg/s with an arrow ("—" at rest), a strip of how full.
  - A narrow cell drops the capacity, then the name.
  - Each cell is its own hover target with its own tooltip: that fuel's amount, burn, minutes left at that burn
    ("—" at rest), and its tanks with how many are lost.
- A click on a fuel's cell opens its callout on the flown hull (`FuelCallout`, `FuelCalloutLayout`).
  - Each tank block of the fuel is outlined in the fuel's colour; a lost one in red. Lines from the design's boxes,
    no mesh.
  - A label per tank (`BIPROP 340 / 1000 KG`, a lost one `BIPROP LOST`) with a thin leader to the tank.
  - More tanks than `calloutMaxLabels` (8 at most): grouped in the hull's frame by nearness, one label each
    (`4 TANKS · 1200 KG`, `· 1 LOST`). Groups do not change as the view turns.
  - Labels stand in two columns beside the hull, clear of each other, the screen's edge and the console.
  - The same fuel again, or `Esc` (a back step), closes it; another fuel switches. It stays open with the pointer
    captured again, and in the command view. Another hull, or no tank of the fuel left, closes it.
  - Figures are in `content/visuals/power.json` (`callout*`).
- Low is under `fuelLowFraction` of capacity (`content/visuals/power.json`). The cell turns red and a notice posts once.
  Empty posts its own notice. Both are own hull only, once per crossing, one kind per fuel in `notices.json`.
- The editor's stats already list tank capacity per fuel.

## Decided: models and effects

- Engines and both thruster kinds have a model of our own design. It adapts to the block's size by itself; the player
  never edits it.
- An idle engine is dark. When thrust starts it ignites with subtle sparks, then opens a vacuum plume: wide, soft,
  faint, bell-shaped, hot only at the exit, at most one faint shock node. Bipropellant is pale blue-violet.
- Cold gas thrusters vent a fast, high-pressure jet: thin and dense at the nozzle, opening to a faint, streaked cone of
  translucent grey-white gas, lit by the star, never glowing. A short denser burst when the valve opens; it clears
  quickly when it closes.
- Hot thrusters show a small version of the engine's plume with a faint orange core.
- Ion parts are a flat grid in a short ring. Firing, they show a faint, steady blue-violet glow over the grid and a
  thin straight beam: no sparks, no shock nodes, no flicker.

## Decided: how the models and plumes are made

- Every figure is in `content/visuals/propulsion.json`, keyed by block type id. A part without an entry stays a plain
  block and shows no plume. A wedge-shaped part is always a plain block. Tanks have their own entry ("Decided: tank
  models").
- **Models** (`PropulsionModels`): low poly, built on a worker, never in a frame.
  - A pure function of the part's box (`PartBox`), its facing and the cell size. A thin face mount works the same way.
  - Engines: a square mount block covers the whole mounting face and the box's sides, so neighbours meet it flush;
    then an octagonal housing with panel seams, corner pipes and a bevelled front, a collar with four gusset
    brackets, a throat ring and a faceted bell (10 to 12 sides, 3 to 4 rings).
  - The engine's face is split into nozzles, squares of its shorter side (a 2 by 1 face has two bells). A nozzle's
    exit follows its share of the face; its length follows its exit, capped by a share of the depth. A deeper box
    grows its body, never its bell.
  - Face mounts are pods (`pod` in the model's entry), inside their thin box:
    - A faceted base plate over the whole footprint, corners cut, top edge bevelled, bolted at its four corners.
    - A small octagonal housing on the plate, with a valve box at its side.
    - One to four nozzles on the housing's front: `perCell` per cell of the footprint, at most two a side.
    - Microthruster: one. Hot thruster: one small engine (seam, pipes, collar with brackets, bell). Ion thruster:
      one grid emitter. A cold gas thruster is not a pod but an array ("Decided: cold gas arrays").
  - Ion parts (`grid` in the entry, bars each way) end in an emitter instead of a bell: a short faceted ring on the
    body (`nozzleLength` exit radii high, `lip` thick), a dark disc a little below its rim and a grid of thin bars on
    it. An ion drive's body is the engine's mount block and octagonal housing. No collar.
  - Every facet has vertices of its own: flat shading. Bare metal rides in the length of the normal (`mesh.vert`).
  - Housings are bare metal of mid grey (`housingShade` about 0.3), nozzles darker, throats near black. Metal shines
    with its shade, so a dark facet never flashes white.
- **Hull light** (`mesh.frag`, every hull): the star is held below the tone curve's shoulder (0.72 of its light),
  so sunlit facets stay apart instead of all reading white. A cool fill that turns with the facet, strongest facing
  away from the star, keeps the shaded side readable. Asteroids keep their own light.
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
  - A cold gas puff has no light of its own: lit by the star, far more when seen against it. A short, narrowing
    spike at the exit (`coreLength`) opens into a straight cone (`spread` exit radii wide at its end), its gas thinning
    as the square of its width. Fine streaks along the cone's lines stream out at `flowSpeed`.
  - The gas only reaches as far as it has flown since the valve opened; its front is domed. While the valve's burst
    lasts (`burst`, `burstSeconds`) the gas is denser and the front a denser slug. It is faint: `brightness` 0.26.
  - When a group stops, its last gas flies off down the plume and clears over `fadeSeconds`.
  - An ion glow (`kind` `ion`) has light of its own and no noise at all: a glow over the grid, a soft column that
    barely widens (`spread`) and fades along its length, and a thin beam down the axis. The beam is worked out from
    the ray's closest approach, never thinner than about a pixel, so it does not break into steps. Loading refuses
    an ion glow with sparks, shock nodes, flicker or burst. Its parts are their own group per facing (the fourth).
- Check with `--thrust` and `--zoom` captures (`AGENTS.md`); `PropulsionVisualTests` holds the rules.

## Decided: tank models

Built. Code in `PropulsionModels.Tanks.cs`; `TankModelTests` holds the rules.

### Shape

- A tank is an open frame with round vessels inside. The rest of the block is see-through.
- A vessel's radius comes from the block's **smallest** side. A vessel is never stretched into an oval.
- How many, and which way (`TankLayout`):
  - A cube (any N x N x N): one **sphere**. Its poles point along the tank's facing.
  - A longer block (one long side, e.g. 2 x 1 x 1): one **capsule** along the long side. A cylinder with two
    hemispherical end caps of the same radius.
  - A slab (two long sides, e.g. 2 x 2 x 1): **parallel capsules** along the longest side, side by side across the
    middle side.
  - Capsules in a slab: the middle side divided by the smallest side, rounded, at most `maxVessels` (16). Past it they
    spread apart, never wider.
  - Ties between sides go to the lower axis (x before y before z).
- Radius: `tankShare` (0.8) of the room inside the frame: the smaller of the strip's half width and the smallest half
  side, less one beam. Ends keep the same clearance as the sides.

### Parts of the model

- **Frame:** twelve square beams on the block's edges, flush with its faces. `beam` is 0.08 of the smallest side.
  - A slab adds beams between its vessels, top and bottom and across both ends, so each vessel has a frame.
- **Vessels:** 12 facets round, 3 latitude rings a hemisphere. Flat shaded, painted.
- **Clamp bands:** one round a sphere's equator. A capsule has one at each end of its cylinder and more between,
  no more than `bandSpacing` (2.5) radii apart, at most 32.
- **Struts:** four from every band, diagonally out to the frame's beams.
- **Pipe stub:** toward the tank's facing, ending in a flange on an X brace over that face. In a slab facing across
  its vessels, only the outer vessel on that side has one.
- **Plates:** a face a neighbour touches gets a plate inside the frame, facing in. Why: a neighbour leaves out a face
  the tank wholly covers, which would show as a hole. A face with mounted thrusters gets one facing both ways.

### Look

- Figures and paint are in the `tanks` entry of `content/visuals/propulsion.json`.
- Paint per fuel, a body and a stripe round the vessel's middle:
  - Nitrogen: pale grey-blue, blue stripe.
  - Hydrazine: yellow, dark stripe.
  - Bipropellant: orange, white stripe.
- Frame and struts are bare metal like the engine housings.
- Paint reaches the mesh shader in the length of the normal: 3.5 plus five bits a channel. Bare metal stays below 3.

### What did not change

- Collision, the shield shell and target picking stay block-based.
- The design mesh, a damaged hull's mesh, the editor's parts and ghost, and far hulls drawn block by block all show
  the model, through the same paths as thrust parts. The fuel callout's outlines are the block's box, as before.
- A wedge-shaped tank cannot exist ("Decided: parts never slanted").

## Decided: parts never slanted

Built. One rule, `BoxDesign.ShapeAllowed`; `BoxOnlyTests` and `FrameOrderTests` hold it.

- `"boxOnly": true` in `blocks.json` marks a type that is always a box, never a wedge.
- Today: every tank (`bipropellantTank`, `hydrazineTank`, `nitrogenTank`, both micro tanks), the cargo rack and the
  block instruments (`sensor`, `charting`). The module scanners are face mounts, not blocks.
- Why: a slant would cut a round tank; sensors are boxes of instruments.
- A design file or a received design with a slanted one is refused, with the reason ("a Nitrogen tank is never
  slanted"). No shipped design has one.
- The editor:
  - The Wedge button is off for such a part, with "NEVER SLANTED: NO WEDGE" under the shape.
  - It is placed as a box whatever shape was chosen; the chosen wedge comes back for the next part that may be one.
  - `R` turns it as a box: a box turned is a box.
  - Paste, replace and mirror refuse a slanted one (`DraftEdit.BoxOnly`). Merge joins boxes only already.
- Wire and saves: unchanged. The design encoding is the same; only what a server accepts changed. `blocks.json` is in
  the content identity, so the flag changes the identity by itself: no protocol bump. No figure changes, so no
  evaluator bump.

## Decided: second pass (look and mounting)

- **Thrusters mount on faces.** Cold gas and hot thrusters clip onto the face of any block and take no grid cell.
  - They stick out about 0.2 block. A hot thruster is a bit bigger than a cold gas one.
  - Several can share one face. The ghost shows only the thruster, on the face under the pointer.
  - Engines stay sized blocks that scale with their size.
- **Models are low poly.** Faceted, chunky panels, flat shading. Our own shapes, no other game's art. Built.
- **No gaps.** A model fills the cells it sits in and meets its neighbours flush. Built.
- **The editor shows the models.** The ghost and every placed part show the real model at once. Built.
- **Plumes are vacuum-realistic.** Wide, soft, faint and bell-shaped; few or no shock diamonds. Built.
- **Thrusters on faces are built** ("Face mounts" below).

## Decided: face mounts

Built. Rules live in `FaceMounts`; `PropulsionTests`, `BuildDraftTests` and `EditorSessionTests` hold them.

### What a mount is

- A thruster (`coldGasThruster`, `hotThruster`, `microthruster`, `ionThruster`, `microIonThruster`) is a **mount**: it sits on a block's face and
  fills no grid cell. A block type with `"mount"` in `blocks.json` is a mount and never a block; every other
  type is a block and never a mount.
- A mount names **its block** (design index), **the face** (one of six) and **where on the face** it is.
- Positions are whole **tenths of a cell**, from the face's low corner: `at: [u, v]` along the axes after the
  face's own (`(axis + 1) % 3`, then `(axis + 2) % 3`).
- Its footprint is a square: `side` and `depth` in tenths, per type. Hot 0.5 x 0.5, a cold gas array 0.2, 0.4 or
  0.6 a side by its size ("Decided: cold gas arrays"), all 0.2 deep. Four hot thrusters or four 2 x 2 arrays fit a
  one-cell face.
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
- A mount type is a thrust part, the tractor beam or a module scanner, with no core, sensor, tank or drive, no size
  rules, no `maxSide` and no `boxOnly`.

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

- Design file version 5 added mounts for thrusters, version 6 for the module scanners: `"mounts": [{ "on": 8, "face": "right", "at": [1, 5], "type": "coldGasThruster" }]`.
- Canonical bytes version 4: after the blocks a count, then per mount its type, block, face and two positions
  (`u`, `v` as 16 bits), in the order block, face, `v`, `u`. Another order is refused as noncanonical.
- **Old files still load** (versions 1 to 4), converted:
  - A thruster block stays as plating (`mount.legacyBlock`: `hull`, or `microhull` for the drone) in its cell.
  - It carries four thrusters on every cell of the face it pointed from. Four cold gas thrusters push what a
    one-cell block of them did.
  - A thruster whose nozzle had a block in front of it is left out: it pushes no more.
  - The ship is heavier by the plating. A thruster more than one cell deep pushes less: thrust follows the face.
- Old saves and old servers are refused untouched: encoding 4, evaluator 12, and the protocol and save layout
  under "Status".

### Decided: scanners mount

Built. `gasScanner`, `asteroidScanner` and `asteroidLocator` are face mounts, five to six tenths wide and two deep.

- Each keeps its `module` data and its idle draw (0.1 MW). It has mass, no hit points and no size: a scanner never grew
  with its block, so one mount is one scanner.
- A hull has the scanner **exactly while it has the block it sits on**. `BoxHullSums.AddMount` picks equipment
  (best quality, then mark, then lowest mount index). `ModuleEquipment.Mount` is that mount's index plus one, in
  `BoxDesign.Mounts`. Losing the block cancels a running scan, as before.
- The model is a small round plate with a head by family (`scanners` in `visuals/propulsion.json`): a dome with a
  dark intake (gas), a dish (asteroid scanner), a ring of pickets (locator). Plate rim is the tier's colour.
  Built on workers with the other mount models; the ghost is the mount ghost.
- **Old design files** (version 5 and older) with a scanner block load converted (`mount.blockThrough` is 5):
  - The block becomes plating (`legacyBlock`, `hull`) and carries ONE scanner, centred on the first face with room.
    Order: top, right, left, back, front, bottom.
  - A scanner with no free face is **refused** with the block named, never dropped. Free a face and load again.
  - Saved designs in `designs/` go through the same reader.
- **Old saved worlds are refused** (layout 15, protocol 62): the world codec has no migrations, a world keeps block
  hit points per design, and the content identity changed. Nothing is converted or touched.
- Still blocks, and open: the sector scanner (`charting`) and the passive `sensor`. Whether they mount is not decided.

### Editor

Placing and picking:

- A thruster card places a mount. The ghost is only the thruster's model and box outline, on the face under the
  pointer, snapped to tenths, with its exhaust line. Green where it has room, red with the reason where not.
- `R` does nothing for it, and `S` only steps the size of a cold gas array. The part list says so.
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

### Drawing

- A mount is drawn with its type's model from `propulsion.json`, built in the mount's small box
  (`PropulsionModels.AddMount`, the same `Build` a block gets), on whole and on damaged hulls. With no model it is
  that box.
- The editor's placed mounts, the mount ghost and far hulls drawn block by block take it from `PartModels`.
- Its nozzle is in the hull's nozzle list under its block, so plumes are lit and lost with the block.
- Check with `--open edit-mount` and `--thrust` captures (`AGENTS.md`).

## Decided: cold gas arrays

Built. Rules live in `FaceMounts` and `MountSpec`; the model in `PropulsionModels.ColdGas.cs`. `MountSizeTests` holds them.

### Sizes

- A cold gas thruster is an **array** of nozzles, placed in one of three sizes: 1 x 1, 2 x 2 or 3 x 3.
- Footprint: 0.2 cell a nozzle, so 0.2, 0.4 or 0.6 of a cell a side. Always 0.2 deep.
- Thrust, fuel burn, power draw and mass follow the **nozzle count**. The figures in `blocks.json` are one nozzle's.
- Why 2 x 2 is the old thruster: the old pod already had four nozzles on a 0.4 footprint. A 2 x 2 array has the old
  footprint and, to the bit, the old thrust, burn, power and mass. So one nozzle is a quarter of the old mount.
- The size is a general mount capability: `"sizes"` and `"defaultSize"` in a type's `mount` entry. Only thrust parts
  may have it. Today only `coldGasThruster` does.

### What a design must hold to

- `FaceMounts.Check` is still the one rule. It refuses a size the part is not made in, a footprint that hangs over
  the face, and one that shares volume with a block or another mount.
- An edit that leaves an array without room takes it away in the same undo step, as for any mount.
- Mirrored copies have the size of the original.

### Files and bytes

- Design file version 8: `"size": 3` on a mount. Written for a part made in several sizes.
- A mount that names no size has its type's `defaultSize`: 2 for the array. So **files of every older version load
  and fly as they did**. Converted thruster blocks of versions 1 to 4 carry 2 x 2 arrays, four a cell.
- Canonical bytes version 6: one byte a mount, after its action group.
- Old saved worlds and old servers are refused untouched: encoding 6, evaluator 22, protocol 74.

### Editor

- While placing: the **ARRAY SIZE** buttons in the details panel, or `S` to step to the next size. The ghost, the
  footprint line and the part card's figures follow. The choice is kept for the next array.
- A placed one: the same buttons when it is selected, or **Size** in its right-click menu.
  - It keeps its middle, moved in from an edge it would hang over. One undo step. Never mirrored.
  - A size with no room on its face is off ("FACE TOO SMALL", "NO ROOM").
- Copying a placed array picks it to place in the size it has.

### Model and lights

- A stepped grey plate with cut corners: orange caps on the corners, one cyan slot in each side.
- On it, short dark flared cups in a grid, one a nozzle, the same size on every array. Ribs between the units.
- Each cup has a cyan band round its foot and another inside its mouth.
- **The bands brighten while that array fires.** They read `HullView.Thrust`, as the plumes do: the hull's mesh
  instance is told which way its arrays fire. Idle, the foot band glows faintly and the mouth is dark.
- Every nozzle has its own entry in the design's nozzle list, so every cup shows its own puff.
- Figures are the `array` entry of `coldGasThruster` in `content/visuals/propulsion.json`, validated on load.
- Check with `--open edit-mount --mount-size 1|2|3` and `--thrust left` captures (`AGENTS.md`).

### Not done

- The Prospector keeps its 52 arrays at 2 x 2: that is its old layout, and its balance is pinned by tests.
- The drone's `microthruster` has one size.
- The lights of an array on a hull drawn block by block (lost blocks, far away) follow thrust too; the editor's
  draft and ghost never fire.

## Decided: provisional figures

All in `content/blocks/blocks.json`. Tunable; change them there, never in code.

| Block | Thrust per cell | Fuel | Burn per cell at full thrust | Power at full thrust | Mass | Largest side |
| --- | --- | --- | --- | --- | --- | --- |
| `engine` | 800 kN | bipropellant | 0.2 kg/s | 1.0 MW | 3.5 t | 64 cells (no limit of its own) |
| `microengine` | 60 kN | bipropellant | 0.02 kg/s | 0.12 MW | 0.3 t | no limit of its own |

| Mount | Thrust each | Fuel | Burn each at full thrust | Power at full thrust | Mass | Footprint |
| --- | --- | --- | --- | --- | --- | --- |
| `hotThruster` | 160 kN | hydrazine | 0.015 kg/s | 0.1 MW | 0.75 t | 0.5 x 0.5 x 0.2 cell |
| `coldGasThruster`, per nozzle | 25 kN | nitrogen | 0.001875 kg/s | 0.009375 MW | 0.1125 t | 0.2 x 0.2 x 0.2 cell |
| the same as a 2 x 2 array | 100 kN | nitrogen | 0.0075 kg/s | 0.0375 MW | 0.45 t | 0.4 x 0.4 x 0.2 cell |
| the same as a 3 x 3 array | 225 kN | nitrogen | 0.016875 kg/s | 0.084375 MW | 1.0125 t | 0.6 x 0.6 x 0.2 cell |
| `microthruster` | 7.5 kN | nitrogen | 0.00075 kg/s | 0.0075 MW | 0.0375 t | 0.4 x 0.4 x 0.2 cell |

- A hot thruster, or a 2 x 2 cold gas array, is a quarter of what a one-cell thruster block was, in every figure:
  four on a face are that block.
- No shipped block has a `maxSide` now. The field stays.

| Tank | Holds per cell | Mass |
| --- | --- | --- |
| `bipropellantTank` | 1,000 kg | 3.0 t |
| `hydrazineTank` | 300 kg | 2.5 t |
| `nitrogenTank` | 150 kg | 2.0 t |
| `microBipropellantTank` | 90 kg | 0.15 t |
| `microNitrogenTank` | 24 kg | 0.15 t |

- Boost costs fuel more than power. `boostPowerMultiplier` 9.4 → 4.5 and `boostFuelMultiplier` 2.5 → 4.5.
  Boost still gives 2× thrust and 2× rated speed, so on a full tank it covers about 0.44 times the distance per kg
  that plain thrust does, and it lasts 41% as long (below). Boost heat still locks it out after about 6 s of use.
- Size limits are a per-block field, `maxSide`: the most cells along any side. Left out, the content's limit applies.
- `fuelBurn` and `fuelCapacity` take size rules like any other figure.
- Why engines stay the main drive: best thrust per tonne, they scale with their size, and a bipropellant tank
  carries twice as many thrust-seconds as a nitrogen tank of the same size. Thrusters are cheap and light, take
  no cell, and are limited by the faces a ship has.
- Gyro torque 520 → 680 and drone gyro 0.3 → 0.46: the tanks and extra thrusters added mass, and the ships turn about
  as fast as before.
- `ftlEnergyPerTonne` 2.0 → 1.5 for the same reason: a jump costs a ship about what it did (pod 62 MJ, vessels
  118 MJ), so the fleet still charges within the 12 s calculation and jumps together.

The command pod, mining vessel and survey vessel are no longer shipped: a player starts in the Prospector alone. The
three live on as test fixtures (`tests/Prospect.Tests/fixtures/ships`).

Shipped designs, tanks full, measured by `PropulsionTests`:

| Design | Forward | Braking, sideways, up, down | Engines at full thrust | Boosting whenever the drive allows | One thruster direction at full |
| --- | --- | --- | --- | --- | --- |
| Prospector (the one starting ship) | 12.1 m/s² | braking 9.3, others 6.5 m/s² | 83 min | 34 min | braking 62 min, others 83 min |
| Drone | 11.6 m/s² | braking 5.8, others 2.9 m/s² | no tanks: electric | no tanks | no tanks |

Boost, measured with the battery full and the drive never overheating (`PropulsionTests`, `PowerFlowTests`):

| Design | Boost draw (thrust, was) | Battery empty after (was) | Engines boosting whenever the drive allows (was) |
| --- | --- | --- | --- |
| Prospector | 21.6 MW forward, 33.1 MW braking (ion parts too) | 15 s forward | 34 min |
| Drone | 8.1 MW, ion (4.05 MW with the single drive) | 2.7 s | no tanks |

- Battery time counts idle draw and generation. The old times are worked out from the old multiplier, not measured. A pod that boosts the 6 s heat allows keeps about three
  quarters of its battery; the vessels about half.

With mounts, same mass, thrust and burn as with thruster blocks, so the shipped designs' table did not move:

| Design | Blocks | Mounts | Where | Hull points |
| --- | --- | --- | --- | --- |
| Prospector | 41 | 80: 52 cold gas arrays (all 2 x 2), 4 hot, 2 ion thrusters, 6 scanners, 2 tractor beams, 2 mining lasers | braking on the racks' fronts (16), sideways on their outer sides (12 each), up under the gyros (12), down on the deck (12) | 17,540 |
| Drone | 14 (was 15) | 24 ion thrusters | four each on the top and bottom, right and left, eight on the front (top and bottom blocks) | 500 (was 490) |

- The ships are more compact, so they turn a little differently (drone 38, 45, 40 deg/s; was 39, 43, 45). The
  drone's later three-gyro design turns at 51, 52, 59 deg/s.

- Before: pod 20.5 and 11.5 m/s², vessels 21.9 and 12.4 m/s², drone 16.6 and 8.0 m/s².
- Electric draw at full thrust: pod 4.1 → 1.75 MW, vessels 8.2 → 3.5 MW, drone 0.6 → 0.3 MW.

## What the effects and the readout read

- **Per hull in view:** `HullView.Thrust` (`ThrustState`), for the own hull and every other hull.
  - Three throttles, -127 to 127, along the hull's right, up and back axes: which way it is pushed and how hard.
  - A bit per fuel while parts of that fuel fire, a bit while boost runs, and a bit while ion parts push.
  - `Thrust.Firing(fuel, facing)` answers for one part: 0 to 1. Zero when idle or out of fuel. `FuelKind.None`
    asks for ion parts: zero when idle or without power.
  - Dampener braking is in it. A hull in the jump tunnel fires nothing.
- **Per part:** its facing is `DesignBox.Facing`, its fuel `BlockType.Fuel` (none for an ion part, `BlockType.Ion`),
  both in `HullDesign.Boxes`.
- **Fuel, own hull only:** `HullView.Fuel` (`FuelFlow`): amount, capacity and burn per fuel in kg and kg/s. The burn
  is smoothed like `HullView.Power`. No other hull's tanks are sent to anyone.
- **Tanks, own hull only:** `HullView.Tanks`: kilograms in each tank ("Fuel per tank").
- It is simulation state (`HullState.Thrust`), written by `FlightModel.Step`. Nothing is worked out a second way.
- Wire: 4 bytes in every hull record; the own tanks are one byte and 4 bytes a tank in the snapshot header.
- Nothing here allocates.

## Known limits

- The client flies its other hulls forward as if their tanks were full. Wrong only for one that has run dry.
- A design has at most 255 tanks ("Fuel per tank").
- The client's tanks are the server's in single precision, so the two can differ in a tank's last place until
  the next snapshot. Flight does not depend on it: only on whether a fuel is left.
- An engine's nozzle with a block in front of it still pushes. Nothing checks for it. A mounted thruster cannot
  have one: its box must be clear.
- A mounted thruster is not hit, not picked and not collided with ("Face mounts").
- The electric draw of fuelled parts follows the largest throttle the pilot asks, over all of them, not each part's
  own use, and the dampeners' braking with them draws nothing. Ion parts are billed per direction, dampeners included.
- A node of the running sums grew from 216 to 528 bytes (thrust and burn for six directions and three fuels), then
  to 624 (ion thrust and power for six directions). See "Sum memory" in `docs/blocks-at-scale.md`.

## Open

- Whether thrusters placed away from the centre also give torque, alongside or instead of gyros.
- Fuel transfer between tanks, refineries and stations.
- Whether fuel should weigh something.
- Whether the figures above are right. They are a first balance, not an approved one.
