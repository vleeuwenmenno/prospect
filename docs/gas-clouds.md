# Gas clouds

Decorative, deterministic bounded gas volumes. No extraction or gameplay effects.
The shape refinement replaces the stretched, box-faded blob with a lopsided envelope, billowy edges, dense knots and real self-shadowing.
Measured results are in the refinement report. Visual acceptance is the developer's call; this is not yet an accepted milestone.

## Placement and ownership

- `gas-clouds` is an independent `SectorModule` in shared `galaxy.json`; its stream never changes asteroid bytes.
- At most one descriptor per sector; 4% presence, including outside the galaxy disc. Placement does not depend on asteroid settings.
- Descriptor and count live in worker-owned `SectorContent`. The renderer no longer reads them from there: it reads the cloud list below.
- Positions become camera-relative only after double-precision sector translation. Density coordinates stay local to the cloud.
- No `ProtocolInfo.Version` or `Galaxy.GenerationVersion` bump: gas adds no wire field and draws from its own streams, so no asteroid list changes. The `galaxy.json` module is covered by the content identity. The frozen asteroid fixture/hash remains unchanged.

## Seen from far away

Decided. A cloud is seen from several sectors away, at one of three levels of detail, with no step between them.

Which clouds exist:

- `IGasCloudSource` answers "which clouds are known around this sector". It is the one place fog of war will narrow.
- `GeneratedGasClouds` is the only source today: every cloud the seed generates, from `GasCloudQuery` in `Prospect.Game`.
- `GasCloudQuery` runs the gas module's own two steps only. No asteroid is generated and nothing is sent.
- `GasCloudField` keeps the list on a worker, one job at a time, and remakes it when the camera changes sector. The frame never waits.
- The list reaches two sectors past the view range and holds at most 512 clouds, nearest sectors first.

Levels, by distance in radii (distance to the centre over the largest half-extent), so a cloud of any size changes level at the same size on screen:

| Level | Radii, shipped | Work per pixel | Where it is drawn |
| --- | --- | --- | --- |
| Full | up to 3, gone by 4 | today's march: 48 steps, at most 216 lookups | in the scene, or composed (one cloud) |
| Middle | to 9, gone by 12 | 24 steps of the full density, light probes once per pair: at most 96 lookups | in the scene, stopping at what is solid, no copy |
| Far | to the view range | 10 steps, finest octave smoothed away, same light: at most 30 lookups | in the sky, before anything solid |

- Hand-over: both levels are drawn, each with a weight; the weights add to one. The weight thins the cloud to the view only, never to its own light, so shading does not change during a hand-over.
- View range: 5 sectors (200 km), fading over its last 30%. The old fade at 30–40% of a sector is gone.
- Every draw is cut to the cloud's outline on screen (scissor), so a far cloud costs its own pixels, not the screen.
- All figures are client data in `content/visuals/gas.json` (`GasTiers`), bounded on load. The full level's sample count stays in `galaxy.json`.
- The full level reaches at least 2 radii (`nearEnd`): the other levels assume a camera outside the cloud's box.

Why these three:

- Full is unchanged, so the near look is exactly what it was.
- Middle keeps every density octave. At 3–9 radii the finest octave is still 15–45 pixels wide; dropping it there would show.
- Far uses the same density with its finest octave replaced by its average. The outline stays the same shape, so the hand-over does not morph.
- A light from the smooth envelope alone (no lookups) was tried for far and rejected: it shaded the cloud visibly more than the probes.
- A half-resolution target was rejected again: it needs new depth, lens and shield edge reconstruction and a resize path.

Many clouds at once, the order:

- A frame queues at most 32 draws (`maxDraws`, 64 at most) plus the primary. When full, the farthest is left out.
- Whole clouds are laid over each other, farthest centre first. Gas of two overlapping clouds is not interleaved along a ray.
- The primary is the nearest cloud at the full level. It is always drawn last, and it is the one cloud composition takes over.

