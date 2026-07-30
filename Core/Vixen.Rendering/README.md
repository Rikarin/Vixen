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

### And the same answer, computed on the device

`IVisibilityGroup` is the seam doc 06's "parallel *or GPU*" asks for, and `GpuVisibilityGroup` is the
second implementation: it packs every object into a 32-byte record, every view into a 128-byte one,
and dispatches `Library/Pipeline/Culling.rvn`. `RenderSystem.Visibility` is settable, so choosing is
one line in a host and nothing downstream knows which it got.

**The interface is bits rather than a list**, which is what makes two implementations of it
interchangeable — sorting walks words and `Hide` clears one, and neither works against an answer
shaped like anything else.

**One invocation owns one word**, thirty-two objects of it. An invocation per object would need
thirty-two of them to agree on one store, which means an atomic `or` per object; owning a whole word
removes the sharing rather than synchronising it — the same trade the CPU path's batch size being a
multiple of 64 makes, and the same one `ClusterCulling` makes. Thirty-two rather than sixty-four
because a 64-bit integer is optional on Vulkan and absent from WebGPU; two device words *are* one
host word, so reassembly is a shift.

**One dispatch for the whole frame**: `x` covers a view's words and `y` is the view, so a camera and
four shadow cascades are one submission. That is why the object count and the word count travel in
the *view* record — a dispatch covering every view has no single count to put in a push constant, and
a uniform block rewritten per view would mean a dispatch per view. The shader declares no scalars at
all.

**The readback is a stall, and paying it is the design.** The interface promises the bits are this
frame's when `Cull` returns, and sorting, preparation and recording all read them inside that frame —
so the alternative is not "no wait" but an answer one frame old, which is geometry popping at the
edges of a moving camera with nothing to say why. What makes it affordable is the queue: the dispatch
goes to `ComputeQueue`, so on hardware with a real async compute queue the wait does not drain the
frame being rendered. The RHI has no fence, which is why the wait is queue-wide rather than on this
submission alone. It is also optional: `ReadBack = false` pays none of it and moves the decision into
the draw call instead — see *And drawing from it without ever asking* below.

**It falls back rather than failing.** A device that cannot run compute at all, no effect system, no
pipeline cache, a variant that has not compiled yet, a provider that does not report set layouts:
each of those is a frame that still has to be drawn, and each culls on the CPU instead, with
`CulledOnDevice` saying which happened. The capability is asked rather than found out — creating a
compute pipeline where there is no compute *throws*, and an exception out of the middle of `Cull` is
not a fallback for the target the fallback exists for. That is
also what a GL or WebGL target gets, which is what doc 06 means by "the CPU path remains". The
fallback costs nothing to provide because the GPU group *is* the CPU group plus a front end — it
composes one and writes the readback into its storage, so there is one bitset implementation in the
engine rather than two that agree.

The host keeps a copy of the shader's arithmetic in `GpuCulling`, for the reason `ClusterGrid` keeps
a copy of the froxel grid's: the two sides agree by construction or not at all, and a copy that runs
on the CPU is what lets a test say they do without a GPU in the room.

**The names and the binding indices are not a copy.** All three passes publish reflection beside the
shader — `Culling.reflect.json` and its two siblings — and the host binds through the constants
generated from it. A binding index is declaration order within a set, so adding a buffer above
another renumbers it; a literal in C# survives that and a generated constant does not. It matters
more here than elsewhere because of how this fails: a name the shader no longer has does not throw,
it makes the group fall back to the CPU, on every frame, silently.

### And what the frustum cannot answer

The frustum says what is in front of the camera; it says nothing about what is *behind a wall*. That
is the second half of doc 06's line, and it is `HiZPyramid` plus the `Occlusion` permutation of
`Culling.rvn`.

**A pyramid of minima, because depth is reversed.** Near is 1 and far is 0, so the smallest value in
a tile is the furthest surface drawn in it — and "is this behind everything" can only be asked of the
furthest one. `HiZReduce.rvn` reduces 3×3 rather than 2×2: a mip chain halves by *flooring*, so an
odd level leaves a trailing row that a 2×2 block never reads, and a missed occluder raises the tile's
minimum, which is the direction that culls something visible. Reading a neighbour's texel only lowers
it, so the extra ring is free in correctness and costs five taps.

**Level 0 is half the depth buffer**, not a copy of it — the first dispatch reduces rather than
copies, which takes a full-resolution level out of both the memory and the chain and costs nothing an
occlusion test would notice.

**The test errs towards keeping the object, four times over**: the box around the sphere is projected
rather than the sphere; anything reaching behind the near plane is kept outright, because its
projection wraps around the screen rather than growing; the level is the one where the rectangle spans
at most two texels, so four taps cover all of it; and the comparison is the object's *nearest* point
against the tile's *furthest* surface. Each of those, written the other way round, culls something
that was visible — which is a hole in the world, not a slow frame.

**It is a frame old, and per view.** A pass that runs before anything is drawn can only have last
frame's depth, so the rectangle is projected with the matrix that depth was drawn with — which
`GpuVisibilityGroup` remembers per view index, and drops whole whenever the number of views changes,
because a frame that added a view has renumbered every view after it. A view with no remembered matrix
is frustum-only rather than wrong, which is what makes the first frame of anything safe.
`OcclusionTested` says whether it happened at all.

### Two phases, and the frame of staleness they remove

A frame old is a real artefact: an object hidden last frame and visible now is drawn one frame late,
which is a pop at the trailing edge of whatever the camera moved past. No amount of conservatism in
the test removes it, because the information was not in the frame. **Two-phase culling puts it
there** — cull, draw, rebuild the pyramid from the depth those draws left, cull again, draw what the
second answer found. `GpuVisibilityGroup.TwoPhase` turns it on and the `Late` permutation of
`Culling.rvn` is the second dispatch.

**The second answer is a difference, not an answer.** The late variant reads the visibility word
before it writes it, clears the bits the main pass already drew, and writes only what is visible now
and was not drawn then — because that is what a second set of draws has to be given. The union would
draw every visible object twice. One invocation owns a whole word, so the read-modify-write is one
thread's; the same ownership that makes the main pass need no atomic.

**It is one buffer and one argument pass, run twice.** The late dispatch overwrites the bits and
`GpuDrawArguments` rewrites the same argument buffer from them, so `MeshRenderFeature` needs to know
nothing at all: the late draws are the *same draws*, reading a buffer whose contents changed between
the two passes. What orders read-then-overwrite-then-read is the barrier `RecordDispatch` places,
which is also why the whole thing lives in one command list.

**A frame with no pyramid still runs the late pass.** It gets an empty difference, and that is the
answer — skipping the dispatch would leave the main pass's bits in the argument buffer for the late
draws to find, and every visible object would be drawn twice. So the key names `Late` by phase alone
and never gates it on occlusion.

**It needs the readback off**, because the two dispatches straddle a set of draws and the readback
path submits and waits before any of them are recorded. With it on, no late dispatch is prepared and
the frame is culled exactly as a one-phase frame. `LatePhaseRan` says which happened, for the reason
`OcclusionTested` does: a two-phase cull quietly running one phase looks exactly like one whose
second phase never finds anything, and those are opposite conclusions.

**Two runs of a thing in one frame is two descriptor rings.** A set may not be rewritten while a
submitted command buffer references it, and two dispatches before one submission are two rewrites —
which sizing a ring to frames in flight alone does not cover. `HiZPyramid.BuildsPerFrame` and
`GpuDrawArguments.DispatchesPerFrame` are the depths, and `CompositorBuilder` sets them by *counting
the nodes the document placed* rather than by inferring them from the late node, because a document
may reduce twice without culling twice.

**`Compositor/HiZRenderer` is why this is a node.** Depth is a graph resource: a dispatch that sampled
it without declaring the read would be ordered against nothing and would read it in whatever layout
the last pass left. The node declares `Reads(depth)` and `SideEffect()` — the second because what it
writes outlives the graph and so, as far as the graph can see, it produces nothing.

### And drawing from it without ever asking

`GpuVisibilityGroup.ReadBack = false` is the end of the path: no submission, no wait, and the bits
never leave the device. What replaces them is `GpuDrawArguments` — `Library/Pipeline/DrawArguments.rvn`
turning the bitset into the five numbers `DrawIndexedIndirect` reads — and
`Compositor/GpuCullingRenderer`, which records both dispatches at the head of the frame's own list.

**Both dispatches in the frame's list, not a submission of the group's own.** With no wait, the only
ordering this RHI can express is a barrier between two things in one queue: it has no fences and no
semaphores. That is the whole reason the culling dispatch became something a node records rather than
something `Cull` submits.

**And the same shader culls clusters.** `Culling.rvn` carries a `Clusters` permutation that turns the
per-object dispatch into a hierarchical walk over a cluster DAG — one workgroup per instance per view,
a `groupshared` queue, and a barrier per round. It is a permutation rather than a shader of its own
because objects and clusters are the same hierarchy at different depths, and because two
implementations of "visible against last frame's pyramid" is two places for the definition to drift:
`Occluded` takes a *sphere* now, and the object cull and the traversal each hand it one.

Raven refuses a second compute entry point in one shader, so the two dispatch shapes are one entry
point branching on the permutation. That turns out to be the better arrangement: the branch is folded
before lowering, so the object variant provably carries no queue, no barrier and no shared memory at
all, and `LibraryTreeTests` asserts it.

**A rejected subtree costs one test**, which is the whole point and the whole difference from the
object cull. A cluster that fails the frustum, the cone or the pyramid takes its children with it, so a
mesh of a hundred thousand clusters behind the camera costs as many tests as it has roots.

**The error is projected at the group's bound, not the cluster's**, and that is the one decision here
whose failure is a crack. A group's simplification produces several parents, each of which replaces
*all* of the group's children, so all of them have to refine or none of them do. They share an error;
they also have to share the distance it is projected at, because their own bounds are in different
places. `GpuClusterCullingTests` found this by comparing the traversal against a brute-force cut over
random DAGs — which is what that comparison is for.

### And drawing what the traversal chose

`GpuClusterVisibility` is the traversal's device side — the DAG in buffers, the dispatch, and the page
requests coming back — and it is deliberately `GpuVisibilityGroup`'s shape: a group that owns the
buffers, a feature (`VirtualGeometryRenderFeature`) that extracts what it walks, and a node
(`ClusterCullingRenderer`) that puts the dispatch in the frame. `GpuClusterRaster` and
`Compositor/VisibilityBufferRenderer` then draw it, through `Library/Pipeline/ClusterRaster.rvn`.

**One indirect draw for the whole frame, and it needs no optional capability.** `DrawIndexedIndirect`
with a single argument structure whose `instanceCount` is a four-byte copy out of the visible list's own
count word — so a frame that drew a million clusters and a frame that drew none are the same command.
`DrawIndexedIndirectCount` exists for a *list* of argument structures, which is what the compacted
per-object path wants and this does not: no `HasDrawIndirectCount`, no `HasMultiDrawIndirect`, no
64-bit atomics and no compute, which is what makes the visibility buffer the portable baseline.

**The index buffer holds no mesh's indices.** It is `0, 1, 2, …` up to three times the largest cluster,
so `SV_VertexID` reaches the vertex stage unchanged and the real corners are bytes fetched out of a
page once the stage knows which cluster it is drawing. A cluster with fewer triangles has corners left
over and they collapse to a degenerate triangle, which is the price of one command instead of one per
cluster.

**The visible word carries the instance index, not its cluster base**, and that distinction is not
cosmetic: a raster needs the transform as well as the record, and an index reaches both. **And the
residency bitset became a slot table** — the traversal only asks the yes-or-no question, but the raster
needs the slot, and two tables that have to agree about whether a page is present is how a cluster comes
to be drawn out of a slot holding another page's bytes.

**A pixel's identity is biased by one so the target can clear to zero.** A clear colour is four floats
in every API the RHI wraps, so an integer target cannot be cleared to all ones — and zero has to mean
"nothing covered this pixel" rather than "the frame's first cluster, its first triangle".

