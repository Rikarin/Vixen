---
title: Authoring a behaviour tree
slug: ai/authoring-a-tree
kind: guide
area: AI
summary: The .vxbt document, the editor over it, and what each gesture means to the tree that runs.
api: [T:Vixen.Ai.BehaviorTreeContent, T:Vixen.Ai.BehaviorNodeContent, T:Vixen.Ai.BehaviorAttachmentContent, T:Vixen.Ai.BehaviorKeyContent, T:Vixen.Ai.BehaviorNodeSchema, T:Vixen.Ai.BehaviorNodeType, T:Vixen.Ai.BehaviorField, T:Vixen.Ai.BehaviorFieldKind, T:Vixen.Ai.BehaviorSlot, T:Vixen.Ai.BehaviorTreeContentCompiler, T:Vixen.Ai.BehaviorTreeResolver, T:Vixen.Editor.Ai.BehaviorTreeModel, T:Vixen.Editor.Ai.BehaviorAttachmentSlot, T:Vixen.Editor.Ai.BehaviorTreeLayout, T:Vixen.Editor.Ai.BehaviorLayoutOptions, T:Vixen.Editor.Ai.BehaviorTreeProjection, T:Vixen.Editor.AssetEditors.Ai.BehaviorTreeDocument, T:Vixen.Editor.AssetEditors.Ai.BehaviorTreeView, T:Vixen.Editor.AssetEditors.Ai.BehaviorSearchPopup, T:Vixen.Editor.AssetEditors.Ai.BehaviorTreeEditorFactory, T:Vixen.Editor.Assets.Ai.BehaviorTreeImporter, T:Vixen.Editor.Assets.Ai.BehaviorTreeImportSettings, T:Vixen.Ui.Controls.Advanced.GraphAttachment, T:Vixen.Ui.Controls.Advanced.GraphOrientation, T:Vixen.Ui.Controls.Advanced.GraphOverlayRegion, T:Vixen.Ui.Controls.Advanced.NodeOverlayLayer, T:Vixen.Ai.BehaviorBuildContext, T:Vixen.Ai.BehaviorTaskBuild, T:Vixen.Ai.BehaviorDecoratorFactory, T:Vixen.Ai.BehaviorServiceFactory, T:Vixen.Ai.BehaviorTaskFactory]
tags: [ai, behaviour-trees, editor, authoring]
since: 0.1
status: stable
related: [ai/behaviour-trees, ai/blackboard, ai/perception]
---

## What it is

A **`.vxbt`** is a behaviour tree as a file: a list of blackboard keys, a root node with its children
in priority order, and the decorators and services attached to each one. The editor opens it on a
canvas drawn top-down, with an execution-index badge on every node and the compiler's complaints
listed beside it.

The document is read by the editor, checked by the importer and turned into a runnable
`BehaviorTreeTemplate` by `BehaviorTreeContentCompiler` against the game's own action and sensor
registries.

## What it is for

Everything a designer should be able to change without a programmer and without a recompile: which
branch outranks which, what interrupts what, how long a cooldown lasts, which key a condition reads.

You do *not* want a file for a tree that only ever exists in one test or one sample. `BehaviorTree`'s
fluent builder makes one in code, and the compiler is the same one.

## Using it

Double-click a `.vxbt` in the project browser. An empty file opens as a selector with one wait, so
the tree compiles from the first frame rather than opening with two complaints about itself.

### The canvas

Nodes are drawn top-down, parents over the branch they own. Each box carries:

- **A badge** in the header — its **execution index**. That number *is* its priority: node 4 outranks
  node 9 because it is earlier in the pre-order walk, which is what "left to right, top to bottom"
  means. An author who cannot see it cannot reason about what a decorator interrupts.
- **Decorator rows** stacked above the body, in evaluation order.
- **Service rows** stacked below it, with their interval.

⚠ **A decorator and a service are attached, not wired.** There is no edge to draw, because an
attachment is always exactly one edge to exactly one parent, can never be shared, and has no position
of its own.

### Reordering, and why it matters more than it looks

A composite's child order is the whole priority ordering of the tree, and it is **stored in the file**
rather than derived from where the boxes sit. Drag a node onto a sibling to become its neighbour, or
onto a composite to become its child; ↑/↓ on the selection moves it among its siblings.

⚠ **Laying the tree out never changes what it does.** Unreal derives child order from horizontal
position, which makes three ordinary gestures dangerous: auto-layout silently reorders the tree,
dragging a node six pixels to line it up changes which branch wins, and a merge that resolves two
positions produces a tree neither author wrote with a diff showing only coordinates. Here **Lay out**
writes positions and nothing else.

### The blackboard

Keys are added, renamed, retyped and deleted in the panel. The type picker offers the six the runtime
has and nothing else.

