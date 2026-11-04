# Vixen.Authoring

What authoring tools share and no interface needs: `IAuthoringCommand`, `ICommandContext`,
`IAuthoredDocument` and the reactive `Selection<T>`. The editor's `IEditorCommand` extends
`IAuthoringCommand` with a richer context, and `CommandStack` runs either, so a headless host and the
editor can execute the same edits.
