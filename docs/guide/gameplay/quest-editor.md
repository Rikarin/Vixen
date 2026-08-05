---
title: The quest and event-chain editor
slug: gameplay/quest-editor
kind: guide
area: Editor
summary: An editable model over the definition, and a chain walked into a spanning tree because an event chain is cyclic by design and both of the engine's graph models refuse a cycle.
api: [T:Vixen.Editor.Gameplay.Quests.QuestModel, T:Vixen.Editor.Gameplay.Quests.QuestProblem, T:Vixen.Editor.Gameplay.Quests.EventChain, T:Vixen.Editor.Gameplay.Quests.EventChainEdge, T:Vixen.Editor.Gameplay.Quests.EventBranch, T:Vixen.Editor.Gameplay.Quests.EventChainProjection]
tags: [editor, quests, events, node-graph, authoring]
since: 0.1
status: preview
related: [gameplay/quests, gameplay/dynamic-events, gameplay/loot-editor]
---

## What it is

**`QuestModel`** is one quest open for editing, with every gesture as an operation and a validator
that reports against rows. **`EventChain`** walks a library's dynamic events into a shape a canvas can
draw, and **`EventChainProjection`** draws it.

## What it is for

Authoring the graph doc 28 asks for — the chain where a failed escort starts "retake the camp" — and
seeing which box is lit while it runs.

## Using it

Open a `QuestDefinition` in a `QuestModel`; snapshot before an edit and restore for an undo. Build an
`EventChain` from a compiled `QuestLibrary` and project it onto a `NodeCanvas`.

⚠ **The host is the canvas, not `Vixen.Editor.NodeGraph`.** That model enforces three rules and an
event chain breaks two: it refuses a cycle as the edge is made — and a camp lost, retaken and lost
again is the content — and it gives an input one edge, while an event reached from a failure here and
a success there is ordinary. `Vixen.Editor.Ai` reached the same conclusion for a tree.

⚠ **Only the spanning tree is wired; everything else is a badge.** The canvas refuses cycles too, so
the walk draws one wire into each event it reaches and every remaining edge becomes a labelled
attachment on the box it left. A picture that silently omitted the edge closing a loop would show a
chain that ends where the content says it begins again.

⚠ **A looping chain has no root, and that is the common case.** Every event has something pointing at
it, so the walk falls back to the event that leads to the most places — stable, and a function of the
content. The accent says *entry*, not *root*.

⚠ **`Validate` answers only what one open document can answer.** Whether a target address exists,
whether a tag is in the build and whether a verb resolves are questions about the whole content set,
and they are `QuestLibrary.Problems`' — a second implementation here is how the two come to disagree.

## Examples

Editing with an undo:

```csharp compile
using Vixen.Editor.Gameplay.Quests;
using Vixen.Gameplay.Quests;

static class Editing {
    public static QuestDefinition Retitle(QuestModel model, int stage, string name) {
        var undo = model.Snapshot();

        model.Edit(stage, 0, objective => objective.DisplayName = name);

        return undo;
    }
}
```

Drawing the chain, and tinting it live:

```csharp compile
using Vixen.Editor.Gameplay.Quests;
using Vixen.Gameplay.Quests;
using Vixen.Ui.Controls.Advanced;

static class Chain {
    public static NodeGraph Draw(QuestLibrary library, DynamicEventDirector? director) {
        var projection = new EventChainProjection();
        var graph = projection.Project(EventChain.Build(library));

        // Every edge the walk could not wire is on a box as a badge — Deferred says how many.
        projection.Live(director);

        return graph;
    }
}
```

## See also

- [Dynamic events](gameplay/dynamic-events) — what the chain is a picture of.
- [Quests](gameplay/quests) — what the model edits.
- [The loot table editor](gameplay/loot-editor) — the same bargain about the document being the definition.