**And with no wait, every descriptor set is a ring.** A set a submitted command buffer still
references may not be written — `VUID-vkUpdateDescriptorSets-None-03047` — so all three classes hold
one set per frame in flight and advance with the frame, which is the invariant `DescriptorAllocator`
and `UploadBuffer` are already built on. The readback path hides this behind its wait, which is
exactly why it stayed hidden: it takes two frames in flight to see, and every test that submits and
waits is a test that cannot.

**It zeroes instance counts, and compacts when it is asked to.** Zeroing is the floor and the
default: the buffer holds one record per object slot *at that slot*, a culled object gets zero
instances, and every API defines that as a draw that fetches and rasterises nothing. The cost is a
command submitted per object; the saving is the round trip.

`Compact` appends survivors instead, behind the `atomicAdd` Raven has had since the atomics landed,
and `MeshRenderFeature` draws the run with one `DrawIndexedIndirectCount` whose count comes out of a
buffer the host never reads. **This used to be recorded here as blocked on two things, and neither
exists now.** The device-supplied count is `GraphicsDeviceFeatures.HasDrawIndirectCount`; the shared
bindings are `GeometryBuffer`, the material records and the transform records together, which between
them mean a run of objects binds nothing at all between its draws. Zeroing stays as the fallback,
because it is what a device without the capability — and WebGL2 forever — still does.

**The host's answer becomes conservative, and that is the one place the two groups differ.** With the
readback off, `Words` holds every live object the view's stages want — everything that *could* be
seen — so the work list is a superset and the GPU removes the rest. Nothing is drawn that should not
be; what costs is recording draws that turn out to be empty. It is opt-in for exactly that reason,
and `Hide` still works, because a bit cleared on the host removes the object from the list entirely.

**A document turns all of it on.** `!GpuCulling` at the head of the frame and `!HiZ` after whatever
fills depth, with `readBack` and `indirectDraws` as the two flags — and a second `!GpuCulling` with
`phase: Late` after the `!HiZ`, followed by a pass that loads rather than clears, for the two-phase
form. Nothing in the file says "two-phase": the ordering *is* the feature, which is why it is
expressed by where a node sits. The builder makes the two
assignments a file cannot: `RenderSystem.Visibility` becomes the group, and every
`IDrawArgumentSource` feature is handed the arguments. Both are things a host placing the node by
hand has to remember in the same breath, and forgetting either is a frame that culls on the CPU or
draws everything, with nothing to say why. The resources stay host-supplied — a visibility group
holds device memory across frames and a file cannot make one — which is the same division
`descriptors` and `samplers` already have, and what lets one document run on a target with no compute
at all: the nodes build, and do nothing.

**The templates are filled by a node, not by a feature's `Prepare`.** A root feature's `Prepare` runs
before its sub-features', so an instancing batch's size and first instance — two of the five numbers
— do not exist yet. A node's `Build` runs after the whole of `RenderSystem.Draw`, which is the first
moment they all do. `IDrawArgumentSource` is the seam; `MeshRenderFeature` implements it and draws
indirectly whenever the arguments cover its object and view, and directly whenever they do not.

## Recording

`RenderSystem.Record(view, stage, context)` walks the sorted list and hands each feature its own
nodes — in **contiguous runs, not one node at a time**. The list is already ordered by a key whose
high bits are the sort group, so nodes sharing a pipeline are adjacent; a run lets a feature bind
once and draw many, which is the entire point of having sorted by group.

Runs follow the **sort order** rather than gathering each feature's work together. Gathering would
save a handover and reorder the stage — which for a transparent stage means reordering blended draws
and changing the image.

The render pass belongs to the caller, not to this. One pass may draw several stages, and the
attachments belong to the render graph — a stage that opened its own pass could not be one of
several in a subpass-fused mobile path. The Null backend refusing a draw outside a pass is what says
so out loud. `Compositor/RenderPassRenderer` declares that pass in a composed frame, and the render graph opens
it.

One `(view, stage)` at a time, because that is the unit a caller can put on its own thread: each gets
a `RenderDrawContext` with its own command list, and they share nothing that is written.

## The first concrete feature

`Features/` holds `MeshRenderFeature` with two sub-features, and between them they are the worked
example of everything above:

| | Owns | Registers |
|---|---|---|
| `MeshRenderFeature` | the draw calls | `MeshDraw` — buffers and a range |
| `TransformRenderFeature` | where the object is | `Matrix4x4` world |
| `MaterialRenderFeature` | which shader variant | a material index and a variant index |
| `ForwardLightingRenderFeature` | which lights reach it | `LightAssignment` — an offset and a count |
| `SkinningRenderFeature` | its bone palette | `BonePalette` — a first bone and a count |
| `InstancingRenderFeature` | its copies | `InstanceBatch` — a first instance and a count |

**None of them references the others' data**, which is the arrangement working rather than a
coincidence of it. Lighting, skinning and instancing were each added after the mesh feature and
changed nothing in it — instancing changed four lines, to pass a draw-call argument it now has a
source for. A UI quad or a particle billboard has bounds and needs culling but has no world matrix at
all — putting one on every object would make them carry 64 bytes to say nothing.

A **skinned, instanced mesh** is the case an inheritance hierarchy needs a class for. Here it is two
independent flags on one object, and neither feature knows the other exists.

## The second one, which makes its own geometry

`ParticleRenderFeature` draws the particles of a `Vixen.Vfx` system, and it is the first feature whose
geometry does not exist until the frame asks for it. A mesh arrives as buffers and the feature binds
them; an effect arrives as a few thousand positions that were different last frame, so `Prepare`
expands each particle into a camera-facing quad, appends it to one vertex buffer shared by every effect
in the frame, and records where the run is. `Draw` then binds that buffer **once** and reaches each
effect's run through the draw call's vertex offset — a hundred effects are a hundred draws and one
binding.

The dependency runs one way: `Vixen.Rendering` references `Vixen.Vfx`, and `Vixen.Vfx` references no
graphics at all. The expansion itself lives over there, in `VfxGeometryBuilder`, which is what lets
"where are the four corners" be a unit test instead of a screenshot.

**Two limits, both deliberate.** The expansion is on the CPU, where doc 06 eventually wants a compute
shader and an indirect draw — this works everywhere, needs no compute, and is what the GPU path will be
checked against. And it expands once, for one view, so an effect drawn into a second view gets quads
facing the first one's camera; that is fine for a reflection and wrong for a shadow caster, so
particles do not belong in a shadow stage until the GPU path removes the limitation rather than working
around it.

It shares `UploadBuffer` with skinning and instancing — the ring per frame in flight, the staging, the
single write — which needed one change to serve it: the buffer's usage became a parameter instead of
always being `Storage`.

**Three kinds of draw, and the binding is keyed on which.** A billboard binds the shared vertex buffer
and the static quad index pattern; a **ribbon** binds the same vertices and a *per-frame* index buffer,
because a strip's triangles depend on where each ribbon ends and a ribbon ends wherever a particle
died; a **mesh** binds the mesh's own buffers at slot 0, this feature's instance stream at slot 1, and
draws the mesh once instanced by however many particles are alive. A frame of nothing but billboards
still binds once — only a frame that mixes kinds pays to switch.

A mesh particle goes through the *mesh's* vertex layout rather than the particle one, because its
vertices are the mesh's and only the instance stream is this feature's. Sharing the billboard layout
would put a mesh's normals through a shader expecting a texture coordinate.

The bug that cost the most here was an early-out: `Draw` returned when the shared vertex buffer was
invalid, which is true of every frame that draws nothing but instanced meshes. A whole renderer that
silently drew nothing, from a guard that had been exactly right when there was one kind of draw.

## The third one, which shares the interface's arithmetic

`SpriteRenderFeature` draws sprites: a textured quad in its own plane, or the nine a border cuts it
into, or the many a tiled fill repeats. Doc 06's row for it says *shares the UI batcher*, and what is
actually shared is `NineSlice` in `Vixen.Core.Mathematics` — the cut of a rectangle into nine, which is
also what `UiGeometryBuilder` stretches a panel's background with. The two halves cannot share more
than that: `Vixen.Ui` describes a frame without a device and this describes a device without an
element tree. What they do share beyond the arithmetic is the shape of the answer, which is the part
that matters — quads appended to one buffer, one binding, a draw per object reaching its own run
through the vertex offset.

**Local space, and that is the whole difference from the particle feature.** A sprite's quads are
built around its pivot with no camera in them, so the geometry is the same for every view and is built
once a frame rather than once a view; where the sprite *is* comes from `TransformRenderFeature`
pushing a matrix, exactly as a mesh's does. A camera-facing sprite is therefore not this feature's
job — that is a billboard, it has to be built against a view, and an object that answered "where am I"
twice would draw a different scene in a shadow pass than in the camera.

| Type | Is |
|---|---|
| `Sprite` | a region of a texture in texels, a pivot, a nine-slice border and a pixel density. No texture handle: the material binds the atlas, and what a sprite needs is the texture's *size*, which is the denominator that turns texels into UVs |
| `SpriteSheet` | many sprites on one texture, cut by `Grid` or by an importer, looked up by name through a frozen dictionary built on first use |
| `SpriteAnimation` | indices into a sheet, a frame rate and a wrap. Sampled by time and never stepped, so nothing holds a playhead and rewinding is passing a smaller number |
| `SpriteGeometry` | the expansion, as a pure function — checked against numbers rather than a screenshot, the same bargain `VfxGeometryBuilder` makes |

**Tiling is here and not in `Vixen.Ui`, for a reason that decides the split.** How many times a strip
repeats is destination ÷ natural size, and the natural size is in texels — a draw list does not know
how big a texture is, so a nine-slice there is stretched only. A sprite carries its own pixel density,
so it can count. The count is capped at `SpriteGeometry.TileLimit` and a cell that would exceed it is
stretched instead: the repeat count is a property of how small somebody drew their artwork, not of the
scene, and one sprite is not allowed to size the frame's vertex buffer.

**Painting order is `SpriteAppearance.SortGroup`, and the stage has to sort `ByGroup`.** Sprites are
blended quads all the same distance from the camera, so what is in front is a decision an artist makes
and a depth buffer cannot make for them — a `FrontToBack` stage would break the tie by object id.

One trap worth naming, because the particle feature has it and this one does not: a per-object array is
native memory the store zeroes when it grows, so an unbiased sprite index would make every object that
has never been given a sprite draw the first one somebody registered. The stored index is one higher
than it is, and zero means *no sprite*.

`MaterialRenderFeature` is where the shader half of the engine meets the renderer half: preparation
turns a material's `ParameterCollection` into an `EffectKey`, resolves it, and remembers the answer
per object — so by recording time "which shader" is an array lookup. It resolves **per material, not
per object**: ten thousand objects sharing twenty materials resolve twenty times.

