# Placeable scanning modules

## Implementation status

Phase 1 is implemented: three Common Mk I blocks, metadata decoding and live hull stats.
Scan execution, knowledge, protocol publication and persistence are not implemented yet.
UI workers must not treat this document as an available scan API.

## Decided: equipment

- Modules are damageable, placeable blocks with size, mass and power draw.
- Installation uses the ship editor now; inventory installation and upgrades come later.
- Family, quality and mark/variant are independent axes. Initial blocks are Common Mk I only.
- Initial families are gasScanner, asteroidScanner and asteroidLocator; future families can extend the schema.
- Quality order is Common, Uncommon, Rare, Epic, Legendary, Alien Tech.

## Decided: tuning

- Range, accuracy/detail, scan time, power draw and cooldown tier multipliers are content data.
- Figures derive from installed blocks through the shared hull arithmetic, including block loss.
- Server validation requires a present, powered module and enforces range, timing and cooldown.

## Decided: gas scanner

- Each cloud has a deterministic seeded mixture of named gases, percentage shares and local purity/density.
- Mixtures vary across a cloud. Candidate gases include hydrogen, helium, methane, ammonia, neon, xenon and water vapour.
- Cold clouds lean toward water vapour/ammonia; lightning clouds toward ionised/exotic gases.
- Scanning inside or near a cloud reveals composition and purity at the sample location, with detail governed by quality.
- Composition is derived rather than stored as generated world state. Learned samples are stored separately.

## Decided: asteroid scanner and locator

- A targeted asteroid scan reveals resource shares/grades, ice, mass and size, with detail governed by quality.
- Asteroid identity is sector plus generated index. Analysis must be invalidated when future mining changes that asteroid.
- Locator is a one-shot regional pulse reporting coarse dominant-resource classification, not detailed analysis.
- Locator observations are retained knowledge and feed the future vision overlay.
- Mining remains deferred.

## Decided: personal knowledge and threading

- Knowledge belongs to an owner, survives world saves, carries observation age and is never automatically shared.
- Intents go through PlayerInputBridge; states, progress, refusal reasons and results through SessionSnapshot.
- Server publication filters each owner's results before transmission, not merely in the UI.
- Persistence must extend WorldSnapshot, its codec, capture, restoration and SaveLifecycleTests.
- Tick work and capture allocate no managed memory; allocation tests use AllocationProbe.

## Available metadata API

`BlockType.Module` is an optional `ModuleSpec`; `Family == None` means absent.
`ModuleSpec` contains Family, Quality, Mark, Variant, Range, Accuracy, ScanSeconds, PowerDraw and CooldownSeconds.
The block's `module` object has camelCase fields with the same names.
Quality names use the exact strings above; family names use the camelCase names above.
Marks are positive integers up to 255; variants are nonblank strings up to 32 characters.

`moduleTiers` lives alongside `blocks` in the existing shared `blocks.json` schema.
Each quality entry multiplies range, accuracy, scanSeconds, powerDraw and cooldownSeconds.
The decoder refuses unknown identities, invalid marks/variants, nonpositive/nonfinite figures and accuracy above one.
The shipped block ids are `gasScanner`, `asteroidScanner` and `asteroidLocator`.

## Open: integration and tuning

- Concrete ranges, power costs, durations, cooldowns, sample capacity and quality detail thresholds.
- Active scan power priority. Duplicate selection and resizing are specified in the phase 1 contract.
- Gas sample identity, spatial variation and whether composition extends the frozen generation hash.
- Bounded sector preparation for locator scans and invalidation of remembered asteroid data after mining.
- Final scan intent/result API, storage budgets and wire/save layouts.

Existing asteroid data has one resource index and one grade per asteroid, not a resource-share vector.
The scanner must document that interpretation or extend generated composition deliberately.
Existing clients receive the galaxy seed; owner-filtered results alone do not provide seed secrecy.

## Phase 1 contract: installed equipment

`HullStats.GasScanner`, `AsteroidScanner`, `AsteroidLocator` contain `ModuleEquipment`.
`HullStats.Module(family)` selects one; `Block == 0` means absent, otherwise one-based design block index.
All figures come from `BoxHullSums`, including live loss updates. Evaluator version is 10.
The highest quality wins, then highest mark, then lowest block index. Extra blocks provide redundancy only.
Size changes mass/hull normally, but not module performance or its 0.1 MW standby draw per block.
`ModuleEquipment.PowerDraw` is additional active scan MW, to be consumed by the server in phase 3.

| Common Mk I | Mass t | HP per cell | Range m | Scan s | Active MW | Cooldown s |
| --- | --- | --- | --- | --- | --- | --- |
| Gas scanner | 2 | 200 | 1500 | 4 | 0.5 | 10 |
| Asteroid scanner | 2.5 | 200 | 3000 | 5 | 0.75 | 10 |
| Asteroid locator | 3 | 200 | 5000 | 2 | 1 | 15 |

Base accuracy is 0.2. All six tier multipliers are in `blocks/blocks.json.moduleTiers`.
The editor's existing All list enumerates `BlockCatalog.Types`, so these blocks are placeable there.
Palette grouping and labels remain the UI worker's task.

## Phase 2 contract: generated gas composition

`Galaxy.GasComposition.Sample(galaxy, placement, worldPosition)` returns a value-only `GasSample`.
Its eight `Shares` follow `GasComposition.Names`; they sum to one, independent of `Purity` and `Density` (both 0–1).
Gas names, kind weights and purity bounds live in shared `galaxy/galaxy.json.gasComposition`.
Cold mixtures favor water vapour/ammonia; lightning mixtures favor ionised hydrogen/xenon.
Eight anchor-frame octants have independent seeded mixes and purity. Density follows the smooth sensor envelope.
An outside point has zero density; the scanner will choose an in-cloud sample point for a nearby scan.

Composition adds generated natural data, so generation is now 7 and protocol 46.
The frozen galaxy hash includes mixture shares, purity and density: `15907186463901216483` on Linux x64.
Placement streams, cloud geometry and asteroid ordering are unchanged; the version change is deliberately conservative.
Old saves are refused by existing compatibility checks, with no migration or write to them.
Sampling allocates nothing; no generated composition is saved, only future observations.
