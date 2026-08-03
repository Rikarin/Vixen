# Vixen.Editor.Scripts

A project's `Editor/` folder, compiled in the running editor and loaded like a plugin. Drop a `.cs`
file in, and its menu item is there without a restart.

[docs/plan/36](../../docs/plan/36-an-extensible-editor.md) § P5. This is the third of that document's
three producers: a source generator writes registrations at compile time, a plugin's `Activate` makes
them at load time, and this one runs when a project opens — the only one whose input is a file
somebody is typing into right now.

## The smallest thing that works

```csharp
using Vixen.Editor.Plugin;

public static class ProjectTools {
    [EditorMenu("Tools/Rebuild Navigation")]
    public static void Rebuild() { … }
}
```

One attribute, one static method, no class to derive from and no registration call. The path creates
whatever of itself does not exist, so two scripts naming `Tools` land in the same menu and neither
has to know the other exists.

For anything larger — a panel, a mode, a custom inspector, an asset kind — a script writes the same
`IEditorPlugin` a packaged plugin does and is handed the same `PluginContext`.

## A script is a plugin

That is the whole design. The compiled assembly goes into a `PluginLoadContext` and through
`PluginHost.Activate`, so it gets the registration scope, the rollback-on-throw, the diagnostics, the
plugin manager's row and the unload a plugin dropped in a folder gets.

⚠ **A script host that reimplemented any of those would be a second answer to a question that has
one** — and the one it would get wrong is the unload, which is where every leak in this part of the
editor lives.

| | |
|---|---|
| `ScriptCompiler` | finds the sources, compiles them, hands back an assembly or diagnostics with spans |
| `EditorScripts` | the build-load-unload cycle, over one `PluginHost` |
| `ScriptsModule` | the editor's side: the rebuild verb, the errors panel, the watcher |
| `EditorMenuAttribute` | in `Vixen.Editor.Plugin`, because a script has to reference it |
| `ReflectedTypes` | a `TypeDescriptor` built by reflection, so a script can declare an importer |

## Four decisions

**Roslyn in process, not `dotnet build`.** `ProjectAssemblies` shells out for the project's *game*
code because a `.csproj` is a real project with a restore, an SDK and package references only MSBuild
resolves. An `Editor/` folder is a pile of `.cs` files with no project file, referencing exactly what
the running editor has loaded — nothing for MSBuild to work out, and a second process per keystroke
would make the loop useless.

**One assembly for the whole folder, not one per file.** Scripts refer to each other; a compilation
unit per file would make that impossible for no gain. It also means one build, one unload and one
reload.

**Every `Editor/` folder under the project, wherever it is.** Unity's rule, and the reason it is a
convention rather than a location: a feature can keep its editor code beside the runtime code it is
about. `Library/`, `bin/`, `obj/` and `Build/` are skipped — what a build produced is not source.

⚠ **A failed build leaves the previous one loaded.** Somebody halfway through typing a method name
should not lose the menu they were about to use. What they get is the errors and the editor they had;
what they must not get is an editor whose tools silently vanished because of a missing semicolon.

## An importer in a script, and the one thing it needed

A game author defines an importer for their own format in `Editor/`, the imported asset appears in
the Project view, and a runtime component in `Assets/` takes a reference to it. That is the pipeline,
and it works.

The only thing standing in the way was a name. An importer is *named* by its settings type's
`[DataContract]` alias, which `TypeRegistry` answers and a generator normally writes. Everything else
follows from the same registry — `YamlSerializer` reads and writes a `.meta` through it, and
`ArtifactKey` hashes the settings as the YAML it emitted.

So `ReflectedTypes` builds the descriptor with reflection and registers it. One registration, and the
rest of the pipeline never knows the difference.

⚠ **This is only permissible because the editor is managed.** The engine is published NativeAOT and
ADR-002 is why the generator exists at all — a reflection describer in `Vixen.Core.Reflection` would
be one a runtime could reach, and the first person to reach it would get a trimmed publish that works
on their machine and not in a build. It lives here.

⚠ **It must agree with the generated one, member for member**, or a settings type moved from a script
into a plugin would change what its `.meta` says. `ReflectedTypeTests` builds both for three shipped
settings types and compares alias, member names, orders and types.

⚠ **A positional settings record does not compile** — `AssetImporter<TSettings>` constrains to
`new()`, so the C# compiler refuses it on the line the author wrote, which is a better message than
anything this assembly could produce.

## The one assembly scan in the editor, and why it is allowed here

Finding `[EditorMenu]` means enumerating an assembly's types. [ADR-002](../../docs/plan/) forbids
that as a way of building the editor, for two reasons that both hold elsewhere: a scan reads metadata
a trimmed publish has already deleted, and start-up cost grows with what is installed.

⚠ **Neither applies to an assembly the editor compiled from source seconds ago**, in a folder it is
watching, in a process that has no publish. What a project's script author cannot do is run a source
generator over a loose `.cs` file — and that is the whole of why this tier is different from the
other two. A packaged plugin has a build and therefore has the generator; it uses `IEditorPlugin`.

## What is not here

- **No incremental compilation.** A save rebuilds the folder. For a dozen files that is tens of
  milliseconds; for a project with hundreds of editor scripts it would be worth doing better, and
  nothing here measures that yet.
- **No `[CustomEditor]`-shaped attribute set.** Doc 36 § D3 describes one and P2 declined to ship it
  with nothing reading it. A script that wants a custom inspector registers a `CustomInspector`
  through its `PluginContext`, which is what the attribute would have compiled to anyway.
- **No `[Component]` and no `[Behavior]`, deliberately.** Those need `SceneComponentRegistry` and a
  binary serializer, both generated — and a component that exists only when the editor compiled a
  script is a scene a game build cannot load. Runtime code belongs in `Assets/`, where the project's
  own `.csproj` compiles it **with** the generators.
- **No editor-only exclusion from a game build.** The convention is honoured *here* — this assembly
  is never written into a build, and `Vixen.Sdk` does not compile these files — but nothing yet fails
  a build that references an `Editor/` type from runtime code.

Licensed under Apache-2.0.
