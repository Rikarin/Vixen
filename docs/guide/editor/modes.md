---
title: Editor modes
slug: editor/modes
kind: guide
area: Editor
summary: What the viewport's input means right now, and how a mode claims keys that already mean something else.
api: [T:Vixen.Editor.Ui.IEditorMode, T:Vixen.Editor.Ui.EditorModes, T:Vixen.Editor.Ui.SelectMode, T:Vixen.Editor.SceneView.IViewportInput, T:Vixen.Editor.Blockout.BlockoutMode, T:Vixen.Editor.Blockout.BlockoutElement]
tags: [editor, viewport, input, blockout, terrain, foliage, plugins]
since: 0.1
status: preview
related: [editor/terrain-mode, editor/foliage-mode, editor/water-mode]
---

## What it is

A mode is a statement about what the viewport's input means right now. `IEditorMode` is one — an id,
a title, an icon, an activation pair, an optional toolbar, an optional panel, a command context, and
first refusal on viewport input. `EditorModes` is the registry behind the mode bar, reached as
`EditorShell.Modes`. `SelectMode` is the neutral one the editor starts in; `BlockoutMode` is the
second, and the one that proves the seam is load-bearing rather than decorative; `TerrainMode` is the
third, and the one that shows what proving it bought.

⚠ **A second and a third claimant on the same keys cost a `Context` string each.** Blockout takes
`1`–`4` for its element modes, terrain `1`–`8` for its sculpt and paint tools, and foliage `1`–`6` for
its own; view-bookmark recall keeps all nine everywhere none of them has the focus. Nothing in `Vixen.Editor.Ui` changed to allow
that, which is the whole of what a seam with one implementation could only assert.

`IViewportInput` is the other end of it. `Vixen.Editor.SceneView` deliberately does not reference the
shell, so a pane declares the hook it needs and the application joins the two.

## What it is for

Unreal's *Select / Landscape / Foliage / Mesh Paint* strip is not a toolbar of commands. It changes
what a click *is*. The reason the interface exists before the second mode does is that retrofitting
one is how an editor ends up with six mutually-exclusive booleans on the viewport, each read by a
different handler.

The concrete thing it resolves is a key conflict, and it is a real one. `1`–`9` recall a view
bookmark, which is what both reference editors bind them to. `1`/`2`/`3`/`4` are object, vertex, edge
and face, which is what every modelling tool has bound them to for thirty years. Both are right. A
mode that owns those keys while it is active and releases them when it is not is the only resolution
that does not make one of them worse — and the machinery is already there, because
`EditorCommand.Context` and `KeyMap` are how the outliner and the content browser already share
Delete.

You do not want a mode for a verb. Anything with a name and a place in a menu is an `EditorCommand`.
A mode is for when the *meaning* of a gesture changes.

## Using it

A mode is registered once and activated whenever. Registration puts its commands in the registry so
they are listed, rebindable and visible in the palette before anybody has entered the mode;
activation is state.

```csharp no-compile="a fragment against a live shell — the mode set is the application's"
shell.Modes.Add(new SelectMode());
shell.Modes.Add(new BlockoutMode());

shell.Modes.Activate(BlockoutMode.ModeId);
```

Adding a mode registers a `mode.<id>` command in a radio group, so the mode bar, the palette and the
keymap all get it from one call. `EditorModes.Bar()` is what the shell draws: the mode buttons as one
segmented control, then the active mode's own strip.

**Claiming a key is `Context` and nothing else.** A command that declares the mode's context is filed
under that context in the keymap, so the chord it wants is not taken from whoever holds it globally:

```csharp no-compile="a fragment; the registration and the binding are two halves of one setup"
shell.Commands.Add(new EditorCommand("blockout.element.vertex", title, Choose) {
    Context = BlockoutMode.BlockoutContext
});

shell.Keys.SetDefault("blockout.element.vertex", new KeyChord(InputKey.Number2, ModifierKeys.None));
```

`scene.bookmark-go-2` keeps `2` everywhere the blockout context does not have the focus, and neither
command had to move. Which context has the focus is the application's to say — see
`EditorApplication.ContextualViewport`, which reports the active mode's context from the scene pane
and hands it back to the scene when the mode has none.

**First refusal is refusal over what a gesture starts.** A pointer event that arrives while the gizmo
is being dragged goes to the pane whatever the mode says, because a mode that could take the release
of a drag it did not begin would leave the gizmo holding the object. Keys are the other way round and
are offered during a drag, because typed numeric entry mid-drag is only meaningful while one is in
flight.

## Examples

A plugin adds a mode through the contract, and unloading takes it back out — including leaving the
mode if the user is in it:

```csharp no-compile="the plugin contract is loaded into a collectible context rather than compiled here"
public sealed class TerrainPlugin : IEditorPlugin {
    public void Activate(PluginContext context) => context.AddMode(new SculptMode());
}
```

A mode that only owns its keys is a small class. `BlockoutMode` is one today: it declares the
blockout context, registers the four element modes and `Tab`, and declines every pointer and key
event, because there is no editable mesh in the engine yet.

```csharp no-compile="a fragment against a live shell"
var mode = new BlockoutMode();

shell.Modes.Add(mode);
shell.Modes.Activate(BlockoutMode.ModeId);

shell.Commands.Execute(BlockoutMode.ElementCommand(BlockoutElement.Face));
// mode.Element is now BlockoutElement.Face; Tab leaves the mesh and comes back into Face.
```

Attaching a mode to a pane is the application's four lines, because the two interfaces live in
assemblies that cannot see each other:

```csharp no-compile="`Vixen.Editor.App` is an executable and is not part of the packable surface"
sealed class ModeInput(EditorModes modes) : IViewportInput {
    public bool Pointer(SceneViewport pane, PointerEvent args) => modes.Active?.Pointer(args) == true;
    public bool Key(SceneViewport pane, KeyEvent args) => modes.Active?.Key(args) == true;
}
```

## See also

- [docs/plan/20 § A1](https://github.com/Rikarin/Vixen/blob/master/docs/plan/20-editor-parity.md) —
  the application frame, and why the mode bar is a structural addition rather than a toolbar section.
- [docs/plan/24 § B2](https://github.com/Rikarin/Vixen/blob/master/docs/plan/24-blockout-tools.md) —
  the blockout toolset, and the argument that a seam with one implementation is a hypothesis.
- [docs/plan/31 § Part 2](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the plan the `TerrainPlugin` example above became: `TerrainMode` ships eight tools in the
  `terrain` context. It is two modes rather than one because they filter different things — sculpt
  and paint need a terrain, foliage paints onto any surface.
- `EditorCommand`, `KeyMap` — the context mechanism a mode's key claim is built out of.
- `PluginContext.AddMode` — the extension point.
