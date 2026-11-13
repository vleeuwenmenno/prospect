# Central accounts and friends

Phase A is an optional backend beside the listing service, and the game's sign-in and friends screens.
Private local play needs no account. The official client asks for a Discord sign-in before it joins a server
or hosts an unlisted or public game. Game servers do not see or check the account: party, chat and the roster
keep their session identities, and a roster name is not a verified account identity.
Recovery depends on Discord; there is no email/password recovery or account linking.
The service can also issue game tickets (below). The game does not use them yet.

## Sign-in contract

Bodies are UTF-8 JSON with exact camelCase names; duplicate properties are rejected.
Every answer from an account or ticket route carries Cache-Control: no-store and Referrer-Policy: no-referrer:
successes, refusals (proxy, rate limit, validation, 401, 404, 503) and the fixed 500.
Not covered: a request that matches no route. A wrong method (405), an unknown path (404) and what Kestrel
refuses before routing (a malformed request, oversized headers) carry neither header.
None of those reads a token, a ticket, a verifier or a body.
Errors contain fixed safe text. No endpoint polls for completion, codes or sessions.

1. The native client binds a listener on literal 127.0.0.1, using an ephemeral port from 1024 through 65535.
   Generate 32 random bytes, encode canonical unpadded base64url as verifier (43 ASCII characters).
   The challenge is unpadded base64url of SHA256 of that **verifier's ASCII text**, per RFC 7636 S256.
2. POST /v1/accounts/signin with {challenge,port} returns 201 {attempt,expiresIn:600}.
   Locally construct the browser URL from the known service base and /v1/accounts/signin/{attempt}.
   Do not shell-open an arbitrary URL received from the service.
3. GET /v1/accounts/signin/{attempt} starts once and returns 302 to Discord.
   It requests only identify, authorization code, consent, random state and independent provider PKCE.
   Missing, used or expired attempts return 400.
4. Discord returns to /v1/accounts/discord/callback?code=...&state=....
   State is consumed before outbound calls, without binding the callback address to the native address.
   Optional iss must equal https://discord.com. After valid state is consumed, cancellation returns to the stored
   loopback address with attempt and error=access_denied; other failures return attempt and error=failed.
   Both release the attempt slot immediately. Invalid, expired or replayed state never redirects.
   Success redirects only to http://127.0.0.1:{storedPort}/prospect/signin?attempt=...&code=....
5. Verify the returned attempt locally. POST /v1/accounts/sessions with {attempt,code,verifier}
   returns 201 {token,expiresIn:86400,account:{id,name,friendCode}}.
   The random handoff code is stored hashed and expires with the 10-minute attempt.
   Wrong code/verifier leaves a valid handoff usable; concurrent redemption succeeds once.

The backend uses fixed Discord token, authorization-info and user endpoints.
Token exchange uses HTTP Basic client authentication and provider PKCE.
Authorization info must name the configured client and exactly the identify scope.
No cookies, data-protection keys or signing keys are required.
State, provider PKCE and native attempt binding remain essential despite having no cookies.
Discord tokens are never persisted or returned to the client.

## Account contract

- GET /v1/accounts/me returns {id,name,friendCode}.
- DELETE /v1/accounts/sessions/current returns 204 and revokes the current session.
  Repeating with the revoked token returns 401.
- Protected routes accept only Authorization: Bearer; not query or body tokens.
  Tokens are 32 random bytes encoded base64url, stored only as SHA256 hashes.
- Sessions expire absolutely after 24 hours, survive service restart, and are capped at ten per account.
  Issuing an eleventh transactionally evicts the oldest (insertion order breaks timestamp ties).
- Account IDs are random 32-character lower-case hex, unrelated to Discord IDs.
  Names use cleaned Discord global_name, capped at 24 UTF-16 units without cutting a rune.
  Empty names become Pilot-XXXX; username handles and Discord IDs never appear in API responses.
  Friend codes are exact, case-sensitive, 16-character upper-case random hex.

## Friends contract

GET /v1/friends returns {friends:[],incoming:[],outgoing:[]}.
Each entry is {id,name,since}: counterpart **account ID**, cleaned name, UTC ISO 8601 timestamp
with explicit +00:00 offset. Accepted timestamps record acceptance; pending timestamps record request time.
Other accounts' friend codes are omitted.

