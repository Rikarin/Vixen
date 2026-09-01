# Vixen.Editor.Plugin

The contract a third-party editor plugin is written against, and the loader that runs one.

A plugin is a folder with a `plugin.yaml` and an assembly in it. The editor finds it, checks what it
says about itself, loads its code into a collectible `AssemblyLoadContext`, and calls one method.
Everything it registers is recorded, so unloading it is undoing that record — which is what makes
"build the plugin, reload, see the change" a thing you can do without closing the project.

```yaml
# Plugins/terrain/plugin.yaml
id: com.example.terrain
name: Terrain Tools
version: 1.2.0
api: 0.1
assembly: Example.Terrain.dll
description: Sculpting brushes and a heightmap importer.
author: Example Ltd
dependencies:
  - com.example.brushes
```

```csharp
public sealed class TerrainPlugin : IEditorPlugin {
    public void Activate(PluginContext context) {
        context.AddCommand("terrain.sculpt", new StringId("terrain.command.sculpt", "Sculpt"), Sculpt);
        context.AddPanel("terrain.brushes", new StringId("terrain.panel", "Brushes"), Build);
        context.AddMenuItem(context.Shell.View, "terrain.sculpt");
    }
}
```

That is the whole of the ordinary case: no teardown, because everything above is undone for the
plugin when it is unloaded.

## Where plugins live

| | |
|---|---|
| `<project>/Plugins/<id>/` | checked in, so everybody on the team has the same tools |
| `<user data>/Plugins/<id>/` | installed by the person, for every project they open |

Searched in that order, and **the first id wins** — a project's copy overrides the user's, which is
the precedence a project-local tool manifest has and for the same reason. The copy that lost is
reported rather than dropped silently.

Both layouts doc 11 names work: the assembly beside the manifest, or under `lib/net10.0/` as a
`.nupkg` unzips it. Nothing recurses further — a plugin's own `lib/`, `runtimes/` and content are its
business, and a scan that walked into them would find the manifest of a plugin the plugin vendored.

⚠ **A folder either declares itself or is not a plugin.** An editor that loaded whatever DLLs it
found under a directory the user can write to would have an interesting security model.

## The two switches

`plugin.yaml`'s `enabled:` is the **author's**, and it lives in the plugin's own directory — which for
a project-local plugin is a file the whole team shares and for a globally installed one may not be
writable at all. `PluginHost.Suppress` is the **user's**, recorded by the editor beside their layout
and their keymap. Either alone keeps a plugin out; only the second can be undone from the plugin
manager, which says which of the two it is looking at.

⚠ **A suppressed plugin is never activated rather than activated and unloaded.** The one somebody
switched off because it broke the editor is exactly the one whose `Activate` must not run.

⚠ **`Enable` goes through `Reload`**, so it re-reads the manifest and the assembly. A plugin being
switched back on *because it has just been fixed* is the ordinary case, and a descriptor read at
start-up would load the copy that did not work.

## The four rules that make unloading work

This is the part worth reading before writing a plugin, because three of the four fail silently.

1. **Anything shared with the host resolves to the host's copy.** If the plugin's folder contains
   `Vixen.Editor.Plugin.dll` — and it will, because that is what a `dotnet build` copies — loading
   it would give the plugin an `IEditorPlugin` that is a *different type* from the host's, with the
   same name. The cast then fails with a message that reads like a lie. `PluginLoadContext` returns
   the default context's answer for every `Vixen.*` and for anything the host already has loaded.
2. **Everything registered is undone.** A command's `Run` is a lambda over the plugin's own state,
   which is a reference from the editor into the plugin's assembly. One left behind does not leak an
   entry; it leaks the whole assembly, permanently, with no error anywhere. Register through
   `PluginContext` and it is recorded; register any other way and pair it with
   `PluginContext.OnUnload`.
3. **The entry assembly is read into memory, not mapped.** `LoadFromAssemblyPath` holds the file
   open until the context is collected, so the next `dotnet build` over the folder fails to write
   the DLL it was asked to reload. Its dependencies *are* mapped — a plugin that changes a library
   beside itself still needs a restart, which is a shadow-copy feature and this is not it.
