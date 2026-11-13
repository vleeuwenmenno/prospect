# Persistent worlds

Build this before charts and persistent exploration.
Product decisions below are settled. The original storage proposals remain below for context;
current implementation and validation status live in [implementation](implementation.md) and
[save foundation](../save-foundation.md).

## Decided: automatic saves

- Every world saves automatically. No manual-save workflow.
- Autosave interval is configurable, with a 30-second default; clean shutdown also saves.
- Private games and dedicated servers use the same server-owned save system.
- Each world has its own save, holding world settings, ships, ownership, changed world state and player progress, including future charts.
- The main menu offers Continue for the last world and Load World for selection; dedicated servers select a saved world.

Only a completed save is a recovery point.
The interval is not a hard maximum crash-loss guarantee: capture, writing, failures and retries affect it.
Saving must keep simulation and rendering responsive, preserve the last valid state when interrupted,
and retain recovery copies. Do not silently claim success after a write failure.

## Decided: grow a world without resetting progress

- A private world can become hosted multiplayer without resetting its progress.
- After Discord sign-in, its local owner can explicitly claim the SP character as their account's character within that world.
- Claiming transfers ships, ownership, exploration and charts; a remote joining player cannot claim the host's local character.
- The claimed character remains playable offline in private mode; hosting requires the linked account's sign-in.
- Move the stopped world's self-contained save folder from desktop to dedicated server, preserving player ownership and progress.

One world remains one world across those hosting modes; this is not cross-world character transfer.
Login tokens and Discord credentials never belong in saves.
Portable progress does not imply that any future game build can load it without compatibility checks.

## Decided: first connection by address

- A first connection by bare IP address may present an unverified server identity for explicit acceptance.
- Remember the accepted fingerprint for later connections.
- A changed fingerprint stops the connection and requires explicit acceptance again.
- Do not request an account ticket before the player accepts the identity.

This is trust on first use, not proof that an unfamiliar server is operated by the intended person.
The authenticated game protocol remains subject to security review.

## Decided: ships after disconnect

- Ships remain in the multiplayer world after their owner disconnects.
- Private play saves and stops with the local world. Existing direction says simulation pauses when nobody is connected.
- The world creator/server owner chooses whether personal offline protection is enabled and its delay.
- Protection delays run per ship. Fresh damage restarts that ship's delay, not the whole fleet's.
- Protected ships cannot take or deal damage. Reconnecting removes the owner's protection immediately.

## Decided: alliance protection, when alliances exist

- Alliance-owned ships become eligible only when all alliance members are offline.
- Any alliance member reconnecting immediately removes alliance protection.
- Alliance protection has separate enable/disable and delay controls from personal protection.
- World administrators choose these rules; individual joining players do not override them.
- Eligibility and deadlines persist. Time spent with the server shut down counts toward the delay.

At restart, already elapsed deadlines apply before damage can occur.
Offline-protection time is wall-clock time; it does not require simulating an empty or stopped world.
Building the full alliance system is not a prerequisite for personal protection.

## Inspected foundation

| Area | Current code and consequence |
| --- | --- |
| Shared server | `GameSession.StartLocal` and the dedicated entry point both construct `ServerHost`; one persistence path can serve both. |
| Lifecycle | `ServerHost.Admit` assigns a new session player ID and starter fleet; `RemovePlayer` destroys owned hulls. Reconnect cannot currently restore ownership. |
| Identity | `docs/accounts.md` explicitly says servers do not verify accounts. A signed-in official client is not authenticated persistent ownership on the server. |
| Runtime state | `HullStore` owns unmanaged motion and block state; handles, pointers and cached sums are runtime structures, not a save format. |
| Storage dependency | The account service uses centrally pinned Microsoft.Data.Sqlite and SQLitePCLRaw. `Prospect.Game` does not yet reference SQLite. |

### Additional state to cover

- `DesignCatalogue` already holds canonical immutable design bytes; custom edited designs must be saved, not just stock indices.
- `RockField` generates baseline sectors and keeps moved-rock deltas with tick-based timing; persist mutations and their time basis.
- `HullState`, block HP/alive state and `HullInfo.Visual` include motion, jump state, damage and charge that must not silently reset on load.
- `Screens.World` owns session-only discovery; replace it with authoritative, durable knowledge when charting lands.
- The dedicated container currently has no writable save volume; deployment and shutdown need explicit durable-storage support.

