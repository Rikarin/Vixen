# Vixen.Navigation

Navmesh: a voxel bake that turns level geometry into convex polygons, a query layer that finds paths
across them, and a crowd of agents that walk those paths without walking through each other or
through walls.

Spec: [docs/plan/14](../../docs/plan/14-roadmap.md) § Phase 8 — *"Recast/Detour binding, navmesh
baking as a build step, agents, avoidance"*.

## State

**Bake, query, agents, avoidance, authored areas and the content-build step are built and tested.
55 tests here, 10 more over the importer, and the frame-loop paths are measured at zero allocation.**

| | |
|---|---|
| `Baking/Heightfield` | Triangle rasterisation into columns of solid voxels; the low-obstacle, ledge and low-ceiling filters. |
| `Baking/CompactHeightfield` | The walkable surface with its neighbours resolved; erosion by the agent radius; authored areas; monotone region partitioning. |
| `Baking/ContourSet` | Region outlines traced from the voxel grid and simplified within an error tolerance. |
| `Baking/PolyMesh` | Ear-clipping triangulation, convex merge to six-vertex polygons, adjacency by edge matching. |
| `Baking/NavAreaVolume` | Boxes and convex prisms that stamp an area — water, road, mud — before the surface is partitioned. |
| `Baking/NavMeshBaker` | The pipeline in order, single-tile and tiled, with the tile margin that makes tiles connect. |
| `NavMesh`, `NavMeshTileData` | Tiles that can be added and removed while agents stand on them; salted references; links across tile borders. |
| `NavMeshAsset` | A whole baked mesh as one serialisable value — what a build writes and a player loads. |
| `NavMeshQuery` | Nearest polygon, A\*, funnel string-pulling, surface raycast, move-along-surface. |
| `NavQueryFilter` | Area costs and capability flags — the two independent questions a filter asks. |
| `Agents/PathCorridor` | The polygons an agent is inside, trimmed as it moves and straightened by raycast. |
| `Agents/LocalAvoidance` | Sampled reciprocal velocity obstacles. |
| `Agents/Crowd` | Agents, targets, steering, avoidance, separation, and the move that keeps them on the mesh. |
| `Ecs/` | `NavigationAgent`, `NavigationDestination`, `NavigationState` and the system that joins them to a crowd. |

Baking is a build step: `Editor/Vixen.Editor.Assets/Navigation/NavMeshImporter` takes a `.vxnavmesh`
naming a collision mesh, bakes it, and writes the `NavMeshAsset` as the artefact — with the geometry
as a declared dependency, so re-exporting it re-bakes.

## Why this is Vixen's own code and not a binding

Doc 14 says "Recast/Detour binding", and that is the one line of the phase this does not follow. The
reasons are practical rather than aesthetic:

- **There is nothing to bind to.** Recast/Detour is a CMake source library with no C API and no
  published binaries. A binding means a C shim, a build per RID, and a new entry in
  `build/native-dependencies.json` — none of which exists, and all of which would have to work before
  a single line of navigation could be compiled or tested.
- **iOS is NativeAOT-only and WebAssembly has no dynamic loading at all.** Every native dependency is
  a per-platform link problem; `Vixen.Platform.Native`'s README is the record of what one of them
  costs. Managed code is one problem fewer on ten RIDs.
- **It is the choice the repository has already made twice.** `Vixen.Ui.Layout` is Yoga's algorithm
  against Vixen's data model, and `Vixen.Ui.Text`'s bidi and line breaking are the Unicode algorithms
  written here. The valuable part of Recast is the *pipeline* — rasterise, filter, erode, partition,
  trace, polygonise — and that is what has been taken.

