# Gas clouds

Decorative, deterministic bounded gas volumes. No extraction or gameplay effects.
The shape refinement replaces the stretched, box-faded blob with a lopsided envelope, billowy edges, dense knots and real self-shadowing.
Measured results are in the refinement report. Visual acceptance is the developer's call; this is not yet an accepted milestone.

## Placement and ownership

Clouds are things in world space; see [world generation](world-generation.md) for the rules and the query API.

- `gas-clouds` is a `SectorModule` in shared `galaxy.json` with its own streams. A sector anchors at most one cloud: 4% presence, including outside the galaxy disc.
- A cloud is centred anywhere in its anchor sector and may lie across its walls. Most have an x half-extent of 4 to 5.6 km; 8% are huge, 10 to 20 km.
- The anchor's `SectorContent` holds the descriptor and count; the renderer reads only Ready slots for its galaxy. `GasCloudModule.Intersecting` lists clouds centred elsewhere.
- Positions become camera-relative only after double-precision sector translation. Density coordinates stay local to the cloud.
- Some clouds hold asteroids (`inGas` in the asteroid module): gas now changes asteroid lists near those clouds. Generation version 4 and protocol 36 cover it; the frozen fixture holds a copy of the gas rules.

## Rendering

`gas.glsl` is the one density and bounded segment integration implementation.
It clips intervals to the volume, camera near plane and selected opaque depth.
Density, per sample:

- Shape: an ellipsoid envelope at 0.6 of the box, offset by a seeded lean, warped and swollen or shrunk by up to half by the broad octave. Lopsided, never an egg; zero before the box fade.
- Coverage: the envelope sets how much of a three-octave field survives. Deep inside nearly all, near the surface only peaks, so edges break into billows and islands.
- Knots: two octaves set the amount (0.05 to 3.25), so dense knots sit among thin veils with no single straight-edged scale.
- Wisps: ridges of the fine octave, warped by the medium one, only in the fringe just outside the body.
- Noise is isotropic in metres and turned by a seeded rotation; each octave is turned again. Nothing lines up with the box axes.

Light, per sample:

- One near probe (two lookups) for local self-shadow; three over the star's chord through the envelope (two lookups and one analytic) for the bulk.
- Near probe per pair of steps, chord per group of four, each taken at the group's first step (lit or not) and held. The group index is uniform across a wave.
- Dual-lobe Henyey-Greenstein phase, a softened multiple-scattering octave, ambient near the rocks' fill level.
- The jitter is an integer hash of the pixel: no radial streaks, no hatch pattern.

