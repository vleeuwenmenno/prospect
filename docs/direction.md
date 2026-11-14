# Game direction

"Decided" is settled. "Open" is not: do not build on an open item, ask.

The [persistence and exploration roadmap](roadmap/README.md) records the agreed next work in detail.
Its product decisions are settled; technical proposals and remaining questions are labelled separately.

## The game in one line

A multiplayer space sandbox: build ships from blocks, mine, and command a fleet across one continuous galaxy that
AI factions are fighting over.

Reference for feel: Avorion (camera scheme, sector map, ship data sheet). A reference, not a clone target.

## Decided: the game

- **PvE first.** The galaxy and its factions are the opponent. PvP is supported as a server setting.
- **One space, no loading.** Sectors are an index, not rooms. No walls, no loading screens, jumps included.
- **Ships are their blocks.** Mass, thrust, turning, power, shield and hull all come from the blocks. Damage is
  per block; a lost block is lost.
- **Two views of the same space.** Pilot view to fly one ship; command (RTS) view to select and order many.
- **Shields break.** A shield emptied by a hit goes offline, takes no hits, and only comes back after a delay,
  charging to a threshold on extra power. No instant reactivation from one point of charge.
- **Shields are smooth and shiver as they recover.** The shell is the blocks grown round by the margin, with gaps of
  about a block bridged: it follows the hull's outline and its slopes, never a box or a hull-sized bubble.
  Rounding stays whatever blocks are resized, merged or turned into wedges: the shell depends only on the shape
  the blocks fill, so one box and the same box built of cubes get the same shield. There is no square fallback.
  A damaged hull gets its own shell, built off the frame; until it is ready, or when it would not fit, the hull
  keeps the shell it had or its intact one, which enclose what is left of it. Heavy damage can therefore keep
  the original outline. A design whose intact shell does not fit is refused before Apply. Coming back online, and while the charge keeps rising, it shivers
  gently (a slow, faint motion, not bubbling) and settles once charging stops. An offline shield stays invisible until it
  restarts; the console marks its restart point and the charge towards it.
- **Shield contacts stay local and visible.** A brief bright patch and paired rings mark the contact surface.
  Below 80% charge the shell gradually warms through amber, already visible at half charge, then red near empty.
- **The pilot's shield rides through their own hyperspace.** Its glow and ripples stay all round the hull in the
  tunnel. Its bend of the old world fades with that world; at full cover only its light is left.
- **You are not your ship.** You can leave a ship in a drone and take control of any ship you own.
- **Taking control reaches one sector.** Only a ship in the same sector as the hull you fly now (a drone
  included), on every axis x, y and z. To take control further away, travel or jump into that sector first.
  Leaving in a drone has no range. The server checks it; the client checks the same rule to say why it did not
  ask. Fleet orders and autopilot have no such range.
- **Planned: remote control and relays.** A separate remote-control range, player-built relay stations that
  extend it, and map coverage visualization. Detailed range and relay rules remain open. None exists yet;
  current fleet orders remain unrestricted.

## Decided: first flight and shield balance pass

- Standard and drone thrust is 20% lower. Boost gives 2× thrust and 2× rated speed, still drawing 4× thrust power.
- Shield recharge is halved: 37.5 points/s per standard emitter, 5 points/s per drone emitter.
- Every damaging hit resets an 8 s recharge delay, including hits while the shield is offline.
  Harmless contact feedback does not delay recovery. Restart remains at 25% charge; its energy premium is defined below.
- These are starting values, not final flight-feel approval. Capacity, mass, gyro, control rules and FTL stay unchanged.
  Power shortage can still slow recovery; the world shield-recharge multiplier still applies.

## Decided (provisional, tunable): shield recharge energy

- Recharge spends the shared ship energy pool for each point restored. Normal MJ/point is surviving emitters' summed
  `powerDraw` divided by their summed base `shieldRecharge`, both evaluated with block size rules.
  Standard and drone emitters currently cost 0.08 MJ/point: 3 MW / 37.5 points/s and 0.4 MW / 5 points/s
  (doubled from 0.04 so recovery is felt on the shipped designs; see the next section).
