# Vixen.Rendering.DistanceFields

How far every point in a box is from a mesh's surface, and which side of it it is on.

This is step one of [docs/plan/19](../../docs/plan/19-lighting-and-global-illumination.md) — the
tracing substrate the whole lighting path stands on. Distance-field shadows and occlusion read a
field directly; the irradiance field traces one; everything above them traces what those produce.
It references no graphics device, which is what makes a bake checkable against a closed form rather
than against a picture.

## Two halves, failing for different reasons

A signed distance field is one number, and the two halves of it are not equally hard.

### The distance is exact

Every sample takes the true distance to the nearest triangle, out of a BVH. The usual answer is to
rasterise a narrow band and sweep a chamfer or vector distance transform over the grid, which is far
faster and approximate — and approximate in the worst way, because the error is invisible until a
tracer takes a step slightly too long and walks through a wall.

Exact costs a closest-point query per sample and buys a field that can be *checked*: a sphere's bake
is compared against `|p| − r`, and a box's against its closed form, to a tolerance the mesh's own
tessellation explains. That is the exit criterion doc 19 sets for this phase, and it is not available
against a propagated field.

### The sign is voted on

The textbook answer counts ray crossings and calls odd inside. It needs the mesh to be closed, and
meshes are not closed. Artists ship facades with no back, walls that are one quad, and shells with a
hole where something else was meant to cover them — and on any of those, parity inverts a whole
region rather than degrading.

So each sample looks in many directions and asks a softer question: **how much of the sky, from here,
is a face seen from behind?**

- A point inside solid geometry sees backfaces nearly everywhere.
- A point outside sees them nearly nowhere.
- A point under an open shell sees them over exactly the fraction of the sphere the shell covers —
  which is what `BackfaceThreshold` is a dial on, and why an open box still bakes as solid.

Unreal does the same thing for the same reason. It degrades exactly where parity inverts.

### The directions are a spiral, not a random set

`SignRayCount` directions off a Fibonacci sphere. Even coverage, no seed — so two bakes of one mesh
are byte-identical without a random source having to be threaded through the bake and pinned. And no
direction in the set is axis-aligned, which matters more than it sounds: a mesh built of axis-aligned
quads is exactly the mesh an axis-aligned ray hits edge-on.

## Samples are on grid points, not in voxel centres

The first sample along an axis sits exactly on `Bounds.Minimum` and the last exactly on
`Bounds.Maximum`, so `CellSize` divides by `Resolution − 1` rather than `Resolution`. Trilinear
interpolation then covers the whole box with no extrapolation anywhere, and `Sample` is exact at the
corners.

The consequence worth knowing: **a trilinear interpolation of a distance function is not a distance
function.** It under-estimates near a concave corner and over-estimates near a convex one, so a
sphere tracer reading it takes conservative steps rather than exact ones. That is the standard trade
and the reason a step scale below one exists at all.

## What it cannot do

**Anything thinner than a voxel is not there.** No sample lands inside a wall thinner than the cell
size, so the field reads as though it were open and light passes through. This is a property of the
representation, not of the bake — doc 19 carries it as risk G3, and the remedy lives at the sampling
end.

Resolution is per-axis, derived from the bounds so voxels stay near-cubic. A door frame asked for 32
would otherwise be coarse along its length and absurdly fine across its thickness, and the thin axis
is the one that decides whether the field leaks.

## Parallelism does not change the answer

Samples do not read each other, so splitting the bake by Z slice cannot change what any of them
computes. `Parallel` exists so a profiler or a debugger can see one thread — not because the result
depends on it, and a test asserts the two agree byte for byte.

## Placing a field: position, rotation, one scale

`DistanceFieldInstance` deliberately cannot hold a matrix. A distance field survives being moved and
turned — a rotated distance is the same distance — and it survives being scaled by one number, where
every distance scales with it. It does **not** survive a non-uniform scale: squash a sphere's field
along one axis and the result over-reports along the squashed axis and under-reports across it, and a
tracer reading it walks through the surface. A `Matrix4x4` would accept that silently, so the type is
shaped to refuse it. A mesh that genuinely needs a non-uniform scale needs its own bake at that
scale.

