---
title: Blend shapes
slug: rendering/blend-shapes
kind: concept
area: Rendering
summary: Sparse quantised vertex deltas, imported off a mesh's morph targets and applied by a compute pre-pass so that every pass agrees about where a vertex is.
api: [T:Vixen.Rendering.MorphTargetData, T:Vixen.Rendering.MorphKernel]
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

The consumer already does it safely: `ForwardPlus`'s fragment stage calls `Math.SafeNormalize` on the
interpolated normal, whose tolerance is `1e-4` and whose degenerate answer is zero rather than a NaN.

### What it costs

Sixteen bytes per moved vertex per shape with normal deltas, ten without — four for the index, six for
each quantised triple. A head with twenty shapes each touching four thousand vertices is **1.28 MB**,
resident, shared by every instance of the mesh. `MorphTargetData.SizeInBytes` reports it per target.

Resident rather than streamed: the deltas are read by a pre-pass that runs whenever any weight is
non-zero, which for a character on screen is every frame.

### What is not built yet

- **The render feature.** Nothing in the frame path yet allocates a per-instance vertex buffer, copies
  the base mesh into it, dispatches the scatter and points `MeshDraw.VertexBuffer` at the result. That
  is the wiring, and the seam it goes through is already the right one — `MeshDraw` is per render
  object and every stage reads the same array, so a feature that overwrites the handle morphs the
  shading pass, the shadow pass and the velocity pass together by construction.
- **A weight track on `AnimationClipData`.** glTF animates morph weights through a sampler targeting
  `weights`, and FBX through a morph channel; `AnimationChannel` has three tracks and none of them is
  a scalar. Reading them needs that member, which is a format change.
- **Cluster pages.** A virtualized mesh's vertices are packed into pages rather than a vertex buffer,
  so morphing one is a different scatter against `ModelCompiler.PageAttributes`' layout.

## See also

- [Meshes and materials, type by type](mesh-and-material.md) — where `MeshData` and `MeshDraw` sit in
  the chain.
- `docs/plan/33-character-creator.md` § D4 — the design, and why it is a pre-pass.
- `docs/plan/06-rendering-pipeline.md` § Geometry — the row this closes half of.
