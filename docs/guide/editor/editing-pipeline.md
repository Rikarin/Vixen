---
title: The editing pipeline
slug: editor/editing-pipeline
kind: guide
area: Editor
summary: One path every edit in the editor takes, so undo, multi-object editing and mixed values are answered once rather than per panel.
api: [T:Vixen.Editor.Core.EditTarget, T:Vixen.Editor.Core.EditProperty, T:Vixen.Editor.Core.EditValue, T:Vixen.Editor.Core.IEditMember, T:Vixen.Editor.Core.IEditProvider, T:Vixen.Editor.Core.EmptyEditProvider, T:Vixen.Editor.Core.SetValuesCommand, T:Vixen.Editor.Inspector.InspectorEditProvider, T:Vixen.Editor.SceneView.GizmoDrag, T:Vixen.Editor.SceneView.GizmoEdit]
tags: [editor, undo, inspector, plugins, multi-object-editing]
since: 0.1
status: preview
related: [editor/modes, editor/index]
---

## What it is

`EditTarget` is what is being edited: some objects, the document their changes are recorded in, and
an `IEditProvider` that says how to reach their members. `EditProperty` is one member bound to all of
them — read it, write it, and the write is an undo entry. `EditValue` is what a read answers with,
which is either a value they all share or the fact that they disagree.

Underneath, `IEditMember` is the narrow contract a provider hands back: a name, a type, a read, a
write, and the command that makes the write undoable. `SetValuesCommand` is that command for anyone
who has no typed accessors to offer, so an implementation gets merging and per-object old values
without writing either. `InspectorEditProvider` is the first real provider, over the descriptors the
inspector's generator emits.

`GizmoDrag` and `GizmoEdit` are the same idea at the other end of the editor: a finished drag, and
the entry it turns into together with the history that entry belongs on.

## What it is for

Every surface that changes something has the same four problems — undo, editing twenty objects at
once, showing that the twenty disagree, and telling everything else that a value moved. Solving them
per surface is how an editor ends up with five commands, five answers to "what does a mixed selection
show", and a plugin that cannot join the undo stack at all because there is no shared thing to join.

⚠ **The concrete consequence is that a plugin gets all four for free.** A panel written outside this
repository builds an `EditTarget` over whatever it is showing and writes through it; the entries land
on the same stack as the inspector's and the viewport's, in the order they happened, and Ctrl+Z takes
them back in the same order.

It is also what markup will bind against. A `.vxml` attribute naming a member has to resolve against
*something* that is not a C# type — `EditTarget.Find` is that something.

You do not want it for state that is not an edit. A camera's position in a viewport, a panel's scroll
offset and which foldouts are open are not things anybody expects Ctrl+Z to touch.

## Using it

Build a target over the selection, ask it for a property by name, and write.

```csharp no-compile="a fragment — the provider and the document belong to whatever panel this is in"
var target = new EditTarget(selection, InspectorEditProvider.Default, document);

if (target.Find("Roughness") is { } roughness) {
    var current = roughness.Read();

    // Twenty objects that disagree read as mixed rather than as one of the twenty.
    slider.Value = current.Or(0f);
    slider.IsIndeterminate = current.IsMixed;

    slider.ValueChanged += (_, value) => roughness.Write(value);
    slider.DragEnded += (_, _) => roughness.Seal();
}
```

⚠ **`Seal` is what ends a run of merged edits**, and it is explicit rather than a time window. A
window makes how many undo steps a drag produced depend on how fast somebody moved a mouse, which is
neither predictable for the user nor testable without a fake clock. Call it on mouse-up and on focus
loss.

⚠ **Wrap the write-back with `Refreshing` when you put a value *into* a control.** Setting a
control's value raises its own changed event, which calls `Write`; that is harmless when the value is
the one already held and destructive when the property is mixed, because a mixed property has no such
value and the control's neutral position would be written to everything selected.

```csharp no-compile="the guard, which every inspector needs exactly once"
using (roughness.Refreshing()) {
    slider.Value = roughness.Read().Or(0f);
}
```

## Examples

**Describing your own type.** Implement `IEditMember` per member and `IEditProvider` over the set;
`SetValuesCommand` supplies the command, so undo, merging and mixed-value editing come with it.

```csharp no-compile="a member of a type this guide does not have — the accessors are yours"
sealed class PortMember(NodePort port) : IEditMember {
    public string Name => port.Name;
    public string DisplayName => port.Label;
    public Type ValueType => port.ValueType;
    public bool CanWrite => !port.IsConnected;
    public bool CoalescesEdits => port.IsNumeric;

    public object? Read(object owner) => ((Node) owner).Read(port);
    public void Write(object owner, object? value) => ((Node) owner).Write(port, value);

    public IEditorCommand CreateSetCommand(
        IReadOnlyList<object> targets,
        object? value,
        EditorDocument? document
    ) {
        var previous = new object?[targets.Count];

        for (var index = 0; index < targets.Count; index++) {
            previous[index] = Read(targets[index]);
        }

        return new SetValuesCommand(this, targets, previous, value, document);
    }
}
```

**Editing one leaf of a struct across a selection.** `Write` sets every object to the same value,
which is wrong for a nested value type: nudging twenty objects along X would give them all the first
one's Y and Z. `WriteEach` says what each object gets, as one undo entry.

```csharp no-compile="the composite case, and the reason WriteEach exists"
var moved = new object?[target.Objects.Count];

for (var index = 0; index < moved.Length; index++) {
    moved[index] = ((Vector3) position.Member.Read(target.Objects[index])) with { X = typed };
}

position.WriteEach(moved);
```

**Recording a drag.** A gizmo target says what its drag was and where the entry belongs, so a
viewport does not hold a list of which target kinds are exceptions to its rule.

```csharp no-compile="an IGizmoTarget's half of the contract"
public GizmoEdit? Record(in GizmoDrag drag) {
    var command = new TransformTargetsCommand(drag.Verb, drag.Targets, drag.Captured, drag.Document);

    return command.IsEmpty ? null : new(command, drag.Document?.Stack);
}
```

⚠ **The stack comes back with the command rather than being assumed.** A proxy shape dragged in an
animation panel belongs to the shape set's file and its entry belongs on that file's history — not on
whichever scene happened to be showing it, which would make undoing it depend on which tab had focus.

## See also

* [The editor shell](index.md) — the command registry every entry ends up in
* [Editor modes](modes.md) — what a gesture means before it becomes an edit
