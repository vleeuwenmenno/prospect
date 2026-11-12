<img src="assets/icon.svg" width="64" height="64" alt="">

# PROSPECT

A multiplayer space sandbox: you command from a pod, mine asteroids, build hulls and run a fleet across star
systems. C# on .NET 10, built on the [BRUTAL](https://github.com/ahwootech/BRUTAL) framework (Vulkan, GLFW,
Dear ImGui, RakNet). Windows x64, Linux x64 and Apple Silicon macOS.

Singleplayer is a server running inside the game process; multiplayer is the same server as a separate binary,
or that local game opened to other players. There is one simulation and one code path.

## Status

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
solid-box evaluation and running sums, with seeded 4,096-block and 150,000-block fixtures.
See [docs/blocks-at-scale.md](docs/blocks-at-scale.md) for measurements and the remaining stages.

Milestone 1 (skeleton) is complete. On top of it, the first slice of the world is in:

- **Sectors.** The galaxy is a grid of 40 km cells generated from the server's seed: name, star, character and
  contents. Nothing is stored or sent, and nothing stops at a sector's edge. The client keeps the sectors around
  the camera generated on worker threads.
- **World generation.** A sector is filled by a list of modules named in `content/galaxy/galaxy.json`, each
  drawing from its own random stream, so adding or retuning one never changes what another makes. Asteroid fields
  are the only module so far. The same seed gives the same world to the last bit on every machine.
- **Sector character.** Every sector has a richness, from barren to very rich, and a set of resources that can
  occur there. Rich and poor space come in regions. Some sectors have every resource, some none, most a few; the
  sector panel and the map (for a discovered sector) show both.
- **Asteroid fields.** A sector has no field, a thin scatter, a cluster or a dense belt: up to 4,096 asteroids.
  Sizes run from pebbles 6 m across to giants 6 km across; most are tiny and a giant is rare. The middle of every
  sector, where ships arrive, is kept clear.
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
  whether the dampeners are on; off is amber. Heads-up panels share the unit list's look: a muted dark panel, a
  thin quiet border and a small tick in the colour of their state. Nothing glows.
- **Collisions and damage.** Hulls collide block by block against each asteroid's real surface, and block
  against block with each other. An impact bounces and turns the hull; damage grows with the square of the speed
  over a safe limit and lands on the blocks that touched, after the shield. A block at zero is gone, and the hull
  flies on what is left. Losing the core destroys the hull; its pilot gets out in a drone.
- **Shields break and restart.** A shield that pays out everything it had breaks and goes offline. Offline it
  takes no hits, even with charge in it: every hit lands on the blocks. After the usual 4 s without a hit it
  charges back at its normal rate on twice its normal power, and comes online only at 25% of its capacity
  (`shieldRestartFraction` and `shieldRestartPowerMultiplier` in `content/blocks/blocks.json`). Online it tops up
  as before. The console says `OFFLINE` in red and dims the charge being rebuilt; target and fleet gauges dim too,
  and the party list shows a broken shield as empty. Snapshots carry the offline state and the newest absorbed hit
  (where, how hard, how long ago), so every client agrees on both.
- **Shield looks.** Every hull with a shield shows a faint, muted cyan shell a margin off its blocks, built from the
  blocks it still has, so it fits any design and any damage. It shimmers and bends what is behind it by a few
  pixels, most at its rim. An absorbed hit spreads a ripple from where it landed, on the pilot's own hull and on
  everyone else's, aged from when the server says it happened (a hit long over is never shown on joining). Near
  breaking it tints stronger with slow waves; a break, or the loss of the last emitter, collapses it red and it is
  gone until the simulation brings it back online, when it fades in. Collapses and fade-ins are timed from when
  the server says the change happened: a remote one waits for the hull as drawn, an old one is not replayed.
  Durations, colours, ripple speed and width, the wavering threshold and the bend are in
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
  Base time and speed are content tuning; preparation and arrival are unchanged, with no distance energy cost or gameplay range limit. Taking the controls
  before the drive fires cancels the jump and loses the charge. The client generates the destination while the
  drive charges.
- **World settings.** A world has settings, chosen when a server or a local game starts: collision damage,
  shields against collisions (full, weak, none), shield recharge, jump charge speed and hull strength. The
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
- **Galaxy map.** Every grid cell is a sector. Zoomed out it shows the whole galaxy; closer, the sector grid; closest,
  each sector's star, asteroids and name. A sector's contents are shown only after one of your ships has been
  within a sector of it. Right-click a sector to choose how to get there: move under engines, or jump with the
  faster-than-light drive (only ships that have a working drive jump). A unit list on the left shows every ship you
  own, which are selected, its sector and what it is doing; click a row to add or remove that ship. A plotted
  jump shows as a dotted route from the ship you fly to its NAV point, on the layer both are on, until the jump
  is made, replaced or cleared. The card for the sector under the pointer sits in the top right corner.
