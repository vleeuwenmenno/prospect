<img src="assets/icon.svg" width="64" height="64" alt="">

# PROSPECT

A multiplayer space sandbox: you command from a pod, mine asteroids, build hulls and run a fleet across star
systems. C# on .NET 10, built on the [BRUTAL](https://github.com/ahwootech/BRUTAL) framework (Vulkan, GLFW,
Dear ImGui, RakNet). Windows x64, Linux x64 and Apple Silicon macOS.

Singleplayer is a server running inside the game process; multiplayer is the same server as a separate binary,
or that local game opened to other players. There is one simulation and one code path.

## Status

Exploration has a server/client backend: personal saved charts, manual scan intent, owned-ship sensors and dynamic visibility filtering.
The galaxy map shows it: fog over uncharted space, charted sectors, live sensor coverage, lost contacts, a sector card
with the scan control, layer navigation and a tilted view. Seed secrecy remains unfinished, and the map card does not
yet show the server's jump range and blind-jump risk. The map's galaxy edge is drawn, not enforced.
See [exploration contract](docs/hosting.md#exploration-foundation-protocol-37) for the map API and disclosure limits.

Rare decorative volumetric gas clouds are generated: lopsided, self-shadowed, with billowy edges and wisps. Most are 8 to 11 km long; a few are huge, up to a sector across, and any of them can lie across sector borders. Some hold asteroids. They are seen from up to five sectors away, at three levels of detail that hand over without a step. A huge cloud seen from inside or close by is broken into strands with depth. Full-screen views reach 144 FPS at 5096x1390 except a pilot view in dense asteroids and the inside of a huge cloud (about 110); visual acceptance is pending. See [gas clouds](docs/gas-clouds.md).

Explicit private local claims have a nonvisual API backed by account-service tickets and an exact durable save commit. No player-facing claim UI exists; remote saved-world admission remains missing. Linked local owners still play offline. Save layout 3 refuses older layouts without migration.

Chat, the player roster and parties work end to end: the server decides, the client shows them.

- **Navigation bar** along the top. In a game: Play (pilot view), Fleet (command view), Map, Social and
  Settings. Out of a game: Play (main menu), Social (Friends) and Settings. At the right, your Discord display
  name; signed out, Sign in with Discord. An amber dot on Social means a friend request or a party invitation is
  waiting. No tab starts or leaves a game. In the pilot view hold `Shift` to click it.
- **Social workspace** (Social, `Tab`/`P`, `O`): a navigator and separate windows for Friends, Party, Players,
  Chat and a card for one account or player. Drag a window by its title bar, close it with its X. Windows keep
  their place for as long as the game runs and stay on screen when the window or scale changes.
- **Pinned windows** stay over the view after Social closes. In flight they take nothing: W, Space and the mouse
  still fly. Hold `Shift` to drag or click one. `Esc` never closes a pinned window; its X does.
- **Players** (`Tab` or `P`): everyone on this server and who left this session, with Invite and a pending
  invitation's Accept and Decline. These are session names, not accounts. **Party** (`O`): your party, Remove
  for the leader, Leave Party.
- **Chat** (`Enter`): everyone or party channel, the last 64 lines, typed lines checked before sending.
- **Party list** at the top left: each other member's name, ship, shield, hull and sector. In a short window
  or at a large interface scale it shrinks to one-line rows with a count of the rest. Recent chat lines show
  at the bottom left for a few seconds while the chat window is not on screen. The left column narrows at a
  large interface scale so the middle of the view stays clear.
- **Friends** (Social, or the account name in the bar): sign in with Discord in your browser, then add friends
  by their 16-character friend code, search your lists, answer requests and remove friends. A name opens its
  card. The list belongs to your account and lasts across games and restarts. It needs the account service
  beside the listing server ([accounts](docs/accounts.md)); with `--listing off` there are no accounts.
- **Not there:** whether a friend is online or in a game, avatars, direct messages, joining a friend, blocking,
  voice or video calls, an inventory tab.
- **Sign-in for playing with others.** Join Server and hosting an unlisted or public game ask for a Discord
  sign-in first, on the same screen; a private game never does, offline included. Servers do not check the
  account yet: in a game, names and ids are still the session's, not verified accounts.
- **Text:** names, chat and server names show Latin and Japanese (kana, kanji, CJK punctuation; bundled Noto
  Sans JP). Other characters the fonts lack show a fallback glyph (`?` in the main font); no emoji.

Not there yet: whether a friend is online or on your server, adding a friend from the player list, factions and
alliances. The sign-in lasts while the game runs; after a restart, sign in again. Names and player ids in a game
last only for the session, and presence covers only the server you are on.

Stage 1 of blocks at scale exists in tooling and tests: versioned boxes, content limits and size rules,
with seeded 4,096-block and 150,000-block fixtures. Its solid-box evaluation and running sums are how every
hull's figures are now worked out. Stage 2 supports mixed-size designs of hundreds of boxes through loading,
network transfer, collision and drawing. Shared resource budgets replace the old 64-block restriction.
See [docs/blocks-at-scale.md](docs/blocks-at-scale.md#final-admission-evidence-and-limits) for current limits,
measurements and the remaining stages.

**Ship editor (second slice).** `B` asks the server to edit the ship you fly; it opens only when the server says
yes (no damage for 30 seconds, no jump ordered or running). The world keeps running around you and other players
are never blocked. The editor works in space, around your ship:

- The part list has a search at the top, tabs by role (hull, power, move, shield, jump) and a Drone tab for the
  compact parts. Each card shows the part's figures at the size parts are placed at (whole cells on X, Y, Z).
- Click a card, then click a block face; or drag the card onto the ship. The ghost shows green where the part
  fits and red with the reason where it does not. Let go over a panel, or press `Esc`, and nothing changes.
- Size the part before placing it: hold `S` with its ghost on a face. The ghost holds still; the face of the
  ghost under the pointer is highlighted, with an arrow along the axis it moves on and the size on X, Y and Z
  beside the pointer. Left-drag that face: it moves out or in by whole cells, 1 to the largest box side, and the
  opposite face stays where it is. The pointer is followed along the face's axis as it shows on screen; a face
  pointing nearly straight at the camera is dragged up (grow) and down instead. A wedge's slant moves the box
  side it stands for, and the wedge stays the same wedge. Let go of the button or `S`: nothing is placed; the
  size and place stay, and a new click while the pointer is still or on the ghost places it there. `Esc` puts
  back the size and place from before `S`. The right button still orbits while `S` is held.
- Turn the part before placing it: `R` with its ghost on a face shows two rings around the ghost, about the
  grid's x (red) and y (green). Left-drag a ring round: the ghost follows the pointer, and the part itself turns
  in exact quarter turns (90 degrees) as the drag passes each half way. Let go, and the ghost glides onto the
  quarter turn; nothing else happens. The wheel over a ring turns a quarter either way; a click on it a quarter
  forward, `Shift`+click back. The two rings together reach all twelve wedge orientations. Each turn glides over
  `turnSeconds` in `content/visuals/editor.json`; the glide is only drawn, the part placed is always the quarter
  turn. The right button orbits the camera without ending the turn. Every turn counts at once; nothing confirms
  it. A click on the ghost places it as it is drawn and ends the turn. `R` again, or a left or middle click
  anywhere else (the sidebar included), ends the turn and keeps it, without placing: the ghost then follows the
  pointer as usual, so the next click places. `Esc`, or choosing another part, shape or the select tool, puts
  back the shape and size from before `R`.
- A ship takes FTL drives of one kind: several hyperspace drives, say, but not a hyperspace drive and a rift
  cutter. The editor refuses a part of another kind at once, and the server and the design files refuse such a
  design.
- The middle button selects a block, also while placing; `Shift` adds or removes one; a middle drag still pans.
  Every selected block is outlined blue. A middle click on empty space clears the selection.
- Drag a selected block with the left button to move the whole selection onto another face. `Ctrl+C` and
  `Ctrl+V` copy and paste it; `Delete` deletes it; **Merge** joins blocks of one type that fill a box exactly,
  and shows how the figures change. Each is one step to undo. Undo and redo clear the selection.
- `F` (the Follow key) turns the camera to the selection, or to the whole draft with nothing selected, keeping
  its angle. The camera's range grows with the draft.
- The draft is the ship's full blueprint: blocks the ship lost are in it, outlined red, and the panel shows hull
  points now against built. A damaged ship (lost blocks or hull points; a shield that is down does not count) is
  repaired first: the editor opens on it to look, select and **Repair**, and nothing can be changed or applied
  until it is whole. The server refuses a design for a damaged hull too. **Repair** rebuilds the ship as built,
  for free. Changes kept from before the damage hold it back until **Discard changes** throws them away.
- The right panel shows the checked figures. Low power, no battery for the shield, low or no forward
  acceleration, low top speed and slow turning show in red with the reason. These warnings never stop Apply.
  Figures of an older version are labelled as such. Thresholds are in `content/visuals/editor.json`.
- The shield shape's complexity is shown against its limit. Over the limit the draft cannot be applied, and the
  panel says why.
- Apply sends the design, shows its progress, and the server rebuilds the ship; you fly the new design at once.
- Damage, a jump or leaving the ship closes the edit and says why; your changes are kept for the next `B`
  on the same hull and design, including after block loss. `B`, `Esc` or Cancel throws the draft away.

- **Wedges.** Part shape Box or Wedge sits under the part list, with the size, the shape and the sizing and
  turning keys. A wedge fills half its box, with half the box's figures; the part cards show them. A selected
  block turns with its own Turn X/Y/Z, keeping
  its low corner; a turn that would overlap is refused. Copy, paste, move and undo keep each block's shape.
  Merge joins boxes only. A part placed on a slope snaps against the wedge's box, on the larger box side.

**Not there in the editor:** other shapes, stock or custom meshes, mirror or symmetry,
dragging a face of a placed block with the mouse (its resize is numeric), saved designs, renaming or recolouring a ship. Ships stay at
the current limits of a few hundred boxes. A server's design catalogue only grows, so it can fill up until it
restarts; the editor then says so. The editor has not been checked on screen by a person yet.

Milestone 1 (skeleton) is complete. On top of it, the first slice of the world is in:

- **Sectors.** The galaxy is a grid of 40 km cells generated from the server's seed: name, star, character and
  contents. Nothing is stored or sent, and nothing stops at a sector's edge. The client keeps the sectors around
  the camera generated on worker threads.
- **World generation.** Space is filled by a list of modules named in `content/galaxy/galaxy.json`, each
  drawing from its own random streams: asteroid fields and gas clouds. Both are things in world space: a field or
  a cloud is centred anywhere in a sector and lies across its walls without a seam, and every sector is still
  generated alone. The same seed gives the same world to the last bit on every machine. See
  [world generation](docs/world-generation.md).
- **Sector character.** Every sector has a richness, from barren to very rich, and a set of resources that can
  occur there. Rich and poor space come in regions. Some sectors have every resource, some none, most a few; the
  sector panel shows both; the map card never shows resources or shares, as no mineral scan exists yet. The sector panel is closed by default: `F2` shows it.
  There is no closed-state hint or reserved row.
- **Asteroid fields.** Thin scatters, clusters, dense belts and great fields, tens of kilometres long at most,
  wherever they fall: a sector holds parts of none, one or several, up to 20,480 asteroids. Density follows
  noise in world space, from almost empty to full, and a field fades out toward its edge. Some gas clouds hold
  asteroids too. The map's counts are estimates; crowded placements are omitted. The default size table runs
  from pebbles 12 m across to giants 12 km across, limited by field fit; most are tiny and a giant is rare. The
  middle of every sector, where ships arrive, is kept clear.
- **Hulls from blocks.** A hull is a list of typed blocks (engine, thruster, gyro, generator, battery, shield
  emitter, cargo, quarters, plating). Mass, moments of inertia, acceleration, turn rates, top speed, hull and
  shield points and the power budget are all computed from them and shown on the ship's data sheet.
- **Flight.** A command pod with inertial flight: thrust on all three axes, roll, dampeners, and a boost that heats
  up and locks out. Thrust, boost and shield recharge draw power; the generator refills the battery with the
  surplus. Boost needs the battery above 5% to start (`boostEnergyFloor` in `content/blocks/blocks.json`), runs
  until the battery is empty, then waits for the battery to pass 5% again; a hull without a battery cannot
  boost. The pilot console at the bottom shows shield and hull on the left, energy and boost heat on the right
  and speed with its unit in a hexagon between them, as slanted segmented bars. The energy bar marks the 5%, a
  strip under it shows the battery's live net draw or recharge, and the heat bar turns from cyan to red and says
  when boost is overheated (with the mark where it unlocks) or out of energy. The boost heat row also says
  whether the dampeners are on; off is amber. With `Shift` held in the pilot view, the bar under the cursor is
  lit and its live figures show above the console: shield charge, recharge rate, delay after damage, draw and
  restart rule; hull points, mass and safe impact speed; stored energy, net flow, generation, base draw and boost
  floor; heat, heating and cooling rates, boost draw and why boost is unavailable. A window covering a bar keeps
  it from the cursor. Heads-up panels share the unit list's look: a muted dark panel, a
  thin quiet border and a small tick in the colour of their state. Nothing glows.
- **Collisions and damage.** Hulls collide block by block against each asteroid's real surface, and block
  against block with each other. An impact bounces and turns the hull; damage grows with the square of the speed
  over a safe limit and lands on the blocks that touched, after the shield. A block at zero is gone, and the hull
  flies on what is left. Losing the core destroys the hull; its pilot gets out in a drone.
- **Shields break and restart.** A shield that pays out everything it had breaks and goes offline. Offline it
  takes no hits, even with charge in it: every hit lands on the blocks. After 8 s without damaging hits it
  charges back at its normal rate on four times its normal power, and comes online only at 25% of its capacity
  (`shieldRestartFraction` and `shieldRestartPowerMultiplier` in `content/blocks/blocks.json`). Online it tops up
  using energy proportional to points restored. FTL and propulsion take priority; insufficient energy slows or
  stops recovery. The world recharge multiplier changes speed and power together, keeping MJ/point fixed.
  The console says `OFFLINE` in red, dims the charge being rebuilt, marks the restart point on the bar
  (the hull's own `shieldRestartFraction`) and how far the charge is towards it (`RESTART 60 %`: 60% of the way). No
  countdown is shown: the delay after the last hit is not published. Target and fleet gauges dim too,
  and the party list shows a broken shield as empty. Snapshots carry the offline state and the newest shield contact
  (where, how hard, how long ago), so every client agrees on both.
- **Shield looks.** Every hull with a shield shows a faint, neutral translucent shell a margin off its blocks: the
  blocks grown round, with gaps of about a block bridged, slopes followed, and the same shell however the shape
  is split into blocks (`shieldRoundness` and `shieldBlend` in `content/blocks/blocks.json`, shared). A damaged hull's
  shell is built on a worker from the blocks it still has; until then, or if it would not fit, it keeps the shell
  it had or its intact one, so heavy damage can keep the original outline. A design whose intact shell does not fit
  is refused before Apply: some four hundred or more separate parts need more than the room. It shimmers and bends what is
  behind it by a few pixels, most at its rim. A contact lights a short, bright patch and paired fading ripples
  at the contacted surface, on the pilot's hull and everyone else's, aged from the server event (old hits never
  restart on joining). Below 80% charge it shifts gradually from neutral through amber, visible by half charge,
  then towards red near empty, with slow waves; a break, or the loss of the last emitter, collapses it red and it is
  gone until the simulation brings it back online, when it fades in. While it comes back and while its charge
  keeps rising it shivers gently: the whole shell squashes and stretches very slightly and faint bands drift across
  it (`recoveryWobble`, `recoveryHz`, and `recoveryShimmer` for the bands' light and bend), settling
  once the charge is full or stops rising (a delay, no energy). Charging is read from the charge rising twice
  running, so a correction that raises it can read as charging too. Collapses and fade-ins are timed from when
  the server says the change happened: a remote one waits for the hull as drawn, an old one is not replayed.
  Light taps also show feedback without changing damage, charge or recharge delay. The minimum closing speed
  is `shieldContactSpeed` in `content/blocks/blocks.json`; offline and bypassed shields show no contact.
  Durations, colours, ripple speed and width, the wavering threshold, the bend, the rounding and the shiver are in
  `content/visuals/shields.json`. In the pilot's own hyperspace tunnel their shield's glow and ripples stay all
  round the hull; its bend of the old world fades as the tunnel covers it, so at full cover only its light shows.
  Not checked on a GPU yet: how it looks, its cost per frame, Vulkan validation.
- **Ramming.** Two hulls that meet share the impulse by mass and inertia and both take the same damage, which
  goes by the pair's reduced mass: about the lighter hull's when the other is far heavier. A drone bounces off a
  freighter and a freighter shoves a drone aside. Below the safe speed hulls only nudge each other and can rest
  in contact. A hull in a jump tunnel is not there to be hit, and
  one that has just come out does no damage and takes none. The client predicts the contact by stepping its own
  hull and the hulls near it with the server's code.
- **Asteroids with mass.** An impact pushes hull and asteroid by their share of the momentum, so a pod bounces off
  and a heavy ship shoves a boulder aside. A pushed asteroid drifts and settles. The server tells clients only
  when an asteroid is hit; its motion from then on is a formula, and new players are sent the displaced ones. The server is authoritative; the client predicts its own hull with the same
  flight code and reconciles against snapshots; other players' hulls are interpolated.
- **Faster-than-light.** A drive is a block. There are three kinds, each with its own look: hyperspace (the stars
  stretch into lines that stream back from the hull's nose and become a tunnel), wormhole (a sphere that bends
  the stars around it) and rift (an orange window torn into space). With a wormhole or a rift the pilot sees their
  hull fly into the opening, rides through the throat behind it and comes out of a second opening that closes
  behind them. Every effect is laid out around the hull's nose, and the view stays the pilot's through the whole
  jump, so looking around shows it from the side or from behind. Another hull's rift or wormhole sits in the world
  where that hull opened it, seen in perspective from wherever the viewer is, in front of or behind what is
  around it; the hull flies into it, cut off where it is through, and out of the one it arrives through.
  Another hull's hyperspace jump shows as light gathering ahead of its nose that it lunges into, drawn out, and
  that fades where it left; at the far end it streaks in from behind and settles. Markers and targeting follow
  a jumping hull where it is drawn, and targeting takes its shape as drawn: what is cut off at an opening cannot be
  targeted, what still shows can, and a drawn-out hull can along its whole length. Only faces on screen count:
  looking into an opening shows the hull hollow, with nothing drawn across the cut, and nothing there is a target. A hull between the ends of its
  jump cannot be targeted. The pilot plots a jump on the map
  (**Plot jump**), which shows a NAV marker in the pilot view; aimed at it, the boost key starts the jump. Ships
  flown by the server jump as soon as they are ordered. A started jump is a standing order: the hull brakes and
  turns its nose to the destination, then calculates for 12 seconds while charging from the hull's power.
  Both must finish; jump charge speed affects charging only. A bigger ship needs more energy, and a weak or
  drained battery charges slower. Once ready, the hull is
  in transit for three seconds plus one second per 40 km of straight-line distance.
  Base time and speed are content tuning; there is no distance energy cost.
  The server requires 5–400 km with current drives. Blind jumps through 40 km are safe; farther ones risk
  proportional block damage, never below 10% intact hull HP. Personal destination charts remove blind risk.
  Shared range/risk previews and server-validated plot feedback exist; the map/RTS UI does not consume them yet. Taking the controls
  before the drive fires cancels the jump and loses the charge. The client generates the destination while the
  drive charges.
- **World settings.** A world has settings, chosen when a server or a local game starts: collision damage,
  shields against collisions (full, weak, none), shield recharge, jump charge speed, hull strength and asteroid generation. The
  server owns them and tells each client on joining; the client predicts by the server's figures, and the data
  sheet and the readouts show the numbers in force. The New game screen sets them for a local game; in a game,
  the menu's World Settings shows the ones in force. See [World settings](#world-settings).
- **Resources.** Asteroids carry iron, copper, nickel, titanium, silver or gold, or are bare rock. Which of them
  a sector has, and in what shares, is part of its character; the rarer ones grow more common toward the core, and
  a richer sector has ore in more of its rocks and at a better grade. Nothing can be mined yet.
- **Fleet.** Each player starts with the command pod (hyperspace drive), a mining vessel (wormhole) and a survey vessel (rift). Hulls nobody is flying are flown by
  the server under their standing orders.
- **Command view.** One key pulls the camera back from the pod into a free orbiting view over the same space.
  Click or drag a box to select hulls, right-click to send them somewhere in formation; a fleet list on the right
  selects and focuses too.
- **Galaxy.** A spiral disc 800 sectors across. Asteroid fields follow its arms and core; between the arms and
  outside the disc space is nearly empty.
- **Galaxy map.** Every grid cell is a sector of the chosen layer. Uncharted space lies under a veil and is an
  anonymous destination; a sector you scanned is charted and shows its star, name and asteroid fields as surveyed,
  never later changes. Green rings are live sensor coverage, cut through the layer; ships inside them are live,
  and a lost contact stays as a fading hollow marker for 15 seconds (a station's indefinitely). Hover a sector for
  its card beside it; click to pin the card (coordinates, distance, sensor state, what the chart holds, the jump
  warning, the scan control in your own sector, Move here and Jump here, and a scrolling list of your ships, then
  live and lost contacts). Right-click a sector for the same course in a menu. The layer bar (bottom right) and
  `Page Up` / `Page Down` change layer; things on other layers show as up and down chevrons. The tilted view (`V`
  or **Tilt**) shows three layers above and below with their charted sectors and ships on stalks; the chosen
  layer stays fully interactive. The galaxy edge ring marks where the disc ends; nothing stops flight there yet. A
  unit list on the left shows every ship you own; click a row to add or remove that ship. A plotted jump shows as a
  dotted route from the ship you fly to its NAV point until the jump is made, replaced or cleared.
- **Scanning.** Pin your own sector's card and press **Scan sector**: 5 seconds, flight continues. Leaving the
  sector, switching ship or losing the charting block cancels it; the card and the top of the flight view say
  which (the reason is inferred on the client, the server only says cancelled). The command view shows sensor
  rings and darkens space beyond them, with lost contacts at their last position.
- **Renderer.** Floating-point scene target and tone mapping, camera-relative rendering from double-precision
  positions, a star field and sky, instanced hull and asteroid meshes. Asteroids have five levels of detail, from
  a speck in the distance to a giant seen from its surface.

- **Hosting.** A game is private (the host alone, no network listener), unlisted (joined by address) or public
  (also on the list). A local game can be hosted for others: the same in-process server, listening on UDP as
  well. A server has a name its host chooses. The New Game screen sets who can join, the name, a password, the
  player slots and the port; see [Hosting and joining](#hosting-and-joining) and [docs/hosting.md](docs/hosting.md).
- **Saved worlds.** A private game is a saved world: New Game creates it with a name and the chosen settings,
  and it saves itself every 30 seconds while it changes (Settings, Saved worlds: 15 s to 5 min) and when you
  leave or quit. There is no save button. The main menu's Continue opens the world played last; Load World lists
  every world with when it was saved, and keeps one that is in use, damaged or from another version in the list
  with the reason. Leaving shows the save in progress; a save that failed or could not be confirmed is reported,
  never passed over. Worlds live in the user data folder (`~/.local/share/prospect/worlds` on Linux,
  `%APPDATA%\prospect\worlds` on Windows); see [docs/save-foundation.md](docs/save-foundation.md).
- **Dedicated saves.** `--world <folder>` loads an existing world; add `--create-world` to create if absent.
  Autosave defaults to 30 seconds (`--autosave 1..3600`). Saved mode refuses all remote players until verified
  ownership exists; it is not playable multiplayer. See [hosting](docs/hosting.md#dedicated-saved-worlds).
- **Server browser.** Join Server opens on the list of public games: name, players, a lock for a password, version.
  Refresh, loading, failed and empty states; entries the game dropped as invalid are counted. Joining a game with a
  password asks for it first. Direct connect (address, port, optional password) sits beside the list.
- **Passwords.** An unlisted or public game can have one. It is never sent: the server challenges, the client
  answers with a keyed hash, and wrong tries are slowed down per address. It protects the password, not the
  traffic: game packets are not encrypted.
- **Connection limits.** A connection that is not a player yet never holds a player's slot. One address can
  keep only a few waiting and open only so many; network input is read within a budget each tick. One address
  cannot keep others from connecting, and no flood holds up a tick. Many addresses can still keep newcomers
  out, and a server without a password lets anyone in until it is full: see
  [docs/hosting.md](docs/hosting.md#connection-limits).
- **Listing server.** A small HTTP service (and its container image) that keeps the list of public games. A
  public server announces itself to it, refreshes every 30 seconds and removes itself on shutdown; the client
  fetches and checks the list. Ours is `https://prospect.mvl.sh`, the default in `content/net/listing.json`. See
  [docs/listing-server.md](docs/listing-server.md).
- **Releases.** Pushing a tag (`server-v…`, `list-v…` or `v…`) publishes the game server and listing server container
  images to GitHub Container Registry. See [docs/releasing.md](docs/releasing.md).

Not there yet: NAT traversal or automatic port forwarding (a host needs a UDP port others can reach), encryption of game traffic, IPv6, mining, taking control of ships owned by other players (permissions), jump range and cost, gates,
saving hosted games (an unlisted or public game is not saved: a saved world cannot yet tell who a returning player
is), playable saved multiplayer, deleting or renaming a world from the game, restoring a recovery copy
from the game. Stations are not generated yet; gas has no gameplay effects; asteroids do not crack into pieces, and ore shows only as
a tint. Asteroids come in a shared set of shapes until mining gives each one its own body. Mixed-size designs of hundreds of blocks now load, transfer, fly and take replicated damage.
Shared admission bounds geometry work, the intact shield shell and total catalogue resources before play.
The default shell budget is 32,768 vertices and 196,608 indices per hull, and at most 910 blocks. A separate
box-shaped part uses a triangulated rounded box of 80 surface vertices, so about four hundred separate boxes fit; separate
non-box parts cost more. The catalogue is limited to 192 KiB encoded data
and a conservative 8,192-box decoded budget. These are practical stage 2 limits, not unlimited scale.
Client meshes, damaged transforms, picking and smooth shields have headless integration coverage.
Full-scale geometry and performance remain stages 3–5.

## Controls

`F1` opens searchable Controls from pilot, command or map view. Search current bindings by action, key,
button or category. `F1`, `Esc` or Close returns to the previous view; the session keeps running.
The cursor is hidden only in the pilot view. The command view, the map and every menu show it.

- **Pilot view:** the hidden cursor steers the view. Hold either `Shift` to show it: mouse flight and targeting
  stop, keyboard flight still works, and the bar and pinned windows can be clicked. With `Shift` held, a right drag that
  starts over space turns the camera, as free look does; letting go of the button or of `Shift` returns the view to
  the nose. Press `Shift` before the right button: a click in the same instant as `Shift` is dropped.
- **Command view:** the cursor is shown as soon as it opens (`N`), and the mouse works in the world: select,
  box-select, order, rotate, pan and zoom. `Shift` there only adds to a selection or moves the plane; it never
  hides the cursor or ends a drag. Back in the pilot view the cursor is hidden again unless `Shift` is held or
  something is open.
- **Menus, data sheet, map, Controls, the Social workspace, Chat while typing:** the cursor shows until they
  close, and the world gets no mouse. Pinned windows do not count: they leave the cursor hidden.
- **Ship editor:** the cursor is shown and the mouse works on the ship, except over the editor's panels. The ship
  gets no controls while it is open. A press on a panel never reaches the ship, with one exception: a part card
  dragged out of the part list. When the editor closes, flight keys still held from it do nothing until they are
  let go.

Gameplay has no bottom-left keybind strip. Bindings come from `content/input/bindings.json`.

Click again at the same place to step through overlapping targets, then wrap to the first.
This works with middle mouse in pilot view and left-click in command view; moving more than four pixels resets it.

| | Keyboard and mouse | Gamepad |
| --- | --- | --- |
| Forward / back, strafe | `W` `S`, `A` `D` | Left stick |
| Up / down | `R`, `F` | Right / left trigger |
| Look around | Mouse, or arrow keys | Right stick |
| Free look (hull holds its heading) | Hold right mouse button | |
| Zoom camera | Mouse wheel | |
| Roll | `Q` `E` | Bumpers |
| Boost | `Space` | Left stick click |
| Show the cursor (pilot view) | Hold `Shift`; with it, right-drag over space turns the camera, and pointing at a console bar shows its figures | |
| Dampeners on / off | `Z` | `X` |
| Command view | `N` or `F9` | `Back` |
| Galaxy map | `M` | `Y` |
| Halt (cancel order) | `H` or `Backspace` | `B` |
| Ship data sheet | `I` | D-pad up |
| Target the ship under the reticle, or the asteroid if there is no ship | Middle mouse button | |
| Take control of the target (same sector only), or leave in a drone | `T` | D-pad down |
| Menu or back | `Esc` | `Start` |
| Social: Players window | `Tab` or `P` | |
| Social: Party window | `O` | |
| Chat: open, send, close | `Enter`; `Enter` sends, `Esc` closes | |
| Close the focused social window, then Social | `Esc` (never a pinned window) | |
| Ship editor: ask to edit / cancel | `B`; `Esc` also cancels | |

On menu screens the arrow keys or the D-pad move between rows, left and right change the focused setting, and
`Enter` or `A` selects. The mouse works too: click a row's arrows, or drag a slider. In the server browser, the
row with focus is the one picked: `Enter` or a double-click joins it.

The pointer turns the view, not the hull. The hull swings its nose onto the view line whenever it is under
thrust, so looking around while coasting costs nothing; hold the right mouse button to look around under thrust
without turning.

A ship near the reticle is bracketed; the middle mouse button targets it and shows its shield and hull. A target
out of view, behind you, or under a panel, the jump strip or the console, shows as an arrow at the edge of the clear
part of the view pointing the way to turn; it slides back into the bracket as the target comes into view. The
plotted jump's NAV marker does the same. With one
of your own ships targeted, `T` puts you at its controls and the ship you left is flown by the server. With nothing
targeted, `T` leaves your ship in a drone: a tiny craft with little hull and shield that is removed when you take
a ship again. In the command view `T` takes control of the first selected ship.

Command view (the cursor is always shown):

| | |
| --- | --- |
| Select a hull or an asteroid | Left-click it; hulls also by their row in the fleet list |
| Select several | Drag a box (your hulls first; asteroids if the box holds no hull); `Ctrl` + drag adds to the selection; `Shift` + click adds or removes one; `Ctrl` + `A` selects all |
| Move the selection | Right-click the destination |
| Rotate the view | Right-drag |
| Zoom | Mouse wheel |
| Pan | `W` `A` `S` `D`, arrow keys, or middle-drag |
| Lower / raise the plane | `Q` / `E`, or `Shift` + left-drag |
| Focus the camera on the selection | `Space` or `F`, or double-click a row in the fleet list |
| Halt the selection | `H` or `Backspace` |

Ship editor (`B`; the cursor is always shown):

| | |
| --- | --- |
| Place the chosen part | Left-click a block face, or drag a part card onto one; new and pasted blocks are not automatically selected |
| Size the part to place | Hold `S` over a face, then left-drag a highlighted face of the ghost (the opposite face stays); click again to place, `Esc` puts it back |
| Turn the part to place | `R` over a face, then left-drag a ring round (exact quarter turns), or the wheel or a click on it (`Shift`+click back); click the ghost to place it, `R` keeps the turn, `Esc` puts it back |
| Select a block / add or remove one | Middle-click / `Shift` + middle-click (left-click too after a right-click stops placing) |
| Move the selection | Left-drag a selected block onto a face; `Esc` or right-click drops it unchanged |
| Copy / paste the selection | `Ctrl` + `C` / `Ctrl` + `V`, then click a face |
| Delete the selection | `Delete` |
| Focus the camera on the selection, or the whole ship | `F` or `Space` (the Follow key) |
| Orbit / pan / zoom the camera | Right-drag / middle-drag / mouse wheel |
| Undo / redo | `Ctrl` + `Z` / `Ctrl` + `Y` or `Ctrl` + `Shift` + `Z` |
| Put back a turn, drop what is carried, then cancel the edit | `Esc`; `B` or Cancel ends the edit at once |

Galaxy map:

| | |
| --- | --- |
| Select ships | Left-click a ship, or drag a box; `Shift` or `Ctrl` adds to the selection; a click on empty space keeps the selection |
| Add or remove one ship | Click its row in the unit list; **All** and **Clear** change the whole list |
| Set course | Right-click a sector, then pick **Move here**, or **Plot jump** (the ship you fly) / **Jump here** (selected fleet ships) in the menu |
| Close the menu | Left-click elsewhere, or `Esc` (then `Esc` closes a pinned card, then the map) |
| Take control of a ship | Select exactly one other ship of yours in the sector you are in, then `T`; the map stays open. Another sector: travel there first |
| Pan / zoom | `W` `A` `S` `D`, arrow keys or middle-drag / mouse wheel |
| Sector card | Hover a sector to preview; left-click to pin; left-click outside the galaxy or `Esc` closes it |
| Scan your sector | Pin your own sector's card, then **Scan sector** |
| Change layer, centre on your ship | `Page Up` / `Page Down` or the layer bar, `Space` |
| Flat or tilted view | `V`, or **Flat** / **Tilt** on the layer bar; flattening shows the pinned card's layer |

Map orders go to exactly the selected ships. Opening the map with nothing selected selects the ship you fly.
After **Clear**, **Move here** and **Jump here** are greyed out and nothing is ordered. Jumps also require a
working drive and a destination outside the current sector; ineligible ships keep their orders.
The ship you fly gets a plotted course instead: a NAV marker, or an arrow at the edge of the view, shows the way.
Turn its nose onto the marker and press boost (`Space`) to start the jump. Forward (`W`) and boost pressed together,
in either order, start it too, and forward held from that start does not cancel it; once both are let go, any
thrust does. Off the marker, boost works as usual
and the HUD says to aim first. A card beside the marker names the destination and its distance. A strip above
the pilot console says what to do: `OFF COURSE` with the boost key to press once aimed, `COURSE ALIGNED` with
what the drive will need, then calculation and charge as two gauges filling together, and that thrust cancels.
It shows only while a course is plotted, a jump is under way or the ship flies a move order. The course clears
on arrival. Outside the map, a move with nothing selected
still goes to the ship you fly.

Where the command plane cuts through an asteroid or a hull, the cut is drawn as a bright line and the part below
the plane is dimmed, so it is clear what sits on the plane. `I` shows the data sheet of the first selected hull or
asteroid.

`F2` shows or hides the sector panel (name,
coordinates, rocks, server, players, richness and resources) in the pilot, command and map views. It starts each
game closed and stays as set when switching views. No closed-state hint is drawn. It takes no flight
keys: `F` is still down and Follow. `F3` shows the
performance overlay, `F11` toggles fullscreen, `F12` takes a screenshot. Bindings live in
`content/input/bindings.json`; block types and flight rules in `content/blocks`, hull designs in `content/ships`,
galaxy rules in `content/galaxy`, world settings in `content/rules`, the listing server's address in `content/net`.

## World settings

Settings are fixed for the life of a server or a local game. Defaults and ranges live in
`content/rules/settings.json`.

Asteroid 1× is the former maximum multiplier preset: amount, radius, occurrence, spacing, ore share and
grade at old 2×; field dimensions at old 1.5×. Material abundance and paired minimum/maximum defaults stay unchanged.
All seven multipliers now reach 5× of that baseline. The old 4,096 count clipping is removed: dense 1× sectors
can contain more rocks than the former preset. Probabilities and grade saturate at 100%; radius remains capped
at 6,000 m and field dimensions fit the sector. Large rocks and spacing compete for room, so 5× requested amount
does not guarantee five times the placed count. Rendering still has a shared 32,768-instance frame budget;
the generation cap does not mean every rock in every visible sector draws simultaneously.

| Id | Values | Default | What it does |
| --- | --- | --- | --- |
| `collisionDamage` | 0 to 4 | 1 | Multiplies collision damage. At 0 collisions push but never damage. |
| `collisionShields` | `Full`, `Weak`, `None` | `Full` | `Full`: the shield absorbs a collision. `Weak`: it absorbs it but drains four times as fast. `None`: collisions pass the shield and hit the blocks. |
| `shieldRecharge` | 0.25 to 4 | 1 | Multiplies how fast shields recharge. |
| `jumpChargeSpeed` | 0.25 to 4 | 1 | Multiplies how fast a jump drive charges. A jump takes the same energy. |
| `hullStrength` | 0.25 to 4 | 1 | Multiplies every block's hit points. |
| `asteroidAmount` | 0 to 5 | 1 | Requested asteroid counts, including home; 0 disables rocks. |
| `asteroidCap` | 0 to 20480, integer | 20480 | Per-sector upper count limit, never above shared content. |
| `asteroidSize` | 0.25 to 5 | 1 | Radius multiplier, limited by field fit. |
| `asteroidFieldSize` | 0.5 to 5 | 1 | Field dimension multiplier, including home. A field reaches at most one sector from its centre. |
| `asteroidFrequency` | 0 to 5 | 1 | Field occurrence probability multiplier, capped at 100%; home is exempt. |
| `asteroidSpacing` | 0.5 to 5 | 1 | Spacing multiplier, with a nonoverlap minimum. |
| `asteroidOreShare` | 0 to 5 | 1 | Ore-bearing fraction multiplier, capped at 100%; no new resource kinds. |
| `asteroidOreGrade` | 0.25 to 5 | 1 | Grade multiplier within ore-bearing rocks, capped at 100%. |
| `asteroidCountMin` | 0 to 20480, integer | 0 | Lower requested count for the fields a sector anchors, which may land next door; pairs with `asteroidCap`. |
| `asteroidRadiusMin`, `asteroidRadiusMax` | 0.75 to 6000 metres | 0.75, 6000 | Clamp sampled radii after the size multiplier. Diameter is twice radius. Rocks that cannot fit are omitted. |
| `oreRichnessMin`, `oreRichnessMax` | 0 to 1 | 0, 1 | Clamp sector richness after the home floor; affects map character, ore-bearing fraction and grade. |
| `oreGradeMin`, `oreGradeMax` | 0 to 1 | 0, 1 | Clamp ore grade after the grade multiplier; 1 means 100%. |
| `<material>Abundance` | 0 to 4 | 1 | Relative share among already available materials. Zero removes that material from the sector character and rocks. |
| `<material>GradeMin`, `<material>GradeMax` | 0 to 1 | 0, 1 | Per-material grade clamp, applied after the global grade range. |

Materials come from galaxy content: `iron`, `copper`, `nickel`, `titanium`, `silver`, `gold`.
New Game has collapsible Asteroids and Resources / Materials sections, and one expandable section per material.
Ranges use paired handles on one track; click the nearest handle and drag, or focus MIN / MAX and use arrow keys.
Ranges clamp existing sampled distributions, not uniform replacement draws. Crossed input limits lower the minimum to the maximum.
A count minimum never creates fields; amount or cap zero disables rocks. Frequency zero keeps the baseline home field but does not raise it to the minimum.
Requested minimum counts can fail to fit. Radius minimums that cannot fit a field omit the rock rather than shrink below the minimum.

Generation settings are shared gameplay rules, not local graphics settings.
Counts are requests: changed amount, size, field size or spacing uses bounded placement and can produce fewer rocks.
The map and sector readouts mark counts as "ABOUT": they are estimates made without placing a rock. Generated sector summaries report actual counts.
Default settings preserve the original generated world, including its crowded-field placement fallback.

How to set them:

```sh
make run ARGS="--start local --set collisionShields=None --set hullStrength=2"    # a local game
make run ARGS="--start newgame --set hullStrength=2"                             # the New game screen, preset
make server ARGS="--set collisionDamage=0.5 --set jumpChargeSpeed=2"              # a server
make server ARGS="--settings my-world.json"                                       # a server, from a file
```

- `--set <id>=<value>` can be repeated; the last one for an id wins. On a server it is applied after the file.
- The file is one JSON object: `{ "collisionDamage": 0.5, "collisionShields": "Weak" }`.
- An unknown id is ignored with a warning. A value out of range is brought into range with a warning. A value
  of the wrong kind, or a file that is not valid JSON, stops the start with a message that says what is wrong.
- A server logs every setting that is not at its default when it starts.
- A client that joins plays by the server's settings; its own `--set` only applies to local games.
- In the client, New Game opens a screen with every setting, starting from the defaults plus any `--set`.
  Changed ones are marked and can be reset one at a time or all at once. Choices are kept until the game exits;
  there are no saved presets. A saved world keeps the settings it was created with, its seed and its spawn
  sector for good: loading it never takes them from `--set` or the screen.
- In a game, `Esc` then World Settings shows the settings in force, read-only. On someone else's server these
  are the server's.

## Hosting and joining

Dedicated server options and defaults: `make server ARGS="--help"`.
CLI options override the settings file. Password precedence is CLI, then nonempty `PROSPECT_PASSWORD`, then file.
An empty CLI password clears it.

| Visibility | Listens on UDP | On the list | Who can join |
| --- | --- | --- | --- |
| `private` | no | no | Nobody: the host alone. A local game's default. |
| `unlisted` | yes | no | Anyone with the address. A dedicated server's default. |
| `public` | yes | yes | Anyone: it is announced to the listing server. |

```sh
make run ARGS="--host unlisted --host-name 'Ceres Station'"                # host a local game, joined by address
PROSPECT_PASSWORD='open sesame' make run ARGS="--host public --host-players 8"  # on the list, with a password
PROSPECT_PASSWORD='open sesame' make run ARGS="--join 203.0.113.7"         # join by address
make server ARGS="--name 'Ceres Station' --visibility public"              # a dedicated public server
PROSPECT_PASSWORD='open sesame' make server ARGS="--visibility public"     # with a password
make run ARGS="--host public --listing http://127.0.0.1:8080"              # against `make listing` on this machine
```

- **A host needs a UDP port others can reach** (default 27420; `--host-port`, or `--port` on a server): a public
  address or a forwarded port. There is no NAT traversal, and the list does not check that a game is reachable.
- The host of a local game is never asked for its password. Everyone else is.
- A name is 1 to 48 characters. A password is 1 to 64.
- The password is never sent and a captured answer opens nothing. Game traffic itself is not encrypted.
- Give the password in `PROSPECT_PASSWORD`, on the client and on the server: arguments show in process lists.
  `--password <text>` still works; `make run` and `make server` then print none of the arguments.
- `--listing <url>` picks another listing server; `--listing off` turns it off. HTTPS only, except on this machine.
- A server's settings file can hold `name`, `visibility`, `port`, `maxPlayers`, `password`, `listing` and
  `limits` under `"server"`. An argument wins over `PROSPECT_PASSWORD`, which wins over the file.
- In a game, the sector panel (`F2`) shows the server's name, its visibility and the player slots. The `Esc` menu shows
  them too, and for a public game you host, whether the listing server has it right now.
- Joining and hosting unlisted or public need a Discord sign-in in the client; private does not. `--join` and
  `--host` without one open Join Server or New Game filled in, to sign in there. A dedicated server needs none.
- In the client: New Game sets the hosting, Join Server lists public games and joins by address. A join that ends
  with "password required" or "wrong password" asks for the password on the spot; after too many wrong ones it
  counts down the server's wait before the next try.

How the password, the connection limits and the list work: [docs/hosting.md](docs/hosting.md).

## Headless social scripts

Use `--social-run <seconds>` with `--join <host[:port]>`, or without `--join` for a private local game.
This path creates no window. Other hosting and world options are not applied by the headless script.

- `--player <name>` chooses the script's callsign (default Prospector).
- `--say <text>` sends one everyone-chat line after joining.
- `--invite <name>` invites the first connected matching name other than yourself. Repeat it for more names; they go out one a second, in order.
- `--accept-invites` accepts incoming invitations; `--party-say <text>` sends one line after a party forms.
  With `--capture` instead of `--social-run`, the capturing client accepts too.
- Script exit prints roster, chat and party counts plus the last refusal. `--verbose` enables chat metadata logs.
  Routine `make run` output hides arguments containing `--say`, `--party-say` or `--password`, even with `V=1`;
  `make -n`, `make --trace` and process listings can still expose them.

Limits come from `content/net/social.json`: 256 UTF-16 units and 768 UTF-8 bytes per chat line,
four social requests at once, replenishing one per second, eight members per party, 30-second invitations,
and four party updates per second. Requests beyond the rate limit are dropped before parsing or replying.
The same allowance covers malformed requests and party operations. Control and Unicode formatting characters
are removed, whitespace is trimmed, and empty lines are refused. Chat text is never logged; accepted chat
metadata is Debug, invitation and membership changes are Information.

The roster retains connected players and up to 32 spare slots for recent departures. The chat log holds 64
lines. Names are looked up by stable session player id. Party members use content design indices for ship names;
departed members leave the party and remain marked disconnected in the roster while retained.
See [docs/social-interface.md](docs/social-interface.md) for the interface contract and wire sizes.

## Requirements

- `make`, and a GPU and driver with Vulkan 1.3
- Everything else comes from `make setup`: it detects the OS, installs the system packages (Linux: the Vulkan loader,
  `vulkaninfo` and validation layers through apt-get, dnf, pacman or zypper), installs the .NET SDK named in
  `global.json` into `~/.dotnet` without root, and restores packages. `make doctor` checks the result.
- Vulkan validation layers are optional for debug builds. Without root, `make tools` fetches them into `.tools/` on
  x86-64 Linux. Without them a debug build logs a warning and runs unvalidated.
- Apple Silicon Macs build for arm64 and render through MoltenVK; `make setup` installs it, the Vulkan loader and the
  validation layers with Homebrew.

## Running

```sh
make setup                                 # first time: SDK, system packages, restore
make run                                   # the game
make run ARGS="--start local"              # straight into a new local game (a saved world)
make run ARGS="--start continue"           # the world played last
make server                                # headless dedicated server on UDP 27420
make server ARGS="--set hullStrength=2"    # the same, in a world with its own settings
make run ARGS="--join 127.0.0.1"           # join it, after a Discord sign-in
make run ARGS="--host unlisted"            # a local game others can join by address (sign-in first)
make check                                 # formatting, warnings-as-errors build, tests
make bench                                 # release benchmark, also while resizing; fails under 144 fps or on any per-frame allocation
make listing                               # the listing server on http://127.0.0.1:8080
make listing-image                         # its container image (docker/listing.Dockerfile)
make server-image                          # the dedicated server's container image (docker/server.Dockerfile)
make help                                  # every target
```

`prospect --help` and `prospect-server --help` list the command-line options.

## Layout

| Path | Contents |
| --- | --- |
| `src/Prospect.Engine` | Fixed-step loop, job graph, thread hand-off, transports (in-process and RakNet), atomic files |
| `src/Prospect.Game` | The simulation shared by client and server: galaxy, flight model, hull storage, protocol, server host, client prediction, and the game's side of the listing server (announcer, server browser) |
| `src/Prospect.Server` | Headless dedicated server |
| `src/Prospect.Client` | Window, Vulkan renderer, cameras, input, interface |
| `src/Prospect.Listing` | Listing server: the HTTP list of public games (ASP.NET Core) |
| `tests/Prospect.Tests` | Unit and integration tests, including real client and server sessions over UDP and the real listing server in-process |
| `tests/Prospect.Listing.Tests` | The listing server run in-process and driven over HTTP |
| `docker/` | Container images |
| `content/` | Game data, loaded at run time |
| `assets/` | Fonts, branding and GLSL shaders (compiled at startup) |

## Threads

| Thread | Owns |
| --- | --- |
| Main | Window events, input, interface, Vulkan. Reads simulation snapshots; never touches simulation state. |
| ClientSim | The client's 60 Hz simulation and its connection to the server. Publishes a snapshot every tick. |
| Server | The authoritative 60 Hz simulation (in a local game, or in `prospect-server`). |
| Job workers | Work scheduled on the job graph. |
| Thread pool | Requests to the listing server: a public server's announcements and the server browser's fetch. Never the simulation or the frame loop. |

## Licences

PROSPECT is built with BRUTAL, a game development framework by Ahwoo Limited, used under the BRUTAL License v1.0.
`NOTICE.txt` ships beside the binaries, the BRUTAL logo is shown at startup and the copyright notice is on the
credits screen. Third-party licence texts, including those of the bundled fonts, are in `LICENSES/`.


Wedges are drawn, picked, collided, edited and shielded as their real shape; see
[wedges](docs/blocks-at-scale.md#wedges-protocol-27).

### Visual collision damage

Authoritative collision contacts now retain up to 16 strongest block dents per hull and recent spark metadata.
State replicates independently of poses and block losses; repair clears it. Shields prevent dents until damage reaches the hull.
Collision shapes and asteroids stay unchanged. Far clients retain state; the renderer culls detail by distance.
Nearby surviving blocks draw inward dents at the replicated contact point; recent hull/hull and hull/asteroid contacts emit short sparks.
Detail fades between 400 and 800 metres from the hull bounds and between 8 and 16 projected pixels.
Repair restores dents too, and a dent-only ship must repair before editing.
Physics, picking and shield geometry retain the original surfaces.

Client budgets and current rendering limits: [collision visuals](docs/collision-visuals.md).
Backend/API and bounded transfer costs: [hosting](docs/hosting.md#visual-collision-damage-backend-protocol-33).
Appearance and GPU performance have not yet been checked in a running client.

Pilot networking uses protocol 33: controls target explicit world intervals, and every hull advances once per interval.
Missing controls brake while standing orders, jumps and recharge continue.
Prediction replays intervals from authoritative snapshots, including missing controls; input batches no longer create extra pilot steps.
Snapshot block health lets prediction reproduce losses on isolated copies; reconciliation restores them without changing authoritative published blocks.
The client schedules controls 3–16 intervals ahead based on RTT and predicts them immediately.
Unknown remote intent and omitted state can still cause corrections. Timing limits are in [hosting.md](docs/hosting.md).

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
