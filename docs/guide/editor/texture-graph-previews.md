---
title: Texture-graph node previews
slug: editor/texture-graph-previews
kind: guide
area: Editor
summary: The swatch under every node of a texture graph — one plan that keeps every node's image, one bake, and pixels handed to the host because a compute queue's images are not the interface's to sample.
api: [T:Vixen.Editor.TextureGraph.TextureGraphPreviews, T:Vixen.Editor.TextureGraph.ITexturePreviewImages]
tags: [editor, texture-graph, node-graph, preview, material-authoring, compute]
since: 0.1
status: preview
related: [editor/texture-graph-evaluation, editor/texture-graph-compiling, editor/texture-graph-plugin, editor/shader-graph-previews]
---

## What it is

`TextureGraphPreviews` is an `INodePreviewSource` for a `.vxtexgraph`: it answers the canvas with a
picture per node, so an author reading a chain of blends and warps can see what each stage is
producing rather than only what the whole graph produces.

`ITexturePreviewImages` is the seam between it and whatever draws. A picture has to be given a number
before the interface can carry it, and that number comes from a texture registry a window owns — which
neither the evaluator nor a plugin has. The source hands over bytes and gets a number back.

## What it is for

The question "which of these forty nodes is the one that went wrong". A texture graph's failure mode is
almost never an error: it compiles, it bakes, and the result is subtly not what was wanted. Without a
swatch the only way to find the stage responsible is to rewire the output to each node in turn.

It is deliberately small — 64 texels square, whatever the graph's own resolution — because every node
of the graph keeps an image at once. That size is the reason a forty-node graph costs a few megabytes
instead of hundreds.

## How it works

Two tiers, and the split is where the cost is.

**Compiling** is a walk that appends records to two lists. It is done whenever the graph has changed,
with `PreviewEveryNode` set on the compiler — which is the whole mechanism. An ordinary plan lets the
image pool hand an intermediate's texture to the next image that needs one the moment its last reader
has run, so reading an intermediate back after the bake gives a picture of a *later* node. It does not
throw and it does not look empty. Which images the pool may reuse is the plan's decision rather than
the evaluator's, so a plan that keeps every node's image is read back node by node from an evaluator
nobody had to change.

**Evaluating** allocates a texture per node and dispatches. It happens once per frame at most and for
one graph at a time — two graph tabs and a paste would otherwise be several bakes between two frames,
which is a freeze spent on pictures nobody has looked at yet.

`TryGet` does neither. It is called from the canvas's draw, once per visible node, which is no place to
record commands on a device: it answers with whatever picture already exists and marks the graph for a
rebuild. A node whose picture is a frame old goes on showing it rather than blinking empty.

### Why the pixels go through the host

A texture graph's images are written on the device's compute queue and each of them is exclusive to one
queue family. Reading one from the queue family the interface draws on, with no ownership transfer,
leaves its contents undefined by specification — and it would look perfect on a laptop, because there
one family serves both. So the source reads the picture back as bytes on the queue that wrote it and
hands those to `ITexturePreviewImages.Register`, which uploads them into a texture of the host's own.

`Register` takes the number the node had last time. A host that can rewrite the texels of the picture
already on the screen keeps that number valid and answers with it; one that cannot answers with a new
number, and the source releases the old one. That is not a micro-optimisation: a preview is
re-registered on every edit, which is every keystroke in a settings field, and a fresh upload per node
per keystroke is a texture and a descriptor set each.

## Using it

The editor's texturing plugin builds one and hands it to the graph canvas. A third-party host doing the
same needs four things, and leaving out any one of them produces a feature that computes every picture
and draws none:

1. **A source**, constructed with a delegate answering the current device's evaluator — asked per
   rebuild rather than held, so a device that has gone is never handed back — and a delegate answering
   a compiler over the node library the canvas is editing against.
2. **A sink**, implementing `ITexturePreviewImages` over whatever the host uses to name a picture.
3. **The assignment**, on the canvas, on every build of the panel: a dock panel's factory runs again
   when the panel is reopened, and the canvas it made last time went with the elements.
4. **A per-frame call to `Update`**, from outside a draw.

### When the device goes

A host that loses its device and acquires another calls `Drop`. Every number handed out names a texture
made on the device that is going, so they are given back — and every graph being watched is marked for
a rebuild, so the swatches return when the device does. Disposing instead would be worse than doing
nothing: the canvas is still holding the source, and a disposed source throws from `Update`.

## Limits

- **A graph that does not compile keeps the pictures it had.** A graph an author is halfway through
  wiring does not compile most of the time, and a preview source that raised its diagnostics would be a
  second, noisier copy of the panel that already shows them. The refusals are counted rather than
  reported.
- **A node with several output ports shows its last image**, which is its result rather than an
  intermediate of it.
- **There is no way to ask for one node's picture at a larger size.** A preview is the same 64 texels
  for every node of every graph.

## Examples

Wiring one to a canvas, which is the whole of what a host owes it:

```csharp no-compile="illustrative — `graphics` and `evaluators` come from the plugin host"
TextureGraphPreviews previews = new(
    () => graphics.Device is { } device ? evaluators(device) : null,
    () => new TextureGraphCompiler(registry),
    new TexturePreviewImages(graphics)
);

// Once per frame. A graph is compiled and baked only if something touched it.
previews.Update();

// From the canvas's draw, per visible node.
if (previews.TryGet(graph, node, definition, out var preview)) {
    // preview.Image is the host's number for a picture 64 texels square.
}
```

⚠ **The first argument answers `null` when there is no device**, and that is not a convenience: the
editor acquires its device and creates its thumbnail surface *after* the first `Update`, so a source
that treated either absence as "drawn" would take the graph off its dirty list and leave the panel
blank until the author typed something.

Releasing a device the host has lost, without disposing an evaluator this does not own:

```csharp no-compile="illustrative — called from the plugin's own device-release hook"
previews.Drop();
```

## See also

- [Evaluating a texture plan](editor/texture-graph-evaluation) — the plan, the pool and the bake this
  reads its pictures out of.
- [Compiling a texture graph](editor/texture-graph-compiling) — where `PreviewEveryNode` changes what
  the pool is allowed to reuse.
- [The texture graph plugin](editor/texture-graph-plugin) — the panel that assigns one of these, and
  the plugin contract it goes through.
- [Shader-graph preview thumbnails](editor/shader-graph-previews) — the same idea for a
  `.vxshadergraph`, which draws its own targets on the graphics queue and therefore hands over a
  texture view instead of pixels.
