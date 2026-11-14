# Controls: tabs, search, remapping

How the two Controls screens and the remap work. Rules for changing them are in `AGENTS.md`.

## Decided

- Two screens, one catalogue. F1 is a read-only quick reference. Settings, Controls changes bindings.
- `content/input/bindings.json` holds the defaults. `content/input/controls.json` holds tabs, labels, scopes and
  the fixed (not remappable) rows.
- The catalogue file must name every `InputAction` and `InputAxis` once. A missing one stops loading.
  `ControlsCatalogueTests` covers it.
- Tabs: Flight, Camera, Jump & navigation, Command view, Galaxy map, Scanning & exploration, Ship editor, Social, Interface.
- Search runs over every tab. No match: tab greyed, still clickable. Matches: count badge, rows lit.
- Remaps live in `ClientSettings.ControlOverrides`. Only differences from the defaults are stored.
  Key: action name, or `Strafe.Negative` / `Strafe.Positive`. Value: controls as in the bindings file, `[]` = cleared.
- Overrides apply at startup (`ActionMap.ApplyOverrides`) and at once on change: `ActionMap` is edited in place.
  `ActionMap.Changed` rebuilds cached key names (tips, social buttons, editor hints, HUD).
- Capture (`ControlCapture`) sets `ActionMap.Suspended`: every query reads "nothing pressed". It lifts at the end
  of the frame the capture finished in, so the ending press reaches nothing.
- Escape cancels a capture. A left click cancels it too, so the left button cannot be bound.
- A clash is the same control in two slots whose `scope` views overlap. Space on Boost (Pilot) and Follow (Command,
  Editor) is no clash. Choices: Swap, Clear the other, Cancel.
- Actions marked `required` (Menu / back) keep their last control.
- An action holds up to four controls. An axis half holds one.

## Limits

- Fixed: mouse gestures, Ctrl chords, the editor's `S` and `R`, Enter and Escape in text fields.
  They are not checked for clashes with remaps.
- Not remappable: sticks, triggers, the roll bumper pair.
- No chords. One press binds one control.
- A gamepad cannot cancel a capture.

## Checks

`--start settings --open controls [--controls-search text] [--controls-tab name] [--controls-sample capture|conflict]`
and `--start local --open controls [--controls-search text]`, always with `--capture`.
