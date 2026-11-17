#!/usr/bin/env python3
"""Copy a client build and give its pod a drone bay, for drone captures and the drone benchmark.

Usage: prepare_drone_capture.py <client build folder> <folder for the copy>
Run the copy with --open drones --capture ... (three drones launch 1.5 s in and are called home at
--drone-recall, 14 s by default), with --bench 25 --open drones for the allocation benchmark, or with
--open edit --capture ... to see the bay and its hatch in the editor. The bay sits left of the pod's sensor with
its hatch facing left, into open space. Shipped designs are never changed.
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
    # Left of the sensor nothing stands, and the sensor carries no mounted part on that face.
    pod["blocks"].append({"at": [-1, 0, -1], "type": "droneBay", "facing": "left"})
    path.write_text(json.dumps(pod, indent=2) + "\n")
    print(f"drone capture build: {target}")


if __name__ == "__main__":
    main()
