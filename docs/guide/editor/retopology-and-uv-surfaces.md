---
title: Retopology and UV surfaces
slug: editor/retopology-and-uv-surfaces
kind: guide
area: Editor
summary: Where a quad remesh and an unwrap are actually invoked from — the model importer, three command-line verbs, and the blockout mode's own verb and UV panel.
api: [T:Vixen.Editor.Assets.Models.ModelRetopology, T:Vixen.Editor.Assets.Models.ModelRetopology.MeshResult, T:Vixen.Editor.Assets.Models.ModelGeometry, T:Vixen.Editor.Assets.Models.ModelWriter, T:Vixen.Editor.Assets.Models.PolygonMesh, T:Vixen.Editor.Assets.Models.SymmetryAxis, T:Vixen.Editor.Assets.Models.UnwrapMode, T:Vixen.Editor.Assets.Models.RetopologyGuideReference, T:Vixen.Cli.GeometryRunner, T:Vixen.Editor.Blockout.BlockoutRetopology, T:Vixen.Editor.Blockout.BlockoutUvPanel, T:Vixen.Editor.Blockout.UvIslandView, T:Vixen.Editor.Blockout.BlockoutRetopologySettings, T:Vixen.Editor.Blockout.BlockoutChartSettings, T:Vixen.Editor.Blockout.BlockoutPackSettings, T:Vixen.Editor.Blockout.BlockoutUvView, T:Vixen.Editor.Blockout.BlockoutRemeshDebug, T:Vixen.Editor.Blockout.BlockoutTheme]
tags: [editor, importer, cli, blockout, retopology, uv, atlas]
since: 0.1
status: preview
related: [engine/retopology, engine/quad-remeshing, engine/uv-charting, engine/uv-flattening, engine/uv-packing, editor/booleans, editor/mesh-map-assets]
---

## What it is

The remesher and the unwrapper are arithmetic over triangles that neither knows where it is being
called from. This is the set of places that call them: `ModelImportSettings` in the model importer,
`GeometryRunner` behind `vixen remesh`, `vixen unwrap` and `vixen uv pack`, and
`BlockoutRetopology` and `BlockoutUvPanel` in the block-out mode.

`ModelRetopology` is the piece all three share — retopologise this mesh, unwrap it, say what happened —
so the decision of what to do to a mesh is made once. `ModelGeometry` is the copy between the
renderer's `MeshData` and the kernel's `EditMesh`; `ModelWriter` is `ModelReader`'s other half.

`PolygonMesh` is what a result travels to a file in, and it exists because `MeshData` cannot carry a
quad. That type is a vertex buffer — one vertex per corner, three corners per triangle — so a
retopology written through it arrives triangulated *and* exploded, with every quad an island of four
vertices joined to nothing. Measured on a 5 766-quad result before this existed: 11 532 triangles over
23 064 positions, and the only edges with two faces were the diagonals inside each split quad.

So `ModelWriter.Write` takes `PolygonMesh` — an `EditMesh` plus a name plus one texture coordinate per
corner — and OBJ writes `f a b c d` with the positions shared. The coordinates stay per corner, which
is what keeps a seam a seam: welding them onto positions would close every cut in the atlas.

⚠ **glTF and GLB are triangles-only by specification, so `.glb` genuinely cannot hold a quad.** Both
write a triangulated copy and say so, naming the format. Write `.obj` to keep the quads.

## What it is for

Getting a generated mesh into a project without a manual step. A four-million-triangle blob out of an
image-to-3D model is not expensive because it has four million triangles; it is expensive because it
has four million triangles *of noise* with no texture coordinates. Five thousand quads plus a 2K
atlas is smaller, subdivides, and can be rigged.

It is also for the hard-surface case, which is the block-out one: a boolean result has a triangulation
with no loops to cut and no rings to select, and retopology gives the fourteen verbs above it
something to work on again.

## Using it

### The importer

Retopology and unwrapping are flat settings on `ModelImportSettings` with a `To…Settings()` mapper
apiece, exactly as `GenerateDistanceFields` and `GenerateMeshlets` are. Both are **off by default** and
stay off: an artist's topology is a decision, and an importer that silently replaced it would be one
nobody could use on a hand-modelled asset.

```yaml
!ModelImporter
retopologize: true
retopologyQuads: 5000
retopologyAdaptivity: 0.7
retopologySymmetry: X
unwrap: WhenMissing
unwrapResolution: 2048
unwrapMargin: 4
```

