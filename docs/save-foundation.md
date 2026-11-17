# Saved worlds: the foundation

What the server can do with a saved world today, and the contract a world store and the load screens build on.
The product decisions are the persistence roadmap's; this is how the code keeps them.

## Not there yet

- Dedicated worlds can be created and reopened with explicit command-line options, but refuse remote players until verified admission lands.
  The client creates, continues and loads private worlds (see "In the client" below); a hosted client game is not saved.
- No account a server can verify, so a saved world lets in the host's own client and nobody else.
- No claim, no offline protection, no migration between builds, no catalogue compaction.

Saved multiplayer and moving a world between machines stay undelivered until verified accounts land.

## Decided

- One snapshot of the whole world, committed in one transaction. No incremental records.
- One capture buffer, taken once at the world's limits. Nothing queues behind a slow save.
- A saved world keeps its own clock, apart from the scheduler's count of callbacks.
- A save is refused unless save content identity, generator version, design encoding and save layout all match.
  Save identity excludes cargo tuning and the rack capacity figure; wire identity still includes both.
  Cargo saves its old unit, shapes, strength and densities. Invalid old records refuse the save.
  Valid records are repacked under current cargo content; containers that no longer fit are dropped with a log line.
  Refused means left untouched and shown as "OTHER VERSION": there is no migration between generator versions.
- Owners and entities are durable. Connections, handles and input queues are not saved.

## The pieces

| Type | What it is |
| --- | --- |
| `WorldSnapshot` | The bounded state of a world between two intervals. A buffer, never live memory. |
| `WorldSnapshotCodec` | The save's own byte layout, `Version` 18. Reading trusts nothing. |
| `SaveLimits` | Aggregate bounds of a world and of a save: owners, hulls, blocks, asteroids, sectors. |
| `IWorldStore` | A leased store: `Commit`, `Backup`, `Dispose`. One worker thread uses it. |
| `SqliteWorldStore` | The store on disk: one SQLite database per world folder. |
| `WorldPersistence` | Store, state and generation, as `ServerConfig.World`. |
| `SaveWorker` | The thread that owns the store and shares the one buffer with the tick. |

All of it is in `src/Prospect.Game/Persistence`; the server's side is `Server/ServerSave.cs`.

## The store contract

```csharp
// Static, on the concrete store:
CreateResult Create(string folder, WorldSnapshot initial);
OpenResult Open(string folder, SaveCompatibility expected);

// IWorldStore:
CommitResult Commit(WorldSnapshot snapshot, long expectedGeneration, bool final);
BackupResult Backup(BackupReason reason);
void Dispose();
```

- `Create` and `Open` take the world's lease first. A second open is refused (`OpenOutcome.Busy`).
- A store that is returned has already made it durable that the world is running: the clean mark is off.
- Only a `Commit` with `final` that succeeds sets the clean mark. `Dispose` never does.
- `Commit` replaces state and metadata together, only if the stored generation is the expected one.
- A store keeps no reference to a snapshot after the call returns.

What a commit's outcome promises:

| Outcome | The stored world | The server then |
| --- | --- | --- |
| `Committed` | Is the new state, durably. Later steps cannot undo it. | Goes on. |
| `Failed` | Is exactly what it was. The store knows. | Saves again later, with newer state. |
| `Conflict` | Differs from the expected generation; this attempt did not change it. | Stops saving; reconcile or reopen. |
| `Rejected` | This attempt did not change it. | Stops saving for this run. |
| `Unknown` | Is the old state or the new one; the store cannot tell. | Stops saving, the final save too. |

- A store that throws has promised nothing: the worker reads it as `Unknown`.
- A close after `Unknown` ends as `FinalCommitOutcome.Unknown`. The next open says which state it was.

The server owns the store from the moment it is constructed with it, also when construction fails.
A new world starts from `WorldSnapshot.NewWorld(...)`, handed to `Create` and then to the server like any other.

## The store on disk

`SqliteWorldStore`, in `Prospect.Game/Persistence`. Microsoft.Data.Sqlite 10.0.0 with the bundled SQLite 3.53.4.

```csharp
static CreateResult Create(string folder, WorldSnapshot initial);
static OpenResult   Open(string folder, SaveCompatibility expected);
static WorldSummary Summarize(string folder);   // for a list of worlds: no snapshot, nothing written
```