- Collapsed shields retain the existing offline state and 8 s damage delay. Recovery to 25% capacity costs 4.5× normal
  MJ/point (`shieldRestartPowerMultiplier` in `content/blocks/blocks.json`). One standard emitter restarts for 135 MJ
  (13.5 MW for 10 s); one drone emitter for 10.8 MJ (1.8 MW for 6 s). No separate restart fee or new phase state.
- The last offline tick stops exactly at the threshold; normal recharge resumes next tick. Partial top-ups pay only
  for points restored. Insufficient energy restores fewer points; zero available energy waits. Energy never goes negative.
- Priority per shared step: FTL takes stored energy first under its existing charge gate, then generator output and
  remaining battery feed idle/propulsion (including boost), then shields, then battery storage. FTL does not reserve next
  tick's energy; shields may consume this tick's generator surplus. Transit still suspends flight and recharge.
- `FlightRules.Under` applies the world's shield-recharge multiplier to both recharge rate and rated shield power,
  including restart power. MJ/point and total recovery cost stay fixed. Generators, batteries and surviving emitters
  still determine available power, storage, capacity and recharge. Existing energy and net-flow displays include this draw.

Open: flight feel and balance approval; these numbers remain content tuning, not final balance.

## Decided (provisional, tunable): shield power must be felt and seen

Why: with 1.5 MW recharge and 6 MW restart against 6 to 12 MW of generation the battery never moved, so the
energy readout showed nothing while a shield recovered.

- Retune: shield emitter `powerDraw` 1.5 → 3.0 MW, drone shield 0.2 → 0.4 MW, `shieldRestartPowerMultiplier`
  4 → 4.5. Generators, batteries, recharge rates, delay and thresholds are unchanged. Content identity changes
  with `blocks.json`; no evaluator arithmetic changed, so `ContentIdentity.EvaluatorVersion` stays.
- Result at rest, battery full, 8 s delay not counted (command pod 6 MW generator; mining and survey vessels
  12 MW): ordinary recharge takes 57% of the pod's spare power and 28% of a vessel's. A restart draws 13.5 MW,
  more than the spare power of every shipped design, so it drains the battery: the pod's to empty, a vessel's by
  about 45%. Restart still takes about 10 s when the battery can carry it.
- Propulsion and boost keep priority over shields, so flying is never slowed by a restart. The price is that
  shields recover slowly while the engines run: the pod under full forward thrust needs about a minute to restart.
- The readout shows live flow: generation less idle, thrust or boost, shield and jump-drive draw, in MW, with an
  arrow, even while a full battery turns the surplus away. The tooltip lists each draw and the battery's own rate.
  A muted blue mark on the energy strip is the shield's share; an amber one, continuing from it, is a running scan's.
  The tooltip has a MODULES row while a scan draws. Figures live in `content/visuals/power.json`.
- Scan modules follow the same rule: Common Mk I draws 6, 9 and 12 MW (was 0.5, 0.75, 1) so a scan turns the pod's net flow
  negative. `moduleTiers` and `ContentIdentity.EvaluatorVersion` are unchanged. See `docs/roadmap/modules.md`.

Open: whether the pod's thrust-while-restarting slowness is too harsh; whether vessels need a second battery.

## Decided: the world

- **AI factions are real actors.** Each runs its own economy, stations and ships, sets up supply lines, fights
  wars, and gains and loses territory. Players live inside that, they do not trigger it.
- **Players do what factions do.** They build ships, found and build stations, claim stations and destroy them.
- **Players form factions and alliances** with each other. A player faction and an AI faction are the same kind
  of thing: everything in space is owned by a faction.
- **A lost ship is gone.** It is rebuilt at a shipyard from its blueprint, for resources and time.
- **The world advances only while someone is connected.** With nobody online it pauses.
- **Travel.** Sublight inside a sector, faster-than-light between them. Three drive kinds (hyperspace, wormhole,
  rift). Transit takes base time plus straight-line distance divided by content speed: initially 3 s plus 1 s per 40 km.
  Preparation and arrival stay unchanged; there is no distance energy cost.
  Server-enforced minimum and drive maximum distances apply, with a safe band and nonfatal blind risk beyond it; destination charts remove
  blind-jump risk. See [exploration](roadmap/exploration.md) for the agreed rules and open tuning.
  Durations beyond 2^40 seconds are rejected to keep elapsed time precise; this is a numerical bound, not drive range.
