#!/usr/bin/env python3
"""Copies a client build and fits the copy for the scan benchmark (`make bench-scans`).

The shipped command pod carries no scanner, so a scan can only run in a copy whose pod does. Scanners are face
mounts, so the copy adds a plating block for each and mounts the scanner on a face of it. The copy also
shortens each scanner's scan and cooldown, so a short run completes many scans instead of one. The build
the copy was made from is never written to.

Usage: prepare_scan_bench.py <client build folder> <folder for the copy>
"""
import json
import shutil
import sys
from pathlib import Path

SCANNERS = ["gasScanner", "asteroidScanner", "asteroidLocator"]
# Beside the pod's own blocks, on cells it leaves free and no mounted thruster stands in.
CELLS = [[1, 0, 0], [-1, 0, 0], [0, 1, 2]]
# The faces a scanner may sit on, with the neighbouring cell each one looks at; the first whose cell is free is used.
FACES = [("up", (0, 1, 0)), ("right", (1, 0, 0)), ("left", (-1, 0, 0)), ("front", (0, 0, 1)), ("back", (0, 0, -1)), ("down", (0, -1, 0))]
# A scanner's side is five tenths of a cell; this puts it in the middle of the face.
MIDDLE = [2, 2]
SCAN_SECONDS = 0.5
COOLDOWN_SECONDS = 1


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    source, target = Path(sys.argv[1]), Path(sys.argv[2])
    if not (source / "content").is_dir():
        raise SystemExit(f"{source} is not a client build: it has no content folder")
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)

    pod_path = target / "content" / "ships" / "command_pod.json"
    pod = json.loads(pod_path.read_text())
    taken = {tuple(block["at"]) for block in pod["blocks"]}
    taken.update(tuple(cell) for cell in CELLS)
    pod["version"] = 6
    mounts = pod.setdefault("mounts", [])
    for scanner, cell in zip(SCANNERS, CELLS):
        if any(tuple(block["at"]) == tuple(cell) for block in pod["blocks"]):
            raise SystemExit(f"cell {cell} of the command pod is taken; choose another for {scanner}")
        pod["blocks"].append({"at": cell, "type": "hull"})
        face = next((name for name, step in FACES if tuple(c + d for c, d in zip(cell, step)) not in taken), None)
        if face is None:
            raise SystemExit(f"every face of cell {cell} is covered; choose another for {scanner}")
        mounts.append({"on": len(pod["blocks"]) - 1, "face": face, "at": MIDDLE, "type": scanner})
    pod_path.write_text(json.dumps(pod, indent=2) + "\n")

    blocks_path = target / "content" / "blocks" / "blocks.json"
    catalogue = json.loads(blocks_path.read_text())
    for scanner in SCANNERS:
        module = catalogue["blocks"][scanner]["module"]
        module["scanSeconds"] = SCAN_SECONDS
        module["cooldownSeconds"] = COOLDOWN_SECONDS
    blocks_path.write_text(json.dumps(catalogue, indent=2) + "\n")
    print(f"scan benchmark build: {target}")


if __name__ == "__main__":
    main()
