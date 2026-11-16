# FTL travel: flying, not teleporting

Product direction from 2026-11-16. Implementation added; validation recorded below.

## Why

- Other players can observe travel through their current sensor coverage.

## Decided

- A jump moves the hull along a straight line from where it left to where it arrives, at its FTL speed, every
  server tick. No teleport at the end.
- All drive kinds (hyperspace, wormhole, rift) travel this way; each may have its own speed figure and its own
  look on the map.
- FTL speed defaults to 40 km/s (today's `ftlSpeed`: about 10 s for 400 km). It is a world setting: chosen on
  New Game, kept in the saved world, readable from the server settings file.
- In transit a hull cannot collide or be hit.
- The map shows a hull in FTL as a moving marker tagged "FTL", with a line to its destination and a short trail.
- Who sees it follows today's sensor and fog rules: a viewer sees an FTL hull where they have sensor coverage
  of its current position.
- A world saved mid-jump resumes the jump from where the hull was.
- The pilot's own view keeps the tunnel; the client does not stream rock sectors passed through at FTL speed.

## Open

- Whether party members (and friends and alliances, once they exist) always see each other's ships, in FTL
  or not, without sensor coverage. Today's exploration rule says knowledge is personal; live sharing of
  positions within a party would be a new, separate rule.

- Whether drives differ in speed by default, or only in look.
- Whether a jump can be interrupted (interdiction) later.

## Implementation

- Shared `HullSim` moves every drive kind and fleet order; prediction runs the same function.
- Existing wire/save fields carry position, phase, destination and clocks; protocol 57 and codec 11 change their meaning.
- Client rock-sector requests pause during transit. Pilot tunnel effects keep their clocks.
- `--start local --open map-ftl --set ftlSpeedKmPerSecond=10 --capture <path>` shows owned fleet travel on the map.
- Party/friend/alliance visibility is not implemented; server policy is `MaySeeHull`.
