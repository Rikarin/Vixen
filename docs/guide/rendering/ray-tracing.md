---
title: Tracing rays against the scene's own triangles
slug: rendering/ray-tracing
kind: guide
area: Rendering
summary: The two-level acceleration structure the RHI exposes, the ray query Raven compiles into one intrinsic, and why the hardware tracer is an alternative behind the distance-field interface rather than a lighting mode of its own.
api: [T:Vixen.Graphics.AccelerationStructureKind, T:Vixen.Graphics.AccelerationStructureDescription, T:Vixen.Graphics.AccelerationStructureHandle, T:Vixen.Graphics.AccelerationStructureSizes, T:Vixen.Graphics.AccelerationStructureBuildInput, T:Vixen.Graphics.AccelerationStructureTriangles, T:Vixen.Graphics.AccelerationStructureInstance, T:Vixen.Graphics.AccelerationStructureInstances, T:Vixen.Graphics.GpuAccelerationStructure, T:Vixen.Raven.IR.IrAccelerationStructureType, T:Vixen.Rendering.RayTracing.QueriedField, T:Vixen.Rendering.RayTracing.QueriedHit]
tags: [rendering, ray-tracing, acceleration-structures, raven, global-illumination]
since: 0.1
status: preview
related: [rendering/lit-path, rendering/physical-lighting, rendering/shadows, rendering/mesh-and-material]
---

## What it is

A hierarchy of triangles the GPU can shoot a ray at, and the three places it shows up: as an RHI
resource, as a binding type in a Raven shader, and as a CPU reference implementation that was
written before either of them.

