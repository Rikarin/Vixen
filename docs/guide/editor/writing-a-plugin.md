---
title: Writing an editor plugin
slug: editor/writing-a-plugin
kind: guide
area: Editor
summary: What a plugin can contribute to the editor, how it registers, and how everything it added is taken back out when it unloads.
api: [T:Vixen.Editor.Plugin.PluginHost, T:Vixen.Editor.Blockout.BlockoutModule, T:Vixen.Editor.Terrain.TerrainModule, T:Vixen.Editor.Diagnostics.DiagnosticsModule, T:Vixen.Editor.AssetEditors.AssetEditorsModule, T:Vixen.Editor.SceneView.IActiveScene, T:Vixen.Editor.Debugger.IDeviceDeploy, T:Vixen.Rendering.Terrain.ITerrainScene, T:Vixen.Editor.Core.EditorRegistry, T:Vixen.Editor.Core.IEditorRegistry, T:Vixen.Editor.Core.NewAssetKind, T:Vixen.Editor.Inspector.CustomInspector, T:Vixen.Editor.SceneView.SceneTool, T:Vixen.Editor.Ui.TypeIcon, T:Vixen.Editor.Ui.AssetIcon, T:Vixen.Editor.Ui.EditorArt, T:Vixen.Editor.Core.AuthoringAssembly, T:Vixen.Editor.SceneView.AuthoringKind, T:Vixen.Editor.Plugin.IEditorPlugin, T:Vixen.Editor.Plugin.PluginContext, T:Vixen.Editor.Plugin.PluginServices, T:Vixen.Editor.Assets.ImporterContributions, T:Vixen.Editor.Assets.ImporterRegistry, T:Vixen.Editor.Assets.ImporterAttribute]
tags: [editor, plugins, extensibility, registry]
since: 0.1
status: preview
related: [editor/index, editor/editing-pipeline, editor/editor-scripts, editor/inspectors-in-markup, editor/modes]
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

You do not want a plugin for something that is one project's own. [A project's `Editor/`
scripts](editor-scripts.md) are a lighter path for that — no manifest, no build, no restart — and a
plugin is what you write when the thing is shared between projects or shipped to somebody else.

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

**An icon, so the asset type you added is not visibly second-class.** Two contributions, because
there are two things to name: a CLR type — a component, a behaviour, the class an asset deserialises
to — and a kind of file. An `AssetIcon`'s key is an importer tag, or an extension with its dot; the
tag is tried first, because two plugins whose asset types share an extension can only be told apart
by which importer claimed the file.

```csharp no-compile="an IconArt is a list of paths, each with its own paint"
registry.Add(
    new AssetIcon(
        ".widget",
        new IconArt(
            new IconPath(Body, IconPaint.Of(new Color4(0.44f, 0.72f, 0.94f, 1f))),
            new IconPath(Dot, IconPaint.Named("--accent"))
        )
    )
);

registry.Add(new TypeIcon(typeof(WidgetComponent), IconArt.Of(EditorIcons.Cube)));
```

⚠ **Pick the paint by whether the colour should follow a retheme, not by how many colours you
want.** `IconPaint.Foreground` is the inherited `color` and is what the editor's own chrome uses;
`IconPaint.Named("--accent")` is a custom property the cascade supplies, so a dark theme can override
it; `IconPaint.Of(colour)` is written into the icon and will not move. A set that offers only the
last looks correct in the theme it was drawn for and wrong in the other one.

⚠ **There is no `[EditorIcon("thing.svg")]`, and that is deliberate.** `Icon` takes a `PathBuilder`
rather than parsing a path string, because an icon set is compiled content — turning `"M12 2L2 22h20z"`
into segments belongs to an asset pipeline rather than to every application at start-up. Declaring
the icon is a registration, which is a line in the same `Activate` everything else here is in.

**An asset importer.** Doc 36 § F8: the registry an import runs against is rebuilt per run, so what
a plugin contributes to is a set that outlives one.

```csharp no-compile="the extensions are the attribute's; the name is the settings type's contract alias"
[Importer(".widget")]
public sealed class WidgetImporter : AssetImporter<WidgetImportSettings> {
    public override int Version => 1;

    protected override ValueTask<ImportResult> ImportAsync(
        ImportContext context, WidgetImportSettings settings, CancellationToken cancellationToken
    ) => …;
}
```

```csharp no-compile="registering it, in Activate"
var importers = context.Services.Require<ImporterContributions>();

context.Owns(importers.Add(new WidgetImporter()));
```

