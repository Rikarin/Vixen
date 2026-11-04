# Vixen.NodeGraph

The headless half of the node-graph framework: the document model, the node registry and its
generator, undoable commands, sub-graphs and the compiler base. Nothing here draws.
`Vixen.Editor.NodeGraph` is the view over it, and its README carries the design reasoning.

`HeadlessClosureTests` holds the line that makes this assembly worth having: no `Vixen.Ui*` or
`Vixen.Editor.*` assembly reaches a consumer's output folder, apart from `Vixen.Ui.Reactive`, which
is signals and references only Core.