- **Jump looks.** Physical, not an overlay: effects sit in the world around the hull, laid out around its nose,
  never the view. No cuts, no full-screen flash; each stage eases into the next. Only hyperspace fills the view
  (Star Wars lines and tunnel), lit from ahead and dark behind the vessel, by the jump's axis, not the view; it
  arrives with a pulse that reaches further and brighter for bigger, heavier hulls, within a bound. Wormhole: a sphere with gravitational lensing, subtle colour. Rift: an orange,
  crisp, ragged window with lensing. With both, the hull visibly flies in through one opening and out of another.
- **The pilot starts their own jump.** The map only plots it: a NAV marker in the pilot view, aim at it, boost to
  start. The hull then brakes and holds its nose on it while the drive calculates and charges.
- **Only halt cancels a started jump.** Boost (press, hold, tap, release) and thrust never do; the shared step
  ignores the pilot's input while the drive works and `HullSim.InputEndsOrder` never ends a jump order. Halt
  (`H`, `Backspace`, `B` on a pad) works until the drive fires; in the tunnel it cannot bring the hull back.
- **Forward and boost together also start it.** Either order, from the bindings. Forward held from that start is
  not sent until forward and boost are both let go, so it does not fly the hull out of its arrival.
  Ships flown by the server still jump straight from an order.
- **The jump has one prompt, off the middle of the view.** A strip above the pilot console says the state and
  the one action, with keys from the bindings. Aimed is "course aligned", never "ready": calculation and
  charge follow the boost press and run together. No hold-to-jump.
- **The view stays the pilot's during a jump.** Free to look anywhere through charge, transit, arrival and a
  cancel; the jump never turns the camera. Other players see each jump where it happens, from where they are.
- **Resources.** Asteroids carry ore: iron, copper, nickel, titanium, silver, gold. More kinds later.

## Decided: servers

- **A game is public, unlisted or private.** Public shows in the server browser. Unlisted is joined by address
  only. Either can have a password. Private is the host alone: nobody can join.
- **A server has a name** its host chooses.
- **A listing server keeps the list.** Games advertise themselves to it; the join menu opens on a server
  browser fed by it. Ours will run at `prospect.mvl.sh`, and clients use that by default.
- **Releases build two containers:** the game server and the listing server. A version tag says which one, or
  both.

## Decided: persistent worlds and exploration (planned)

- **Automatic saves only:** configurable interval, 30 seconds by default, plus clean-shutdown saves and recovery copies.
  One save per world; Continue and Load World. The interval is not a guaranteed maximum crash-loss window.
- **One portable world:** private play can become hosted multiplayer, then move to a dedicated server.
  Explicit Discord claiming preserves the local character's progress; private offline play remains available.
- **Ships remain after MP logout:** configurable personal protection and separate future alliance protection,
  with per-ship damage-reset delays and deadlines that survive server downtime. Details: [persistence](roadmap/persistence.md).
- **Explore a bounded 3D galaxy:** configurable thickness, interactive slices/tilt, anonymous destinations,
  hover/pinned sector cards and ship lists. This is a planned map upgrade, not removal of the existing Y layers.
- **Charting and sensors are separate blocks:** manual scans while flying create permanent personal charts;
  passive 3D sensors give live coverage across sector borders. Stale knowledge remains without live updates.
  Details and deferred chart sharing/trading: [exploration](roadmap/exploration.md).

## Decided: players together

- **Chat** in game.
- **Parties.** Party members show at the top left: name, ship, shield and hull of the ship they fly.
- **Factions have ranks.** An owner, members, and ranks in between that may invite and remove.
- **Tab opens the player menu.** Its first tab lists the faction's members.

Factions are not saved yet: they are gone when a server restarts. They must be written to disk once saving
exists.

## Decided: the interface

- **One navigation bar along the top**: Play, Fleet, Map, Social, Settings in a game; the Discord display name
  at the right. No inventory tab until there is an inventory.
- **Social windows are movable and can be pinned.** Pinned means kept on screen in flight, not locked in place.
  A pinned window never takes flight's keys; the release key (`Shift`) lets the pointer use it.
- **The sector panel is hidden until asked for.** `F2` (from `bindings.json`) shows and hides the panel in the pilot view, the command
  view and the map. No hint or empty row is drawn when closed. Closed at the start of every game, kept while switching views, never saved. Passive: it never
  pauses, captures the pointer or takes flight keys. `F` stays downward thrust and Follow.