Source entry points: [ServerHost](../../src/Prospect.Game/Server/ServerHost.cs),
[GameSession](../../src/Prospect.Game/Client/GameSession.cs),
[HullStore](../../src/Prospect.Game/World/HullStore.cs),
[RockField](../../src/Prospect.Game/World/RockField.cs),
[account contract](../accounts.md).

## Proposed: one SQLite database per world

Use a world folder containing the database and bounded automatic recovery copies.
The reviewed v1 baseline is one bounded snapshot committed with its metadata, inside `Prospect.Game`.
Preserve the complete ordered design catalogue and role mappings; do not compact it on restart.
Require matching content and generator compatibility. Defer incremental tables and retune migrations until justified.
Keep world metadata, durable owners, hull records, designs and world deltas within one transactional database.
Use explicit versioned records; compact versioned blobs are suitable for block state and canonical designs.
Do not dump unmanaged memory or make the save format depend on the network packet format.

### Why this is the starting recommendation

- It reuses a library family already in the repository instead of adding a database service.
- Transactions allow related ownership, design and hull changes to become durable together.
- A custom whole-world binary/JSON snapshot is simpler initially, but adds bespoke atomic replacement, migration and incremental-update work.
- An external database service complicates offline play and copying a world to a dedicated server.
- A full event log would require replaying every historical simulation version; it is unnecessary for the agreed autosave behaviour.

