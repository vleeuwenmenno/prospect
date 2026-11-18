#!/usr/bin/env python3
"""Copy a client build for advanced locator captures.

The shipped ship carries two basic asteroid locators. This copy carries advanced locators in their place, so a pulse
can be filtered and every mark can show its rock's diameter. A ship takes locators of one kind only, so the copy
replaces both of the ship's; nothing else changes and the build the copy was made from is never written to.

Usage: prepare_locator_capture.py <client build folder> <folder for the copy>

Captures (`--locator-diameter` and `--locator-ores` are scripted only, and need `--capture`):
  --open locator-popup --capture p.png --capture-after 3
      the chip's filter popup over the LOCATOR chip.
  --open command --module locator --module-at 2.5 --locator-ores iron,nickel --locator-diameter 0,400
      --capture m.png --capture-after 9
      a pulse that reports only iron and nickel rocks no wider than 400 m, each mark with its diameter.
  --open edit-advanced-locator --capture e.png --capture-after 4
      the editor holding an advanced locator's ghost on the pod, which already carries basic ones: refused.
"""
import json
import shutil
import sys
from pathlib import Path

BASIC = "asteroidLocator"
ADVANCED = "advancedLocator"


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    source, target = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    if not (source / "content").is_dir() or target == source or target in source.parents or source in target.parents:
        raise SystemExit("Supply a client build and a separate destination folder.")
    shutil.copytree(source, target, dirs_exist_ok=True)

    ship_path = target / "content/ships/prospector.json"
    ship = json.loads(ship_path.read_text())
    replaced = 0
    for mount in ship["mounts"]:
        if mount["type"] == BASIC:
            mount["type"] = ADVANCED
            replaced += 1
    if replaced == 0:
        raise SystemExit(f"{ship_path} has no {BASIC} to replace")
    ship_path.write_text(json.dumps(ship, indent=2) + "\n")
    print(f"advanced locator capture build: {target} ({replaced} locators replaced)")


if __name__ == "__main__":
    main()
