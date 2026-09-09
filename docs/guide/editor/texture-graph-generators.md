---
title: Generators and the compound library
slug: editor/texture-graph-generators
kind: guide
area: Editor
summary: How a generator asks for a baked mesh map by what it measures rather than by which file it is, where a shipped compound lives, and why a graph must not name the mesh it is for.
api: [T:Vixen.Editor.Assets.MeshMaps.MeshMapReference, T:Vixen.Editor.Assets.MeshMaps.MeshMapBinding, T:Vixen.Editor.TextureGraph.TextureCompoundLibrary, T:Vixen.Editor.TextureGraph.TextureCompoundProblem]
tags: [editor, texture-graph, mesh-maps, generators, material-authoring, compounds]
since: 0.1
status: preview
related: [editor/mesh-map-assets, editor/texture-graph-evaluation, editor/texture-graph-plugin, editor/index]
---

## What it is

A *generator* is a texture graph that reads what a mesh-map bake measured — curvature, occlusion,
thickness — and turns it into a mask. It is the thing that makes a texturing tool feel clever: dirt
in cavities is a curvature multiplied by an occlusion, and edge wear is a curvature with a threshold
on it. Neither is code. Both are `.vxtexgraph` files.

Three pieces make that work, and they are described here because none of them is obvious from the
type it lives in:

| | |
|---|---|
| `Source/Mesh Map` | A node that asks for **a measurement**, not a file |
| `MeshMapReference` · `MeshMapBinding` | What that request looks like crossing an assembly boundary, and what turns it into a file |
| `TextureCompoundLibrary` | Where a shipped compound lives, and how a project's own sit beside it |

## What it is for

### The design decision: a graph does not name a mesh

⚠ **This is the whole of why one generator works on every mesh, and it is a decision about what a
graph is *not* allowed to contain.**

A texture graph carries a name, nodes, edges, an interface and the settings of
[the evaluation guide](editor/texture-graph-evaluation)'s § D8 — a base resolution and a seed. It
carries no mesh, and adding somewhere to put one would be the bug rather than the fix: a generator
that named a mesh is a generator that works on that mesh.

So the mesh enters at the **evaluation**, in exactly the place the bake resolution enters. A
`Source/Mesh Map` node emits an external image whose reference is `meshmap:curvature` — a scheme, not
a path — and a host that knows which texture set it is baking resolves that reference against the
project's baked maps.

The consequence worth stating out loud is that **the compiled plan is the same plan for every mesh**.
Two bakes of one generator differ only in their external table. "One compound, two meshes, no
rewiring" is therefore not a feature the node implements; it is the only thing this shape can do —
which is why the test that proves it checks the two bakes bind *different* files, rather than merely
that both compiled.

## Using it

### Asking for a map

```csharp no-compile="a fragment against a graph the caller already has"
var node = graph.Add("Source/Mesh Map");

node.SetText("Map", "curvature");
```

The `Map` setting takes one of nine names — `normal`, `height`, `ao`, `bent`, `curvature`,
`thickness`, `position`, `world`, `id` — which are the suffixes
[mesh maps as project assets](editor/mesh-map-assets) writes into each map's sidecar. A name that is
not one of them is a `TG0010` naming the node and the setting, and **not** a fallback to the first:
every mesh map looks like a mesh map, so a generator wired to the wrong measurement produces a
plausible picture nobody would question.

Two things the node decides that no setting exposes:

- **Grey or colour.** `height`, `ao`, `curvature` and `thickness` are one measurement per texel and
  come back grey; the other five are directions, positions or indices and come back colour. A bent
  normal read as grey loses two thirds of itself at the first node that touches it, and a curvature
  read as colour is a type error at any port that measures — both are silent, in opposite directions.
- ⚠ **`id` is point-sampled and everything else is interpolated.** Interpolating two material indices
  produces a third that belongs to no material, so a bilinearly resampled id map grows a hairline of a
  fourth material along every boundary. It looks like an antialiased edge.

⚠ **A quantized map arrives quantized.** Displacement and curvature are stored as `0.5 + 0.5·v/range`
with the range in the sidecar, so `curvature` is a map whose **0.5 is zero curvature** — edges above
it, creases below. The node cannot decode it: reading the sidecar means an asset database, and a
compilation runs on every edit. A `Colour/Levels` picking the half you want is what a generator does,
and every shipped one does exactly that.

### Resolving one

A compilation hands back what it could not fill itself, and a mesh-map reference is one of those:

```csharp no-compile="a fragment: project, compiler and Report are the caller's"
var library = MeshMapLibrary.Index(project.Assets);
MeshMapBinding binding = new(library, "Barrel");

foreach (var external in compiler.Externals) {
    if (binding.TryResolve(external.Asset, out var map, out var problem)) {
        // `map.Map` is the AssetReference to load and upload.
        continue;
    }

    if (problem.Length > 0) {
        // A mesh map this set was baked without, or a usage this build does not bake.
        Report(problem);
    }

    // An empty problem means "not mine" — an imported bitmap, which a host resolves as a path.
}
```

`MeshMapBinding.TryFor(library, model, out var binding, out var problem)` is the convenience for a
model with one texture set, and it **refuses** a model with several rather than picking one: every
mesh of a model has its own curvature map, so "the curvature map of this model" has as many answers as
the model has meshes.

⚠ **A `false` with an empty `problem` is not a failure.** A compilation's external list mixes imported
bitmaps with mesh maps, and a host walks it once — so a resolver that reported
`Assets/Textures/rust.png` as an unresolvable mesh map would make every graph containing a
`Source/Bitmap` look broken. `MeshMapReference.IsMeshMap` is the same question asked before the call.

## Examples

### The compound library

Doc 48 § D5's claim is that the several hundred nodes a reference tool ships are **content**, and this
is the mechanism that makes it true. `TextureCompoundLibrary.Publish` reads two roots into one menu:

- The compounds embedded in `Vixen.Editor.TextureGraph`, from its `Compounds/` folder — the same
  arrangement the kernels use, and for the same reason: there is nothing a deployment can leave
  behind.
- A project's own folder, if it names one, published beside them.

A file's path under the root is its node-type path, so `Compounds/Generators/Dirt.vxtexgraph` is the
node `Generators/Dirt`.

⚠ **A project compound whose path collides with a shipped one is refused rather than allowed to
shadow it**, and comes back as a `TextureCompoundProblem` naming both files. Overriding is what a
library grows into wanting; it is also how an author's half-finished copy of `Generators/Dirt`
silently rebinds every material that reads it.

⚠ **A shipped compound's file name may not contain a dot.** A manifest resource name has no way to
tell a folder separator from a dot somebody typed, so `Grunge v2.vxtexgraph` would publish under a
path with a phantom folder in it.

### What ships today

| Path | What it is |
|---|---|
| `Utility/Histogram Scan` | A `Colour/Levels` behind two threshold ports — § 4.5's "Histogram Scan is a compound over Levels" |
| `Utility/Histogram Range` · `Histogram Select` | The other two of § 4.5's histogram family |
| `Utility/Contrast Luminosity` · `Highpass` · `Equalize` | Tone, detail and doc 40 § D2's *Delight / Equalize* row |
| `Utility/Safe Transform` · `Make It Tile` | doc 40 § D2's first row: an offset-wrap behind an edge mask |
| `Patterns/Brick` · `Panels` · `Tile Random` · `Rivets` | Four of § 4.9's seven pattern marks, all over `Placement/Tile Sampler` |
| `Grunges/…` | The family of eight — `Clouds` · `Concrete` · `Damage` · `Fibres` · `Leaks` · `Rust` · `Scratches` · `Smears`, which is `Source/Noise` and `Filters/Slope Blur` in eight arrangements |
| `Surface/Height Blend` · `Bevel` · `Curvature Smooth` · `Height to AO` | Four of § 4.9's five surface marks; `Metal Reflectance` is refused, not missing — see below |
| `Generators/Dirt` | Curvature's cavities multiplied by occlusion's enclosure |
| `Generators/Curvature Edge Wear` · `Metal Edge Wear` | Curvature's convex half, broken up by a noise |
| `Generators/Grunge Rough Dirty` | A noise slope-blurred against itself, darkened by occlusion |
| `Generators/Dust` · `Position Gradient` | A `Source/Mesh Map` read by usage, levelled — which is the whole of what makes a generator work on a mesh it was not authored against |
| `Generators/Mask Editor` | The composite with the sliders, and § D9's parameters end to end |

**Thirty-one**, against the thirty-five doc 48 § 4.9 marks for M10 and the several hundred the
references ship. ⚠ **This page said "sixteen, against the two dozen doc 48 marks" and both halves
were wrong** — fifteen more compounds had landed, and § 4.9's own summary sentence miscounted its own
table by eleven, which is corrected there. Count them off `TextureCompoundLibrary.Shipped`, which is
derived from the manifest, rather than off any prose including this sentence.