- **No calls.** No voice, video or phone controls.
- **Show only what the services say.** No presence, avatars, activity or join buttons until an API gives them.

## Decided: accounts and friends

- **Persistent accounts use Discord sign-in.** Discord handles credentials and account recovery; PROSPECT
  does not run an email/password system or require SMTP. Sign-in and the friends list are in the game.
- **Friends persist across reconnects and game restarts.** They belong to a stable account, not a game's
  temporary player id or a display name.
- **Request only Discord identity.** No email, server membership, messages or Discord friends access is needed.
- **Private play needs no account.** A private local game works signed out, offline and with `--listing off`.
- **Playing with others needs a Discord sign-in in the official client.** Join Server and hosting an unlisted or
  public game ask for it first; so do friends. It is checked when a game is entered, not later.
- **Game servers do not check accounts yet.** The wire keeps session identities; roster names are not verified.
  Account-backed identity in a game needs a later protocol change.

## Decided: first account release

- **Accounts live alongside the listing service**, with a durable SQLite volume. Public game listings remain
  temporary and in memory. Existing deployments work with accounts disabled.
- **Sign-in returns through the game's loopback listener.** The browser returns a one-use code to
  `127.0.0.1`; the game exchanges it with its private verifier over HTTPS. There is no polling login link.
- **Use Discord's display name, not its username handle**, cleaned for the interface, with a generated pilot
  name when none is present. Discord ids stay inside the account service.
- **Friends are added by PROSPECT friend code first.** Requests and accepted friendships survive restarts.
  Same-server friend markers and adding friends from the player roster come in a later protocol change.
- **Account tokens stay in the client process.** Signing in again restores the same account and friends.
  Game servers never receive the account-service token or a Discord token.

## Decided: world generation

- **One seed makes the whole world.** Same seed, same world, on the server and on every client.
- **Asteroid baseline:** the former maximum asteroid multipliers are the new 1× defaults, with controls up to 5×.
  This includes ore share and grade, not material abundance or paired minima. The per-sector ceiling is 20,480;
  bounded placement can omit rocks. Frequency and ore saturate at 100%; a field reaches at most one sector
  from its centre.
- **Sectors are filled by modules.** Each module makes one kind of thing: asteroid fields and gas clouds now,
  stations later. Each draws from its own streams. One may read what another anchors: rocks in gas do.
- **Every sector has a character:** how rich it is and which resources can occur there. Some have every
  resource, some have none, some never have a given one.
- **Asteroids come in all sizes.** Many tiny ones, a few huge ones, in dense fields.
  An asteroid with ore looks slightly different from bare rock: noticeable, not loud.

## Decided: fields and clouds in world space

How it is built: [world generation](world-generation.md).

- **No seams.** Asteroid fields and gas clouds lie across sector borders and run through them unbroken.
- **Density is noise in world space:** from very few rocks, through some, up to a field's full density. A field
  sits where it falls, not around a sector's centre, and fades in and out at its edge.
- **Rocks in gas.** Some gas clouds hold asteroids. How often and how dense is content.
- **Gas clouds are mostly the size they were, and now and then huge:** up to a sector across, about 40 km.
- **A sector is still generated alone,** the same on every machine, and an asteroid is still named by its index
  in its sector.

## Decided: world settings

- **A world has settings:** switches and multipliers, chosen when a new game or a server is set up.
- **The server owns them.** A client is told on joining and plays by them.
- **First ones:** collision damage, shields against collisions (full, weak, none), shield recharge, jump charge
  speed and hull strength. Asteroid settings control amount, per-sector cap, radius, field dimensions,
  occurrence, spacing, ore-bearing share and grade. Defaults use the approved former-maximum asteroid preset as 1×.
- **Fixed for the life of a world.** Set when a server or a local game starts, not while it runs.

## Decided: world generation controls

- Counts are requested per-sector ranges, and sizes are radius ranges in metres; placement may fall short.
- Richness ranges clamp sector character; ore grade ranges clamp ore quality, globally then per material.
- Material abundance weights only materials already allowed in that sector; zero removes their share and map presence.
- Existing distribution shapes and the guaranteed home field stay intact under the rebased scales. No mining economy changes.

## Decided: building

