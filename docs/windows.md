# Interface mode and windows

## Decided

- `Tab` (`ToggleInterface`, rebindable) toggles interface mode. `Tab` again or `Esc` leaves it.
- In it: the pointer is free and stays free; flight input is off; a click never reaches the world.
- A right drag begun over space (not over a window) turns the pilot camera as free look does: same sensitivity, invert and limits, wheel zooms. The hull never turns and no flight input is sent. Letting go leaves the view where it is, as free look does. A press begun on a window never turns it; a drag that leaves onto a window keeps turning until release. The pointer stays shown. Pilot view only: the command view and map are unchanged. `PointerFrame.InterfaceMode` (`Screens.InterfaceCameraDragAllowed`: interface mode is the only surface open); `FrameOrderTests` holds it.
- In it every window that is open and not pinned is shown. Leaving hides them and recaptures the pointer in the pilot view.
- Windows: Social (navigator, Friends, Party, Players, Chat, card), Charts, Gas, Cargo, Ship, Player.
- Each window has a pin. A pinned window stays on screen outside interface mode. It takes nothing in flight; hold `Shift` to click it.
- The window's X closes it and unpins it. The top bar button, or its key, opens it again.
- Charts, Cargo and Player start open. Keys: `G`, `U`, `J` (`ToggleCharts`, `ToggleCargo`, `TogglePlayerInfo`).
- Top bar in a game: Fleet, Map, Charts, Cargo, Ship, Player, Social, Settings. No Play. Fleet again returns to the pilot view.
- Charts, Cargo, Player and Social buttons open the window in front, entering interface mode. A key or button pressed on the window already in front leaves interface mode.
- Open, pinned and place of each window are saved per client in `settings.json` (`Windows`), place unscaled. A drag is saved when released.
- Player is a placeholder ("Coming soon"), by the product owner's request.

## Charts window

- Charts belong to the player (`SessionSnapshot.Exploration`); nothing holds them physically.
- Title: how many charts are owned out of the limit (`ChartsPerOwner`).
- Row: name, coordinates and layer, distance from the ship flown, star, fields, gas, how long ago surveyed ("not live").
- Gas shows the worst kind reaching the sector: lightning, cold, normal. Fields are the count that reach it (as the map card).
- Sort: nearest, newest, name. The active button turns the order round. Filter: part of a name, ignoring case.
- Distance order is measured from the centre of the sector the ship is in, so it does not shuffle as the ship moves inside it.
- Show on map: leaves interface mode, opens the map centred on the sector, its layer, card pinned.
- Plot jump: `JumpPlan` for the selected ships, or for the ship flown when none is selected; `WorldView.OrderJump`. The row says why when it cannot.
- No allocation: `ChartCatalogue` makes its arrays once for the chart limit. A row's star, fields and gas are worked out three rows a frame.

## Gas window

- Key `F4` (`ToggleGas`). Starts closed; no top bar tab. Pinnable like Charts.
- Lists the clouds the player sampled (`SessionSnapshot.Modules`), newest first. Nothing about an unsampled cloud.
- Per cloud: kind, best scanner quality, regions sampled of eight, age; each resolved gas as a bar with its share; unresolved share; purity and density (means over the regions).
- Show on map: the same as Charts.
- No allocation: `GasLedger` makes its arrays once and rebuilds only when the records change.

## Cargo window

- Key `U` (`ToggleCargo`), or the Cargo top bar tab. Starts open in interface mode; pinnable like Charts.
- Shows the flown ship's hold (`SessionSnapshot.Hulls[OwnHull].Cargo`) as a grid of size-stepped, resource-coloured tiles with size class (`XXS`..`XXXL`) and fill bar; header shows used/total volume (`u`) and total cargo mass (`kg`).
- Drones and ships without racks show an empty-state message.
- Clicking a tile shows its size, resource, mass, fill %, HP bar and rack index, with `Jettison` (`JettisonMode.Abandon`) and `Jettison, keep claim` (`JettisonMode.KeepClaim`) via `PlayerInputBridge.JettisonCargo`, disabled in FTL transit. Selection clears when `OwnCargoTick` or `Hull.Id` changes.
- The selected tile is also called out on the ship: its packed box outlined in the resource's colour, with a label (resource, kg, size class) on a leader. Pilot and command view, like the fuel callout.
- It follows the hull and clears with the selection: hold or hull change, jettison, a second click on the tile, window closed, Escape (`BackStep.CargoCallout`).
- No allocation: `CargoHoldView` keeps its tile buffer fixed.

## Ship window

- Key `I` (`ShipInfo`, D-pad up), or the Ship top bar tab. Starts closed; pinnable like Charts. Replaces the old data sheet.
- Shows the stats of the ship flown, in sections: identity, flight, power, fuel, shield, cargo, ore, drones, modules. A section with nothing to show is left out.
- ORE (a hull with ore bins): what the bins hold of what they can, then a row a bin: its ore, grade, kilograms and capacity, in the ore's colour; an empty bin says so. Read from `HullView.Ore`, which the server sends for the hull flown only; any other hull shows its bins' count and capacity. The Ore Cargo window is not built.
- Every figure is the hull's published sheet (`HullView.Stats`), its design's blocks, or the flown hull's live state. Nothing is worked out in the window.
- Fuel, battery, shield now, cargo carried and ore held are told only for the hull flown. The rest show for any hull.
- In the command view it follows the first selected hull; with only an asteroid selected it shows that asteroid's survey (scan findings only).
- Not shown: minutes of fuel at full thrust (no shared figure yet); fuel time is at the current burn.
- Sections are a fixed list (`ShipSheet.Section`, drawn in `DrawShipRows`). A new section is one more entry and one draw call.

## Checks

- `--charted <n>` (with `--capture` or `--bench`, no `--worlds`) charts every sector within n of the start, for captures.
- `--open interface`, `charts`, `pinned-charts`, `cargo-window`, `info` (the Ship window) (pair with `--cargo-sample` for a populated hold), `gas`, `pinned-gas`.

## Open

- Flight keys stay off in interface mode, as in the old social workspace. Letting the hull fly while the pointer is free is not decided.
- A filter by fields or gas, and a chart's own notes, are not built.
