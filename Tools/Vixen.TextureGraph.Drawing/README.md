# Vixen.TextureGraph.Drawing

The pictures a texture graph's host draws rather than a kernel: `Source/Text` and `Source/Vector` (Patina #139).

A compute kernel has no rasteriser and cannot reach a font or a file, so these two nodes are pictures the caller
supplies, like `Source/Bitmap`. The node writes what it was told into the reference of an external image —
`text:` or `vector:`, then the fields as an escaped query — and a host that fills a plan asks
`TextureDrawings.Draws` of every reference `TextureGraphExternals.Upload` handed back. For a drawing it calls
`TextureDrawings.Fill` with the plan and a way to read a file by the name the node wrote. How a name becomes a
file, and how the file is opened, are the host's; everything else is here, so no host re-reads a node's settings.

## Why an assembly of its own

`Vixen.TextureGraph` links no interface assembly (#720, `HeadlessClosureTests`), and a font is `Vixen.Ui.Text`'s
and SVG path data is `Vixen.Ui`'s. Doc 48 § 4.1 refused `Svg Path` inside the evaluator on exactly that compile
surface and said where it belonged instead: where a picture is *filled*, never where a plan is *evaluated*. This
is that place. A host that never draws text never loads either.

`TextureText` moved here from `Vixen.Editor.TextureGraph`, where it had no caller but its own tests: an editor
assembly is the one place a headless host cannot reach.

## What it draws

- **Text.** A string shaped by HarfBuzz and filled through the `Outlines` path, never `GlyphAtlas`: an atlas
  caches small rasterisations behind a distance field, and a graph wants one outline filled once at whatever size
  a 4K bake asks for. Lines break where the text has a line break and nowhere else. A node that names no font draws
  with the embedded Open Sans (OFL-1.1, linked from `Vixen.Editor.App/Fonts`).
- **Vector.** A stated subset of SVG: paths and the basic shapes, groups, `use`, nested `svg`, transforms, solid
  fills and strokes, opacity, the fill rule, `display` and `visibility`, presentation attributes, inline `style`
  and class rules. Gradients, patterns, text, images, filters and masks are refused with a sentence naming them,
  because a drawing quietly missing a part is a wrong picture nobody can explain. ⚠ `clip-path` is not applied
  (exporters clip a whole drawing to its own frame, which the picture's edge does anyway), and a group's opacity
  multiplies into each shape, so an overlap inside a half-transparent group shows.

⚠ **Both draw at the bake's size** — the plan's base moved by `BakeLevelOffset` — and both **fold** ink across an
edge the plan wraps, so a graph that tiles still tiles with a word across its seam.

⚠ **No new rasteriser.** `GlyphRasterizer` fills both, with the even-odd rule it grew for exactly this, and a
stroke goes through `PathTessellator.Stroke`, its triangles wound alike so that non-zero winding fills their union.

Licensed under Apache-2.0.
