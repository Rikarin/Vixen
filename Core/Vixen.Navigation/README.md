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
| `Baking/CompactHeightfield` | The walkable surface with its neighbours resolved; erosion by the agent radius; authored areas; the distance field; both partitioners. |
| `Baking/Watershed` | Regions grown out from the ridges of the distance field, one water level at a time. |
| `Baking/RegionMerge` | Absorbs regions too small to be worth their own polygons, and drops the groups that lead nowhere. |
| `Baking/ContourSet` | Region outlines traced from the voxel grid and simplified within an error tolerance. |
| `Baking/ContourHoles` | Bridges a region's holes into its outer outline, so a region that grew round a pillar is still a simple polygon. |
| `Baking/PolyMesh` | Ear-clipping triangulation, convex merge to six-vertex polygons, adjacency by edge matching. |
| `Baking/PolyMeshDetail` | The ground under each polygon, sampled back off the heightfield and triangulated. |
| `Baking/NavAreaVolume` | Boxes and convex prisms that stamp an area — water, road, mud — before the surface is partitioned. |
| `Baking/NavMeshBaker` | The pipeline in order, single-tile and tiled, with the tile margin that makes tiles connect. |
| `Baking/NavTileCache` | The voxelised level kept resident, so an obstacle rebuilds the tiles under it rather than the level. |
| `NavMesh`, `NavMeshTileData` | Tiles that can be added and removed while agents stand on them; salted references; links across tile borders. |
| `NavMeshAsset` | A whole baked mesh as one serialisable value — what a build writes and a player loads. |
| `NavMeshQuery` | Nearest polygon, A\*, funnel string-pulling, surface raycast, move-along-surface — whole or sliced. |
| `Agents/NavPathQueue` | Searches run a slice at a time against a frame budget, on the caller's thread or on jobs, so a crowd changing its mind costs a budget rather than a search each. |
| `NavQueryFilter` | Area costs and capability flags — the two independent questions a filter asks. |
| `Agents/PathCorridor` | The polygons an agent is inside, trimmed as it moves and straightened by raycast. |
| `Agents/LocalAvoidance` | Sampled reciprocal velocity obstacles. |
| `Agents/Crowd` | Agents, targets — as a point or as a polygon the caller already has — steering, avoidance, separation, and the move that keeps them on the mesh. |
| `Ecs/` | `NavigationAgent`, `NavigationDestination`, `NavigationState` and the system that joins them to a crowd. |
| `Diagnostics/NavMeshDebugDraw` | The mesh, a corridor, a path and a crowd, as lines into `DebugDraw`. |

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

## One A\*, run either way

A search across an eighty-metre level is about thirteen microseconds, which is fine — and 256 agents
given a new destination in the same update is three and a half milliseconds, which is not. It is more
than the entire rest of the crowd, it lands in one frame, and it lands exactly when something
interesting has just happened in the game.

So the search is an incremental one — `InitSlicedFindPath`, `UpdateSlicedFindPath(iterations)`,
`FinalizeSlicedFindPath` — and `NavPathQueue` runs it against a budget shared between however many
searches are in flight. `Crowd` plans through the queue: an agent that asks for a path gets one a few
updates later and **keeps walking its old corridor in the meantime**, which is both cheaper and
better-looking than stopping dead while it thinks.

**There is one A\* here, not two.** `FindPath` is the sliced search run to completion, so the
synchronous answer cannot drift from the sliced one — and a test asserts they produce the same
corridor, polygon for polygon, when the same search is run four expansions at a time.

**It is a budget first and threads second.** Setting `NavPathQueue.Scheduler` runs each search's slice
on a job instead of on the caller's thread; leaving it null is the default and runs everything inline.
The searches were separate `NavMeshQuery` objects from the start, each with its own node pool and open
list, and they only read the mesh — so there is nothing to lock, and the only rule is the obvious one:
**do not change the mesh while an update is in flight**, which means `NavTileCache.Update` and a
scheduled queue update do not overlap.

**Both run the same rounds and give the same answers in the same updates.** An update is a sequence of
rounds — assign every free query from the waiting list, advance every assigned query by its share,
collect whatever finished — and nothing in that depends on which thread ran what or how fast. A test
runs two queues side by side over sixty-four updates and asserts the same request is `Ready` in the
same update with the same corridor polygon for polygon, and that both expanded the same number of
polygons. Scheduling a slice allocates nothing, which is also asserted: a job is a struct copied into a
preallocated array.

**What it buys, and the ceiling on it.** Thirty-two routes across an eighty-metre level, on a machine
with nine workers:

