# Listing server

A small HTTP service that keeps the list of public games. Game servers announce themselves to it; clients ask it
for the list. Ours runs at `mvl.sh`, behind a reverse proxy that terminates TLS.

- Code: `src/Prospect.Listing` (ASP.NET Core, no database). Tests: `tests/Prospect.Listing.Tests`.
- The list lives in memory. After a restart it is whole again within 90 seconds, from heartbeats.
- Not connected to the game yet: the game server and the client will use the contract below.

## Contract, version 1

JSON in UTF-8. Every error is `{"error": "<one sentence>"}` with a 4xx status; it never repeats what was sent.

### Announce or refresh: `POST /v1/servers`

Body (`Content-Type: application/json`, at most 4 KB):

| Field | Rule |
| --- | --- |
| `name` | 1 to 48 characters after sanitising (below) |
| `port` | 1 to 65535: the UDP port players connect to |
| `players`, `maxPlayers` | 0 to 255, `players` not above `maxPlayers` |
| `protocol` | `ProtocolInfo.Version`, 0 to 4294967295 |
| `version` | 1 to 32 of `A-Z a-z 0-9 . _ + -` |
| `password` | `true` if joining needs a password |
| `token` | Only when refreshing: the token from the first answer |

- The address is the request's own address. There is no address field; one sent is ignored, as is any unknown field.
- Field names are exact (`maxPlayers`, not `MaxPlayers`). A duplicate field, a number in quotes or a fraction is a 400.
- Sanitising: control, formatting (bidirectional, zero-width), private-use and unassigned characters are removed;
  whitespace runs become one space; the ends are trimmed.

Answer: `{"id": "...", "token": "...", "ttlSeconds": 90}`.

| Status | Meaning |
| --- | --- |
| 201 | Listed. Keep `id` and `token`. |
| 200 | Refreshed: same address and port, right token. |
| 400 / 413 / 415 | Bad field or JSON / body over the cap / not `application/json` in UTF-8. |
| 400 | Behind a proxy: the proxy did not say which address the request came from (see below). |
| 403 | Wrong token for this address and port, or the address is not public. |
| 409 | This address and port are listed and no token was sent. |
| 429 | Rate limit, service busy, address limit or list full. `Retry-After` comes with the first two. |

Public means: IPv4 outside every block of the IANA IPv4 special-purpose registry (private, loopback, CGNAT,
documentation, benchmarking and the rest), or IPv6 global unicast (`2000::/3`) outside the special-purpose blocks in it.
NAT64, 6to4 and Teredo addresses are refused.

### Remove: `DELETE /v1/servers/{id}`

- Header `Authorization: Bearer <token>`. Works from any address.
- 204 removed, 401 no token, 403 wrong token, 404 no such entry (or expired), 429 rate limit.

### List: `GET /v1/servers?protocol=N`

- `protocol` is required, once. Only entries with that protocol come back. 429 on the rate limit.
- Answer: `{"servers": [{"name", "address", "port", "players", "maxPlayers", "password", "version"}]}`.
- Newest heartbeat first, at most 200. No ids, no tokens.
- `address` is plain text: IPv4 dotted (`203.0.113.7`) or IPv6 compressed (`2001:db8::7`), no brackets, no port.
- An IPv4 client that reached the service over IPv6 (`::ffff:203.0.113.7`) is shown and counted as plain IPv4.

### Health: `GET /healthz`

- 200 `ok` while the service runs. Not rate limited, and answered for the proxy itself.

### Requests that resolve to a trusted proxy

- Every `/v1` request whose address, after reading `X-Forwarded-For`, is still a trusted proxy is refused with 400.
- That happens when the header is missing, unreadable (an unmatched quote makes the whole value unreadable), or names
  only trusted proxies.
- It is refused before rate limiting, so a client cannot step into the proxy's bucket to escape its own limit.

## How the game side uses it

1. On start of a public game: `POST` without a token. Store `id` and `token`.
2. Every 30 seconds: `POST` again with the token. An entry not refreshed for 90 seconds is dropped.
3. On shutdown: `DELETE` with the token.
4. A 201 on a refresh means the old entry had expired: store the new `id` and `token`.
5. A 409 after a crash means the old entry is still there: keep retrying every 30 seconds; it expires within 90.

The client lists with its own `ProtocolInfo.Version` and joins `address`:`port` over UDP.

## For the game side

What the client must do when it shows this list. Anyone can list a server, so every entry is untrusted.

- **Connect only to the entry the player picked.** Never probe, ping or connect to every entry automatically: the list
  would turn every player into a traffic source aimed at whatever address someone listed.
- **`name` is untrusted text.** Up to 48 characters is up to 192 UTF-8 bytes. Size buffers for bytes, not characters.
- **Render `name` as text, never as a label or format string.** It may contain `##` and `###`, which Dear ImGui reads
  as an id separator in a widget label, and `%`. Draw it with a text call, and give the row its own id
  (`PushID(index)`, or a label of `##` plus the index).
- **Clip it** to the width of its column.
- **Never infer "official" from a name.** Any server can call itself "Official". The list says nothing about who runs
  a server.

## Configuration