## The clipmap

`GlobalDistanceField` is what a tracer actually walks: nested cubes sharing a resolution and doubling
in extent, fine where the camera is and coarse where it is far, composited as the minimum over every
instance.

**Every level snaps to its own cell grid.** A level centred exactly on the camera slides its sampling
grid under static geometry, and a wall that has not moved changes its distance every frame. This is
the same defect `ShadowCascades` fixes by snapping a cascade to whole texels — same cause, same fix,
and the two look unrelated until both are written down.

**A level clamps to what it can know.** Nothing outside a level's cube was consulted, so a cell that
found nothing reports `MaxDistanceOf(level)` rather than infinity. "At least this far" is a step a
tracer is always allowed to take.

**Outside an instance's bounds the composite substitutes a bound, not a reading.** Let *f* be the
field's value at the nearest point of its bounds and *t* the distance from the query to those bounds.
Because every mesh point is inside the box, the true distance is at least `√(f² + t²)` — safe, tight,
and *continuous* at the boundary, where `t = 0` makes it exactly *f*. A plain distance-to-box would
drop to zero there and make every tracer crawl around every object.

It is still loose in the open gap between two objects — about a sixth low, with a test that says so
in numbers. Under-reporting is the survivable direction: a tracer that thinks a surface is nearer
takes an extra step; one that thinks it is further goes through it.

## Marching it

`DistanceFieldTracer` works on `IDistanceField`, which both the baked field and the clipmap
implement. The interface earns its place twice: the tracer is written once for both, and a **test can
march an exact analytic sphere**. Marching a sampled sphere measures the tracer and the interpolation
together and cannot say which was wrong; marching an analytic one measures the tracer alone.

The interface's contract is a *lower bound*, not an exact distance. Under-report and a tracer is
still correct — it takes more steps. Over-report and a step of that length passes through the
surface. Everything in this project fails in the first direction on purpose.

**`StepScale` below one is correctness, not timidity.** Sphere tracing is exact when the field is
exact: a step of *d* cannot cross a surface *d* away. A trilinear field over-reports near a convex
corner, which is exactly a step that crosses the surface and a ray that comes out the other side.

**`Shadow` is why distance fields are worth having for shadows at all.** A shadow map or a ray cast
answers one binary question and needs many samples to soften it. Here the field already knows how
close the ray passed: at distance *t* with clearance *d*, the occluder subtends about *d/t*, and the
smallest such ratio over the march **is** the penumbra. One march, a soft shadow, and softness that
grows with distance from the occluder for free — which is the part that reads as real.

**`AmbientOcclusion` needs no rays and produces no noise.** Above open ground the field a metre up
reads a metre and the difference is nothing; in a corner it reads less, and that shortfall *is* the
occlusion. One sample per step, nothing to denoise. It sees geometry at the field's resolution and
nothing finer, so it complements the screen-space kind rather than replacing it — a flat floor
correctly occludes nothing at all, which is the test that says the integral is measuring geometry and
not its own step size.

## A trap worth naming

`default(T)` does **not** run a struct's parameterless constructor. An optional parameter written
`settings = default` therefore hands over zeroes, not the documented defaults — a resolution of zero,
a step budget of zero. Both `Bake` and the tracer take `T?` and coalesce instead, and there is a test
that omitting the settings agrees with passing `new()`.

## Not yet

Stored as `float`, not quantised into a narrow band. Correctness first: a quantisation is measured
against this, not instead of it.

`Update` recomposites every cell of every level every time. A camera that moved one cell invalidates
one slab per axis and nothing else — which is the entire reason the levels are snapped to their own
grids — so scrolling is the next thing here, and it changes no result, only how long an update takes.

Instances are rejected against their bounds in a linear scan, early-outing against the best distance
so far. A tree over the instances is what replaces that when the scan stops being enough.

Nothing here has touched a GPU yet. Uploading a clipmap level into a 3D texture and porting the
tracer to a shader — with this one as the reference it gets compared against — is the rest of doc
19's L1, along with the importer stage that writes baked fields into the content pipeline.
