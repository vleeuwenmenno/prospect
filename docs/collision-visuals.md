# Collision visuals

Dents and sparks are client geometry driven by authoritative collision metadata.
No collider, picking shape, shield shell or gameplay figure changes.

## Geometry

- Boxes and all twelve wedges retain every face boundary. Only a face-interior contact vertex moves toward the solid centroid.
- The decoded contact is pulled 2% toward the face centre to prevent degenerate edge-hit fans. Depth comes from shared collision rules.
- Dented faces use triangle normals. Original and dented vertices blend during distance fading; normals blend and the fragment shader normalizes them.
- Destroyed blocks are excluded using the current live mask, even when cosmetic replication lags. A stale mesh is never drawn against newer losses.
- Intact hulls retain exterior-face suppression. Hulls with losses include all faces of surviving blocks, including interior faces hidden by other blocks.

## Work and memory

- One reusable worker request freezes the live mask and sixteen profiles. Keys include session generation, hull identity, design reference, loss revision and visual version.
- Sixty-four resident cache slots retire when their hull leaves the snapshot. Repair creates a fresh identity. Old session results cannot install.
- Default per-hull ceiling: 40,000 triangle vertices. Preflight conservatively requires `36 * blueprint blocks + 6 * dent count` to fit; oversized hulls retain original rendering, never a truncated replacement.
- Default frame ceiling: 2,000,000 vertices (48,000,000 bytes), plus 65 instances (4,160 bytes), per frame slot. At most 64 hull draws and one spark draw. Streams are non-indexed, so no dynamic index allocation or vertex-offset rebasing exists.
- CPU cached original/deformed vertices total at most 122,880,000 bytes for 64 full slots, plus bounded changed-index lists and one worker's temporary lists/output. Frozen live-mask storage is 512 bytes; larger masks use original rendering.

Mapped coherent writes occur after the frame slot's fence and before submission.
Allocation failure uses the renderer's existing constructor unwind; buffers are destroyed with its other per-frame resources.
Impacts never rebuild the catalogue or call `WaitIdle`.

## Distance and sparks

- `content/visuals/collision.json` controls distances, projection thresholds, upload ceilings and particle budgets. No settings-menu slider was added.
- Detail is full within 400 m of the hull bounding sphere, zero beyond 800 m. Projected radius fades from zero at 8 pixels to full at 16 pixels.
- Distance and frustum checks precede profile scans, worker requests and mesh copies. Replicated metadata and completed cache entries remain available while far away.
- At most 128 contact bursts live for 0.35 seconds, with six streaks each. Events baseline new identities, consume sequences even when culled, and expire by server tick. A full pool drops new events.
- Sparks use the contact surface normal and inherited hull velocity, then travel analytically without simulation particles. The pool reserves 9,216 vertices for a configurable maximum of twelve streaks per burst; defaults use at most 4,608 vertices (110,592 bytes).

## Limits and verification

Cold, oversized or upload-budget-exhausted replacement meshes use the existing original renderer.
That renderer still draws lost-block hulls per block and retains its existing 32,768 shared-instance ceiling.
The whole replacement is refused before writing anything; its vertices are never truncated.
Dents with no surviving profile fall back to original geometry.
Sparks are opaque emissive ribbons with shrinking width, not physically simulated debris or transparent smoke.
Jump clipping is shared for dent meshes; spark ribbons use ordinary world depth and do not follow jump clipping planes.

Focused CPU geometry, worker ownership, allocation, spark lifetime and real editor repair tests pass.
A 30/50-hull CPU fixture uses 910 separated boxes per hull and zero, four or sixteen dents.
It measures preparation/copy time, not GPU rendering or FPS; raw measurements accompany the renderer patch.
GPU validation, appearance, collision feel and Windows remain unchecked.
