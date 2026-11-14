# Placeable scanning modules

## Implementation status

Only the block metadata decoder is implemented on this branch.
No scanner blocks, scan service, knowledge records, protocol messages or persistence are shipped yet.
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
No shipped content declares module metadata yet.

## Open: integration and tuning

- Concrete ranges, power costs, durations, cooldowns, sample capacity and quality detail thresholds.
- Selecting among duplicate modules, block resizing effects and power priority.
- Gas sample identity, spatial variation and whether composition extends the frozen generation hash.
- Bounded sector preparation for locator scans and invalidation of remembered asteroid data after mining.
- Final scan intent/result API, storage budgets and wire/save layouts.

Existing asteroid data has one resource index and one grade per asteroid, not a resource-share vector.
The scanner must document that interpretation or extend generated composition deliberately.
Existing clients receive the galaxy seed; owner-filtered results alone do not provide seed secrecy.