| Type | What it is |
|---|---|
| `AccelerationStructureKind` | `BottomLevel` (geometry, in one object's space) or `TopLevel` (placed references to bottom levels) |
| `AccelerationStructureBuildInput` | Everything one build reads. One record for both levels |
| `AccelerationStructureTriangles` | A bottom level's geometry: a vertex buffer, an index buffer, and the counts |
| `AccelerationStructureInstances` | A top level's geometry: a buffer of packed instance records |
| `AccelerationStructureSizes` | What the device says a build costs — `Structure` and `Scratch` |
| `AccelerationStructureDescription` | What to create: the kind, the size sizing returned, and a name |
| `AccelerationStructureHandle` | The structure, by handle. `Null`, `IsValid`, and nothing else |
| `GpuAccelerationStructure` | The backend's object behind that handle. Only a backend derives from it |
| `AccelerationStructureInstance` | One placed reference, 64 bytes, in the layout Vulkan and D3D12 already share |
| `IrAccelerationStructureType` | Raven's IR type for the binding — a singleton, because there is nothing to parameterise |
| `QueriedField` | The same answers on the CPU, over a `TriangleBvh`. The referee |
| `QueriedHit` | One of its answers: hit, distance, position, steps |

**Two levels, and the split is not an implementation detail.** A bottom level holds triangles in
one object's space and is expensive to build. A top level holds *instances* — a transform, a
visibility mask, a custom index and the GPU address of a bottom level — and is cheap. A ray query
opens against the top level, never against a bottom one. That is what lets a thousand copies of a
rock cost one triangle build and a thousand 64-byte records.

## What it is for

Answering a trace exactly, where the alternative is marching a distance field and accepting what a
voxelised approximation of the world can tell you. A reflection off a thin railing, a shadow from a
leaf, a probe ray that must not miss a wall it grazes — those are the cases where the field's
resolution is the error and the triangles are not.

**It is an alternative tracer, not a renderer.** Everything above it — the screen probes, the
reflections, the radiosity, the ambient occlusion — composes a tracer through one shader interface
and never learns which one it got. `RayQueryField.rvn` is that composition: an `IDistanceFieldSource`
whose trace is a ray query instead of a march, dropped into the same slot the clipmap marcher
occupies. No pass changes, and no pass is allowed to ask.

⚠ **The hardware tracer is not the default and not the better mode — it is the one some devices
have.** `GraphicsDeviceFeatures.HasRayTracing` is three promises at once, and a backend says yes
only when it holds all three: structures can be built, a shader can open a query against one, and
buffer device addresses work — a build addresses its geometry by GPU address, so ray tracing
without addressing is not a configuration that exists. On Vulkan that is
`VK_KHR_acceleration_structure` + `VK_KHR_ray_query` with the feature bits actually enabled, not
merely the extensions listed. It is **false on MoltenVK**, which exposes neither, so macOS and iOS
run the distance-field tracer — and that is the configuration this project develops on.

⚠ **An acceleration structure holds surfaces, not distances**, and every honest limit follows from
that one sentence. A ray query answers *where the nearest triangle along this ray is*. It cannot
answer *how far is the nearest surface to this point*, because a position alone names no triangle.
So the tracer's point questions answer what `NoDistanceField` answers — `SampleField` says nothing
is near, `GradientField` says up, `OcclusionField` says fully open — and its shadow is hard, because
the penumbra a field's shadow derives from how *near* a march grazed is exactly the information a
hierarchy of triangles does not carry. A caller that wants a soft shadow and an exact trace composes
both tracers; that is what the slot is for.

## Using it

### Ask the device before you touch any of it

Every ray-tracing entry point on `IGraphicsDevice` throws `NotSupportedException` on a device
without the feature, deliberately, rather than returning an empty handle. A structure that cannot be
queried is not a fallback, and the fallback already exists.

```csharp compile
using Vixen.Graphics;

public static class Tracer {
    public static bool Hardware(IGraphicsDevice device) => device.Features.HasRayTracing;
}
```

### Sizes come from the device, over the input the build will use

There is no formula a caller may apply. `GetAccelerationStructureSizes` reads the *counts* in an
`AccelerationStructureBuildInput` and answers two numbers:

| Field | What it is | Who owns the memory |
|---|---|---|
| `Structure` | The structure's own size, which goes straight into `AccelerationStructureDescription.Size` | The backend, which allocates the backing buffer itself |
| `Scratch` | The build's working memory | You — a `Storage` + `ShaderDeviceAddress` buffer handed to `BuildAccelerationStructure` |

⚠ **Size and build must describe the same input.** That is Vulkan's own rule, and it is why there is
one `AccelerationStructureBuildInput` record for both levels rather than an overload per level: two
types would let the two descriptions drift the day one of them was edited. Only the counts matter to
sizing — the buffers may still be empty, and are usually filled right up to the build.

⚠ **Do not invent the size.** A caller who computes `Size` themselves gets a refusal at creation on
a good day and corruption on a bad one.

### The buffers a build reads

Every buffer a build touches must be created with both `BufferUsage.AccelerationStructureInput` and
`BufferUsage.ShaderDeviceAddress` — vertices, indices and instances alike. Addressability is a
property of the *allocation*, not of the handle, so it cannot be asked for later;
`BufferDescription.Validate` refuses the combination that omits it rather than letting the build
take an address the buffer was never placed for.

One vertex format and one index width, and that is the whole list: `float3` positions
`VertexStride` bytes apart, and `uint32` indices, three per triangle. Not a limitation being
apologised for — it is what every mesh in this engine already holds, and a format enum grows the day
a second format has a caller.

### Bottom, then top, on one queue

A top-level build reads the bottom levels its instances name, so their builds must be recorded
*before it, on the same queue*. Nothing else is asked of you: each `BuildAccelerationStructure`
ends with the barrier that makes the structure readable by ray queries and by later builds. Builds
are rare and coarse enough that a barrier per build costs nothing measurable, and the alternative is
a resource-state vocabulary every caller of a niche feature would have to learn before their first
query worked.

The geometry buffers themselves are yours to have filled and barriered, exactly as for any other
read.

### The instance record

`AccelerationStructureInstance` is 64 bytes and bit-for-bit `VkAccelerationStructureInstanceKHR` —
which is also `D3D12_RAYTRACING_INSTANCE_DESC`, the vendors having agreed for once. Four fields:

| Field | What it holds |
|---|---|
| `R0X`…`R2W` | A 3×4 transform, row by row |
| `IndexAndMask` | Low 24 bits the custom index a shader reads back; high 8 the visibility mask |
| `OffsetAndFlags` | Low 24 bits the hit-group offset (zero for ray queries); high 8 the instance flags |
| `BottomLevel` | The bottom level's GPU address, from `GetAccelerationStructureAddress` |

⚠ **The transform's rows are the upper three rows of a *column-vector* matrix** — each row is
(basis.X, basis.Y, basis.Z, translation) for one world axis. A row-vector `Matrix4x4` transposes on
the way in, and `FromRows` exists so that the transposition lives in exactly one place. Twelve
floats, or it throws.

⚠ **A visibility mask of zero is invisible to every ray**, which is what a zeroed struct gives you.
`Identity` sets the mask to `0xFF` and the transform to identity, so an instance built any other way
has to set the mask itself — a structure that builds cleanly, validates cleanly and returns nothing
but misses is almost always this.

### Reaching it from a shader

The top level is bound like any other resource: `DescriptorWrite.Acceleration(binding, structure)`,
against a binding the reflection reports as `DescriptorType.AccelerationStructure`.

On the Raven side it is a binding type called `AccelerationStructure`, which lowers to
`IrAccelerationStructureType` — a singleton, like the sampler type, because there is no element
type, no dimension and no format to parameterise. The emitters turn it into `accelerationStructureEXT`
under `GL_EXT_ray_query`, and into SPIR-V 1.4 with `SPV_KHR_ray_query`; only modules that actually
use it lift their version.

**There is no ray query object anywhere in the language or the IR.** `structure.Trace(origin, tMin,
direction, tMax)` is one intrinsic, and each backend synthesises the whole initialise / proceed /
committed-hit sequence behind it — which is what keeps mutable opaque locals out of Raven. The
contract is fixed so both backends implement one thing: ray flags *Opaque*, cull mask `0xFF`,
proceed to completion, and a `float4` answer:

| Case | `.x` | `.y` | `.z` | `.w` |
|---|---|---|---|---|
| Committed hit | distance | primitive index | instance id | `1.0` |
| Miss | `maxDistance` | `-1.0` | `-1.0` | `0.0` |

So `.w` is the hit test and `.x` is always a distance a march may safely take. The indices ride in
floats deliberately: a float is exact for every integer below 2²⁴, which is more primitives than a
bottom level may hold, so nothing is lost and no result struct has to exist in the IR, the
reflection and two backends for one intrinsic's sake.

### The referee

`QueriedField` is the same answers on the CPU, over a `TriangleBvh` built from the same triangles —
device-free, and written before any device gave them. A hardware ray query cannot be checked against
arithmetic; it can be checked against a traversal already held hit-for-hit against brute force, so
when a device disagrees the disagreement is the device's rather than the fixture's.

`QueriedHit` is one answer: `Hit`, `Distance` (the budget, on a miss), `Position`, and `Steps` —
which is always one, because a query *is* one step and every tracer here reports its cost.
`SampleField`, `GradientField` and `OcclusionField` are **static**: those answers belong to the
tracer kind, not to any one hierarchy, since every ray-query field answers them identically whatever
was built.

## Examples

The whole two-level build, sized and recorded:

```csharp compile
using System.Runtime.InteropServices;
using Vixen.Graphics;

public static class SceneStructure {
    // Three floats per position — the only vertex format a build reads.
    const int PositionStride = 12;

    public static AccelerationStructureHandle Build(
        IGraphicsDevice device,
        ICommandList commands,
        BufferHandle vertices,
        int vertexCount,
        BufferHandle indices,
        int indexCount,
        BufferHandle instances
    ) {
        var bottomInput = new AccelerationStructureBuildInput(
            AccelerationStructureKind.BottomLevel,
            Triangles: new(vertices, 0, vertexCount, PositionStride, indices, 0, indexCount)
        );

        var bottomSizes = device.GetAccelerationStructureSizes(bottomInput);

        var bottom = device.CreateAccelerationStructure(
            new(AccelerationStructureKind.BottomLevel, bottomSizes.Structure, "rock-blas")
        );

        // One identity instance, referring to the bottom level by the address the device names.
        var instance = AccelerationStructureInstance.Identity(device.GetAccelerationStructureAddress(bottom));

        device.Write(instances, 0, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref instance, 1)));

        var topInput = new AccelerationStructureBuildInput(
            AccelerationStructureKind.TopLevel,
            Instances: new(instances, 0, 1)
        );

        var topSizes = device.GetAccelerationStructureSizes(topInput);

        var top = device.CreateAccelerationStructure(
            new(AccelerationStructureKind.TopLevel, topSizes.Structure, "scene-tlas")
        );

        var scratch = device.CreateBuffer(new(
            Math.Max(bottomSizes.Scratch, topSizes.Scratch),
            BufferUsage.Storage | BufferUsage.ShaderDeviceAddress,
            MemoryAccess.DeviceLocal,
            "as-scratch"
        ));

        // Bottom before top, on one queue. Each build's trailing barrier is the ordering.
        commands.BuildAccelerationStructure(bottom, bottomInput, scratch);
        commands.BuildAccelerationStructure(top, topInput, scratch);

        return top;
    }
}
```

One scratch buffer serves both builds here because they are recorded in order and the first build's
trailing barrier covers the second's read. Two builds that could overlap need two.

An instance placed rather than identity — the twelve floats, spelled as rows:

```csharp compile
using Vixen.Graphics;

public static class Placement {
    // A rock at (12, 0, -4), scaled by two, with no rotation: each row is
    // (basis.X, basis.Y, basis.Z, translation) for one world axis.
    public static AccelerationStructureInstance Rock(ulong bottomLevel, int index) =>
        AccelerationStructureInstance.FromRows(
            bottomLevel,
            [
                2f, 0f, 0f, 12f,
                0f, 2f, 0f, 0f,
                0f, 0f, 2f, -4f
            ],
            index
        );
}
```

The shader that composes it. Nothing above this changes when it is swapped for the marcher — that
is the entire claim, and the reason the interface carries the traces rather than the passes:

```rvn
shader RayQueryField : IDistanceFieldSource {
    [PerFrame] var sceneStructure: AccelerationStructure

    func TraceField(origin: float3, direction: float3, maxDistance: float, maxSteps: int, threshold: float, stepScale: float): DistanceFieldHit {
        val answer = sceneStructure.Trace(origin, 0f, direction, maxDistance)

        if (answer.w > 0f) {
            return DistanceFieldHit(true, answer.x, origin + direction * answer.x, 1)
        }

        return DistanceFieldHit(false, maxDistance, origin + direction * maxDistance, 1)
    }
}
```

⚠ **The march's parameters are accepted and unread.** A query needs no step budget, no threshold and
no step scale — but the interface is one protocol serving two tracers, and the price of that is paid
here, once, rather than as a branch in every pass that composes a tracer.

The same questions on the CPU, which is what a device's answers get held against:

```csharp compile
using Vixen.Core.Mathematics;
using Vixen.Rendering.RayTracing;

public static class Referee {
    public static QueriedField Over(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices) =>
        new(new TriangleBvh(vertices, indices));

    public static QueriedHit Nearest(QueriedField field, Vector3 origin, Vector3 direction) =>
        field.TraceField(origin, direction, 64f);

    // The bias is the query's own minimum distance, not a step off the surface.
    public static bool Lit(QueriedField field, Vector3 point, Vector3 toLight, float lightDistance) =>
        field.ShadowField(point, toLight, lightDistance, 0.01f) > 0.5f;
}
```

⚠ **`QueriedField.Nothing` is `1e8f`, and that is an answer rather than a sentinel.** It is
`NoDistanceField.Nothing` by value — a step any march may safely take — so a kernel that composes
the hardware tracer and then samples the field walks forward instead of stalling on a zero.

### What is owed, and what it costs today

Named so the absence is a decision rather than a surprise:

- **The hit's true normal.** The query already returns the committed primitive's index, and the
  vertex buffer the structure was built from turns it into that triangle's geometric normal. Until
  that read lands, a cache hit through the hardware tracer **faces up** — which is the one gap here
  that costs image quality.
- **Refit.** A build per change is the baseline; updating in place rides the same
  `BuildAccelerationStructure` seam.
- **A surface-area heuristic** in the CPU referee's build. The median split is deterministic from
  the input alone, which is what makes it a referee; SAH is the optimisation measured against it.

⚠ **The device comparison skips where it cannot run.** The end-to-end test — geometry buffers
through both builds into an unmodified probe dispatch, against the referee — is gated on
`HasRayTracing` and therefore does not execute on MoltenVK. On this project's own hardware the
capability-detection tests carry the correctness burden, and the query comparison waits for a device
that can run it. Stated here rather than discovered later, because a test that has never failed
anywhere is a different claim from one that has passed somewhere.

## See also

- [Turning on dynamic global illumination](lit-path.md) — the tracer slot this plugs into, and the
  distance-field marcher that fills it everywhere else.
- [Lighting a scene in lux and lumens](physical-lighting.md) — the units the traced radiance is in.
- [Making everything cast a shadow](shadows.md) — the shadow maps a hard query shadow does not
  replace.
- [Meshes and materials, type by type](mesh-and-material.md) — where the vertex and index buffers a
  bottom level is built from come from.
