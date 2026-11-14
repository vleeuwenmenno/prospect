# Scanning modules

## Status

The non-visual implementation is complete: placeable blocks, derived equipment, generated gas composition,
server scans, private session publication and saved knowledge.
The interface is built (see UI below). Mining, inventory installation and knowledge sharing remain open.

## Decided: equipment

- Modules are damageable, sized blocks with mass and power draw, installed through the ship editor now.
- Family, quality and mark/variant are independent axes. Only Common Mk I blocks ship now.
- Families: gasScanner, asteroidScanner, asteroidLocator; future families extend the schema and server behavior.
- Quality order: Common, Uncommon, Rare, Epic, Legendary, Alien Tech.
- Inventory installation and upgrades come later; mining remains deferred.

## Decided: surveys

- A gas scan reveals local cloud composition, purity and density, with detail determined by equipment quality.
- An asteroid scan targets sector + generated index and reveals resource/grade, mass and size.
- A locator pulse reveals coarse dominant-resource classifications and sampled positions in its sphere.
- Knowledge belongs to its owner, ages by observation tick, survives saves and is never automatically shared.
- Future mining must increment asteroid content revisions and invalidate observed analysis when that change is learned.

## Equipment contract and provisional figures

`BlockType.Module` is a `ModuleSpec`; Family None means absent.
Its family, quality, positive mark (1–255), nonblank variant (up to 32 characters) and figures come from `blocks.json`.
`moduleTiers` multiplies range, accuracy, scanSeconds, powerDraw and cooldownSeconds.
The decoder rejects unknown identities, nonfinite/nonpositive figures and accuracy above one.

`HullStats.GasScanner`, `AsteroidScanner`, `AsteroidLocator` contain `ModuleEquipment`; `Module(family)` selects one.
Block is a one-based design index; zero means absent.
`BoxHullSums` selects highest surviving quality, then mark, then lowest block index; duplicates provide redundancy only.
Size affects mass/hull normally, but not range/detail/time or the 0.1 MW standby draw per block.
Short display names fit existing editor cards; quality and mark remain module metadata.
The editor's All list already enumerates these block types. Designs still pass through `DesignBytes.Decode`.

| Common Mk I block id | Mass t | HP/cell | Range m | Scan s | Active MW | Cooldown s |
| --- | --- | --- | --- | --- | --- | --- |
| gasScanner | 2 | 200 | 1500 | 4 | 0.5 | 10 |
| asteroidScanner | 2.5 | 200 | 3000 | 5 | 0.75 | 10 |
| asteroidLocator | 3 | 200 | 5000 | 2 | 1 | 15 |

Base accuracy is 0.2: percentage observations resolve 5 percentage points; Alien Tech resolves one point.
All six tier multipliers are content data, not hard-coded quality tables.
Asteroids currently carry one resource index and grade: the resource-bearing host material has share one and grade is ore fraction.
Bare rock has resource -1; ice is the existing ice resource. No invented multi-resource composition is published.

## Generated gas contract

`Galaxy.GasComposition.Sample(galaxy, placement, worldPosition)` returns a value-only `GasSample`.
Eight Shares follow `GasComposition.Names`; full generated shares sum to one.
Purity and Density are separate 0–1 figures. Eight anchor-frame octants have independent seeded mixtures and purity.
Density follows the smooth sensor envelope; outside samples have zero density.
Named gases, weights by kind and purity bounds are shared content in `galaxy/galaxy.json.gasComposition`.
Cold favors water vapour/ammonia; lightning favors ionised hydrogen/xenon. Sampling allocates nothing.

Composition adds generated natural data, so generation is 7 even though placement and asteroid ordering are unchanged.
The frozen galaxy hash includes mixture shares, purity and density: `15907186463901216483` on Linux x64.
Cross-platform execution of the new hash remains unverified.

## Server execution

Generation runs on a dedicated worker with preallocated connection mailboxes, never on the tick thread.
The worker's sector buffer is sized for the largest sector when the server is made (`Galaxy.Reserve`, about 2.4 MB),
so the first scan takes no memory and neither does any later one. Only a sector whose candidate rocks exceed twice
`maxPerSector` would grow the key buffer, once.
`make bench-scans` runs each family repeating and fails on any managed allocation; `ModuleScanAllocationTests` covers the
worker, the tick and the interface.
Preparing takes no active energy; Scanning runs for the equipment duration rounded up to simulation ticks.
Each scanning interval consumes active MW / 60 from stored energy after ordinary flight/shield spending.
Active module spending is shared with client prediction through `ModuleScanRules.Spend`; `PowerFlow.Modules` contributes to total draw/net flow. The existing net energy readout includes it; a separate module tooltip row is UI follow-up.
Generators replenish through ordinary simulation. Insufficient energy refuses/cancels as NoPower without partial-interval spending.
A jump, control change or selected module loss/change cancels the operation.