**No Recast or Detour code is copied.** The algorithms are re-derived from their published
descriptions and credited at the call sites and in the repository `NOTICE`, under
[ADR-015](../../docs/plan/01-technology-decisions.md#adr-015--vixen-is-apache-20)'s reference-material
rule. Recast/Detour is zlib-licensed, which the dependency audit already records.

## Counter-clockwise, everywhere

Every polygon the bake produces is wound counter-clockwise in XZ — positive signed area with X and Z
read as an ordinary 2D plane. Three separate things depend on it and none of them can check it:

- `NavMesh.GetPortalPoints` puts vertex *i* on the right and *i + 1* on the left of the direction of
  travel, which is what makes the funnel's left and right mean anything.
- `NavGeometry.ClipSegment2D` is a half-plane clip that assumes the interior is to the left of every
  edge — it is the raycast, once per polygon.
- The convexity test in the polygon merge has a sign.

Deriving the winding per query instead would be a cross product per portal and would disagree with
itself whenever a path doubles back inside a polygon. So it is fixed once, in `PolyMesh.Build`, and
asserted for every polygon of a real bake by `ThePolygonsAreCounterClockwiseInXz`.

## Tiles connect by overlap, not by agreement

Two neighbouring tiles are baked independently and simplify their shared border independently, so one
tile's edge can span two of its neighbour's. Links therefore carry the *part* of the edge they cover,
as a pair of parameters along it, and the funnel uses the clipped segment. A link that assumed the
whole edge would walk an agent through a wall at every border where the two tiles disagreed.

The margin is the other half of it. Each tile is baked from the geometry a little way outside itself —
the agent radius plus three cells — so erosion at the tile edge can see the wall on the other side;
the margin is then marked unwalkable before the partition runs, so contours stop exactly on the tile
boundary. Getting the *inner* window wrong by one column, which is what happens if it is counted back
from the field width rather than forward from the margin, puts every border edge half a cell outside
its own tile and no tile links to anything. That is a measured failure, not a hypothetical: the first
tiled bake produced twenty-five tiles and two links each.

## An agent cannot leave the mesh

Steering produces a wish and `NavMeshQuery.MoveAlongSurface` turns the wish into a position. Avoidance,
separation, a stale corridor and a destination inside a wall all feed the wish; none of them feeds the
position. A bug in avoidance makes an agent walk oddly, and cannot make one walk through a wall —
which is the invariant `AnAgentIsOnTheMeshAtEveryStepOfTheWay` checks on every frame of a crossing.

## Standing still is not a candidate velocity

The avoidance sampler scores candidate velocities and takes the best. Zero is not among them, and that
is deliberate: standing still is very often the lowest-penalty velocity — nothing can be hit at zero
speed — and it is a *stable* one. Two agents who both choose it become two stationary obstacles for
whom it is still the best answer, and they face each other for ever.

That is not a prediction. With a zero candidate in the pool, two agents walking through each other
stopped 1.08 m apart and stayed there for the remaining fourteen seconds of the test. Removing it, the
same pair dodges, orbits briefly, and both arrive.

## A record struct's defaults are not its `new()`

`LocalAvoidanceSettings` and `CrowdAgentParams` are property-initialiser structs rather than positional
records with default parameter values, because for a record struct `new T()` binds to the struct's own
parameterless constructor and zeroes everything — the defaults written beside the positional
parameters are never applied. The failure is silent and total: an avoidance sampler with zero rings,
zero samples and zero weights returns the desired velocity unchanged, which looks exactly like
avoidance that has decided there is nothing to avoid. It cost an afternoon; the shape here cannot
reproduce it.

## Nothing in the frame loop allocates, and that is measured

`NavigationAllocationTests` runs each per-frame path until whatever it grows has stopped growing, then
runs it a thousand times more and asserts that the process allocated **zero** bytes: a search, a
string-pull, a raycast, a move across the surface, and sixteen agents walking a route with a wall in
it — replans, avoidance and all.

The one thing that failed it was the proximity grid. A crowd walking across a level occupies a roughly
constant *number* of cells and a constantly changing *set* of them, so a bucket kept per visited cell
allocated a list every time somebody walked somewhere new: 872 bytes over a thousand frames, a drip
that never quite stops. Buckets now go back to a pool on clear, and the number is zero.

The benchmark project (`Benchmarks/Vixen.Benchmarks.Navigation`) is where the same paths get a time
next to that zero: a search across an eighty-metre level is 12.8 µs, the funnel over its result is
another 1.3 µs, and a 256-agent crowd frame is 1.59 ms with avoidance and 255 µs without — which is
where the cost of a crowd actually is.

## What is not implemented, and why

- **Watershed partitioning.** Regions are built by monotone sweep, which is hole-free by construction
  and is what makes the contour tracer's life simple. Watershed gives rounder regions and therefore
  fewer, fatter polygons; both give correct paths. This is the obvious next improvement.
- **Region merging.** Regions below `MinRegionArea` are discarded rather than merged into a
  neighbour. Merging keeps more surface and can produce regions with holes, which is the property the
  partitioning was chosen to avoid.
- **Off-mesh links between areas an authored volume creates.** A volume stamps a *cost*; it cannot
  make ground walkable that the bake found unwalkable, and it cannot connect two surfaces that do not
  touch.
- **The detail mesh.** Heights come from the polygon corners, so the surface is flat within a polygon
  and a floor sits up to one cell height above where it really is. On rolling terrain that is visible;
  the fix is Recast's height-detail pass, which is a bake stage of its own.
- **Off-mesh connections.** Ladders, jump links and doors-as-teleports have no representation. The
  flag word is there and `NavPolyFlags.Jump` is reserved for it.
- **Dynamic obstacles.** A crate dropped on the floor means rebaking its tile. There is no tile cache
  and no obstacle carving. Closing a route with `NavMesh.SetPolyFlags` is the cheap half and works
  today.
- **Asynchronous pathfinding.** `NavMeshQuery` is one per thread and every search runs to completion
  inside the frame that asked for it. A crowd of a few hundred is fine; a thousand agents replanning
  in the same frame wants a sliced queue, which is where `Vixen.Core.Threading` comes in.
- **The scene is not a bake input.** The importer bakes the collision mesh a `.vxnavmesh` names. What
  it cannot yet do is bake *a scene* — every static collider in it, at its placed transform — because
  that needs the scene compiler doc 08 splits out and which does not exist. Naming a merged collision
  export is the shape that works today and is what most projects do anyway.
- **Nothing draws it.** `Vixen.Engine.Diagnostics.DebugDraw` exists and nothing here calls it, so a
  bad bake is diagnosed by a failing path rather than by looking at it. That is the cheapest missing
  thing on this list.

## Testing

The bake is checked against properties rather than against golden meshes: polygons are convex and
counter-clockwise, adjacency is symmetric, every vertex is at least an agent radius from the geometry,
the same input produces the same output, and a box standing on a floor is not walkable where it
stands. The query layer is checked against geometry whose answer is known — a straight line across an
open floor, a wall with one gap in it — and the crowd against its invariants: on the mesh at every
step, no serious interpenetration, and everybody arrives.

Determinism is asserted at the byte level rather than at the polygon level: two bakes of the same
level serialise to identical bytes, which is the form a content build's determinism check can actually
use.

Licensed under Apache-2.0.
