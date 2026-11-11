# Central accounts and friends

Phase A is an optional backend beside the listing service.
Client sign-in and friends UI integration are still pending.
Guest/local multiplayer, party and chat keep their existing session identities.
An ordinary guest roster name is not a verified account identity.
Recovery depends on Discord; there is no email/password recovery or account linking.

## Sign-in contract

Bodies are UTF-8 JSON with exact camelCase names; duplicate properties are rejected.
All account responses use Cache-Control: no-store; redirects also use Referrer-Policy: no-referrer.
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
No global presence, game tickets or game protocol changes are included.

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

PublicUrl is an HTTPS origin without userinfo, path, query or fragment.
HTTP is allowed only for literal loopback 127.0.0.1 or [::1] development origins.
The callback always appends /v1/accounts/discord/callback to that configured origin.
Incoming Host, scheme, forwarded host and return URLs never choose the callback.

Limits are validated at startup; invalid enabled configuration fails without printing secrets.
Disabled accounts neither open/create a database nor require OAuth credentials, and mapped endpoints return 503.
Proxy/rate gates still run first, so malformed forwarding or exhausted limits can return 400/429 instead.
Listing__AuthPerMinute=20 and Listing__AccountPerMinute=120 add separate address buckets.
All routes, including browser start and callback, carry limiter metadata.
All four address buckets must refill before a limiter slot can be recycled.

## Operations and storage

One service instance owns the SQLite volume.
WAL, foreign keys, one-second busy timeout and user_version=1 migration are used.
Only one DB operation is admitted at once; excess requests immediately receive 503 rather than queue.
Runtime DB errors return 503 while health and public listings continue.
Every DB operation and the ten-second sweeper delete at most 256 expired sessions and 256 expired requests.
Expiry checks apply even before rows are physically reclaimed. SQLite reuses freed pages.
Attempt sweeps cover started, aborted, failed and completed states with cost bounded by MaxAttempts.
Restart drops in-flight attempts; users restart sign-in.
Abandoned flows without a callback retain their slot until the ten-minute expiry.

Discord exchanges have immediate admission, one total timeout, 64-KiB response caps, no redirects or decompression.
There are no fire-and-forget exchanges or unbounded waiting queues.
Account rows and accepted friendships are durable; budget disk for the number of registered users.

Register the exact external callback https://prospect.mvl.sh/v1/accounts/discord/callback in Discord.
Supply client credentials via protected service configuration/environment, never command arguments or source control.
Keep Microsoft.AspNetCore at Warning. Do not enable HTTP body/header logging.
Configure proxy access logs to omit callback query strings, Authorization headers and redirect Location values.
Never log account tokens, handoff codes, Discord tokens, Discord IDs or credential values.

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
