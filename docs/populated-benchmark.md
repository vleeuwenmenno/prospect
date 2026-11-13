# Populated visual benchmark

`--visual-scene` is a private, opt-in diagnostic scenario, accepted only with `--bench` or `--capture`.
It is never a saved world: `--worlds`, `--save-fault`, `--start continue|load|newgame` and `--open leave` are refused.
The measurement ends at the run's last world frame, before the session closes.
It does not change the ordinary `make bench` scene or its pass criteria.

## Run

Use `make run CONFIG=Release ARGS="--visual-scene 50,dents,near,large --bench 32 --listing off --no-validation"`.
Set `XDG_CONFIG_HOME` and `XDG_DATA_HOME` to scratch directories before running.
Set `PROSPECT_VISUAL_REPORT` to the absolute CSV destination (default `/tmp/prospect-populated.csv`).
`python tools/populated-benchmark.py` runs the bounded comparison matrix sequentially with temporary user directories.

- Population: 30 or 50 total hulls, including the pilot's pod.
- Activity: `clean`, server-seeded retained `dents`, or `impacts` (seeded dents plus re-staged real hull contacts).
- View: `near`, `far` (1,200 m ahead), or `behind` (200 m behind).
- Optional `,large`: shared connected 910-box plate, admitted by `DesignBytes.Decode` before any session threads start.
- Default designs alternate the shipped mining and survey vessels. All scenarios use the normal authoritative server, snapshot replication and renderer.

## What is controlled

The server creates the population on its own tick after admission, in a fixed grid relative to the spawn sector.
Normal fleet creation is suppressed only for the scenario; the pilot stays in the ordinary pod.
Retained dents are explicit diagnostic metadata, not a claim of prior physical collision damage.
The impact mode repositions two bodies once per simulated second; the normal solver determines damage and sparks.
The two contact bodies are replaced once per second before re-staging, so contacts continue after destruction.
These replacements are diagnostic setup; each hit and its replicated visual event still come from ordinary simulation.
Inspect hull counts: destruction may temporarily reduce the authoritative population between resets.
Client snapshots can retain the removed identities until their existing expiry, so this mode can draw more than the configured population.
The recorded sustained runs show 32–34 and 52–54 drawable hulls for nominal 30/50; they are not exact matched-population comparisons.
Far/behind comparisons should use `dents`: impact mode moves its two contact bodies near the camera.

The large plate reaches the current 910-block admission ceiling; it is not a representative combat ship.
Its touching boxes suppress interior faces, unlike the older separated-box CPU fixture.
No content thresholds, simulation rules, render distances or upload budgets are changed.

## Read the CSV

Times are CPU frame durations, including normal renderer submission/fence work, **not GPU execution timestamps**.
The CSV retains cold startup and cosmetic replication, worker completion, and retained rendering.
One cosmetic recovery request per 30 ticks means a two-second warm-up is insufficient for many dirty hulls.
For retained comparisons, use the final settled interval and verify the full expected damage draw count.
The console's aggregate after two seconds still includes this recovery and must not be called warm-cache timing.

- `damage_draws`, `spark_vertices`, and `dynamic_bytes` count accepted whole dynamic uploads in frames actually drawn.
- `drawn_dents` counts profiles on submitted damage meshes, not screen-space visible pixels; depth occlusion is not measured.
- `fallbacks` counts eligible dented hulls that used original rendering (pending, refused or upload budget); these causes are not split.
- `loaded_sector_rocks` sums ready client sector caches; `visible_rocks` counts asteroid draw candidates after frustum/size checks. Neither is an occlusion query.
- `last_worker_ms` repeats the latest completed worker build duration; it is not request queue latency. `tick_ms` samples the latest server tick, not every tick.

Six streaks with six vertices each make 36 vertices per submitted spark burst under shipped tuning.
Render allocations are sampled by existing FrameStats. Process allocation includes cold workers and setup after the first active frame.
The sample array holds 200,000 frames; longer/faster runs can fill it. The ordinary benchmark still bounds run duration.
File formatting and writing happen after measurement, not inside the frame.

## Verification and limits

A requested near dent scene fails unless every staged hull submits a damage mesh at least once in the same frame.
Impact scenes fail if no real spark vertices are submitted; runs longer than twelve seconds also require sparks after ten seconds.
Far/behind dent scenes fail if any dent or spark geometry is submitted.
Every scenario requires the requested population to have appeared and no Vulkan validation errors.
Inspect captures separately: upload counters do not establish artistic quality, unobstructed visibility or pixel coverage.

The matrix uses default asteroids and an all-5× preset for the seven asteroid multipliers.
`--dense-only` runs only the two all-5× comparisons. Earlier `*-dense.csv` artifacts use only amount, radius and field dimensions at 5×.
Actual bounded placement counts must be reported; this does not promise five times as many rocks.
Repair and jump compositing are separate checks, not implied by passing the timing matrix.
`30,repair,near` seeds only the pilot's dents and sends ordinary RequestEdit/RepairEdit intent after two/four seconds.
Its verification requires a new own-hull identity with zero dents; no renderer state is cleared by the script.
`30,jump,near --open jump` seeds only the pilot's dents and uses existing jump automation.
Use timed capture sequences to inspect these transitions. The seed is cosmetic only, so it does not manufacture physical block loss.

Sustained spark stress in this harness involves two contact hulls; it does not saturate the 128-burst pool.
The separated-box CPU fixture remains useful for isolated worst-case copies, but its output is not a GPU benchmark.

The generated large plate uses BoxDesign’s default black albedo. It measures admitted geometry and uploads, not representative ship lighting or large-dent appearance.

## Recording reliability

Every sample has `drawn`: skipped acquisition frames keep their CPU time and allocation, with zero draw/upload counters.
Effect and population verification require drawn rows.
The fixed 200,000-row buffer records overflow and real end time in `<csv>.meta.json`; incomplete recordings fail verification.
A truncated prefix is never labeled the final five seconds.

The runner removes the current scene's previous CSV/metadata before launch and writes a per-scene status plus a run manifest.
Failed, timed-out, missing and stale-run scenes remain visible in the summary without successful final-interval statistics.
Legacy CSVs without these guards remain historical evidence, not verified current-run output.
The earlier 32-second measurements are not thereby proven truncated; their metadata did not establish this guard.

CPU-only reliability fixtures: `python tools/test_populated_benchmark.py` and `PopulatedMeasurementTests`.
They cover skipped draws, overflow, preserved slow rows, missing scenes, and successful output followed by a failed or timed-out rerun.