| Searches in flight | Inline | Scheduled |
|---|---|---|
| 2 | 545 µs | 448 µs |
| 4 | 545 µs | 333 µs |
| 8 | 551 µs | **308 µs** |

Under 1.8×, on nine workers. **The round is a barrier, so a round costs its longest search** — and
these routes run from a few polygons to fifty, so the short ones finish and wait. Letting each query
free-run and pick up the next request itself would recover most of the rest, and would cost the
property in the paragraph above. The barrier is what makes a scheduler an implementation detail, and
that is worth more than the throughput it gives up.

**It moved the bottleneck before it removed it.** Once the searching was spread over frames, 256
agents retargeting at once cost the same whether the budget was 256 expansions or a million — because
what the frame was spending its time on was the two `FindNearestPoly` calls each agent made to resolve
its own ends before it could even ask. That is the next section.

## The two lookups an agent did not need

Planning began by searching for the polygon the agent was standing on and the polygon its destination
was on. Neither search was necessary.

**The start was already known.** An agent's `Poly` is its corridor's first polygon and every move has
kept it current — that is what a corridor *is*. Searching for it again was asking the mesh a question
the agent had the answer to.

**The destination is asked once instead of once per plan.** It is resolved when the destination is set
rather than when the search is submitted, because a destination is set once and planned for however
many times it takes: a request the queue refused, a corridor gone bad, an agent knocked out of its
own path. And for the caller that already has it — a patrol point resolved at load, a rally marker, a
destination taken from another agent's `Poly` — there is `SetTarget(handle, poly, point)`, which skips
the search entirely.

**Both are cached and neither is trusted.** `IsUsable` checks that the reference still resolves *and*
that the filter still accepts it, falling back to a search when either fails. Both halves earn their
place: a rebuilt tile changes the salt of every reference into it, which is exactly what a remembered
destination is holding; and a door closed with `SetPolyFlags` is a polygon that still exists and may
not be used. Getting the second wrong would have an agent plan from ground it is not allowed to stand
on.

Two invariant breaks came out of writing it down: `AddAgent` and `ClearTarget` set the agent's target
without setting the polygon it is on, so a **recycled agent slot inherited the previous occupant's
destination** and a fresh agent with no orders would walk to it. A test covers that specifically.

**What it was worth**, on the eighty-metre level, in the frame where everybody is given a new
destination at once:

| Agents | Destination as a point | Destination as a polygon |
|---|---|---|
| 16 | 18.6 µs | **4.1 µs** |
| 64 | 81.7 µs | **27.5 µs** |
| 256 | 363.8 µs | **142.9 µs** |

The 256-agent storm was 480 µs before this. Dropping the start lookup alone takes it to 364 µs; giving
the destination as a polygon takes it to 143 µs — **3.4× faster than where the previous round left
it**, and 24× faster than the 3.5 ms it cost before there was a queue at all.

**And the budget still does not show**, at any agent count: 368 µs against 367 µs for a budget of 256
expansions against a million. The searching has not been the bound for two rounds now. What bounds it
now is that the queue holds 64 outstanding requests, so 256 agents asking at once take four updates to
be heard — which is the queue working, not a cost.

## Merging regions, and the number that says how much it was worth

`RegionMerge` absorbs a region smaller than `MergeRegionArea` into the smallest neighbour that will
take it, and discards a *group* of regions whose combined size is under `MinRegionArea` — by group
rather than one at a time, because three slivers that only touch each other are as unreachable as one.

Two regions are only merged when each touches the other along **one** stretch of boundary, and never
when one sits directly above the other. Two stretches means the pair encloses something, and merging
them would produce a region with a hole. That rule is what makes this stage hole-safe without any help
from the contour tracer; watershed is not, and the next section is about what that costs.

**What it is worth, measured rather than assumed.** At Recast's default of 20 it does *nothing* on the
levels here: a monotone sweep produces regions that are long rather than small, so almost none of them
are under any modest threshold. Turned up to 2 000 on a pillared eighty-metre level it takes 109
regions to 20 and 401 polygons to 310 — 23 % fewer — with the path across the level identical to the
centimetre and no measured improvement in search time. So the default stays at Recast's, the knob is
documented, and the claim in this paragraph is a number rather than a hope.

## Watershed, and the measurement that decided the default

`NavMeshPartitioning` picks how the walkable surface is cut up. **Watershed** builds a distance field —
how far every cell is from the nearest wall — blurs it, and floods it from the top down: at each water
level the regions that already exist grow outwards, and only what is left over seeds a region of its
own. **Monotone** sweeps the surface row by row instead. Both give correct paths; what differs is the
shape of the polygons.

