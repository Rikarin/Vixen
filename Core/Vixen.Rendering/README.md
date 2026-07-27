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

`MaterialRenderFeature` is where the shader half of the engine meets the renderer half: preparation
turns a material's `ParameterCollection` into an `EffectKey`, resolves it, and remembers the answer
per object — so by recording time "which shader" is an array lookup. It resolves **per material, not
per object**: ten thousand objects sharing twenty materials resolve twenty times.

**The sort group comes from the resolved effect**, and that is what closes the loop. Objects that
will bind the same pipeline get the same group, the key puts groups above depth, they land adjacent,
and the mesh feature sees one run and binds once. Break any link and four objects sharing a material
become four pipeline binds — which is asserted, not assumed.

The transform goes out as **push constants**: the smallest, most per-draw thing a frame has, with no
descriptor, no upload-ring allocation and no offset to track. A `mat4` is 64 bytes against Vulkan's
guaranteed 128, and Raven warns at `RVN3007` if a shader's block exceeds that, so both sides agree
about the budget. The matrix is sent unchanged — see the `Matrix4x4` note in
[Vixen.Shaders](../Vixen.Shaders/README.md).

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

**Clustered lighting does no per-object work at all.** No selection, no block per object, no
descriptor bound per draw — `ForwardLightingRenderFeature.Clustered` turns the whole per-object path
off, and eight objects produce eight draws and nothing else. That is the point of the pipeline, and
it is easy to claim and easy to get wrong, so it is asserted.

## What is not here yet

Blend shapes, area lights, and clustered light culling on the CPU side (the shader half,
`Library/Pipeline/ClusterCulling.rvn`, already exists). Punctual shadows are not cached — only the
directional cascades are, and a spot light over static geometry has the same argument waiting for it.

Instance batching by locality: an instanced batch is culled as one object, so what goes in one is the
caller's decision and there is nothing here to help make it.

The shadow renderers still take a light direction and a camera from a host rather than from the
scene, and nothing yet resolves a compositor by *address* — the binary form is proven, the
`AssetManager` lookup around it is not wired up here.

`ComputeRenderer` binds its own resources through a callback rather than owning a descriptor set,
because the buffers it reads are graph resources whose handles do not exist until the graph has
allocated them. A per-frame descriptor allocator is the missing piece, and it is missing engine-wide
rather than here.

GPU-driven culling is a second implementation of `VisibilityGroup` behind the same interface, which
is why that interface is bits rather than a list.

## Testing

`Vixen.Rendering.Tests` holds culling to a **brute-force oracle over randomised scenes** (doc 06's
testing table asks for exactly that), pins that the parallel path agrees with the inline one word for
word, and asserts that a settled frame of 10 000 objects through extract → cull → sort **allocates
nothing**. The last one is the guard that a change starting to allocate per object per frame fails a
test rather than appearing months later as a GC spike nobody can attribute.

Everything from the mesh feature outward is driven through the **Null backend and asserted against
the recorded command stream**, so what is checked is the calls that were made rather than the
intention behind them: four objects sharing a material produce one `BindPipeline`; four objects
produce four binds of one light set at four distinct offsets; the same objects in two passes of
different formats produce two pipelines and in two passes of the same format produce one.
