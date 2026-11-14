# Hosting and joining

How a game is opened to other players, how a password works, and what the game does with the listing server.
The listing server's own contract is in [listing-server.md](listing-server.md).

- Code: `src/Prospect.Game/Server/ServerHost.cs` (visibility, handshake, limits),
  `src/Prospect.Game/Protocol/Passwords.cs`, `src/Prospect.Game/Server/ConnectionLimits.cs`,
  `src/Prospect.Game/World/ContentIdentity.cs`, `src/Prospect.Game/Lobby` (announcer, server browser).
- Screens: New Game (hosting), Join Server (browser, direct connect, password prompt), the session-ended panel
  (password prompt) and the in-game menu (listed or not). All in `src/Prospect.Client/Screens`.

## Visibility

| Visibility | UDP listener | On the list | Who can join |
| --- | --- | --- | --- |
| `private` | none | no | Nobody. The host alone. A local game's default. |
| `unlisted` | yes | no | Anyone with the address. A dedicated server's default. |
| `public` | yes | yes | Anyone; it shows in the server browser. |

- A private game opens no socket. There is nothing to connect to.
- Unlisted and public can have a password.
- A dedicated server cannot be private.

## Hosting from the client

A hosted local game is the same server a local game always runs, with a UDP listener beside the in-process link.

- The host plays over the in-process link and is never asked for the password.
- Others join over UDP, by address or from the list.
- **The host needs a UDP port that others can reach** (default 27420): a public address, or a port forwarded
  on the router. There is no NAT traversal.
- The listing server does not check that a listed game can be reached.

In the client: New Game, then "Who can join". Unlisted and Public show the name, password, player slots and port,
and need a Discord sign-in before Start ([accounts](accounts.md)). Private does not. Join Server needs it too.
A start that fails (port busy, a name or password that cannot be used) comes back to the screen with the reason. In the
game, the `Esc` menu says whether a public game is listed right now.

A hosted game is not saved, and the screen says so. A private game is a saved world
([save-foundation](save-foundation.md)); a saved world lets in no remote player until the server can verify one.

The same from the command line:

```sh
make run ARGS="--host unlisted --host-name 'Ceres Station'"                 # joined by address
make run ARGS="--host public --host-name 'Ceres Station' --host-players 8"  # also on the list
PROSPECT_PASSWORD='open sesame' make run ARGS="--host public"               # with a password
PROSPECT_PASSWORD='open sesame' make run ARGS="--join 203.0.113.7:27420"    # join one
```

- Give the password in `PROSPECT_PASSWORD`. Arguments show in process lists; the environment does not.
- `--password <text>` still works. `make run` and `make server` then print none of the arguments, so the
  password stays out of the scroll-back and out of a captured launch log.
- `make -n` and `make --trace` print commands as they are. Do not use them with `--password`.

## Dedicated server

```sh
make server ARGS="--name 'Ceres Station' --visibility public"
PROSPECT_PASSWORD='open sesame' make server ARGS="--visibility public"
make server ARGS="--settings my-server.json"
```

The settings file may hold the server's own values under `"server"`, beside the world settings:

```json
{
  "hullStrength": 2,
  "server": {
    "name": "Ceres Station",
    "visibility": "public",
    "port": 27420,
    "maxPlayers": 16,
    "password": "open sesame",
    "listing": "https://prospect.mvl.sh",
    "limits": { "pendingPerAddress": 8, "pendingTotal": 32 }
  }
}
```

