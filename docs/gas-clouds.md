# Gas clouds

Deterministic bounded gas volumes. Gas attenuates passive sensors through a shared smooth envelope; see
[the sensor contract](hosting.md#decided-provisional-tunable-gas-sensor-contract). No extraction.
The shape refinement replaces the stretched, box-faded blob with a lopsided envelope, billowy edges, dense knots and real self-shadowing.
Measured results are in the refinement report. Visual acceptance is the developer's call; this is not yet an accepted milestone.

## Placement and ownership

Clouds are things in world space; see [world generation](world-generation.md) for the rules and the query API.

- `gas-clouds` is a `SectorModule` in shared `galaxy.json` with its own streams. A sector anchors at most one cloud: 4% presence, including outside the galaxy disc.
- A cloud is centred anywhere in its anchor sector and may lie across its walls. Most have an x half-extent of 4 to 5.6 km; 8% are huge, 10 to 20 km.
- The anchor's `SectorContent` holds the descriptor and count. The renderer does not read them from there: it reads the cloud list below, which names every cloud by its anchor.
- Positions become camera-relative only after double-precision sector translation. Density coordinates stay local to the cloud.
- Some clouds hold asteroids (`inGas` in the asteroid module): gas now changes asteroid lists near those clouds. Generation version 4 and protocol 36 cover it; the frozen fixture holds a copy of the gas rules.

## Seen from far away

Decided. A cloud is seen from several sectors away, at one of three levels of detail, with no step between them.

Which clouds exist:

- `IGasCloudSource` answers "which clouds are known around this sector". It is the one place fog of war will narrow.
- `GeneratedGasClouds` is the only source today: every cloud the seed generates.
- It asks `GasCloudModule.TryGetCloud` for each anchor sector, nearest first (`GasCloudQuery.Collect`). No generation rule is repeated, no asteroid is generated and nothing is sent.
- A cloud is listed once, by its anchor, whatever its size: a huge one that lies across sector walls is found like any other.
- `GasCloudField` keeps the list on a worker, one job at a time, and remakes it when the camera changes sector. The frame never waits.
- The list reaches two sectors past the view range, measured to a cloud's centre, and holds at most 512 clouds, nearest sectors first.

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
- The primary cloud alone is marched at half resolution; see "Half-resolution march". The other levels already cost little.

Renderer order:

- A frame queues at most 32 draws (`maxDraws`, 64 at most) plus the primary. When full, the farthest is left out.
- Whole clouds are laid over each other, farthest centre first. The only clouds that can overlap, a cold and a normal one, are one draw and are integrated together along each ray (see "Drawing the kinds").
- The primary is the nearest cloud at the full level. It is always drawn last, and it is the one cloud composition takes over.

Many clouds at once, where each is drawn:

- Far draws that are behind every other cloud go into the sky: over the stars, under rocks and hulls. The lens's sky copy and the scene behind the shields hold them, at no extra copy.
- Any other draw is in the scene after the overlays, before the shields.
- Middle and far draws there read the scene's own depth, attached read-only meanwhile (`gas_scene.frag`): nothing is copied for them. A full cloud needs the pilot's hull mask, so it reads a scene copy, taken once a frame.

Huge clouds (x half-extent 10 to 20 km) go through the same levels: levels, bounds and step lengths are relative to the cloud, positions are camera-relative.
The view range is the same 5 sectors for every size, so a huge cloud is at the full or the middle level for as long as it shows.

## Large clouds close up

Decided. A large cloud seen from inside or close by keeps structure and depth; it is not a flat wall.

The cause of the wall: a cloud's octaves and its steps scale with its size.
In a cloud four times larger the finest feature is 2.3 km and a step is up to 800 m, so the gas near the camera has no detail and no depth.
Its extinction (`GasCloudModule.ExtinctionOf`) is not the cause: it makes a huge cloud as deep as an ordinary one, which looks right from outside.

What a large cloud gains, near the camera only and at the full level only:

- Strands: one further octave (18.3 waves to the x half-extent, about 1 km in the largest cloud) breaks the gas into strands and gaps. It counts only where a step is short enough to follow it.
- Relief: one more lookup a third of a wave toward the star gives each strand a lit and a shaded side. Without it the strands are there but flat.
- Bent steps: the march's first steps are a fraction of the even length and grow with distance, the last nearly twice it. The count stays the content's.
- Clearing: the gas nearest the camera is thinned, so the eye reaches a few strands deep.
- All of it fades out between half the reach and the reach (0.8 x half-extents) from the camera; beyond that the cloud is exactly what it was.

How it is bounded:

- Figures are client data: `closeUp` in `content/visuals/gas.json`. Nothing below `fromHalfSize` (7 km), all of it from `fullHalfSize` (10 km).
- Every ordinary cloud is below 7 km: its march takes the same expressions as before, and fixtures 1–13 run unchanged.
- The middle and far levels take none of it. They start at 3 radii, far past the reach.
- `GasData` carries the figures in an eighth vector (`fine`). At most two more lookups per near sample.

What it costs, at 5096x1390 on an RTX 5070 Ti, wall clock, one quiet run of two each:

| View of the huge cloud at `133,0,-77` | Without | With |
| --- | --- | --- |
| Inside, at its centre | 5.4 ms | 8.7 ms |
| Inside, halfway out (`--gas-distance 0.5`) | 4.9 ms | 9.3 ms |
| At its surface (`0.8`) | 4.9 ms | 6.1 ms |
| Edge | 2.9 ms | 4.0 ms |

- So inside a huge cloud this resolution drops under 144 fps (about 110). Setting `strands`, `stepBend` and `clearing` to 0 gives the old cost and the old wall back.
- Most of the cost is the bent steps: more of the samples fall inside the gas, and thinned gas is marched deeper before it is opaque.

Tried and dropped:

- A second, finer octave: a lookup a sample, hardly visible.
- Splitting the nearest even steps instead of bending them: as costly, and blurrier.
- Reusing a step's density and light for its neighbour where steps are short: cheaper, but it drew arcs at the distance where the reuse stopped.
- A reach of 0.4: strands only within a few kilometres, so the surface seen from just outside was a wall again.

Open:

- Clearing thins gas as the camera comes near it. Seen in motion this may read as fog drawing back; stills cannot show it.
- Deep inside a dense part the light is the same everywhere, so the view is soft and low in contrast even with strands.
- Strands and relief are not in the light probes: a strand does not shade the gas behind it.
- A segment that begins at a shield or between two openings bends its own steps from its own start.
- Whether the cost inside a huge cloud is acceptable at 5096x1390, or the figures should be lowered.

## Wispiness

Decided. How soft and frayed clouds are drawn is a world setting, `gasWispiness`: 0 to 2, step 0.25, default 1.

- 0 is the look before the setting: dense bodies, a hard outline. Eleven captures at 0 match the earlier build: no pixel off by more than 2%.
- 1 thins the outer gas into strands and gaps; the outline fades instead of stopping. 2 is the most.
- Drawing only. Where clouds are, their shape data and the asteroids in them do not change, so no generation or protocol bump.
- The server owns it like every setting and sends it on joining, so all players of a world see the same clouds.
- Applied in `Galaxy.Under` (`GasCloudModule.Under` sets `Wispiness`); the renderer passes it in `GasData.motion.z`, a lane nothing used.

Why the clouds were cotton balls: gas at density 1 is about 12 optical depths per x half-extent.
Even the fringe strands were as dense as the core, so they alone drew a solid rim around the cloud.

What the setting does, all in `gasDensity`, so every level (full, middle, far) and the light probes take it:

- Shell: the outer part of the body thins steeply toward the outside, its depth made uneven by noise.
- Gaps: near the surface the gas survives only above a noise threshold that rises outward; strands with sky between.
- Fringe: thinner toward its outer end and sparser, so stars and rocks show through.
- Without the fine octave the gaps follow the medium octave alone (near light probe, far level); the far probe takes their average.
- Above 1 the gap threshold stays at its value at 1: higher, it emptied a whole band and the core showed a cut edge.

What it costs, at 5096x1390 on an RTX 5070 Ti, Release, mean frame of one 4 s benchmark each:

| View (`--gas-view`, `--gas-distance`) | 0 | 1 | 2 |
| --- | --- | --- | --- |
| Huge cloud `133,0,-77`, inside | 8.9 ms | 9.7 ms | 10.3 ms |
| Huge, edge | 4.1 ms | 4.3 ms | 4.1 ms |
| Huge, at its surface (`outside 0.8`) | 6.2 ms | 6.1 ms | 6.0 ms |
| Huge, `outside 2` / `7` | 1.7 / 1.0 ms | 1.8 / 1.0 ms | 1.8 / 1.1 ms |
| Ordinary cloud `127,-1,-127`, inside | 6.5 ms | 7.2 ms | 7.1 ms |
| Ordinary, edge | 3.4 ms | 3.5 ms | 3.7 ms |
| Ordinary, `outside 2` / `6` / `14` (far level) | 1.8 / 1.1 / 0.9 ms | 1.9 / 1.2 / 0.9 ms | 1.8 / 1.1 / 0.9 ms |

- The three values were measured one after the other per view; other work on the machine shifted absolute times between rounds by up to 40%.
- Inside costs most: gaps need the fine octave deeper into the body, and thinner gas is marched further before it is opaque.
- Inside a cloud the look stays close to before: the camera sits in the dense core, which the setting leaves alone.

Open:

- Middle-level fringes are grainy at 1 and 2: thin gas, 24 steps and per-pixel jitter. Motion shimmer is not reviewed.
- Bodies seen from outside are still evenly bright: thin gas lets the light in, so the inside is lit flat.
- The light probes see the gaps only at the medium octave or on average: a fine gap casts no shadow of its own.
- Whether 1 is the right default, and how 2 reads in motion, is the developer's call.

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
FrameConstants/frame.glsl append matching 128-byte gas data; only deferred frames have a nonzero budget there.
Composition uses two extra sampled descriptors and 128 additional uniform bytes per frame slot.
The gas passes push the same 128 bytes, all that Vulkan guarantees; the shield pipeline layout's push range is the larger of the two users.
The noise cache adds one immutable image/sampler and one sampled descriptor per frame slot; no render graph or history.
The ordinary pass retains its guarded pipeline module acquisition and RGB-only blend.

## Half-resolution march

Decided. The primary cloud is marched for one pixel in four, then read back per pixel. Inside a cloud the march was
most of the frame (GPU-bound: about 10 ms of 11 at 5096x1390), and gas changes slowly from pixel to pixel.

How it works (`gas_half.frag`, `gas_half.glsl`):

- A target of half the size each way (`SceneRenderer`, rebuilt with the other targets on resize). Each texel covers two by
  two pixels and is marched along the ray through their centre, to the farthest of their opaque depths (the copy kept
  behind the shields).
- The four texels of a two-by-two block take one quarter of the step each, in an order and shift drawn from the block.
  Their offsets then average out when read; with an independent offset per texel the read-back showed blotches.
- A pixel reads the four by four texels around it with tent weights two texels wide, each only where it was marched to
  the pixel's own depth (within 1%). Where none was, the pixel marches itself, exactly as before.
- The ordinary pass (`gas.frag`) and composition (`tonemap.frag`) read the same target, so they still agree. Composition
  reads it only for the interval from the camera to the pixel's own opaque depth on an unbent ray; lenses, shields in front
  of the opaque depth and the own-hull tunnel march as before.
- `GasConstants.Budget.w` is `HalfMarched` (-1) on a draw, or on the deferred `frame.gas`, whose cloud the target holds.

What marches at full resolution, as before:

- Pixels near a bolt (the half march leaves bolt lines out; their light on the gas stays in).
- The pilot's own hull while hyperspace thins the gas there, and every frame while the pilot's own rift or wormhole is open.
- Cold clouds and cold/normal pairs: their ice glints are dots a pixel across.
- The gas fixtures (`--gas-fixture 1..12`), which must take the exact march. Fixture 13 uses the target in both captures.
- Every draw other than the primary (middle and far levels, a second full cloud).

What it costs and saves, Release, RTX 5070 Ti, mean of 5 s benchmarks, other GPU work on the machine (±15%):

| View | 5096x1390 before | after | 5096x688 before | after |
| --- | --- | --- | --- | --- |
| Ordinary `127,-1,-127`, inside | 84 fps | 235 fps | 193 fps | 434 fps |
| Ordinary, pilot (composed, own shield) | 91 fps | 191 fps | 172 fps | 373 fps |
| Ordinary, command view (`--open command-fog`) | 94 fps | 355 fps | 278 fps | 827 fps |
| Huge `133,0,-77`, inside | 77 fps | 215 fps | 178 fps | 417 fps |
| Lightning `-15,-1,3`, inside | 49 fps | 204 fps | 185 fps | 415 fps |

- Cold clouds and pairs are unchanged: about 90 fps inside a cold cloud and 46 fps inside a pair at 5096x1390.
- `make bench-gas` runs these views and fails under 144 fps or on a managed allocation; cold and pair are reported only.

How it looks:

- The same at hull, shield, rock and bolt edges (in-process captures with the clock held, half off and on in turn).
- Slightly cleaner: the per-pixel grain of the jitter is gone, and thin gas in front of a rock is its mean, not speckle.

Open:

- Glints for cold clouds and pairs at half resolution: a pixel would need the transmission at the glint's depth.
- Motion has not been reviewed: the read-back is stable from frame to frame (no history), but grain at 2 px may shimmer
  differently from the old 1 px grain.

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
Deferred frame data holds the primary draw; when it is a cold/normal pair, its second cloud is in `gasPartners`, so composition integrates both. Every other draw is part of the scene the composition reads.
Extinction per metre falls with size above the standard 4.8 km half-extent (`GasCloudModule.ExtinctionOf`), so a huge cloud is as deep to look through as an ordinary one.
A huge cloud keeps the content's sample count over a longer path: its even steps are up to four times as long. Close to the camera they are bent shorter; see "Large clouds close up".

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
- Motion shimmer of the far and middle levels has not been reviewed.

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
- `133,0,-77`: a huge cloud, half extents `(19616,12750,16674)` m, centred about 1.9 km from arrival. Its box lies across the wall to sector `132,0,-77`.
- `-66,1,37`: a huge cloud with rocks in it, half extents `(19967,12978,16972)` m, centred about 7.4 km from arrival.

- `outside` / `absent`: same camera, 2.4 z half-extents from cloud center; absent disables gas submission.
- `--gas-distance <radii>` with either: the camera's distance in largest half-extents, 0.1 to 200. `--gas-distance-to <radii>` moves it there over the captures in equal ratios: `--gas-distance 2.6 --gas-distance-to 30 --capture-count 12 --capture-every 0.7` crosses both hand-overs.
- Each capture prints how many draws the frame queued at each level and whether the primary was composed.
- A huge cloud close up: `--sector 133,0,-77 --gas-view outside --gas-distance 0.5` (inside, halfway out), `0.8` (at its surface), `1.2` (just outside); `2`, `4` and `6` are one, two and three sectors away.
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
Half-resolution rendering of the primary cloud is described in "Half-resolution march".
The twelve composition fixtures use homogeneous input, so their rerun alone cannot validate texture quality or speed.
`GasNoiseLatticeTests` checks the actual CPU payload, packing, nondegenerate values and allocation after provisioning, not GLSL execution.

## Per-cloud wispiness

`gasWispiness` stays 1 by default. `gasWispinessMin`/`gasWispinessMax` default to 0.75–1.25.
`GasCloudModule.WispinessOf(cloud)` interpolates the multiplier using `cloud.Seed / 128`, then clamps the product to 0–2.
This is render-only and consumes no generation draws; changing it never moves clouds or rocks.
`WorldView.QueueGas` packs it into `GasConstants.Motion.z` for every tier; shaders stay unchanged.
Larger ordinary clouds can now enter the existing close-up treatment above 7 km half-extent.

## Decided (provisional, tunable): kinds and renderer integration

Generation 6, protocol 44, evaluator 9. This non-visual implementation requires the renderer changes below before release.

- `GasCloud.Kind` is `Normal=0`, `Cold=1`, or `Lightning=2`. It is generated, never sent independently.
- `seed / 128` selects cold below `coldChance` (0.20); among the remainder, `lightningChance` (0.10) selects lightning. Candidate proportions are 20% cold, 8% lightning, 72% normal. Separation biases surviving proportions.
- `gasColdChance` and `gasLightningChance` apply through `Galaxy.Under`, each 0–1 in steps of 0.01. Values in `galaxy.json` also apply when loading an unapplied galaxy.
- Only cold/normal pairs bypass rivalry. Positions do not move to force a mixture: independently seeded envelopes mix wherever they intersect. Cold/cold, normal/normal and every lightning pair retain the old separation margin and tie break.
- Clouds still have one anchor, one descriptor per anchor, and reach at most its 26 neighbours. There can be at most one cold and one normal at a point; neither can share that point with lightning.

Renderer contract (implemented; see "Drawing the kinds"):

- Carry `Kind` with every queued cloud, including distant tiers. Do not infer it from tint or repeat probability logic.
- Overlapping cold/normal volumes require joint front-to-back integration along each ray: split at both volumes' entry/exit and every opaque, shield or portal boundary. At each sample sum extinction and extinction-weighted source light from every active volume, then apply one transmittance update. Whole-cloud alpha sorting is incorrect.
- Both active volumes must participate in deferred HDR shield/opening composition. The single-primary assumption must be removed; preserve gas-free shield copies and per-sample own-passage checks. A hard limit must keep the pair together rather than silently dropping one.
- Lightning visuals may use `GasSensors.Disrupted(cloud, rules, snapshotTick)` to align flashes with gameplay. Keep visual-only density noise separate from gameplay membership.
- `AsteroidShape.Icicle` is a fixed six-tip elongated octahedron: unit x tips and y/z tips at 1/6. Use its six axial vertices and eight triangular faces (24 indices), or a subdivision of those faces within the existing mesh budget; a generic sphere sampling can miss the sharp tips. Server and client collision already use its `Point`/`RadiusAlong` definitions.

Gameplay envelopes and UI contract:

- `GasSensors.Contains` uses the smooth ellipsoid `HalfSize * Shape.X`, centred on the cloud. Cold membership wins wherever it intersects normal gas. Membership is at the hull/asteroid centre, not the noisy visible fringe.
- `rules/exploration.json.lightning`: period 480 ticks (8 seconds at 60 Hz), burst 60 ticks (1 second), remaining sensor range 0.15. Period allowed 1–36000; burst 1–period; share 0–1.
- Burst phase is `floor(seed / 128 * period)` plus the saved server tick modulo period. No mutable timer or RNG; reload continues the same phase.
- `Covers`, `Reach` and `SampleReach` accept the same optional `long tick`; production callers must pass snapshot/server time, never wall time. A burst reduces the range budget to max(proximity, base range × share), then applies ordinary gas attenuation. Proximity remains clear. Optical penalties from overlapping clouds add.
- `HullRecord.Environment` is one validated byte, propagated to `SessionSnapshot.Hulls[].Environment`: `InColdCloud=1`, `InLightningCloud=2`, `SensorsDisrupted=4`. Unknown bits and disruption without lightning are rejected. UI displays these authoritative flags for each visible hull, including fleet rows and RTS markers; do not derive flags from interpolated poses.

Status icons (decided, built):

- `HullStatus` (client, font-free) maps the flags to icons: bolt for a lightning cloud, pulsing with a ring while `SensorsDisrupted` is set; snowflake for a cold cloud. Look and colours: `content/visuals/status.json` (`StatusTuning`).
- Shown after the hull's name in the command view (hover for a tooltip), as icon plus words ("SENSORS DISRUPTED", "LIGHTNING CLOUD", "COLD CLOUD") on a third line of the fleet list row, in the map's UNITS rows, and as a chip above the pilot console. The map index has a row for each icon.
- Capture seeds: sector centres inside a cloud are rare. Use `--sector 133,0,-77 --set gasLightningChance=1 --set gasColdChance=0` (or the reverse); a burst shows about 8.3 s after the world starts. Natural: cold at `97,0,-60`.

Coverage renderer follow-up:

- `SensorCoverage.Compute` must pass the latest authoritative snapshot tick to `GasSensors.Reach`.
- Invalidate cached sensor shapes when disruption state changes, even for stationary ships; use the same sampled time for an entire outline. Cache creation must not depend on `GasOpticalPenalty != 0`, since lightning works with zero optical attenuation.
- This branch intentionally leaves that rendering integration to the renderer worker. Until integrated, displayed reach does not track lightning bursts.

Ice and gas fields:

- Cloud fields use 90% probability above x half-size 20 km, otherwise the existing 50%; content owns both threshold and probability. No additional slider is needed.
- Every generated asteroid whose final centre lies inside a cold gameplay envelope uses the icicle shape, including ordinary fields and the part of normal-cloud fields mixed with cold.
- Ice is appended to the resource catalogue, preserving existing indices. It has weight 2, presence 1, core bias 0; ordinary rocks may also carry ice ore. A sector without ice can still contain bare icicle shapes.
- `iceAbundance` and `iceGradeMin`/`iceGradeMax` use the existing abundance and grade paths. Grade uses the same global ore tuning before the per-material clamp. The commented server example is covered by the existing catalogue drift test.
- Mining and cargo inventory do not exist in this repository. No extraction, cargo transfer or mining yield is claimed by this change; only the resource identity and asteroid grade are ready for that future system.

## Non-visual implementation verification

- `timeout 900 make check TEST_ARGS="-parallel none"`: passed, 1,874 game tests and 354 listing tests.
- `make bench`: passed at 5096×1390, 982 fps normal and 1,012 fps resizing; zero managed allocations, 12 measured swapchain/target rebuilds. Validation layers were not active.
- Generation budget: 10,166 rocks at 80 km in 13.047 ms; 6,450 at 20 km in 9.526 ms, both below 15 ms. Frozen generation hash: `16723476013675883020`, recorded on Linux x64; other architectures remain unverified.
- Gas stress in Debug: 192 hulls, 32 clients, 17.989 ms/server tick, zero measured managed allocations. This was measured by Debug `make check`, not Release; see the same-configuration comparison below.
- No mining-yield test: the mining system is absent. Renderer integration is in "Drawing the kinds"; UI status flags remain outstanding.

Gas-kind changes relative to the integrated generation optimization:

`AsteroidFieldsModule` changes only its constructor (huge-field tuning), `Generate` (one cloud query, cold membership and final shape/resource/grade), and `Anchored` (huge-field probability).
`Settle`, `Gather`, `Reaches`, thinning, sort keys, placement streams and `RockIndex` are unchanged.
Ordinary shape draws use `Galaxy.RockShapes` (12); total `Galaxy.AsteroidShapes` is 13 including the icicle.
The generation and placement fixture hashes changed intentionally for the appended ice resource and cloud kinds; optimization must preserve the new bytes.

## Integration and deferred performance baseline

The branch merges `wip/integration` at `0f745cc`, including optimized placement, 20–160 km sectors and scan cooldown.
Protocol 44 combines the cooldown envelope and hull environment byte; generation stays 6 and evaluator stays 9.
The only conflict was `ExplorationRules`: both cooldown and lightning fields and parsers are retained.
Performance optimization is deferred by the product owner; no gas hot-path or cache-invalidation rewrite was attempted.

Before/after the gas-kind change on the same integration base:

- Before: `0f745cc`, without gas kinds, built from an isolated source archive.
- After: `7590134`, the merged gas-kind branch.
- `ManySensorsNearGasHaveMeasuredTickCost`: 192 hulls, 32 clients, generated gas at anchor `(133,0,-77)`.
- Three separate standalone test processes per build and revision, run serially on the same Linux x64 machine. Every run measured zero allocated bytes.

Server tick time in milliseconds; median of three, with the observed range:

| Build | Before gas kinds | After gas kinds |
| --- | --- | --- |
| Debug | 17.7079 (17.2712–17.8719) | 18.0626 (18.0113–18.5177) |
| Release | 1.8690 (1.8284–1.8814) | 1.9949 (1.9569–2.0849) |

The previously reported 17.989 ms was Debug, from `make check`, not Release.
The earlier 1.96 ms comparison was Release; these build configurations must not be compared as a regression.
These are warm stress samples for the later performance pass, not a statistical latency guarantee or coverage of every cloud kind/layout.

Build each revision with `make build CONFIG=Debug` and `make build CONFIG=Release`.
Run the corresponding test binary with `-method '*ManySensorsNearGasHaveMeasuredTickCost' -parallel none`,
under `DOTNET_GCgen0size=0x10000000` and `DOTNET_GCgen0MaxBudget=0x10000000`, matching the Makefile's `TEST_ENV`.
Mining/inventory and the joint ship-turret brainstorming session are [deferred](direction.md#deferred) until the environment work is complete.

Integrated verification:

- `timeout 900 make check`: passed, 1,952 game tests and 354 listing tests, including scan cooldown, jump input, editor follow, tips and range-drawing regressions.
- `make bench`: passed, 897 fps normal and 965 fps resizing at 5096×1390; zero managed allocations and 12 measured swapchain/target rebuilds. Validation layers were not active.
- Debug generation at 20/80/160 km: 8.203/10.724/11.555 ms, all below 15 ms. All fifteen selectable sizes passed.
- Client/rendering sources, `RockIndex`, shared jump input and scan-message/server sources match `wip/integration`; gas-specific shared state is retained alongside them.

## Drawing the kinds

Decided (provisional, tunable). Normal clouds are drawn exactly as before. Figures are client data in `content/visuals/gas.json`
(`cold`, `lightning`; `GasTiers`), bounded on load.

How a draw names its kind:

- `GasConstants.Motion.w` is `KindCode`: the kind plus four times one more than a slot. Draw order uses a distance kept beside the draw in `GasQueue`.
- A lightning cloud's slot is its entry in `gasFlashes`; a cold or normal cloud's slot is its partner in `gasPartners`.
- Every consumer integrates through `gasSegment`, which marches one cloud or a pair. Its single `gasMarch` call keeps the driver's compile time down; the tone mapper compiles the fixtures only with `--gas-fixture` (specialization constant `GasFixtures`).

Cold clouds:

- Tint, edge, knots, wisps, veil and extinction are factors on the shared figures: bluish white, crisper billows, clearer gaps.
- A faint 22° ice halo around the star is added to the phase (`halo`); it costs nothing per sample.
- Glints: a few cells of a fixed grid hold a glint, drawn about a pixel across, full level only (`sparkle*`).

Cold and normal overlap:

- `GasCloudField.Pair` pairs every cold cloud with the first normal cloud whose box overlaps its own, on the worker. Each cloud has at most one partner.
- The pair is one draw, levelled by the sphere around both, so both change level together. At most 8 pairs a frame; a pair past that is left out whole.
- Along a ray the pair is cut at both boxes' ends into at most three stretches: one cloud alone (the ordinary march), both jointly, one alone.
- Joint stretch: even steps, both clouds sampled each step with their own light probes, extinctions summed, light weighted by extinction, one transmission update. A cloud does not shade the other.
- Deferred composition, shield refraction, the own-hull hyperspace thinning (`gasThin`) and own-passage checks all go through the same `gasSegment`.

Lightning:

- Timing: `GasSensors.LightningCycle` on the snapshot clock (`ServerTickPrecise` plus the frame's tick fraction), the same burst the sensors lose. Strokes start in the first 80% of the burst.
- Deep strokes light the cloud from inside (`strokes`, `flash*`); their reach is a share of the cloud but at most `flashReach` metres. Middle and far levels multiply them by `farFlash`, so a burst shows from afar.
- Bolts (`LightningBolts`, CPU, no allocation): `bolts` in the cloud's outer shell each burst, and strikes in a lattice of `boltCell` cells around the camera, or around the nearest part of the envelope when the camera is outside within `boltNear`.
- If none of those cells strikes in a burst, the one nearest to striking does, so a viewer in or near a cloud sees a bolt every burst. Clients looking at the same cells see the same bolts.
- Each bolt is seeded by the cloud, the cycle and its number or cell: a jagged main channel and forks off forks to `boltDepth`, breadth first, at most 128 segments; at most 6 bolts a frame, nearest first.
- A bolt grows along its path (`boltGrow`), holds and flickers (`boltHold`, `boltFlicker`), fades (`boltFade`), and the main channel keeps an afterglow (`boltAfterglow`).
- Drawn in the march at its own depth, so gas in front dims it: a core about a pixel or two wide at any distance (fainter when narrower than a pixel), segments covering many pixels kinked further in the shader, and a glow that is a smooth union of the segments' glows, added at the bolt's centre depth.
- The two brightest bolts of a cloud also light the gas around them (`boltLight`, `boltLightRadius`). Far-level draws take the light but not the lines.

Ice shards:

- `MeshBuilder.Icicle`: the exact octahedron the simulation collides with, eight flat faces, 24 vertices; one mesh for every level of detail.
- Instance material 0.25 (`WorldView.MaterialIce`): pale blue, light wrapping into the crystal and through it against the star, streaks along the shard, a sharp glint and a bright rim. It writes the asteroid mark (0.25) like any rock.

Muted captures:

- `--gas-flash <s>` holds every lightning cloud at that moment of a burst; `--gas-flash-to <s>` moves it evenly over the captures.
- `--gas-view pair` looks across the anchored cloud and its overlapping partner; `--gas-view ice` frames the anchored sector's largest ice shard. `--gas-distance` scales both.
- Each capture prints the pairs drawn together, the lightning clouds lit and the bolts drawn.

Kind costs, at 5096x1390 on an RTX 5070 Ti, Release, best of three 4 s benchmarks each, other work running on the machine (medians up to twice as high):

| View | Before the kinds | With them |
| --- | --- | --- |
| Normal `127,-1,-127`, inside / 2 / 6 / 14 radii | 9.55 / 1.70 / 1.00 / 0.87 ms | 10.39 / 1.72 / 1.15 / 0.90 ms |
| Lightning `-15,-1,3`, inside: quiet / in a burst | | 10.2 / 11.0 ms |
| Lightning, 2 / 6 radii in a burst | | 2.15 / 0.95 ms |
| Huge lightning `133,0,-77` (`gasLightningChance=1`), inside: quiet / bolts nearby | | 10.6 / 13.8 ms |
| Cold `43,0,38`, inside / 2 radii | | 11.1 / 2.2 ms |
| Cold/normal pair (`sectorSize=20`, `-21,2,35`): side view / inside the cold one / inside the overlap | | 5.0 / 21.8 / 33.8 ms |

- With no cold, lightning or pair in view nothing extra runs; the normal-cloud figures above are that case.
- A pair's stretches share one step length, so together they take about the content's budget; each joint step still evaluates both clouds' density and light probes.
- Bolts reject far segments before the exact test; a frame of near bolts still adds about 3 ms inside a huge cloud.
- First start after a shader change: the driver compiles the tone mapper; about 12 s to the menu here, against about 14 s before.

Open:

- Normal clouds cost up to 15% more at the middle level and about 9% more inside than before the kinds, though a normal draw never takes the joint or lightning paths; the larger shader is the likely cause, unproven.
- Inside a cold/normal overlap a frame costs two to three times a single cloud's.
- A cold cloud does not shade its normal partner or the other way round; a joint stretch takes even steps (no close-up step bend).
- Flash strength and bolt look are the product owner's call; motion (growth, flicker, glints) has only been seen in stills.
- The sensor outline does not yet follow bursts, and the hull environment flags are not shown in the interface.