| Setting | What it decides |
|---|---|
| `Retopologize`, `RetopologyQuads`, `RetopologyAdaptivity` | Whether to remesh, to what budget, and how much curvature moves the density |
| `RetopologyFeatureAngle`, `RetopologyKeepUvSeams` | What counts as a hard edge, and whether an existing atlas's cuts survive |
| `RetopologySymmetry` | `None`, `X`, `Y` or `Z` — a plane through the origin, see below |
| `RetopologyGuides` | `.vxspline` paths whose curves the edge flow follows |
| `Unwrap` | `Never`, `WhenMissing` or `Always` |
| `UnwrapResolution`, `UnwrapMargin`, `UnwrapTexelDensity` | The atlas, in whose texels the margin is counted |

⚠ **The retopology runs before the cluster hierarchy and the distance field, and the ordering is not
cosmetic.** Both are built from the mesh's triangles; building them from the source and then replacing
the source is the most expensive no-op the pipeline could perform.

⚠ **The setting reaches the content hash by being written into the `.meta` at all.** `ImportPipeline`
hashes the serialized `importer` mapping, skipping only `version` and `sourceHash` — so there is
nothing extra to register, and the thing that can silently break it is the *serializer* dropping a
property rather than the hash. A type in the settings needs `[DataContract]` or it never reaches the
meta and is neither hashed nor read.

### Guides are an asset

A guide curve is a `.vxspline` named by path, not a polyline pasted into the `.meta`:

```yaml
retopologyGuides:
  - spline: Curves/spine.vxspline
    strength: 1.0
```

⚠ **A painted guide dies with the mesh it was painted on.** The AI pipeline's whole shape is
"regenerate the source and import it again", which throws away anything that lived on the previous
mesh's vertices. A curve saved beside the mesh survives that, can be shared between the meshes it
applies to, and is declared as a file dependency — so editing the curve re-imports the models that
follow it instead of leaving them on a cached artefact.

⚠ **A guide is dropped in silence when it lies on no edge of the conditioned surface.** The feature
detector claims an edge whose midpoint is within one percent of the bounding-box diagonal of the
polyline, while the pre-remesh rebuilds the surface at `√(area / quads)` — which on a coarse target is
several times larger. Sample the curve densely (`ModelRetopology.ToGuide` takes 128 points by default,
spaced by *distance* rather than by parameter) and expect a guide to bite on a dense target rather
than a coarse one.

### Symmetry is an axis, and that is why it is exact

`RetopologySymmetry` takes an axis rather than an arbitrary plane. On an axis plane through the origin
the snap onto the plane is a store of `0f` and the mirror is a sign-bit flip, both exact for every
float — so output vertex *k* and its mirror are exact negations and every vertex on the plane has an
exactly zero coordinate. A character remeshed this way can be rigged once and mirrored.

⚠ **The seam is one vertex shared by both halves, not two welded by tolerance.** A tolerance weld
leaves a mesh that looks right, renders right, and splits open the first time it is subdivided.

An arbitrary plane is still accepted by `RemeshSettings.Symmetry` itself; what it gives back is a
rounded reflection, and the report says so rather than letting a caller believe otherwise.

### The command line

```console
vixen remesh in.glb out.glb --quads 5000 --adaptivity 0.7 --symmetry x
vixen unwrap in.glb out.glb --resolution 2048 --margin 4 --density 512
vixen uv pack in.glb out.glb --resolution 2048 --margin 8
```

None of the three opens a project: a file is the unit, because the case they exist for is a directory
of generated files that are not assets yet. Input goes through Assimp; output is chosen by the
extension and is one of `.obj`, `.gltf` or `.glb`.

`vixen uv pack` is the third stage alone. It reads the coordinates the file already carries, groups
them into islands, and repacks them — **seams untouched, island shapes untouched**, because a
placement is a transform rather than rewritten coordinates and a quarter turn is a subtraction and a
swap with no resampling in it. That is the standalone-packer case: unwrap in another package, repack
here.

| Failure | Exit code |
|---|---|
| No such input, an output format nothing writes, an axis that is not an axis, a missing guide | `UsageError` (2) |
| A file Assimp will not read, a mesh a stage refused, a partial result | `Failed` (1) |

⚠ **A partial result is a failure.** A build script that saw a zero exit code and an output file with
three of its five meshes in it would ship the three.