All families share one owner cooldown. Completion and any end after worker dispatch start it; pre-dispatch refusals preserve it.
Repeating a running request preserves progress and reports Busy in Refusal.
States: Idle, Preparing, Scanning, Completed, Cancelled, NoEquipment, NoPower, OutOfRange, Cooldown,
InvalidTarget, Busy, KnowledgeLimit, Unavailable.

A gas request names a cloud anchor. Its sample is the starting hull position or a radial point at 95% of the smooth envelope.
Range is checked against that sample each tick. Asteroid range uses its current displaced surface, including at completion.
Locator origin stays at the starting hull position. Range is bounded to one applied sector width; at most 27 sectors are prepared.
There are at most 256 locator candidates. Partial explicitly reports truncation or possible omissions from displaced asteroids.
Candidates are checked at their current positions before publication, so out-of-range current positions are not disclosed.

## Client API and wire

- `PlayerInputBridge.StartGasScan(SectorCoord anchor)`.
- `PlayerInputBridge.StartAsteroidScan(RockRef target)`.
- `PlayerInputBridge.PulseAsteroidLocator()`.
- `SessionSnapshot.Modules.Scan`: state, Refusal, Serial, Hull, quality, start/end ticks, cooldown, origin, Partial.
- `SessionSnapshot.Modules.Records[0..Count]`: retained observations; `Find(family, sector, index)` locates one.

Progress uses authoritative world tick and StartedTick/CompletesTick only while Scanning.
Gas shares omitted by accuracy remain unresolved; do not renormalize them or infer missing gas names.
Locator records deliberately contain zero radius/mass/grade. Their positions are observations, not live tracking.

Protocol 47: ModuleScan (33), reliable, 18 bytes; ModuleSurvey (34), reliable, at most 999 bytes.
The publication is a 79-byte header plus at most eight 115-byte observations.
Admission, catalogue readiness and normal receive/rate budgets precede request decoding.
Only the receiving owner's records are sent; the client rejects a packet containing a foreign owner.
Snapshot buffers are copied, not shared with simulation state. Publication and active ticks allocate nothing.
Existing clients receive the galaxy seed; this does not solve seed secrecy.

## Knowledge and persistence

Each owner has 1024 observation slots, keyed by family + sector + index.
Gas index is octant; asteroid index is its generated identity. Locator and detailed analysis have distinct keys.
Capacity is checked before growth; a completion that does not fit is refused without evicting old knowledge.
RecordedTick preserves age. ContentRevision is zero while composition is immutable; mining must supply revision/invalidation rules.

WorldSnapshot holds the counted ModuleObservations prefix. Save codec 4 owns its layout independently of network messages.
Capture copies into a preallocated buffer; the save worker alone writes it.
Loading checks owner, uniqueness, capacity, finite values, observation tick, resource catalogue and generated target existence.
Active scans and cooldowns are transient and reset on load. Generated mixtures are recomputed; observed values are retained unchanged.
Evaluator version is 10; protocol 47; generation 7; save codec 4. Older incompatible saves are refused untouched, without migration.

## Decided: interface

- Editor: a Module tab (`PartRole.Module`); cards show family, quality badge (tier colour), mark, range, scan time, draw, cooldown.
- Keys: `X` asteroid scan on the target, `C` gas scan, `Y` locator pulse, `F4` Gas window. All rebindable. They work in flight and the command view.
- Chips over the console, only for modules the ship flown has; click or key does the same. A known no (no target, no cloud, out of range) is said on the chip and sends nothing.
- Target panel: analysis from `SessionSnapshot.Modules` only. Unscanned asteroids show size and distance, never generated ore or mass.
- Gas: window, map card section and Charts mark come from `GasLedger`, a mean over the regions sampled. Unresolved share stays unresolved.
- Locator: screen-space marks by class (`content/visuals/modules.json`). Fresh marks fade; the command view keeps a faint mark. Classes are in the map index.
- Tier colours, classes, gas colours and strip figures are content (`content/visuals/modules.json`).
- Captures: `--open modules|gas|pinned-gas|module-command`, `--target-rock`, `--module gas|asteroid|locator [--module-at s]`, on a copy of the client build whose pod carries modules.

## Open

- Gameplay balance of the provisional figures and quality multipliers.
- UI controls, presentation and locator overlay treatment of partial/stale observations.
- Inventory, upgrades, mining revisions and explicit knowledge sharing/trading.
- Exhaustive locator coverage of displaced asteroids beyond the bounded generated candidate set.
