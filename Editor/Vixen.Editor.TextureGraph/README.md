# Vixen.Editor.TextureGraph

What the texture graph needs from the editor and nothing else: `TextureProjectImages` resolves an
external image through an `EditorProject`, and `TextureGraphPreviews` feeds node thumbnails.
`TextureText` used to live here and moved to
[`Vixen.TextureGraph.Drawing`](../../Tools/Vixen.TextureGraph.Drawing/README.md) with Patina #139's `Source/Text`
node, since a headless host has to draw it too. `TextureGraphPreviews` evaluates
incrementally and reads back only the nodes whose picture changed, which needs the same compiler
object on every rebuild. The compiler, evaluator, nodes, kernels
and compounds are in [`Vixen.TextureGraph`](../../Tools/Vixen.TextureGraph/README.md), which
references none of this.
