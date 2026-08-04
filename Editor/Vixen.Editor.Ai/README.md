# Vixen.Editor.Ai

The authoring half of [doc 37](../../docs/plan/37-ai-behaviour-trees-utility-and-goap.md)'s behaviour
trees: an editable model over the `.vxbt` document, the tidy top-down layout a tree wants, the
projection onto the node canvas, and the abort-scope query the overlay shades.

## Why it is not on `Vixen.Editor.NodeGraph`

Doc 11's tree files every graph editor under the node-graph framework. Doc 37 § D19 works through
`NodeGraphModel`'s rules against a behaviour tree, and the exercise is worth doing rather than
assuming — a tree passes two of them and fails two more:

| `NodeGraphModel`'s rule | A behaviour tree |
|---|---|
| An edge carries a typed value | ✗ nothing on the edge, but `PortKind.Flow` already means "an edge that means *after*" |
| An input takes one edge | ✓ a child has exactly one parent |
| No cycles | ✓ a tree |
| — | ✗ **a composite's children are ordered**, and `Edges` is an unordered list |
| — | ✗ **decorators and services attach**, and the model has no notion of an attachment |

The temptation is to add ordered edges and attachments to the framework. That was refused: neither
the shader graph nor the VFX graph nor the compositor has any use for either, and a framework that
grows a feature for one consumer grows a feature every consumer's tests have to consider.

**What is shared is everything above the model** — `NodeCanvas`, its wire arithmetic, its pooling and
its culling. The five things doc 37 asked to be added to it are additive and landed there:

| | Where |
|---|---|
| Stacked attachment rows on a node | `GraphNode.Attachments`, drawn by `NodeItem` above and below the body |
| A badge in the node header | `GraphNode.Badge` — the execution index, which *is* the priority |
| Top-down layout | `BehaviorTreeLayout` here, **not** `NodeGraphLayout` — see below |
| Reorder-drop between siblings | `NodeCanvas.Dropped`, turned into a reorder by `BehaviorTreeView` |
| A runtime overlay layer | `NodeCanvas.Overlay` and `NodeOverlayLayer` |

⚠ **The layout landed here rather than in the framework, and that is a deviation worth naming.** Doc
37 asked for a top-down layered layout on the canvas. A tree layout needs the *tree*: a longest-path
layout over the projected graph would have to rediscover the parent-child structure it was projected
from, and would take the sibling order from the wires rather than from the list that actually holds
it. `NodeGraphLayout` is untouched and still left-to-right by longest path, which is right for
dataflow.

A sixth thing was needed and is not in doc 37's list: **`NodeCanvas.Orientation`**. A wire in a
dataflow graph leaves the right edge and arrives at the left; in a tree it leaves the bottom and
arrives at the top. It is not a rotation of the picture — nodes keep their shape and their header
stays on top — it is which edge an anchor is on and which way the curve's handles reach.

## The document

`BehaviorTreeContent` is `Vixen.Ai`'s, not this assembly's, and that is deliberate: a game loading a
`.vxbt` at run time needs the same shape and the same schema to turn a type name into a decorator, so
an editor-side copy would be two of them and a way for them to disagree.

```
tree
 ├── keys   name, type — declaration order is index order
 └── root
      ├── children     ordered, and the order is the whole priority ordering of the tree
      ├── decorators   type + fields, evaluated top to bottom
      ├── services     type + fields + interval + deviation
      └── x, y         where the box sits
```

Three things about the shape are decisions rather than translations:

- **A type name and a bag of named strings, not a polymorphic tree.** A discriminated hierarchy in a
  text asset is a tag people have to get right by hand, and binding one needs reflection at load,
  which ADR-002 rules out. `BehaviorNodeSchema` is the table that resolves the name, and it carries
  the label, the category, the per-field tooltip and the default — none of which a constructor
  signature has, and all of which the generated inspector draws.
- **Child order is stored, not derived from `X`.** Unreal derives a composite's child order from
  horizontal position, which makes three ordinary gestures dangerous: auto-layout silently reorders
  the tree, dragging a node six pixels changes which branch wins, and a merge that resolves two
  positions produces a tree neither author wrote with a diff showing only coordinates. Doc 37 § D5.
- **The blackboard travels with the tree.** Unreal makes it a second asset and shares it between
  trees, which is genuinely useful and is a thing this can grow. What it costs today is that a tree
  cannot be opened, read or compiled on its own, and every diagnostic about a key becomes a
  diagnostic about a file the author is not looking at.

## Undo is a snapshot

The node graphs use fine-grained inverses because a shader graph is thousands of nodes. A behaviour
tree is tens, and a snapshot is a few kilobytes of strings — so every gesture is one snapshot in and
one snapshot out. What that buys is that a reparent, a reorder and a key rename that rewrote forty
references are all undoable *by construction*, with no chance of an inverse that puts back four of
the five things it changed.

⚠ **The first `Do` installs nothing, and that is not an optimisation.** The gesture has already run
against the live tree; installing a copy would change nothing about the document's value and
everything about its *identity* — every node a caller was holding would point into an orphaned tree,
silently, and the next edit through it would go nowhere. That cost a hung test to find, and the
comment on `SnapshotCommand` is where it is recorded.

## What is owed

The live half of doc 37 § Part 5: the active path highlighted in play mode, each node tinted by its
last result, the blackboard panel showing live values with changed keys flashing, and breakpoints on
nodes. Those need a running agent to look at, which is P7's debugger. The utility table, the GOAP
viewer and the query editor are P5, P6 and P8's, and land in this assembly beside the tree.