⚠ **Nothing is owed as of 2026-09-09** — `Patterns/Scratches`, `Wood Grain`, `Cells` and
`Surface/Metal Reflectance` all shipped, and the *Delight / Equalize* row turned out to be one ● for
one compound under two names rather than two compounds with one missing
([#1110](https://github.com/Rikarin/Vixen/issues/1110)). § 4.9's marks and the folder are both 35.
⚠ Do not take a remainder off this paragraph either: `TextureCompoundLibrary.Shipped` is the derived
list and `TextureCompoundLibraryTests` is what compares it with the plan, by name.
⚠ **The measurement M10 exists to make is written up beside the content itself**, in
`Editor/Vixen.Editor.TextureGraph/Compounds/README.md`: what could *not* be authored out of the
atomic set, which is the standing test of whether M2 and M3 got that set right.

⚠ **`Histogram Scan`'s knobs are a black and a white point, not the reference's position and
contrast.** That was because a compound's knob could only be a port, and a port cannot be arithmetic;
it is no longer a constraint (see below) and the node keeps the two numbers it was authored with.

### A compound's knobs may be ports *or* parameters

⚠ **This section said until 2026-09-08 that only ports work, and
[#742](https://github.com/Rikarin/Vixen/issues/742) closed that.** `SubGraphs.Flatten` replaces the
sub-graph node with the graph's contents, and the node is where a parameter override is stored — so
before #742 an expression inside a published graph folded against that graph's own declared default
and turning the knob changed nothing. `NodeGraphInlining` now carries each expansion's settings and
the compiler reads them, so **seven of the sixteen shipped compounds declare parameters and drive
node ports through folded expressions**. ⚠ **That read "seven of the sixteen" and is now
twenty-five of the thirty-one** — every shipped compound except `Generators/Curvature Edge Wear` ·
`Dirt` · `Grunge Rough Dirty` and `Utility/Equalize` · `Highpass` · `Histogram Scan` declares at
least one, so a knob is the norm here rather than the exception,
and the sentence a reader should take from this section is that a port is what you reach for when the
knob has to be *wired*, not when it has to exist.

Which to reach for is now a real choice rather than a workaround:

* a **port** is an edge, so it can take a whole image and can be wired from another node;
* a **parameter** has a group and, for the three numeric kinds, a range — so it can be arithmetic,
  `position ± contrast/2` is expressible, and it appears in the node inspector as a knob rather than
  as a dangling input. ⚠ **This said "a parameter is a number" and there are four kinds, not
  three**: `Scalar`, `Integer`, `Boolean` and, since 2026-09-08, `Name` — which is substituted into
  a `[Setting]` on a node *inside* the published graph rather than folded into arithmetic, and is
  the one kind an expression cannot spell, because a name is not a number and a source that declared
  one would not parse.

### What this does not do yet

- ⚠ **This bullet said no host calls `TextureCompoundLibrary.Publish`, so the shipped compounds were
  "not in the panel's search" and a `Source/Mesh Map` graph "compiles and does not bake" — and every
  clause of it is false, re-measured 2026-09-08.** `TextureCompoundLibrary.Publish` has two production
  callers: `TextureNodeLibrary.Publish` (`Editor/Vixen.Editor.Texturing/TextureNodeLibrary.cs:138`),
  which both documents, the compound watch and the layer-stack compiler start from, and
  `Tools/Vixen.Cli/TextureGraphRunner.cs:105`. ⚠ **Not `TextureNodeLibrary.Create`** — the two are
  different methods and `Create` registers the generated node types and deliberately publishes no
  compounds, which is a distinction worth keeping rather than blurring.
  A mesh map resolves as a `meshmap:` external through `TextureExternalImages` and bakes on a device.
  ⚠ **A guide page telling an artist that the library they can see in the search is unreachable is a
  worse defect than the gap it described**, and it survived two batches of the fix because nothing
  re-reads a page whose subject somebody else repaired.
- ⚠ **What is still true, one name along: `MeshMapBinding` — `Vixen.Editor.Assets`' resolver — has
  no caller outside its own assembly.** The plugin resolves the scheme itself and says so
  (`TextureExternalImages.cs:49` duplicates `TextureMeshMaps.Scheme` deliberately, because the
  alternative is telling an artist that `meshmap:curvature` is a missing file), so this is two
  answers to one question rather than a missing one.
- ⚠ **Two `Source/Mesh Map` nodes asking for one usage ask for it twice.** Each allocates its own
  external image, so a host uploads one PNG twice. The pictures are identical, so no bake is wrong;
  de-duplicating means the compiler keying externals by their reference —
  [#800](https://github.com/Rikarin/Vixen/issues/800).

## See also

- [Mesh maps as project assets](editor/mesh-map-assets) · what a bake writes, and the sidecar it
  leaves beside each file
- [Evaluating a texture graph](editor/texture-graph-evaluation) · the plan a compilation produces
- [The texture graph plugin](editor/texture-graph-plugin) · the document and the panel
