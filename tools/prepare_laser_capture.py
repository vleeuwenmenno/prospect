#!/usr/bin/env python3
"""Copy a client build and fit its pod with mining lasers for captures and the benchmark.

Usage: prepare_laser_capture.py <client build folder> <folder for the copy>
Run the copy with --start local --open laser (or laser-command) and --capture or --bench.
The pod gets a laser in group 1 on the front face of its hyperdrive, aimed along the nose, and one in group 2 on
the underside of its charting block, which the forward aim leaves outside its gimbal limit. Shipped designs are
never changed.
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
    path = target / "content/ships/command_pod.json"
    pod = json.loads(path.read_text())
    blocks = pod["blocks"]

    def index(kind: str) -> int:
        for i, block in enumerate(blocks):
            if block["type"] == kind:
                return i
        raise SystemExit(f"The pod has no {kind} block.")

    pod["version"] = 7
    pod["mounts"].append({"on": index("hyperdrive"), "face": "front", "at": [2, 2], "type": "miningLaser", "group": 1})
    pod["mounts"].append({"on": index("charting"), "face": "down", "at": [2, 2], "type": "miningLaser", "group": 2})
    path.write_text(json.dumps(pod, indent=2) + "\n")
    print(f"laser capture build: {target}")


if __name__ == "__main__":
    main()
