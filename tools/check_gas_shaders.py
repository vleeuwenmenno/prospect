#!/usr/bin/env python3
"""Offline shader compilation and shield specialization inspection. Never creates a Vulkan device."""
import json
from pathlib import Path
import re
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
shaders = root / "assets" / "shaders"
with tempfile.TemporaryDirectory(prefix="prospect-gas-spirv-") as scratch:
    output = Path(scratch)
    files = sorted([*shaders.glob("*.vert"), *shaders.glob("*.frag")])
    for shader in files:
        subprocess.run(["glslc", "-I", str(shaders), str(shader), "-o", str(output / (shader.name + ".spv"))], check=True)
    # Check compiled production modules, not source strings: the 3D explicit-LOD lookup must survive.
    for name in ["gas.frag", "gas_half.frag", "gas_scene.frag", "gas_sky.frag", "shield.frag", "tonemap.frag"]:
        production = subprocess.check_output(["spirv-dis", str(output / (name + ".spv"))], text=True)
        if not re.search(r"OpTypeImage\s+%\S+\s+3D\b", production) or "OpImageSampleExplicitLod" not in production:
            raise SystemExit(f"{name} is missing the compiled 3D noise lookup")
    mask = output / "mask.spv"
    # Boolean specialization defaults use 'true', not the numeric syntax used by integer constants.
    subprocess.run(["spirv-opt", str(output / "shield.frag.spv"), "--set-spec-const-default-value", "1:true",
                    "--freeze-spec-const", "--eliminate-dead-branches", "-O", "-o", str(mask)], check=True)
    assembly = subprocess.check_output(["spirv-dis", str(mask)], text=True)
    samples = len(re.findall(r"\bOpImage(?:Sample\w*|Gather\w*)\b", assembly))
    kills = len(re.findall(r"\b(?:OpKill|OpTerminateInvocation)\b", assembly))
    loops = len(re.findall(r"\bOpLoopMerge\b", assembly))
    if samples != 0 or kills == 0 or loops != 0:
        raise SystemExit(f"Mask specialization retained {samples} texture operations, {kills} discard sites, {loops} loops")
    print(json.dumps({"compiled_shaders": len(files), "noise_consumers_checked": 6, "mask_texture_operations": samples,
                      "mask_discard_sites": kills, "mask_loops": loops, "mask_spirv_bytes": mask.stat().st_size,
                      "runtime_execution": False}, indent=2))