Many clouds at once, where each is drawn:

- Far draws that are behind every other cloud go into the sky: over the stars, under rocks and hulls. The lens's sky copy and the scene behind the shields hold them, at no extra copy.
- Any other draw is in the scene after the overlays, before the shields.
- Middle and far draws there read the scene's own depth, attached read-only meanwhile (`gas_scene.frag`): nothing is copied for them. A full cloud needs the pilot's hull mask, so it reads a scene copy, taken once a frame.

Half-extents up to a full sector edge are supported by construction: levels, bounds and step lengths are relative to the cloud, positions are camera-relative.
No cloud that large is generated yet, so this is checked by unit tests only (`GasLevelsTests`), not by a capture.

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
Far clouds alone make no copy either: they are drawn into the sky.

Several clouds can be in view; see "Seen from far away" for the queue and the order.
Deferred frame data still holds one record: the primary cloud. Every other draw is part of the scene the composition reads.

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
Open limits of far gas, layering:

- A rock inside or behind a cloud drawn into the sky is not dimmed by it. Rocks are small or not drawn at those distances.
- A shield or an opening inside a cloud at the middle or far level, or inside a second full cloud, is drawn over that cloud's gas, not through it.
- A lens's sky and rock fallbacks hold far gas but not middle gas or a second full cloud.
- When the nearest full cloud changes, two overlapping thick clouds swap order at once.

Open limits of far gas, look:

- Far and middle levels show more grain at thin edges: fewer steps, the same per-pixel jitter, no accumulation over frames.
- Every cloud is lit by the star of the camera's sector, whatever sector the cloud is in.
- Motion shimmer of the far and middle levels, and the look of a sector-sized cloud, have not been reviewed.

These are open visual limits, not certified acceptance. Real captures showed dense haze and visible structure,
but motion/shimmer and fallback silhouettes still need acceptance review. Close-up, the gas is soft: the finest octave is about 550 m.
V3 runtime validation reported no warnings/errors through captures and automatic compositor resize.
The new immutable texture still requires those same validation checks.

## Reproducible muted inspection

Default seed `0x50524F5350454354`, sector `-3,0,5` has a cloud.
Half extents: `(4800,3120,4080)` m. Center approximately `(-536.49,1066.86,-257.67)` m relative to the sector.
`--gas-view` is refused unless `--capture` or `--bench` is present, and WorldView checks muted devices again.
It moves only the inspection camera; it never creates a fake cloud or moves ships/asteroids.

- `outside` / `absent`: same camera, 2.4 z half-extents from cloud center; absent disables gas submission.
- `--gas-distance <radii>` with either: the camera's distance in largest half-extents, 0.1 to 200. `--gas-distance-to <radii>` moves it there over the captures in equal ratios: `--gas-distance 2.6 --gas-distance-to 30 --capture-count 12 --capture-every 0.7` crosses both hand-overs.
- Each capture prints how many draws the frame queued at each level and whether the primary was composed.
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
make run ARGS="--start local --listing off --sector -3,0,5 --open command --gas-fixture 13 --validation --capture /tmp/overlay.png --capture-after 5 --capture-count 2 --capture-every 2"
python3 tools/check_gas_overlay.py /tmp/overlay-00.png /tmp/overlay-01.png
```

`--open command` adds the grid and hull markers; `--gas-view outside --open hud` shows dust over the cloud only.
Jump marks are not covered: they follow the simulated jump stage, which the frozen clock cannot hold between two captures.

Staged visual checks (muted captures, validation on):

- Arrival into gas: `--sector -3,0,4 --gas-view pilot --open jump-rift --jump-to -3,0,5 --capture-after 8 --capture-count 30 --capture-every 1` (arrival about 19 s; also `--open jump`).
- Overlapping openings in gas: `--sector -3,0,5 --gas-view pilot --open jump-fleet-ahead --capture-after 13.5 --capture-count 8 --capture-every 0.85`.
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