| Route | Result |
| --- | --- |
| POST /v1/friends/requests {friendCode} | 201 new pending; 200 existing outgoing or reverse pending autoaccepted |
| POST /v1/friends/requests/{id}/accept | 200 accepted or already accepted; 404 absent or wrong recipient |
| POST /v1/friends/requests/{id}/decline | 204 declined or absent; 404 wrong recipient or already accepted |
| DELETE /v1/friends/requests/{id} | 204 own outgoing cancelled or absent; 404 incoming or accepted |
| DELETE /v1/friends/{id} | 204 accepted relationship removed or absent; 404 pending |

Request creation returns 404 for an unknown code, 409 for self/already friends, 429 for either party's cap.
Decline/cancel/remove retries are harmless, but cannot delete a different relationship state.
Concurrent DB operations can return 503; retry after a short delay.
There are at most 200 friends, 50 outgoing pending and 200 incoming pending per account by default.
Pending requests expire after 30 days; accepted friendships remain until removed.
Canonical unordered pairs use ordinal lower-case hex order and SQLite BINARY collation.
Unique constraints and immediate transactions prevent duplicate/crossed relationships and cap races.
No global presence or game protocol changes are included.

## Game tickets (service only)

Groundwork for telling a game server which account a player has. Only the service side exists.
The explicit private saved-world claim API issues and redeems tickets. Network game names remain unverified; remote saved-world admission is not implemented.

What a ticket proves, and what it does not:

- **Proves:** whoever redeems it holds the verifier behind the challenge an authenticated session supplied.
- **Does not prove:** which game server is redeeming, or that the player agreed to anything.
- The challenge is opaque to the service. Binding it to a server, a purpose or a handshake is the game's job.
- No session keys or other secrets are derived or returned.

Issue: `POST /v1/game/tickets`, `Authorization: Bearer <session token>`, body `{challenge}`.

- `challenge` is unpadded base64url of SHA256 of the verifier's ASCII text, as in sign-in.
- 201 `{ticket,expiresIn}`. `ticket` is 32 random bytes in base64url.
- `expiresIn` is 30 seconds, or what is left of the session when that is less.

Redeem: `POST /v1/game/tickets/redeem`, no bearer token, body `{ticket,verifier}`.

- 200 `{account:{id,name}}`. Nothing else: no friend code, no token.
- A ticket is consumed at most once. A lost answer is not repeated: ask for a new ticket.

Both bodies are strict: exactly the named properties, each a string, each canonical (43 characters).
A missing, extra, repeated or wrongly cased property is a 400.

| Status | Issue | Redeem |
| --- | --- | --- |
| 400 | Bad body or challenge | Bad body, ticket or verifier |
| 401 | No single Bearer header, or no live session | never |
| 404 | never | Unknown, used or expired ticket; wrong verifier; session ended |
| 413 / 415 | Body over the cap / not JSON | the same |
| 429 | Address rate limit (Retry-After), or a ticket cap | Address rate limit (Retry-After) |
| 503 | Database busy or failing; accounts disabled | the same, or the ticket is being redeemed right now |

Rules:

- **Wrong verifier changes nothing.** It is the same 404 as an unknown ticket, and the ticket stays usable.
- **The session is asked again at redemption.** It must exist, be unexpired and belong to the same account.
  Logout, eviction or expiry before redemption gives 404 and drops the ticket.
- **Logout after consumption changes nothing**, even if the 200 has not arrived yet.
- **503 means this request did not consume the ticket.** Another in-flight redemption may consume it;
  bounded retries may return 404. Never restore a ticket after response loss.
- **A redemption that began is finished** even if the caller disconnects.

Limits:

- A ticket lives 30 seconds on a monotonic clock, counted from when it is issued, not from the request.
- The session's own expiry still ends it sooner. A wait for the database never extends either.
- At most 10,000 tickets in all and 4 per account. A ticket being redeemed counts until it is settled.
- 30 issues and 240 redemptions per address per minute. Every request spends allowance, refused ones too.
- Tickets live in memory, keyed by hash. A restart drops them all; clients ask again.

Decided (game side, not built):

- A server reached by a bare address is accepted by the player explicitly on first contact, and then pinned.
- A changed pin needs the player's explicit trust again.

Open (game side, not built):

- The remote handshake that carries a ticket and binds it to an accepted server identity.

## Game client

`AccountClient` (`Prospect.Game/Lobby`) is the game's side; the interface only reads its `View`.

- **Where:** Friends in the main menu, and the Friends tab of the in-game Social panel. Both draw the same parts.
- **One per game.** `App` owns it, so joining and leaving games keeps the sign-in.
- **Service:** the configured listing address (`content/net/listing.json`, `--listing`). `off` means no accounts.
- **Token:** in memory only. Never written to disk, logged, shown, or sent to a game server.

