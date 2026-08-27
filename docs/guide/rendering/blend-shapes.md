---
title: Blend shapes
slug: rendering/blend-shapes
kind: concept
area: Rendering
summary: Sparse quantised vertex deltas, imported off a mesh's morph targets and applied by a compute pre-pass into a per-instance vertex buffer — or, for a virtualized mesh, gathered per vertex where a page is decoded — so that every pass agrees about where a vertex is, driven by hand, by an imported clip, or by a weight curve typed into a .vxanim.
api: [T:Vixen.Rendering.MorphTargetData, T:Vixen.Rendering.MorphKernel, T:Vixen.Rendering.MorphIndex, T:Vixen.Rendering.Features.MorphRenderFeature, T:Vixen.Rendering.Features.MorphInstance, T:Vixen.Rendering.Ecs.BlendShapeWeights, T:Vixen.Rendering.Ecs.MorphWeightSystem, T:Vixen.Animation.MorphWeightBuffer, T:Vixen.Animation.AnimationClipContent, T:Vixen.Animation.Ecs.BlendShapeAnimationSystem, R:Pipeline/MorphScatter]
tags: [rendering, animation, meshes, characters, morph-targets]
since: 0.2
status: preview
related: [rendering/mesh-and-material, rendering/lit-path, assets/content-in-a-game]
---

## What it is

A **blend shape** — a *morph target*, a *shape key*, depending on which tool exported it — is a named
displacement of a mesh's vertices. `jawOpen` at weight 1 is the head with its mouth open; at 0.4 it is
four tenths of the way there. Vixen stores one as a `MorphTargetData` and applies a set of them with
`MorphKernel` on the host or `Pipeline/MorphScatter.rvn` on the device.

Three facts decide the shape of everything below:

| | |
|---|---|
| **Deltas are sparse** | A brow-raise moves a few hundred vertices of a forty-thousand-vertex face. Storing one delta per mesh vertex per shape would make twenty shapes larger than the mesh. |
| **Deltas are quantised** | Sixteen-bit signed components against a range the target carries, so an entry is sixteen bytes rather than twenty-eight. |
| **Application is a pre-pass** | The morphed vertices go in a buffer, and that buffer is what the shading, shadow, velocity and depth passes all read. |

## What it is for

The third fact is the one that is about correctness rather than cost. The obvious implementation is a
loop inside every vertex stage: for each target, read this vertex's delta, add it. That reads every
delta for every vertex — including the ones the shape does not touch — and then does it again in the
shadow pass, the motion-vector pass and the depth pre-pass. Worse, the four can *disagree*, and the
symptom is a face whose shadow does not match it.

A pre-pass writes once. Everything downstream binds the same buffer and cannot disagree.

The design is `docs/plan/33-character-creator.md` § D4, which also explains why this lives in
`Vixen.Rendering` beside `SkinningRenderFeature` rather than in a character system: a game that never
touches a character gets facial animation on a hand-authored head out of it.

## Using it

### Drawing a morphed mesh

`WorldRenderer` wires the whole of it, so a game does two things: import a model with shapes, and put
the weights on the entity.

```csharp no-compile="a fragment; `world` and `entity` are a game's own"
world.Add(entity, new BlendShapeWeights { Weights = [1f, 0.4f] });
```

One weight per `MeshData.MorphTargets` entry, in that order. A shorter array is read as zero for the
rest, so an entity that only ever opens its jaw carries one number; null and empty are both "at rest".
`MorphWeightSystem` pushes them at `MorphRenderFeature` every frame, and the feature does the
comparing — so a face holding an expression costs a comparison rather than a copy of its whole vertex
range.

⚠ **Every weight is applied, including a negative one and one past one.** An exporter that authored a
shape as the inverse of its neighbour relies on the first, and an animator overshooting a corrective
relies on the second.

⚠ **An array field makes `BlendShapeWeights` a *managed* component.** Its values live in the world's
store and a chunk column holds a four-byte handle, so `Chunk.ReadValues` refuses it and a system reads
one entity at a time — the way `SkinningSystem` reads its animator.

