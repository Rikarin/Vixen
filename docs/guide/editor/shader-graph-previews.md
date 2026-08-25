---
title: Shader-graph preview thumbnails
slug: editor/shader-graph-previews
kind: guide
area: Editor
summary: Compiling one node's sub-expression on its own, running it over a quad, and keeping the target alive across edits.
api: [T:Vixen.Editor.ShaderGraph.ShaderGraphPreview, T:Vixen.Editor.ShaderGraph.ShaderGraphPreviewRenderer, T:Vixen.Editor.ShaderGraph.IPreviewImages]
tags: [editor, shader-graph, node-graph, preview, raven]
since: 0.1
status: preview
related: [editor/node-port-editing, editor/vfx-graph]
---

## What it is

Two halves of one feature, and they are separate on purpose.

`ShaderGraphPreview` turns **one node's sub-expression into a whole shader**. It copies that node and
everything upstream of it into a graph of its own, hangs a `Master/Unlit` on the node's output, and
hands the result to the ordinary `ShaderGraphCompiler`. What comes back is `ShaderGraphSource` — the
same record a whole graph compiles to, type-checked by the same Raven front end. It needs no device
and no editor.

`ShaderGraphPreviewRenderer` takes that text, compiles it to SPIR-V, builds a pipeline, and draws the
quad into a 64×64 target it owns. It is an `INodePreviewSource`, which is what `NodeGraphView` asks
for the picture under each node that declared `[Node(Preview = true)]`.

`IPreviewImages` is the seam between them and the interface: a target has to be given a number before
`DrawContext.DrawImage` can carry it, and that number comes from `UiRenderer.RegisterImage`, which
belongs to a window. The shader graph does not have one and should not acquire one.

## What it is for

Answering "what does this node actually produce" without reading the generated Raven. A swatch can
say that a constant is red; only a rendered quad can show what a tiling node does to a coordinate or
what a `Lerp` looks like across the surface.

It is deliberately **not** a material preview. There is no sphere, no light, no camera and no
exposure: the closure ends at `Master/Unlit`, the target is `Rgba8UNorm` rather than its sRGB form,
and the fragment's value is written straight out. A preview shaded like the scene would answer a
different question — and in a cd/m² frame an authored 0–1 value and a pass that never ran are the
same picture.

It is also not a way to preview a node that needs a resource. The renderer binds exactly one uniform
block, holding the two transforms every graph declares; a node whose expression wants a texture or a
sampler — `Texture/Sample 2D` — is refused and counted in `Refusals`, because binding a *material's*
textures means knowing which material, which is the material compiler's job.

## Using it

Compiling a sub-expression needs nothing but the graph, the node and the library:

```csharp no-compile="a fragment against a graph the editor already has open"
var result = ShaderGraphPreview.Compile(document.Graph, node.Id, document.Registry);

if (result.Artefact is { } shader) {
    Console.WriteLine(shader.Source);
}
```

Rendering one is the host's, because it owns the device and the interface renderer. `EditorHost`
builds the renderer at the moment both exist, hands it to the application, and pumps it inside the
frame:

```csharp no-compile="the host's own wiring, quoted — the device and the renderer are its fields"
previews = new ShaderGraphPreviewRenderer(device, ShaderNodeLibrary.Create(), new UiPreviewImages(renderer));
editor.ShaderGraphPreviews = previews;

// …and once per frame, after BeginFrame and before anything records:
previews.Update();
```

⚠ **`TryGet` never compiles and never draws.** It is called from the canvas's draw, once per visible
node, which is no place to record commands on a device. It emits, compares, and answers with whatever
picture already exists; `Update` does the work, and rations it to `RebuildsPerUpdate` rebuilds per
frame. A node whose expression has just changed keeps showing the *old* picture until the rebuild
lands, which is better than blinking empty on every edit.

⚠ **The canvas's preview source is the `ShaderGraphDocument`, not the renderer.** The document
forwards to its `PreviewSource`, which the host sets when a device appears. `ShaderGraphView.Show`
runs when a tab opens — which for a restored session is before the first frame, and therefore before
there is a device — so a view that had been handed the renderer directly would show flat swatches for
the rest of the session.

## Examples

**What is invalidated, and what is not.** Three gates, in the order they are cheapest:

| The author did | Emitted again? | Compiled again? |
|---|---|---|
| nothing — the canvas simply redrew | no | no |
| dragged a node, renamed a property, selected something | yes | no |
| typed a number into a port this node depends on | yes | yes |
| typed a number into a port it does not depend on | yes | no |

The first gate is the graph's own revision: every `NodeGraphCommand` calls `NodeGraphModel.Touch`, so
a frame in which nothing happened emits nothing. The second is a comparison of the emitted text
against the text the cached preview was built from — which is why the closure keeps each node's
identity, since `NodeGraphCompiler` names variables after it and a renumbering closure would emit
different text for the same expression.

**What owns the target.** The renderer, one per node, for as long as the node is among the
`Capacity` most recently asked about. A rebuild draws into the texture that is already there, so the
number the interface holds stays valid and the picture changes underneath it. Eviction and `Dispose`
give the number up *first* and destroy the texture second — a registered view whose texture has been
freed is undefined behaviour rather than an error — and `Dispose` idles the device before any of it.
`Created`, `Destroyed` and `Live` are public so that the claim can be measured; `ShaderGraphPreviewDeviceTests`
asserts they balance.

**A master previews as itself.** A master node has no output port, so the closure ends there and what
is compiled is the shader the graph emits — lighting, uniforms and all. That is the one case where a
preview is not unlit, and it is not a special case in the code.

## See also

- [Editing a node's ports](node-port-editing.md) — the inspector the preview hangs under.
- [The VFX graph](vfx-graph.md) — the other node library over the same framework, whose live preview
  is a different problem: a spawner has no value to show.
