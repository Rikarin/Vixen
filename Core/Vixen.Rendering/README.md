# Vixen.Rendering

Turns a scene into ordered work lists: extract once into flat arrays, cull per view, sort per stage.

This is the spine of [docs/plan/06](../../docs/plan/06-rendering-pipeline.md) — Stride's
`RenderObject`/`RenderNode` model, which that document calls the part of Stride "worth taking most
directly". It is entirely CPU-side and deterministic, which is why the whole of it is tested without
a device.

## The shape of a frame

```
Extract   features pull the scene into RenderObjectStore — the one place references are touched
Cull      every object against every view, in parallel, into one bit each
Prepare   features fill data for what survived, which in a culled scene is far fewer objects
Sort      per (view, stage): collect visible objects and order them by a packed 64-bit key
```

The order is a **data dependency, not a convention**. Culling needs everything extracted, or a
late object is tested against a stale bitset. Preparation needs culling, or it loses its whole point.
Sorting needs preparation, because a feature's sort group may be something preparation resolved.
Reordering any pair produces a frame that is quietly wrong rather than one that fails.

## Three ideas taken from Stride, and what each buys

### `RenderObject` / `RenderNode`

Scene data is extracted once per frame into a flat array; per-view work is a list of indices into it.
One mesh in the camera's opaque stage and four shadow cascades is **five nodes over one object** —
the object's data was extracted once, and nothing downstream touches a scene graph.

A `RenderNode` is sixteen bytes and deliberately no more. It is the array a frame sorts, and sorting
is a memory-bandwidth problem long before it is a comparison problem.

### `RenderDataHolder` — per-feature arrays

The reason the renderer is extensible. A feature that needs per-object state does not extend a type
and does not add a field to `RenderObject`; it registers an array of its own, which the holder keeps
the same length as every other:

```csharp
protected override void Initialize(RenderSystem system) =>
    bones = system.Objects.Data.Register<Matrix4x4>();
```

Adding skinning therefore does not touch the mesh feature, and an object that is not skinned costs
**one unused slot rather than a branch**. Structure of arrays, so a job that reads only transforms
touches only transforms — which matters most in exactly the passes that read one small thing for
every object in the scene.

Ids are dense and stable: removal frees a slot rather than compacting, because compacting would move
objects and invalidate every registered array at once. The cost is holes, and it is cheap — a dead
slot is one predictable branch on a value already in cache.

### Stages are not passes

A **stage** is *which* objects and in what order. A **pass** is where they are drawn — a render-graph
node with attachments and barriers. One stage feeds several passes (an opaque stage draws into every
shadow cascade); one pass may draw several stages. Binding them together would mean a shadow map
needing its own copy of "the opaque list".

## The sort key

Grouping in the high 32 bits, quantised depth in the low 32. That one decision is what makes a
front-to-back sort *also* a state-change-minimising sort: comparing the whole 64 bits orders by group
first and by depth within a group, with no branch and no second pass.

Sorting by depth alone is the classic mistake — it makes a scene **slower the better it is culled**,
because the draw order stops correlating with pipeline state. A transparent stage leaves the group
out entirely, and that is not an omission: blending is order-dependent, so reordering two overlapping
draws to save a pipeline change would change the image.

Equal keys break ties by id, so a frame draws in the same order run to run — which is what a
golden-image test depends on.

## Culling

Per view, not per view per stage: an object either is or is not inside a frustum, and the stage mask
filters afterwards where it is one `and` on a value already loaded.

Parallel **over objects, not over views** — a frame has a handful of views and tens of thousands of
objects, so splitting by view leaves most threads idle. A `ulong` holds 64 objects' bits, so batches
are whole words: every thread owns the words it writes, and no lock or atomic appears anywhere.

Three rejections come before the frustum test, each cheaper than it and each removing objects it
would have accepted: dead slot, no stage in common, beyond the view's own distance.

## What is not here yet

Recording, materials, lighting, shadows and the `GraphicsCompositor` asset. Recording needs the
effect system, which needs `ParameterCollection` — see
[Vixen.Shaders](../Vixen.Shaders/README.md). GPU-driven culling is a second implementation of
`VisibilityGroup` behind the same interface, which is why that interface is bits rather than a list.

## Testing

`Vixen.Rendering.Tests` holds culling to a **brute-force oracle over randomised scenes** (doc 06's
testing table asks for exactly that), pins that the parallel path agrees with the inline one word for
word, and asserts that a settled frame of 10 000 objects through extract → cull → sort **allocates
nothing**. The last one is the guard that a change starting to allocate per object per frame fails a
test rather than appearing months later as a GC spike nobody can attribute.
