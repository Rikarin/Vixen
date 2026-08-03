---
title: Writing an editor plugin
slug: editor/writing-a-plugin
kind: guide
area: Editor
summary: What a plugin can contribute to the editor, how it registers, and how everything it added is taken back out when it unloads.
api: [T:Vixen.Editor.Core.EditorRegistry, T:Vixen.Editor.Core.IEditorRegistry, T:Vixen.Editor.Core.NewAssetKind, T:Vixen.Editor.Inspector.CustomInspector, T:Vixen.Editor.SceneView.SceneTool, T:Vixen.Editor.Plugin.IEditorPlugin, T:Vixen.Editor.Plugin.PluginContext, T:Vixen.Editor.Plugin.PluginServices]
tags: [editor, plugins, extensibility, registry]
since: 0.1
status: preview
related: [editor/index, editor/editing-pipeline, editor/modes]
---

## What it is

A plugin is a folder with a manifest and an assembly, dropped where the editor looks. One public type
implements `IEditorPlugin`; its `Activate` is handed a `PluginContext` and adds whatever the plugin
contributes.

There are two places a contribution can go, and which one depends on whether the thing already has a
registry of its own.

**`IEditorRegistry`** holds the contributions that had no owner: an entry in Create ▸ (`NewAssetKind`),
a hand-written inspector for a type (`CustomInspector`), a tool in the scene pane (`SceneTool`).
`Add` hands back the removal, and `PluginContext.Owns` takes it.

**The host's own registries** hold everything that already had a home: drawers in `DrawerRegistry`,
commands, panels, layouts and modes in `EditorShell`. `PluginContext.With` registers with one and
records how to undo it.

## What it is for

⚠ **The audit that produced this found that every built-in feature was a project reference**, so the
plugin API had never had to be sufficient and was not: a plugin could add a command, a panel, a mode
and a keybinding, and could not add an inspector, a drawer, a Create-menu entry, an importer, a
scene-view tool, a gizmo, a settings page or a preview. The registry is what closes that, and the
measure of it is a plugin built outside this repository doing four of those things at once — which is
what `OutOfTreePluginTests` is.

⚠ **A contribution kind is a record in the assembly that owns it, and nothing in the plugin contract
changes when one is added.** A method per kind on `PluginContext` would have put the whole kind list
in the contract assembly, which would mean it referencing every feature assembly that owns one — the
same shape of problem as an application that hard-references its own features, one layer down.

You do not want a plugin for something that is one project's own. A project's `Editor/` scripts are a
lighter path for that, and a plugin is what you write when the thing is shared between projects or
shipped to somebody else.

## Using it

`Activate` runs on the frame thread, after everything the plugin declared a dependency on. Throwing is
how a plugin refuses a host it cannot work with: what was registered before the throw is rolled back,
the assembly is unloaded, and the failure is reported against the plugin by name.

```csharp no-compile="a plugin assembly — the types it contributes are its own"
public sealed class SamplePlugin : IEditorPlugin {
    public void Activate(PluginContext context) {
        var registry = context.Services.Require<IEditorRegistry>();

        context.Owns(
            registry.Add(new NewAssetKind("sample.create-widget", "Widget", ".widget", "New Widget"))
        );

        context.Owns(registry.Add(new CustomInspector(typeof(Widget), DrawWidget)));
        context.Owns(registry.Add(new SceneTool("sample.paint", "Paint", new PaintTool())));

        var drawer = new WidgetDrawer();

        context.With<DrawerRegistry>(
            drawers => drawers.ForType<Widget>(drawer),
            drawers => drawers.Remove(drawer)
        );
    }
}
```

⚠ **Everything a plugin leaves behind keeps its assembly loaded**, and the runtime's answer to "this
context cannot be collected" is silence. A registration with no matching removal is a leak with no
symptom, which is why every path above pairs the two in one statement rather than trusting
`Deactivate` to remember.

⚠ **`Services.Require` rather than a static.** A plugin reaching for `DrawerRegistry.Default` writes
to a process global whatever the host intended; asking for the published one means a host running two
editors, or a test running two plugins, gets two answers instead of one shared one. `Require` fails
with a sentence naming what was missing, caught by the loader and reported as a diagnostic — rather
than as a null reference from inside the plugin's own `Activate`.

## Examples

**A Create ▸ entry that writes a starter document.** An empty file is right for a kind whose editor
opens a zero-byte file as a new document; a kind read by an *importer* wants text, because an empty
one deserialises to an asset that reports itself incomplete.

```csharp no-compile="the contribution; the extension is the plugin's own"
registry.Add(
    new NewAssetKind(
        "sample.create-widget",
        "Widget",
        ".widget",
        "New Widget",
        Contents: "size: 1",
        Opens: false
    )
);
```

**A hand-written inspector, for a type the descriptor generator never saw.** A plugin compiled outside
the solution has no analyzer, so its types have no generated rows — which is the case that most needs
this. It is handed an `EditTarget`, so it is as multi-object-aware and as undoable as a generated one
without writing any of that; see [the editing pipeline](editing-pipeline.md).

```csharp no-compile="the builder half of a CustomInspector"
static void DrawWidget(UiElement body, EditTarget target) {
    if (target.Find("Size") is { } size) {
        var slider = body.Add<Slider>();

        using (size.Refreshing()) {
            slider.Value = size.Read().Or(0f);
        }

        slider.ValueChanged += (_, value) => size.Write(value);
    }
}
```

**A scene-view tool.** It gets the pane's input before the active mode does, because it is the more
specific claim: a tool was chosen for this selection where a mode was chosen for the session.

```csharp no-compile="an IViewportInput, which is the whole of a tool's contract"
public sealed class PaintTool : IViewportInput {
    public bool Pointer(SceneViewport pane, PointerEvent args) {
        if (args.Action != PointerAction.Pressed) {
            return false;
        }

        // …paint…
        return true;
    }

    public bool Key(SceneViewport pane, KeyEvent args) => false;
}
```

## See also

* [The editor shell](index.md) — commands, panels, menus and the keymap a plugin also reaches
* [The editing pipeline](editing-pipeline.md) — what a contributed inspector or tool writes through
* [Editor modes](modes.md) — the coarser thing a `SceneTool` sits inside
