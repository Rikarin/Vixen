# Vixen.Editor.Gameplay.Quests

The authoring half of a quest and of an event chain: an editable model, the chain's spanning walk, and
the projection onto the node canvas.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Dynamic events — *"it is a graph, so
it is authored in `Vixen.Editor.Gameplay.Quests` on the existing node-graph host"*.

## State

**Built: the quest model with its validator, the chain walk with its back edges named, and the canvas
projection with a live overlay. 22 tests. The view is owed**, exactly as
[`Vixen.Editor.Gameplay.Loot`](../Vixen.Editor.Gameplay.Loot/README.md)'s is.

| | |
|---|---|
| `QuestModel` · `QuestProblem` | The document *is* a `QuestDefinition`; every gesture is one operation. |
| `EventChain` · `EventChainEdge` · `EventBranch` | The chain walked into something a canvas can draw. |
| `EventChainProjection` | Boxes, wires, badges, and the live tint. |

## The host is the canvas, not the document model, and that is a finding

Doc 28 says the chain graph is authored "on the existing node-graph host". There are two things that
could mean, and building it settled which.

`Vixen.Editor.NodeGraph`'s `NodeGraphModel` enforces three rules. **An event chain breaks two of
them:**

- **It refuses a cycle as the edge is made.** A camp lost, retaken and lost again is the content — doc
  28 calls that the thing that makes a chain feel alive — not a mistake to be prevented.
- **An input takes one edge.** An event reached from a failure here and a success there is ordinary
  authoring; the model would silently replace the first wire with the second.

`Vixen.Editor.Ai` reached the same conclusion from the other direction, for a tree: what is shared is
everything *above* the model, which is the canvas. So this library owns its own walk and projects onto
`Vixen.Ui.Controls.Advanced.NodeGraph`.

⚠ **The canvas refuses cycles too**, so the projection cannot simply draw every edge either. It walks
a depth-first spanning tree from the entries and wires those; **every remaining edge becomes a
labelled badge on the box it left** rather than disappearing. A canvas that silently omitted the edge
closing a loop would show a chain that ends where the content says it begins again.

## A looping chain has no root, and that is the common case

This was the second finding, and the tests are what produced it. A "root" is an event nothing leads
to — and in a chain that loops, *every* event has something pointing at it, so there are none. A walk
that started only from roots would draw an empty canvas for perfectly good content.

So a component with no root contributes an entry: **the event that leads to the most places**, ties
broken by how much leads to it and then by address. It is a heuristic, but it is a stable one and a
function of the content — which is what a picture nobody wants to see rearrange itself needs. The
accent on a box says *entry*, not *root*, for the same reason.

## What the model checks, and what it deliberately leaves alone

`QuestModel.Validate` answers what an editor can answer with one document open: a stage with no
objectives, a stage of only optional ones, an objective type this build has no `IQuestObjective` for,
a count below one, a hidden optional objective nobody could know to do, a reward "choice" of one.

⚠ **It does not check whether a target address exists, whether a tag is in the build, or whether a
verb resolves.** Those are questions about the whole content set, they are `QuestLibrary.Problems`'
job, and answering half of them here in a second implementation is how the two come to disagree.

## What is owed

- **The view.** A `NodeCanvas`-hosted panel with the objective rows editable in place, and the quest
  model's problems shown against their rows.
- **Editing the chain from the canvas.** The projection is one-way: a wire dragged on the canvas does
  not yet write `onSuccess:` back into the definition.
- **A tag picker and a definition picker** for `target:` and `targetTags:`, which are
  `Vixen.Editor.Gameplay`'s and are not built.
