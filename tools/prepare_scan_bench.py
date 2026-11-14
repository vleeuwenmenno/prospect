#!/usr/bin/env python3
"""Copies a client build and fits the copy for the scan benchmark (`make bench-scans`).

The shipped command pod carries no scanner, so a scan can only run in a copy whose pod does. The copy also
shortens each scanner's scan and cooldown, so a short run completes many scans instead of one. The build
the copy was made from is never written to.

Usage: prepare_scan_bench.py <client build folder> <folder for the copy>
"""
import json
import shutil
import sys
from pathlib import Path

SCANNERS = ["gasScanner", "asteroidScanner", "asteroidLocator"]
# Beside the pod's own blocks, on cells it leaves free.
CELLS = [[2, 0, 0], [-2, 0, 0], [0, -1, 0]]
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
    for scanner, cell in zip(SCANNERS, CELLS):
        if tuple(cell) in taken:
            raise SystemExit(f"cell {cell} of the command pod is taken; choose another for {scanner}")
        pod["blocks"].append({"at": cell, "type": scanner})
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