4. **The runtime says nothing when a context cannot be collected.** A plugin that left a static
   subscription behind unloads on paper, stays in memory in fact, and is not noticed until it is
   loaded a second time and its statics are not what it expected.
   `PluginHost.WaitForCollection` is what turns that silence into a warning, and `Reload Plugins`
   in the editor calls it.
5. **A coroutine a behaviour started is undone by detaching the behaviour, and by nothing else.**
   `BehaviorStore.Remove` cancels every coroutine the behaviour has suspended and unwinds them
   through their `finally` blocks before it returns — so the scheduler is no longer holding a state
   machine whose type is in the context about to be dropped. This is why `ProjectAssemblies.Unload`
   is safe to call in the same breath as the detach, with no frame in between: there is no "next
   resume point" in that sequence, and a cancellation deferred to one would never happen. An owner
   that is *not* a behaviour — a tool the plugin registered that starts coroutines of its own —
   calls `CoroutineScheduler.Cancel(this)` from its `OnUnload`, for the same reason a command's
   lambda has to be unregistered from one.

## The extension points, and which are here

Doc 11 lists eight and doc 20's A1 adds a ninth. Six are the shell's vocabulary and are
`PluginContext` methods directly, because `Vixen.Editor.Ui` is the only editor assembly this contract
references:

| | |
|---|---|
| Commands | `AddCommand` — and it is in the menu, the toolbar, the palette and the keymap at once |
| Panels | `AddPanel` — with the command that shows it, as the shell always makes them |
| Menus | `AddMenu`, `AddMenuItem` |
| Layouts | `AddLayout` |
| Keybindings | `AddDefaultBinding` |
| Modes | `AddMode` — a viewport mode, its button on the mode bar and its claim on the keymap |

⚠ **A mode is the one registration where unloading has to do something before it undoes anything.**
`EditorModes.Remove` leaves the mode if the user is in it and falls back to the first remaining one,
because a viewport whose input means a mode that is no longer loaded is not a state any gesture knows
how to be in. See [the editor modes guide](../../docs/guide/editor/modes.md).

The rest come through `PluginServices`, and there are two shapes of them.

**Contributions** — a Create ▸ entry, a hand-written inspector, a scene-view tool, and everything
doc 36 § D4 adds after them — go into `IEditorRegistry`, which hands back the removal. `Owns` takes
ownership of it:

```csharp
var registry = context.Services.Require<IEditorRegistry>();

context.Owns(registry.Add(new NewAssetKind("mine.create-thing", "Thing", ".thing", "New Thing")));
context.Owns(registry.Add(new CustomInspector(typeof(Thing), DrawThing)));
context.Owns(registry.Add(new SceneTool("mine.paint", "Paint", new PaintTool())));
```

⚠ **One method rather than one per kind, and that is a decision rather than an omission.** D4's
table names eight; a method for each would have put the whole kind list in *this* assembly, which
means this contract referencing every feature assembly that owns one. A contribution kind is a record
where it belongs, and nothing here changes when one is added.

**The host's own registries** — drawers, and whatever else the host publishes — are registered with
directly, and `With` records the undo in the same statement:

```csharp
var drawer = new HeightmapDrawer();

context.With<DrawerRegistry>(
    drawers => drawers.ForType<Heightmap>(drawer),
    drawers => drawers.Remove(drawer)
);
```

⚠ **These are not copied into the contribution registry, deliberately.** `DrawerRegistry` is where a
drawer is declared; a second place to declare one means half a plugin's drawers landing in the one
the inspector is not reading. What goes in `IEditorRegistry` is what had no owner.

⚠ **Why a lookup and not four more project references.** `Vixen.Editor.Assets` carries Assimp and a
model importer for two dozen authoring formats. A contract that referenced it would put all of that
in the build of every plugin that only wanted to add a menu item. A plugin that *does* write an
importer references that assembly itself, gets the real `IAssetImporter`, and hands the typed
registry to `Require<T>` — one weakly-typed line at the top of `Activate` and nothing after it.

What `Vixen.Editor.App` publishes today is `EditorProject`, `SceneDocument`, `DrawerRegistry` and
`IEditorRegistry`.
**Importers and build steps are not published**, and the reason is upstream rather than here:
`ContentPipeline` builds its `ImporterRegistry` per run, deliberately, so that the editor and the CLI
and the compiler workers cannot disagree about the set. A registry that outlives a run is a change to
`Vixen.Editor.Assets`.