⚠ **A rename rewrites every reference in the document**, and the count is what the operation returns.
That is why a file references a key by *name* and the compiled form by *index*: a rename that only
changed the declaration would leave every decorator pointing at a key that no longer exists.

⚠ **A delete leaves its references dangling, on purpose.** Clearing them would throw away which key
forty decorators used to read, which is exactly what somebody undoing a mistaken delete wants back.
The compiler reports each one by name.

### Selecting a decorator shades what it can interrupt

This is the payoff for the narrower abort rule. An observer reaches the siblings under its own parent
composite and no further, which makes the region it can interrupt a **subtree** — and a subtree is a
thing that can be drawn. Select a decorator with `Aborts` set to anything but `None` and the region
is shaded on the canvas.

### The inspector

The selected node's settings are drawn from its declaration in `BehaviorNodeSchema` — label, type and
tooltip — with no per-node editor code. A node type your project adds shows up in the search popup
and gets an inspector for free.

### Compiling

**Compile** checks the tree and lists what it found. A tree whose sensors and actions are registered
in your game's code will report those as unresolved, and that is not an error: laying out a tree
before the code exists is the ordinary order of work, so those become remarks and the topology, the
key references and the parallel's shape are all checked anyway.

The importer applies the same check at build time, and fails the build on everything *except* the
names only a game can resolve.

## Examples

A small tree as a file:

```yaml
version: 1
name: Guard
keys:
  - { name: target, type: Entity }
  - { name: alert, type: Bool }
root:
  name: Brain
  type: Selector
  services:
    - type: UpdateBlackboard
      interval: 0.4
      randomDeviation: 0.1
      fields: { Sensor: nearest, Key: target }
  children:
    - name: Respond
      type: Sequence
      decorators:
        - type: Blackboard
          fields: { Key: target, Test: IsSet, Aborts: Both }
      children:
        - { name: Shout, type: Log, fields: { Message: contact } }
        - { name: Close, type: Wait, fields: { Seconds: "2" } }
    - { name: Patrol, type: Wait, fields: { Seconds: "5" } }
```

Loading one in a game — the tree is data, and the actions and sensors are yours:

```csharp no-compile="a fragment; the content comes from the asset database and the sensor is the game's"
var resolver = new BehaviorTreeResolver();

resolver.AddSensor("nearest", new NearestEnemySensor());

if (BehaviorTreeContentCompiler.TryCompile(content, resolver, out var diagnostics, out var template)) {
    ai.Trees.Add(template!);
    world.Create(AiAgent.Thinking(0));
}
```

⚠ **The action registry is the resolver's, and the tree fills it.** Two `Wait`s with different
durations are two actions — an action object carries its own settings and is shared by every agent —
so the compiler registers one per distinct set of fields rather than one per type.

Adding a node type of your own, which the popup and the inspector then know about:

```csharp compile
using Vixen.Ai;

public static class ProjectNodes {
    public static BehaviorNodeSchema WithCombat(BehaviorNodeSchema schema) {
        ArgumentNullException.ThrowIfNull(schema);

        return schema.Add(
            new BehaviorNodeType(
                "CastAbility",
                "Cast ability",
                "Combat",
                BehaviorSlot.Task,
                "Casts an ability by name and waits for it.",
                [
                    new("Ability", "Ability", BehaviorFieldKind.Text, "Which ability."),
                    new("Target", "Target", BehaviorFieldKind.Key, "The key naming who at.")
                ]
            )
        );
    }
}
```

⚠ **Declaring it is half the job; the compiler also has to be able to build it.** The schema says what
a node is called and what fields it takes, and `Vixen.Ai` cannot construct a type it does not
reference — so a node whose implementation is elsewhere registers a factory too, and gets a
`BehaviorBuildContext` that resolves a key *name* to the index the runtime uses:

```csharp no-compile="a fragment; CastAbilityTask is the project's own"
resolver.AddTask(
    "CastAbility",
    (in BehaviorBuildContext context) => new BehaviorTaskBuild(
        new CastAbilityTask(context.Word("Ability"), context.Key("Target")),
        CastAbilityTask.StateSize
    )
);
```

`AddDecorator` and `AddService` are the same shape. The shipped nodes are matched first, so a factory
that reuses a builtin name does nothing rather than silently changing what every existing file means;
a type in the schema with no factory behind it is a diagnostic on the node, not a crash.
`Vixen.Ai.Perception` is the worked example — its three nodes arrive exactly this way.

## See also

- [Behaviour trees](behaviour-trees.md) — what the tree does once it is running, and what an abort
  means.
- [Perception](perception.md) — three nodes that live in another assembly and register themselves
  through the factories above.
- [The blackboard](blackboard.md) — the six key types and what set-ness is.