A world folder:

| File | What it is |
| --- | --- |
| `world.lock` | The lease. Made once, never removed or replaced. |
| `world.db` | The world: `application_id` 0x50525350, `user_version` 2, one table, one row. |
| `world.key` | Separate P256 PKCS8 PEM, at most 4096 bytes, Linux mode 0600. Never in a snapshot or database backup. |
| `world.db-wal` | SQLite's log while the world runs. Part of the world: never copy `world.db` alone. |
| `backups/world-<generation>.db` | Gameplay-only recovery copies, each a whole database of one generation. |

The row holds the world's id, name, times, generation, clean mark, the four compatibility values, and the
snapshot as one blob in the codec's layout. A commit replaces all of it in one transaction.

The lease:

- Taken before anything else is opened: an exclusive lock on `world.lock`, plus a table of this process's folders.
- Tried twice. Where the second try is granted, locks hold nobody off on this machine, and the world is refused.
- That check cannot see whether a lock is kept between machines. Local file systems only; network shares are
  not supported, and not all of them are detected.
- A second open, in this process or another, is `Busy`. `Summarize` of a running world is `Busy` too.
- A symbolic link to the folder is the same world: both paths meet at one lock file.

Opening:

1. The lease.
2. A read-only look: sizes, header, exact schema, `quick_check`, the row, compatibility, blob length, decode.
3. Only then the one writing connection: exclusive locking first, write-ahead log, `synchronous=FULL`.
4. The clean mark is turned off, durably, before `Open` returns.

- A refused world keeps `world.db` and `world.db-wal` byte for byte. SQLite may still make a `-shm` file to read the log.
- The store decodes the snapshot within `SaveLimits`. The server judges it against its content when it restores.
- `Create` never builds over a database, a log or a journal that is there. It builds under another name first.
- A folder that is made is synced into its parent before anything is put in it, every new folder on the way too.
- A folder that is already there has its name synced again, all the way up: a sync that failed is retried.
- Except where the system says this user may not write: system directories stay untouched.
- That exception assumes permissions do not change between a failed try and the next. It proves nothing about who
  made a folder; where permissions were changed in between, a missed sync is not made up for.
- Any other error asking that question stops the create or the copy.
- On Windows none of this syncs. A new folder's name is left to the file system's journal there; not tested.

A commit that goes wrong:

- A statement fails and the rollback goes through: `Failed`, nothing changed.
- Anything else is looked up on a new connection, never on the one the transaction lived on.
- The old generation there: `Failed`. The new one, with the same bytes and clean mark: the log is folded into
  the database with full syncing first, and only then is it `Committed`.
- If any of that cannot be done: `Unknown`, and nothing more is written.

The log:

- A write is begun only with room in the log for the largest save. Otherwise the log is folded in first.
- If it cannot be folded in, the write is refused as `Failed`, before anything changed.
- So the log never passes what a world is opened with, and an acknowledged world always opens again.

Recovery copies:

- Made by the worker between commits, with SQLite's backup API: after the run's first save, every 10 minutes
  when a newer save exists, and after the final save.
- Checked before they get their name: reopened from their own file, integrity, world id, generation, decode.
- Three are kept. An older one goes only after a newer one is published.
- Nothing restores from one yet: copy it into an empty folder as `world.db` and open that.

What counts as a copy:

