# Implementation sequence and agent ownership

Execution is in progress in isolated worktrees; no commit is authorized.
Snapshot capture/restore and the SQLite store are implemented. Storage review findings are resolved in source;
104 focused save/storage tests pass according to the author. The foundation worktree passed one full gate:
1,451 game tests and 320 listing tests. This does not establish the integrated parent checkout's state.
Private Create/Continue/Load and asynchronous closing are implemented; lifecycle corrections passed source review,
23 focused tests across three runs, and the benchmark with zero allocations.
The foundation and ticket service are now integrated into the parent checkout, preserving its generation changes.
The integrated full gate now passes: 1,531 game tests and 354 listing tests, with clean formatting/build.
The latest claim-proof benchmark passes at 1,911 fps with zero managed allocations at the compositor's 5096×688 resolution.
Generation expectations were recorded against the intended distribution before optimizing its index;
2,245 generations match between baseline and optimized Debug/Release. Independent cross-family review remains pending.
Game admission does not use account tickets yet.
Dedicated startup and packaging are integrated. The Linux nonroot container created/reopened a world and exited cleanly on SIGTERM.
The internal owner-binding and claim-save fence is integrated, including private-world enforcement; private service verification now has an explicit nonvisual API, while UI wiring remains missing.
Independent cross-family review remains pending for generation, dedicated startup and the claim foundation.
Immutable world identity is implemented with schema version 2. Its Linux tests pass; independent security review remains pending.
Private world creation now falls back to a public-only identity when protected key creation is unavailable.
Windows/macOS report Unsafe; real platform storage behavior remains unverified.
Automatic backups remain gameplay-only; they cannot recover a missing private world key.

The false claim-success race now has deterministic pending-commit and later refused/expired-proof coverage.
Dedicated disk-world tests use explicit isolated ports, including failed server construction.
Hosted owner-claim refusal tests use OS-assigned ports; failed-claim retries wait for the worker to return its buffer.

## Current acceptance blockers

- Preserve the reviewed worker-synchronization test fixes and unchanged timing budgets; generation has little headroom.
- Keep an outer process timeout on test commands; bounded worker diagnostics do not bound teardown joins.
- Complete independent review of the measured generation optimization; keep the 15 ms budget unchanged.
- Review dedicated configuration, native packaging, licence texts and claim persistence; recovery workflow remains incomplete.
- Verified multiplayer admission/claims, offline protection and exploration/map implementation remain unfinished.

Power-loss behavior and macOS/Windows storage behavior remain unverified.
Storage retry durability assumes stable filesystem permissions between attempts, as documented in the foundation contract.

Target at most three implementation workers plus the coordinator, not maximum concurrency at every stage.
Models below are requested roles; verify availability through T3 capabilities before delegating.

## 1. Persistence contract first

Fable 5.2 at high effort owns the initial persistence and identity design.
Use xhigh for the security-sensitive admission/claim protocol and hard-to-reverse save migration decisions.
Define durable IDs, the save boundary, load rules, content compatibility and ownership verification before splitting files.
The coordinator resolves design choices and reviews the contract before work depends on it.

The existing account database does not solve world persistence or authenticate game connections.
The foundation must cover those gaps without waiting for the galaxy-map redesign.

## 2. Persistence implementation, up to three workers

| Worker | Model | Owned work |
| --- | --- | --- |
| World lifecycle | Fable 5.2, high | State capture/restore, runtime rebinding, disconnect retention, protection clocks and server integration. |
| Storage | Opus 5.5, high | Database codec, schema, backup/recovery and failure tests against the agreed immutable save contract. Escalate threading or identity changes to Fable. |
| Load interface | Opus 5.5, high | Continue/Load World, save status and claim/hosting flows against agreed APIs; no invented backend success. |

Start workers only after their contracts exist. Storage and UI cannot be integrated as disconnected stubs.
Authentication is a Fable-owned core task; if it needs a separate worker, schedule it in a separate wave rather than sharing edited server files.
Sonnet 5.5 at medium replaces a finished slot for bounded content, deployment documentation and existing-behaviour tests.
Do not add a fourth implementation worker to keep every model busy.

## 3. Exploration foundation

After save/load and ownership pass acceptance, Fable defines and implements authoritative charts, sensors and contact lifetimes.
Resolve seed leakage and collision-prediction needs before declaring server-enforced fog complete.
Define stable snapshot/input contracts for scans, chart state, visibility and jump eligibility.
Keep alliance sharing, trading and equipment upgrades out of this phase.

## 4. Exploration and map, up to three workers

| Worker | Model | Owned work |
| --- | --- | --- |
| Simulation/networking | Fable 5.2, high | Authoritative visibility, chart persistence, travel limits/risk and galaxy bounds. |
| Map/interface | Opus 5.5, high | Interactive slices/tilt, sector cards, contacts, fog and scan feedback. |
| Content/docs | Sonnet 5.5, medium | Agreed configuration, starter sensor/charting equipment, descriptions and controls. |

Each worker owns tests for its changed behaviour.
Sonnet owns content files; core/UI workers request content changes through the coordinator.
Fable alone owns shared protocol changes and version bumps in a wave.
Sonnet's smaller workload can finish early; use it for bounded follow-up work, not speculative features.

## Isolation and review

