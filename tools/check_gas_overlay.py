#!/usr/bin/env python3
"""Compares the two captures of `--gas-fixture 13` (ordinary gas pass, then deferred composition).

Dust, the command grid, jump marks and gas must look the same in both: only the renderer's gas mode changed.
Pixels that differ by more than 8/255 in any channel fail, outside the menu bar and the right-hand panels,
whose performance figures change between captures. Rounding between the two paths stays well below that.
"""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

if len(sys.argv) != 3:
    raise SystemExit("usage: check_gas_overlay.py <capture-00.png> <capture-01.png>")
first, second = sys.argv[1:]
metadata = []
for index, name in enumerate((first, second)):
    path = Path(name)
    try:
        record = json.loads(Path(name + ".json").read_text())
        digest = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    except (OSError, ValueError) as error:
        raise SystemExit(f"missing or invalid capture evidence: {error}")
    expected = {"Index": index, "Deferred": bool(index), "Opening": bool(index),
                "HasGas": True, "Shields": 0, "Saved": True, "Sha256": digest}
    if any(record.get(key) != value for key, value in expected.items()) or not record.get("Run"):
        raise SystemExit(f"capture {index} has invalid mode, gas, save status or image hash")
    metadata.append(record)
if metadata[0]["Run"] != metadata[1]["Run"]:
    raise SystemExit("captures belong to different runs")
size = subprocess.check_output(["magick", "identify", "-format", "%w %h\n", first], text=True).split()
width, height = int(size[0]), int(size[1])
if subprocess.check_output(["magick", "identify", "-format", "%w %h\n", second], text=True).split() != size:
    raise SystemExit("captures differ in size")
# The scene, without the 40-pixel menu bar and the panels in the right 8.8% of the window.
crop = f"{int(width * 0.912)}x{height - 40}+0+40"
count = subprocess.check_output([
    "magick", first, second, "-compose", "difference", "-composite", "-crop", crop, "+repage",
    "-separate", "-evaluate-sequence", "max", "-threshold", str(100 * 8.5 / 255) + "%",
    "-format", "%[fx:round(mean*w*h)]", "info:",
], text=True).strip()
print(f"{count} pixels differ by more than 8/255 in {crop}")
sys.exit(0 if int(float(count)) == 0 else 1)
