# Exploration and galaxy map

Planned behaviour. [Persistence](persistence.md) is the foundation for retained knowledge.

## Decided: a bounded 3D galaxy

- Keep true XYZ sectors; do not flatten the world to solve a map problem.
- Provide readable 2D layer slices and an optional tilted/3D view of the same map.
- Both views are fully interactive for selecting sectors and issuing navigation/jump intent, including up and down.
- Configure galaxy thickness at world creation, enforced by the server, with a hard floor and ceiling.
- Show clear layer navigation and playable bounds. Anonymous sectors remain selectable destinations.

Existing code already has Y layers and Page Up/Page Down navigation.
The work improves discoverability and spatial understanding; it does not introduce a missing third coordinate.
Preserve the existing pilot NAV/boost jump flow unless separately changed; map interaction is not a new bypass.

## Decided: the sector card

- Hover shows a temporary card beside the sector; clicking pins the same card for interaction.
- Clicking another sector replaces the pinned card. Empty-space click or Escape dismisses it.
- Decided: any click on the map outside the pinned card closes it. A left click on a sector then pins that sector in
  the same click (another sector replaces the card; the pinned sector stays pinned; outside the galaxy nothing is
  pinned). A right click only closes it, and opens the course menu as before.
- Decided: clicks on the card itself (its X, scan button, course rows, ship list and scrollbar) never close it. The
  legend, unit list, layer bar and top bar are surfaces the map blocks, so clicks there change no card.
- Decided: a click that closes the course menu is the menu's and changes no card; Escape closes the menu first, then the card.
- Hovering another sector while a card is pinned shows no second preview and leaves the pinned card alone.
- Move the map's top-right sector information into this card, respecting what the player knows.
- Include a readable ship list rather than only a ship count; show only contacts the player may know about.

Coordinates, distance, known sector information and navigation warnings belong together.
An unexplored sector has anonymous navigation information, not its hidden name, resources or occupants.
No mineral percentages in the card before an appropriate future mineral scan reveals them.
The pilot/RTS F2 panel is not automatically removed by this map-specific decision.

### Proposed presentation details

Show owned ships first, then other detectable contacts, with name, hull type and known current activity.
Scroll long lists within the pinned card. Unknown or stale activity must not look current.
Use above/below markers in flat view; when returning from tilt, show the selected sector's layer.
These are suggested layouts, not a settled control scheme.

### Built: first map version (tunable, not a settled control scheme)

- Hover previews a card; a click pins it; another sector's click replaces it; Escape or a click outside the galaxy edge dismisses it.
- The card lists own ships, then live contacts, then lost ones (faded, with their age). It scrolls when pinned and turns compact on short screens.
- Layer bar and Page Up/Page Down change layer; `V` or the bar tilts the map (orthographic, chosen layer stays clickable).
- Flattening shows the pinned card's layer. Other layers' items show as up/down chevrons in the flat view.
- Figures live in `content/visuals/map.json`. The galaxy edge drawn is where the disc's matter ends; nothing enforces it.

### Built: what a chart draws on the map

- A charted sector shows its name, a small muted star icon in the top right corner of its cell, and the outlines of the fields and clouds that reach it. No large star disc.
- Star icon: one shape per star class (circle, square, diamond, sparkle, asterisk) plus a dot in the star's colour. Shapes, colours, strengths: `content/visuals/map.json` (`starIcons`, `starIconSize`).
- Field outline: the real ribbon the rocks lie in (`MapFootprint.Field`: the four seeded sections, smoothstepped, tapered ends), a denser core at half the width inside it. Amber (`fieldColor`).
- Cloud outline: the envelope's fill share of the cloud's box, cut across the layer's slab, with the thin fringe round it. Violet (`cloudColor`), apart from fields and from the charted teal. The lumps are a seeded bulge: the renderer's noise is not shared, size and fill are the cloud's own.
- Rule for a feature across a wall: it is drawn only inside charted sectors. Each charted sector clips what it draws to its own cell, so the part in an uncharted neighbour stays hidden until that sector is charted, and across two charted sectors it is one whole outline.
- Outlines are worked out on a worker (`MapFeatureCache`, one job at a time, 64 sectors kept, at most 48 features of at most 24 vertices each) when a charted sector comes into view. A chart never changes, so an entry stays until its slot is needed or the galaxy changes. The frame projects the cached points, so the tilted view squashes them like everything else.
- Shown from `fieldDetailCell` (64 px a sector) up.
- The map index on the left sits under the unit list: charted, live sensors, unexplored, lost contact, asteroid field, gas cloud, the star types. Short screens drop the last rows first; where nothing fits under the unit list it sits beside it.
- Resting the pointer on one of the player's own ships (within `unitHoverPixels`) names it beside it, small and muted, no box. The sector's preview card stays away meanwhile; a pinned card is unaffected.
- The pinned card has an X in its top right corner that does what `Esc` does; its label reads PINNED to the left of it. A press on it never reaches the map.

