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
- A save is refused unless content identity, generator version, design encoding and save layout all match.
  Refused means left untouched and shown as "OTHER VERSION": there is no migration between generator versions.
- Owners and entities are durable. Connections, handles and input queues are not saved.

## The pieces

| Type | What it is |
| --- | --- |
| `WorldSnapshot` | The bounded state of a world between two intervals. A buffer, never live memory. |
| `WorldSnapshotCodec` | The save's own byte layout, `Version` 2. Reading trusts nothing. |
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
| One database file, live or copy | 24.5 MB (twice the largest save, plus 1 MiB) |
| All files named as recovery copies, the one being made included | 98 MB |
| The log: never written past it, and refused as damaged beyond it | 98 MB |

Measured on the development machine (Linux, NVMe, Debug build), the largest save there is, 10.7 MB encoded:
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

- The capture buffer at these limits is about 11 MiB, taken once per saved server.
- The largest encoded save is about 11.2 MiB; the catalogue is at most 1 MiB of it.
- A world refuses to grow past them before it grows: see `ServerHost.HasRoom` and `RockField.CanMove`.
- A refusal is counted in `SaveStatus.RefusedGrowth`. Nothing is dropped from a save.
- One drone per owner is held back, so a pilot whose hull is destroyed always gets out.

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

- Rebuilt from the save: block sets, revisions, lost cores, collision counts, sums, flight figures.
- Kept exactly: motion, charge, shield marks, jump stage and timers, orders, hit points, the hit total, dents.
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

- Only Linux supports protected creation. Windows/macOS creation fails closed; private open reports Unsafe.
- The key is one PKCS8 PEM block, with no trailing non-whitespace, bounded to 4096 bytes. Other algorithms and curves are refused.
- Creation writes mode 0600 from its first byte, flushes, then publishes and syncs its name before publishing the database.
- Group/other writable directories are refused (sticky ancestors such as /tmp are allowed); symbolic links are unsafe.
- A failed creation cleans only its own key files before releasing the lease if no final database exists. Unknown key staging is preserved and refused, including ambiguous crash leftovers.

Missing, corrupt or mismatched keys do not block private gameplay and are never silently replaced.
Database recovery copies are gameplay-only, even though their public metadata retains the fingerprint.
No protected private-key recovery artifact is implemented. Back up the entire stopped folder separately with equivalent permissions.
Copying the stopped complete folder preserves identity; copying only the database cannot recover a missing key.
No commit outcome is changed by identity diagnostics or backup degradation.
Independent Claude security review is pending; saved multiplayer is not unlocked.