- An argument wins over `PROSPECT_PASSWORD`, which wins over the file.
- Prefer the environment variable or the file for the password: arguments show in process lists.
- A file with a password in it should be readable by the server's user only.
- A name is 1 to 48 characters after cleaning, the listing server's own rule.
- `limits` is optional; what it can hold is under [Connection limits](#connection-limits).

## The hello

The first message on a connection. Three fields, in this order:

| Field | Size | What |
| --- | --- | --- |
| Protocol version | 4 bytes | First in every version, so two builds can always say they differ. |
| Player name | 1 length byte, up to 48 bytes of UTF-8 | Cleaned on arrival. |
| Content identity | 16 bytes | What the client's shared content comes to. |

The server checks a hello in this order and stops at the first that fails:

1. The version is the server's. Else "game versions do not match".
2. A player slot is free. Else "server full".
3. With a password: the address is not locked out. Else "too many attempts", with the wait.
4. The name and the identity are both there. Else the connection is refused as a bad handshake.
5. The identity is the server's. Else "game files do not match" (`ContentMismatch`).

Then the player is let in, or, with a password, challenged.

- A hello that is refused costs the server no memory and no log line. Refusals are counted; see
  [Connection limits](#connection-limits).
- The host's own client, on the in-process link, goes through the same checks.

## Content must match

A client joins only with the same shared content as the server. The same version is not enough.
Both sides simulate from their own `content/`: with other block figures the same hull flies differently on each.

The identity covers:

- `content/blocks/blocks.json`: block types, flight rules, size rules, design limits and fixed collision budgets.
- `content/rules/settings.json`: which world settings exist, their ranges and defaults.
- `content/galaxy/galaxy.json`: the galaxy rules and sector modules.
- `content/net/blocks.json`: block recovery and bulk transfer bounds.

It also includes the evaluator version, `ContentIdentity.EvaluatorVersion`, a number in code.

It leaves out:

- `content/visuals` and `content/input`: only the client reads them.
- `content/net/listing.json`: where the list is. A host may point at their own.
- `content/net/social.json` and `content/net/editor.json`: the server enforces its own limits and tells a
  client what it refused. A client's copy only shapes its interface.
- A world's settings: the server sends them in the welcome.
- Fleet mappings and all design files: the server supplies its catalogue after welcome.

How it is made:

- Each file is parsed and counts for what it says. Line endings, indentation and string escapes do not matter.
- A changed value or name, an added or removed entry, and the order of entries or properties all do.
- A number counts as written: `1` and `1.0` are different content.
- Each file goes in with its name and length, in the order the game loads them.
- The identity is the first 16 bytes of the SHA-256 of that, computed once when content is loaded.

What it gives:

- It catches accidents: a stale checkout, a retuned file, a mod on one side only.
- A refusal for content is not a wrong password. Nothing is counted against the address.
- With a password, other content is refused before any challenge. No password would help.
- The server logs its identity when it starts. A client that is refused logs its own. Compare the two.

What it does **not** give:

- **It is not a proof.** A changed client can send any identity. The server still decides everything.
- It is not part of the password proof. It is not secret, and the proof protects the password only.
- Shared rules are not sent. Ship designs are sent by canonical SHA-256 hash after welcome.

Bump `ContentIdentity.EvaluatorVersion` when the arithmetic that turns blocks into figures changes what comes
out and the files stay the same.

## Password

The password is never sent. It is not replayable.

1. The server makes a random 16-byte **salt** when it starts and derives its **key** once: PBKDF2-HMAC-SHA256 of
   the password (UTF-8) with that salt, 200,000 rounds, 32 bytes.
2. It answers every `Hello` that passes [its checks](#the-hello) with a `Challenge`: the salt and 32 fresh
   random bytes for that one connection.
3. The client derives the same key and sends a `Proof`: HMAC-SHA256, keyed with the key, of the text
   `PROSPECT password proof v1`, the protocol version (4 bytes, little-endian), the salt and the challenge.
4. The server computes the same and compares in constant time. One proof per connection.

What that gives:

- A proof answers one challenge. Overheard, it opens no other connection and no other server.
- A client without a password ends with "password required"; a wrong one with "wrong password".
- Wrong passwords are counted per address. Three are free; then each further one locks the address out for
  twice as long: 2, 4, 8 seconds, up to 5 minutes. A count is forgotten 15 minutes after the last one.
- The table holds 256 addresses. When it is full, addresses without a row share one.
- A handshake that goes silent is not a wrong password. It is ended by the clock; see
  [Connection limits](#connection-limits).

What it does **not** give:

- **It protects the password, not the traffic.** Game packets are not encrypted or signed. Anyone on the path
  can read and change them.
- Someone who relays a live handshake between a player and the server gets in with it.
- A weak password can be guessed offline by anyone who overhears one handshake. Use a long one.
- The bytes typed are compared. The composed and the decomposed form of one accented letter are two
  different passwords.

What stays in memory:

- The server keeps the key derived from the password, never the password. The key is cleared when the server
  is disposed.
- A client session lets go of the password once it has answered the challenge, been let in or ended.
- The client's launch options let go of it once the game has started. A hosted game's password stays in its
  hosting options, where the next game takes it from.
- That is all managed memory allows. The text itself cannot be overwritten: it stays until the collector frees
  it, and the command line, the environment or the settings file it came from still holds it.

## Connection limits

A connection is **pending** from the moment it is made until its player is let in or turned away.
One address cannot keep the door shut to others, and no amount of network input holds up a tick.

| Limit | Default | In the settings file, under `"server"`, `"limits"` |
| --- | --- | --- |
| Pending connections from one address | 8 | `pendingPerAddress` |
| Pending connections in all | 32 | `pendingTotal` |
| Connections one address may open | 20 at once, then 1 a second | `connectionBurst`, `connectionsPerSecond` |
| Time for the whole handshake | 5 seconds, by the clock | `handshakeSeconds` |

At the door:

- A pending connection has a slot of its own. It never holds a player's slot.
- The limits are checked before a connection gets a slot and before anything it sent is read.
- Over a limit, the connection is told how long to wait and closed.
- A pending connection may send its hello, then its proof. Anything else, an empty message too, closes it.
- The host's own client, on the in-process link, is not counted.

Players behind one address (a household, a LAN party):

- Eight can be in the handshake at the same moment. A handshake takes a tick, or a fraction of a second with a password.
- Twenty connections at once covers ten players who are each first told a password is needed.
- The listener starts one connection per address every 100 ms. A client told to wait asks again by itself.

Reading the network, every tick:

1. Handshakes that are out of time end. A server that is running slow gives nobody longer.
2. Each listener is read up to a budget: 1,024 packets and 256 KiB, plus 32 packets and 8 KiB per player slot.
3. What is still waiting stays queued, in order, for the next tick. Nothing is dropped by the budget.
4. A legitimate player's packets behind a burst are read a tick later. The simulation runs on time either way.
5. Each player gets separate unreliable and reliable allowances of 32 messages and 8 KiB per tick.
   Excess unreliable input is dropped; half a second of it removes the player.
   Reliable messages execute in arrival order, even after an input burst. Exceeding either reliable allowance
   disconnects immediately: one-shot commands are never silently dropped while the player stays connected.
   The listener's total budget is unchanged; packets beyond it wait in transport order for the next tick.

Refusals are counted, not logged one by one: one line in the log every 5 seconds says how many and why.
Refusing a connection allocates nothing.

What one address can still do:

- Hold up to 8 of the 32 pending slots, for as long as it keeps connecting.
- Lock out everyone else behind the same address, who share its limits.
- On a server without a password, join like anyone, once per connection, until the player slots are full.
  There is no limit on players per address: a LAN party needs them. Use a password to keep strangers out.
- Send more than the link can carry. Nothing in the game helps against that.

What many addresses can still do (a thousand, or one sender that forges them):

- Fill the door: a handful of addresses hold all 32 pending slots. Newcomers are told to wait. Players already
  in stay in.
- Use up the table of 256 addresses the connection rate is counted in. The rest then share one allowance.
- Fill the network layer's own table. RakNet gives a slot to anyone who sends the first datagram of its
  handshake and keeps it for 10 seconds; the game never sees these. One address is held to about 100 of them
  and the listener has 512 to spare. Some fifty forged datagrams a second fill it, and nobody new connects.
- Fill the budget every tick. Players' input is then read late, for as long as the flood lasts.

A host who needs more than this needs a firewall or a provider that filters floods in front of the server.

## The listing server, from the game's side

- The default address is in `content/net/listing.json` (`https://prospect.mvl.sh`). `--listing <url>` replaces it,
  `--listing off` turns it off, on the client and on the server.
- HTTPS is required. Plain HTTP is accepted only for this machine (`make listing`).
- Every request has a time-out and a size limit. Redirects are not followed.

A public server (dedicated, or a hosted local game):

1. Announces itself, then refreshes every 30 seconds with its token.
2. On a 409 (an entry from a crashed run) keeps asking every 30 seconds until that entry expires.
3. On a failure waits 5 seconds, then twice as long each time, up to 5 minutes. One log line per change.
4. Removes its entry on a clean shutdown, waiting at most 3 seconds for it.
5. Announces over IPv4 only: the list shows the address the announcement came from, and the game's transport
   is IPv4.

The server browser (`ServerBrowser`, shown by Join Server):

- Fetches the list for the game's own protocol version, off the render thread.
- Trusts nothing: at most 200 entries, names cleaned, lengths checked again.
- An address must be a literal IPv4 address in its normal form. Host names, IPv6 and odd spellings are dropped.
- Private and loopback addresses are dropped unless the listing server itself is on this machine.
- Nothing is sent to a listed game until the player picks it.

The browser screen:

- A name is drawn as text only, clipped to its column with "...". It is never a widget label, an id or a format
  string; a row's id is its index in the list. `##`, `###` and `%` show as typed.
- Japanese (kana, kanji, CJK punctuation, full and half width forms) draws with the bundled Noto Sans JP, merged
  behind every interface font.
- Any other character the interface font lacks shows as `?`, the font's fallback glyph, with that glyph's width.
  Emoji are not drawn.
- The list is sorted once per fetch: games with room first, then the busiest, then by name.
- Nothing marks any game as official.
- With the listing server turned off, the screen says so and shows only direct connect.

## Known limits

- No NAT traversal and no reachability check.
- IPv4 only: BRUTAL's RakNet binding carries no IPv6 addresses.
- The announced port is the port the game listens on. A router that maps another outside port lists the wrong one.
- The list takes at most 255 players per server; a larger server reports 255.
- The lock-out and the connection limits count by address, so players behind one address share them.
- A flood from many addresses, or with forged addresses, can keep newcomers out; see
  [Connection limits](#connection-limits).
- Game traffic is not encrypted or signed.
- Shared rules are compared and the list does not show them. Different ships are downloaded at admission.

## Optional central accounts

The optional [account backend](accounts.md) stores Discord accounts and friends beside the listing service.
The official client asks for a sign-in before joining or hosting unlisted/public; that is client policy only.
The game handshake does not attest an account; servers admit as before and roster names are not verified.
Party and chat keep session identities. A dedicated server needs no account service.

## Block state replication (protocol 22)

A pose names its block revision. Losses name the preceding revision and up to 16 block ids.
The receiver applies only contiguous new losses; duplicates do nothing. Gaps request the complete alive set.
Applied acknowledgements are explicit, per hull. Queuing a snapshot never acknowledges its damage.

- `HullRecord.MaxWireSize` is 218 B; loss-free records omit the 64 B of loss ids.
  A block acknowledgement datagram is at most 578 B (64 entries). It uses the unreliable receive allowance,
  after admission and the existing packet/byte limits; social and reliable command budgets are unchanged.
- Full-state chunks use `NetChannel.Bulk`, ordered independently on RakNet stream 1 (gameplay uses stream 0).
  Each chunk has 20 B of envelope and whole 8-B words; sends cost the per-client byte allowance, including headers.
- Shared defaults: 32,768 B per transfer, two simultaneous transfers, 65,536 B held recovery data per peer,
  one new request per 30 ticks, 600 ticks to finish and 1,024 outgoing bulk bytes per server tick.
  Failed sends retain their offset. Expired transfers release their slot; repeated requests restart recovery.
- Client storage holds at most 256 received hull states and 256 latest incomplete poses. Only known shared designs
  allocate state, sized by that design. Chunk size, offsets, revision, alive count and unused tail bits are validated.
  Published states remain separate until a matching pose arrives; no partial block set enters prediction.
- Loss history reserves four bytes per block when a hull is created, and lasts until the hull is released.
  It bounds retries without allocating on damage. Repairs and changing a hull's design are not part of this protocol.

## Admission catalogue (protocol 23)

Welcome enters `SessionState.Downloading`; no poses enter prediction until catalogue completion.
The client requests manifest id 255 after welcome, avoiding ordering assumptions between reliable streams.
The manifest carries count, pod, drone, starting-fleet indices, then each index's byte length and SHA-256.
At most 255 designs and 255 starting-fleet entries are admitted. Conflicting hash lengths fail.

- `DesignData` uses the bulk lane: 12-byte envelope (id, design byte, total length, offset, ushort chunk length).
  Manifest and design bytes share `BlockSender` slots, request throttling and outgoing byte budget with block recovery.
  Defaults remain 32,768 bytes, two slots, 65,536 held bytes, 30 request ticks, 600 timeout ticks and 1,024 bytes/tick.
- Validated configurable ceilings are 8 MiB per transfer and 64 MiB held; defaults are unchanged.
  Admission has one receive buffer and one decode worker; block recovery only starts afterwards.
  Refused sends keep offsets. Admission has a fixed hard deadline computed from aggregate bytes, request spacing and bounded retry slack.
- Cache matches use canonical hashes, regardless of local index order. Missing bytes are hashed, decoded and
  evaluated on one task worker. Bad hashes, malformed geometry and nonfinite figures fail admission.
  Shutdown cancels and joins this bounded worker. No arbitrary mid-session catalogue changes are supported.
- Successful admission creates a new `GameContent`; existing content and snapshot arrays are never rebound.
  `CatalogueReady` reliably unlocks server snapshots, initial rock deltas and roster publication.

The loader and canonical decoder both enforce the same shield admission before publication: the intact hull's
smooth shield shell must fit `surfaceVertices` and `surfaceIndices` within the builder's work limits, and is kept
with the design.
`blocks/blocks.json` includes surfaceVertices, surfaceIndices, geometryPairTests, shieldRoundness and shieldBlend; `net/blocks.json` includes catalogueBytes
and catalogueBoxes. Both files are hashed as shared content. Manifest totals include cache hits and duplicates.
Defaults: 196,608 encoded catalogue bytes, conservatively at most 8,192 decoded boxes (26 minimum bytes/box).
Admission deadline: timeoutTicks + 256 * (requestTicks + 1) + 4 * (catalogueBytes + 9500) / min(bytesPerTick - 24, 1000).
Transfer deadline: timeoutTicks + 4 * transfers * maxBytes / min(bytesPerTick - 24, 1000).
Integer divisions round down; timeoutTicks provides additional slack. Headers are included in the throughput
allowance. These fixed deadlines never extend with peer progress, so stalled peers cannot hold admission forever.

## Ship editor (protocol 26)

A player edits the hull they fly, in space. The server decides when, checks the design and replaces the hull.
`content/net/editor.json` holds the server's limits. It is not part of the content identity.
The client's editor (`B`) drives it through `PlayerInputBridge` and reads `SessionSnapshot.Editor`.

The edit:

- **Begin** names the player's own hull. Allowed when it is alive, piloted, not jumping and has gone
  `quietSeconds` (30) without damage. A refusal carries the reason and the seconds to wait.
- **Damage ends it.** So do leaving the hull, losing it, a jump order, cancelling and disconnecting.
  Other players do not block an edit. The hull is not protected while it is edited.
- **Commit** announces a design: its byte count and SHA-256. Chunks of at most 1,024 B follow on the bulk lane,
  each at exactly the offset the server has reached.
- **A damaged hull is refused** (`RepairFirst`, protocol 28) before a buffer, a check or the catalogue is
  touched, cached designs included: lost blocks or hull points, by `HullDesign.NeedsRepair`. The edit stays open
  for a repair. Asked again before a checked design is published.
- **Checked off the tick** by `DesignBytes.Decode`, the proof admission uses: hash, canonical form, limits,
  one kind of FTL drive, geometry admission, finite figures. Name, class, block size and colour must be the
  edited hull's.
- **Applied** when the owner's client can show the design. Every stage is checked again before it.

What a design need not have:

- **Thrust, torque or power.** A design needs a core. Without thrusters it lies still, without a gyro it does not
  turn, without a generator nothing runs. The server builds it all the same; the editor warns.
- **A shield with nothing to feed it** has a capacity and no charge. Nothing makes energy or shield from no power.
- **An FTL drive.** Without one the hull does not jump. Any number of drives of one kind (hyperspace, wormhole,
  rift) is fine, normal and compact alike; drives of two kinds are refused, in design files and the editor too.
- Shipped design files still must fly (`HullDesign.Parse`): that check is for content, not for players.

Repair:

- **`Repair` names an open edit by its token** and carries nothing else. The server has the design.
- **The hull is rebuilt as the design it has**: every block back and whole, at the blueprint's hull points.
- **Same design number.** No upload, no check, no catalogue entry, no `DesignNotice`.
- **Same gates as a commit**, except that it is the one request a damaged hull is allowed: stale token, damage
  since the edit began, a jump, leaving the hull, `commitSeconds`.
- **A whole hull is refused** (`NoChange`). A shield that is down is not hull damage. `HullDesign.NeedsRepair`
  is the one test, for the server and for a client asking beforehand.
- **Free for now**, as building is. Shield and battery charge is kept, not refilled.
- The game side is `PlayerInputBridge.RepairEdit(token)`, behind the editor's **Repair ship** button. On a
  damaged ship the editor only shows, selects and repairs until the ship is whole.

What a server holds:

- `uploads` (2) designs at a time, over all players, in buffers taken at start: within `heldBytes`.
- Size, room in the catalogue and a free buffer are checked before a byte is copied.
- Chunks count against the sender's unreliable allowance. A dropped or misplaced chunk fails that upload.
- One check per held design on the thread pool. An edit that ends keeps its buffer until its check is done.
  Shutdown waits for the checks.
- `commitSeconds` (2) between a player's accepted commits, including cached designs; `uploadSeconds` (20) to arrive, `readySeconds` (20) for
  the owner's client, `peerWaitSeconds` (3) more for the other clients.

The catalogue grows:

- **Append-only.** A new design takes the next number, 255 at most. A number's bytes never change.
- **Same bytes, same number.** A design the catalogue has is not sent again and takes no number.
- **The hull's own design again is refused** (`NoChange`): it is no edit. A repair is asked for by name.
- **A full catalogue refuses the whole design** (`CatalogueFull`). The budgets are `catalogueBytes`,
  `catalogueBoxes` and the 255 numbers; every entry counts.
- **Nothing is removed** until the server stops, used or not.

Clients:

- `DesignNotice` (number, size, hash) goes to every admitted client, in order. A refused send is repeated.
- Missing bytes are asked for with `DesignRequest` and come on the existing bulk slots and budget. A client
  does not fetch the design it committed, or one in its own files. A partial download with no progress for
  `AdmissionTicks` restarts through the same rate-limited request path.
- One worker checks and decodes. The design goes at the end; known designs keep their numbers and objects.
- `DesignReady` is sent when the design is decoded **and** the renderer has installed its mesh
  (`PlayerInputBridge.ReportInstalledDesigns`). A client that could do neither says so.
- Until then the server sends that client no hull of the design. Nobody is paused and nothing reconnects.

Joining meanwhile:

- A manifest lists the designs of the moment the client asked for it, and never changes afterwards.
- Designs added later are announced after admission, like to anyone else.

The replacement:

- **A new hull with a new id.** The old one is removed only after the new one exists.
- Owner, pilot, heading, velocity and spin are kept. The grid stays where it was in space.
- Blocks are new and whole: building is free. Shield and battery keep their charge, up to the new capacity.
- A repair is this same replacement, by the design the hull already has.
- A jump order effective in this interval cancels the edit before replacement. Otherwise queued inputs are dropped.
  `EditorState` gives the old and the new id.
- The time without damage carries over, also into a drone a player steps out in. Editing an escape drone
  preserves its temporary identity: no further ejection, cleanup on leaving, and respawn at the spawn point.

Sizes: `Editor` 42 B at most (a repair is 6 B), `EditorData` 11 B and its chunk, `EditorState` 17 B, `DesignNotice` 38 B,
`DesignReady` 3 B. A client sends one editor request and one `DesignReady` a tick; both are reserved in its
reliable allowance.

Known limits:

- The catalogue fills up. About 190 kB of designs fit beside the shipped ones, and then every new design is
  refused until the server restarts. Numbers are not reused.
- A session without a renderer never reports a mesh, so it is sent no edited hull and cannot edit.
- A client that cannot take one design takes none after it: designs are numbered by their place.
- Inputs already on their way when the hull is replaced fly the new hull, as after a change of hull.
- For `peerWaitSeconds` at most, a client that is not ready does not see the rebuilt ship; it can still be hit by it.


### Internal shape encoding (protocol 27)

Canonical design bytes version 2 append one shape byte per block (0 box, 1–12 canonical wedges).
The minimum record is now 27 bytes; catalogue allocation checks use that bound.
Evaluator identity 4 includes half-volume statistics and shape-aware collision.
JSON version 3 carries canonical shape names; old JSON 1/2 files remain boxes.
Wedges are playable through the editor and take the smooth shield like any block; see
[wedges](blocks-at-scale.md#wedges-protocol-27).

## World generation settings (protocol 33)

The welcome carries asteroid settings by id through the existing world settings catalogue.
Server and client apply them before creating their seeded galaxy or generating any rocks.
`GameContent.WithSettings` applies `Galaxy.Under` and `AsteroidFieldsModule.Under` from original rules,
alongside `FlightRules.Under`; repeated applications never compound and other sessions stay unchanged.

- Controls and ranges: [World settings](../README.md#world-settings). They also work through `--set` and settings files.
- Missing entries in older settings files use catalogue defaults. The shared content identity covers this catalogue.
- The cap never raises the content's `maxPerSector`; settings do not raise placement attempts or field budgets.
- Changed geometry uses bounded placement: failed placements are omitted. Map counts are estimates;
  generated sector summaries use actual counts. Arrival clearance and resource availability remain enforced.
- Live defaults use the former maximum asteroid multiplier preset as 1×; seven multipliers accept up to 5×.
  Content and the absolute count maximum now allow 20,480 rocks per sector. Dense defaults may exceed old clipping.
  Radius stays within 0.75–6,000 m controls and field fit; occurrence, ore share and grade cap at 100%.
  Placement has ten attempts per rock and can omit crowded rocks, even below the requested minimum.
  Protocol 33 separates this retuning; the frozen legacy fixture and block evaluator are unchanged.

World settings now have 48 slots (38 shipped); the maximum settings payload is 1,777 bytes.
Welcome's maximum grows from that constant; the server has a 64 KiB send buffer and RakNet grows its receive scratch buffer.
Shared sanitization bounds every endpoint and lowers a crossed minimum to its maximum before generation and after network reads.
The paired ranges and per-material rules are listed in the README. Material abundance weights only existing availability;
zero removes the material consistently from sector character and asteroid picks.

Protocol 36 and generation version 4 identify fields and clouds in world space; saves of older generation versions are refused, untouched.
The rules are in [world generation](world-generation.md): a field is anchored in the sector its centre lies in and reaches the sectors around it.
Field support follows four seeded transverse sections with smooth bends, varying widths and tapered ends.
Sparse outskirts follow that support rather than filling an enclosing ellipsoid.
`distribution.outskirts`, `width`, `bend`, `fade` and `jitter` tune this shape in galaxy content; `density` and `inGas` tune thinning and rocks in gas.
Field envelopes may overlap, but every rock passes its sector's bounds, arrival clearance and spacing against all rocks placed before it in that sector.
No rock is moved after acceptance. The frozen generation hash and live retuning hashes record this algorithm.

## Visual collision damage backend (protocol 33)

- The server retains at most 16 dents per hull, one strongest quantized profile per blueprint block.
  New blocks are dropped at capacity; existing profiles remain, and destruction frees slots. Physical damage is unchanged.
- Threshold, full-depth damage fraction and maximum inward fraction are shared content in `blocks/blocks.json`.
  Dents require post-shield collision damage. Asteroids and collision shapes never deform. Repair replaces the hull and clears dents and spark ownership.
- Cosmetic revisions are independent of loss revisions and input sequences. Each pose adds 26 bytes: visual version (4), spark sequence (4), authoritative tick (8), contact profile (10).
  A full dent payload is 161 bytes maximum, zero-padded to 168 bytes for the existing word transfer envelope (20 bytes); one chunk costs 188 bytes.
- `VisualState` uses the existing bulk slots, byte budget, timeout and request spacing. Transfers freeze bytes/version; acknowledgements request the latest missing revision again.
  Loss recovery preempts cosmetic transfers. Client installation is atomic; poses never wait for dents. Each acknowledgement is 13 bytes, at most 32 entries (418 bytes including header).
- Defaults remain two slots, 32,768 bytes per slot, 65,536 held bytes, 1,024 bytes per tick and one new request per 30 ticks.
  Two dirty hulls normally recover within two request intervals after catalogue admission; many dirty hulls share this finite service rate. Retries have no guaranteed latency under sustained congestion.

### Renderer contract (backend only)

`HullView.Visual` is an inline value copy owned by each `SessionSnapshot`; no simulation arrays are borrowed.
`Version` names the installed full state, not necessarily the latest advertised version. Read only `Dents[0..Count]`.
Profiles retain blueprint block indices; use the view's live block mask to hide destroyed blocks while a newer cosmetic revision is in flight.

- `DentProfile.Point(design)` returns hull-local coordinates relative to the immutable design pivot.
  `Face` uses `BlockShapes.Face` sides: +X, -X, +Y, -Y, +Z, -Z. Wedge slants use `BlockShapes.SlantSide`; absent faces are rejected.
- `U/65535` and `V/65535` multiply the face edges from corner 0 to corner 1 and corner 0 to the last corner.
  Triangles require U+V <= 65535. `BlockShapes.FaceNormal` supplies the outward normal.
- `Depth/255 * Content.Rules.DentMaxDepth` is the inward fraction toward the original solid centroid; geometry, pivots and physics remain unchanged in this slice.
  Equal quantized strength keeps the earlier profile. Versions saturate at uint.MaxValue; all profile writes then stop; the live mask still hides destroyed blocks.
- `Visual.Spark` carries independent sequence, authoritative world tick and localized contact/strength (`Contact.Depth`). Coalesced to the strongest hit per tick.
  First observation establishes a baseline, recovery never creates events, and publication suppresses events older than 30 ticks or ahead of the estimated server clock.
  Renderers must remember sequences by hull id and never replay the same sequence; a same-sequence stronger sample can refine an existing effect.
- Far clients retain all received profiles. The renderer culls detail by distance and projected size, independently of replication.
  Nearby dents and sparks are drawn from replicated profiles; `NeedsRepair` and the editor include dent-only damage.

Inline storage is 12 bytes per profile and 232 bytes per visual state on the tested runtime.
Contact records are 28 bytes: two 4,096-entry scratch arrays (229,376 bytes), plus 64 deferred 4,096-entry arrays (7,340,032 bytes) and 256 count bytes per field.
These are allocated when the field is provisioned, never during a contact. Transfer buffers reuse existing allocations; each replica reserves 256 inline visual states, and every hull view owns its copy (14,848 additional bytes for a 64-view snapshot, plus the same for the client's previous-view array).

## Pilot interval contract (protocol 33)

A snapshot at T contains state after world interval T; T also closes input acceptance through T.
Every eligible hull advances one HullSim.Dt per interval, regardless of packets or player count.

- Before the round, resolve control changes and orders effective in T, then editor lifecycle work.
- Capture participant handles, including generation. A drone created during the round waits until the next interval.
- Step hulls and collision candidates in authoritative slot order. Prediction retains that order for its known subset.
- Missing input means zero axes, boost off and dampeners on; the standing order, physics, jumps and cooldowns continue.

Input packets carry a server-issued control epoch and up to four complete records.
Each record carries a signed 64-bit target tick, diagnostic sequence, order id and full order definition.

- The server retains 32 target-tagged slots and accepts T through T+31 while preparing T.
- Older targets are discarded without retargeting; unseen future targets are accepted even after higher targets arrived.
- Validate the whole packet before mutation. Identical duplicates are harmless; conflicting targets mark the existing abuse budget.
- Receive limits remain unchanged. Packet arrival never advances simulation time.

A control change, ejection, destruction or editor replacement increments the epoch and clears input/order state.
Returning to an earlier hull still uses a new epoch. A usable authoritative pose resets client prediction.
The snapshot includes the epoch and applied order id.
Order ids use ushort serial comparison: forward differences below 32768 are newer, including rollover.
Repeated definitions do not restart completed orders or jumps.

The client samples at 60 Hz and retains 256 tick-tagged history slots.
Initial lead is ceil(smoothed RTT / 2 / Dt) + 3, clamped to 3–16 intervals.
These limits are fixed by this protocol, not independently configurable.
Lead increases leave explicit fallback gaps; decreases pause assignment while retaining current controls and order intent.
Transmitted targets never change.

Reconciliation restores authoritative T and replays every interval through the prediction horizon.
Assignment stops at the 32-slot window; silence for 256 client ticks requires a fresh usable pose.
Phase drift beyond eight intervals triggers bounded gap/wait correction.
Render-time estimation is separate from physics interval ticks; session clocks can be injected in tests.
Server late/fallback counters and client fallback/phase-reset counters are diagnostic state.

Unknown remote controls and omitted remote poses remain correction sources.
This contract does not promise exact prediction of another player's undisclosed intent.


### Damage during prediction lead

Protocol 33 snapshots include complete sparse health for each hull record: live damaged block ids and remaining HP.
Unlisted live blocks have intact HP; lost blocks still come through the existing revision/loss recovery contract.
Ids are sorted, unique and bounded by the design; values must be finite, positive and below intact HP.
A record carries at most 910 entries, derived from GeometryAdmission.MaxBlocks.

The client retains this health with the authoritative block state and copies it into a separate prediction store.
The same HullField/HullSim damage path may remove predicted blocks there.
Reconciliation restores the entire copy, including blocks, health, sums, collision branch counts and loss history.
Speculative losses never change published block sets, revision acknowledgements or the recovery replica.

A record without complete health uses aggregate-only prediction.
The server always supplies complete health; the flag also permits bounded parser/test fixtures without inventing block HP.

Health adds three fixed bytes plus six per damaged live block.
HullRecord.MaxWireSize is 5,707 bytes including visual metadata; actual sparse records are normally much smaller.
Snapshots remain at most 65,536 bytes and 255 hulls; the own hull is serialized first.
A full-health worst-case record allows at most eleven hulls in that packet.
Each connection resumes at its first unsent remote slot, draining overflow on intervening ticks as well.
A refused transport send does not advance that cursor; a new connection starts a fresh sweep.
With 50 ready hulls and successful delivery, a complete sweep takes five packets, plus at most two ticks
waiting for the next regular remote snapshot, below the 12-tick collision freshness window.
Larger fleets or packet loss can still exceed that window; this is not interest management or compression.
Client prediction therefore still cannot promise exact remote motion when poses or intent are missing.

Memory now includes independent prediction block states, not borrowed published blocks.
Each uses BlockStateStore.BytesFor(design, true), with pooled unmanaged storage reused during replay.
The fixed 256 held recovery poses also retain the inline sparse health payload (about 1.4 MiB).

## Dedicated saved worlds

Saved dedicated worlds are not joinable yet: every remote admission is refused until verified ownership exists.
This preserves state, but does not provide playable persistent multiplayer or portable owner identities.

- Without `--world`, the dedicated server remains unsaved.
- `--world <folder>` opens an existing world. A missing save is an error, never an implicit new game.
- `--world <folder> --create-world` creates only when no world exists. An existing compatible world is opened;
  conflicting seed/settings, damaged files and busy leases are refused, never overwritten.
- `--autosave <seconds>` requires `--world`: 1–3600 seconds, default 30, only while dirty. There is no manual-save option.
- The saved seed and settings are authoritative. Explicit `--seed`, file settings and `--set` must agree;
  omitted settings retain saved values. Normal setting parsing/clamping and last-assignment precedence apply.

### Startup and refusal

Launch seed/settings are validated against the decoded snapshot under the world's lease, before writable open
clears the clean-close mark. The same lease remains held through writable open and the store's lifetime.
A conflict releases the lease and leaves database, WAL and clean-close metadata unchanged.
Compatibility and structural storage checks also run before the launch validator and writable open.
The server owns the store after construction starts, including when construction fails.

### Stop and files

SIGTERM and Ctrl+C request a final save; the server waits up to 60 seconds for close, then disposes the store.
Store disposal can wait another 15 seconds. Give container shutdown at least 90 seconds.
Exit 0 means committed (or unsaved mode); checkpoint/backup degradation is separately reported on stderr.
Exit 1 means definite failure or startup failure; exit 3 means final commit is unknown or unfinished.
A clean-close mark alone does not prove checkpoint or backup success: read the shutdown diagnostics.
Use local filesystems with working locks. See [save-foundation.md](save-foundation.md) for the folder contract
and [releasing.md](releasing.md) for the nonroot container volume.

## Private saved-world account proof

An explicit `GameSession.RequestLocalClaim` uses a bounded in-process mailbox, not game transport messages.
`ServerConfig.AccountAuthority` configures the verifier service separately from advertisement, defaulting to `Listing`.
The client option `--account-authority` defaults to its listing configuration; `off` prevents account requests.
Only private saved local connections qualify. Remote connections cannot request a challenge or submit a proof.
The existing remote saved-world refusal is unchanged. No hosting unlock or player-facing claim UI is implemented.
Proof/authority/deadline rules are in docs/accounts.md; exact durable outcomes are in docs/save-foundation.md.

## Protected disk world identity foundation

Linux disk worlds now have an immutable P256 public key in database schema 2 and a separate `world.key`.
Snapshot codec 2 and its account/owner bindings are unchanged; older database schemas are refused without migration.
`SqliteWorldStore.Identity` exposes UUID, canonical 91-byte SPKI (base64), SHA256 fingerprint and key status.
Missing, malformed, mismatched or unsafe keys never regenerate; private gameplay remains loadable.
This status is an observation at open, not hosting authorization. Saved multiplayer remains locked.

- Key creation uses mode 0600 from the first byte, file flush and durable publication before database publication.
- Only Linux protection is supported. Windows and macOS creation are refused; copied worlds can open privately with status Unsafe.
- Automatic database copies are gameplay-only: they cannot recover a lost private key. Protected identity recovery artifacts are not implemented.
- Move or copy the complete stopped folder, preserving key permissions, to retain identity. A bare database is insufficient.
- Independent Claude security review remains required. No admission, TLS, key rotation or destructive recovery is included.

## Exploration foundation (protocol 37)

The server owns scans and charts and filters dynamic live state by personal sensor coverage.
`rules/exploration.json` is shared content and participates in content identity.
The provisional values are in `roadmap/exploration.md`; extra equipment gives redundancy only.
The pod has separate sensor and charting blocks; both starting fleet ships have a sensor.
Equipment has the plating mass and hit points it replaces, preserving stock flight figures and geometry.

### Simulation and save contract

- A scan runs on the controlled hull while flight continues. Sector/control change or loss of its last charting block cancels it.
- Completion records only that sector, its owner and authoritative survey tick. Repeat scans refresh that tick.
- Permanent charts are bounded by content per owner; new sectors at capacity are refused, never evicted.
- `SectorChart` describes immutable geography under the saved seed, generator version and content identity.
  `ExplorationView.ChartFields` reconstructs field centers, axes and envelope extents without mineral composition.
- Charts survive snapshot codec 3. Transient scans and ship markers start empty after loading.

A chart never records a live occupant or later mining/construction/destruction.
`ChartFields` requires a retained chart and a caller-owned span of `AsteroidFieldsModule.MaxFieldsInReach` entries.
Field envelopes can cross the surveyed sector's walls; this does not chart those other sectors.

### Client contract for the map worker

`PlayerInputBridge.RequestScan()` queues a one-shot intent. The server refuses missing equipment or chart capacity.
`SessionSnapshot.Exploration` owns its buffers; read only counted prefixes, as with hull views.

- `Scan`: state, sector, start and completion world ticks. Completed/refused/cancelled states remain until another request.
- `Charts[0..ChartCount]`: owner, sector and recorded world tick. Chart delivery is incremental; it never implies live coverage.
- `Contacts[0..ContactCount]`: hull, owner, design, last observed position/tick, live flag and loss tick.
- A lost ship marker freezes and expires after the content lifetime. It is not present in `SessionSnapshot.Hulls` or collision prediction.
- `JumpEligibility` reports existing drive/order eligibility, chart ownership and `BlindRiskImplemented=false`.
  It does not invent a maximum range or claim an uncharted jump is safe.

A station-class hull uses an indefinite stale marker; this repository has no generated station gameplay system.
The map worker still needs scan controls/feedback, authoritative chart styling, field markers, contact rows,
stale presentation and blind-risk-unfinished feedback. Existing map discovery is not this new chart API.
No screen, HUD or renderer implementation is included here.

### Visibility and disclosure

Every owned, equipped hull contributes a spherical volume in double-precision world coordinates, across XYZ walls.
Ownership always discloses the owner's own ships, including remote ones without sensors.
Detection uses object centers. Visibility grants no remote-control permission and never charts a sector.

- Hull snapshots require visibility; block/dent recovery checks it at request admission and before each continued transfer.
- Initial asteroid deltas, repeated sweeps and immediate changes require coverage of the asteroid's current center.
- Party live sector/health messages require visibility too; roster membership alone grants no coverage.
- A reliable visibility mask removes lost hulls independently of sparse snapshot scheduling. Old poses cannot revive them.
- Newly visible asteroid changes arrive on a bounded repeated sweep. Losing coverage sends no later mutations.

The galaxy seed, deterministic natural geometry, mineral generation and admission-time immutable design catalogue remain disclosed.
During play, new designs are announced only when needed by a visible hull or visible pending edit.
Catalogue indices are contiguous: revealing a later design also discloses preceding historical designs, not their live owners or locations.
Global player roster/count remain social information. The catalogue does not establish a hull's current existence or position.
This is dynamic-state filtering, not secure natural-data secrecy.
Collision prediction still generates baseline asteroids from the seed and can extrapolate already-disclosed asteroid motion.
No hidden hull is disclosed solely to improve prediction; unknown collisions and unseen later asteroid changes require correction.
Blind-jump risk, drive range tuning and galaxy vertical limits remain unfinished.

### Wire sizes and bounded work

Scan is 1 byte. Exploration is at most 799 bytes: scan state/times and up to 32 chart records of 24 bytes.
Visibility is 11 bytes plus one 64-bit word per group through the highest visible slot; maximum 8,203 bytes.
Invisible tail capacity is omitted. Requests use existing admission/rate budgets; the client reserves one scan request per tick.

- Sensor gathering walks live hulls and their block types. Coverage checks scan owned sensor centers; no spatial index yet.
- Visibility costs O(hulls × sensors × active clients), plus equipment checks. Each client retains an 8 KiB mask.
- One asteroid sweep visits at most 32 deltas per client/tick; immediate collision updates use at most two 32-record packets.
  At the saved-world limit of 16,384 deltas, a new observer's complete sweep takes 512 ticks without refused sends.
- Chart delivery visits at most the content chart limit per tick and sends at most 32 matching records. Refused sends retry.
- Contact storage is bounded to 65,536 records (the existing hull-slot ceiling), keyed by full hull identity so slot reuse preserves stale markers.
  At capacity new markers are refused and `ContactsComplete` stays false for the session; live hull replication continues. The existing 64 rendered/predicted hull-view limit remains;
  chart/contact publication is separate, and packet/sweep limits still delay observations of large fleets.

Measured on this Linux machine in Debug: 128 hulls and two clients averaged 1.6261 ms per complete server/client round,
with zero managed allocations across 120 measured rounds after warmup. This does not establish a thousands-of-hulls budget.
Release client benchmark passed at 1,097 fps normal and 999 fps during resize, zero render/process allocations.
Validation layers were inactive; Windows/macOS and interactive map behavior remain unverified.
