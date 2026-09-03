---
title: A material that draws with a shader graph
slug: editor/shader-graph-materials
kind: guide
area: Editor
summary: A graph emits a material feature rather than a whole shader, which is why the engine's existing draw can put one on a mesh.
api: [T:Vixen.Editor.ShaderGraph.ShaderGraphKind, T:Vixen.Editor.ShaderGraph.ShaderGraphMap, T:Vixen.Editor.ShaderGraph.ShaderGraphMaterial, T:Vixen.Editor.ShaderGraph.Nodes.SurfaceMasterNode, T:Vixen.Editor.Assets.Shading.ShaderGraphImporter, T:Vixen.Editor.Assets.Shading.ShaderGraphImportSettings, T:Vixen.Editor.Assets.Shading.ShaderGraphSources, T:Vixen.Editor.Assets.Shading.ShaderGraphSourceFile]
tags: [editor, shader-graph, materials, raven, assets]
since: 0.1
status: preview
related: [editor/shader-graph-previews, editor/graph-diagnostics, rendering/mesh-and-material]
---

## What it is

A shader graph can be two different things, and only one of them can be drawn.

`ShaderGraphKind.Standalone` is a whole shader — its own `worldViewProjection`, its own vertex stage,
a `float4` returned from a fragment entry point. `Master/Unlit`, `Master/Sprite` and `Master/PBR`
make one. It is readable, it is what a preview thumbnail draws, and an author can hand it to
`raven compile`.

`ShaderGraphKind.Surface` is a **material feature**: `shader N : IMaterialSurface`, with no stages, no
entry point and no `return`. `SurfaceMasterNode` — `Master/Surface` on the menu — makes one.

The master decides which, and that is the only structural decision a node makes.

## What it is for

⚠ **A standalone shader cannot be put on a mesh, and the reason is not that something is missing.** A
draw in this engine binds a transform record, a light cluster, a shadow atlas and a bindless texture
table, under names that come from the pass the material is composed into. A shader that declares its
own `worldViewProjection` is not a smaller version of that; it is a different program, and drawing
with one would mean a second render feature, a second pass and a second way of being lit.

An `IMaterialSurface` is exactly what an `IMaterialFeature` names. So a graph-authored material is a
material with one more feature in its list — `GraphSurfaceFeature` — composed into `CompositeSurface`
beside `MetalRoughnessSurface` and the rest. Everything after that is the path every hand-authored
material already takes.

⚠ **A feature is composed into a pass it has never seen**, so it may only read `MaterialData`. `uv`
resolves to `d.uv` and a world normal to `d.tangentFrame.normal`. A **world position** and a **vertex
colour** are not on that struct at all, and a graph that reads one is refused with `SG0004` rather
than given the origin or white — both of which compile, draw, and produce a surface lit as though the
graph said something it did not.

## Using it

Add a `Master/Surface` node, wire the channels, and save. Then name the graph as a feature on a
`.vxmat`.

`ShaderGraphSources` is what turns the file into Raven: it compiles every `.vxshadergraph` under
`Assets/` and hands the text to whichever compilation wants it — `EditorEffects` in the editor,
`ShaderBuildRunner` in `vixen build`. ⚠ Nothing is written to disk. A generated `.rvn` beside its
graph would acquire a `.meta`, an address and a place in the browser, be committed by somebody, and
then be silently overwritten by the next import.

`ShaderGraphImporter` claims the extension and writes **no artefact**. What a graph produces is
source, and source is not content; the importer exists to compile the graph and report its
diagnostics beside the file that caused them, on `MaterialImporter`'s terms.

⚠ **A graph that will not compile contributes nothing rather than its text.** Unparseable source in a
shared compilation refuses *every* material in the project, so one graph an author is halfway through
would take the whole editor's shading with it. The diagnostics come back on
`ShaderGraphSourceFile.Diagnostics`; `EditorEffects.GraphRefusals` keeps them apart from the refusal
that means "there are no effects at all".

**Textures are slots, not bindings.** A standalone graph declares `albedo: Texture2D` and
`albedoSampler: Sampler` and owns them. A surface declares `albedoIndex: uint` and indexes
`MaterialTextures`'s shared table. `ShaderGraphMap` carries the `albedo` ⇄ `albedoIndex` pairing a
host feeds `MaterialRenderFeature.TextureIndices` — both halves, explicitly, because a runtime reading
a baked material never ran the generator that spells the convention.

⚠ The table is indexed directly rather than through `SampleSurface`, which samples at `d.uv`
unconditionally — so a graph with a `Tiling and Offset` node would otherwise compile, draw, and
sample somewhere the author did not ask for.

## Examples

Compile a graph and turn it into the feature a material composes:

```csharp no-compile="a fragment; the compilation is asserted before .Value in real code"
var registry = new NodeTypeRegistry();
NodeTypes.Register(registry);

var graph = new NodeGraphModel { Name = "Painted" };
var master = graph.Add("Master/Surface");

master.SetValue("BaseColour", 0.82f, 0.21f, 0.11f);
master.SetValue("Roughness", 0.45f);

var source = new ShaderGraphCompiler(registry).Compile(graph).Value;
var feature = ShaderGraphMaterial.Feature(source);

var material = MaterialCompiler.Compile(
    new() { ShaderName = "ForwardPlus", Features = [feature] }
).Material;
```

`ShaderGraphMaterial.Values(source)` is the narrower list an inspector offers a person: every property
the graph declares *except* the texture slots, which a host overwrites from the bindless table every
frame.

Give it the values a material sets, by the names the graph declares:

```csharp no-compile="a fragment continuing the one above, over the same `source`"
var feature = ShaderGraphMaterial.Feature(
    source,
    new Dictionary<string, Vector4>(StringComparer.Ordinal) {
        ["tint"] = new(0.8f, 0.2f, 0.2f, 1f),
        ["roughness"] = new(0.35f, 0f, 0f, 0f)
    }
);
```

And read a project's graphs the way both shader compilations do:

```csharp no-compile="quoted from EditorEffects.Build, whose `project` and `sources` are its own"
foreach (var compiled in ShaderGraphSources.All(project.Paths.Assets)) {
    foreach (var diagnostic in compiled.Diagnostics) {
        Report(compiled.Path, diagnostic);
    }

    if (compiled.Compiled) {
        sources.Add((compiled.Path, compiled.Text));
    }
}
```

## See also

- [Shader-graph preview thumbnails](shader-graph-previews.md) — the other consumer of the compiler,
  and the one that wants a standalone shader rather than a surface.
- [Graph diagnostics](graph-diagnostics.md) — how a complaint about generated text names the node an
  author can select.
- [Meshes and materials, type by type](../rendering/mesh-and-material.md) — where
  `GraphSurfaceFeature` sits among the features it is composed beside.
