# Vixen.Texturing

The headless half of material painting: the layer stack and the compiler that turns it into a texture
graph, smart materials, strokes, brushes and projection onto a mesh. Nothing here draws.
[`Vixen.Editor.Texturing`](../../Editor/Vixen.Editor.Texturing/README.md) is the panels over it, and
its README carries the design reasoning and the list of known gaps.

Commands take an `ILayerStackDocument`, so a host with no editor can run them. Most of this code's
tests still live in `Vixen.Editor.Texturing.Tests`, because their fixtures open a real document.

Paint layers are sparse tiles with copy-on-write undo, `.vxpaint` is a pack of them, and
`LayerStackEvaluator` is a stack through the one evaluator, which a host with no editor uses as it is
and Vixen's paint panes use through `LayerStackPreview`. There is no C# composite beside it: a session
writes a layer and the plan is what shows it. See
[the paint tile store](../../docs/guide/editor/paint-tile-store.md).

## A stamp on a mesh

`PaintMeshSurface` is the join between a mesh and an atlas, and [the guide](../../docs/guide/editor/paint-tile-store.md)
says how a stamp uses it. ⚠ Two things it deliberately does not do: depth under the brush is a limit rather than an
occlusion test, and a UV outside the unit square is not wrapped, so a mesh laid out across UDIM tiles paints only the
first.
