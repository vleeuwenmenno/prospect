# World generation: fields and clouds in world space

Asteroid fields and gas clouds are things in world space, not contents of a sector.
A sector is still generated alone, from the seed, without generating its neighbours.
Generation version 5, protocol 41. The code is `AsteroidFieldsModule` and `GasCloudModule` in `Prospect.Game/World`.

## Decided: what the player gets

- Fields and clouds lie across sector walls. No seam at a wall.
- A field sits where it falls, not around a sector's centre.
- Asteroid density follows noise in world space: from almost none, through some, to a field's full density.
- A field fades out toward its edge.
- Some gas clouds hold asteroids. How many clouds, and how dense, is content.

Gas clouds:

- Most are the size they were, about 16 to 22.4 km long by default.
- A few are huge: 40 to 80 km long by default, up to a sector across.
- Any cloud can lie across a wall.

## Decided: anchors

- Every field and every cloud has an anchor: the sector its centre lies in.
- What a sector anchors is drawn from that sector's own stream. It needs no generated contents.
- A field or a cloud reaches at most one sector from its anchor, on every axis.
- So a sector finds all that reaches it by asking 27 sectors: itself and the 26 around it.
- Positions of a field are worked out relative to its anchor: the same numbers whichever sector asks.

## Decided: which sector takes a rock

A field proposes a set number of rocks. Rock number `i` of a field has streams of its own, keyed by the field and `i`.

1. Its first position is drawn in the field's frame. It follows the field's shape and nothing else.
2. The sector that position lies in takes the rock. A wall belongs to the sector on its upper side.
3. That sector keeps or drops the rock by the density noise at that position, against the rock's own draw.
4. It then looks for room: clear of its walls, of its arrival point and of larger rocks already placed.
5. No room there: up to nine more tries a short way off, inside the same sector. Then the rock is left out.

Why this has no seam:

- Steps 1 and 3 are functions of a point in world space. Both sides of a wall use the same ones.
- A crowded rock stays near where it landed. It never moves to another part of its field.
- Every rock is taken exactly once. A test counts a lone field's rocks over the sectors it reaches.

What stays per sector:

- The list: largest first, at most `maxPerSector` (20,480). An asteroid is still named by its index in it.
- Arrival clearance around the sector's centre.
- Ore: a rock takes its ore from the character of the sector it lands in.
- A rock is wholly inside its sector. Hull collision looks only at the sectors a hull touches.

## Decided: a field's shape

- A bent ribbon through four seeded sections, tapered at both ends, as before.
- Its length is cut into 24 pieces, each holding a set run of the field's rocks.
- A sector looks only at the pieces whose box reaches it. A piece wholly inside needs no test per rock.
- `distribution.fade` draws that share of rocks toward the ribbon's spine: density falls to nothing at the rim.
- `distribution.jitter` is how far a crowded rock looks for room, in units of its spacing reach.

## Decided: the density noise

- Two lattices in world space: `density.broadCells` and `density.cells` along a sector's edge (10 km and 2.5 km).
- Lattice points on a wall are the neighbour's points too, so the noise runs through it.
- A value below `from` keeps `sparse` of the rocks; above `to` all of them; a smooth step between.
- Thinning only removes. The field kinds' counts are the full density; about 65% are kept on average.
- The home field is not thinned: the starter field stays whole beside arrival.

## Decided: rocks in gas

- `inGas.chance` of clouds hold a field, drawn from the asteroid module's own stream.
- The field fills `inGas.fill` of the cloud's box at `inGas.perCubicKm` rocks, at most `inGas.max`.
- It is a field like any other: thinned, spaced, capped, scaled by the amount setting.
- With no gas module, or a chance of 0, gas changes no asteroid.
- A sector no cloud reaches has the same asteroids with and without the gas module.

## Decided: gas clouds

- A sector anchors a cloud with chance `chance` (4%), centred anywhere in it (`centerReach`).
- Size: `halfSizeShare` of the sector edge; with chance `huge.chance` the range `huge.halfSizeShare` instead.
- Clouds stand apart: any two centres are further apart than both x half-extents plus 56% of a sector edge. Where two would stand closer, the larger stays.
- So clouds never overlap, and a camera in or close to one is never in another. Many show at once from afar; see [gas clouds](gas-clouds.md).
- That rule looks at candidates two sectors around, never further, and only ever removes a cloud.
- A huge cloud is thinner by as much as it is larger (`GasCloudModule.ExtinctionOf`), so it is not a wall.

## The API for gas

```csharp
public readonly record struct GasCloud(Vector3 Center, Vector3 HalfSize, float Seed);   // unchanged fields
public readonly record struct GasCloudPlacement(SectorCoord Anchor, GasCloud Cloud);

bool TryGetCloud(Galaxy galaxy, SectorCoord anchor, out GasCloud cloud);
int Intersecting(Galaxy galaxy, SectorCoord sector, Span<GasCloudPlacement> output);
int Intersecting(Galaxy galaxy, Double3 center, double radius, Span<GasCloudPlacement> output);
float ExtinctionOf(in GasCloud cloud, float sectorSize);
```