- **Big builds that stay smooth.** The near-term target is 30–50 ships and stations in a sector, with thousands
  of blocks per hull. Later, stations can reach about 150,000 blocks; 20 ships and 10 such stations on screen
  must stay within the frame and tick budgets. These are targets, not current measured capacity.
- **A block is a box on the grid.** Any whole size, so a long wall is one block, not hundreds. A box that takes
  enough damage breaks as a whole; it does not chip into pieces.
- **Building is free for now.** Later it costs materials.
- **A ship generator comes later.** Pick a style, move sliders, shuffle a seed, preview, build. For players who
  do not want to place blocks.

## Decided: the ship editor

References for feel: the assembly building of Kerbal Space Program (parts, attaching, symmetry) and Avorion
(stretching a block). References, not clone targets.

- **A parts editor over the grid.** Pick a part from a catalogue, attach it face to face, stretch it, mirror
  it. What it saves is still boxes on the grid.
- **Two kinds of part.** A dynamic part stretches to any whole size. A stock part has a fixed shape and mesh and
  fills a box on the grid, so it snaps and collides like any other box.
- **B opens the editor around the ship in space** after 30 seconds without damage. Until hostility exists,
  other players in the sector do not block editing; there is no enemy-presence gate yet.
- **Shipyards become the normal place to build, later.** Building anywhere stays as the fallback for when no
  shipyard is left.
- **Stage 2 is implemented:** the old 64-block cap is gone. The editor is next, within the current shared
  resource limits; scale work must raise measured capacity before thousands of blocks are promised.

## Decided: applying an edit

