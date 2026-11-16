# Shield contact, exhaust on shields, exhaust on rock

Product direction from the brainstorm on 2026-11-16. Built step by step; this says what is decided.

## Why

- A drone that taps a rock at walking pace flashes its whole shield. Too much.
- A large ship that settles flat on a big rock glows at one point, not along the side that touches.
- Exhaust passes through shields and hits rock with no visible effect.

## Decided

### Everything here is visual

- No push, no heat, no damage from exhaust. Heat comes later with mining.
- Nothing new on the wire or in the save for the contact patch, exhaust glow, dust or marks.

### Contact strength

- A contact's strength is its impact energy (half the reduced mass times the closing speed squared) over the
  shield's capacity, through a square-root curve. The server works it out where it records the hit.
- The faintest contact shows faintly (about 5 %, not today's 65 %). Figures are in `content/visuals/shields.json`
  and the server's share in content.
- Hull damage already scales with the same energy (`collisionDamagePerTonneSpeedSquared`), so glow and damage agree.

### Contact patch

- The server still sends one hit a hull a tick: strength and a point.
- Each client lights every part of the shield near what it touches (the rock's surface, the other hull), not
  one point. A flat landing glows along the whole side.

### Exhaust on shields

- Another hull's plume that reaches a shield makes it shimmer where it passes, scaled by the part's thrust and
  distance.
- A hull's own plumes never light its own shield.

### Exhaust on rock

- Where a plume meets a rock: dust thrown out sideways, and a mark that glows hot for about 2 s and cools to a
  dark scorch that fades over about 30 s. At most 64 marks at once, oldest replaced.
- Per kind, as data in `content/visuals/propulsion.json`: bipropellant and hot thrusters glow, scorch and raise a
  lot of dust; cold gas raises dust only; ion parts a faint shimmer and nothing else.
- Each client works marks out from what every hull already sends (`HullView.Thrust`); they are not saved.

## Order

1. Done: contact strength from energy, and the contact patch.
2. Done: exhaust on shields.
3. Dust and marks on rock.

### Other hull contacts

- Use the nearest live block face as the tangent, with extent bounded by that block and the struck hull.

## Open

- Dust on rocks that are not drawn up close (far levels of detail).

## Step 1 implementation

- Strength uses max(kinetic energy, absorbed shield charge), avoiding double-counting the same collision.
- Full energy is 0.9 times capacity (`shieldImpactEnergyFraction`); visual floor is 0.05.
- Against a heavy rock, shipped drone and pod reach full strength near 8 m/s; the new 5.17-tonne drone at 0.3 m/s is 12/255 (before braking).
  A square-root energy curve is linear in speed: requiring full strength at 8 m/s prevents a lower tap byte
  for the same mass/capacity. Equal-mass hull contacts use half the mass and are correspondingly fainter.
- Loaded rocks provide a sampled tangent; other hulls provide the nearest live block surface, bounded by the
  collision test budget. Remote hulls use the same path. Missing geometry retains the point effect.
- Both the flash and waves stay in the contact plane band; waves spread along that plane from the disk edge.
- Patch extent is bounded by hull size and rock curvature; the band is at most rippleWidth or 10% of hull radius.
  It approximates a curved surface by a tangent disk; narrow ridges and disconnected faces are not reconstructed.
- No evaluator version bump: block-derived physical figures are unchanged; the changed shared blocks.json is
  already hashed into content identity. No protocol or save changes.

## Step 2 implementation

- `ThrustEffects` exports the same nozzle positions, grown lengths and spread radii used for drawn plumes.
  Reaction intensity uses replicated throttle and ignition/release fading. No flight arithmetic is duplicated.
- `ShieldExhaust` rejects own hull ids and capsules outside the shield's bounding sphere, then keeps the nearest eight.
  Each frame slot holds 64 fixed shield records of 25 vec4s (25,600 bytes); no frame allocations or resize rebuilds.
- Strength, reach fraction and colour live in each `propulsion.json` plume entry, beside the geometry they modify.
  `shieldStrength: 0` disables a kind for capture comparisons. All four kinds validate on load.
- `shield.frag` evaluates a soft radial/axial envelope and slow turbulent drift only for selected capsules.
  Coloured light is bounded, never flashes white. Empty lists skip the loop; the mask pass skips all exhaust reads.
- Shields remain last, using the same scene copies. No wire, save or frame.glsl change.

### Limits and checks

- Candidate plumes share the existing visibility, minimum-pixel and global plume budget (largest on screen retained).
  A visible plume can come from a hull outside the frustum. This is a capsule approximation, not gas simulation.
- A capsule uses the plume's maximum spread radius; it does not reconstruct its detailed cone or shock nodes.
  Only the shell receives light; no push, heat, damage, rock marks or extra particles.
- `--open exhaust-shield` and `exhaust-shield-cold` hold two real hulls beside one another for captures;
  ordinary flight still computes thrust and fuel use. See AGENTS.md for pose and ion content-copy instructions.
- Linux GPU checks: empty 765 fps, resize 761 fps (12 rebuilds), engine reaction 765 fps, cold reaction 690 fps,
  all at 5096 x 1390 with zero render/process allocations. All 22 shaders compile; mask specialization has no loops.
  Validation layers were inactive; Windows and the hot-thruster appearance remain unchecked.