### Decided (provisional, tunable): scan key, readout and cooldown

- `K` (D-pad left; the `ScanSector` binding, rebindable) asks for a scan of the sector the ship being flown is in, in
  the flight view, the command view and on the map. It is the card button's request: same server rules.
- A ship with no charting block still asks, so the server's refusal is shown. A running scan or a known cooldown makes
  the key do nothing.
- The key again while a scan runs **cancels** it (progress lost): the same for the card's button, which reads
  "Cancel scan" with the time left while scanning. The server cancels only the sender's own running scan; with none
  running the request is ignored. A cancel starts the cooldown like any other end.
- A scan belongs to the ship that began it (`ScanView.Hull`). The readout shows only while that ship is flown, on
  the map or off it. Switching ship ends the scan on the server as `Idle`, still with the cooldown, so nothing
  shows for it.
- Readout: a small muted strip under the top of the flight and command views (`scanStrip*` in `content/visuals/map.json`):
  bar and time left while it runs, then the result or cancellation ("SCAN CANCELLED" when the player did it) for `scanResultSeconds`, then
  "NEXT SCAN IN n S" until the cooldown ends. The cursor over it, or over the card's button, names the key.
- Tip `scan-key` (trigger `unchartedSector`): fired when the ship being flown has a charting block and its sector has no
  chart, in the flight or command view.
- Scan pulse: purely visual and only the scanning player's. It starts when the snapshot's scan goes from running to
  completed (`ScanPulse`), once per completed scan; a cancelled or refused scan, or a completion never seen running,
  starts none. Its front spreads from where the flown ship was then. Faint short pings follow the ship while a scan
  runs. All figures in `content/visuals/scan.json`; its own pass (`scan_pulse.frag`) after tone mapping, so it changes
  no gas, shield or opening composition. Gas dims it through the sensors' smooth envelope of the nearest cloud. Not on
  the map, and not inside the pilot's own jump.
- Cooldown: 10 s (`scanCooldownSeconds` in `rules/exploration.json`) on the sector a scan ended in, enforced by the
  server, starting when the scan completes or is cancelled (cancelling is no way round it). A request in it is refused
  with a reason (`ScanState.Cooldown`). Other sectors are not held. One entry per owner, not saved.

## Decided: exploration knowledge

- **Unexplored:** an anonymous destination, without surveyed contents.
- **Charted:** retained navigation and natural-feature knowledge; no automatic updates about later changes.
- **Live:** current observations inside sensor coverage, in both RTS and galaxy views.
- Leaving an area keeps learned knowledge but hides later construction, destruction, movement and mining.
- Knowledge is personal. No automatic party or alliance exploration sharing.

## Decided: charting

- Charting uses its own block, separate from passive sensors.
- The player manually starts a short scan; normal flight remains available throughout it.
- Completing the scan marks the current sector explored and produces its chart.
- The chart reveals asteroid-field locations even beyond the player's current sensor view.
- A sector scan does not expose hidden stations, live ships or detailed asteroid composition.

Nearby asteroids can be discovered through normal scouting before the scan completes.
Targeting an asteroid does not reveal detailed contents.
Scanning individual asteroids or fields for mining is deferred.

## Decided: charts as knowledge

- Each completed sector scan yields knowledge retained in that world's player record.
- Charts are copyable: sharing or selling a copy never removes the original owner's knowledge.
- Receiving a chart removes blind-jump risk for that destination; drive range still applies.
- A chart conveys navigation and surveyed natural features, not current activity or live sensor coverage.
- Later, players may explicitly share with an alliance or trade/sell copies. Sharing and trading are deferred.

## Decided: passive sensors

- A separate sensor block automatically detects nearby ships, stations and asteroids within directional 3D coverage.
- Every owned ship contributes coverage, including ships in other sectors.
- Coverage crosses sector borders seamlessly according to actual distance.
- Detecting something in a neighbouring sector does not chart that sector.
- Sensor information does not grant remote control; control range and relays remain separate work.