Section `Listing` in `appsettings.json`, or environment variables `Listing__<Key>`. Bad values stop the service at
start with the key named.

Proxy and addresses:

| Key | Default | Meaning |
| --- | --- | --- |
| `TrustedProxies` | empty | Comma-separated addresses or networks (`172.16.0.0/12`) whose `X-Forwarded-For` is believed. Empty: none. Never a client: a request that resolves to one is refused. |
| `AllowPrivateAddresses` | `false` | Allow loopback, private, link-local and similar addresses to announce. `make listing` turns it on. |
| `Ipv6PrefixLength` | `64` | IPv6 addresses in one prefix of this length count as one address for the limits. |

Entries:

| Key | Default | Meaning |
| --- | --- | --- |
| `EntryTtlSeconds` | `90` | An entry without a heartbeat for this long is dropped. |
| `SweepIntervalMilliseconds` | `5000` | How often expired entries are swept, with or without requests. |
| `MaxEntries` | `1000` | Entries in the whole list. |
| `MaxEntriesPerAddress` | `4` | Entries one address (or IPv6 prefix) may hold. |
| `MaxListResults` | `200` | Entries one list answer carries. |

Requests:

| Key | Default | Meaning |
| --- | --- | --- |
| `PostsPerMinute` | `20` | Announces, refreshes and removals per address per minute. Bursts up to this, then exactly this per minute. |
| `ListsPerMinute` | `60` | List requests per address per minute, the same way. |
| `GlobalRequestsPerSecond` | `200` | `/v1` requests per second for the whole service. Bursts up to this. Only requests an address's own limit lets through use it. |
| `MaxBodyBytes` | `4096` | Largest request body. |
| `MaxHeaderBytes` | `8192` | Largest request line, and separately largest total of the headers: two limits, not one shared. |
| `MaxConnections`, `RequestHeadersTimeoutSeconds` | `1000`, `10` | Open connections; seconds to send headers. |

Rate-limit memory:

| Key | Default | Meaning |
| --- | --- | --- |
| `RateLimitAddresses` | `16384` | Addresses (or IPv6 prefixes) with rate-limit state of their own. Reserved at start; never grows. |
| `RateLimitOverflowBuckets` | `64` | Shared buckets for new addresses while every slot is taken. |

- A slot is freed only once its buckets have refilled completely, so freeing it lets no one escape a limit.
- An address that holds a slot keeps its own limit however many new addresses arrive.
- At most `GlobalRequestsPerSecond` × 60 / `PostsPerMinute` slots can be kept busy (600 by default), so the default
  number of slots is not reached by requests the service accepts.

`ASPNETCORE_HTTP_PORTS` (image default `8080`) or `ASPNETCORE_URLS` sets where it listens.

## Running

- Locally: `make listing` (127.0.0.1:8080, private addresses allowed; `LISTING_PORT=9000` to move it).
- Image: `make listing-image`, or `docker build -f docker/listing.Dockerfile -t prospect-listing .` from the root.
- Container: `docker run -d -p 127.0.0.1:8080:8080 -e Listing__TrustedProxies=<proxy address> prospect-listing`.
- The image runs as the unprivileged `app` user, listens on 8080 and checks `/healthz` with its own binary.
- Arch Linux needs `aspnet-runtime` and `aspnet-targeting-pack` to build and test it; `make setup` installs them.

## Deploying behind a proxy

1. Run the container where only the proxy can reach port 8080 (publish it on `127.0.0.1`, or a private network).
   Nothing else may reach that port.
2. Set `Listing__TrustedProxies` to the address the proxy connects from, as the container sees it. Check the start-up
   log line, which prints the trusted proxies.
3. Make the proxy **replace** `X-Forwarded-For` with the address it saw, not append to what the client sent.
   Caddy: `header_up X-Forwarded-For {remote_host}`. nginx: `proxy_set_header X-Forwarded-For $remote_addr;` (not
   `$proxy_add_x_forwarded_for`).
4. Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED` (or `ForwardedHeaders_Enabled`) or
   `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`. The first makes the framework believe `X-Forwarded-For` from anyone,
   bypassing `TrustedProxies`; the service refuses to start with either set, naming the key.
5. Check: announce from outside and see your public address in the list, not the proxy's.

Getting step 2 wrong is the one dangerous mistake:

- Too narrow: every server is listed at the proxy's address and all share one set of limits.
- Too wide: anyone who can reach port 8080 directly can claim any address. Never trust a public range.
- The service walks `X-Forwarded-For` from the right, only through trusted proxies, so entries a client writes
  into the header itself are never reached. A proxy that replaces the header leaves a client nothing to write.

## Known limits

- **Reachability is not checked.** A listed server may be behind a firewall or NAT and unreachable from outside.
- **The list is lost on restart** and rebuilt from heartbeats within 90 seconds.
- **One instance only.** The list is in memory; two instances behind one proxy would each hold half.
- **The service-wide limit can be used up.** Many addresses together can spend `GlobalRequestsPerSecond` and delay
  everyone's heartbeats; entries survive 90 seconds of that. Blocking such a flood belongs in front of the service.
- **An address owns its ports.** Anyone at a server's address (the same NAT) could list a port the server is not
  using, and can replace its entry once it expires.