A material is more than its values, and the other half is its **composition**: which shader fills each
of the pass's `compose` slots. It travels in the `EffectKey` beside the permutations, because two
materials with the same shader name and the same permutations but different features are different
code — a key blind to that hands the second material the first one's shader, which is a wrong image
with nothing logged anywhere. A stage that overrides the shader says whether its own shader composes
(`RenderStage.ShaderComposes`), which is false by default: `DepthOnly` declares no slot, so a prepass
that carried the composition anyway would compile one byte-identical variant per material in the
scene. See [Materials](#materials) for where a composition comes from.

**The sort group comes from the resolved effect**, and that is what closes the loop. Objects that
will bind the same pipeline get the same group, the key puts groups above depth, they land adjacent,
and the mesh feature sees one run and binds once. Break any link and four objects sharing a material
become four pipeline binds — which is asserted, not assumed.

The transform goes out as **push constants** by default: the smallest, most per-draw thing a frame
has, with no descriptor, no upload-ring allocation and no offset to track. A `mat4` is 64 bytes
against Vulkan's guaranteed 128, and Raven warns at `RVN3007` if a shader's block exceeds that, so
both sides agree about the budget. The matrix is sent unchanged — see the `Matrix4x4` note in
[Vixen.Shaders](../Vixen.Shaders/README.md).

**Or as a record, when the frame is merging draws.** `EnableRecords` puts every matrix in one buffer
at the object's own slot and carries the slot in the draw's `firstInstance`; the shader's half is
`UseTransformRecords`, and both move together because either alone draws a wrong picture rather than
failing. What it buys is not bandwidth — it is that a push constant is *per command*, so a run of
objects that bind nothing between them cannot become one command while each still has a matrix to
push. See [Compacted draws](#what-is-not-here-yet).

## The components a scene places

`Ecs/` holds this assembly's ECS components and the systems that bridge them, the arrangement
`Vixen.Physics` and `Vixen.Audio` already use: the subsystem references `Vixen.Ecs` and `Vixen.Engine`
and owns both halves. `Vixen.Engine` references no graphics API, so the arrow points one way only.

```csharp
Lights.Attach(world, entity, LightKind.Directional);   // aimed with the entity's transform
MeshRenderables.Attach(world, entity, MeshRenderables.Default(mesh));
PrimitiveShapes.Attach(world, entity, PrimitiveKind.Cube);

loop.Add(new LightExtractionSystem(lighting));         // fills lighting.Lights every frame
```

All three carry `[Component]` and `[DataContract]`, which is what declares them to
`SceneComponentRegistry` — so a `.vxscene` places a light, the inspector draws one, and a compiled
scene carries one, with no registration call anywhere. All three spent a while as editor-side
components for want of exactly this.

**`Light` is everything a light is except where it is.** Position, direction and the axis a tube or a
rectangle runs along all come from `WorldTransform`, which is what makes a spot light something you aim
with the rotate gizmo rather than by typing a vector — and what stops a file saying two different things
about where one points.

`LightExtractionSystem` is the bridge, and it is a copy rather than a translation: `Light`'s fields line
up with `RenderLight`'s, plus the basis folded in from the transform. It runs in `SystemPhase.PreRender`
and declares `Read<WorldTransform>`, so the dependency graph puts it after `TransformSystem` — which is
what makes a light's position this frame's rather than last frame's. Naming the phase alone would not
have been enough.

⚠ **The list is rebuilt every frame rather than mirrored.** A light has no handle to keep, so there is
nothing to reconcile and a destroyed entity cannot leave a light burning. That is the opposite trade
from `PhysicsBody`, and the difference is exactly that a body is state and a light is not.

⚠ **`MeshRenderable` is authored, compiled, loaded — and not yet drawn.** Resolving it is done:
`ContentCatalog.TryGetAddress` turns its `AssetReference` into an address and `AssetManager.LoadAsync`
turns that into a `MeshData`. What is missing is the extraction system, which needs a residency cache
over `GeometryBuffer` — one slice per mesh, shared by every entity drawing it — and a material asset
resolved to a `Material`. `LightExtractionSystem` is the same shape of thing and is finished, so the
pattern is in the tree.

## Lighting

`ForwardLightingRenderFeature` is the fourth sub-feature, and it registers an eight-byte
`LightAssignment` per object: where that object's light block starts, and how many lights it holds.

**Lights are selected against objects, never against the view frustum.** That looks like a missed
optimisation and is a correctness requirement — a lamp behind the camera lights everything in front
of it, so culling lights by the frustum would darken exactly the objects that are on screen. The
frustum has already done its work: the objects considered are the ones that survived it.

Range is measured to the sphere's **surface**, and the ranking is the same windowed inverse-square
falloff the fragment will evaluate — so "the eight brightest" means the same thing on both sides.
When more lights reach an object than the block holds, the dimmest are dropped; that minimises the
error and it is also what pops, which is the argument for clustering rather than for a longer list.

The directional light is not in anybody's list. It reaches everything, so paying list traversal for
it would be paying for nothing — `Library/Pipeline/ForwardPlus.rvn` takes it as its own uniform for
the same reason, and `Sun` is what a per-frame binder reads.

**One buffer, one descriptor, a per-draw offset.** Every object's block lives in one uniform buffer
reached through a `DynamicUniformBuffer`, so a thousand objects cost a thousand offsets rather than a
thousand descriptor sets — the first real user of the mechanism `DescriptorKind` documents, and the
avoidance of the single most common reason a Vulkan renderer ends up slower than the D3D11 one it
replaced. The block's layout is `PunctualLight` from `Library/Shading/Lighting.rvn`, byte for byte:
64 bytes, no padding, so the upload is a blit. A test asserts the offsets rather than trusting the
comment, because the failure is silent — the shader reads whatever is at the offsets it was compiled
for.

**One set per frame, out of a `DescriptorAllocator`, not one set for ever.** The buffer is recreated
when the scene outgrows it, and a set held across frames would then have to be rewritten to point at
the new one — a write to a set the frames still in flight are reading, which most drivers execute
without a word and the validation layers only catch with synchronisation validation switched on. The
feature ticks a ring exactly `FramesInFlight` deep from its own `Prepare`, so the set a frame writes
is one no frame in flight can be reading. The *buffer* needs no such care and gets none:
`IGraphicsDevice` defers every destruction until the frames that could reference the handle have
retired, which is the backend's job precisely because a renderer cannot know when that is. A test
grows the buffer mid-run and asserts the property rather than the mechanism — no set is written
twice inside a window of `FramesInFlight` frames, growth frame included — and a second one pins that
the ring settles at `FramesInFlight` sets and leaks no buffers.

## Materials

A material is a **tree of features**, not a fixed parameter block: a workflow, optionally a normal
map, optionally a clear coat, and a shading model that says what the surface does with light.
`MaterialCompiler` turns that tree into the two things the renderer already knew how to carry — a
`ShaderComposition` naming the shader that implements each feature, and a `ParameterCollection` keyed
by the names those shaders will have once composed.

Doc 06 asks for Stride's composable model. What makes it composable here is Raven's `compose` rather
than a mixin resolver: `ForwardPlus` declares `compose val surface: IMaterialSurface` and
`compose val shading: IShadingModel`, each feature is a shader implementing one of those protocols,
and resolution happens when the effect is compiled — so a material with no clear coat contains no
clear-coat code at all, rather than a branch that is always false.

**Two slots rather than one**, because the two vary independently: a clear coat over a
metal-roughness base and over a specular-glossiness one is the same lobe. A surface feature writes
channels into `MaterialData`; a shading model reads them and evaluates lobes. Five of each is ten
shaders where folding them together would be twenty-five — and it is the only place cel shading can
live, because a stylised material keeps its base colour and its normal map and changes only the
response.

**The chain, and why it is one shader and not five.** A pass has one `surface` slot and a material has
several features, so `CompositeSurface` composes eight of them in order, each reading the surface as
the previous one left it. Slots a material does not use take `IdentitySurface`, which contributes
nothing. That shape is forced by two properties of `compose` worth knowing:

- Every declared slot in a compilation must be bound, reachable or not (`RVN2073`). Chains of two
  through six would make every material bind twenty slots it never calls.
- A composed shader's parameters belong to its **type**, not to the slot it filled. A
  `CompositeSurface` nested in a `CompositeSurface` would be one chain, not two.

The same rule is the sharp edge in the whole model, and it is why the compiler refuses a material that
uses one feature twice. Two slots bound to one shader compile perfectly, into a material where both
read the same values — so a two-layer blend of one workflow is one layer drawn twice, and the artist
who painted two colours sees one. That is a wrong image with no error anywhere, which is exactly the
kind this codebase would rather fail on.

**Layering is therefore two mechanisms.** `BlendSurface` composes two *different* surfaces and mixes
their channels by a weight. `MaterialLayersSurface` is N layers of one workflow held in an array, with
`LayerCount` as a permutation so the loop unrolls and a two-layer material's block holds two layers —
which is the case terrain and decals actually want, and the one composition cannot express.

**The parameter names are predicted, not read.** Raven qualifies a composed shader's parameters by the
path of types they were reached through, so a base colour inside the chain is
`CompositeSurface.MetalRoughnessSurface.baseColor`, and the engine qualifies every key by the shader
that owns it, giving `ForwardPlus.CompositeSurface.MetalRoughnessSurface.baseColor`. The compiler
works that out without a compiler in the process, because a material is authored and serialised on
machines that never compile a shader and a shipping build must build the key that finds a baked effect
without linking Raven at all. A rule written down twice is a rule that drifts, so
`Raven/Library/Pipeline/ForwardPlus.reflect.json` is checked in — regenerated and compared on the
compiler's side, and read back on this side by `MaterialReflectionTests`, which holds the prediction
against it in both directions.

### A material binds itself

A material knows it has a texture called `albedo`. Which binding index that is belongs to the compiled
shader — Raven assigns it from declaration order within a set, so adding a texture above it renumbers
it — so until the binding plan reached the runtime a host had to write the number down and hand over a
finished descriptor set.

Give `MaterialRenderFeature` a `Device` and a `DescriptorAllocator` and it writes the set itself: the
uniform block through `EffectConstants`, and every texture, sampler and storage buffer looked up in
`Effect.Bindings` by the shader's own name for it. The same fix, and the same argument, as the one that
made a compositor node's bindings authorable. Leave either null and nothing changes — `DescriptorsOf`
falls back to `Material.Descriptors`, which is what a host with a bindless table or a texture array
still wants.

**Per variant, not per material.** A permutation can fold a texture out of the shader entirely, and a
set written for the variant that has it does not fit the layout of the variant that does not. It is
also what keeps a depth prepass from binding anything: its effect declares no per-material layout, so
there is nothing to write.

### A material's texture as a value

Doc 06 says materials are values and not resources, and names what that cost: a feature that samples
needs a binding index only the compiled shader knows, so a material feature could carry channels and
could not carry a texture.

With a `BindlessTable` it needs no index of the shader's. Give `MaterialRenderFeature` a `Textures`
table and a `TextureIndices` pairing, and each material's texture takes a table slot whose number is
written into the material's own parameters — from where `EffectConstants` fills it into the block out
of the same offset table it fills the base colour from, with no idea that this particular `uint` means
a descriptor. The shader declares `var albedoIndex: uint` and samples `textures[albedoIndex]`.

**The pairing is explicit**, for the reason `PermutationSources` gives about its own: a shader's
parameter name and a material's texture name belong to different things, and a convention that
stripped `Index` and matched the rest would guess. An unmatched pair leaves the index at zero, which
is a valid slot holding some other material's texture.

**Per material, not per variant** — the one thing in this class that is. A permutation can fold a
texture out of the block but cannot change which texture the material carries, and the table is
global, so indexing per variant would take two references to one view and release neither.

⚠ **And idempotently, because this runs every frame.** A table asked for the same view sixty times a
second raises a reference count nothing lowers, and the symptom is not a wrong picture — it is a
table that fills up after a few minutes of play and then refuses a texture on a machine with
descriptors to spare. A settled material costs one dictionary hit, no table write and no upload.

Leave the table null and none of this happens, which is the non-bindless path exactly as it was: what
runs on GL, on WebGL2 and on MoltenVK below argument-buffer tier 2.

**Every binding or none.** A material that set no `albedo` gets no set at all, rather than one with a
hole in it. A partly-written set is a validation error on one backend and a sampled black texture on
another, and neither says which material forgot which texture — where an object that does not draw is
unmistakable and the material that owns it is the one being looked at.

Through the frame allocator, so a value that changes is safe: a set rewritten in place is one rewritten
while an unfinished frame may still be reading it. That costs one descriptor write per variant per
frame, which is what every compositor node costs too. The bytes are the part worth not repeating, and
`EffectConstants` compares the collection's version — a material nobody touched uploads once.

## Image-based lighting

The sky is a light, and `Lighting/` is what turns one into something a shader can evaluate. Karis's
split-sum, with both halves produced on the CPU because a bake is a per-environment cost and closed
forms can check it: `EnvironmentBaker` prefilters a cube per roughness by GGX importance sampling, and
`SphericalHarmonics` projects it into nine coefficients per channel. What reaches a frame is a mip
chain and twenty-seven floats.

**The cube convention is not invented here.** `CubeMapping.Direction` unprojects
`ShadowProjections.Cube` — the same matrix a point light renders its shadow cube with, already
asserted to tile the sphere — so a probe and a shadow cannot disagree about which way `+Y` is. Its
inverse is the major-axis rule, because a prefilter takes millions of samples and cannot afford six
matrix multiplies each, and a test holds the two against each other over thousands of directions. It
earned its place immediately: every face's horizontal axis was mirrored relative to the published D3D
table, which the engine's look-at convention flips, and nothing else would have noticed — a mirrored
environment is still an environment.

Two defects came out of wiring it up, both of the kind that survive because they look like something
else. **The pass sampled the reflection at mip zero whatever the roughness said**, so every surface
mirrored the environment and `Ibl.SpecularLod` and `environmentMipCount` were dead code — a rough
metal reflecting a sharp world reads as a material problem. And the diffuse term was fed a *radiance*
sample where irradiance belongs, which is where a missing `1/π` was hiding: `Ibl.Diffuse` now applies
the Lambert BRDF's own factor, so a white surface under a uniform white environment comes back exactly
as bright as the environment. That one is stated in three places and tested in two, because the way it
fails is a frame that is uniformly too bright, and the fix usually lands on the exposure.

**Reflection probes** are the local version: a cube captured in a room, parallax-corrected against a
box or a sphere so a floor reflects what is in front of it rather than what was above the probe, and
faded against the sky over the probe's own blend distance. `ReflectionProbeSelector` decides which one
applies — priority, then weight, then volume, so a cupboard inside a room wins inside the cupboard —
and it decides it from positions alone, which is why it needs no device to test.

**A probe is chosen per object, and it costs an `int`.** The cubes are one binding with a count, bound
for the frame; the volumes are an array beside them in the per-frame block; and
`ForwardLightingRenderFeature` writes the index and the blend weight into the per-object block it
already fills — in the twelve bytes of padding std140 leaves after the light count, so the block is the
size it always was. Nothing extra is bound per draw.

**`SceneLighting` is what puts the probes in the array that index reaches.** For a while the index was
written and the array was empty: the shader declared four cubes, the feature named one of them per
object, and no code anywhere bound a probe — so every object pointed into descriptors nobody had
written. It walks the scene's `EnvironmentLight`, its `ReflectionProbeSelector` and its sun into
`SceneConstants.Parameters`, and two properties of it are load-bearing:

- **The array's length is the shader's.** `ProbeCount` is a permutation that sizes a *binding*, so the
  count comes off `Effect.Bindings` at bind time rather than out of a host's configuration. A variant
  compiled with `UseReflectionProbe` off has no probe binding at all and gets nothing written for one.
- **The order is the selector's.** The index in the per-object block is a *position in that list*, so
  sorting the probes here — by weight, by priority, by anything — would leave both halves internally
  consistent and every object reflecting somebody else's room. `SceneLightingTests` asserts the two
  agree through the bytes one uploaded and the handle the other produced.

Every slot is filled, including the ones no probe occupies: the shader samples
`probes[clamp(probeIndex, 0, ProbeCount - 1)]` and only *then* weighs the result against zero, so a
slot with no descriptor is read rather than skipped. The spare slots take the environment's own cube,
which is the right answer as well as a valid one. `EffectBinding.Count` is what carries the array
length to the runtime, and `EffectSetWriter` fills an array element by element under `probes[2]` —
falling back to the bare `probes` for any element that names nothing, which is exactly the case a
frame with two probes and four slots is.

Probes the array cannot hold are dropped and **counted** (`SceneLighting.Dropped`), because the failure
is invisible from the frame: an object that selected the fifth of four carries an index the shader
clamps, so it reflects the wrong room rather than nothing.

This section used to say per-object selection needed a descriptor set per probe bound per draw, and
that `ForwardLightingRenderFeature` owning the per-draw set was the obstacle. Both halves were wrong.
A set per probe bound per draw is a set per object in all but name — the cost the four-set convention
exists to refuse — and the real obstacle was the compiler: Raven folded an array of textures *into the
uniform block*, an opaque type in a `Block`-decorated struct, which `glslc` rejects outright and which
`spirv-val` accepts and no driver would.

## Light probes, and the predicates they turned out to need

An environment map says what is around the *scene*; a reflection probe says what is around a *room*.
A light probe says what is around **here**, for a thing that moves — and the answer for a position
between probes is a blend of the four that surround it. `LightProbeVolume` is that, and it is short,
because spherical harmonics make the blend free: a weighted sum of projections is the projection of
the weighted sum, so four probes cost nine multiply-adds rather than four evaluations. Everything
difficult is in *which four*.

Which four is a **Delaunay tetrahedralisation** of the probe positions, and Delaunay specifically
rather than any tetrahedral mesh over the same points: the empty-circumsphere property is what makes
the four probes a position blends between its natural neighbours. Group them any other way and an
object lights differently depending on which arbitrary seam it is standing on.

**This section used to say the tetrahedralisation was written, found wrong by its own tests, and
withdrawn.** It was, and the reason was always the same reason under three disguises. Bowyer–Watson
is fifteen lines — delete every cell whose circumsphere contains the new point, then join the point
to the cavity's boundary — and every one of those lines rests on a question whose answer is one of
three values. Floating point cannot answer that question. It returns a number that is nearly right,
and *nearly right* is a category error for a sign: an in-sphere test that says `-1e-19` where the
truth is `0` has not made a small error, it has given the wrong answer, and the cavity built on it is
not star-shaped and the mesh that comes out is not a mesh. The three disguises were an enclosing
tetrahedron whose circumspheres swallowed the domain, a grid of probes that is *cospherical* so a
strict test found no cavity, and a near-degenerate cell whose circumsphere ate the mesh on the next
insertion. All three are the same defect.

So the fix is not in the mesh builder. It is `Vixen.Core.Mathematics.ExactPredicates`, and it has
three parts:

- **Filtered evaluation.** Each predicate runs in `double` alongside a bound on its own rounding
  error — the permanent of the same expression, which is what you get by replacing every term with
  its absolute value. Further from zero than the bound and the sign is certain, which is the case for
  essentially every call. That is the fast path and it is the only path most inputs ever take.
- **An exact fallback.** When the value and the bound overlap, the same determinant is evaluated in
  `BigInteger` over the inputs rescaled to integers — every binary float already *is* an integer
  times a power of two, so factoring out the smallest exponent loses nothing and the determinants are
  homogeneous, so the common factor cannot change a sign.
- **Simulation of simplicity.** Zero is a real answer and a common one — eight probes on the corners
  of a cube are cospherical, and so is every grid anybody authors — so the tie is broken by
  *symbolically* raising each point off the paraboloid by an infinitesimal ordered by index. The
  in-sphere test is a determinant and a determinant is linear in each row, so the perturbed value is
  exactly `S + Σ δᵢ·Cᵢ` with **no cross terms at all**: two rows perturbed in the same column are two
  identical rows and contribute nothing. The answer is therefore the sign of the first non-zero `Cᵢ`,
  each of which is an orientation of the other four points, and the one belonging to the point being
  tested is the cell's own orientation — non-zero by construction, so the scan always terminates with
  a decision.

**And the mesh checks itself.** The points are inserted into an enclosing tetrahedron and the cells
touching its corners are dropped at the end, which is the textbook arrangement and has the textbook
hazard: an enclosure that is not large enough silently loses cells near the hull, and everything that
remains still looks Delaunay because it *is* Delaunay. What it leaves is a dent. So
`FillsConvexHull` is asserted rather than assumed — a complex of empty-sphere cells that uses every
point and whose boundary is closed and convex is the Delaunay tetrahedralisation, and a failed check
grows the enclosure and rebuilds. The convexity is checked edge by edge rather than point by point,
because on a grid both counts are in the thousands and every one of those tests lands on the exact
path.

Outside the hull, `Sample` returns the **nearest** probe rather than extrapolating. An order-2 fit
pushed past the data it was fitted to produces negative irradiance, which is a surface that removes
light from the frame; a flat seam at the edge of the bake is visible, and a black patch on a
character is a bug report. Probes all on one plane — a single-height grid over a floor, which is a
reasonable thing to author — have no tetrahedralisation at all, and `IsTetrahedral` says so rather
than an exception saying it.

What is **not** here is the GPU half: nothing yet uploads a probe volume or samples one in a shader,
and `ForwardLightingRenderFeature` still takes its ambient term from the environment. The CPU side is
complete and tested, which is the part that needed the predicates.

## Area lights

Five light kinds now share one eighty-byte record and one loop: directional, point, spot, tube and
rectangle. Sharing matters more than it sounds — clustering, the per-object light list and the
per-draw block all work on "a light", so two of the five being shapes cost no second path anywhere.
The record grew by sixteen bytes rather than gaining a list of its own, and every area shape needs
exactly two extents, so a rectangle's half-height is the field a sphere and a tube use for their
radius.

The shading is Karis's **representative point**: shade the point on the shape nearest the reflection
ray, and widen the lobe by the angle the shape subtends. What that buys is a highlight in the right
place and roughly the right size; what it does not buy is its shape — a tube seen edge-on should
streak and gives a widened blob — and a large near light lights a surface as though all its energy
came from one spot on it. Linearly transformed cosines are the upgrade doc 06 asks for, and they need
a fitted table that comes from an offline optimisation this repository cannot run; nothing here is in
their way.

The cluster culler's reach now includes half a tube's length, for the reason its radius term already
existed: a shaped light whose centre is out of range still reaches in from its end.

## Skinning and instancing, and three ways to reach per-draw data

Both want the same thing — a variable-length run of matrices per object, in one buffer written once a
frame (`MatrixBuffer`). They get it to the shader by deliberately different routes, because the
cheapest mechanism differs:

| | Per-draw data | Reached by |
|---|---|---|
| lighting | a light block | a **dynamic descriptor offset** |
| skinning | a bone palette | a **push constant** holding the base index |
| instancing | a run of transforms | the draw call's own **`firstInstance`** |

Instancing needs no binding at all: Vulkan adds `firstInstance` into `gl_InstanceIndex` before the
shader runs. Skinning needs four bytes of the push block that already exists for the transform — 68
of Vulkan's guaranteed 128 between them. Only lighting has a fixed-size block that every draw reads
the same way, which is the one case a dynamic offset actually fits.

Skinning uploads **skinning matrices**, `inverseBindPose * boneWorld` already multiplied: one
multiply per bone per frame instead of one per vertex, which for a hundred bones and fifty thousand
vertices is four orders of magnitude.

**Both contribute a permutation** through `IPermutationSubFeature`, because an object is skinned
when it has a skeleton and not when a material says so. `MaterialRenderFeature` applies the
contributions without knowing either feature exists, and resolves **per distinct (material, flags)
pair** — ten thousand objects over twenty materials, half of them skinned, is forty resolutions and
ten thousand dictionary lookups.

A batch of one is *not* instanced. It would draw identically and would compile a second pipeline to
do it.

## The compositor

`Compositor/` is docs/plan/06's third idea from Stride: **the frame's structure is data the user
edits, not code.** A `GraphicsCompositor` holds a tree of `SceneRenderer`s — a sequence, a render
pass, a single stage from a single view, or a delegate — and "swap forward for deferred" is a
different tree rather than a different build.

Three phases, and each can only do its own job:

```
Collect   nodes declare which views they need and which stages those views draw
          → then extract, cull, prepare, sort
Build     nodes declare render-graph passes: what each reads, writes and does
          → then the graph places barriers, sizes transients and culls
Record    runs inside a pass the graph opened; only drawing is left
```

The last split is not bureaucracy, it is the RHI's own: a draw has to be inside a render pass and a
pass has to be declared before the graph can order it, so a node either owns a pass or draws into
someone else's. A node that did both would be declaring a pass from inside one.

**The view list is derived from the tree, not handed to it.** A stage is in a view's mask because a
node draws it, so a stage nothing draws costs no culling and a stage that is drawn cannot have been
left out of the mask. Masks are rebuilt each frame rather than accumulated: a stage removed from the
tree stops being sorted for, instead of quietly producing a list nobody reads.

There is no `ClearRenderer`, and that is not an omission — **clearing is a load action on an
attachment.** Issuing it as its own operation is a D3D11-ism that costs a tile-based GPU a full extra
pass writing a colour the next pass overwrites.

### It declares passes rather than opening them

A compositor node names its targets and hands the naming to
[`Vixen.Graphics.RenderGraph`](../Vixen.Graphics.RenderGraph/README.md), which then decides how big
each one is, whether two of them can share memory, whether their contents ever have to reach memory,
what barriers precede a pass, and whether the pass is worth running. A node that called
`BeginRenderPass` itself would be answering all of those with "I do not know" — which is what it was
doing before.

**`Reads` is load-bearing, not bookkeeping.** A pass that samples the shadow atlas says so, and that
one line is the edge that orders the shadow pass before it, puts the barrier between them, and keeps
the shadow pass from being culled for producing something nobody wanted. Both directions are
asserted: a pass that reads another runs after it with a barrier, and a pass whose target nothing
reads is dropped along with its draws.

That last rule is why the frame's final target is **imported**. A pass writing an import always
survives — the swapchain image belongs to the presentation engine and has to be handed back in
`Present` — so "the last pass" cannot disappear, while an over-specified preset's unused passes cost
nothing.

### The document owns its targets

`resources:` is the half of "the frame is data" that naming host textures could not express. A
document that can say *a half-resolution R11G11B10 chain* can describe a post-processing pipeline;
one that could only refer to textures somebody else made could describe the order of passes and
nothing about what flows between them. Sizes are a `scale` of the frame rather than pixels, so a
bloom chain authored at half resolution stays half resolution on a window nobody anticipated.

Two things stay imports, for opposite reasons: the swapchain image, because it belongs to the
presentation engine; and a **cached** shadow atlas, because a cache outlives its frame by definition
and the graph's pool exists precisely to recycle memory whose lifetime ends inside one. An import
wins over a declaration of the same name, so one document runs against a swapchain in one preset and
an offscreen buffer in another without being edited.

### Shadows

`ShadowMapRenderer` is the clearest thing the compositor buys, because **a cascade is a view**. Four
cascades are four `RenderView`s over one stage, culled and sorted by machinery that knows nothing
about shadows, drawn into four tiles of one atlas in **one pass with a viewport per tile** — four
passes would be four loads and stores of a depth buffer nothing reads outside the frame. The pass has
no colour attachment at all.

`ShadowCascades` is pure arithmetic, and it is where cascaded shadow maps go wrong in their two
famous ways:

- **Crawl when the camera turns** — fixed by fitting a **sphere**, not the eight frustum corners. A
  corner fit's extent depends on where the camera points, so turning on the spot resizes the cascade,
  which resizes its texels. The sphere's radius is a function of the split distances and the field of
  view alone. Twelve directions, one radius, asserted.
- **Crawl when it moves** — fixed by snapping the fitted centre to the light's texel grid, on all
  three axes. Sub-texel movement then produces a *bit-identical* matrix; movement past a texel does
  move it, which is the other half of the test and the reason it means something.

The fit is checked against the eight corners it was deliberately not computed from, so the test and
the implementation do not share their arithmetic. Splits use the practical scheme — logarithmic
blended toward uniform at λ = 0.75, because pure logarithmic puts the first boundary absurdly close
to the eye. Shadow distance is its own setting rather than the camera's far plane; cascades sized to
a two-kilometre view distance spend their whole budget on terrain nobody can see a shadow on.

`PunctualShadowRenderer` does spot and point lights, and it is short for a reason worth naming: a
directional light has no position, so a cascade has to be **invented** from the camera and everything
hard about it follows from that. A punctual light already is a volume — a spot's shadow frustum *is*
its cone, a point's is six of them — so there is nothing to stabilise, because nothing moves when the
camera does. `ShadowProjections` is forty lines against `ShadowCascades`'s two hundred.

Six 90° frusta tile the sphere of directions exactly, which is what makes a cube map a cube map; ten
thousand random directions each land in at least one face, because a wrong up vector or a field of
view that is not exactly 90° leaves a seam, and a seam in a shadow cube is light through a wall along
one line.

**A point light is six tiles and a spot light is one** — six times the culling, six times the draws,
six times the atlas. That is why the atlas is allocated in tile units: a spot light still fits behind
a point light that did not. When it runs out, lights are dropped **whole and counted**; a point light
with four of six faces rendered is worse than one with none, because the two missing directions are
lit as though nothing occludes them.

### The camera, once

A cascade fit needs a camera, and for a long time it held seven scalars describing one — a copy of
something the frame already knew, which a host had to keep in step with the view it also set. Nothing
checked that they agreed, and a cascade fitted to a field of view the camera no longer has puts the
shadow distance somewhere the setting does not say. That shows up as shadows fading in at the wrong
distance and gets attributed to the shadow distance.

`RenderCamera` is that description once. A `RenderView` carrying one has its position, matrix and
frustum derived from it, and `ShadowMapRenderer.Camera` points at the same view — so the thing the
frame is drawn from and the thing the cascades are fitted to are the same object. The scalars remain
for a test or a tool fitting cascades to a hypothetical camera, which has no view to point at.

A view is still not a camera. Most views are not — a cascade, a probe face — and `Camera` is null on
every one of them. What changed is that the one view that *is* a camera can say so.

The sun is the same argument: `ISunSource` gives the shadow renderer the scene's brightest
directional light, rather than a host copying its direction across every frame and one day
forgetting, leaving a level lit from one direction and shadowed from another. An interface rather than
a reference to the lighting feature, so a scripted or cinematic sun supplies it and nothing else
changes.

The golden fixture fits its cascades from a scene camera now, and produces the same reference image
it did from the scalars.

### Caching a cascade

Two things have to be true together, and neither is worth anything alone.

**The projection has to stop moving.** A tight fit re-fits the moment the camera moves a texel, so
there is never anything to keep. `Slack` cuts the cascade wider than its slice, and it is kept while
it still covers one — buying stability with resolution, since the same texels then cover 1.5625 times
the area at 25%. That trade is the whole feature, and it is asserted as a number.

**The static casters have to be separable.** They already are: "which objects, in what order" is what
a `RenderStage` *means*, so a host puts level geometry in one stage and everything that moves in
another, and no filtering machinery is needed. The static stage is drawn into a cache atlas only when
something invalidates it — a cascade re-fitted, or the host bumped `StaticVersion` — and every frame
copies that into the working atlas and draws the movers on top with a `Load`.

The copy is a full depth atlas per frame, so this is a trade rather than a win: it pays when a
level's worth of geometry would otherwise be rasterised into four cascades every frame, and not when
the scene is small. Leaving `StaticCasterStage` null keeps the uncached path exactly as it was.
`StaticRebuilds` is the number the whole thing is judged by — "it caches" is otherwise a claim
nothing can check.

"Static" is a claim the scene makes and the renderer cannot verify: a host that moves a static caster
without saying so gets a shadow where the object used to be, which is the bargain the word already
implied.

## Level of detail

A LOD group is **several render objects**, not one object that swaps its mesh — the same argument that
makes a three-material mesh three render objects: one object would have to pick one sort key for
meshes that resolve to different pipelines. `LodRenderFeature` decides which level a view sees and
clears the others' visibility bits.

Selection sits **after culling and before sorting**, which is the only gap it fits in. Earlier and an
object outside the frustum has no screen size to measure — and asking would mean measuring every
object rather than every visible one. Later and sorting has already built the list a level would have
to be absent from. `VisibilityGroup.Hide` is the seam, and it only ever clears a bit: a pass that
could *add* visibility would be one that could draw what the frustum rejected.

**Per view, because screen size is.** The same tree is level 0 to the camera and level 3 to a distant
probe. A view with `ScreenHeightScale` at zero — a shadow cascade, by default — sees every level,
because a shadow drawn from a different mesh than its caster stops matching it.

**Hysteresis is the difference between LOD that works and LOD that flickers.** An object drifting at
exactly a threshold would otherwise change mesh every frame, and a level change is a different
silhouette — far more visible than the detail the switch was protecting. Ten frames across a boundary
decide once; a clear move past it still changes level, which is the paired test that keeps the first
one honest.

**Cross-fade** (`CrossFadeDuration`, off by default) keeps both levels visible for the transition and
gives each a weight, pushed as a constant. Dither, not blend: two translucent copies of one object
write depth twice and sort against each other, where a dithered discard by weight makes the two
levels' surviving pixels tile the silhouette exactly once — which is why the weights summing to one
is asserted. It is off by default because a fade doubles the draws for every object crossing a
threshold, and only a fading object pushes anything. A fade interrupted mid-way turns round rather
than finishing, so a camera swinging past a boundary and back does not pay the duration twice showing
a level it is no longer going to.

`DeltaTime` is supplied rather than measured — a renderer that reads a clock is one whose frames
cannot be reproduced, and a fade is exactly what a golden-image test wants to step through.

### What decides a pipeline

`DescribePipeline` is gone. Four things decide what a driver compiles, and each contributes only what
it knows:

| | Contributes |
|---|---|
| `Effect` | the shader modules and the pipeline layout |
| `RenderStage` | how its objects are drawn — blend, depth, raster |
| `RenderOutput` | what they are drawn into — formats and sample count |
| vertex layout | how a vertex is read |

Which is exactly `PipelineKey`, and `EffectPipelineDescriber` takes exactly those four. State belongs
to the stage and formats to the pass because a stage is drawn into many passes: "Opaque" means
depth-written *wherever* it is drawn, while what it draws into changes every time.

`RenderOutput` holds **formats, not textures**, and that is what makes the whole thing work: the
swapchain hands out a different image every frame and the render graph aliases transient targets
freely, yet neither invalidates a single pipeline. Two passes of the same format share every
pipeline; two passes of different formats share none — both asserted, because the second is the one
that fails as a validation-layer complaint on one driver and a wrong image on another.

### The compositor as a file

`GraphicsCompositorAsset` is the same tree as a serialisable record graph, and `CompositorBuilder`
turns one into a running compositor. A `[DataContract]` name per node type is the YAML tag, so
`!ShadowMap` selects the type — the same polymorphism the `.meta` model uses, with no registration
table to keep in sync.

**The asset names resources; the host binds the names.** A texture handle belongs to a device that
did not exist when the file was written, and a `RenderView` is built from a camera that moves —
neither can be in a document. So one authored compositor runs against a swapchain, an offscreen
buffer or a test's scratch texture without changing a line. An unbound name throws naming the node,
the kind and the name, because binding what it can and skipping the rest produces a frame missing a
pass that reports nothing.

Stages are created rather than bound, because a stage *is* its authored settings; blend and depth are
named presets rather than spelled-out states, since those four and those three are what a stage has
ever wanted and an author writing seven blend factors is an author about to get one wrong. The
version is checked rather than ignored — a file from a later editor is refused by number.

`Vixen.Rendering` does **not** reference `Vixen.Core.Yaml`. The model is a plain record graph carrying
`[DataContract]`, so both generators run over it: the reflection one for the YAML binder an editor
uses, and the **binary one** for the chunk a content build bakes. The runtime reads the chunk and
never links a parser — asserted by round-tripping a document through `Serializer` and drawing the
same frame out the far side.

## Forward+

### The pass says which set each binding is in

It did not, and everything it declared therefore landed in set 2 — the material's — including the
sixteen-entry light list, the camera, the shadow atlas and the scene's environment. That is not a
tidiness complaint. `ForwardLightingRenderFeature` writes the per-object block and binds it at **set
3**, so the shader and the feature that fills it disagreed about which set it was in, and nothing
anywhere said so: a marker nobody wrote is a default nobody chose.

| Set | Holds | Because |
|---|---|---|
| 0 per-frame | environment, probes, shadow atlas, the sun, the light and cluster buffers | one scene, bound once |
| 1 per-view | `viewProjection`, `viewPosition`, `view` | the block every shader shares — a texture or a buffer here would make two shaders' set 1 incompatible and the shared set unbindable |
| 2 per-material | whatever the composed surface declares | 1888 bytes to **32**, which is the measure of what was wrong |
| 3 per-draw | the light list, its count, the probe index and weight | what `ForwardLightingRenderFeature` was writing all along |

`world` became a **push constant**, because that is what `TransformRenderFeature` already does with
it — and it leaves the per-draw block with exactly one owner, where a block holding both a transform
and a light list needs two features to agree on its layout. `worldViewProjection` went with it: it was
world × the view's matrix, computed per object on the CPU and uploaded per object, where the vertex
stage can multiply two matrices it already has.

Set 0 has since gained `transforms` and `transformBase` beside them, which is where `world` reads from
when `UseTransformRecords` is on — a push constant is per command, and that turned out to matter more
than what it costs. The push-constant range is still declared either way.

The per-draw block's declaration order is not a style choice either. std140 starts an array of
structures on a sixteen-byte boundary, so the count and the two probe fields fill exactly the header
`ForwardLightingRenderFeature.HeaderSize` was already writing — and `ForwardPlusLayoutTests` holds all
four offsets against the checked-in reflection, so the shader and the feature cannot drift apart again
without a test saying so.

### One block per set, and who fills each

Marking the sets meant `Effect.ConstantBufferSize` stopped being enough: it names *one* block, which is
all a shader that marks nothing has, and this pass now has four. `Effect.BlockOf(slot)` is the same
question asked per set — a caller handed the wrong pair writes the right values into the wrong buffer,
which is a frame lit by whatever those bytes meant.

| Set | Filled by |
|---|---|
| 0 | `SceneConstants` — the environment, the probes, the shadow atlas, the sun |
| 1 | `ViewConstants` — the block every shader shares |
| 2 | `MaterialRenderFeature` |
| 3 | `ForwardLightingRenderFeature` |

`SceneConstants` is set 0's counterpart to `ViewConstants`, and it differs in one way that follows from
what the two sets are. Set 1 is a **contract between shaders**, so its layout is configured once and
holds a block only — a texture there would make two shaders' set 1 incompatible and the shared set
unbindable. Set 0 belongs to whichever pass is drawing, so it takes its shape from that pass's own
binding plan and can hold resources: a host sets `ForwardPlusKeys.Environment` and `EffectSetWriter`
finds where `environment` goes.

`EffectSetWriter` is that lookup, shared by both fillers, because the rule is one rule: a caller names
a resource and `Effect.Bindings` says where it goes. Every binding or none, for the reason a material's
set is all-or-nothing — and every *element* of an array binding, which is the same rule one level down.

`SceneConstants.Lighting` is the hook that runs the extract, and it is here rather than in a host's
frame loop for the reason the whole section is about: the probe array's length is the shader's, and the
bind is where the shader is known. A host that had to size the array itself would be keeping the
`ProbeCount` permutation in two places. Set it and a frame binds its own set 0 with nothing named by
hand; leave it null and `SceneConstants` is what it was, a collection somebody fills.

The rest of set 0 is handled the same way and by different owners, because the split is real: the
environment and the probes are objects a *scene* holds, while an atlas and a cluster list are handles a
*frame* produced and are only valid after the pass that produced them ran.

| Binding | Published by | Why there |
|---|---|---|
| `environment`, `probes[i]`, `probeVolumes[i]`, the sun | `SceneLighting` | objects the scene holds |
| `lightBuffer` | `ForwardLightingRenderFeature` | its own buffer, recreated whenever the scene outgrows it |
| `shadowMap`, `clusters` | `RenderPassRenderer.SceneTextures` / `SceneBuffers` | frame resources — see below |
| `lightViewProjection`, `shadowTexelSize`, both biases, `shadowSampler` | `ShadowMapRenderer` | everything the atlas cannot say about itself |
| `tanHalfFov`, `nearPlane`, `farPlane` | `ClusterGrid.Apply`, through `SceneLighting.Camera` | the culler and the fragment must be given the same four |

**The consuming pass publishes the frame resources, not the producing one.** A graph resource has no
handle until the graph has placed it, so a producer that published its own output would be handing over
a handle whose read barrier nobody declared. Naming one in `SceneTextures` *is* declaring the read —
the two lists cannot disagree, because there is only one.

`ShadowMapRenderer` publishes **every cascade**, each with its own atlas tile folded into its own
matrix by `ShadowCascades.AtlasProjection`, and the shader picks between them **per fragment**. That
selection is what a cascade *is* — a fragment's own distance deciding the resolution it is shadowed
at — and it was missing for a while: the shader read one matrix and one distance, so everything past
the nearest slice projected outside its tile and came back unshadowed. The symptom is a shadow
distance far shorter than the setting, which reads as a settings problem.

Three things make it hold together:

- **The matrix and its distance are one record.** `ShadowCascade` in `Lighting.rvn` is a `mat4` and a
  `split`, so `cascades[i]` is self-describing. Two parallel arrays would be two things a host keeps in
  step, and the failure — a matrix used past the distance it was fitted for — is a shadow that looks
  like a shadow and is in the wrong place.
- **The tile is in the matrix.** `NdcToUv(cascade · p)` addresses the *whole* atlas, so with four tiles
  in it every lookup would land a quarter of the way into somebody else's and read a plausible depth.
- **The last cascade's end is a ramp, not a line.** `Lighting.CascadeFade` existed unused for exactly
  this; a shadow term that simply stops reads as a rendering error rather than as the shadow distance.

`CascadeCount` is a permutation, because it sizes an array *in the block* — the same argument as
`MaxLights`, one array along, and the same agreement required of the host. `ShadowCascades.CascadeOf`
mirrors the shader's search so a test can assert the round trip: **the cascade a fragment selects is one
whose projection contains that fragment.** Neither half can make that claim alone.

`ViewConstants` defaults to **144 bytes with `Vixen.View` at 80**, which is what `ForwardPlus.rvn`
declares for set 1. That is not a coincidence to be tidied away: set 1 is a contract between shaders, so
the engine's own pass defines it. It was 80 while the shader said 144 — a descriptor range shorter than
the block it points at.

`ForwardFrameTests` is the end-to-end assertion, and it builds its effect from
`ForwardPlus.reflect.json` through the real `EffectLoader`: one frame, four sets bound, one draw, and a
paired negative where a single hand-off is removed and set 0 goes unbound rather than half-written. A
hand-written fake would only assert that the renderer agrees with itself.

It also asserts that **no name the frame publishes is one the shader does not have**, which is the
general form of the failure this area keeps producing. Six types write into set 0 by string, and a typo
in any of them is silent: the value is written, no binding claims it, and the surface is lit by whatever
the shader declared as a default. The assertion is not that a particular name is right — it is that
nothing is orphaned.

`MeshRenderFeature` binds set 0 where it binds set 1 — after the first pipeline, once per run. After,
because `BindDescriptorSet` takes no pipeline layout and infers one from what is bound, so a set before
the first pipeline is undefined and the Vulkan backend refuses it. Once, because the four-set convention
makes every pipeline in a frame layout-compatible up to set 1, which covers set 0 with it.

The generator followed. `BindingsEmitter` used to pick the first uniform block a shader had, which was
every shader's only block until this pass had four — so `ForwardPlusKeys` would have covered set 0 and
named nothing in the other three. It now emits a key for every block, a `PerFrameBlockSize` /
`PerDrawBlockSize` pair per set, and a writer struct per block (`ForwardPlusPerDrawConstants`). A
shader with one block is untouched: `ConstantBufferSize` and `<Shader>Constants` are what every
post-process pass names, and there is a test that says so.

The shader half — `Library/Pipeline/ClusterCulling.rvn` binning lights into a froxel grid, and
`ForwardPlus.rvn`'s `UseClusteredLights` permutation swapping its uniform-array loop for the cluster
list — has existed for a while. What was missing was the CPU side, and what was *blocking* it was the
edge in the middle: compute writes the cluster buffer and the shading pass reads it, and until the
compositor declared its dependencies there was nowhere for that barrier to come from.

`ComputeRenderer` is a compute pass as a compositor node: what it reads, what it writes, and how many
groups. Its whole value over a hand-written dispatch is those two lists — a pass that says it writes
the cluster buffer, next to one that says it reads it, is a pass the graph orders first and puts a
barrier after. Its effect resolves through the ordinary `EffectSystem`, so a compute shader is
permuted, cached and baked like a graphics one, and a shipping build cannot compile one for the same
structural reason it cannot compile a vertex shader.

**A contributed flag and a shader permutation are different names for the same thing**, and
`PermutationSources` is what joins them. A sub-feature's key is the renderer's — `Vixen.Clustered`, so
that one feature drives the flag across every shader that has it — and the shader's is its own,
`ForwardPlus.UseClusteredLights`. The effect key is built from the keys registered for the shader, read
out of a collection the sub-features wrote under *their* names, so registering the shader's key found
nothing and took its default, and registering the renderer's key produced a define no compiler could
match. Neither showed, because a test provider that answers every key alike cannot tell them apart —
and what it meant in a shipping build is that **the clustered variant was never selected**: the culler
filled its buffer and the shading pass read the uniform-array loop beside it.

**A compute node fills its own uniform block**, through `ConstantBinding` and `Parameters`, at the
offsets the effect's plan gives. That was missing until the culler was actually run: a node could
declare the buffers and textures it read and wrote, and the *values* beside them — a camera, a count,
a threshold — had to go through `OnBind`, which means a host building a buffer, filling it and writing
a descriptor by hand. `ClusterCulling.rvn` takes four such values, so **the clustered path could not
run in a composed frame at all** while every test of it passed. The block rides in the set
`Descriptors` writes, which costs a compute pass nothing: one that binds no buffer and no storage
image has nowhere to put its result.

**The cluster buffer is declared, not imported**, and that is what makes the ordering test mean
something: a cull whose result nothing reads is dropped along with its dispatch. The scene's light
list is imported, because the host filled it before the frame began.

`ClusterGrid` mirrors the shader's constants — 16 × 9 × 24 froxels, 32 lights each, about 445 KB.
They are `const` there rather than permutations because they size an array *inside a struct*, and a
struct's shape cannot depend on a variant while the host binds one buffer; so both sides must agree
by construction, and a test is what notices when they stop. The exponential slicing is asserted to be
its own inverse, since that is the property it was chosen for — a fragment finds its slice with a
logarithm rather than a search, and if the two derivations disagree a fragment reads a cluster the
culler filled for somewhere else.

**View space is right-handed, and the grid did not know it.** `Matrix4x4.LookAt` says it outright —
the camera looks down −Z — and `PerspectiveFieldOfView` agrees, taking `w` from `-z`. But
`Transform.ViewRay` returned `+1` for its z, so a cluster's box came out mirrored in z from the light
positions `Touches` transformed into the same space. Nearly nothing intersected: **every cluster list
came back empty and the clustered path lit a scene by the sun alone.** A handedness mistake produces
an empty result rather than a wrong-looking one, which is how it survived being written down twice.

`ClusterGrid.DepthOf` is now the one place the two conventions meet, on both sides, and
`ClusterGrid.UvOf` is exactly the rasteriser's own NDC → UV — asserted against the projection matrix
rather than against trigonometry repeated in the test, because two derivations of one quantity is the
failure itself. The round trip is asserted too: the cluster a fragment computes for itself has to be
the box the culler tested lights against. It was found by cross-checking the published half-tangents
against `RenderCamera.Projection`, which is the sort of thing only a differential oracle finds.

One of the tests reads shader *source* — the two lines where `ClusterCulling.rvn` and `Transform.rvn`
state the convention. That is deliberate and narrow: the host's mirror is not what runs, and the bug
was two sides disagreeing while each stayed internally consistent, so a test of either alone would
have passed throughout.

And one asks the GPU. `ClusterCullingDeviceTests` in `Platform/Vixen.Graphics.Golden.Tests` compiles
`ClusterCulling.rvn` through the content build's own compiler, dispatches it, and reads every cluster
list back to compare against `ClusterGrid.Bounds`. Reverting the handedness fix fails it with
`expected [0], got []` — the original bug, verbatim, from hardware.

**Clustered lighting does no per-object work at all.** No selection, no block per object, no
descriptor bound per draw — `ForwardLightingRenderFeature.Clustered` turns the whole per-object path
off, and eight objects produce eight draws and nothing else. That is the point of the pipeline, and
it is easy to claim and easy to get wrong, so it is asserted.

## Bytes into a frame, answers out of it

A compute node could say what it read. What nothing could say is where the values it read came from,
or where its answer went. Both ends are shut for the same two reasons: a graph buffer has no handle
until the graph compiles, so nothing outside a pass can name it; and a device-local buffer is not
addressable by the host at all, so even a handle would not help. So a histogram had no way to start
cleared, a table of coefficients had no way in, and the number a dispatch produced had no way home.

`Compositor/BufferUploadRenderer` and `Compositor/BufferReadbackRenderer` are the two copies, as
nodes. The upload stages its bytes through a host-upload ring — `UploadBuffer<byte>`, the same ring
and the same argument as skinning's — and declares `Writes(target, CopyDestination)`; the readback
declares `Reads(source, CopySource)` and copies into a host-readback ring of its own.

**The declaration is the entire point, at both ends.** A copy recorded by hand runs where the host
wrote it and is not ordered against anything: a dispatch may read the buffer before the upload has
landed, and a readback may copy the buffer as it was before the dispatch — which is zeroes on a fresh
allocation and last frame's contents on a recycled one. **Both of those are plausible answers**,
which is why this was the shape of the last clustered-lighting bug and why neither could be a
function the host calls around `Graph.Execute`.

**A readback's pass has a side effect** and says so, because nothing in the frame reads what it
writes; culling is otherwise right to remove it.

**When the bytes are valid is `Latency`'s question, and there is no implicit wait anywhere.** Zero is
the stall: the region holds the frame just submitted, so the caller submits, waits and calls `Fetch`
— what a test wants and what a one-off query wants. At or above `FramesInFlight` the region belongs
to a frame the host's loop has already waited on, so the build fetches it itself and nothing in the
frame loop has to know a readback exists; the cost is an answer that many frames old, which for an
exposure or a survivor count is invisible. In between is neither, and buys nothing the full depth
does not — see `IGraphicsDevice.Read` for why an RHI that inserted a wait would be hiding a stall the
caller cannot see.

**A buffer that was not declared as a copy source or destination is refused, naming the node.** The
mistake is a usage flag missing from a document and what it otherwise produces is a validation error
on a debug driver and silence on a release one — the frame renders, the value never arrives, and
nothing says why.

Both are node kinds in a document — `!Upload` and `!Readback` — and `CompositorBuilder` hands the
built ones back through `Uploads` and `Readbacks`, because what a file cannot say about an upload is
the one thing that matters: this frame's bytes.

`BufferTransferDeviceTests` in `Platform/Vixen.Graphics.Golden.Tests` is what says a byte moved. The
recording backend has no memory behind a buffer — it validates a write and drops it, and answers a
read with zeroes — so **a readback that always returned zeroes would agree with every structural test
there is.** One fixture round-trips known bytes through a graph-owned transient; the other uploads a
light list, dispatches `ClusterCulling.rvn` over it and reads the cluster lists back, with nothing
hand-recorded between the three.

## Binding what a node declared

Declaring a read was always only half of it. The declaration orders the producing pass first and puts
the barrier in; it does not put anything in front of a shader. The other half had nowhere to live,
because a graph resource has no handle until the graph compiles — so a compute node bound through a
callback, and a pass that declared it read the shadow atlas bound nothing at all.

`Vixen.Graphics.DescriptorAllocator` is the missing lifetime, and `DescriptorBindings` is what a node
says with it: a list of `ResourceBinding`s naming a graph resource, a binding index and a kind.
`ComputeRenderer` binds its set between the pipeline and the dispatch; `RenderPassRenderer` binds its
own once, before anything under it draws, which is why its default slot is `PerView` — the materials
drawing into it rebind sets 2 and 3 without disturbing it.

**A binding may only name a resource the node itself declared.** Resolving against the frame at large
would compile, and would silently drop the edge that orders the producer first and places the
barrier: a pass would sample a texture nothing had transitioned, which is corruption on a tiler and
nothing at all on a desktop driver until it is somebody else's machine. So resolution goes through
the node's own read lists and anything else throws while the frame is being built.

**The layout comes from the effect**, through `Effect.SetLayouts`, which had been carried unused since
the effect system was written. A set is only bindable to a pipeline whose layout it was allocated
from, so a node taking one from anywhere else is how a frame ends up with a set the validation layers
reject and a release driver mis-binds in silence. A host may still supply its own — a
`RenderPassRenderer` has no effect of its own and must.

**The binding index is never written down twice.** Raven assigns it from declaration order within a
set, so adding a texture above another in a `.rvn` renumbers everything below it — and a host holding
the old number gets a validation error at best and the wrong texture at worst, with nothing to tell
it. `BloomRenderer`'s four were all wrong when they were guessed, which is the argument in one line.

Two ways to avoid guessing, for two kinds of caller. Code that can reference generated code names
`BloomKeys.SourceBinding`. Everything else — a compositor document, a shader loaded from a bundle at
run time — sets `ResourceBinding.Name` to the shader's own name for the resource and the index comes
off `Effect.Bindings`, which is the binding plan the reflection always had and the runtime never
carried. An explicit index remains as the fallback, for a provider that reports no plan — a test
fake, a host supplying effects of its own. The shipped ones do report it: a baked `EffectData` carries
the plan and `EffectLoader` puts it on the effect.

Samplers are describable too: `SamplerDescription` is twelve fields and no device, so it survives
being written in a document where a handle cannot, and it resolves through the shared `SamplerCache`.

The reflection is checked in beside the shaders rather than compiled during the build, because the
alternative is `Vixen.Rendering` depending on the compiler being built first, in a repository where
the compiler is the larger of the two. `Vixen.Raven.Tests` regenerates and compares them, so they
cannot drift from the shaders without a test saying so — the same arrangement as the checked-in
generated bindings in `Vixen.Shaders.Tests`, for the same reason.

## Post-processing

Everything else in the compositor draws *objects*. A post effect has none, which is why a node that
draws three vertices was the last thing between the compositor and doc 06's fifteen post-process
entries.

`FullScreenRenderer` is that node. The triangle comes out of `SV_VertexID` in
`Library/PostFx/Fullscreen.rvn`, so there is nothing to allocate and nothing to bind — and a triangle
rather than a quad, because two triangles meeting across the screen have a diagonal seam where the
interpolators are least accurate. It declares its own pass, like every other node that needs graph
resources, and its pipeline cache is its own rather than `PipelineCache`: three of that key's four
parts are degenerate here, since there is no vertex layout, no stage list, and the "stage" is the
node.

Two caches sit behind it, and both are shared rather than per-node.

**`SamplerCache`** is the smallest cache in the renderer and the one with the widest reach. A sampler
is pure state, so two that describe the same filtering *are* the same sampler — and Vulkan caps how
many a device will create, which turns "make one where you need one" from wasteful into a device-lost.

**`EffectConstants`** fills an effect's uniform block from a `ParameterCollection`, using the offset
table `Effect.Parameters` has carried since the effect system was written. **Every parameter is
written, not only the ones somebody set**: `var exposure: float = 1f` arriving as zero is a black
frame that nothing anywhere reports, so the default comes off the key — which meant carrying the
author's initialiser the whole way from Raven's lowering, through the reflection, into the generated
literal. It re-uploads only when the values change, which needed a fix one level down:
`ParameterCollection.Set` used to move the version even when the value was identical, and a post chain
reconfigures itself every frame.

### The depth prepass, and what actually made it one

A prepass drawn with each object's own material is not a prepass: it runs every fragment shader twice
and costs more than the overdraw it removes. So the load-bearing piece is not the second pass, which
the compositor could always express — it is **`RenderStage.ShaderName`**, which lets one stage draw
the objects with `DepthOnly.rvn` while another draws them with their materials, in the same frame and
off one extraction and one cull.

The override resolves in *preparation*, per distinct `(material, flags, shader)`, for the same reason
the base variant does: resolving can compile, and compiling inside a command list is a stall a frame
budget cannot absorb. The draw-time cost is one array index into a variant × stage table.

Two consequences fall out. The per-material set is bound **only where the resolved effect declares
one**, because a depth-only pipeline's layout has no per-material set and binding one is a validation
error rather than a wasted call — and the shader is what knows, not the stage. And every object in a
prepass resolves to the same variant, so they share a sort group and the stage's sort collapses to
pure front-to-back, which is exactly what makes early-Z reject the most.

It is the same fix a shadow-caster stage wants, for the same reason.

### Authored

`!FullScreen` and `!Bloom` are nodes a document declares, so a post chain is twenty lines of YAML
that mention no binding index, no sampler handle and no pass count:

```yaml
- !Bloom
  name: Bloom
  source: SceneColour
  output: BloomResult
  levels: 3
- !FullScreen
  name: Tonemap
  shader: Tonemap
  colourTargets: [Display]
  reads: [BloomResult]
  bindings:
    - name: source
      resource: BloomResult
    - kind: Sampler
      binding: 1
      sampler: LinearClamp
```

Two things had made that impossible and both are gone: `name: source` resolves against the shader's
own binding plan, and `sampler: LinearClamp` is a preset the frame's `SamplerCache` turns into a
description. What a file still cannot carry is a device, a module cache, a descriptor allocator or a
sampler cache — so `CompositorBuilder` takes those four and hands them to every node it builds. The
document says what; a running renderer supplies what only it has.

Bloom is a node rather than a list of passes because its shape follows from its depth and the frame's
size: nine passes and nine textures out of one line, where a document that spelled them out would need
rewriting to change the resolution.

### Bloom

The first effect that is more than one pass, and worth building early for that reason: a pyramid is
nine textures whose lifetimes overlap in a strict pattern, each written by one pass and read by
exactly one other, which is precisely the shape transient aliasing exists for.

`BloomRenderer` — now in [`Vixen.Rendering.PostFx`](../Vixen.Rendering.PostFx/README.md) with the rest
of the effect set — builds the chain out of real `FullScreenRenderer`s and **keeps them between frames** —
each owns a pipeline cache and a uniform buffer, and rebuilding them every frame would recompile the
same pipelines and reallocate the same buffers. What is rebuilt is only what depends on the frame's
size.

**The pyramid is declared, not imported**, so a bloom nothing reads costs no passes and no memory at
all. **Each pass steps in its source's texel grid, not its target's** — both filters offset their taps
by a texel of what they are reading, and taking it from the target makes the downsample's taps land
half a texel apart and the upsample's tent twice as wide. That is a bloom that is subtly too soft: it
throws nothing, and no screenshot answers it, so it is asserted.

The up-chain is one shorter than the down-chain, which is not an off-by-one — the smallest level is
already its own upsample source, so there is nothing to add into it.

**The first downsample is a mode of its own**, and that is what the Karis average is for. Weighting
each tap by `1 / (1 + luma)` before summing makes the 13-tap kernel an average biased towards its
darker taps, so a specular highlight sitting in one texel is pulled towards its neighbours instead of
dragging the whole kernel up — which is what stops it flickering as it moves between texels, the most
visible temporal artefact a bloom chain has. It belongs to that pass and no other: the prefilter takes
a single tap, where the weight is a darkening rather than an average, and every level below the first
has already been averaged, so applying it again would cost brightness and buy nothing. Nothing else in
the shader distinguishes the first downsample from the rest of the chain, hence a fourth `Mode` value
rather than a `FirstDownsample` flag — the chain asks for four variants either way, and the flag would
be a key every pass carries in order to say nothing.

## Set 1, and where a set can actually be bound

`ViewConstants` is the per-view uniform block: one per `RenderView`, filled from the view's own
matrix and position, bound before that view's work. Its absence was the largest hole in the renderer —
`TransformRenderFeature` pushed a world matrix and nothing carried a view-projection, so a shadow
caster could not be told which cascade it was drawing for.

**The layout is shared across every shader in the frame, and that is what makes set 1 work at all.**
A descriptor set survives a pipeline change only if the two layouts agree up to that set, so the
members are configured once rather than taken from an effect: the block belongs to the frame, not to
any shader in it. Which is also why a *document* declares it — sets 2 and 3 follow from the shaders,
and set 1 is a contract between them that only the frame can state:

```yaml
viewBlock:
  binding: 0
  stages: Vertex
```

Declared with no members it takes the standard block — the view-projection at 0 and the view position
at 64, which `ViewConstants` writes for every view whether or not anything asked. A member names the
parameter key rather than an offset alone, so a document cannot drift from the block a shader reads
without the build refusing it. The builder creates the descriptor set layout, which makes it the one
piece of device state a build produces — and the caller owns it, because a builder outlives nothing.

`RenderView.ViewProjection` had to exist first, and **setting it re-derives the frustum**. Two
properties describing one volume is a bug waiting to be written — a view culled against last frame's
planes and drawn with this frame's matrix drops geometry at the edges and reports nothing. The shadow
renderer had exactly that shape: it built the frustum from a matrix it then discarded.

**A set cannot be bound before the first pipeline**, which is where this stopped being a design
question and became an API one. `ICommandList.BindDescriptorSet` takes no pipeline layout and infers
one from what is bound, so binding set 1 at the start of a view — the obvious place — is undefined,
and the Vulkan backend refuses it outright. So a compositor node says *what* through
`RenderDrawContext.ViewConstants` and `MeshRenderFeature` says *when*, immediately after its first
pipeline. Once per run is enough, because the convention makes every pipeline in a frame compatible
up to set 1.

The proof is the shadow golden fixture: it used to compose the cascade's matrix into the caster's
world transform, and now the matrix arrives through set 1 — **against the same reference image**.

## Writing to memory a frame is still reading

The hazard that keeps reappearing, and the one no API reports. `Write` on a host-visible buffer is a
memcpy into memory the GPU may still be reading for a frame that has not finished. Nothing validates
it, nothing logs it, and the symptom is data that is briefly a blend of two frames — under load, on
somebody else's machine.

Three things had it. `ForwardLightingRenderFeature` rewrote one persistent descriptor set; that was
fixed by moving it onto `DescriptorAllocator`. `UploadBuffer<T>` — which skinning, instancing and the
scene light list all share — wrote every frame's records at offset zero. And `EffectConstants` wrote
every changed block over the last one.

All three now use the same shape: **one region per frame in flight, and the caller binds at an
offset**. Offsets rather than shifted indices, deliberately, so that a push-constant base, a
`firstInstance` and a shader indexing from zero all keep working without knowing the ring is there —
the ring is a property of the binding, not of the data.

`EffectConstants` moves only when a value actually changed, so a post pass whose parameters are the
same every frame keeps reading the region it already has and the ring costs nothing.

**And one of them is a ring whose regions are not interchangeable.** `PersistentUploadBuffer<T>` —
the culling scene's object records — keeps its contents across frames rather than refilling them,
because a hundred thousand object bounds are the same bytes they were last frame for all but a
handful. That turns the ring's invariant inside out: this frame's region is not empty, it is
`FramesInFlight` frames *stale*, and what it is missing is every change since it was last written. So
a change is marked dirty in every region and each of them flushes its own set when its turn comes.
One moved object costs one record per frame for three frames, rather than three megabytes once.

Which records changed is decided by **comparing the bytes**, not by a flag a writer sets. Anything
holding a `ref RenderObject` can move an object, and a writer that forgets to say so would be
silently wrong — bounds a frame culled against, with nothing anywhere to say why. The comparison
cannot miss one, and it reads exactly the data the culling loop reads anyway.

**Destroying is not the same problem, and it was already solved.** Growing one of these buffers hands
the old handle back while the frame that used it may still be running — which is safe, because every
`Destroy` on `IGraphicsDevice` is deferred by `FramesInFlight`. The contract is now stated on the
interface rather than only in the Vulkan backend that implements it, and
`ValidationCleanTests.DestroyingAResourceAFrameIsUsingProducesNoValidationMessages` asserts it against
a driver.

The two are easy to conflate and worth keeping apart: **the RHI defers handing a resource back, and
nothing can defer overwriting one's contents.** The first is the backend's job and is done; the second
is the caller's, and is what the ring above is for.

## What is not here yet

Blend shapes. Punctual shadows are not cached — only the directional cascades are, and a spot light
over static geometry has the same argument waiting for it.

**Light probes reach a frame.** The tetrahedralisation and the interpolation are built and tested —
see [Light probes](#light-probes-and-the-predicates-they-turned-out-to-need) — and what is still owed
is the GPU half: a buffer of coefficients, a per-object index or a compute lookup, and an ambient
term in `ForwardLightingRenderFeature` that comes from a probe rather than from the environment.
`LightProbeVolume.Sample` is a CPU call, which is enough for a bake and not enough for a frame.

Transmission has a surface feature's worth of channels and no shading model, deliberately: refraction
needs either the scene colour or an environment sample, both of which belong to the pass rather than
to the lobe, and inventing a `Shade` that could not reach them would be a feature that compiles and
does nothing. Back-lit thin surfaces are covered — that is `SubsurfaceShading`.

Indirect lighting does not go through the shading model. `Ambient` is the pass's, so a cel-shaded
material still takes a physically-based IBL term, and a clear coat has no second lobe against the
environment.

Instance batching by locality: an instanced batch is culled as one object, so what goes in one is the
caller's decision and there is nothing here to help make it.

A compositor **does** resolve by address: `Vixen.Assets.Tests.CompositorContentTests` writes one into
a bundle, asks for it by address and builds a running frame from what comes back. It is asserted
there rather than here because this assembly does not reference the content system and should not —
which is why the claim stayed open so long. Nothing was missing; nothing had put the two halves in
one room.

A node's bindings **are** authorable, and this section used to say the opposite — see
[Authored](#authored) above, which is where the current answer lives. A binding names what the shader
calls the resource and the index comes off `Effect.Bindings`; a sampler names a preset the frame's
`SamplerCache` turns into a description. What a document still cannot carry is the four things only a
running renderer has — a device, a module cache, a descriptor allocator, a sampler cache — and
`CompositorBuilder` is what supplies those.

The generated keys cover the shaders the engine names — `PostFx/Bloom` and `PostFx/Tonemap` — and
nothing else. The list grows when a node starts binding a shader, not in anticipation, because every
entry is a file somebody has to keep compiling.

Reflection describes **one variant**, so a resource only a non-default variant reads gets no key at
all. Bloom is exactly that shape — `previous` is read only by the upsample mode — and a test asserts
it survives the default rather than leaving that to luck. A shader that failed it would generate a
node that does not compile, with no hint as to why.

Bloom has no lens flare and no light streak, and the tonemap pass has no grading LUT as an asset —
the shader takes one, nothing loads one.

**Compacted draws are built**, and this section used to say they were the thing blocked on
everything. `GpuDrawArguments.Compact` appends survivors to a run per batch and
`MeshRenderFeature` covers a whole batch with one `DrawIndexedIndirectCount` — see
[docs/bindless-materials.md](../../docs/bindless-materials.md), which is the record of the whole
chain. Both halves of the old objection are gone: the count comes from a buffer the host never reads,
and objects share their bindings because a material is a record (`MaterialRecords`) and geometry is a
range of a shared buffer (`GeometryBuffer`).

**The transform is out of the command buffer too.** `TransformRenderFeature.EnableRecords` puts every
object's matrix in one buffer at the object's own slot and carries the slot in the draw's own
`firstInstance` — the field `InstancingRenderFeature` has always used, which the compaction shader
copies and the API adds into `SV_InstanceID` before the vertex stage runs. A push constant is not a
binding, which is why nothing in the bindless plan touched it, and it stopped a merge anyway: data in
the command buffer is per command by construction. `ForwardPlusKeys.UseTransformRecords` is the
shader's half, and the gate is `HasDrawIndirectCount` — not because a device could not read the
buffer, but because without a merged command to gain the read is a straight loss.

**What is left is the per-object light block, and only where there is one.** With a uniform light
list `ForwardLightingRenderFeature` binds each object's block at its own dynamic offset, and a
dynamic offset travels in the *bind* rather than in the block — nothing inside a merged command can
change it. With clustering on it binds nothing per object, so a clustered frame with transform
records does merge. That is why the gate asks a sub-feature what it is *doing* this frame
(`IDrawSubFeature.IsRecording`) rather than what type it is: the type gives the same answer to both
of those and it is wrong for one of them. There is a test on each side.

**And `materialIndex` is out of that block too**, because it was never per-object data: a variant is
keyed `(material, flags, shader)`, so a batch is one material and one record. It is a push constant
now, pushed once per *run* at the offset the effect declares — `EffectPushConstant.OffsetOf`, since
nothing is generated for a push block and the only offset a host had was one it assumed. That fixed a
real exclusion: the clustered path binds no per-draw set, so bindless materials and clustered lighting
could not both be on.

**`probeIndex` and `probeWeight` came out of that block too**, but as records rather than as a push:
a probe is chosen by where the object *is*, so those are genuinely per object. They go in a buffer
`ForwardLightingRenderFeature` owns, read in the fragment stage through a flat `objectIndex` varying —
which needed Raven to emit `Flat` on integer fragment inputs, because it did not. `UseObjectRecords`
takes "is the record addressable" as a parameter rather than checking, since the answer is the
transform feature's: `SV_InstanceID` holds the object's slot only because `firstInstance` does.

**A compositor document turns all of this on.** `gpuDriven:` on the asset root carries
`materialRecords` and `transformRecords`; `compact:` sits on the culling node beside `indirectDraws`.
Every flag is a request the device answers, so one authored frame runs on a machine with descriptor
indexing and on one without — `CompositorBuilder.GpuDriven` reports which.

⚠ **Except the table itself, which nothing outside the tests creates.** `MaterialRenderFeature.Textures`
and `TextureIndices` are still host-supplied, so a material that samples through
`TexturedMetalRoughnessSurface` keeps `baseColorIndex = 0` unless a host wired one. Worse, that
surface declares set 4 whatever the permutation says — bindings are declared, not discovered — so its
pipeline layout has five sets while the draw loop binds the fifth only when a table exists. A table
needs a capacity and a *fallback view*, and both are project decisions; see
[docs/bindless-materials.md](../../docs/bindless-materials.md) § *The one thing left* for the two
shapes it could take and the guard that belongs with either.

## Testing

`Vixen.Rendering.Tests` holds culling to a **brute-force oracle over randomised scenes** (doc 06's
testing table asks for exactly that), pins that the parallel path agrees with the inline one word for
word, and asserts that a settled frame of 10 000 objects through extract → cull → sort **allocates
nothing**. The GPU path is held to the CPU one the same way: `GpuCulling.IsVisible` is the host's
transliteration of the shader, compared against `VisibilityGroup` object by object and view by view
over randomised scenes, with the *shape* of the pass — one invocation per word per view, a barrier
between the dispatch and the copy out of it — asserted against the recorded command stream. The
occlusion half is mirrored the same way and tested against a pyramid a test writes by hand: a wall at
the near plane occludes, an empty frame does not, and an object reaching behind the camera is kept.
What none of them can see is the shader still containing the arithmetic the mirror mirrors, so two
tests read `Culling.rvn` and `HiZReduce.rvn` themselves — the same defence the clustered path uses,
and pointed at the two lines (the rounding slack, and the min-versus-max of the depth comparison)
whose reversal is invisible everywhere else.

**And the device is asked about all three passes**, in
`Vixen.Graphics.Golden.Tests/ViewCullingDeviceTests`: the shaders are compiled through the compiler
the content build uses, and then the frustum cull is compared against the CPU path over five hundred
randomised objects and two views, the pyramid is built from a texture the test filled and compared
against a reduction written out again here, and the argument pass is run over the culler's own bits
and read back record by record. It is worth more than its size, because it is the only test that can see three
things a mirror structurally cannot — that `CullObject` really is thirty-two bytes and `CullView` two
hundred and eight *on the other side of the binding*, that the descriptor plan is the shader's own
rather than the one the host imagined, and that two 32-bit words reassemble into ours the way round we
think. It found all three classes of problem on its first run: a shader no Metal driver would compile
(see doc 07 on `OpName`), a permutation that removes the sampling but not the binding — so the host
had to fill an occluder slot it did not have — and a placeholder texture bound in no layout, which the
validation layers named exactly. The indirect path is asserted where it shows: the node
records two dispatches with a barrier between them, the templates are the numbers the direct draw
would have used, and `MeshRenderFeature` emits `DrawIndexedIndirect` at the object's own slot when
the arguments cover it and `DrawIndexed` when they do not. The last one is the guard that a change starting to allocate per object per frame fails a
test rather than appearing months later as a GC spike nobody can attribute.

Everything from the mesh feature outward is driven through the **Null backend and asserted against
the recorded command stream**, so what is checked is the calls that were made rather than the
intention behind them: four objects sharing a material produce one `BindPipeline`; four objects
produce four binds of one light set at four distinct offsets; the same objects in two passes of
different formats produce two pipelines and in two passes of the same format produce one.