Preserve current T-control eligibility across all three sector coordinates, keep the map open after map control,
and keep the YOU label tied to the authoritative controlled hull. Visibility is not control permission.

## Decided: stale contacts

- Stations require sensor contact, including stations in a charted sector.
- Once detected, a station keeps a last-known marker indefinitely, clearly marked stale outside coverage.
- Destruction or other station changes become known only when observed again.
- A lost ship contact leaves a temporary marker at its last observed position; it fades and disappears.
- Lost contacts never keep tracking outside coverage. A remembered marker is not a live target.

## Decided: first equipment version

- The starter ship includes both a sensor block and a charting block.
- Start with one fixed sensor range and one fixed charting duration, defined in content.
- Extra blocks provide redundancy only; neither more blocks nor oversized blocks grant greater range or speed.
- Present simple figures: sensor range and scan duration.
- Tiers, materials, upgrade modules and capped diminishing bonuses are future ideas, not first-version requirements.

## Decided: jump limits and blind risk

- Every jump has a minimum distance to prevent micro-jumps.
- Drives impose a maximum jump distance in every direction, whether the destination is charted or not.
- Short blind jumps have a safe distance band so ordinary exploration need not cause damage.
- Longer blind jumps carry a damage risk increasing with distance, within the drive's range limit.
- The destination card warns that the jump is blind and shows its estimated risk before departure.
- Owning the destination chart removes that blind-jump risk, even when local activity is stale.

Blind-jump damage can leave a ship at 10% health, but cannot destroy it or reduce it below that floor.
A ship already below 10% takes no additional blind-jump damage.
This protection applies only to blind-jump damage; other damage remains dangerous.

### Decided: Jump here follows who flies the ship

- For the ship the player controls, Jump here plots; the pilot starts the jump through NAV/boost.
- For an owned unpiloted ship, Jump here issues an autonomous jump order.
- Plotting and starting use the same server range checks; starting checks again from the current position.
- Taking control still requires the same XYZ sector. Fleet orders remain unrestricted by control range.

### Built: the course menus and the card

- The command view's right click opens a menu at the cursor: Move here, and Plot jump or Jump here. Escape or a click elsewhere closes it.
- The menu, the map's course menu and the pinned card read one plan (`JumpPlan`, `WorldView.PlanJump`): `JumpRules.Evaluate` per selected ship.
- Too close, too far, driveless, or in transit: the row is disabled and says why.
- The jump line says charted, or blind with hit chance and damage; its warning colour is `courseRiskDanger` in `content/visuals/map.json`.
- The label says who acts: Plot jump when only the ship being flown can go, Jump here when other ships go by themselves.
- A mixed selection plots the ship being flown and orders the rest.
- A refusal from `Exploration.Navigation` shows for `courseRefusalSeconds`: on the jump strip, and under the command view's status.
- Decided: same-sector jumps are allowed when they meet the fixed minimum; client navigation must preserve the target until arrival.

## Required technical boundary

The server owns visibility, scan completion, charts, range checks and damage.
UI-only hiding is insufficient: hidden live objects must not continue arriving in ordinary snapshots.
Interest management must preserve nearby collision prediction and correct contact disappearance/reappearance.
The implementation must account for both ordinary hull snapshots and other channels that reveal positions.

Clients currently receive the galaxy seed and generate sectors locally.
Snapshot filtering alone therefore cannot make generated field locations or mineral contents secret.
Before claiming secure fog, design a bounded way to disclose needed sector data without exposing all generation secrets.
Shared deterministic generation code can remain shared even when its secret inputs are server-owned.

## Decided (provisional, tunable)

- Shared content sets sensor radius to 5,000 m, sensorless collision proximity to 250 m, scan duration to 5 s and lost ship-contact lifetime to 15 s.
- A scan cancels on sector change, control change or loss of the last live charting block; flight remains available.
- Loading discards transient scans and ship markers. Permanent charts survive.
- Each owner retains at most 4,096 charts. At capacity, refuse new sectors without evicting knowledge.
- This foundation filters dynamic live information only. Seed secrecy remains unfinished.

### Decided (provisional, tunable): jump figures

