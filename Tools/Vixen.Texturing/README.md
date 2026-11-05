# Vixen.Texturing

The headless half of material painting: the layer stack and the compiler that turns it into a texture
graph, smart materials, strokes, brushes and projection onto a mesh. Nothing here draws.
[`Vixen.Editor.Texturing`](../../Editor/Vixen.Editor.Texturing/README.md) is the panels over it, and
its README carries the design reasoning and the list of known gaps.

Commands take an `ILayerStackDocument`, so a host with no editor can run them. Most of this code's
tests still live in `Vixen.Editor.Texturing.Tests`, because their fixtures open a real document.

Paint layers are sparse tiles with copy-on-write undo, `.vxpaint` is a pack of them, and
`LayerStackEvaluator` is a stack through the one evaluator with no editor around it:
[the paint tile store](../../docs/guide/editor/paint-tile-store.md).
