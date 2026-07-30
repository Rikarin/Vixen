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

## Not yet, and named so the absence is a decision

- **The RHI concept.** Acceleration-structure build/refit and ray queries behind
  `GraphicsDeviceFeatures.HasRayTracing` — the only genuinely new RHI concept in doc 19's § 6
  table, capability-gated like everything else, with this build as the comparison.
- **The tracer seam.** `IDistanceFieldSource.TraceField` is the interface everything above
  marches; the hardware implementation answers it and nothing above changes — doc 19 § L6's
  sentence, and the reason the CPU form answers with a hit, a distance and a normal.

**Nothing here creates or calls a graphics device.**