The editor's viewport runs the same two things, wired by hand rather than by a system graph, so a
weight typed into the inspector moves the mesh in the scene view.

### What the frame does with them

`MorphRenderFeature` is a sub-feature of `MeshRenderFeature`, on `RenderFeature`'s terms: a morphed mesh
and a still one are drawn the same way with different data. When a blend-shaped mesh is extracted it
takes a vertex range of its own, and its `MeshDraw` is pointed at that range instead of at the scene's
shared buffer. Every frame, `WorldRenderer.Draw` records the pass — after `GeometryResidency.Flush` and
before any draw, outside any render pass — which:

1. copies each changed instance's **rest pose** out of the scene's `GeometryBuffer` into its own range;
2. dispatches `MorphScatter` once per **active** shape, with a barrier between;
3. leaves the buffer in `VertexInput` for the draws.

| | |
|---|---|
| **Deltas are per mesh** | Two characters wearing one head share its entry run. `MorphRenderFeature.MeshCount` is how many are resident. |
| **Vertices are per instance** | Because the weights differ. Forty-eight bytes a vertex each, out of a fixed `vertexCapacity` — `Dropped` counts what did not fit and is drawn at rest. |
| **Only what changed is recorded** | `Copies` and `Dispatches` are both zero on a frame where every morphed character held still. |

⚠ **The index buffer is not touched and must not be.** A morph displaces vertices and never renumbers
them, so the object keeps drawing the scene buffer's indices — which is why `MeshDraw.VertexOffset` is
rewritten in the same breath as the handle. An index is relative to it.

⚠ **The first frame copies even with every weight at zero.** An instance that reached its first record
clean would be drawn out of whatever the allocator left in its range, and that does not look like a
morph gone wrong — it looks like the geometry is missing.

⚠ **`MorphRenderFeature.Degraded` is what a frame that could not dispatch says.** It still copies the
rest pose, so the mesh is drawn unmorphed rather than out of an uninitialised range, and the instance
stays dirty so that the frame after the shader compiles is right.

### Animating one from a clip

A clip drives a shape by **name**. `AnimationChannel` carries a scalar track —
`Shape`, `WeightTimes`, `Weights` — beside its three vector ones, the importer fills it from a glTF
`weights` sampler or an FBX morph channel, and three things happen every frame with no wiring beyond
`AddAnimation()`:

1. `ClipMotion` collects the clip's weights into `Animator.MorphWeights` as the blend tree is
   evaluated, scaled by what that clip is contributing;
2. `BlendShapeAnimationSystem` lands them on the entity's `BlendShapeWeights`, slot by slot;
3. `MorphWeightSystem` pushes those at the feature, as it already did for a hand-set weight.

```csharp no-compile="a fragment; the clip came out of a model import"
animator.AddLayer("Face", new AnimationStateMachine([new AnimationState("Talk", new ClipMotion(clip))]));
```

⚠ **A name and not a slot, and the difference is not cosmetic.** The ordinal a source file addresses
a morph target by is *not* `MeshData.MorphTargets`' ordinal — the import drops a shape that moves
nothing above `BlendShapeThreshold` and deduplicates the names of the rest — so a curve stored against
an index would silently re-target itself on the next export. `BlendShapeWeights.Shapes` is the
translation, and `MorphWeightSystem` publishes it out of what the feature actually attached.

⚠ **Which means an entity is bound the frame *after* it appears**, because the feature has nothing
attached until extraction has run. A face is drawn at rest on the frame it spawns, which is the frame
its rest pose is being copied in anyway.

⚠ **A weight of zero is a value and an absent track is not.** `WeightTimes.Length` is what says the
clip drives a shape at all; a curve that is flat at zero holds a face at rest, and a clip that says
nothing about a shape leaves whatever a script set. `BlendShapeAnimationSystem` writes only the slots
the animator named — writing all of them would make playing a wave animation wipe an expression.

