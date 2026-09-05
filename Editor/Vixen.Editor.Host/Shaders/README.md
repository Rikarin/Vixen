# Shaders

The editor's own shaders, in Raven. Three sources, six modules: a line pair and two mesh pairs.

| Source | Shaders | Modules |
|---|---|---|
| `Line.rvn` | `LineVertex`, `LineFragment` | one of each |
| `Mesh.rvn` | `Mesh` | one of each |
| `MeshInstanced.rvn` | `MeshInstanced` | one of each |

⚠ **This table said four sources and eleven modules until 2026-09-04, and both halves were wrong.**
The fourth was a `Ui.rvn` deleted for the reason [below](#regenerating) — a divergent copy of the
host's — and the eleven counts the whole directory, which holds five more `.spv` this directory does
not have a source for: `Terrain`, `Grass` and the three `GrassScatter` permutations are compiled out
of `Raven/Library/Terrain` by `CheckShaders`' `EditorShaders` and land here because this is where
`EditorEffects` loads them from.

## Why there are two mesh shaders

`Mesh` takes triangles that are already in world space and `MeshInstanced` takes a shape and a transform
per entity, and the split is the same one `MeshRenderer` and `MeshInstanceRenderer` are: geometry that
is genuinely rebuilt every frame against geometry that is not. The gizmo's solid handles are the first —
a handle is a different size and colour at every camera position, and there is one of it — and the
scene's shapes are the second, which `docs/plan/24-blockout-tools.md` § B1 is the argument for.

⚠ **`MeshInstanced`'s thirteen vertex attributes are two buffers rather than one**, and the second one
steps per *instance*: the shape's position and normal come from the geometry buffer, and the four
transform rows, three normal-matrix rows, colour, style, material and emission come from the frame's
instance ring. The step mode is the host's to set — Raven has no opinion about it — so
`MeshInstanceRenderer` is where a mistake there would live, and what it would draw is the whole scene
as one exploded object.

## The material is two attributes and not a uniform block

`MeshInstanced` shades with a metal-roughness BRDF, and where the metalness, the roughness and the
emission arrive from is the one design decision in it. They are **per instance**, beside the transform,
because a material is per entity — so two entities that share a shape and are made of different things
stay one draw. A uniform block would be one draw per material and a descriptor set would be one *set*
per material, which is the compositor's arrangement and needs a compositor. Thirty-two more bytes an
entity per frame buys a block-out that reads as brick and metal rather than as grey and grey.

⚠ **The BRDF is written out here rather than imported from `Raven/Library/Shading/Brdf.rvn`.** Raven
resolves an `import` against declarations in the same *compilation*, and the command line below takes
one input file — so referencing the library would mean emitting and committing a `.rvnlib` for every
package in the chain, which is a build step the editor's shaders do not have. What is duplicated is
four functions of three lines each. What is deliberately *not* duplicated is everything the library
does beyond them: no anisotropy, no clear coat, no image-based lighting, no energy compensation. The
two are not two implementations of one thing that could drift apart — one is a viewport's
approximation of the other, and `MaterialSurface` says on the C# side exactly which parts of a material
survive the trip.

## The block-out checker is two lanes, not a texture

`MeshInstanced`'s `surface` attribute reserved two lanes when it was written, and doc 24's P5 is what
they turned out to be for: the size of a block-out checker square in metres, and how strongly to tint
it by which axis a face points along. Zero in the first is no checker, which is what every instance
carrying a material has — the checker is what an *undressed* surface is drawn with.

⚠ **World space, and that is P5's decision rather than an implementation detail.** A block-out box
scaled 8×3 must not stretch its texels, and what makes proportion readable at a glance is a square
that is the same number of metres everywhere in the level. So it is a function of the fragment's world
position and world normal and of one number, and nothing about the object reaches it — which also
means it needs no UV layout on geometry that exists to be thrown away, and no descriptor set per
material.

⚠ **Filtered by the screen-space derivative, which is what "legible at grazing angles" costs.** A
checker sampled per pixel with no filtering becomes a shimmering moiré the moment a cell is smaller
than a pixel, which on a floor is most of the floor. Fading the contrast out as the cell shrinks below
about two pixels is what a mip chain does for a texture — four instructions, and no texture.

## Regenerating

The gate does it: `./build.sh CheckShaders --update-shaders` recompiles all three sources here — plus
the library modules the editor loads — and rewrites what differs. Read the diff.

To do one by hand, from the repository root:

```bash
dotnet run --project Raven/Vixen.Raven.Cli -- compile --target spirv Editor/Vixen.Editor.Host/Shaders/Line.rvn Editor/Vixen.Editor.Host/Shaders/ --emit-reflection
```

…and the same for `Mesh.rvn` and `MeshInstanced.rvn`. The `.spv` and the `.reflect.json`
beside each source are committed, for the reason `Samples/01` and `Samples/02` give: `UiRenderer`'s
modules are *supplied* rather than compiled, so a caller hands over what it has.

⚠ **The path above used to say `Vixen.Editor.App`, which has not been where these live since doc 36
§ P3 split the executable out.** It was a command that could not run as written, in the one file
somebody reaches for when they need to run it.

⚠ **`--emit-reflection` is not optional.** The `.reflect.json` is what `Vixen.Shaders.Generators`
reads, and what it tells `EditorHost` is where each vertex attribute lives. Regenerating a module
without it leaves the host binding against the previous source's numbering — which is not a
validation error but a stage reading whatever the driver left in an attribute nothing was bound to.

⚠ **There used to be a fourth source here, `Ui.rvn`, and deleting it is what fixed the editor's
missing compositing stages.** It was a copy of `Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn` carrying
five of that file's eight shaders — every line of the five identical, and `UiBlur`, `UiColour` and
`UiMask` simply absent — so the editor composited groups and never blurred, filtered or masked them.
`EditorHost` calls `UiShaderLibrary.Load` now, and the interface's shaders live in exactly one place.
`UiShapeLayoutTests` reads the reflection beside that one.

⚠ **These were committed and unchecked until `UiShape` grew.** `CheckShaders` covered the
library modules and described itself as covering these; it did not, so a `.rvn` edited without
recompiling and a stale `.spv` could sit in one commit. `Build.Shaders.cs`'s
`DiscoverEditorSources` is that half now, and unlike the library entries it compares *every* module a
source emits — so a shader added to one of these files and never committed fails too.

⚠ **Nothing lists these files, which is the point.** That half of the target was four hand-written
tuples, and a source nobody added to them was a source somebody could edit without recompiling —
exactly the state the target exists to make impossible. It reads the directory now: a `.rvn` here
with a `.spv` for one of the shaders it declares beside it is compiled and diffed by the next run.
Two properties are read out of the file rather than assumed, and both refuse by name rather than
skipping — a source with an `import` cannot be compiled alone and belongs in `EditorShaders` with its
closure, and a directory this walk stops reaching fails the floor in `EditorSourceFloor`, because a
walk that finds nothing would otherwise compile nothing and print success.

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