The blur is not cosmetic. An unsmoothed chamfer field has one-voxel local maxima scattered along every
corridor and each of them seeds a region, so the partition comes out with several times as many
regions as the level has rooms. Expanding before flooding is not cosmetic either: growing the existing
regions first is what makes a newly-emerged strip beside a room join the room instead of becoming a
region that has to be merged away afterwards.

**A watershed region can enclose a pillar**, which a monotone region cannot, and that is the whole
reason `ContourHoles` exists. The tracer emits such a region as two outlines — the outside, and the
pillar wound the other way — and nothing downstream knows they belong together; ear clipping would
take the second on its own terms and produce a solid polygon over the pillar. So the hole is bridged
into the outline with a diagonal traversed in both directions, turning the annulus into one ring with
a zero-width slit. **The ear clipper needed a second pass for it**: the slit makes the polygon touch
itself, the strict diagonal test correctly calls every ear near it blocked, and without a fallback
that allows touching but not crossing the entire region is dropped. That fallback is the difference
between the pillar being an obstacle and the whole ring around it vanishing from the mesh.

**What it is worth, measured rather than assumed.** Same level, same settings, one tile:

| Level | Watershed polys | Monotone polys | Watershed nodes/search | Monotone nodes/search |
|---|---|---|---|---|
| 40 m room, 16 axis-aligned pillars | 86 | **65** | 30.5 | **24.8** |
| 40 m room, axis-aligned side rooms | 76 | **57** | **7.3** | 10.7 |
| Ring of blocks approximating a circle | **30** | 37 | **12.0** | 17.7 |
| Empty 40 m room | 9 | 9 | 3.5 | 3.5 |
| 45° corridor across the level | 127 | 127 | 26.5 | 26.5 |

Watershed is not uniformly better, and it is 1.3–2× slower to bake. On an **axis-aligned** level the
row sweep wins on polygon count outright, because a sweep whose direction agrees with the geometry
produces straight boundaries while watershed's follow the medial axis and come out diagonal and
staircase-shaped. On the round obstacle — the case a row sweep has no answer for — watershed is 19 %
fewer polygons and **32 % fewer nodes expanded per search**, which is the number a frame pays.

Path *length* barely moved either way: 40.10 m against 40.16 m on the pillars, 43.30 against 43.46 on
the ring. So the case for watershed is search cost on geometry that is not aligned to an axis, and the
case against it is bake time on geometry that is. It is the default because levels are not
grids — and `Monotone` is one initialiser away for a tile being rebaked per frame, where the bake time
is the number that matters and the shapes cannot go far wrong at that size.

## The ground under a polygon, and the flip that made it work

A navmesh polygon is flat, and it is flat at the height of its corners — which the contour tracer took
as the *highest* of the four spans meeting there, because a corner at the lowest of them would sink
below the floor it belongs to. Over a hill that means the polygon is a lid: it cuts the humps off and
bridges the dips. `PolyMeshDetail` samples the ground back out of the compact heightfield and gives
each polygon its own small triangulation to answer height queries from. Nothing about connectivity
changes — the detail triangles are never searched, never linked and never crossed.

**Sampling the right surface needs no search.** A column can hold several walkable spans, and picking
the wrong one would put a polygon's interior on a different storey from its corners. The rule is: take
the span nearest the polygon's own plane, and refuse anything further than an agent's height from it.
That window is *exact* rather than generous — the low-ceiling filter has already removed every span
without an agent's headroom above it, so two walkable spans in one column are at least an agent's
height apart and a window that size can contain only one. Recast floods out from the polygon's region
instead, which also survives a polygon whose plane is a poor guess; this relies on the corners being on
the surface they describe, which is what the bake guarantees.

**The greedy split alone did not work, and the measurement is what said so.** Starting from a fan over
the convex polygon and splitting whichever triangle contains the worst sample is correct, converges,
and produced a surface that was exact at every point it sampled and a metre out halfway between two of
them. The reason is that a 1-to-3 split keeps all three of the original triangle's edges, so a fan over
a large polygon keeps its enormous spokes for ever — and the error concentrated exactly along the
diagonals. Lawson's flip after each insertion fixes it: an edge is illegal when the far vertex of one
of its triangles is inside the other's circumcircle, and sweeping until none is gives the Delaunay
triangulation of the points. It also **halved the vertices needed** — 51 down to 26 on the polygon in
question — because a well-shaped triangle is worth more than two badly-shaped ones.

The in-circle determinant is computed in `double`. It is a difference of fourth powers of coordinates
in the hundreds, and in `float` the sign for a nearly-cocircular quad is noise — a sign that flickers
is two triangles flipping each other until the sweep limit.

