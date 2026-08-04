---
title: Scene menus
slug: editor/scene-menus
kind: guide
area: Editor
summary: A pie menu under the cursor and a list beside it, both filled by one registration and filtered by the active mode.
api: [T:Vixen.Editor.SceneView.SceneMenuItem, T:Vixen.Editor.SceneView.SceneMenuSurface, T:Vixen.Ui.Controls.RadialMenu, T:Vixen.Ui.Controls.RadialItem]
tags: [editor, viewport, menus, modes, plugins]
since: 0.2
status: preview
related: [editor/modes, editor/writing-a-plugin]
---

## What it is

Two menus the viewport summons, and one registration behind both.

`RadialMenu` is a pie menu: wedges on a ring round the point it opened at, aimed by direction rather
than by pointing. `RadialItem` is one wedge. Both live in `Vixen.Ui.Controls` and know nothing about
the editor — a radial menu is a general idiom and this one is a peer of `Menu` and `ContextMenu`.

`SceneMenuItem` is the editor's half: a record naming a command, which of the two menus it belongs
to, and — the part that matters — which mode offers it. `SceneMenuSurface` is that choice, and it is
a flags enum because the useful case is both.

The editor binds `Q` to the pie and `C` to the list, both scoped to the scene pane.

## What it is for

A mode is a set of verbs somebody uses constantly, and doc 24's argument for modes is that a viewport
cannot show them all at once. The mode bar and the tool strip are two answers to that and both cost a
trip to the edge of the screen; a pie under the cursor costs a flick and does not move the eye, which
is what a tool used forty times a minute needs.

⚠ **A pie is fast because a given verb is always in the same direction.** After a week the direction
is muscle memory and the menu is not read at all. That is why nothing sorts the wedges, why `Order`
is worth setting, and why an entry belonging to a mode is *absent* in other modes rather than greyed
— a ring whose live wedges move as modes change is a ring nobody can learn.

⚠ **Two gestures, and both have to work or neither is used.** *Press the key, then click a wedge* is
what somebody does while they are still reading the labels. *Hold the key, flick, release* is what
they do afterwards. They are the same menu in the same place: the command runs on the key going down,
so the menu opens held, and the release either lands on a wedge and runs it or lands in the dead zone
and leaves the menu up to be clicked.

⚠ **The context list is a key rather than the secondary button, and the viewport is why.** Right-drag
orbits and right-press begins fly navigation, which every 3D editor binds the same way and none of
them gives up for a menu.

## Using it

An entry names a command id rather than carrying an action, which is the same decision the toolbar
and the menu bar already made: "Extrude" is one thing whether it is reached from a menu, a pie, a
shortcut or the palette, with one enablement and one binding. What a `SceneMenuItem` adds is *where*,
not *what*.

`Mode` is matched against `EditorModes.Active`. An entry with none is offered everywhere.

## Examples

A module putting its two commonest verbs a flick away, and its whole set in the list:

```csharp no-compile="`SceneMenuItem` is registered through a plugin context, which needs a running shell"
public void Activate(PluginContext context) {
    var registry = context.Services.Require<IEditorRegistry>();

    context.Owns(registry.Add(new SceneMenuItem("blockout.extrude") { Mode = "blockout", Order = 0 }));
    context.Owns(registry.Add(new SceneMenuItem("blockout.bevel") { Mode = "blockout", Order = 1 }));

    // Read rather than aimed: the long tail goes in the list only.
    context.Owns(
        registry.Add(new SceneMenuItem("blockout.flip", SceneMenuSurface.Context) { Mode = "blockout" })
    );
}
```

Driving the control directly, for a host that is not the scene view:

```csharp no-compile="an overlay needs a document, which a compiled example has no way to open"
var pie = document.Root.Add<RadialMenu>();

pie.AddItem("Move");
pie.AddItem("Rotate");
pie.AddItem("Scale");
pie.AddItem("Delete");

pie.Chose += (_, item) => Run(item.Index);
pie.OpenAt(cursor.X, cursor.Y, hold: true);
```

`WedgeAt` is what the aiming is built out of, and it is worth knowing about because it is *not* a hit
test: a flick routinely overshoots the ring by a long way, so what is measured is the angle from the
centre, and how far out the pointer went matters only for the dead zone.

## See also

- [Editor modes](modes.md) — what a mode is, and the key-conflict argument these menus finish.
- [Writing a plugin](writing-a-plugin.md) — the registration scope `context.Owns` puts an entry in,
  and what withdraws it on unload.
- `EditorCommand`, `CommandRegistry` — where the verbs themselves live.
