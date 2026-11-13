# Client asteroid visibility

The GPU instance limit stays 32,768, with hull instances reserved first.
Asteroid generation, identities, authority and protocol are unchanged.

## Admission

- All ready sectors compete by projected radius, with stable sector/index ties.
- Last frame's retained rocks get a 2% radius preference to resist small cutoff oscillations.
- One fixed heap keeps only the remaining instance budget; only retained rocks build transforms.
- Point and box picking use the last rendered identity mask before the next camera update.
- Framebuffer pixels determine admission; window pixels determine pointer grab distance only.

A skipped draw or resize invalidates the old picking mask when its camera/projection no longer matches.

## Residency

Command view requests the focus sector, camera sector, focus neighbors, then camera neighbors.
The camera ring has 75 sectors; the focus neighborhood has 27, with duplicates removed.
The same desired set protects loaded slots. Generating slots are never reused.
Pilot mode drops focus protection after the command blend finishes.
Existing jump refresh behavior remains; separate destination prefetch is not implemented.

## Limits

The visible population is still bounded. A much more important incoming rock can replace a retained one.
Cold sectors still appear when generation completes; rapid movement can wait for old jobs.
The neighborhood does not cover every distant visible sector or three disjoint camera/focus/jump blocks.
Existing subpixel and LOD thresholds, depth occlusion and ordinary offscreen exits remain.
GPU captures and performance acceptance must be checked before declaring the change complete.

## Muted validation harness

`--rts-script <json> --rts-seed <n>` requires private `--capture` play with `--start local`.
It is never a saved world: `--worlds`, `--save-fault`, `--start continue|load|newgame` and `--open leave` are refused.
The run's measurement ends at the frame its last screenshot is confirmed, before the session closes.
The JSON array holds numeric `x`, `y`, `z`, `distance`, `yaw`, `pitch` (radians),
with optional `pilot`, `waitReady`, `hold` (seconds), and `isolate`.
`isolate` draws only the focus sector for whole-field inspection; it is ignored outside muted runs.
Normal sweeps leave it false. The first pose's sector/index 0 is watched throughout the run.

Readiness gates each requested screenshot when `waitReady` is true.
Fixed samples record actual camera position, resolution, counts, allocation and CPU wall duration.
CSV output goes beside the JSON only after the run; no sample formats text in the frame.
The retained reason describes instance admission, not a GPU occlusion query.
Worker completion after admission is reported as pending until the next scan.

`PROSPECT_RTS_CPU=<csv>` enables the opt-in `RtsPerformanceTests` comparison.
It measures the production scan plus transforms, and generation/summary separately, with reused storage.

Capture CSVs also mark skipped draws, track overflow and real end time, and fail incomplete recording.
Skipped draws cannot satisfy capture readiness. The optional inspection mask remains disabled in ordinary play.
A hull-exhausted instance budget clears retained masks without scanning rocks; retained transforms reuse the scanned sector center.