⚠ **Weights add across layers rather than overriding.** Inside a blend tree that is exact, because a
tree's child weights sum to one. Across layers it is not: two layers both driving `jawOpen` as an
override produce their sum. A facial layer is normally additive, which is what this serves; the
machinery that models an override works on joints, and a shape is not one.

⚠ **`AnimationClip.UnresolvedChannels` does not count a weight channel.** It names the morphed mesh's
node, which is not a joint — counting them would report a head's worth of unresolved channels on every
correct import and drown the signal that number exists for.

### Importing

`ModelImportSettings.ImportBlendShapes` is on by default. `BlendShapeThreshold` is what makes the
result sparse — an exporter writes a delta for every vertex of the mesh, most of them zero and the
rest rounding noise, and a vertex under the threshold in *both* its position and normal delta is
dropped.

```yaml
importer: !ModelImporter
  scale: 0.01
  importBlendShapes: true
  blendShapeThreshold: 0.0001
```

⚠ **The threshold is in the model's units after `scale`**, not in the file's. It has to be: a tenth of
a millimetre means one thing on a character and another on a building, and the importer is the only
thing that knows which it just read.

What arrives is `MeshData.MorphTargets`, and `MeshData.IsMorphed` says whether there are any.

### Reading a target

```csharp no-compile="a fragment; `mesh` is a MeshData the content build produced"
foreach (var target in mesh.MorphTargets) {
    for (var entry = 0; entry < target.Count; entry++) {
        var vertex = target.Indices[entry];
        var movement = target.PositionDelta(entry);
        var reshading = target.NormalDelta(entry);
    }
}
```

### Applying weights

On the host — which is what a test, a tool, a physics cook or a headless server wants:

```csharp no-compile="a fragment; `mesh` is a MeshData and `weights` a span the animation filled"
var morphed = new SurfaceVertex[mesh.VertexCount];

MorphKernel.Apply(SurfaceGeometry.Packed(mesh), mesh.MorphTargets, weights, morphed);
```

On the device, `Pipeline/MorphScatter.rvn` is the same arithmetic as a compute kernel. Its buffer of
entries is `MorphKernel.Pack`'s output — the same sixteen bits the host reads, so the two cannot
diverge by rounding differently — and the host supplies the stride and the two offsets into the vertex
rather than the shader assuming a layout.

⚠ **One dispatch per *active* target, with a barrier between.** Two shapes may move the same vertex —
that is what a corrective is — so a single dispatch over their concatenated entries would have two
invocations read-modify-writing one vertex, and the answer would be whichever landed last. Within one
target the indices are distinct by construction.

⚠ **The destination buffer must already hold the base mesh**, because the kernel adds. Dispatching
onto an uninitialised buffer does not look like a morph gone wrong; it looks like the geometry is
missing.

## Examples

### The normal is not renormalised, and that is deliberate

A morphed normal is `n + Σ wᵢ·Δnᵢ`, whose length is not one. Nothing in the pre-pass normalises it,
for two reasons:

- A shape may cancel a normal exactly — `Δn = −n` at full weight is an authored shape, not a corrupt
  one — and `Vector3.Normalize` gives up below an **absolute** `1e-6` and answers with infinities.
- `rsqrt` and `1/sqrt` are not the same function, so a normalise would be a host/device divergence
  that has nothing to do with morphing.

⚠ **The *base* normal is normalised, though, and by somebody else.** `SurfaceGeometry.Pack` does it on
the way into the vertex buffer — an unset normal becomes `+Y` and a set one is made unit — so the rest
pose a frame morphs is the packed mesh's, not the `MeshData`'s. A comparison written against the
unpacked arrays reports a difference of about `1e-4` in every moved normal and looks exactly like two
processors doing different arithmetic.

The consumer already does it safely: `ForwardPlus`'s fragment stage calls `Math.SafeNormalize` on the
interpolated normal, whose tolerance is `1e-4` and whose degenerate answer is zero rather than a NaN.

### What it costs