- **Renderer.** Floating-point scene target and tone mapping, camera-relative rendering from double-precision
  positions, a star field and sky, instanced hull and asteroid meshes. Asteroids have five levels of detail, from
  a speck in the distance to a giant seen from its surface.

- **Hosting.** A game is private (the host alone, no network listener), unlisted (joined by address) or public
  (also on the list). A local game can be hosted for others: the same in-process server, listening on UDP as
  well. A server has a name its host chooses. The New Game screen sets who can join, the name, a password, the
  player slots and the port; see [Hosting and joining](#hosting-and-joining) and [docs/hosting.md](docs/hosting.md).
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
saving. Stations and gas fields are not generated yet; asteroids do not crack into pieces, and ore shows only as
a tint. Asteroids come in a shared set of shapes until mining gives each one its own body. Designs are limited to 64
unit-sized blocks for now, because block state is sent as one bit per block. Sized boxes and larger designs
are refused by the live loader until stage 2 adds storage, wire support, collision and drawing.

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
| Show the cursor (pilot view) | Hold `Shift`; with it, right-drag over space turns the camera | |
| Dampeners on / off | `Z` | `X` |
| Command view | `N` or `F9` | `Back` |
| Galaxy map | `M` | `Y` |
| Halt (cancel order) | `H` or `Backspace` | `B` |
| Ship data sheet | `I` | D-pad up |
| Target the ship under the reticle, or the asteroid if there is no ship | Middle mouse button | |
| Take control of the target, or leave in a drone | `T` | D-pad down |
| Menu or back | `Esc` | `Start` |
| Social: Players window | `Tab` or `P` | |
| Social: Party window | `O` | |
| Chat: open, send, close | `Enter`; `Enter` sends, `Esc` closes | |
| Close the focused social window, then Social | `Esc` (never a pinned window) | |

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

Galaxy map:

| | |
| --- | --- |
| Select ships | Left-click a ship, or drag a box; `Shift` or `Ctrl` adds to the selection |
| Add or remove one ship | Click its row in the unit list; **All** and **Clear** change the whole list |
| Set course | Right-click a sector, then pick **Move here**, or **Plot jump** (the ship you fly) / **Jump here** (selected fleet ships) in the menu |
| Close the menu | Left-click elsewhere, or `Esc` (a second `Esc` closes the map) |
| Pan / zoom | `W` `A` `S` `D`, arrow keys or middle-drag / mouse wheel |
| Change layer, centre on your ship | `Page Up` / `Page Down`, `Space` |

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

`F3` shows the
performance overlay, `F11` toggles fullscreen, `F12` takes a screenshot. Bindings live in
`content/input/bindings.json`; block types and flight rules in `content/blocks`, hull designs in `content/ships`,
galaxy rules in `content/galaxy`, world settings in `content/rules`, the listing server's address in `content/net`.

## World settings

Settings are fixed for the life of a server or a local game. Defaults and ranges live in
`content/rules/settings.json`.

| Id | Values | Default | What it does |
| --- | --- | --- | --- |
| `collisionDamage` | 0 to 4 | 1 | Multiplies collision damage. At 0 collisions push but never damage. |
| `collisionShields` | `Full`, `Weak`, `None` | `Full` | `Full`: the shield absorbs a collision. `Weak`: it absorbs it but drains four times as fast. `None`: collisions pass the shield and hit the blocks. |
| `shieldRecharge` | 0.25 to 4 | 1 | Multiplies how fast shields recharge. |
| `jumpChargeSpeed` | 0.25 to 4 | 1 | Multiplies how fast a jump drive charges. A jump takes the same energy. |
| `hullStrength` | 0.25 to 4 | 1 | Multiplies every block's hit points. |

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
  there are no saved presets.
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
- In a game, the sector panel shows the server's name, its visibility and the player slots. The `Esc` menu shows
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
make run ARGS="--start local"              # straight into a local game
make server                                # headless dedicated server on UDP 27420
make server ARGS="--set hullStrength=2"    # the same, in a world with its own settings
make run ARGS="--join 127.0.0.1"           # join it, after a Discord sign-in
make run ARGS="--host unlisted"            # a local game others can join by address (sign-in first)
make check                                 # formatting, warnings-as-errors build, tests
make bench                                 # release benchmark; fails under 144 fps or on any per-frame allocation
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