Sign-in, in order:

1. The player presses Sign in with Discord. Nothing signs in by itself.
2. `LoopbackReceiver` binds 127.0.0.1 on an ephemeral port before the attempt is created.
3. The browser opens `{listing}/v1/accounts/signin/{attempt}`, built locally from a checked attempt.
   `BrowserLauncher` uses the system opener (`xdg-open`, `open`, ShellExecute) with one argument.
4. The receiver answers one valid `GET /prospect/signin`, then the port closes. It then redeems the code.

The receiver:

- Checks method, path, `Host: 127.0.0.1:{port}`, each query field once, the attempt, and a canonical code or a
  known error. Anything else gets 400 and changes nothing.
- Reads at most 4 KiB of head, gives a connection 3 s, serves 4 connections at once and closes more unread.
- Returns a fixed page: no-store, no-referrer, a CSP with no external sources, nothing from the request.
- Closes on success, on Cancel, after the attempt's 10 minutes, and when the game closes.
  An abandoned attempt keeps its service slot until it expires.

Requests:

- One operation at a time; a second is refused, never queued.
- 503 is retried at most three times, 0.25 to 5 s apart (Retry-After when given). Nothing else is retried.
- 401 signs out. 429 and 503 show a notice and keep the sign-in.
- After any change the lists are read again, so they show what the service decided.
- Sign out, Cancel and closing advance a generation; an answer from an older one changes nothing.
- Sign out forgets the token at once, then asks the service to end the session.
- Replies are strict: duplicate names, wrong types, bad ids or times, or over 400 entries a list refuse the
  whole reply. Names are cleaned of control and format characters and cut to 24 UTF-16 units.

Interface:

- Notices are fixed sentences; service text is never shown.
- Names go only to draw calls. Remove asks for a second press.
- The code field takes hex only. Enter sends from the field; the button takes the pointer only.
- Captures: `--account-sample signed-out|waiting|signed-in|friends|failed|off`, with `--capture` or `--bench`
  only. `--start account` opens the screen; `--open friends` opens the tab.

Network play (`NetworkPlay` in `Prospect.Client`):

- Join Server (direct, listed, the password retry, `--join`) and Start for an unlisted or public game need
  `AccountClient.HasCurrentSession()`: a token the service issued, within its `expiresIn`, measured on a
  monotonic clock from before the session was asked for. A sample never counts. Private never needs it.
- The check runs before the running session is left or a socket opens; a refusal changes nothing.
- The sign-in is offered on the screen that needs it, so the address, the picked game and the hosting choices
  stay. Signing in does not join or start anything: the player presses Join or Start again.
- `--join` without a sign-in opens Join Server filled in. `--listing off` says accounts are off; private works.
- Entry only: signing out or expiring never ends a running game or a hosted server. The service can end a
  session sooner; the client learns that from its next 401.

Not in the game yet: online presence, same-server friend markers, adding from the player list, account identity
on remote game servers. The private local claim API uses tickets; no player-facing claim UI exists.
The game does not revoke the session when it quits; the session expires at the service after 24 hours.

## Configuration

Environment variables use Accounts__<Key>; configuration files use the Accounts section.

| Key | Default |
| --- | --- |
| Enabled | false |
| PublicUrl | https://prospect.mvl.sh |
| ClientId, ClientSecret | empty; required only when enabled |
| DatabasePath | /data/accounts.db |
| MaxAttempts, AttemptsPerAddress | 1000, 5 |
| DiscordConcurrency, DiscordTimeoutSeconds | 8, 15 |
| MaxFriends, MaxOutgoing, MaxIncoming | 200, 50, 200 |
| PendingDays | 30 |
| TicketSeconds | 30 (1 to 300) |
| MaxTickets, TicketsPerAccount | 10000, 4 |

PublicUrl is an HTTPS origin without userinfo, path, query or fragment.
HTTP is allowed only for literal loopback 127.0.0.1 or [::1] development origins.
The callback always appends /v1/accounts/discord/callback to that configured origin.
Incoming Host, scheme, forwarded host and return URLs never choose the callback.

