# Placeable scanning modules

## Implementation status

Phases 1–4 are implemented: blocks/stats, gas composition, server execution and private client publication.
Persistence is not implemented yet.
The phase 4 contract is available to non-rendering callers and future UI integration.

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

## Phase 3 contract: authoritative execution

The server supports gas scan, targeted asteroid scan and locator preparation/execution.
Generation runs on one dedicated worker with a preallocated mailbox per connection; the tick never generates survey sectors.
`Preparing` has no progress timer or active draw. Once prepared, `Scanning` lasts the equipment's rounded-up tick duration.
Each active interval consumes the module's additional MW / 60 from stored energy after ordinary flight/shield spending.
Generators replenish that pool through ordinary simulation; no battery/energy means `NoPower`.
Jumping, control change or loss/change of the selected module cancels a running operation.

All families share an owner cooldown. Completion, cancellation, target/range failure after preparation and power failure start it.
Pre-dispatch refusals do not change cooldown. Repeating a running request keeps progress and reports `Busy` separately.
States/refusals: Idle, Preparing, Scanning, Completed, Cancelled, NoEquipment, NoPower, OutOfRange, Cooldown,
InvalidTarget, Busy, KnowledgeLimit, Unavailable.

Gas targets identify a cloud anchor; the sample is at the starting hull position or the nearest radial point at 95% of its smooth envelope.
Range is checked against that fixed sample every tick. Asteroid range uses its current displaced surface, including at completion.
The locator's sphere stays at its starting origin. Maximum supported range is one applied sector width; preparation visits at most 27 sectors.
Locator output is bounded to 256 candidates; `Partial` explicitly reports truncation or moved-rock candidate incompleteness.
Moved candidates are filtered against the sphere again before publication; no current out-of-range position is disclosed.

Owner knowledge has 1024 slots, keyed by family + sector + index. Gas index is octant; asteroid index is generated identity.
Capacity refuses an entire completion before growth, without evicting older knowledge.
`RecordedTick` is observation age. `ContentRevision` is zero until mining implements revision/invalidation.
Asteroids currently have one resource and grade: the resource-bearing host material has share one; grade is ore fraction.
Bare rock has resource -1. Ice is the existing ice resource. Locator reveals only the dominant resource and sampled position.
Common percentage detail is 5 percentage points, improving with content accuracy; unreported gas share remains unresolved, not renormalized.
No client intent/publication contract is available until phase 4; persistence follows in phase 5.

## Phase 4 contract: client integration and privacy

Protocol is 47. `ModuleScan` (33) is an 18-byte reliable intent; `ModuleSurvey` (34) is a bounded reliable publication.
`ModuleMessage.MaxWireSize` is 999 bytes: 79-byte header and at most eight 115-byte observations.
Requests are decoded only after admission, catalogue readiness and normal receive/rate budgets.
Client reliable budgets reserve one module request; retries retain a newer queued intent.

Call `PlayerInputBridge.StartGasScan(anchor)`, `StartAsteroidScan(RockRef)` or `PulseAsteroidLocator()`.
Read `SessionSnapshot.Modules.Scan` for state, serial, hull, quality, start/end ticks, cooldown, origin and Partial.
`Refusal` reports Busy without replacing an operation already running.
Progress is derived from authoritative world tick against StartedTick/CompletesTick, only while Scanning.
Read only `Modules.Records[0..Count]`; `Find(family, sector, index)` locates retained knowledge.
Gas percentages omitted by accuracy are an unresolved remainder. Locator records deliberately carry zero mass/radius/grade.

The server sends only the receiving owner's records, incrementally, and the client rejects a packet containing any foreign owner.
Publication uses caller-owned buffers and counted prefixes; snapshot copies never expose server-owned arrays.
No automatic party sharing, live tracking or uncharted-data regeneration is granted by an observation.
Seed secrecy remains a separate unfinished problem: ordinary clients still receive the galaxy seed.
