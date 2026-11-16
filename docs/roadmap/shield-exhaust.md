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

1. Contact strength from energy, and the contact patch.
2. Exhaust on shields.
3. Dust and marks on rock.

## Open

- Whether other hulls' contacts (not only rocks) need their own patch rule.
- Dust on rocks that are not drawn up close (far levels of detail).
