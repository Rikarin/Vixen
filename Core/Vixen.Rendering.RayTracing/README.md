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

⚠ **And as of 2026-09-09 it has run nowhere.** The goldens execute on two machines — this project's
Mac, which is MoltenVK, and the Linux CI runner, which is llvmpipe — so the *only* end-to-end proof
that the hardware tracer answers what the BVH answers has never executed. Everything under it is
real coverage of the **detection** and none of the **query**: `VulkanFeatures.Translate`'s hand-built
structs, `QueriedField`, the BVH's own referee against brute force. Read that as "L6's kernel is
unexecuted", not as "L6 is tested", when a quality tier decides whether to use it.

The skip now says so out loud and has an expiry. It names the adapter that declined, so a log
records *which* device answered no rather than only that one did; and `VIXEN_REQUIRE_RAY_QUERY=1`
turns the skip into a failure, the same escalation `VIXEN_REQUIRE_VULKAN` is for a missing device.
Set it on a runner that has both extensions and the comparison stops being skippable — which is what
closing this needs, since nothing in the repository can conjure a device that has them.

## Not yet, and named so the absence is a decision

- **The hit's true normal**, and ⚠ **it is not the small read this list used to describe.** The
  `Trace` intrinsic does answer `(t, primitive, instance, hit)`, so the index is there — but
  `RayQueryField.TraceField` discards it one line later, because `DistanceFieldHit` carries
  `hit`, `distance`, `position` and `steps` and no normal, and the consumers ask
  `GradientField(hit.position)` — a *position*, which names no triangle. So the primitive index is
  gone before anything that wants a normal can see it, and giving the hardware tracer an honest
  normal is a change to the shared protocol (a field on `DistanceFieldHit`, or a `GradientField`
  overload taking the hit) that every `IDistanceFieldSource` and every consuming kernel touches —
  not a read inside this shader.

  ⚠ It is also not a quality item in the same sense as the two below. `SurfaceRadiance(position,
  normal)` picks a card by normal, so a constant upward answer picks every horizontal card in the
  atlas whatever the surface is: under the hardware tracer that is a **wrong colour, not a rough
  one**, and it will not look like a normal bug — it will look like the surface cache being wrong.
  Filed as [#1169](https://github.com/Rikarin/Vixen/issues/1169) rather than left here,
  because the protocol change is its own piece of work.

  ⚠ **The CPU half has landed, and it was smaller than that paragraph made it sound.** `QueriedHit`
  now carries `Normal` and `Primitive`, and `QueriedField.GradientField(in QueriedHit)` is the
  overload the shared protocol has to grow. Nothing was computed to do it: `TriangleBvh.Trace`
  already crosses the committed triangle's edges and already faces the answer at the ray, and
  already returns the index beside it — `QueriedField.TraceField` was discarding both while
  building its answer, which is the *same discard* the shader makes one line after the intrinsic.
  So the reference for the owed device read exists and is held to a closed form: the fixture wall is
  vertical, so its normal is perpendicular to the up vector and the old answer is not a worse
  approximation of the new one but unrelated to it.

  What is still owed is the device half, and it is still the protocol change: a field on
  `DistanceFieldHit`, every `IDistanceFieldSource` filling it, the three consuming kernels asking
  the hit rather than the position, and the vertex and index buffers bound beside `sceneStructure`.
  ⚠ That lands in the one place nothing in this repository can referee — see the skip above.
- **SAH.** The median build is the baseline and the referee; the surface-area heuristic is the
  optimisation measured against it.
- **Refit.** A build per change is the baseline; updating in place is the optimisation, and it
  rides the same `BuildAccelerationStructure` seam.

**Nothing here creates or calls a graphics device** — the referee and the BVH are device-free;
the structures live in `Vixen.Graphics`, and the comparison in the golden tests.