Sixteen bytes per moved vertex per shape with normal deltas, ten without — four for the index, six for
each quantised triple. A head with twenty shapes each touching four thousand vertices is **1.28 MB**,
resident, shared by every instance of the mesh. `MorphTargetData.SizeInBytes` reports it per target.

Resident rather than streamed: the deltas are read by a pre-pass that runs whenever any weight is
non-zero, which for a character on screen is every frame.

### Writing a weight curve by hand

A `.vxanim` is the authored form, and `Weight` is one of its properties. What identifies a curve
there is the pair **(property, shape)** rather than the property alone, because a face's node carries
one weight curve per shape:

```yaml
version: 2
name: Expression
duration: 2.4
wrap: Loop
targets:
  - target: Head
    curves:
      - property: Weight
        shape: jawOpen
        keys:
          - { time: 0.0, value: 0.0, mode: Auto }
          - { time: 0.5, value: 1.0, mode: Auto }
```

`Samples/03-PbrShowcase/Assets/Animation/expression.vxanim` is the working example, and the editor's
dope sheet shows one row per shape — `Head · Weight · jawOpen` — rather than one row called `Weight`.

⚠ **`target` names the morphed mesh's *node*, not a joint**, which is what the imported form does and
for the same reason. `AnimationClip.Create` resolves a weight channel before it looks a joint up, so
a face's curves stay out of `UnresolvedChannels`. A target whose curves are *all* weight curves emits
no transform channel at all — an empty one would put the mesh's node in that count, and a correct
facial clip would report one unresolved channel per face.

⚠ **A weight curve with no `shape` is a build error.** It is the one mistake this format makes easy
and the one that says nothing: it would import, ship, play, and hold a face perfectly still.

⚠ **`version: 2` is a compatibility fence, and it is the odd one out.** The version bumps beside it —
`ModelImporter.Version` 10, 11 and 12, and `AnimationClipContent.Current` 2 — are re-import triggers: those
are binary chunks whose generated reader takes an appended member as its default. This is YAML bound
by name and the value that moved is inside an *enum*, which `Enum.Parse` throws on, so an older build
meeting `property: Weight` fails outright. The number a file carries is therefore the minimum it
needs: **a clip with no weight curve is still written as version 1**, so the rest of a project's
clips are not fenced for a member none of them uses.

### Driving one without a rig

```csharp no-compile="a fragment; `clip` came out of the catalog and `head` is an entity"
foreach (var (index, shape) in world.Read<BlendShapeWeights>(head).Shapes!.Index()) {
    if (clip.TrySampleWeight(shape, seconds, out var weight)) {
        weights[index] = weight;
    }
}
```

`AnimationClipContent.TrySampleWeight` is `TrySample`'s sibling and exists for its reason: half of
what the authored format is for has no rig, and a head that is one mesh has no joint for
`AnimationClip.Create` to resolve against. A character with an `Animator` should take the baked path
and let `BlendShapeAnimationSystem` land the weights.

⚠ **The loop is over `BlendShapeWeights.Shapes` and not over the clip**, and the return value is the
fact rather than the weight. A shape the clip says nothing about keeps whatever a script set; a false
read as zero is an additive facial layer turned into an override by accident.

### A virtualized mesh gathers instead

A mesh with a cluster hierarchy is drawn by `VirtualGeometryRenderFeature`, out of pages, and never
reaches `MorphRenderFeature.Attach`. It morphs anyway, and by a different mechanism — which is worth
understanding before writing a shader that touches either path.

⚠ **A page is per *mesh* and every instance reads the same bytes.** That sharing is what makes
streaming a hundred thousand clusters affordable. Weights are per instance. So there is nowhere for a
per-instance scatter to write, short of giving every instance a private copy of every resident page it
touches — and that is the one property the whole of `docs/plan/22-virtualized-geometry.md` rests on.

