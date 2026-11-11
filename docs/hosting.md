# Hosting and joining

How a game is opened to other players, how a password works, and what the game does with the listing server.
The listing server's own contract is in [listing-server.md](listing-server.md).

- Code: `src/Prospect.Game/Server/ServerHost.cs` (visibility, handshake), `src/Prospect.Game/Protocol/Passwords.cs`,
  `src/Prospect.Game/Lobby` (announcer, server browser).
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

In the client: New Game, then "Who can join". Unlisted and Public show the name, password, player slots and port.
A start that fails (port busy, a name or password that cannot be used) stays on the screen with the reason. In the
game, the `Esc` menu says whether a public game is listed right now.

The same from the command line:

```sh
make run ARGS="--host unlisted --host-name 'Ceres Station'"                 # joined by address
make run ARGS="--host public --host-name 'Ceres Station' --host-players 8"  # also on the list
make run ARGS="--host public --password 'open sesame'"                      # with a password
make run ARGS="--join 203.0.113.7:27420 --password 'open sesame'"           # join one
```

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
    "listing": "https://mvl.sh"
  }
}
```

- An argument wins over `PROSPECT_PASSWORD`, which wins over the file.
- Prefer the environment variable or the file for the password: arguments show in process lists.
- A file with a password in it should be readable by the server's user only.
- A name is 1 to 48 characters after cleaning, the listing server's own rule.

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

What it does **not** give:

- **It protects the password, not the traffic.** Game packets are not encrypted or signed. Anyone on the path
  can read and change them.
- Someone who relays a live handshake between a player and the server gets in with it.
- A weak password can be guessed offline by anyone who overhears one handshake. Use a long one.
- The bytes typed are compared. The composed and the decomposed form of one accented letter are two
  different passwords.

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

The server browser (`ServerBrowser`, shown by Join Server):

- Fetches the list for the game's own protocol version, off the render thread.
- Trusts nothing: at most 200 entries, names cleaned, lengths checked again.
- An address must be a literal IPv4 address in its normal form. Host names, IPv6 and odd spellings are dropped.
- Private and loopback addresses are dropped unless the listing server itself is on this machine.
- Nothing is sent to a listed game until the player picks it.

The browser screen:

- A name is drawn as text only, clipped to its column with "...". It is never a widget label, an id or a format
  string; a row's id is its index in the list. `##`, `###` and `%` show as typed.
- A character the interface font lacks shows as `?`, the font's fallback glyph, with that glyph's width.
- The list is sorted once per fetch: games with room first, then the busiest, then by name.
- Nothing marks any game as official.
- With the listing server turned off, the screen says so and shows only direct connect.

## Known limits

- No NAT traversal and no reachability check.
- IPv4 only: BRUTAL's RakNet binding carries no IPv6 addresses.
- The announced port is the port the game listens on. A router that maps another outside port lists the wrong one.
- The list takes at most 255 players per server; a larger server reports 255.
- The lock-out counts by address, so players behind one address share it.
