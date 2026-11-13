# Persistence, exploration and galaxy-map roadmap

Agreed product direction from the discussion on 2026-11-13.
These are planned features, not claims about what works today.
Implementation continues separately; this change records decisions and a read-only technical assessment.

## Read in order

1. [Persistent worlds](persistence.md): autosaves, identity, hosting migration and offline protection.
2. [Exploration and map](exploration.md): hybrid map, charts, sensors, fog and jump limits.
3. [Implementation plan](implementation.md): dependencies, agent ownership and acceptance checks.

## Status vocabulary

- **Decided:** agreed product behaviour; implementation must preserve it.
- **Proposed:** technical recommendation from inspecting the repository; not yet implemented or measured.
- **Open:** unresolved behaviour or tuning; do not silently turn it into a requirement.
- **Deferred:** recorded future work, excluded from the first implementation.

## Priority

Persistent worlds come before persistent exploration and charts.
Establish durable identity and safe world capture before parallel feature work.
Then build charting, authoritative visibility and travel rules, with the map consuming their shared contract.
This orders this roadmap; it does not cancel the editor, collision or scale work progressing separately.

## Scope boundary

No full alliance system, chart market, mining scans, equipment tiers or relay network is required now.
Do not add them merely to make room for future features.
Keep their agreed behaviour documented until their own milestone.

The current implementation is described by [direction](../direction.md), [hosting](../hosting.md),
[accounts](../accounts.md) and the repository README.
