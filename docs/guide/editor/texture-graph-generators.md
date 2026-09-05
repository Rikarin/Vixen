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

## The design decision: a graph does not name a mesh

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

## Asking for a map

```csharp
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

## Resolving one

A compilation hands back what it could not fill itself, and a mesh-map reference is one of those:

```csharp
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

## The compound library

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
| `Generators/Dirt` | Curvature's cavities multiplied by occlusion's enclosure |
| `Generators/Curvature Edge Wear` | Curvature's convex half, broken up by a noise |
| `Generators/Grunge Rough Dirty` | A noise slope-blurred against itself, darkened by occlusion |

Four, against the two dozen doc 48 marks for M10 and the several hundred the references ship. That gap
is real and named in doc 48 § A.9; it is content authoring rather than engineering.

⚠ **`Histogram Scan`'s knobs are a black and a white point, not the reference's position and
contrast**, and the reason is worth knowing before authoring the next compound: a graph cannot do
arithmetic on a scalar port. `position ± contrast/2` would have to be an *expression*, expressions
bind against a graph's `TextureGraphParameter`s, and a parameter's override does not survive inlining
— see below. So the two numbers a compound exposes are the two numbers a node underneath it takes,
until [#742](https://github.com/Rikarin/Vixen/issues/742).

### Why a compound's knobs are ports and not parameters

⚠ A published graph can declare `TextureGraphParameter`s **or** put scalar ports on its interface, and
only the second works today. `SubGraphs.Flatten` replaces the sub-graph node with the graph's contents,
and the node — which is where a parameter override is stored — is then gone, so an expression inside a
published graph folds against that graph's own declared default and turning the knob changes nothing
until [#742](https://github.com/Rikarin/Vixen/issues/742). A port survives inlining because it is an
edge. So every knob on a shipped compound is an interface port.

## What this does not do yet

- ⚠ **No host in this tree calls `TextureCompoundLibrary.Publish` or `MeshMapBinding.TryResolve`.**
  `TextureNodeLibrary.Create` registers the generated node types and nothing else, so the shipped
  compounds are in the assembly, loadable and compilable, and **not in the panel's search**; and a
  graph containing a `Source/Mesh Map` compiles and does not bake, exactly as one containing a
  `Source/Bitmap` does. [#799](https://github.com/Rikarin/Vixen/issues/799) carries the compound
  half; [#702](https://github.com/Rikarin/Vixen/issues/702) and
  [#573](https://github.com/Rikarin/Vixen/issues/573) the resolver half.
- ⚠ **Two `Source/Mesh Map` nodes asking for one usage ask for it twice.** Each allocates its own
  external image, so a host uploads one PNG twice. The pictures are identical, so no bake is wrong;
  de-duplicating means the compiler keying externals by their reference —
  [#800](https://github.com/Rikarin/Vixen/issues/800).

## See also

- [Mesh maps as project assets](editor/mesh-map-assets) · what a bake writes, and the sidecar it
  leaves beside each file
- [Evaluating a texture graph](editor/texture-graph-evaluation) · the plan a compilation produces
- [The texture graph plugin](editor/texture-graph-plugin) · the document and the panel
