# Vixen.Rendering.RayTracing

The reference half of [docs/plan/19](../../docs/plan/19-lighting-and-global-illumination.md) § L6.
Acceleration structures enter the RHI as an alternative tracer behind L1's interface — and a
hardware ray query cannot be checked against arithmetic, so this exists first: a triangle BVH
built and traversed on the CPU, with the closed forms a query's answers will be held against.

## A median build, deliberately

Longest centroid axis, split at the median — not the surface-area heuristic, and that is a choice
about testability: a median build is deterministic from the input alone, two builds agree to the
bit, and the traversal's cost has a shape a test can hold. SAH is a quality optimisation with this
as its baseline and its referee — the shelf atlas's own argument, one level down.

## The traversal is the brute force, at a fraction of the visits

Front-to-back, near child first, the far subtree closed by a nearer hit — and `RayHit.Visited`
counts what it touched, so the logarithm is measured against `BruteForce` rather than asserted:
four hundred rays through a four-hundred-triangle soup agree hit for hit, triangle for triangle,
at better than four times fewer visits. Möller–Trumbore answers the triangles, two-sided,
because a tracer that culls back faces is the cube capture's brightest-possible-wrong-answer
warning all over again; the normal is geometric and faces the ray, so a caller's bias is never a
bias into the surface.

## The tracer's answers, written here before any device gives them

`QueriedField` is the CPU pair of `RayQueryField.rvn` — the `IDistanceFieldSource` whose trace is
a ray query instead of a march, doc 19 § L6's "nothing above it changes" made literal: the same
kernels compose it through the same slot, and only the composition names the tracer. One class
answers exactly what the shader answers, over this package's own BVH, so the device comparison
stands on a traversal already held hit-for-hit against brute force.

An acceleration structure holds surfaces, not distances, and the answers say so honestly: the
trace and the shadow are queries and exact — the shadow hard, deliberately, because a query
answers *whether* and a penumbra is derived from how near a march grazed; the point questions
answer `NoDistanceField`'s answers, because a position alone names no triangle. The one seam that
costs image quality is the gradient: a cache hit through the hardware tracer currently faces up.

## The RHI half, and where it is checked

The concept landed in `Vixen.Graphics`: `HasRayTracing` (three promises — build/refit, queries,
and buffer device addresses — declared true only where all three hold), the two-level build
through `GetAccelerationStructureSizes` / `CreateAccelerationStructure` /
`ICommandList.BuildAccelerationStructure`, and the descriptor kind a kernel binds the top level
through. The Vulkan backend implements it behind `VK_KHR_acceleration_structure` +
`VK_KHR_ray_query`; every other backend answers the honest no. The device comparison —
`AccelerationStructureDeviceTests`, the whole path from geometry buffers to a probe dispatch
composed with `RayQueryField`, against `QueriedField` — is gated on the feature and therefore
**skips on MoltenVK, which exposes neither extension**: on this project's own development
hardware the detection logic is what the unit tests hold, and the query comparison waits for a
device that can run it. That is stated here rather than discovered later, because a test that has
never failed anywhere is a different claim from a test that has passed somewhere.

## Not yet, and named so the absence is a decision

- **The hit's true normal.** The query returns the committed primitive's index, and the vertex
  buffer the structure was built from can turn it into the triangle's geometric normal —
  `GradientField`'s honest answer, landing in `QueriedField` first.
- **SAH.** The median build is the baseline and the referee; the surface-area heuristic is the
  optimisation measured against it.
- **Refit.** A build per change is the baseline; updating in place is the optimisation, and it
  rides the same `BuildAccelerationStructure` seam.

**Nothing here creates or calls a graphics device** — the referee and the BVH are device-free;
the structures live in `Vixen.Graphics`, and the comparison in the golden tests.
