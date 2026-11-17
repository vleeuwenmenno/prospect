#!/usr/bin/env python3
"""Copy a client build for drone fetch captures and the fetch benchmark.

Usage: prepare_drone_fetch_capture.py <client build folder> <folder for the copy>
The shipped ship already carries tractor beams, racks and a drone bay. The copy only jettisons containers at 6 m/s
instead of 2, so one leaves the beam's 40 m range before its 10 s pickup delay is over and only drones can bring it
back. Run the copy with --open cargo --jettison 5 --drone-fetch 12 --capture ... Shipped files are never changed.
"""
import json
import shutil
import sys
from pathlib import Path


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    source, target = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    if not (source / "content").is_dir() or target == source or target in source.parents or source in target.parents:
        raise SystemExit("Supply a client build and a separate destination folder.")
    shutil.copytree(source, target, dirs_exist_ok=True)
    cargo_path = target / "content/blocks/cargo.json"
    cargo = json.loads(cargo_path.read_text())
    cargo["space"]["jettisonSpeed"] = 6
    cargo_path.write_text(json.dumps(cargo, indent=2) + "\n")
    print(f"drone fetch capture build: {target}")


if __name__ == "__main__":
    main()