⚠ **There is no `--bake`.** The normal and displacement bake is a later phase and nothing performs it
yet; a flag that parsed and wrote no maps would be discovered by a build script as a success, which is
worse than a parse error it can act on.

### The block-out verb

`BlockoutRetopology.Run` is the `Retopologize` verb: it remeshes every selected solid, records **one
undo entry that holds the whole previous mesh**, and selects the results.

```csharp no-compile="needs a scene document"
BlockoutRetopology.Run(document, new RemeshSettings { TargetQuads = 2000 });
```

⚠ **The undo entry is the mesh as it was, not a description of what happened**, because a topology
change has no inverse — the same rule the booleans follow. A derived result or a parametric shape is
collapsed first, or the next refresh would quietly put the triangle soup back.

⚠ **A refusal leaves the mesh alone and pushes nothing.** An undo step that undoes nothing is worse
than a verb that visibly did not fire.

### The settings the block-out mode's verbs read

`BlockoutMode` holds three settings objects — `Retopology`, `Charting` and `Packing` — and the
**Blockout** panel draws all three. The panel opens with the mode and closes with it, because
`BlockoutMode.Panel` names it.

```csharp no-compile="needs an editor shell"
mode.Retopology.TargetQuads = 4000;
mode.Packing.Resolution = 2048;

BlockoutRetopology.Run(document, mode.Retopology.ToRemeshSettings());
```

⚠ **These are classes beside the records rather than the records themselves, and there are three
separate reasons — any one of them decisive.** `RemeshSettings`, `UvSettings` and `PackSettings` are
`init`-only, so the inspector's generator cannot emit a setter for them; they live in `Core/`, which
must not reference an editor assembly — `ReflectedDescriptor`'s own remarks put it as *"no runtime
type carries `[Inspector]`, and none should"*; and a panel needs an object that survives being
edited, which a record replaced wholesale by every `with` expression is not. The model importer
reached the same conclusion first, in `ModelImportEdits`.

⚠ **Not every member of each record is drawn, and the absences are named.** Guide curves, a density
mask and a symmetry plane are per-run data or a gizmo's business rather than numbers somebody types;
`UvSettings.Decomposition` is a plug point, not a dial. Each `To…Settings()` leaves those at the
record's own defaults, so a caller that has one passes it *beside* the panel's values.

### The debug overlays

`BlockoutRemeshDebug` is `RemeshDump` on screen: doc 41 § D1's per-stage artefacts drawn over the
solid they were captured from. Before it, `RemeshDump` had a full test suite and no consumer under
`Editor/` at all — and § D1's own argument for making each stage an artefact is that a remesher is
judged by a picture.

| Switch | Stage | What it draws |
|---|---|---|
| `ShowConditioned` | ① | the conditioned triangles, as a wireframe |
| `ShowFeatures` | ② | every feature polyline: the edges the remesh must reproduce |
| `ShowField` | ③ | the 4-RoSy field, a cross per vertex — a cross and not an arrow, because the field has no sign |
| `ShowSingularities` | ③ | where the field's singularities landed |
| `ShowPatches` | ④ | the patch partition, one colour per patch; grey is a triangle nothing claimed |
| `ShowQuantization` | ⑤ | each arc, red where the integer it got is far from what the density field asked |

`Capture` runs the stages for the first selected solid and remembers *which* entity it is of, because
every artefact is indexed against that solid's conditioned mesh — drawn over anything else it is a
field and a patch partition laid over geometry they say nothing about. `BlockoutMode` draws it through
`SceneViewport.Cursor`, on the pane the pointer is in, and the `Retopology Debug Overlays` command is
a toggle: it captures when there is nothing and clears when there is.

⚠ **It re-runs the stages rather than capturing them from a remesh in flight, and that costs what a
remesh costs.** § D14's determinism is what makes it legitimate — the same input and settings give the
same answer, so the artefacts captured are the artefacts the remesh had. A hook threaded through
`Remesher` would put a debugging concern in the middle of a pipeline every caller pays for.

⚠ **The colours are chosen here and not in `Core/`**, which is what `RemeshRegion` asks for in as many
words: "a patch index rather than a colour … the index is the artefact; colouring it is one modulus
away". Eight hues of one lightness, wrapped — a layout has hundreds of patches and no palette
distinguishes hundreds of colours, and what the drawing is asked is "where does this patch end".

⚠ **What is still owed is § R7's live preview at the approximate quantization**, and the blocker is
not the editor: `QuantizeMode` and `Quantizer` are **internal** to `Vixen.Geometry.Remeshing`, so
nothing outside that assembly can ask for the cheap answer. That is an API change before it is a
panel.

