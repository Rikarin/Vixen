---
title: Editor scripts
slug: editor/editor-scripts
kind: guide
area: Editor
summary: A .cs file in your project's Editor/ folder is compiled by the running editor and loaded like a plugin — drop it in, and its menu item is there.
api: [T:Vixen.Editor.Plugin.EditorMenuAttribute, T:Vixen.Editor.Scripts.ScriptCompiler, T:Vixen.Editor.Scripts.EditorScripts, T:Vixen.Editor.Scripts.ScriptsModule, T:Vixen.Editor.Scripts.ScriptBuild, T:Vixen.Editor.Scripts.ScriptDiagnostic, T:Vixen.Editor.Scripts.ScriptState]
tags: [editor, scripting, plugins, extensibility, roslyn]
since: 0.1
status: preview
related: [editor/writing-a-plugin, editor/inspectors-in-markup, editor/index]
---

## What it is

Put a `.cs` file in an `Editor/` folder anywhere in your project. The editor compiles it and loads
it. No project file, no build step, no restart.

```csharp no-compile="Assets/Editor/ProjectTools.cs — the whole file"
using Vixen.Editor.Plugin;

public static class ProjectTools {
    [EditorMenu("Tools/Rebuild Navigation")]
    public static void Rebuild() {
        // …
    }
}
```

Save it, and **Tools ▸ Rebuild Navigation** is in the menu bar.

## What it is for

The tools that are only about *your* project — a scene validator that knows your naming rules, a
verb that regenerates a lookup table, a menu item that fixes the thing that always needs fixing.

⚠ **You do not want a plugin for those.** A plugin is a folder with a manifest and a built assembly,
which is right for something shared between projects or shipped to somebody else, and heavy for
something one team uses. See [writing a plugin](writing-a-plugin.md) for that end.

## Using it

**Any folder called `Editor`, anywhere under the project.** The name is what matters, not the
location — so a feature can keep its editor code beside the runtime code it is about.
`Library/`, `bin/`, `obj/` and `Build/` are skipped; what a build produced is not source.

**A menu path creates whatever of itself does not exist.** `"Tools/My Thing/Do It"` makes the menu,
the submenu and the line. Two scripts naming `Tools` land in the same menu and neither has to know
the other exists.

```csharp no-compile="priority orders two lines in one menu; an id survives a rename"
[EditorMenu("Tools/Bake Lighting", Priority = 10, Id = "mygame.bake-lighting")]
public static void Bake() { … }
```

⚠ **Set `Id` for anything you want to bind a key to.** Without one the id is derived from the path,
so renaming the menu item silently drops the user's keybinding for it.

**For anything larger than a verb, write a plugin in the same folder.** It is the same interface a
packaged plugin implements and it is handed the same context, so panels, modes, custom inspectors,
Create ▸ entries and scene-view tools are all available:

```csharp no-compile="a script that is a whole plugin"
public sealed class MyTools : IEditorPlugin {
    public void Activate(PluginContext context) {
        context.AddPanel("mygame.audit", new StringId("mygame.audit", "Audit"), Build);
    }

    static void Build(DockPanel panel) { … }
}
```

## What you can call

**Whatever the editor has loaded.** There is no project file to add a reference to, so the compiler
is given the running editor's own assemblies.

⚠ **The consequence is that the set can grow between sessions.** A script that calls into an
assembly the editor only loads when a particular panel opens will compile in a session where that
panel was opened and not in one where it was not. If a script needs something exotic, it belongs in
a plugin with a `.csproj` that says so.

## What a script cannot do

**Declare an asset importer.** An importer is named by its settings type's `[DataContract]` alias,
and that alias is written by a source generator. Editor scripts are compiled without generators, so
the alias would not exist — the editor says so by name rather than letting the registration fail with
"no descriptor" about a type you did put the attribute on. [Ship it as a plugin](writing-a-plugin.md).

⚠ **The same limit applies to anything a generator writes**: a `[Component]`, a `[DataContract]`
serializer, a generated inspector descriptor. A script's own types are ordinary C# and work
everywhere the editor does not need generated metadata about them.

## When it goes wrong

**A compile error is the Editor Scripts panel**, with the file, the line and the message. The editor
keeps running.

⚠ **A failed build leaves the last working one loaded.** Halfway through typing a method name you
still have the menu you were about to use. The errors are in the panel; the tools are still there.

**Window ▸ Editor Scripts** opens the panel, and **Rebuild Editor Scripts** compiles the folder
again — which is also the fallback on a machine where the file watcher could not be opened.

## What the game build sees

Nothing. `Vixen.Sdk` excludes `**/Editor/**/*.cs` from your game's compilation, because those files
reference editor packages a game does not have.

⚠ **If you have a folder called `Editor` that holds runtime code**, set
`<VixenExcludeEditorScripts>false</VixenExcludeEditorScripts>` — and then the editor will still try
to compile it, so pick one or the other.

## See also

* [Writing an editor plugin](writing-a-plugin.md) — the packaged end of the same door
* [Inspectors in markup](inspectors-in-markup.md) — what a script's `CustomInspector` can be written as
* [The editor shell](index.md) — commands, panels, menus and the keymap
