---
title: Editing a node's ports
slug: editor/node-port-editing
kind: guide
area: Editor
summary: The editing pipeline's second IEditProvider — a graph node's inline port values described as inspector members, so the ordinary inspector panel and a .vxml tree edit them by name instead of a hand-written panel per graph.
api: [T:Vixen.Editor.NodeGraph.NodePortEditProvider, T:Vixen.Editor.NodeGraph.NodePortMember, T:Vixen.Editor.NodeGraph.NodeInspector]
tags: [editor, node-graph, inspector, undo, multi-object-editing]
since: 0.2
status: preview
related: [editor/editing-pipeline, editor/inspectors-in-markup, editor/vfx-graph]
---

## What it is

`NodePortEditProvider` is an [`IEditProvider`](editing-pipeline.md) over one node type's inline port
values. `NodePortMember` is one of those ports as an `InspectorMember`: a name, the type a person
edits it as, a read, a write, and the `SetPortValueCommand` that makes the write undoable.

`NodeInspector` is the panel beside a graph canvas. It no longer draws anything itself — it works out
what may be shown and hands it to an `InspectorView`, the same panel that draws components and
assets.

## What it is for

A node's numbers live on the **graph**, in `GraphNode.Values` keyed by port name, because that is
what survives a save and an undo. They are therefore not members of any type: no generator describes
them, no reflection pass can find them, and `InspectorEditProvider` — which answers from the
inspector registry — answers nothing for a `GraphNode`.

That is why every graph editor used to write its own panel. A provider is what replaces it: once the
ports are described, everything generic over members works on them without knowing what a port is —
binding by name, editing several nodes at once, reporting a mixed value, `Changed`, a drawer chosen
by type, a reset button, and a `.vxml` tree that names a member in a string.

⚠ **One provider per node type, not one per process.** `EditTarget` resolves members by CLR type and
every node is a `GraphNode`, so a provider that answered for `GraphNode` in general would have to
answer with *some* node type's ports and would be wrong for all the others. `For` builds one against
the definition of the type actually selected.

## Using it

Build a provider for the selected type, then hand its description and itself to any panel:

```csharp no-compile="the graph, the definition and the nodes are the caller's"
var provider = NodePortEditProvider.For(graph, definition, node.Id, readOnly: view.IsReadOnly);

if (provider.Describes(graph, selected)) {
    panel.Inspect(provider.Descriptor, provider, [.. selected]);
}
```

`Describes` is the check `EditTarget` cannot make. Its `CommonType` is the CLR type, so a selection
of an Add and a Multiply looks uniform to it; only the graph knows they are different node types, or
that two nodes of one type are wired differently. Ask before showing a selection.

⚠ **A connected input is not a member.** A port fed by an edge takes its value from that edge, so a
row showing a number the compiler ignores is how somebody comes to spend an afternoon changing a
field that does nothing. Those ports are listed on `Connected` instead, for a panel to say where the
value comes from. The wiring is part of what a provider was built against, so a graph whose edges
changed needs a new one.

⚠ **The lanes are the storage; the value is what a person edits.** A port is one to four floats on
the node, and `MemberType` is what a row edits it as — `bool`, `int`, `float`, `Vector2`, `Vector3`
or `Vector4`. A dynamic port is one `float` however wide it resolved, because the compiler splats a
short constant. A texture, a sampler and a flow port take no typed value and are not members at all.

## Examples

**Binding a port by name, with no panel at all.** The pipeline is enough:

```csharp no-compile="the graph and the definition are the caller's"
var target = new EditTarget([node], NodePortEditProvider.For(graph, definition, node.Id), document);

target.Find("Base Colour")?.Write(new Vector3(0.5f, 0.6f, 0.7f));
```

**Laying a node type's ports out in markup.** A `<PropertyField>` names a member in a string the
compiler never sees and the join happens after the tree is built, so a node type can be grouped and
ordered exactly as [`TerrainBrushInspector.vxml`](inspectors-in-markup.md) does for a brush:

```xml
<Expander Label="Inputs" IsExpanded="true">
    <PropertyField Path="Base Colour" />
    <PropertyField Path="Roughness" />
</Expander>
```

```csharp no-compile="binding the built tree, which is what MarkupBinding is for"
var target = new InspectorTarget([node], document, null, provider, provider.Descriptor);

MarkupBinding.Bind(view.Root, target);
```

**Editing several nodes as one undo entry.** Nothing extra is asked for: `EditProperty` writes to
every object it reaches, and `SetPortValueCommand` keeps a "before" per node — including, for a node
that held no inline value at all, the fact that it held none.

```csharp no-compile="two nodes of one type, one entry on the stack"
var target = new EditTarget(selected, provider, document);

target.Find("A")?.Write(0.5f);   // "Set A (2)", one Ctrl+Z
```

## See also

- [The editing pipeline](editing-pipeline.md) — `EditTarget`, `EditProperty` and what a provider is.
- [Inspectors in markup](inspectors-in-markup.md) — `PropertyField`, `binding-path` and
  `MarkupBinding`.
- [The VFX graph](vfx-graph.md) — one of the two shipping editors whose side panel this drives.