With neither portals nor shields, the RGB-only gas pass runs after dust/grid/marks, just before shields (none are queued then).
Either way the overlays sit under the gas, so an unrelated portal or shield elsewhere cannot change their brightness.
Limit: dust, the grid and jump marks have no depth, so both paths attenuate them as if they were at the opaque depth behind them (or the cloud's far side over sky). Gas between the camera and a nearby mote is not modelled separately.
With any portal or shield, gas is deferred to HDR composition, before filmic mapping.
A portal elsewhere cannot switch the shield equation at an unaffected pixel:

- A pixel no lens moves reads its own scene, copy and depth texels exactly, as the ordinary pass does. A filtered read blended silhouettes.
- The scene and sky/rock fallbacks stay gas-free. The selected source gets background gas beyond the mouth, ending at its own opaque depth (sky ends at the cloud boundary).
- At most five mouths are depth-ordered into six intervals. Each interval uses only bends from nearer mouths. The first stays on the original ray; gas between mouths is retained. Portal coverage is applied at its own layer.
- Every sample uses `ftlOwnRay` / `ftlOwnReal` for the own passage, including protected own-hull pixels. Scattering is not dissolved a second time by the surface's own-passage blend. Nearer opaque resets retain camera-side own dissolve, except for the own hull.
- Shields refract only the gas interval behind their surface. The composer applies foreground transmission to shield light/deltas. Own hyperspace lifts that attenuated contribution out and smoothly restores the own shield's light above the tunnel.
- Semantic scene alpha and depth remain unchanged. No fake gas depth or jump-wide gas suppression.

The existing behind-shield colour/depth views are also tone-map bindings 6/7.
A deferred frame copies them even when no shield is drawn. Their layouts are initialized before any static descriptor use.
FrameConstants/frame.glsl append matching 112-byte gas data; only deferred frames have a nonzero budget there.
Composition uses two extra sampled descriptors and 112 additional uniform bytes per frame slot.
The ordinary pass pushes the same 112 bytes; the shield pipeline layout's push range is the larger of the two users (Vulkan guarantees 128).
The noise cache adds one immutable image/sampler and one sampled descriptor per frame slot; no render graph or history.
The ordinary pass retains its guarded pipeline module acquisition and RGB-only blend.

## Budgets and limits

Shared content controls presence, dimensions, extinction, tint, motion, samples and shape (`gas-clouds.shape` in `galaxy.json`).

Shape data, each bounded on load:

- `fill` 0.3–0.7: envelope radius as a share of the box. `lumpiness` 0–3: how far the broad octave swells or shrinks it (squared radius between 0.6 and 1.4 times).
- `warp` 0–0.2 (x half-extents) and `lean` 0–0.4 (box units): bend and seeded offset.
- Together: fill / sqrt(0.6) + lean / 2 + warp / 0.65 must not exceed 1, so a swollen surface stays inside the box face on its tightest (y) axis.
- `edge` 0.02–0.3: coverage step half-width. `knots` 0–6 and `veil` 0–0.5: dense and thin gas. `wisps` 0–2: fringe strand strength.
The content budget (16–64, 48 shipped) is the sample count for an interval at least as long as the cloud's smallest half-extent. Shorter intervals keep that step length with fewer samples; the last step is cut at the interval's end. Marches exit early at 1% transmission.
Sample-count boundaries add zero weight initially, and light probes use fixed step groups rather than the previous occupied step. This avoids those rounding-sensitive jumps between ordinary and deferred rays; the density cutoff and early termination still introduce discontinuities.
An occupied sample uses at most three density lookups and four lighting lookups. Across a full 64-step march, lighting uses at most 96 lookups before density-based skips.
CPU distance/frustum rejection precedes submission. Zero submitted clouds make no gas copy or raymarch draw.

A cloud shows up to its x half-extent plus 28% of a sector edge from its centre, fading over the last 10%.
Generation keeps any two clouds further apart than both of those reaches together: where two could show at once, the larger stays.
Thus at most one cloud has positive visibility at any camera. The eight-record queue is conservative; deferred frame data needs only one record.
A cloud of today's usual size fades from 30% to 40% of a sector edge, as before; a huge one from 68% to 78%. Both are inside the streamed block of sectors.
Extinction per metre falls with size above the standard 4.8 km half-extent, so a huge cloud is as deep to look through as an ordinary one.
A huge cloud keeps the content's sample count over a longer path: its steps are up to four times as long.
First captures of huge clouds (`133,0,-77` and `-66,1,37`, outside, edge and pilot) render without errors: a bright, soft mass with less inner structure than an ordinary cloud. Not tuned, and not run with validation layers.

Deferred tone mapping uses at most thirteen segment calls: six intervals, up to six shield attenuation calls, and own-shield lift.
A shield uses at most two refraction segments in its light pass, skipped when the bend is zero.
The mask specialization exits after the shared clipping/presence discards; offline SPIR-V inspection checks that no texture operations survive.
A fully covering external mouth initializes its layer directly, skipping all farther marches while retaining nearer intervals and opaque resets.
Loops remain bounded. Steady matched runs measure zero render/process allocations.
Frame pacing is measured on the CPU wall clock; actual GPU timestamp duration is still unmeasured.

Portal intervals accumulate screen-space deflections in depth order; each selected source keeps the existing four-texel disocclusion guard for its prefix of lenses.
This is still screen-space lensing, with mirrored offscreen lookups and reference layer depths for parallel bent rays, not physical multi-lens ray tracing.
Overlapping mouths and crossing shield silhouettes still require GPU inspection.
Overlapping shields retain the shared-background approximation; summed deltas use the nearest shield depth for gas attenuation.
These are open visual limits, not certified acceptance. Real captures showed dense haze and visible structure,
but motion/shimmer and fallback silhouettes still need acceptance review. Close-up, the gas is soft: the finest octave is about 550 m.
V3 runtime validation reported no warnings/errors through captures and automatic compositor resize.
The new immutable texture still requires those same validation checks.

## Reproducible muted inspection

Default seed `0x50524F5350454354`, sector `-3,0,5` anchors an ordinary cloud.
Half extents: `(4071,2646,3461)` m. Center approximately `(-5364.91,10668.58,-2576.74)` m relative to the sector: well off the arrival point.
`--gas-view` is refused unless `--capture` or `--bench` is present, and WorldView checks muted devices again.
It moves only the inspection camera; it never creates a fake cloud or moves ships/asteroids.

Sectors with the default seed whose arrival point is inside a cloud, for the pilot views:

- `127,-1,-127`: an ordinary cloud, half extents `(5256,3416,4468)` m, centred about 1.1 km from arrival, with rocks in it.
- `133,0,-77`: a huge cloud, half extents `(19616,12750,16674)` m, centred about 1.9 km from arrival. It reaches all 26 sectors around.
- `-66,1,37`: a huge cloud with rocks in it, half extents `(19967,12978,16972)` m, centred about 7.4 km from arrival.

- `outside` / `absent`: same camera, 2.4 z half-extents from cloud center, or nearer for a huge cloud: no further than it still shows in full. Absent disables gas submission.
- `inside` / `inside-absent`: same camera at cloud center, looking -z; full-screen volume stress.
- `edge`: camera at 92% of x half-extent; `offscreen`: outside camera facing away.
- `pilot` / `pilot-absent`: normal ship camera for opaque hull, shield and jump inspection; `--look` still works.
- Logs record requested scenario and initial framebuffer resolution; camera modes log generated descriptor and seed. Screenshot dimensions are the final resolution authority.

Use scratch XDG settings, private games and listing off. Never launch outside muted capture/benchmark mode.
The handoff report contains the exact pending commands. Full `make check`, `make bench`, GPU validation captures and matched timings are still required.

## Production-shader regression fixtures

`--gas-fixture 1..12` requires `--capture` and rejects `--bench`.
It freezes shader inputs and supplies controlled source colours/depths and homogeneous density.
It calls production `gasComposite`, sorting, opaque resets, passage functions and the numerical integrator.
It does not replace those functions with a CPU equation or a second compositor.
The renderer still selects the ordinary/deferred path and uses real frame resources.

- 1/2: fractional own rift entry/exit, nearer non-own rock, zero density on the pixel.
- 3/4: same rift cases with own-hull exemption and nonzero, passage-filtered gas.
- 5/6: identical shield/light/cloud pixel, with no portal versus an unrelated submitted portal behind the camera. Actual queued shields must be present or the fixture fails red.
- 7/8: far-first external events require production sorting. Nearest full cover skips farther marches; 8 adds a nearer opaque replacement. The shader requires exactly one nonempty segment call.
- 9–12: repeat 1–4 for wormholes.

Left three quarters show actual production-compositor output. Right quarter is green on numerical pass, red on failure; blue encodes segment calls divided by 16.
The mode/fixture tag is retained even when the renderer incorrectly selects ordinary mode, so that failure is visible.
These synthetic fixtures are **not** realistic gas captures, runtime synchronization proof, or performance benchmarks.

Fixture 13 is the real-source check the synthetic ones cannot be: real scene, density and overlays, the clock held still and no shields queued in either capture (hulls in view show without them).
Capture 00 renders the ordinary pass; only after its image is saved does an unrelated opening behind the camera switch the renderer to deferred composition for capture 01.
Each saved image has a JSON sidecar recording its actual rendered mode, gas presence, shield count, run identity and image SHA-256. `tools/check_gas_overlay.py` requires a successful ordinary/deferred pair from one run before checking pixels. Missing or stale evidence fails. It also fails if any pixel outside the menu bar and right-hand panels differs by more than 8/255. Run `make gas-overlay-tests` for evidence-rejection tests.

```fish
make run ARGS="--start local --listing off --sector 127,-1,-127 --open command --gas-fixture 13 --validation --capture /tmp/overlay.png --capture-after 5 --capture-count 2 --capture-every 2"
python3 tools/check_gas_overlay.py /tmp/overlay-00.png /tmp/overlay-01.png
```

`--open command` adds the grid and hull markers; `--gas-view outside --open hud` shows dust over the cloud only.
Jump marks are not covered: they follow the simulated jump stage, which the frozen clock cannot hold between two captures.

Staged visual checks (muted captures, validation on):

- Arrival into gas: `--sector 127,-1,-128 --gas-view pilot --open jump-rift --jump-to 127,-1,-127 --capture-after 8 --capture-count 30 --capture-every 1` (arrival about 19 s; also `--open jump`).
- Overlapping openings in gas: `--sector 127,-1,-127 --gas-view pilot --open jump-fleet-ahead --capture-after 13.5 --capture-count 8 --capture-every 0.85`.
- Camera motion: `--gas-view pilot --open hud --look -30 --look-to 30 --capture-after 4 --capture-count 24 --capture-every 1`. At 5096x1390 a capture takes about 0.8 s to save, so intervals under 1 s bunch up.
All twelve pass with the noise texture and the refined density at 5096x1390 on an RTX 5070 Ti with validation on.
The same shield pixel matched with/without the unrelated portal; both pruning cases entered one nonempty segment.
Own jumps were captured through arrival into a sector without gas and into a known cloud; two exit openings were captured overlapping in gas. Real-scene fallback branch coverage remains open.

`make gas-shaders` compiles real shaders and inspects frozen mask-specialized SPIR-V without creating a Vulkan device.
CPU `GasCompositionTests` are equation/source documentation only; `GasShaderFixtureTests` exercises input construction only.
Neither executes GLSL. The report contains commands for the separate serialized GPU fixture gate.

## Immutable noise cache

One 64³ RGBA8 UNorm lattice replaces repeated lattice hashing in the shared density function.
Eight bits lose nothing for random lattice values; filtering interpolates between them at full precision.
RGB stores the original three warp translations; alpha stores the scalar value field.
Cubic interpolation coordinates plus hardware trilinear filtering retain smooth value noise.
One lookup replaces twelve corner hashes.

- Image payload: 1 MiB. Temporary coherent staging: 1 MiB, freed after the one-time upload completes. Vulkan alignment/metadata are additional.
- No retained CPU payload, per-cloud image, frame upload or resize allocation. Two extra sampled descriptors across the two frame sets.
- Transfer-to-fragment barrier completes before descriptor publication. Renderer disposal waits for frame work before retiring the image.
- Repeat addressing makes the lattice continuous at its 64-cell boundary. The highest octave spans about 25 cells across a cloud, inside one period.

Cached lighting would need invalidation for cloud motion, light and scale changes; it was not added.
Half-resolution rendering would require new depth/portal/shield boundary reconstruction; it was not added.
The twelve composition fixtures use homogeneous input, so their rerun alone cannot validate texture quality or speed.
`GasNoiseLatticeTests` checks the actual CPU payload, packing, nondegenerate values and allocation after provisioning, not GLSL execution.
