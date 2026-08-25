---
title: The editor shell
slug: editor/index
kind: guide
area: Editor
summary: The window the editor is made of, and the command registry every part of it is a view over.
api: [T:Vixen.Editor.Ui.EditorShell, T:Vixen.Editor.Ui.EditorCommand, T:Vixen.Editor.Ui.CommandRegistry, T:Vixen.Editor.Ui.KeyMap]
tags: [editor, shell, commands, keybindings]
since: 0.1
status: preview
related: [ui/commands, editor/modes, editor/play-mode-systems, editor/external-edits, editor/scene-menus, editor/icons, editor/utility-styles, editor/editing-pipeline, editor/writing-a-plugin, editor/editor-scripts, editor/inspectors-in-markup, editor/frame-panel, editor/network-panel, editor/sub-object-picking, editor/selection-cage, editor/snapping, editor/precision, editor/mesh-editing, editor/element-selection, editor/shape-tool, editor/face-materials, editor/booleans, editor/retopology-and-uv-surfaces, editor/vfx-graph]
---

## What it is

`EditorShell` is the editor's window: a menu bar, a mode bar, a toolbar, a docking workspace and a
status bar, built into a `UiDocument` and nothing else — no platform, no device, no window. Inside it
are `CommandRegistry`, the one table of everything the editor can be asked to do; `EditorCommand`,
one entry in it; and `KeyMap`, which says what runs each of them from the keyboard.

## What it is for

Menus, toolbars, context menus and the command palette are *views over the registry* rather than four
places that each know how to save a file. An action added once appears everywhere it belongs, gets a
keybinding, gets a place in the palette, and gets its enabled state from one predicate instead of
four copies that drift.

The shell is deliberately constructible with no GPU, which is what makes the whole of the editor's
chrome testable headless — a shell, synthetic input, and assertions against the real element tree.

You do not want it when you are writing a panel. A panel is a control over a model; which dock group
it lands in and which commands act on it is the *application's* arbitration, and a panel that reached
for the shell would be one that cannot be tested without one.

## Using it

Register a panel, a layout and a command; the menu, the palette and the shortcut follow.

```csharp no-compile="a fragment against a live shell — the project and its verbs are the application's"
using var shell = new EditorShell(1600f, 1000f);

shell.RegisterPanel("scene", new StringId("editor.panel.scene", "Scene"), panel => panel.Add<Viewport>());

shell.Commands.Add(new EditorCommand("file.save", EditorStrings.CommandSave, project.Save) {
    Enablement = () => project.IsDirty
});

shell.Keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));
```

**Enablement is a predicate, not a flag.** A flag has to be pushed at every view whenever the world
changes, which means a menu that is right only if somebody remembered to invalidate it. A menu asks
as it opens and a toolbar asks on the tick, so neither can be stale — the cost is that the predicate
runs often and has to be cheap.

**A command carries no keybinding.** That is `KeyMap`'s, because a binding is the user's and a
command is the application's. The map is three layers — the defaults the application ships, a chosen
preset, and the user's own overrides — and only the last is saved, so a default moved in a release
reaches everyone who had not deliberately rebound it.

**A command may declare a context.** Delete in the outliner and Delete in the content browser are two
commands and one key. `EditorCommand.Context` names the place a verb belongs, `EditorShell.Context`
says which place has the focus, and `CommandRegistry.CanExecute` refuses the one belonging somewhere
else. Within a context a chord belongs to one command and a conflict is reported rather than
resolved; across contexts, sharing a chord is the point.

## Examples

The context mechanism is what an editor mode's claim on a key is built out of — see
[editor modes](modes.md), where `1`–`4` mean the blockout element modes while that mode is active and
view-bookmark recall while it is not.

A command that is declared but not built yet says so rather than being absent:

```csharp no-compile="a fragment; `EditorStrings` and the id are the application's"
shell.Commands.Add(new EditorCommand("scene.bake-lighting", title, Bake) {
    Unavailable = new StringId("editor.unavailable.bake", "There is no lightmapper yet")
});
```

It is greyed wherever it appears, and the palette and the refusal notice both have a sentence to
show. A menu line that is missing reads as an editor that cannot do the thing; one that is there and
greyed reads as an editor that will.

## See also

- [Editor modes](modes.md) — what the viewport's input means right now.
- [Sub-object picking](sub-object-picking.md) — which face, edge or vertex of one mesh is under the
  pointer, which is the question a mode's element modes ask.
- [Showing what is selected](selection-cage.md) — what a viewport draws round the object a click
  landed on, and why a pane drawn by a compositor cannot say it with colour.
- [Snapping](snapping.md) — what a transform lands on, and which part of it lands there.
- [Building to a number](precision.md) — the work plane, typed transforms, the tape measure and the
  scale references.
- [Editable meshes in a scene](mesh-editing.md) — how an entity comes to carry geometry you can edit.
- [docs/plan/11](https://github.com/Rikarin/Vixen/blob/master/docs/plan/11-editor.md) — the editor's
  shape, and the eight extension points a plugin writes against.
- [docs/plan/20](https://github.com/Rikarin/Vixen/blob/master/docs/plan/20-editor-parity.md) — the
  parity plan, panel by panel and menu by menu.