- A file is a copy when it opens, is whole, decodes, and is of this world and of the generation in its name.
- A file that only has the name (damaged, or another world's) is never counted, removed or written over.
- Such a file where the newest copy belongs stops that copy: `Failed`, and it says why.
- A copy is skipped only when a real one of that generation is there, after its file and folder are synced.

The folder's bound:

- Added up before a copy is begun: every file named as a copy, real or not, plus room for a whole database.
- The room set aside is the largest a database may be, not the main file's size: a save may still be in the log.
- Past the bound, no copy is made and nothing is removed to make room.
- Copies left over when an earlier call failed after publishing are removed first, down to three.
- Before any is removed, the three that stay and the folder are synced. If that fails, all of them stay.

Bounds at `SaveLimits.Default`:

| What | Bound |
| --- | --- |
| One database file, live or copy | 121.4 MB (twice the format ceiling, plus 1 MiB) |
| All files named as recovery copies, the one being made included | 485.7 MB |
| The log: never written past it, and refused as damaged beyond it | 485.7 MB |

Earlier pre-cargo measurement on the development machine (Linux, NVMe, Debug build), 10.7 MB encoded:
commit 38 to 51 ms, recovery copy 63 to 67 ms, open and decode 50 ms, 4.6 KiB of managed memory a commit.
The database file was 21.4 MB: a replaced blob's pages stay in the file.

What is verified, and what is not:

- Verified by tests here, on Linux: the native library loads and is the audited build; same-process and
  cross-process exclusion; a killed server's files (database plus log) open at the last acknowledged commit.
- The check for a process told not to lock files is the Unix runtime's switch; it does not exist on Windows.
- Not tested: power loss. The promise is SQLite's own for a write-ahead log with full syncing, no more.
- Not run: macOS and Windows. The directory sync after a rename is exercised on Linux only (`DurableFiles`).
- Dedicated startup uses `--world <folder>` and opt-in `--create-world`; the image provides writable `/data`.
  See [hosting.md](hosting.md) for launch validation under the lease and close exit codes.

## Limits

Engineering defaults in `SaveLimits.Default`, not host tuning.

| Bound | Value |
| --- | --- |
| Owners | 64 |
| Live hulls | 4,096 |
| Blocks of all live hulls | 1,048,576 |
| Pushed asteroids | 16,384 |
| Sectors with a pushed asteroid | 256 |

- Capture arrays are reserved before ticking. The decoded-memory ceiling, including bounded validation scratch, is about 120.1 MB.
- Encoded saves are bounded to about 60.2 MB, including charts, module observations and cargo; the catalogue is at most 1 MiB.
- Cargo reserves 255 records per hull: at the hull limit, about 39.8 MiB in memory and 23.9 MiB encoded. Fuel reserves another 8 MiB in each.
- A world refuses growth before exceeding these bounds (`ServerHost.HasRoom`, `RockField.CanMove`); refusals increment `SaveStatus.RefusedGrowth`.
- One drone per owner is held back. Ordinary growth drops no saved data; cargo-only retuning uses the explicit logged drop rule below.

An asteroid the world cannot move:

- Is decided before the contact exchanges anything, pushes promised earlier in the same interval counted.
- Is struck as a body that does not give way: the hull takes the whole impact, the asteroid stays.
- Is never told to a client. A client predicts it as movable and is corrected by the next snapshot.
- One that moved before can always move again.

A save is refused on load unless it leaves that drone room:

- Leave out the escape drone each owner flies, up to a stock drone's blocks.
- Every other hull and block must fit the limits less one stock drone per owner.

## The clock

- `ServerStatus.Tick` is the next world interval. `TickStats.Tick` is the scheduler's count.
- A callback binds the world's next interval, and uses it up only if it simulates.
- With nobody let in, the callback reads the network, ends handshakes, saves and closes. No interval passes.
- A player who is let in and still downloading counts as let in.
- A game without a store takes the scheduler's count as its interval, as before.

## What a load does

- Rebuilt from the save: block sets, revisions, lost cores, collision counts, sums, flight figures, what a hull
  has of each fuel (its tanks added up).
- Kept exactly: motion, charge, each tank's fuel, shield marks, jump stage and timers, orders, hit points, the hit
  total, dents.
- Started over: contact ticks, order counters, control epochs, rounds, everything of a connection.
- The catalogue and its roles (pod, drone, starting fleet) come from the save, in order. The files are not asked.
- Hulls keep the order they are stepped in. The gaps between slots are not kept.

Letting an owner in again is not a change of control: the hull keeps its order, so a jump goes on.

## Closing

```csharp
void BeginClose();          // ServerHost and GameSession: returns at once, any thread, any number of times
SaveStatus Save { get; }    // any thread, takes no memory
CloseStatus Close { get; }  // any thread, takes no memory
```

1. Call `BeginClose`. The world freezes after its current interval; the network is no longer read.
2. Keep drawing the session while `Close.Completed` is false.
3. Read `Close.FinalCommit`. Tell the player when it is not `Committed`.
4. Stop reading the session, then `Dispose`.

- `Checkpoint` and `Backup` are reported apart: a committed save stays committed when either fails.
- `CloseStatus.CheckpointError` and `BackupError` say why, each on its own; `Error` is both, for one line of text.
- A tick that throws is never captured: `SkippedAfterFault`. A capture already with the worker is still written.
- The final save is tried three times, then the close ends as `Failed`. After `Unknown` it is not tried at all.
- `Dispose` without a close saves nothing and waits a bounded time for the worker.
- A server that is not started closes when whoever ticks it keeps ticking.

## In the client

- New Game with Private creates a world; Continue opens the world played last; Load World lists them all.
- A world folder is named by the world's id (`WorldLibrary.NewFolderId`), never by its name.
- The library is `AppPaths.UserData("worlds")`, or `--worlds`. A capture or benchmark without `--worlds` has none.
- `last-world` in the library names the world played last; it is written once a world runs.
- Every exit the player makes, and a session thread's fault, closes through `SessionLifecycle`: freeze and save,
  stop reading, dispose on a worker, then the next once the world's store is disposed.
- An open that is no longer wanted is cancelled; a session it still makes is closed again by itself, exactly once.
- Work that does not stop offers Stop Waiting after 10 s. A world let go of that way stays "releasing" until its
  save worker disposes the store; opening it again waits for that, or opens anyway when the player says so.
- At most 2 pieces of work let go of without waiting at once. At that bound nothing more opens or is let go of;
  Cancel and Quit still work, and Quit may leave without them after 10 s.
- A session that opens after the player stopped waiting is closed by itself; a close that is not clean is
  reported as any other, and holds back what follows. A store whose dispose fails is reported, not called free.
- A report's headline is SAVE UNCONFIRMED for `Unknown` and `Abandoned`, never NOT SAVED.
- Reports never replace one another: they wait in order, at most 7 (two per piece of work let go of, one for the
  foreground). Quitting ends only after every report is read and that work has ended, unless the player chose
  Quit Anyway.
- An exception in the game's own loop disposes the session without a final save: the last completed save stands.
- A close that is not `Committed`, or is degraded, is shown to the player before anything else happens.
- Autosave is a client setting, 15 s to 5 min, default 30 s, read when a world is opened.

## Deleting worlds

Decided. The Load World screen deletes one world, or every incompatible one, after a confirmation.

- The confirmation names each world (the first five, then a count) and says it is permanent. It lists the save,
  the recovery copies and the identity key (`world.key`). It starts on Cancel.
- "Incompatible" is what the list shows as OTHER VERSION. A damaged or unreadable world is never swept up; it is
  deleted only by name, from its own row. A world in use has no Delete.
- The sweep deletes the worlds the player was shown, by folder id, and checks each again under its lease: one
  this build can open since is kept and reported.
- `WorldLibrary.Delete(ids, onlyIfIncompatibleWith)` runs on a worker (`WorldShelf.Delete`) and returns one
  `DeleteResult` per world: `Deleted`, `Busy`, `Missing`, `Kept`, `Unsafe`, `Failed` or `Partial`. A failure for
  one world never stops the rest and is never hidden: the screen names each world that stayed and why.
- Order, per world: (1) the lease, so a world open here or in another process is refused as `Busy`;
  (2) `world.db` is renamed to `world.db.deleting` and the folder synced: from then on the folder is no world to a
  list, an open or a copy; (3) log, key, recovery copies, then the tombstone; (4) lease released, then
  `world.lock` and the folder.
- A crash or a failure after (2) leaves a tombstone and no `world.db`. `WorldLibrary.RecoverDeletions` finishes
  it, before every list is read. It never touches a folder that still has a `world.db`.
- Paths: only a checked folder id directly under the library. A world folder that is a link, or holds a link
  (directly or in `backups/`), is `Unsafe`: refused whole, nothing followed, nothing touched. A folder with no
  `world.db` is not a world and is left alone. Files the game did not make are left, and the result says so.
- The world played last is forgotten (`last-world` removed) when it is deleted.
- Verified on Linux (`WorldDeletionTests`). Not run: the rename and delete semantics on macOS and Windows,
  power loss, and a network share.

Open: moving deleted worlds to a trash for undo; deleting by age.

## Open

- Player-facing local claim UI and remote account admission.
- What a remote player is in a saved world before that: today, refused.
- Whether a spawn lane is checked for hulls that have flown into it.
- Backup cadence and retention beyond the first numbers: 10 minutes, three copies.
- Restoring from a recovery copy, and what happens to the world that is replaced.

## Local account binding foundation

- `SavedOwner.Kind` remains `Local` after linking. Private reconnect always finds that local character.
- `SavedAccount` stores an account UUID and canonical HTTPS authority, never tokens. Authority uses a lower-case DNS host, optional non-default decimal port, and a trailing slash; paths, credentials and IP literals in brackets are refused.
- Account bindings are unique across Local and Account owners. At most one owner is Local.
- Layout 2 is deliberately incompatible with layout 1; old saves are refused untouched. The generator version
  is `Galaxy.GenerationVersion`, now 4: a saved asteroid is named by its index in a sector's generated list.

`ServerHost.StageVerifiedLocalClaim` is internal, tick-thread only, and requires a private world with an active in-process local owner.
It is a trusted integration seam, not an authentication API: no network or UI handler calls it.
The private local proof worker verifies an account-service ticket before the tick calls this seam.
See docs/accounts.md for the explicit nonvisual API; player-facing claims remain missing.

- One pending claim reserves the owner and account. Another request is refused; an already committed identical link returns `AlreadyLinked`.
- Only its candidate snapshot has the proposed binding. Live state changes only after the exact operation reports `Committed`.
- Operation IDs are independent of capture revision and store generation. An earlier save cannot settle a newer claim.
- Commit reports precede backup maintenance. The worker retains its buffer through the last read; reports never grant buffer ownership.
- Ordinary and final captures wait for settlement. `Failed` leaves the binding unchanged and permits retry; Conflict, Rejected and Unknown halt further writes.

Close before submission discards the pending claim. Close after submission waits for its exact result before final capture.
An unknown commit, including a thrown store call, permits no final overwrite; reopen determines the durable binding.
Deterministic regression tests hold the candidate commit pending and reject a second request; after commitment,
a later refused or expired proof cannot inherit the earlier success. Gates and a manual clock establish the ordering.
Disconnect and backup failure never undo a claim. A tick fault never captures a partially advanced world.
No network admission, hosting authorization, keys, protection, alliances or owner merging are added here.

## Immutable disk identity

Database schema 2 adds a canonical 91-byte P256 SPKI public key to the world's UUID and metadata row.
The fingerprint is SHA256 of that public key. Commits never update the key.
Snapshot codec 2 is unchanged, including account authority and owner bindings.
Older database layouts are refused untouched; there is no migration.

`SqliteWorldStore.Identity` returns the UUID, public key as base64, lowercase hexadecimal fingerprint and `KeyStatus`.
`Present`, `Missing`, `Mismatch`, `Unreadable` and `Unsafe` describe the separate key at open.
This is diagnostic state, not a capability to host: future hosting must revalidate protection and possession.
All identity work occurs during create/open on workers, never during capture or ticks.

- Private creation works without protected-key support: Windows/macOS store only the public identity and report Unsafe.
  No private key is written there until platform protection is implemented; these worlds cannot later host under this identity.
- The key is one PKCS8 PEM block, with no trailing non-whitespace, bounded to 4096 bytes. Other algorithms and curves are refused.
- Creation writes mode 0600 from its first byte, flushes, then publishes and syncs its name before publishing the database.

Path protection and fallback:

- New Unix directories use mode 0700 even with umask 002. Existing permissions are never changed.
- Linux checks filesystem owners and modes, including the opened key inode. Ancestors must belong to this user or root;
  group/other writable ancestors require the sticky bit. The world directory itself must not be shared writable.
- Symlinked parents are supported: link ownership and both link and target ancestor chains are checked. Key symlinks and hard links are unsafe.
- Unsafe paths or unavailable Linux ownership checks create a private world without writing a secret. Status is Unsafe
  while the path is unsafe, or Missing once protection is restored. No open ever generates the absent key.
- A failed creation cleans only its own key files before releasing the lease if no final database exists. Unknown key staging is preserved and refused, including ambiguous crash leftovers.

Missing, corrupt or mismatched keys do not block private gameplay and are never silently replaced.
Database recovery copies are gameplay-only, even though their public metadata retains the fingerprint.
No protected private-key recovery artifact is implemented. Back up the entire stopped folder separately with equivalent permissions.
Copying the stopped complete folder preserves identity; copying only the database cannot recover a missing key.
No commit outcome is changed by identity diagnostics or backup degradation.
Protection assumes the same user and root are trusted and permissions remain stable during create/open.
Private fallback preserves gameplay, not a recoverable signing identity. Database and lease protection are unchanged.
Real Windows/macOS storage and power-loss behavior remain unverified.
Independent Claude security review is pending; saved multiplayer is not unlocked.

## Exploration charts (snapshot codec 3)

Owner, sector and survey world tick are retained for every chart. Natural geography is reconstructed
from the save's immutable seed, generation version and content; charts grant no live activity or mineral survey.
Loading rejects duplicate owner/sector pairs, unknown owners, future survey ticks and content-limit violations.
The tick checks chart room before insertion; no chart is evicted. Scans and temporary ship markers start empty.
The capture reserves content's charts-per-owner times the owner budget; charts use 24 encoded bytes each.
The format ceiling is ushort.MaxValue charts per owner, distinct from the content's gameplay limit.
Codec 2 saves are refused untouched; no migration is included.

The codec-ceiling storage test now includes 4,194,240 charts (65,535 per owner), beyond shipped gameplay tuning.
Measured encoding: 111,346,609 bytes; disk commit median 435 ms, worst 554 ms; copy 3,318 ms; open/decode 1,907 ms.
This is an engineering decoder/storage ceiling, not a gameplay chart allowance.

The decoder checks chart owner references, uniqueness and world-clock timestamps before writable store open.
`MaxDecodedBytes` conservatively includes 128 extra bytes per chart for bounded temporary validation sets.

Exploration storage caps each owner at 4,096 charts, including encoded-size and decode-cache bounds.

### Blind-jump damage and navigation

Blind damage is applied once when the hull enters transit, directly to the existing saved per-block HP and total.
A saved transit therefore already contains its damage; resuming it never rolls again.
No pending roll or extra snapshot field is needed; snapshot codec remains 3.
Navigation plots and last refusal feedback are transient connection state and start empty on load.
The new content and evaluator identity require a compatible save; no migration is provided.

## Sector-size generation update

Generation version 5 stores sector size and gas ranges through the existing setting-id/value snapshot fields.
Loading reapplies them through shared content before restoring sector-addressed state.
Older generation/content identities are refused without migration; the snapshot codec layout is unchanged.

### New ships from a drone

New root-block ships use CreateHull, the existing owner and durable entity id, and an appended canonical design.
Capture and restore keep their name, blocks, position and ownership through the existing hull/catalogue fields.
The drone remains the controlled hull; creation feedback and the remote editor grant are transient.
No new saved field is needed; WorldSnapshotCodec remains version 3.

## Gas kinds compatibility

Generation 6 and evaluator 9 reject older worlds before mutation; there is no migration.
Environment flags and cloud kinds are derived, not persisted. Lightning phase uses the saved world tick.
Save codec remains 3: settings already encode a variable count of id/value pairs; its admitted capacity is now 64.
Ice retains a stable appended resource index. Mining inventory is not yet implemented or saved.

## Module observations (codec 5)

WorldSnapshot.ModuleObservations retains personal gas samples, asteroid analysis and locator observations.
ModuleObservationCount selects the valid prefix; capacity is MaxOwners × 1024, reserved before the server starts.
The server checks capacity before adding knowledge and captures without allocation; only SaveWorker touches the store.
The save layout is separate from ModuleSurvey network messages.

An asteroid analysis stores its main ore and up to two mix-ins with their shares, 131 bytes an observation.
The decoder refuses foreign/missing owners, duplicate family/sector/index keys, future timestamps, invalid finite ranges, impossible mixes,
unsupported content revisions and excessive counts before accepting the snapshot.
Server restoration additionally validates resource ids and that each named cloud/asteroid exists under the saved generator.
Observation timestamps and reported values survive unchanged; operation progress and cooldowns reset.
Generation 8 and codec 5 made older worlds incompatible; they are refused without writes or migration.

## Fuel (codec 8)

- Fuel is saved tank by tank. After its hit points each saved hull holds a count of tanks and eight bytes a tank:
  the kilograms in it, double precision, in the order of the design's tanks (`HullDesign.Tanks`).
- The snapshot holds them in `WorldSnapshot.TankFuel`, hull after hull; `SavedHull.TankStart` and `Tanks` say where.
- A hull's per-fuel figures are not saved. A load adds its tanks up (`HullField.RestoreFigures`).
- After its battery charge a hull still holds four bytes of what its thrust parts were doing (`ThrustState`).
- Load rules (`BlockStateStore.TryRestoreTanks`), after the losses are replayed:
  - One figure for each tank of the hull's design, no more and no fewer.
  - Each a number, not below nothing and not above the tank's capacity.
  - Nothing in a tank the hull has lost.
  - Thrust bits no step writes are refused.
  - A hull that breaks a rule refuses the whole save, as any other rule does. Nothing is clamped to fit.
- Kept exactly, to the bit. Capacity is not saved: it comes from the design.
- Room: a tank is a block, so `HasRoom`'s bound on blocks bounds tanks. The capture buffer has a figure for
  every block.
- The design encoding is part of what a save must match: 4 since thrusters mount on faces. Codec 8, encoding 4
  and evaluator 12 make older worlds incompatible; they are refused untouched, without migration.
- A mounted thruster has nothing of its own in a save: a hull has it while it has the block it sits on.
- Rules and figures: `docs/roadmap/propulsion.md`.

### Motion storage

- Server and client reserve 16,384 rock records and 256 moved-sector list headers before simulation starts.
- Saved worlds use their save limits; unsaved servers use the same default limits.
- One global rock lookup and linked sector lists replace the dense per-sector index arrays. First pushes,
  new moved sectors and full-budget refusals take no memory once the generated sector is resident.
- The client's 512 rendered rocks are a snapshot limit, not a limit on its prediction history.

## Wrecks (codec 9)

- Owner zero marks an unowned wreck. Its existing design, live block state, HP and tank fuel use ordinary hull storage.
- Each saved hull adds two 64-bit values: wreck creation tick and unattended age; owned hulls keep both zero.
- Load rejects negative/future clocks, empty hulls, or wrecks with ownership-only state, power, shields or a jump.
  Core loss alone no longer makes a remaining hull invalid. Live COM and inertia are rebuilt from surviving blocks.
- Wreck growth calls `HasRoom`; excess debris is discarded only after evicting existing wrecks to try to make room.
- Live cleanup settings overwrite their existing setting values during capture. Codec 8 saves are refused untouched.

## Cargo hold (codec 12)

- Each hull saves a bounded range of container records: rack, size, resource, kilograms, health and placement.
- Storage reserves at most 255 records per allowed hull; save size and decoded-memory bounds include them.
- Capture copies the hold without allocating. Restoration validates old bounds and overlaps before any retune.
- Dropped containers are absent from the next save. Other simulation-content changes remain incompatible.
### Moving FTL transit (introduced in codec 11, retained in 12)

- The saved position is the position reached along the route, not the destination.
- Existing saved order, transit duration and elapsed time resume through shared `HullSim` from that position.
- Load requires a jump order, positive duration and elapsed time within that duration for a hull in transit.
- Earlier codecs are refused explicitly; an old teleported transit is never silently treated as a moving one.
- World settings retain `ftlSpeedKmPerSecond`; no per-tick disk work or new snapshot array is needed.

## Free containers (codec 15)

- The snapshot reserves 256 free records before ticking. Each encodes 110 bytes: durable id, Double3 position,
  velocity, orientation, angular velocity, size, resource, kilograms, HP, birth tick, unattended age, durable claim owner and pickup-ready tick.
  Count and next id add eight header bytes; maximum extra encoded storage is 28,168 bytes.
- Capture takes no memory. Save limits reserve both encoded and decoded space, even when the pool is empty.
  Sources still call `HasRoom`; oldest-first sector/global eviction bounds growth.
- Load validates finite poses, normalized orientation, positive HP, payload against original saved cargo rules,
  unique nonzero ids, next-id ordering and clocks. Corrupt originals refuse the save untouched.
  Current cargo retunes then validate payloads, logging drops, and reapply sector caps.
- `containerLifetimeMinutes` persists with world settings (default 60, zero never). Unattended age advances only
  without a connected pilot within the base sensor radius. Birth/id order controls cap eviction.
- Layouts before 15 are refused without migration. Wreck splitting preserves held cargo; only rack damage ejects.

## Rock edits (codec 18)

- A save holds every scraped rock's edit list: rock by rock in the order each was first scraped, a rock's edits in
  the order they were made. Bricks and meshes are not saved; they are worked out again on load.
- A rock's record is its sector, index and edit count (20 bytes), then its edits as on the wire: 21 bytes for one
  that starts anywhere, 9 for one that continues the edit before it. Two counts add eight header bytes.
- Limits: `SaveLimits.MaxEditedRocks` (512) and `MaxRockEdits` (65,536), at most 1,386,496 encoded bytes.
  The server refuses a scrape that would pass them, or the content's own limits, before anything grows.
- Capture copies the lists and takes no memory.
- Decoding checks structure: every rock named once, every count met, every edit one the codec reads.
- Load rule (`RestoreRockEdits`): every edit must be one `content/rules/rock_edits.json` allows on a rock this
  galaxy really has there, and that rock must be one that can be scraped. Otherwise the save is refused whole.
- `rock_edits.json` is in the save identity: a retune refuses older worlds, as for other shared content.
- Layouts before 18 are refused without migration. Codec 18 holds the drone bays of codec 17 and these lists;
  each has its own load rule.

## Drone auto pickup (codec 19)

- Each hull record ends with one byte after its drone bays: auto pickup, 0 or 1 (`SavedHull.AutoPickup`).
- Load rule: 1 is allowed only on a hull with drone bays; any other value, or 1 without bays, refuses the save.
- Only the setting is saved. The pause after a manual recall, the rotating cursor and the refused-container memory
  are not: a load starts them fresh.
- Layouts before 19 are refused without migration.

## Ore chunks and action groups (codec 20)

- A save holds every loose ore chunk and every hull's action group changes made without a rebuild.
- They follow the rock edits as one section: chunk count, next chunk id, change count, then the records.
- A chunk is the wire's record (77 bytes: id, pose, spin, resource, grade, mass) and its birth and age (16 bytes).
- A change is the hull's index in the save, the laser's place in its design's laser list and the group (6 bytes).
- Limits: `OreChunks.Capacity` (256) chunks; 64 changes a saved hull.
- Decoding checks structure: ids below the counter and unique, clocks within the world's, figures finite, a hull
  the save has, each part once.
- Load rule (`RestoreMining`): every chunk of an ore this galaxy has, every change of a laser the restored hull's
  design has and a group it may take. Otherwise the save is refused whole.
- Beams, strokes and ore not yet broken off are not saved: a laser starts over after a load.
- `mining.json` is in the save identity. Layouts before 20 are refused without migration.

## Tractor acquisition (codec 15)

- Free-body position, velocity, orientation and payload are part of the 110-byte saved record.
- Tractor hull handles, mount/rack selection, approach stage and powered flag are transient, never encoded.
  Both codec loading and direct store restoration clear transient pulls; acquisition restarts from the saved pose.
- A stowed container is already in the saved hold; an approaching container remains in the free pool.
  There is no second cargo copy or pending disk operation. Older codec layouts are refused untouched.

- Pickup ownership survives load and reconnect using the same durable owner as hulls. Unknown owners refuse loading.
- Pickup-ready ticks survive unchanged on the saved simulation clock; time offline does not consume the delay.

## Drone bays (codec 17)

- Each owned hull saves one record per bay of its design, in block order: drones inside and ticks left on the
  drone being rebuilt. Five bytes a bay; a wreck saves none. Bays are blocks, so block room covers them.
- Drones out of their bays are not saved. Capture counts each one back into its bay: a loaded world has every
  drone docked, and no order survives a load. Docked drones never exceed the bay's capacity.
- The work drone design is a catalogue role, saved with the pod and the escape drone; load refuses one that is not
  a small craft without a jump drive.
- Load refuses a bay count other than the design's, drones above capacity, a rebuild longer than the bay's, a
  rebuild in a full bay, and anything in a bay whose block was lost. Layouts before 18 are refused untouched
  (18 added rock edits to the bays of 17).