Limits are validated at startup; invalid enabled configuration fails without printing secrets.
Disabled accounts neither open/create a database nor require OAuth credentials, and mapped endpoints return 503.
Proxy/rate gates still run first, so malformed forwarding or exhausted limits can return 400/429 instead.
Listing__AuthPerMinute=20 and Listing__AccountPerMinute=120 add separate address buckets.
Listing__TicketIssuesPerMinute=30 and Listing__TicketRedeemsPerMinute=240 add two more, for game tickets.
All routes, including browser start and callback, carry limiter metadata.
All six address buckets must refill before a limiter slot can be recycled.
An address moved out of a shared overflow bucket inherits all six, so the move grants no fresh burst.

## Operations and storage

One service instance owns the SQLite volume.
WAL, foreign keys, one-second busy timeout and user_version=1 migration are used.
Only one DB operation is admitted at once; excess requests immediately receive 503 rather than queue.
Runtime DB errors return 503 while health and public listings continue.
Every DB operation and the ten-second sweeper delete at most 256 expired sessions and 256 expired requests.
Expiry checks apply even before rows are physically reclaimed. SQLite reuses freed pages.
Attempt sweeps cover started, aborted, failed and completed states with cost bounded by MaxAttempts.
Restart drops in-flight attempts; users restart sign-in.
Game tickets take no database rows: redemption only reads the session, inside the same one-at-a-time slot.
Expired tickets are dropped by every issue and redemption, and every Listing__SweepIntervalMilliseconds without one.
Abandoned flows without a callback retain their slot until the ten-minute expiry.

Discord exchanges have immediate admission, one total timeout, 64-KiB response caps, no redirects or decompression.
There are no fire-and-forget exchanges or unbounded waiting queues.
Account rows and accepted friendships are durable; budget disk for the number of registered users.

Register the exact external callback https://prospect.mvl.sh/v1/accounts/discord/callback in Discord.
Supply client credentials via protected service configuration/environment, never command arguments or source control.
Keep Microsoft.AspNetCore at Warning. Do not enable HTTP body/header logging.
Configure proxy access logs to omit callback query strings, Authorization headers and redirect Location values.
Never log account tokens, handoff codes, game tickets, verifiers, Discord tokens, Discord IDs or credential values.

Mount durable storage at /data; the image creates it owned by APP_UID (the runtime's unprivileged app user).
For a bind mount, give that user write access to the directory, database, WAL and SHM files.
Use SQLite online backup, or stop the service before copying the entire volume; copying only a live DB file loses WAL writes.
Protect backups as account data. Restore to the same single-instance setup and verify the account API after restart.
No SMTP service, bot, additional scopes or signing-key infrastructure is needed.

The image publishes SQLite native assets for Linux amd64 and arm64.
Microsoft.Data.Sqlite 10.0.0 is centrally pinned; SQLitePCLRaw.bundle_e_sqlite3 3.0.5 overrides its old vulnerable native bundle.
Package references: [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.0),
[SQLitePCLRaw](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/3.0.5).
Provider contract: [Discord OAuth2](https://docs.discord.com/developers/topics/oauth2).

## Private local claim API

`GameSession.RequestLocalClaim(AccountClient)` records explicit intent; `LocalClaim` is a thread-safe enum result.
Only a private saved world with an active in-process local connection accepts it.
No menu, load, reconnect or ordinary tick requests a ticket automatically.

- The server worker generates a fresh 256-bit verifier and S256 challenge for purpose `local-claim`.
- The client matches its configured authority and captured account generation before issuing a ticket through its in-memory bearer.
- The server redeems through its own configured authority, without a bearer. Only the service's verified UUID can enter the claim save path.
- The tick rechecks connection generation, the 30-second deadline, authority, private state and closing before staging the candidate.
- One slot stays occupied until the worker ends, including cancellation. Proof success is not claim success: only exact `Committed` or `AlreadyLinked` means linked.

`--account-authority <url|off>` configures accounts independently of advertisement; omitted, it follows `--listing`.
Thus `--listing off` leaves accounts off unless explicitly configured. Configuration alone makes no request.
Claims require a canonical HTTPS origin accepted by `SavedAccount`, with no path or bracketed IP literal.
Existing listing URLs with a path or loopback HTTP still work for their existing purposes but cannot claim a saved identity.
No bearer is sent to a server-chosen URL; the two configured authorities must match exactly.

503 retries are bounded to three attempts, delays clamped to 250 ms–5 s, within a 30-second whole-proof limit.
A timeout or lost answer is terminal for that proof. A later explicit request creates a fresh verifier and ticket.
No credentials or proof material enter saves, game transport messages or logs.
The mailbox is in-process only; it adds no wire message or protocol version change.
A linked world's other authority is refused, never merged or replaced. Local offline ownership remains available.
