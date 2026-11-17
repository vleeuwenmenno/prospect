#!/usr/bin/env python3
"""Copies a client build and fits the copy for the scan benchmark (`make bench-scans`).

The shipped ship carries every scanner. The copy shortens each scanner's scan and cooldown, so a short run completes
many scans instead of one. The build the copy was made from is never written to.

Usage: prepare_scan_bench.py <client build folder> <folder for the copy>
"""
import json
import shutil
import sys
from pathlib import Path

SCANNERS = ["gasScanner", "asteroidScanner", "asteroidLocator"]
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
