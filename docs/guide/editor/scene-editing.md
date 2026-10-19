---
title: The scene editing service
slug: editor/scene-editing
kind: guide
area: Editor
summary: The Entity menu's verbs as ISceneEditing — group, parent, duplicate, align, snap and the selection walks — each one undo entry on the active scene, callable by a plugin the way the menu calls it.
api: [T:Vixen.Editor.SceneView.ISceneEditing]
tags: [editor, scene, hierarchy, plugins, services, undo]
since: 0.1
status: preview
related: [editor/index, editor/writing-a-plugin, editor/content-library, editor/scene-menus, editor/editing-pipeline]
---

## What it is

`ISceneEditing` is the Scene Authoring context's verbs as a service, published through
`PluginServices`. Every verb acts on `Active` — the scene being edited — and its selection:

| Verb | What it does |
|---|---|
| `CreateChild()` | Adds an entity under the first selected one and selects it; `Entity.Null` when nothing was selected. |
| `Group()` / `GroupAsLod()` | Puts the selection under a new empty parent and selects it — or under a LOD group, one level each, which needs two or more. |
| `DuplicateSelection()` | Copies the selection beside itself, selects the copies and returns how many. |
| `SetParent(entities, parent)` / `ClearParent()` | Moves entities under a parent, or the selection to the root; `WhyNotParent` says in words why a candidate would be refused, before trying. |
| `AlignWithView()` / `MoveToView()` | Puts the selection where the focused viewport's camera (`View`) is, looking where it looks — or moves it, keeping its shape, to the view's pivot. |
| `SnapToFloor()` | Drops each selected entity straight down onto whatever is under it, or onto Y = 0. |
| `InvertSelection()` / `SelectChildren()` / `SelectParent()` | The selection walks. |

`HierarchyChanged` is raised after a verb changed the hierarchy in a way an outliner redraws for.

## What it is for

The Entity menu's lines are names for these methods, registered by the editor's `SceneModule` through
the same `PluginContext` a third party is handed. Before, they were private methods of the
application — so a plugin that wanted "group the selection, undoably, the way Ctrl+G does" could only
rebuild it, with its own idea of what a group's pivot is and its own undo entry.

⚠ **Every verb is one undo entry on `Active`'s stack.** A verb that touches several entities opens
one transaction or executes one command, so Ctrl+Z after a plugin's `Group()` undoes the whole group.

⚠ **`Active` is the active scene, not the active document.** Opening an asset editor activates its
document; a Group pressed while that tab has focus must still group entities, so the verbs follow the
scene rather than whichever document was last focused.

What it deliberately does **not** do:

- **Ask.** Set Parent's chooser is the editor's; the service takes the answer. `WhyNotParent` is
  there so a plugin's own chooser can grey a row with the reason.
- **Create primitives or prefabs.** The Create ▸ entity lines are still the application's.

## Using it

```csharp no-compile="a fragment of a module's Activate"
var editing = context.Services.Require<ISceneEditing>();

context.AddCommand(
    new EditorCommand("mine.group-and-snap", new StringId("mine.group-and-snap", "Group and Snap"), () => {
        editing.Group();
        editing.SnapToFloor();
    }) {
        Enablement = () => editing.Active.Selection.Count > 0
    });
```

Those are two undo entries, one per verb, because each verb is its own entry.

## Examples

A plugin's parent picker that greys the candidates the editor would refuse:

```csharp no-compile="a fragment; `row`, `candidate` and `moving` are the plugin's"
row.Disabled = editing.WhyNotParent(moving, candidate) is not null;
row.Tooltip = editing.WhyNotParent(moving, candidate) ?? string.Empty;
```

## See also

- [The content library](content-library.md) — the same shape for the project's assets.
- [Scene menus](scene-menus.md) — where a module's verbs reach the Scene menu and the context menu.
- [The editing pipeline](editing-pipeline.md) — the one edit path every verb records through.