**What it is worth**, on a 24 m hill of amplitude 1.5 m at the default 0.3 m cell size:

| | Mean height error | Worst | Bake | Detail stored |
|---|---|---|---|---|
| No detail | 0.764 m | 1.410 m | 5.2 ms | — |
| Sampled every 1.8 m | **0.152 m** | **0.305 m** | 8.1 ms | 1.4 kB |

Five times closer for a bake 56 % longer. On a flat floor and on a constant ramp it costs **nothing**:
the sampling finds no deviation, adds no vertices, and the timings are identical — which is why
`DetailSampleDistance = 0` is worth setting for a level built out of floors rather than left as a
default that pays for itself everywhere.

## The voxel a surface is in, and where in it

A span records the voxel its surface passes through, and every decision the bake makes about that
surface — step or wall, ledge or floor, connected or not — is integer arithmetic on that voxel index,
tuned against a grid and tested against one. The height *reported* to an agent came from the same
integer, so a flat floor read one whole cell height above itself.

The fix is not to make the filters fractional. `HeightfieldSpan.Drop` records how far below the voxel's
top the triangle actually was, in sixteenths, **alongside** the integer rather than instead of it. It
is carried through rasterisation, the merge, and the compact field without being read, past every
filter that wants a grid, and is first used by the contour tracer — the first stage that *reports* a
height rather than comparing one. From there the heights are in sixteenths of a voxel all the way to
`ToTile`, which is the one place they become metres.

That unit change costs exactly two adjustments: the polygon vertex matcher's tolerance, which is still
two voxels but now says so in sixteenths, and the detail pass's error tolerance. Nothing else in the
bake looks at a height — every geometric predicate in the contour tracer and the polygoniser is in XZ.

**Taking the highest of four corners gets better for it**, which was not the point but is worth having:
four spans meeting at a corner used to be four identical integers and are now four distinct heights,
so the corner lands on the highest surface rather than on whichever the tie-break happened to pick.

| Level | Detail | Before | After |
|---|---|---|---|
| Flat floor | either | 0.200 m | **0.000 m** |
| Constant ramp | either | 0.200 m | **0.075 m** |
| 24 m hill | on | 0.152 m | **0.054 m** |
| 24 m hill | off | 0.764 m | 0.897 m |

Mean absolute height error. The flat floor is now exact, the ramp keeps only the error the cell size
genuinely implies — a cell of a 1-in-4 slope really does span 0.075 m, and the corner takes the top of
it — and the hill with detail is three times closer with its bias gone: it was **+0.152 m systematically
high** and is now +0.010 m, which is no bias at all.

**The last row got worse, and that is the honest reading of it.** A hill with detail sampling switched
off is three enormous flat polygons, and their plane sits *below* the ground it spans; the old
whole-cell upward bias was accidentally cancelling part of that. Removing the bias exposes the plane
error at its true size. The fix for that configuration is the detail pass, which is on by default — and
a number that was only ever right because two errors pointed in opposite directions was not right.

## A crate on the level, and where the bake is cut in half

A bake is two halves. The first turns triangles into a walkable surface — rasterise, filter, compact,
resolve the neighbours. The second decides what shape that surface is — erode, partition, trace,
polygonise. **An obstacle only changes the second**: the ground under a crate is the same ground, it
just is not walkable while the crate is there. `NavTileCache` keeps the first half per tile and replays
the second.

```csharp
var cache = NavTileCache.Build(vertices, indices, bounds, settings, tileSize: 48);
var mesh = cache.CreateNavMesh();

var crate = cache.AddObstacle(NavAreaVolume.Cylinder(position, radius: 1f, height: 2f, NavArea.Null));

// One tile a frame. The crate appears over a few frames rather than in one long one.
cache.Update(mesh, maxTiles: 1);
```

**The carve happens before erosion, and that ordering is the feature.** Erosion is the promise that a
point on the mesh is a place the agent's body fits; carving after it would leave the mesh flush against
the crate and the agent standing half inside it. So a volume whose area is `NavArea.Null` is applied
*before* erosion, and a volume that stamps a cost is applied *after* — for the opposite reason, that an
area painted over ground the bake found unreachable should not resurrect it. One mechanism, two
placements, and the difference is exactly the difference between a shape claim and a cost claim.

**What it is worth, and what it costs.** Per tile, and a tile is the unit of a rebuild:

| Level | Tiles | Cache build | Rebuild | Bake from geometry | Resident |
|---|---|---|---|---|---|
| 30 m, two rooms and a corridor | 4 × 4 of 32 cells | 13 ms | 0.46 ms | 0.60 ms | 0.44 MB |
| 80 m, sixteen pillars | 6 × 6 of 48 cells | 55 ms | **0.75 ms** | 1.54 ms | 2.20 MB |

