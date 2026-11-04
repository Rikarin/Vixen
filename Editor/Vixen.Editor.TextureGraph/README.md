# Vixen.Editor.TextureGraph

What the texture graph needs from the editor and nothing else: `TextureProjectImages` resolves an
external image through an `EditorProject`, `TextureGraphPreviews` feeds node thumbnails, and
`TextureText` rasterises through the interface's font stack. The compiler, evaluator, nodes, kernels
and compounds are in [`Vixen.TextureGraph`](../../Tools/Vixen.TextureGraph/README.md), which
references none of this.