The server and session side is built ([hosting](hosting.md#ship-editor-protocol-26)), and so is a first,
playable editor on top of it.

- **Playable first, scale after.** The editor works within today's limits; nothing is raised for it.
- **A design never changes.** Applying an edit puts a new hull, with a new id, where the old one was.
- **The catalogue only grows** while a server runs. No number is reused or rebound. It can fill up.
- **No protection while editing.** Damage ends the edit; the draft stays with the player.
- **Building is free:** new blocks are whole. Shield and battery charge is kept, not refilled.
- **A ship keeps its blueprint.** Losing blocks does not change the design. The hull points a ship is shown
  against are the blueprint's, not what its surviving blocks add up to.
- **Repair is its own request, and free for now.** It rebuilds the ship as its blueprint. Later it costs materials.
- **A design need not work.** Missing thrust, torque or power does not stop Apply. Those systems are simply
  off; the editor warns instead of refusing.

## The editor as built (second slice)

What works now:

- `B` asks; the editor opens only on the server's grant. The world keeps running; the ship gets no controls.
- The draft is the full blueprint. Lost blocks are in it, outlined red; the hull reads now against built.
- An orbit camera around the ship. `F` (the Follow key) fits the selection, or the whole draft, in view.
- Parts: a search, tabs by role, drone parts on their own tab, cards with figures at the placing size.
- Place by click or by dragging a card onto a face. Select with the middle button; Shift adds or removes.
- Selected blocks are outlined blue. A left drag moves them; Copy, Paste, Delete and Merge act on all.
- Every change is one undo step. Undo and redo clear the selection rather than name shifted blocks.
- Repair first: a damaged ship opens repair-only (look, select, Repair). Changes kept from before the damage
  are thrown away only with **Discard changes**. The server refuses any other design for a damaged hull.
- One kind of FTL drive per ship, any number of it; refused at placement, in the check and on the server.
- The part to place is sized by holding `S` and dragging a face of its ghost (the opposite face stays), and
  turned with `R` by dragging one of two rings around its ghost, in exact quarter turns, before it is placed.
- The ghost glides into each turn; the glide is drawn only, never placed. The right button orbits through both.
- A turn needs no confirming. A click on the turned ghost places it; `R` or a click elsewhere keeps the turn
  without placing; `Esc` puts it back.
- Choosing a part, tab or shape in the part list hands the keyboard back to the world: the ghost follows the
  pointer onto the ship at once, and the first click there places.
- Figures in red with the reason when the ship cannot power, move or turn well. Warnings never stop Apply.
- The shield shape's complexity against its limit. Over the limit blocks Apply, with the reason.

Not built yet:

- Shapes other than boxes and wedges; stock parts with their own meshes.
- Mirror and symmetry. Saved designs; changing a ship's name, class, block size or colour.
- A drag handle for resizing a placed block; its resize is numeric.
- Anything past today's limits of a few hundred boxes.

## Decided: networking

- **1 to 128 players in one world on one server.**
- **A lot of things in space.** Debris, asteroid pieces, stations, ships, items, resources and canisters must be
  cheap to have around in large numbers.
- **Server decides everything that can be cheated.** Clients predict their own ship and get corrected.
- **One code path.** Singleplayer is a server inside the game; multiplayer is the same server on its own.
- **Target feel:** right at 100 ms round trip and 1% loss.

## What that demands of the engine

Not built yet. These are the consequences of the decisions above, in the order they block other work.

1. **One store for every kind of thing**, not just hulls: ships, stations, fighters, asteroid pieces, debris,
   canisters, gas fields. Each is owned by a faction, not by a player; a player acts through their faction.
2. **Things at rest cost nothing to send.** Tell clients once, then stay silent until something disturbs them.
   Asteroids already work this way; debris and canisters follow the same rule.
3. **Send only what is near and what changed**, most important first, inside a bandwidth budget per player.
4. **Two levels of simulation.** Sectors with a player in them run in full. The rest of the galaxy runs as a
   cheap strategic model (faction economy, supply lines, fleets as numbers), so factions keep living without
   128 sectors of physics.
5. **Sectors simulated in parallel** on the job graph: one occupied sector, one job.

Remaining scale work: full-state snapshots, bounded runtime geometry and hull-only storage.

## Planned, in order

Persistence is the foundation for the newly agreed exploration/map work.
The [implementation sequence](roadmap/implementation.md) orders that work and its parallel-agent boundaries.
The existing editor and scale tracks below continue separately; this roadmap does not cancel ongoing work.

Next:

1. The ship editor (B): its second slice and wedges are in. Smooth shields are in. Next: mirror, stock parts, a drag handle.
2. Blocks at scale, stages 3 to 5: collision, drawing and the full load.
3. The entity store and the entity kinds: ships, stations, fighters, asteroids, gas fields.
4. Interest management and delta snapshots.

After that:

1. Asteroid looks: more complex shapes, generated textures, dust where a ship bumps one.
2. Asteroids that crack into pieces, for the ones with a rich core.
3. The ship generator.
4. Mining and the loop it feeds: mine, haul, refine, sell, build. Shipyards.
5. Factions: strategic model first, then their ships and stations in occupied sectors.

## Deferred

- Mining and inventory wait until the environment work is complete.
- Before implementation, hold a brainstorming session with the product owner covering mining/inventory together with ship turrets.
- Ice currently exists only as a generated resource; there is no extraction, cargo inventory or mining yield.
- Gas-stress performance optimization is deferred to a later pass; retain same-build measurements for that work.

## Open

- **Shield room for many separate non-box parts.** A separate part that is not a box (a wedge, an L) is sampled
  and needs about a thousand indices; a few hundred of them do not fit. Cheaper shapes for those are undecided.

- Whether players can make deals with AI factions (alliance, trade pact, war), or AI factions only react.
- What PvP rules a server can set: everywhere, by sector, or by consent.
- Persistence architecture and remaining load/recovery policies: [open items](roadmap/persistence.md#open-before-implementation).

## Open: servers and players

- Faction rights beyond inviting and removing: who may fly, order, build or scrap what.

## Open: building

- What "no shipyard is left" means once shipyards exist: none in the galaxy, in the faction, or in reach.
- Where stock part meshes come from: generated in code, or modelled files.

## Decided: visual collision damage

- Surviving blocks can retain bounded dents after sufficiently hard collision damage passes shields; both players share authoritative state.
- Dents are visual only. Hull and asteroid collision shapes stay unchanged; asteroids do not deform.
- Repair restores the original blueprint. Far clients may skip drawing detail but retain state for returning nearby.
- The client draws nearby inward dent meshes and short contact sparks from replicated state, with distance and projected-size fading. Physics and picking keep the original surfaces. GPU appearance remains unchecked.

## Decided (provisional, tunable): new ships from a drone

- A player flying their escape drone may name and create a free ship, then edit its single core block.
- The drone remains controlled, including when Apply replaces the new ship.
- Server limits, clearance and client contract are in [hosting](hosting.md#new-ships-from-a-drone-protocol-41).
- The server/session foundation is built; dialog and key handling are separate client work.
