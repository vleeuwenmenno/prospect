# Social interface contract

Protocol 16 provides reliable `MessageId.Social`. All access from the interface goes through the session snapshot
and input bridge; never read the server or send on a transport from the render thread.

## Read

`SessionSnapshot.Social` is a `SocialState` owned by that snapshot. Read only the counted portions of its arrays.
Text uses `SocialText`, an inline 256-character UTF-16 buffer; read `message.Text[..message.Length]` as a span.
Do not convert it to a string every frame. Snapshot copies allocate nothing after admission.

- `SocialMessage[] Roster`, `int RosterCount`: `Player` is the stable session id, `Text` the sanitized name,
  `Value` 1 for connected or 0 for departed. Capacity is server slots plus 32; old departures are replaced when
  full. The admission roster contains everyone currently connected, including yourself.
- `SocialMessage[] Chat`, `int ChatCount`: oldest first, at most 64. `Player` is the server-stamped sender,
  `Value` casts to `ChatChannel` (Everyone or Party), `Text` is the line, `Tick` is server simulation time.
- `SocialMessage[] Party`, `int PartyCount`, `uint Leader`: at most 16 storage slots (content currently allows
  eight). Each member has `Player`, design index `Value`, `Shield` and `Hull` in [0,1], sector `X/Y/Z`, and
  sample `Tick`. Resolve names through Roster and `SessionSnapshot.Content.Designs[Value].Name`.
  Every listed member is connected; disconnect removes the member and marks their roster entry disconnected.
- `SocialMessage Invitation`: `Player` is inviter, zero means none; `Tick` is expiry in server ticks.
  `ulong InvitationId` is the unique server-issued identity to retain with the displayed invitation.
- `SocialRefusal LastRefusal`: last refusal, retained until another refusal. Translate enum values in the UI.
  Rate-limited packets deliberately receive no reply. Bridge calls return false on local queue/text overflow.

Party batches become visible only after the complete batch has arrived. A member leaving a party of two clears
both parties. The creator leads; on leaving, the lowest remaining player id leads. Pending invitations from
the old leader are cancelled on membership changes that remove someone. A solo invitation creates no party
until accepted. On session end, roster entries are disconnected and party/invitation state clears.

## Send

Call `ClientSession.Input` (`PlayerInputBridge`). These methods enqueue bounded intent and return `bool`:

- `SendChat(ChatChannel channel, ReadOnlySpan<char> text)`.
- `Invite(uint player)`.
- `AcceptInvitation(ulong invitationId)` or `DeclineInvitation(ulong invitationId)`.
- `LeaveParty()`.
- `RemoveMember(uint player)`.

Accept/Decline must use the identity from the invitation displayed when the player chose the action, not a
newer invitation obtained while processing it. Tokens increase for the lifetime of the server and are never
reused, including same-player reissues in the same tick. A stale reply returns `StaleInvitation` without
clearing or accepting the replacement. The unsafe parameterless methods do not exist.

The bridge holds 16 pending operations. At most seven social messages (5,719 worst-case bytes) leave per client
tick, reserving 1,858 bytes for a maximum fleet message and five for Control, inside the server's 8-KiB and
32-message allowances. Unsent operations remain in FIFO order. A newer authoritative snapshot opens the next
allowance; catch-up ticks without one also leave control/fleet intents queued. This prevents repeated local
catch-up steps from draining again before the server advances; arbitrary network stalls may still bunch traffic
beyond the server allowance and disconnect. Server chat/party rate limiting is unchanged.

The client simulation consumes queued intent. The server enforces all ownership,
capacity, invitation and recipient rules. Only admitted clients on the reliable channel reach this handler,
after the existing per-player receive budget. If a transport cannot queue a reliable social message, the session closes rather than retaining silently stale state.
A content-driven token bucket runs before parsing, including for
malformed packets: four requests in a burst and one more per second, shared by chat and party requests.

## Wire and costs

One social envelope has 47 fixed bytes including message id, followed by a varuint UTF-8 byte count and text.
`SocialMessage.MaxWireSize` is 817 bytes. Text-free requests and events take 48 bytes. A maximum chat line takes
817 bytes. Roster text is bounded by the existing player-name admission rules (24 UTF-16 units, 48 input bytes).
The uniform envelope leaves unused fields zero; no request field can choose a chat sender.

Party status uses a 48-byte batch header and one 48-byte member record per member, reliably at 4 Hz. At eight
members this is 432 bytes per update, 1,728 bytes/second per recipient, excluding transport overhead. Membership
changes also trigger an immediate batch. Status comes from the currently controlled authoritative hull,
including drone ejection and destruction. No text is sent on periodic status updates.

Storage is allocated on construction and admission. No strings are created when decoding social traffic,
copying snapshots, cleaning chat or checking the rate limit. Rare party-event logs may allocate as existing
admission logs do. Chat logs contain only sender/channel/length at Debug; chat text is never logged.

## Validation

`SocialTests` exercises real server/client sessions with deterministic manual ticks, plus a threaded UDP pair.
It covers routing, sender stamping, roster lifecycle, invitations, expiry, refusal reasons, leadership,
dissolution, full-party acceptance races, hull switch/loss, bounded parsing and snapshot/tick allocation.
Ship destruction invokes the server's existing loss path directly; it does not simulate a destructive collision.

Follow-up regressions exercise a network-budgeted server with eleven maximum UTF-8 lines plus a full fleet
message and control, FIFO leftovers, catch-up, producer refills, stale Accept/Decline, and failed sends in both
directions. Routine `make run` launch output suppresses the entire argument list for `--say`, `--party-say`
and `--password`, including with `V=1`. This does not protect `make -n`, `make --trace` or process listings.