Recommend WAL with a durability setting appropriate to committed autosaves, initially `synchronous=FULL`.
WAL supports concurrent readers and one writer on a local filesystem; do not run a live world on a network share.
The WAL is part of database state, so copying only a live database file is unsafe.
Verify the actual bundled SQLite runtime against current upstream WAL fixes before adopting it.
Sources: [SQLite WAL](https://www.sqlite.org/wal.html),
[synchronous settings](https://www.sqlite.org/pragma.html#pragma_synchronous).

## Proposed: capture, then write

1. At a defined simulation boundary, publish a coherent save generation into owned, bounded buffers.
2. A single dedicated persistence worker serializes and commits that generation; it never reads live mutable hull memory.
3. Publish completion only after commit, with the captured simulation tick and completed-save time.
4. If writing is slow, bound pending work and expose the overdue state; do not queue unlimited full-world copies.
5. On orderly shutdown, stop mutations, capture the final state, finish writing, then release world memory.

Database I/O, serialization and backup work stay off tick/render threads.
Capturing still has a cost: benchmark copying time, peak memory and tick overruns at supported world sizes.
Do not assume a single huge copy is acceptable or that an unsynchronized multi-tick copy is consistent.
Start with a bounded capture design; use dirty records or versioned chunks only where measurements require them.
Microsoft.Data.Sqlite's async methods execute synchronously, so `async` alone does not move disk work off the tick.
Source: [Microsoft.Data.Sqlite async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async).

## Proposed: durable data and load

- Persist stable world, owner and entity IDs. Rebind runtime slots and references on load; never use display names as identity.
- Save seed, world settings, generator/content compatibility information, simulation time, designs and authoritative mutations.
- Preserve hull transforms, velocities, block HP/losses, visual damage, shields, energy and relevant orders/timers; specify every transient field's load rule.
- Load and validate off the tick before admitting players; rebuild derived sums, geometry and runtime handles using existing validation paths.
- Store protection eligibility/deadlines separately from simulation ticks. Clock rollback and unclean-restart rules need explicit tests.

Seed plus changed asteroids is safe only with compatible generation and content.
Never apply saved asteroid indices against a newly generated, different list.
Initially reject incompatible content with a clear reason; migration support must be explicit and preserve the source save.
Retained ships also need persistent-player/entity budgets independent of simultaneous connection slots.

## Proposed: verified ownership

Before restoring an MP fleet, verify a stable account through a purpose-built admission proof.
Do not accept a caller-supplied account ID or send the account-service bearer token to a game server.
Proof expiry, replay prevention, server binding and verification off the tick belong in the authentication design.
Private local identity works without the account service; claiming creates a durable world-local account binding.
Commit the claim atomically and handle repeated claims/reconnects without duplicating starter fleets.

This is a prerequisite for safe persistent multiplayer, not an optional account-polish task.
The exact proof protocol remains open and needs security review before implementation.

### Reviewed next slice: local identity and claims

- Keep the local-owner designation independent of an optional account binding; validate account uniqueness across owner kinds.
- Put a proposed claim only in its candidate snapshot. Install it live only after that operation's confirmed commit.
- Fence ordinary and final captures until the claim outcome is processed. Publish the exact operation result before backup maintenance.
- Preserve store outcomes: failed attempts may retry; rejected saves halt; conflicts require reconciliation; unknown outcomes halt all further writes.
- Keep private offline play available and remote saved-world admission refused until the network implementation passes review and validation.

Timeouts, disconnects and backup failures do not prove that a claim failed to commit.
Publishing a commit result does not return ownership of the worker's snapshot buffer.
Refuse conflicting ownership without merging, replacing or deleting characters.

### Identity protection and recovery requirements

- Protect world-key files from the first write on every supported hosting platform, including Windows custom world paths; otherwise refuse hosting.
- Missing or mismatched keys never trigger silent regeneration. Private play may remain available.
- A database recovery copy alone restores gameplay, not a lost private key. Identity-preserving backup must retain the matching key with equivalent protection.
- Preserve keys and existing recovery points. Automatic rotation and destructive restore are outside the first slice.
- An observed server fingerprint is not accepted trust: persist it only after explicit acceptance and proof of key possession; preserve old pins on mismatch.

### Network implementation gate

The reviewed direction uses platform TLS for authentication and secret exchange over a bounded carrier,
then custom authenticated records. This is not security certification or delivered networking.
Secure network joining must not silently fall back to an unsigned welcome, including first contact.
Validate purpose, authority, TLS profile and accepted identity before requesting an account ticket.
Only the tick touches transport; only a worker touches TLS. Bound both queues and client receive work,
retain slots until workers finish, and invalidate stale outgoing chunks as well as completed results.
Install receive keys before sealed traffic; sealed Welcome activates gameplay. Freeze encoding and
transition rules before implementation, and test platform interoperability before enabling saved multiplayer.

## Proposed: recovery and portability

- Enforce one running writer/server per world folder; reject a second instance instead of racing two simulations.
- Use SQLite's backup API for live automatic recovery copies, publish only completed copies, and retain a bounded history.
- Preserve a damaged/incompatible source; recovery must report the selected save and potential lost progress, never silently create a fresh world over it.
- Version the schema and payloads; back up before migration and refuse unknown newer formats.
- For host migration, close the world and copy the whole folder. Require compatible game/content, validate on startup, and keep credentials outside it.

The backup API supports consistent copies of an active database; filesystem copying is not its substitute.
Source: [SQLite online backup](https://www.sqlite.org/backup.html).
Exact backup cadence, retention and recovery UI remain open; no named manual-save feature is required.

## Open before implementation

- Remote authentication and recovery/merge rules when an account already owns another character. The private local proof slice refuses such conflicts.
- Protection against blocking/ramming with invulnerable ships, and behaviour of existing fleet orders after logout.
- Health-floor survival semantics, clock changes, and protection eligibility for players connected at the instant of a crash.
- Save capture memory/time budgets, size limits, backup retention and handling prolonged disk-full or I/O failure.
- Resume rules for in-progress jumps, edits and scans; content migration policy and the final storage/schema choice.

## Current limits

SQLite persistence and private saved-world menus are implemented; see the linked foundation contract for measurements and limits.
Dedicated startup and verified ownership are still being completed. Claims, offline protection and exploration are not delivered.
Process-crash tests do not establish power-loss durability. Windows/macOS storage and authentication interoperability remain unverified.

## Implemented local proof boundary

The explicit private saved-world API uses a server-generated S256 challenge and real account-service ticket redemption.
Only the exact candidate save commit reports a link; unknown commits require reopening, never rollback.
The local character remains Local for offline play. Other authorities and existing bindings are refused, never merged.
No player-facing claim UI or remote saved admission is implemented; cross-family review remains required.

## Identity foundation implementation status

Linux disk creation/open now binds a canonical P256 public identity to immutable schema-2 world metadata.
The protected separate private key is never regenerated on load; diagnostic status permits private gameplay after key loss.
Snapshot codec 2 owner/account binding is retained without migration.
Database backups remain gameplay-only; identity-preserving recovery artifacts remain missing.
Windows/macOS protection, independent Claude security review and all remote admission remain unfinished.