- `GasCloud.Center` is relative to the anchor's centre. The box may reach into the sectors around it.
- `SectorContent.GasCloud` and `SectorSummary.GasCloudCount` are the cloud the sector anchors, as before.
- `Intersecting` for a sector lists every cloud whose box reaches it, at most `MaxPerSector` (27).
- `Intersecting` for a sphere lists clouds wherever they are centred, up to 8 sector edges out.
- All are pure and take no memory. The sphere query asks hundreds of sectors: use it on a worker or when the
  camera changes sector, not every frame.

## Decided: the map's counts

- `Galaxy.Summarize` estimates. It weighs the pieces of each field by how much of their box is in the sector.
- No rock is placed for it. It costs about 20 microseconds in a debug build; the map caches it.
- A sector it calls empty is empty. One it gives rocks can come out with none.
- It knows nothing of crowding: with size and spacing at 5× it can say twice what is there.
- After generation `SectorContent.Summary` holds the actual counts. Readouts say "ABOUT" for an estimate.

## Decided: world settings

They apply through `Galaxy.Under` and `AsteroidFieldsModule.Under`, as before.

| Setting | What it does now |
| --- | --- |
| `asteroidAmount` | Scales what every field proposes, gas fields too. |
| `asteroidFieldSize` | Scales fields. One reaches at most a sector from its centre, not to its sector's walls. |
| `asteroidFrequency` | Chance a sector anchors fields, and that a cloud holds one. Home is exempt. |
| `asteroidCap` | Most rocks a sector keeps and most a sector's own fields propose. |
| `asteroidCountMin` | Least the fields a sector anchors propose together. They may land next door. |

Size, spacing, radius, ore and material settings are unchanged.

## Decided: saved worlds

- A save names a pushed asteroid by its index, so it is held to `Galaxy.GenerationVersion`.
- A world saved under another version is refused, untouched: `OpenOutcome.Incompatible`, "OTHER VERSION" in the list.
- There is no migration. Worlds saved under versions 3 or 4 do not open in this build.
- `galaxy.json` is in the content identity: retuning it refuses older saves the same way.

## Limits

- A rock is never cut by a wall: one of radius r keeps its centre r from every wall. Giants thin out there.
- Spacing is kept between rocks of one sector only. Two rocks either side of a wall can be closer.
- A sector over its cap loses its smallest rocks. Its neighbour keeps them: a step in pebbles at that wall.
- `asteroidCountMin` is per anchoring sector, so a sector's own count can be under it.
- Two clouds never show at once, so clouds do not overlap or merge with each other.

## Measured

Development machine, default content. Before is generation version 3.

| What | Before | Now |
| --- | --- | --- |
| Densest sector near home, seed 11, debug | 11,129 rocks, 13.5 ms | 8,711 rocks, 11.7 to 12.4 ms |
| The same, release | not measured | 4.5 ms |
| A summary, debug | 1.4 µs | 21 µs |
| A summary, release | not measured | 5 µs |
| 1,089 sectors around home, seed 42 | 611,688 rocks, 690 with rock | 587,177 rocks, 950 with rock |

- The budget is 15 ms in a debug build for the densest sector near home.
- A rock costs about what it did. Fields now spread over more sectors, so the fullest sector holds fewer.
- Generation takes no memory once a sector's buffers are warm.
- `make bench`: 1,018 fps at 5096x1390, no allocation in the frame loop.

## Open

- Whether clouds may overlap and merge with each other. The renderer draws many clouds but lays whole clouds over each other and composes only the nearest; overlapping ones need their gas interleaved along a ray.
- Whether the map should show a field as one shape across sectors rather than a count per sector.
- Whether the current sector's readout should switch to the actual count once the sector is generated.
- Broad regions of dense and empty space beyond the galaxy's arms: a third, sector-scale noise is not there.

## Decided: variable sector and cloud sizes

- `sectorSize` is 20–80 km, default 80 km, step 10 km; `Galaxy.Under` owns its applied metre value.
- Ordinary and huge cloud diameters each have a minimum/maximum pair in km; huge chance is 0–1.
- Both endpoints are capped at the applied sector width before sampling. The cloud remains within one neighbour of its anchor; the 27-sector query remains complete.
- Anchors, gas queries, field density, exploration, maps and saved positions use the same applied `Galaxy.SectorSize`.
- Saved settings already store ids and values. No snapshot codec change is needed; generation and content identity refuse older saves.

### Sector-size budget and supported range

The selectable range is capped at 20–80 km; the default stays 80 km.
The former 160 km endpoint took 15.233 ms in Debug, exceeding the unchanged 15 ms budget.
Best of 20 generations of the densest sampled sector, seed 11, after integration:

- 20 km: 6,450 rocks, 9.811 ms Debug.
- 80 km: 10,166 rocks, 14.446 ms Debug.

Both supported endpoints enforce the 15 ms regression budget; no endpoint is exempt.
The generation hash and version stay unchanged because the default world's generation is unchanged.
