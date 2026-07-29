# Shaders

The editor's own shaders, in Raven. Eleven modules from four sources: four interface pipelines over one
vertex stage, a line pair, and two mesh pairs.

| Source | Shaders | Modules |
|---|---|---|
| `Ui.rvn` | `UiVertex`, `UiBox`, `UiText`, `UiSolid`, `UiImage` | one `.vert.spv`, four `.frag.spv` |
| `Line.rvn` | `LineVertex`, `LineFragment` | one of each |
| `Mesh.rvn` | `Mesh` | one of each |
| `MeshInstanced.rvn` | `MeshInstanced` | one of each |

## Why there are two mesh shaders

`Mesh` takes triangles that are already in world space and `MeshInstanced` takes a shape and a transform
per entity, and the split is the same one `MeshRenderer` and `MeshInstanceRenderer` are: geometry that
is genuinely rebuilt every frame against geometry that is not. The gizmo's solid handles are the first —
a handle is a different size and colour at every camera position, and there is one of it — and the
scene's shapes are the second, which `docs/blockout-tools.md` § B1 is the argument for.

⚠ **`MeshInstanced`'s eleven vertex attributes are two buffers rather than one**, and the second one
steps per *instance*: the shape's position and normal come from the geometry buffer, and the four
transform rows, three normal-matrix rows, colour and style come from the frame's instance ring. The
step mode is the host's to set — Raven has no opinion about it — so `MeshInstanceRenderer` is where a
mistake there would live, and what it would draw is the whole scene as one exploded object.

## Regenerating

From the repository root, one command per source:

```bash
dotnet run --project Raven/Vixen.Raven.Cli -- compile --target spirv Editor/Vixen.Editor.App/Shaders/Ui.rvn Editor/Vixen.Editor.App/Shaders/ --emit-reflection
```

…and the same for `Line.rvn`, `Mesh.rvn` and `MeshInstanced.rvn`. The `.spv` and the `.reflect.json`
beside each source are committed, for the reason `Samples/01` and `Samples/02` give: `UiRenderer`'s
modules are *supplied* rather than compiled, so a caller hands over what it has.

⚠ **`--emit-reflection` is not optional.** The `.reflect.json` is what `Vixen.Shaders.Generators`
reads, and what it tells `EditorHost` is where each vertex attribute lives. Regenerating a module
without it leaves the host binding against the previous source's numbering — which is not a
validation error but a stage reading whatever the driver left in an attribute nothing was bound to.

`spirv-val --target-env vulkan1.2` over the eleven `.spv` is worth running after: Raven's SPIR-V is
checked against the validator in its own tests, but these are the modules the editor actually loads.

## Why five shaders and not four pairs

`UiRenderer` takes one vertex module and a fragment module per pipeline, because the four interface
pipelines read one vertex layout — two layouts would mean two buffers and two uploads to save sixteen
bytes on a vertex count in the thousands. A Raven shader carrying both stages would emit the vertex
module four times over, and four copies of one stage is four chances for three of them to be wrong. So
`UiVertex` is a shader with only a vertex entry point and the other four have only a fragment one.

**The stream declarations are the contract between them, and their order is the whole of it.** A
stream's location is its index in the shader's declaration list, so `UiVertex` writing `uv, colour,
shape` and `UiBox` reading `uv, colour, shape` arrive at the same three numbers without either naming
one — exactly as the GLSL these replace agreed by both spelling `layout(location = 2)`.

A stream a fragment stage does not read is still declared there, and has to be: only a stage's *reads*
become interface variables, so declaring one costs nothing and keeps the ones after it where they are.
`UiSolid` never touches `uv`, and if it stopped declaring it, `colour` would move to location 0 and the
stage would read the vertex stage's texture coordinates as a colour.

## Where the splits came from, which is the pipeline layout

`Line` is two shaders and `Mesh` is one, and the difference is not stylistic. Raven puts a shader's
`[PushConstant]` block into **every** module it emits, and a module declaring push constants the
pipeline layout does not cover *for that stage* is refused at pipeline creation:

| | Push-constant range | So |
|---|---|---|
| `LineRenderer` | `Vertex`, 64 bytes | the fragment stage must declare none, hence two shaders |
| `MeshRenderer` | `Vertex \| Fragment`, 80 bytes | one shader, both stages, nothing to split |
| `MeshInstanceRenderer` | `Vertex \| Fragment`, 128 bytes | the same, and 128 is exactly what every Vulkan implementation guarantees — the outline's pixel measurement needs three more vectors than `Mesh` does, and one more would need a limit asked of the device |
| `UiRenderer` | `Vertex`, 16 bytes | only `UiVertex` declares them, which the split already gives |

## Bindings, and the two `UiBox` declares and never reads

The four interface pipelines share one descriptor set layout — `UiRenderer`'s own remarks say why: a
set one pipeline has and another does not is a set a pipeline change disturbs. So set 0 is a sampled
texture at 0, a sampler at 1 and a storage buffer at 2, for every one of them.

A binding's index is its position among the shader's declarations, so `UiBox` declares `atlas` and
`atlasSampler` purely to hold 0 and 1. Without them the shape buffer would be binding 0 — which is the
atlas — and every box would read a texture as a storage buffer. Declaring a binding a shader ignores
costs nothing; the host writes all three either way, and `UiRenderer.Write` says the same thing from
the other side about the image set.

## What changed from the GLSL

Two things, and neither is a translation artefact:

- **`ui-box.frag` was a version behind.** `UiShape` carries a shadow blur in `axis.z` and the editor's
  copy of the shader never read it, so the editor could not draw a box shadow while the golden-image
  fixture's copy of the same file could. `UiBox` is the current behaviour.
- **The `flat int` varying is gone.** It carried `int(shape.x + 0.5)` from the vertex stage, and Raven
  has no interpolation control (docs/plan/07 § Streams), so the fragment stage computes it from
  `shape.x` instead. The precision is identical — interpolating a value equal at all three corners is
  exact — and the qualifier was insurance rather than a covered claim in the GLSL too.