### The UV panel

`BlockoutUvPanel` holds what a UV panel shows and runs the three stages separately. `Chart()`,
`Flatten()` and `Pack()` each run what they need first, so `Pack()` alone works on a fresh mesh — and
`Pack()` on a panel that already has islands repacks *those*, which is the same property the CLI verb
exposes.

| Member | What it is for |
|---|---|
| `Islands`, `Views` | The islands, and one `UvIslandView` apiece: where it landed and how stretched it is |
| `Seams()` | Every edge whose two faces went into different charts, plus every open rim |
| `Heat(view)` | The heat map's ramp position, anchored at "no distortion" rather than at this atlas's best |
| `Report`, `Messages` | The last stage's numbers, and a sentence about them |
| `Changed` | Raised whenever any of the above moved |

⚠ **`UvIslandView.IsBad` is or-ed rather than averaged.** A flipped triangle is a correctness failure
wearing a metric's clothes; an island with one is bad however low its stretch is, and a single blended
score would hide exactly that case.

⚠ **This is not a drag-a-vertex-in-UV-space tool.** Every verb replaces the whole layout and nothing
moves one coordinate. Editing an island by hand needs an undo model, a selection model and a snapping
model, and is a different surface.

### What draws it

`BlockoutUvView` is the panel itself — the atlas square, one rectangle per island, the three verbs as
buttons and whatever the last stage said. It is registered as `blockout.uv`, so it comes with its own
toggle command and its own View-menu line rather than opening with the mode. `BlockoutTheme` carries
the rules that colour it.

Set `Model` to a `BlockoutUvPanel` and the view follows it: it subscribes to `Changed`, so a verb run
from anywhere redraws the atlas, and it unsubscribes when it is removed. `Source` says where the mesh
comes from and is read at the moment a verb runs; `Configure` does the same for the dials, so the
inspector's resolution and margin are the ones a click uses rather than the ones that were current
when the tab was opened.

⚠ **The heat map is two channels rather than one ramp, and that is `IsBad`'s disjunction on screen.**
An island with a flipped triangle takes `uv-flipped` and never a ramp bucket, however low its stretch
is — folding the two into a single score is the simplification the model was written to refuse, and
it would let a badly wound island wearing a distortion of 1.0 draw as the best island in the atlas.

⚠ **v is flipped on the way to the square.** UV space puts `v = 0` at the bottom and the layout puts
`y = 0` at the top, so an atlas drawn without the flip is vertically mirrored — and it looks entirely
plausible, because a packed atlas is roughly symmetric and every count-shaped test stays green.

## Examples

Turning one generated file into an atlased quad mesh, which is the whole of the AI pipeline's last
step:

```console
vixen remesh generated.glb clean.glb --quads 5000 --adaptivity 0.7
vixen unwrap clean.glb clean-uv.glb --resolution 2048 --margin 4
```

The same thing inside the import, so that dropping the file into the project is the only step:

```yaml
!ModelImporter
retopologize: true
retopologyQuads: 5000
unwrap: WhenMissing
unwrapResolution: 2048
```

A character, where symmetry is the setting that matters and the axis is what makes it exact:

```yaml
!ModelImporter
retopologize: true
retopologyQuads: 8000
retopologySymmetry: X
retopologyGuides:
  - spline: Curves/spine.vxspline
```

Repacking a layout somebody else cut, which leaves the seams and the island shapes exactly as they
arrived and only moves them:

```console
vixen uv pack from-blender.glb repacked.glb --resolution 4096 --margin 8
```

Driving the three stages from a panel, where each one is separately re-runnable:

```csharp no-compile="needs a mesh to look at"
var panel = new BlockoutUvPanel { Mesh = mesh, Packing = new() { Resolution = 2048, Margin = 4 } };

panel.Pack();                                  // charts and flattens first, then places

panel.Packing = panel.Packing with { Margin = 16 };
panel.Pack();                                  // repacks the same islands, seams untouched

foreach (var view in panel.Views.Where(island => island.IsBad)) {
    Highlight(view, BlockoutUvPanel.Heat(view));
}
```

## See also

- [Retopology settings and reports](engine/retopology) — what the settings mean and what the report measures
- [UV packing](engine/uv-packing) — the packer these verbs call, and what its report says
- [Booleans and handoff](editor/booleans) — the block-out verb whose output this one exists to clean up
