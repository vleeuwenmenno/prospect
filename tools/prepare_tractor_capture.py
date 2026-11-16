#!/usr/bin/env python3
"""Copy a client build and fit a pod for real tractor captures.

Usage: prepare_tractor_capture.py <client build folder> <folder for the copy>
Run the copy with --open cargo --jettison 6 for pull/stow, or --open edit-cargo for reach lines.
For an active allocation benchmark: --bench 10 --open edit-cargo --jettison 5.
The second rack is deliberately hidden below the core. Shipped designs are never changed.
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
    parts = [
        ("core", [0, 0, 0]), ("cargo", [2, 1, 0]),
        ("generator", [0, -1, 0]), ("battery", [0, -2, 0]),
        ("gyro", [-1, 0, 0]), ("ionDrive", [0, 0, 1]),
        ("sensor", [0, 0, -1]), ("hull", [1, 0, 0]),
        ("hull", [2, 0, 0]), ("hull", [3, 1, 0]), ("cargo", [0, -3, 0]),
    ]
    pod = {
        "version": 5, "name": "Tractor pod", "class": "ship", "blockSize": 1,
        "color": [0.34, 0.37, 0.42],
        "blocks": [{"at": at, "type": kind} for kind, at in parts],
        "mounts": [{"on": 0, "face": "up", "at": [2, 2], "type": "tractorBeam"}],
    }
    (target / "content/ships/command_pod.json").write_text(json.dumps(pod, indent=2) + "\n")
    print(f"tractor capture build: {target}")


if __name__ == "__main__":
    main()
