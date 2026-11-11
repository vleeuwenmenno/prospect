# Hosting and joining

How a game is opened to other players, how a password works, and what the game does with the listing server.
The listing server's own contract is in [listing-server.md](listing-server.md).

- Code: `src/Prospect.Game/Server/ServerHost.cs` (visibility, handshake, limits),
  `src/Prospect.Game/Protocol/Passwords.cs`, `src/Prospect.Game/Server/ConnectionLimits.cs`,
  `src/Prospect.Game/Lobby` (announcer, server browser).
- No screens yet: hosting and the browser work from the command line and are ready for a screen to call.

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
    "listing": "https://mvl.sh",
    "limits": { "pendingPerAddress": 8, "pendingTotal": 32 }
  }
}
```

- An argument wins over `PROSPECT_PASSWORD`, which wins over the file.
- Prefer the environment variable or the file for the password: arguments show in process lists.
- A file with a password in it should be readable by the server's user only.
- A name is 1 to 48 characters after cleaning, the listing server's own rule.
- `limits` is optional; what it can hold is under [Connection limits](#connection-limits).

## Password

The password is never sent. It is not replayable.

1. The server makes a random 16-byte **salt** when it starts and derives its **key** once: PBKDF2-HMAC-SHA256 of
   the password (UTF-8) with that salt, 200,000 rounds, 32 bytes.
2. It answers every `Hello` with a `Challenge`: the salt and 32 fresh random bytes for that one connection.
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
5. One player gets 32 messages and 8 KiB read per tick. More is dropped unread; half a second of it removes the player.

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

- The default address is in `content/net/listing.json` (`https://mvl.sh`). `--listing <url>` replaces it,
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

The server browser (`ServerBrowser`):

- Fetches the list for the game's own protocol version, off the render thread.
- Trusts nothing: at most 200 entries, names cleaned, lengths checked again.
- An address must be a literal IPv4 address in its normal form. Host names, IPv6 and odd spellings are dropped.
- Private and loopback addresses are dropped unless the listing server itself is on this machine.
- Nothing is sent to a listed game until the player picks it.

## Known limits

- No NAT traversal and no reachability check.
- IPv4 only: BRUTAL's RakNet binding carries no IPv6 addresses.
- The announced port is the port the game listens on. A router that maps another outside port lists the wrong one.
- The list takes at most 255 players per server; a larger server reports 255.
- The lock-out and the connection limits count by address, so players behind one address share them.
- A flood from many addresses, or with forged addresses, can keep newcomers out; see
  [Connection limits](#connection-limits).
- Game traffic is not encrypted or signed.