⚠ **Two importers claiming one extension is an error naming both**, raised when the set is assembled
rather than when you contribute — so a plugin does not fail to load because of an importer that has
already been withdrawn. Contributions are folded in after the built-ins and before the fallback.

⚠ **A contributed importer does not reach an out-of-process compiler worker.** The asset compiler
starts workers for crash isolation and each builds its own registry; a worker has not loaded your
plugin, so an asset only your importer claims fails there. In the editor, and in the CLI's in-process
path, it works.

⚠ **This is one thing a project script cannot do.** An importer is named by its settings type's
`[DataContract]` alias, which a source generator writes — and [editor scripts](editor-scripts.md) are
compiled without generators. Ship an importer as a plugin.

**Components in an assembly of your own.** If your plugin's components live in a runtime assembly the
editor never calls into, its `[ModuleInitializer]` may not have run by the time the Add ▸ menu asks
what exists — so say which assembly declares them:

```csharp no-compile="any type in the assembly will do"
registry.Add(new AuthoringAssembly(typeof(WidgetComponent)));
```

## A built-in feature is the same thing, activated by name

The editor's own features register the same way, through `PluginHost.Activate(id, name, module)` —
no assembly loading and no `AssemblyLoadContext`, because a compiled-in module is already in the
default one, but the same `PluginContext`, the same registration scope, the same rollback when
`Activate` throws and the same unload.

`BlockoutModule` is the worked example: it registers an editor mode and five submenus of the Scene
menu, and asks for the four things it needs of the host — the shared mesh-editing state, the work
plane, a mesh baker and a mesh source — through `Services.Require`.

⚠ **It exists because an API whose own authors bypass it is a guess.** A feature wired into the
application through internals proves nothing about whether a third party could have written it; one
that goes through this door proves it every build, because its assembly cannot see the application at
all.

```csharp no-compile="the composition root's half — one line per module"
plugins.Activate(BlockoutModule.ModuleId, BlockoutModule.ModuleName, new BlockoutModule());
```

`TerrainModule` is the larger one — two modes, five panels, and the session that binds them to the
scene in front of them. It needed two extension points that a contribution-shaped API does not have,
and every feature with a *mode* will want both:

* **`PluginContext.OnUpdate`** — a brush follows the entity selection, and nothing raises an event
  about that. Once a frame, on the frame thread, and not the place for work.
* **`EditorDocument.Saved`** — a scene names a heightfield and a foliage file beside itself. Saving
  one without the others leaves a project whose ground exists only in a process that has exited. It
  throws through, so a sidecar that could not be written is a failed save rather than a silent half
  of one.

⚠ **A module can be a third assembly, and sometimes has to be.** `DiagnosticsModule` joins the
profiler and the frame debugger to a project, a scene and a graphics device — and it is its own
assembly because neither of those two has ever heard of any of the three, which is what lets both be
tested against a bare `UiDocument`. Putting the joining code inside one would have bought the
registration and spent the testability.

It also shows the two shapes a host dependency takes. `IActiveScene` is **required**, because a panel
that counts entities has to count the scene being *shown* — an editor inspecting a prefab must count
the prefab. `IDeviceDeploy` is **optional**, fetched with `TryGet`: a host that cannot build a player
greys Deploy with a sentence rather than hiding the panel.

⚠ **A module contributes what a panel needs of it rather than being fetched.** The terrain module
puts an `ITerrainScene` — what ground to draw and where — into `IEditorRegistry`, and the viewport's
presenter reads it from there. Neither end names the other, and it goes away when the module does, so
a pane cannot be left drawing terrain out of an assembly that has unloaded.

⚠ **Put verbs in the menu the thing they act on already has**, with `FindMenu` and `AddSubmenu`, and
say *where* using `MenuGroup.IndexOfSubmenu` rather than a number. A module that could only append
would reorder somebody's menu the day it stopped being compiled in.

## See also

* [The editor shell](index.md) — commands, panels, menus and the keymap a plugin also reaches
* [The editing pipeline](editing-pipeline.md) — what a contributed inspector or tool writes through
* [Inspectors in markup](inspectors-in-markup.md) — writing the inspector above as a `.vxml` instead
* [Editor scripts](editor-scripts.md) — the same contributions from a loose `.cs` in a project
* [Editor modes](modes.md) — the coarser thing a `SceneTool` sits inside