Twice as fast per tile, not ten times — rasterisation is about half of a small tile's bake, so keeping
it saves about half. **The larger win is the bound rather than the ratio**: a crate dirties the tiles
its footprint touches and no others, so dropping one on an eighty-metre level is four tiles and three
milliseconds instead of the level and fifty-five. `Update` takes a budget in tiles because that is the
honest way to spend it — one a frame, and the obstacle appears over four.

`ResidentBytes` is on the cache because the memory is the whole cost of the design and a project should
be able to read it rather than find it. Recast's tile cache compresses its layers to avoid this; that
is a thing to add when something has measured that it needs it.

**A crate against a tile border dirties the tile next door.** The dirty rectangle is the obstacle's
footprint widened by the agent radius, because the erosion around an obstacle reaches that far past the
obstacle itself — without the widening, the mesh thins on one side of a border and not the other.

## A connection is a polygon with two vertices

Ladders, jumps and drops are authored as `NavOffMeshConnectionData` on the tile, and the loaded tile
turns each into a polygon of its own: two vertices, no interior, a link at each end. Everything that
walks the mesh — A\*, the funnel, the corridor — then has one kind of thing to reason about, and only
the three places that treat a polygon as an *area* have to know the difference: nearest-polygon skips
them, a raycast stops at them, and the closest point on one is the closest point on a segment.

**The funnel needs no special case at all**, which is the part worth pointing at. A connection's
portal is a single point rather than a segment, so the wedge collapses there and the algorithm emits a
corner — which is exactly right: an agent has to arrive at the foot of the ladder before it climbs.

**A connection reaches as far as it was authored to.** A border edge reaches exactly one tile, so
relinking a tile visits its four neighbours; a zip line across half a level does not, and visiting
four neighbours used to leave one attached at the near end and dangling at the far one. Three tiles
have a stake in a long connection — the one that declared it and the one under each end — so a tile
load or unload revisits all three, and building a tile's links asks *every* tile that declares
connections rather than the ones next door. The mesh keeps a list of those, so a level that authors
none walks an empty list.

The direction of the question is what makes it cheap: a tile cannot know which faraway tile declared
a jump into it, so the declaring tiles are asked instead, and the answer is filtered by whether the
link's owning polygon is in the tile being built. Asking a tile with no stake in a connection costs
the walk and nothing else.

**Crossing takes time.** `Crowd` walks an agent across over `distance / maxSpeed` seconds, during
which it is out of the proximity grid, out of avoidance and out of separation, and
`CrowdAgentState.OffMesh` carries the authored `UserId` and the progress — which is what a game plays
a climb animation from. An agent that teleported would leave the game to fake the time back.

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

- **Off-mesh links between areas an authored volume creates.** A volume stamps a *cost*; it cannot
  make ground walkable that the bake found unwalkable, and it cannot connect two surfaces that do not
  touch.
- **A sub-voxel *horizontal* surface.** Heights are sub-voxel now; X and Z are not, and a wall still
  lands on a cell boundary. That is a different trade — the horizontal grid is what makes contours,
  regions and adjacency integer problems, and the agent radius already keeps a path well clear of
  where the wall exactly was.
- **Off-mesh connections between two meshes.** A connection joins two places on one navmesh. Two
  agent sizes are two meshes and nothing joins them, which is right — they are different graphs — but
  it does mean a lift shared between them is authored twice.
- **Compressed tile-cache layers.** `NavTileCache` keeps its voxels uncompressed — 2.2 MB for an
  eighty-metre level. Recast compresses; that is worth adding when a project has measured that it
  needs it, and inventing the requirement first would be picking a compressor for nobody.
- **A free-running path queue.** The searches run on jobs, but a round is a barrier and so costs its
  longest search. Letting each query pick up its next request itself would recover the rest and would
  give up the property that a scheduler changes nothing a caller can observe.
- **The scene is not a bake input, and half of that is now only a wiring problem.** A `.vxnavmesh`
  takes a *list* of placed pieces — `source`, `position`, `rotation`, `scale` — so a level assembled
  from a floor and thirty crates bakes correctly and each piece is a dependency of its own. What is
  still missing is reading those placements out of the level the game actually loads instead of out of
  this file. **What it was waiting on has landed**: `SceneCompiler` compiles a `.vxscene` into a
  `[DataContract]` `SceneAsset` whose entities carry their transforms and their components, so the
  work left here is to fill the same list from one — the reading, the transforming and the flattening
  do not care where a placement came from.

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
