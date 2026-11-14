# Interface mode and windows

## Decided

- `Tab` (`ToggleInterface`, rebindable) toggles interface mode. `Tab` again or `Esc` leaves it.
- In it: the pointer is free and stays free; flight input is off; a click never reaches the world.
- In it every window that is open and not pinned is shown. Leaving hides them and recaptures the pointer in the pilot view.
- Windows: Social (navigator, Friends, Party, Players, Chat, card), Charts, Inventory, Player.
- Each window has a pin. A pinned window stays on screen outside interface mode. It takes nothing in flight; hold `Shift` to click it.
- The window's X closes it and unpins it. The top bar button, or its key, opens it again.
- Charts, Inventory and Player start open. Keys: `G`, `U`, `L` (`ToggleCharts`, `ToggleInventory`, `TogglePlayerInfo`).
- Top bar in a game: Fleet, Map, Charts, Inventory, Player, Social, Settings. No Play. Fleet again returns to the pilot view.
- Charts, Inventory, Player and Social buttons open the window in front, entering interface mode. A key or button pressed on the window already in front leaves interface mode.
- Open, pinned and place of each window are saved per client in `settings.json` (`Windows`), place unscaled. A drag is saved when released.
- Inventory and Player are placeholders ("Coming soon"), by the product owner's request. Inventory arrives with mining.

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

## Checks

- `--charted <n>` (with `--capture` or `--bench`, no `--worlds`) charts every sector within n of the start, for captures.
- `--open interface`, `charts`, `pinned-charts`, `inventory`.

## Open

- Flight keys stay off in interface mode, as in the old social workspace. Letting the hull fly while the pointer is free is not decided.
- A filter by fields or gas, and a chart's own notes, are not built.