- min = fixed km; max = derived from drive blocks/modules. Minimum remains 5,000 m in content, independent of sector size.
- Blind-safe band is one applied sector width (80 km by default).
- Every current drive block has `ftlMaxSectors: 10`, including the drone drive. `BoxHullSums` converts the selected live block’s stat to metres using the world’s sector size (800 km by default).
- Future modules may add to or modify the derived maximum in that same hull arithmetic; it is not a global range limit.
- The selected live drive block supplies range; extra blocks and block size do not multiply it.
- Beyond the safe band, hit probability rises linearly from 0 to 100% at maximum range.
- A hit removes the same linear fraction times 90% of current remaining HP, capped at the floor.

The floor is 10% of the intact design's total HP, including blocks already lost.
Damage scales all surviving blocks proportionally; it loses no additional block, core or propulsion.
Already below the floor means no blind damage. Shields and ordinary damage rules are unchanged.
The server rolls once at entry into transit, using galaxy seed, hull identity and world tick.
Chart ownership is checked there too; cancelled charging has no blind damage.
UI estimates use the same curve, not knowledge of the future departure tick.

## Open: product and tuning

- Default thickness, boundary coordinates and ordinary-flight behaviour at the floor/ceiling; reconcile with the continuous-space direction.
- Card ship-row detail, map tilt controls and exact flat/tilted selection transitions.

## Open: implementation constraints

- Delivery of undiscovered natural data without seed leakage, and the minimum data needed for local collision prediction.
- Bounds for stale station records and live contact sets; no unbounded tick work.
- Shared intelligence beyond owned ships, including future alliance sensor access.

## Deferred

- Mining scans and detailed mineral surveying.
- Alliance chart-sharing UI, chart trading/selling and an economy around it.
- Equipment tiers, material scaling, modules and diminishing-return upgrades.
- Stations as relays, remote-control range and its coverage visualization.

### Decided (provisional, tunable): gas disturbs passive sensors

- Gas spends sensor range according to the column between sensor and target: reduced in every direction inside,
  further outward than inward at an edge, and shorter behind an intervening cloud.
- Shared `GasSensors.Covers` and `SampleReach` use the same smooth generated ellipsoid and exact density integral.
  The drawable contract and cache lifetime are documented in [hosting](../hosting.md#decided-provisional-tunable-gas-sensor-contract).
- `gasOpticalPenalty` in shared `rules/exploration.json` is 1.0; base range stays 5,000 m.
  These are provisional tuning numbers. Shader noise and the render-only `gasWispiness` setting never affect sensing.
- The 250 m collision proximity sphere is unaffected, with or without sensors. Own ships remain disclosed.
- Charting and generation remain unchanged.

### Decided (provisional, tunable): how coverage is drawn

- Both views draw each sensor's boundary from the shared `GasSensors.Reach`, sampled in 96 directions on the view's
  plane (the command plane, or the chosen layer's middle plane on the map). A sensor is sampled again when it
  moves 0.4 % of its range or the plane does; its gas comes from `SensorGasCache`. Where gas cuts range, a faint
  amber line (`gasRangeAlpha` in `content/visuals/map.json`) marks the plain range, on that sphere's circle only and
  once where such circles overlap. It is never a fill: thick gas can leave a fifth of the range, so a fill of the gap
  would cover most of the view. The fade outside the boundary is one faint stroke (`commandFadeRings`: 1).
- The command view shows coverage of the **selected** ships only. With nothing selected it shows the ship being
  flown, or nothing if that ship has no sensor. Selected ships with no sensor show none.
- **Radar link (display grouping only).** Selected ships whose centres are within `radarLinkMeters` (10,000 m) of
  each other, directly or through a chain, share one picture: their reach is drawn as one merged outline with no
  line inside it, and the faint fade outside follows that outline. Ships farther apart form separate groups, each
  with its own outline, never merged across groups. A faint line (`linkLineAlpha`) joins linked ships.
- Nothing on the server changes: every owned ship still reports what it sees, so a remote scout keeps working.
  Real relay limits, and what a link may carry, come later with relay stations.
- The galaxy map keeps showing every owned sensor, whatever is selected, each as its own gas-shaped region.

## Decided: lightning sensor disruption

Lightning at the sensor centre intermittently cuts its range budget, using shared `GasSensors` and server time.
Hull snapshots carry cold/lightning/disrupted flags. Proximity and ownership disclosure remain unchanged.
The renderer must consume snapshot time and invalidate stationary outlines at burst edges.
Numbers and integration contract: [gas kinds](../gas-clouds.md#decided-provisional-tunable-kinds-and-renderer-integration).
