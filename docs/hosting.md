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
A start that fails (port busy, a name or password that cannot be used) stays on the screen with the reason. In the
game, the `Esc` menu says whether a public game is listed right now.

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
- A jump order already received cancels the edit before replacement. Otherwise queued inputs are dropped.
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
