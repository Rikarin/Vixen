---
title: Authoring a graph for someone else
slug: editor/graph-authoring
kind: guide
area: Editor
summary: Switching a node off, bending a wire, describing a graph's exposed parameters and saving presets of them — what an author says about a graph besides its wiring, and where each of those lives.
api: [T:Vixen.NodeGraph.NodeBypass, T:Vixen.NodeGraph.Reroutes, T:Vixen.NodeGraph.GraphPreset, T:Vixen.NodeGraph.GraphPresetAsset, T:Vixen.NodeGraph.SettingWidget, T:Vixen.NodeGraph.SettingCondition, T:Vixen.NodeGraph.GraphInfo]
tags: [editor, node-graph, texture-graph, parameters, presets]
since: 0.2
status: preview
related: [editor/sub-graph-values, editor/node-port-editing, editor/texture-graph-compiling]
---

## What it is

Six things in `Vixen.NodeGraph` that a graph's author sets and a compiler or an inspector reads:

- `GraphNode.Bypassed`, a node switched off. `NodeBypass` is the rewrite that honours it.
- `Reroutes`, the reroute dot: a node of type `Reroute` that only bends a wire.
- `SettingDefinition.Label`, `Widget` and `VisibleIf`, which describe an exposed parameter to whoever
  turns it — `SettingWidget` is the control, `SettingCondition` the visibility rule.
- `GraphPreset`, a named set of parameter values kept in the graph, written as `GraphPresetAsset`.
- `GraphGroup.Color` and `Description`, a frame's colour and what its nodes do together.
- `GraphInfo`, what a graph says about itself on a shelf: a description, an author, tags and the physical
  size one tile stands for, in centimetres. No compiler reads it.

## What it is for

A graph that another person opens — or that another graph holds as a node — is read more often than it
is written. Switching a node off to compare, bending a wire round a crowded corner, naming a knob
`Rust amount` while its expressions keep writing `RustAmount`, hiding the scratch settings while
scratches are off, and shipping a rusty and a clean preset are what make it readable without changing
what it renders.

## Using it

A bypassed node keeps its wires, values and place; only the graph a compiler walks loses it.
`NodeBypass.Through` names the input that is passed on — the first input of the output's own kind —
and `NodeBypass.Apply` returns the walkable graph. `NodeBypass.Bridges` answers the same question for
deleting a node and keeping the chain joined.

A reroute is added like any node, under `Reroutes.Type`, with `Reroutes.In` and `Reroutes.Out`.
`Reroutes.KindOf` says what it carries: what feeds it, or failing that, what it feeds.

A parameter's `Title` is its label, or its name when it has none. `SettingCondition.Holds` decides
whether it is shown, comparing numbers as numbers and names ignoring case.

## How it works

`NodeGraphCompiler.Compile` walks `Walkable(graph)`: `NodeBypass.Apply` on the author's graph, then
inlining, then `NodeBypass.Apply` again, so a node switched off inside a sub-graph is honoured too.
`TextureGraphCompiler.Refresh` walks the same graph, so switching a node off is structural and is never
answered by a refresh, while a number moved beside a bypassed node still is.

## Things to know

- **A bypassed node with no input of its output's kind passes nothing.** What it fed is left unwired
  and takes its own value — a generator switched off is a hole, not a pass-through.
- **The author's graph is never edited by the rewrite.** A graph with nothing switched off comes back
  as itself; otherwise the answer is a copy with the same node identities, so diagnostics still name
  nodes on the canvas.
- **A condition hides; it never changes a value.** A hidden parameter keeps what it holds and compiles
  the same.
- **A preset names parameters by name and only the ones it names.** One added later keeps its value
  when the preset is chosen, and a name the graph no longer declares is ignored.

## Examples

Switching a Levels off and taking the graph a compile walks, which wires the noise straight to the output:

```csharp no-compile="a fragment — `registry` is whatever node library the host registered"
levels.Bypassed = true;
var walked = NodeBypass.Apply(graph, node => registry.TryGet(node.Type, out var type) ? type : null);
// walked has no Levels, and the Output's input comes from the noise.
```

A parameter shown as a dial, labelled apart from its name, and only while another is on:

```csharp no-compile="a fragment — the parameter list belongs to a graph the caller holds"
graph.Parameters.Add(new("Worn", "true", Kind: SettingKind.Bool, Widget: SettingWidget.Toggle));
graph.Parameters.Add(new(
    "ScratchAngle", "45", Kind: SettingKind.Float, Minimum: 0f, Maximum: 360f,
    Label: "Scratch angle", Widget: SettingWidget.Angle, VisibleIf: new("Worn", "true")));
```

## See also

- [Sub-graph values](sub-graph-values.md) — how a containing graph's settings reach a published graph's
  parameters.
- [Editing a node's ports](node-port-editing.md) — the inspector members a setting is drawn with.
