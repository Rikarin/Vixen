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

## Not yet

Stored as `float`, not quantised into a narrow band. Correctness first: a quantisation is measured
against this, not instead of it. The clipmap that composites instances of these fields into a
camera-centred volume is the rest of doc 19's L1 and is not built.
