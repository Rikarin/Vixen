# Vixen.Editor.AnimationGraph

The third graph [doc 11](../../docs/plan/11-editor.md) names, and doc 20's
[E5](../../docs/plan/20-editor-parity.md#e5--authoring-surfaces-25-em) row for it: an authored
animation state machine as a serialisable document, and the compiler that turns it into the
`AnimationStateMachine` and `AnimationLayer`s `Vixen.Animation` runs.

## Why it is not on `Vixen.Editor.NodeGraph`

Doc 11's tree puts this project under the node-graph framework beside `.ShaderGraph` and
`.VfxGraph`, and building it there was tried first. It does not fit, and the reason is not a detail:

- **A shader graph's edge carries a value. A VFX graph's carries order. A state machine's carries
  "may become".** There is nothing on the edge — no type, no lanes, no default — so every port rule
  the framework exists for has no subject.
- **Several transitions leave one state and several arrive at another.** `NodeGraphModel.Connect`
  replaces the edge into an input, deliberately, because a dataflow input has one source. A state
  with four ways into it is the ordinary case.
- **A state machine has cycles by construction.** `NodeGraphCompiler` refuses one, and it is right
  to: a shader graph with a loop has no evaluation order. A character that cannot get back to idle
  is a bug.

So the model is its own. What *is* shared is the shape of the editor around it — a canvas, a side
panel of the selected thing's settings, a diagnostics list, a compile button — and that is where
sharing belongs.

## The document

`AnimationGraphAsset` is a `[DataContract]` record tree, written as `.vxanimgraph` by whoever holds
a YAML writer. Nothing in this assembly links a parser: which format a document is written in belongs
to its writer, the same arrangement `NodeGraphAsset` has.

```
graph
 ├── parameters   name, type, default
 └── layers       weight, blend, mask, root motion, default state
      └── states  name, motion, speed, wrap, editor position
           └── transitions  destination, duration, exit time, offset, interruption, conditions
```

Three things about the shape are decisions rather than translations:

- **Everything cross-references by name; clips reference by GUID.** A transition names its
  destination and a condition names its parameter, because both are things a person types and both
  survive somebody reordering a list. A clip is an `AssetId` for doc 08's reason — moving the file
  needs nothing done to this one, and `ReferenceIndex` counts the link.
- **A condition stores a parameter *name*, where `AnimationCondition` stores an index.** The index is
  a position in a parameter set that does not exist until the graph is compiled; a file storing one
  would break the first time somebody moved a row in a list that has an up arrow beside it.
- **The state's editor position is in the asset.** Doc 11's argument for the node graphs, unchanged:
  a layout somebody spent an afternoon on is authored data, and re-laying it out on every open throws
  it away every time.

## The compiler

`AnimationGraphCompiler.Compile` resolves every name and every GUID and hands back the runtime
objects plus a list of what was wrong. Three rules it follows:

- **A missing clip is a diagnostic and an empty state, not a refusal.** Laying out idle/walk/run
  before the clips are imported is the ordinary order of work, and a compiler that refused would make
  the graph unopenable until every file existed. `EmptyMotion` writes nothing, so the pose underneath
  survives — which for a masked layer is the arm staying where the layer below put it — and the
  topology is still checkable.
- **Transitions are wired after every state exists.** A state may transition to one declared further
  down the file; a single pass would report half a graph as dangling.
- **A transition with no conditions and no exit time is reported even though it is legal.** It fires
  on the first frame the state is entered, which reads as the state being skipped, and it is the
  single commonest thing to get wrong in a graph like this. An entry state that immediately moves on
  is a real thing to author, so it is a remark rather than an error.

A mask needs a `Skeleton` and the compiler will not invent one: without it, the mask is *reported*
and not applied, because a `BoneMask` is weights per joint of one specific rig and guessing would
silently mask the wrong arm.

Licensed under Apache-2.0.
