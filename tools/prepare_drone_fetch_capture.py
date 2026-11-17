#!/usr/bin/env python3
"""Copy a client build and fit a pod for drone fetch captures and the fetch benchmark.

Usage: prepare_drone_fetch_capture.py <client build folder> <folder for the copy>
The pod is the tractor capture pod (prepare_tractor_capture.py: a tractor beam on the core's top face and racks)
with a drone bay left of the sensor, hatch facing left. Containers are jettisoned at 6 m/s instead of 2, so one
leaves the beam's 40 m range before its 10 s pickup delay is over and only drones can bring it back.
Run the copy with --open cargo --jettison 5 --drone-fetch 16 --capture ... Shipped designs are never changed.
For auto pickup use --drone-autopickup 3 instead of --drone-fetch: the drones go for the jettisoned container on their
own once its pickup delay is over; add --drone-popup to open the settings popup over the DRONES chip.
"""
import json
import subprocess
import sys
from pathlib import Path


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    tools = Path(__file__).resolve().parent
    subprocess.run([sys.executable, str(tools / "prepare_tractor_capture.py"), sys.argv[1], sys.argv[2]], check=True)
    target = Path(sys.argv[2]).resolve()
    path = target / "content/ships/command_pod.json"
    pod = json.loads(path.read_text())
    # Left of the sensor nothing stands, and the hatch faces open space.
    pod["blocks"].append({"at": [-1, 0, -1], "type": "droneBay", "facing": "left"})
    path.write_text(json.dumps(pod, indent=2) + "\n")
    cargo_path = target / "content/blocks/cargo.json"
    cargo = json.loads(cargo_path.read_text())
    cargo["space"]["jettisonSpeed"] = 6
    cargo_path.write_text(json.dumps(cargo, indent=2) + "\n")
    print(f"drone fetch capture build: {target}")


if __name__ == "__main__":
    main()
