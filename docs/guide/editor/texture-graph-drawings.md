---
title: Text and vector drawings in a texture graph
slug: editor/texture-graph-drawings
kind: guide
area: Editor
summary: How a Source/Text or Source/Vector node becomes pixels — the node writes a reference, the host finds the file, and TextureDrawings draws it at the bake's size.
api: [T:Vixen.TextureGraph.Drawing.TextureDrawings]
tags: [editor, texture-graph, text, svg, material-authoring]
since: 0.1
status: preview
related: [editor/texture-graph-generators, editor/texture-graph-evaluation, editor/index]
---

## What it is

Two source nodes draw pictures no compute kernel can: `Source/Text` sets lines of text in a font, and
`Source/Vector` draws an SVG file. Both are *pictures a host fills*, the way `Source/Bitmap` is, because a
compilation runs on every edit and must not open a file, and because a kernel has no rasteriser and cannot reach
a font.

| | |
|---|---|
| `Source/Text` | Lines, a font file (or the embedded Open Sans), size and position as fractions of the image, alignment, letter and line spacing |
| `Source/Vector` | An SVG file, fit (contain, cover, stretch), scale and position, drawn as its colours or as a coverage mask |
| `TextureDrawings` | What a host calls to fill either one |

## How a host fills one

A compilation hands back the external images it could not fill — `TextureGraphExternals.Upload` returns them.
For each, ask `TextureDrawings.Draws(reference)`; for a drawing, call `TextureDrawings.Fill` with the uploads,
the plan and a function that returns a file's bytes by the name the node wrote. `TextureDrawings.FileOf` gives
that name up front, so a host can watch the file.

```csharp
foreach (var owed in TextureGraphExternals.Upload(uploads, plan, compiler.Externals)) {
    if (TextureDrawings.Draws(owed.Asset)) {
        TextureDrawings.Fill(uploads, plan, owed, name => File.ReadAllBytes(Find(name)));
    }
}
```

**What is the host's:** how a name becomes a file, and how it is opened. **What is not:** everything else — the
node's numbers travel in the reference, so no host re-reads a node's settings.

## Things worth knowing

- ⚠ **Drawn at the bake's size.** The fill reads the plan's base and its `BakeLevelOffset`, so a 1K graph baked at
  4K sets the same words with four times the texels on every edge.
- ⚠ **Ink folds across an edge the plan wraps**, so a graph that tiles still tiles with a word across its seam.
- ⚠ **The SVG reader draws a stated subset** and refuses the rest by name: gradients, patterns, text, images,
  filters and masks are an `InvalidDataException` saying which. `clip-path` is not applied, and a group's opacity
  multiplies into each shape inside it.
- **Its own assembly**, `Vixen.TextureGraph.Drawing`, because a font is `Vixen.Ui.Text`'s and SVG path data
  `Vixen.Ui`'s, and the evaluator links no interface assembly.