- Independent simultaneous implementations use separate T3 worktrees from an agreed committed base; this documentation turn does not authorize committing.
- Every brief names goal, files, exclusions, contract dependencies, acceptance checks and what the worker must report unverified.
- Integrate one branch at a time. The coordinator reads every diff and independently verifies the integrated result.
- Risky plans/diffs receive a fresh read-only cross-family review: Codex for Claude-authored nonvisual core work; Claude handles visual review. Implementation stays on the requested Claude models.
- Verify findings before fixes. Every new review carries earlier findings, responses and open objections; no reviewer edits or pushes.

T3 availability was checked: Claude Fable 5.2, Opus 5.5 and Sonnet 5.5 are available.
Do not silently substitute another model when a requested provider/model is unavailable.

## Acceptance: persistence

- Round-trip damaged custom hulls, motion, charges, world settings, asteroid changes and stable owners without regeneration or starter-fleet duplication.
- Reconnect, offline private play, local-account claim and desktop-to-dedicated migration retain ownership; replayed/forged identity is rejected.
- Interrupt capture/write/commit/backup/migration; exercise disk-full, permission failure, corrupt saves and duplicate world opens while preserving the last valid recovery point.
- Verify personal protection, per-ship damage resets, reconnect removal and persisted deadlines with a controllable clock; alliance cases wait for alliance implementation.
- Measure save capture time, memory, write lag and recovery time; no per-tick managed allocation or disk I/O. Exercise shutdown while a save is busy.

## Acceptance: exploration and UI

- Scans chart only the current sector, work while flying and survive save/load as permanent knowledge; ordinary contacts never auto-chart.
- Sensors cross XYZ boundaries; owned remote scouts contribute coverage; unseen changes do not leak through snapshots or other channels.
- Charted jumps, safe blind jumps, longer risky jumps and all vertical bounds agree between client feedback and server checks; damage never violates survival/floor rules.
- Pinned cards resist unrelated hover, ship markers expire correctly, station markers stay stale, and both map views support navigation without bypassing pilot controls.
- Safe captures cover layouts and scale; headless frame-order tests cover pointer/keyboard behaviour. Human flight feel and unsupported-platform checks remain explicitly unverified.

Use the Makefile: `make check` before accepting code, and `make bench` for frame/tick changes.
Add save/visibility scale measurements: the existing benchmark alone does not establish large-world persistence performance.
Only launch the client with approved capture/bench arguments, following AGENTS.md.
Update hosting/account contracts, protocol sizes/version, content identity, README, deployment and this roadmap when their implementation changes.

## Next implementation milestone

A world can be created, automatically saved, closed and loaded with its current authoritative state intact.
Use the same persistence path in a private game and a dedicated server.
Persistent multiplayer ownership must pass authentication/reconnect checks before that mode is called complete.
Ship the save foundation before the larger map/fog milestone; do not bundle the whole roadmap into one risky merge.

## Local proof slice awaiting independent review

The explicit private saved-world claim API now verifies real service tickets before staging the candidate save.
It retains local offline ownership and rejects authority changes; remote saved admission and player-facing claim UI remain missing.
No wire changes, world keys, TLS carrier, hosting unlock, protection, alliances or owner merging are included.
Cross-family review is required before acceptance.

Local proof author checks: 40 initial focused tests passed; after isolating the new hosted-refusal ports,
57 focused proof/claim/flood tests passed. The listing suite passed all 354 tests separately.
The single full `make check` passed formatting/build but failed 2 of 1,515 game tests on UDP binds:
the new public refusal test used port 27420 (now isolated), and the existing constructor-cleanup test failed a rebind.
Both passed the focused rerun. No clean integrated full gate after these changes is claimed.
`make bench`: 1,911 fps at 5096×688, zero render and process allocations; validation layers inactive.
The parent must run the next full gate and obtain independent cross-family review before acceptance.

## Protected disk world identity foundation

Linux disk worlds now have an immutable P256 public key in database schema 2 and a separate `world.key`.
Snapshot codec 2 and its account/owner bindings are unchanged; older database schemas are refused without migration.
`SqliteWorldStore.Identity` exposes UUID, canonical 91-byte SPKI (base64), SHA256 fingerprint and key status.
Missing, malformed, mismatched or unsafe keys never regenerate; private gameplay remains loadable.
This status is an observation at open, not hosting authorization. Saved multiplayer remains locked.

- Key creation uses mode 0600 from the first byte, file flush and durable publication before database publication.
- Only Linux key protection is implemented. Windows/macOS create and open privately with status Unsafe and no newly written private key.
- New Unix directories use 0700 regardless of umask. Linux verifies inode ownership and both sides of symlinked parent paths.
  Shared writable paths fall back to a private, non-hostable world without a secret; keys are never regenerated.
- Automatic database copies are gameplay-only: they cannot recover a lost private key. Protected identity recovery artifacts are not implemented.
- Move or copy the complete stopped folder, preserving key permissions, to retain identity. A bare database is insufficient.
- Independent Claude security review remains required. No admission, TLS, key rotation or destructive recovery is included.

## Identity blocker fix validation

Linux `make check` passes: formatting, build, 1,605 game tests and 354 listing tests.
All 23 identity tests also pass under umask 002.
Intermediate runs exposed transient allocation assertions in unchanged hull tests; their budgets remain unchanged.
The claim retry test now waits for buffer release, and hosted refusal tests no longer bind the default port.
Real Windows/macOS behavior, power-loss durability and independent security review remain unverified.
No frame/tick implementation changed, so this slice did not rerun the benchmark.