So the paged path does what it already does for skinning: it **gathers**, in the shader, per instance.
`ClusterRaster` decodes a page vertex and then transforms it by that instance's own bone palette; it
now adds that vertex's own shapes first. `MorphIndex` is the table that makes "that vertex's own
shapes" a lookup — a mesh's targets re-indexed by vertex, built at registration out of
`MeshData.MorphTargets`, not an artefact.

| | |
|---|---|
| **No barrier, and no race** | `MorphKernel` dispatches per target because two shapes may move one vertex and there is no float atomic. A gather sums a vertex's own shapes inside one invocation, so the question never arises. |
| **Three shaders, one function** | `ClusterRaster`, `ClusterSoftwareRaster` and `VisibilityResolve` each decode a page vertex, so each carries `Morphed` — character for character, which `MorphedClusterTests` asserts, exactly as `Skin` is. |
| **Morph, then skin** | A delta is authored in the mesh's own space, which is what a page decodes into. Skinning first would put a jaw's displacement in the head's bind pose. |
| **Only the resolve morphs the normal** | A visibility buffer carries an identity, so a raster needs a position. A resolve left unmorphed is a face whose geometry opens its mouth and whose shading does not. |

⚠ **A page vertex does not know which mesh vertex it is**, which is why this costs two indirections
where the classic path costs none. A cluster's vertex list is the DAG's — `MeshletMesh.Vertices` — and
the page carries only the position and attributes copied out of it. The happy consequence is that a
vertex on a locked boundary appears in a cluster on each side and at several levels, resolves to one
source vertex from all of them, and picks up the same delta: **the cut stays crack-free through an
expression**, which a scatter into quantized page bytes could not have promised.

⚠ **The instance's bound is inflated by what its expression is actually doing** —
`Σ |wᵢ|·MorphIndex.Reaches[i]`, added into `CullInstance.MotionRadius` beside whatever a pose put
there. Every bound in the DAG is a rest-pose bound, and a cluster culled by where it is not says so
nowhere. Per instance rather than per mesh, because the mesh-wide sum is loose by the number of
shapes: a twenty-shape head making one expression would be tested against a bound twenty shapes wide.

⚠ **A weight past one is applied and is not covered by that bound.** Every weight is applied, an
animator overshooting a corrective relies on it, and a bound computed at full weight does not reach
it. The failure is a cluster culled a frame early at the silhouette rather than corruption, and the
alternative costs every frame for a case no exporter produces.

`ModelImporter.Version` 12 is what turns this on for a project: version 11 refused a cluster hierarchy
for a morphed mesh outright, because the paged path drew it at rest. Re-importing gets the hierarchy
back — see below for what that bump is and is not.

## What is not built yet

- **Device-local growth.** `MorphRenderFeature`'s buffer is fixed at construction and an instance that
  does not fit is refused — `GeometryBuffer`'s trade, for `GeometryBuffer`'s reason: the handle is
  already in every `MeshDraw` that was attached.
- **The software raster's copy is unverified on Apple silicon.** MoltenVK reports no 64-bit buffer
  atomics, so phase 6's routing is forced off and no morph through it has been drawn on this machine.
  Its `Morphed` is character-for-character the hardware raster's, which is the only defence a
  duplicated fetch has and is the same one `Skin` has.
- **A mesh's shapes are resident whether or not it is paged.** `MorphIndex` is built from
  `MeshData.MorphTargets` at registration, so a virtualized head streams its geometry and keeps all
  1.28 MB of its deltas — the pre-pass's argument for residency, applied to a path that streams
  everything else.

## See also

- `Samples/03-PbrShowcase` — the two heads in the foreground, one at rest and one driven by
  `expression.vxanim`. Its README says what to look at and why there are two.
- [Meshes and materials, type by type](mesh-and-material.md) — where `MeshData` and `MeshDraw` sit in
  the chain.
- `docs/plan/33-character-creator.md` § D4 — the design, and why it is a pre-pass.
- `docs/plan/22-virtualized-geometry.md` — the paged path, and why a page is per mesh.
- `docs/plan/06-rendering-pipeline.md` § Geometry — the row this closes half of.
