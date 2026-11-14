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

- A separate sensor block automatically detects nearby ships, stations and asteroids in a 3D radius.
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

- Minimum 5,000 m; blind-safe band through 40,000 m (one sector width).
- Every current drive block has a 400,000 m maximum (ten sector widths), including the drone drive.
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