## A built-in feature is a plugin that was not loaded from a folder

`PluginHost.Activate(id, name, module)` runs a compiled-in `IEditorPlugin` through the same
`PluginContext`, the same registration scope, the same rollback when `Activate` throws, and the same
`Unload`. What it does not do is load an assembly: a built-in is already in the default context and
will be for the life of the process, so there is no `AssemblyLoadContext` and no collectibility —
claiming otherwise would make `WaitForCollection` report a leak for every feature the editor ships.

⚠ **This exists because an API whose own authors bypass it is a guess.** Doc 36 § F2 measured the
editor hard-referencing twelve feature assemblies, which meant the plugin surface had never had to be
sufficient for anything the editor itself does. A feature that registers here is a feature holding
the door open for a third party, and one that cannot is a gap with a name.

Modules are activated before `Load`, so a third-party plugin can declare a dependency on one. They do
not take part in `PluginOrder`: a module set is chosen by the composition root in the order it wants,
where a folder of plugins is a set nobody chose.

`FindMenu` and `AddSubmenu` are what a module needs to put its verbs where they belong — the blockout
tools in Scene, the diagnostics panels in Tools. A top-level menu per feature is a menu bar that
grows a heading for every plugin somebody installs, and `IEditorMode`'s own remarks say why one that
appears and disappears with a mode is worse still.

## What the loader refuses, and when

Everything below is a `PluginDiagnostic` and a `PluginState.Failed`. **Nothing here throws for a
plugin's mistake** — an editor that will not start because of a third-party plugin is an editor whose
users learn to distrust plugins.

| Refused | When it is caught |
|---|---|
| A manifest that does not parse | Discovery, before anything is loaded |
| A manifest missing `id`, `name` or `api` — all problems at once, not the first | Discovery |
| An `api` this editor does not implement | Before a byte of its IL is mapped |
| A dependency that is not installed, and anything behind it | Before either is activated |
| A dependency cycle, named once, whole | Before any of it is activated |
| An assembly the manifest names and the folder has not got | Before the context is created |
| No `IEditorPlugin` in the assembly, or two of them with no `entryPoint` | After loading, before constructing |
| A constructor or an `Activate` that throws | Rolled back completely, then unloaded |
| A command id somebody already owns | Same — `CommandRegistry` refuses duplicates, so a plugin cannot take over `file.save` by naming it |

⚠ **A failed activation is rolled back completely.** A plugin that registered two commands and then
threw would otherwise leave both behind, pointing into an assembly nothing else refers to — half a
plugin, permanently, and a context that can never be collected.

## Versioning, which is stricter here than anywhere else in the editor

`EditorApi.Version` is the contract version, and it is deliberately **not** the package version: the
package moves for a bug fix in the loader, and conflating the two would lock out every plugin on
every patch release.

While the major is `0`, the minor is the breaking number — SemVer's own reading of `0.x`, and the
honest one for extension points that are still moving. `0.1` and `0.2` are not compatible in either
direction and the loader says which of the two things to update. After `1.0` the ordinary rule
applies: same major, and a minor no higher than the host's.

This project also carries a `PublicAPI.Shipped.txt` and is checked by `nuke CheckApi` — the only one
under `Editor/` that is, because doc 11 asks for a stricter promise here than anywhere else and a
promise nobody diffed is not one.

## Threading

Everything on `PluginContext` runs on the frame thread. The shell's registries are not thread-safe
and nothing in the editor's loop locks them; `PluginHost` does not take a lock either, because one
would be advertising a guarantee the things it writes to do not make.

A plugin doing real work puts it on `context.Shell.Tasks` — the background-task manager the importer
and the content build already use — and touches the interface from the continuation the manager
pumps.

## Tests

`Vixen.Editor.Plugin.Tests` compiles its plugins with Roslyn into a temp directory, from C# written
in the test that loads them. Deliberately not a fixture project copied into the output: a plugin has
to be a file the test can **replace between loads**, or the reload path is proved by loading a
different assembly rather than a different build of the same one. The suite asserts that an unloaded
plugin's context is actually collected, that a reload picks up a rebuild, and that a plugin whose
folder contains a copy of `Vixen.Editor.Plugin.dll` still gets the host's types.
