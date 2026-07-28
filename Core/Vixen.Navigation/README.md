# Vixen.Navigation

Navmesh: a voxel bake that turns level geometry into convex polygons, a query layer that finds paths
across them, and a crowd of agents that walk those paths without walking through each other or
through walls.

Spec: [docs/plan/14](../../docs/plan/14-roadmap.md) § Phase 8 — *"Recast/Detour binding, navmesh
baking as a build step, agents, avoidance"*.

## State

**Bake, query, agents and avoidance are built and tested. 40 tests.**

| | |
|---|---|
| `Baking/Heightfield` | Triangle rasterisation into columns of solid voxels; the low-obstacle, ledge and low-ceiling filters. |
| `Baking/CompactHeightfield` | The walkable surface with its neighbours resolved; erosion by the agent radius; monotone region partitioning. |
| `Baking/ContourSet` | Region outlines traced from the voxel grid and simplified within an error tolerance. |
| `Baking/PolyMesh` | Ear-clipping triangulation, convex merge to six-vertex polygons, adjacency by edge matching. |
| `Baking/NavMeshBaker` | The pipeline in order, single-tile and tiled, with the tile margin that makes tiles connect. |
| `NavMesh`, `NavMeshTileData` | Tiles that can be added and removed while agents stand on them; salted references; links across tile borders. |
| `NavMeshQuery` | Nearest polygon, A\*, funnel string-pulling, surface raycast, move-along-surface. |
| `NavQueryFilter` | Area costs and capability flags — the two independent questions a filter asks. |
| `Agents/PathCorridor` | The polygons an agent is inside, trimmed as it moves and straightened by raycast. |
| `Agents/LocalAvoidance` | Sampled reciprocal velocity obstacles. |
| `Agents/Crowd` | Agents, targets, steering, avoidance, separation, and the move that keeps them on the mesh. |
| `Ecs/` | `NavigationAgent`, `NavigationDestination`, `NavigationState` and the system that joins them to a crowd. |

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

## What is not implemented, and why

- **Watershed partitioning.** Regions are built by monotone sweep, which is hole-free by construction
  and is what makes the contour tracer's life simple. Watershed gives rounder regions and therefore
  fewer, fatter polygons; both give correct paths. This is the obvious next improvement.
- **Region merging.** Regions below `MinRegionArea` are discarded rather than merged into a
  neighbour. Merging keeps more surface and can produce regions with holes, which is the property the
  partitioning was chosen to avoid.
- **The detail mesh.** Heights come from the polygon corners, so the surface is flat within a polygon
  and a floor sits up to one cell height above where it really is. On rolling terrain that is visible;
  the fix is Recast's height-detail pass, which is a bake stage of its own.
- **Off-mesh connections.** Ladders, jump links and doors-as-teleports have no representation. The
  flag word is there and `NavPolyFlags.Jump` is reserved for it.
- **Dynamic obstacles.** A crate dropped on the floor means rebaking its tile. There is no tile cache
  and no obstacle carving. Closing a route with `NavMesh.SetPolyFlags` is the cheap half and works
  today.
- **Baking as a content-build step.** `NavMeshBaker` produces a `NavMeshTileData`, which is inert
  arrays and is ready to be serialised, but nothing in `Vixen.Assets` imports or writes one yet. Doc
  14's phrase "navmesh baking as a build step" is therefore half done: the bake is a step anything can
  call, and the pipeline does not yet call it.
- **Asynchronous pathfinding.** `NavMeshQuery` is one per thread and every search runs to completion
  inside the frame that asked for it. A crowd of a few hundred is fine; a thousand agents replanning
  in the same frame wants a sliced queue, which is where `Vixen.Core.Threading` comes in.

## Testing

The bake is checked against properties rather than against golden meshes: polygons are convex and
counter-clockwise, adjacency is symmetric, every vertex is at least an agent radius from the geometry,
the same input produces the same output, and a box standing on a floor is not walkable where it
stands. The query layer is checked against geometry whose answer is known — a straight line across an
open floor, a wall with one gap in it — and the crowd against its invariants: on the mesh at every
step, no serious interpenetration, and everybody arrives.

Licensed under Apache-2.0.
