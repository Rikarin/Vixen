---
title: Contributing an importer
slug: editor/importer-contributions
kind: guide
area: Editor
summary: What a plugin gets by asking for ImporterContributions — a set of importers every registry in the process folds in, withdrawn on unload — and how the host's own Linked set differs from it.
api: [T:Vixen.Editor.Assets.ImporterContributions]
tags: [editor, assets, importers, plugins, services]
since: 0.1
status: preview
related: [editor/index, editor/writing-a-plugin, editor/content-library, editor/editor-scripts]
---

## What it is

`ImporterContributions` is a set of importers that outlives one import run. Every registry the
process builds — `BuiltInImporters.Create()`, which the editor's imports, the content build and the
`vixen` CLI all go through — folds two of them in:

| Set | Filled by | Reaches |
|---|---|---|
| `ImporterContributions.Default` | a plugin or a project script, with `Add`, withdrawn on unload | the editor, the CLI's in-process path, and an out-of-process compiler worker |
| `ImporterContributions.Linked` | each host at start-up, with `Link`, for the assemblies it links | that process only |

The editor publishes `Default` through `PluginServices`, so a plugin asks for the set rather than
reaching for the static. The registry is assembled in a fixed order: the pipeline's own importers,
then `Linked`, then `Default`, then the raw-bytes fallback.

## What it is for

Doc 36 § F8 found that importers were constructed and handed in, with no registry for a plugin to
add to. The registry had existed all along — but it is built fresh per run, inside a background task,
so that the editor and the CLI cannot disagree about the set, and every registry a plugin could have
reached was about to be thrown away. What was needed was a set that outlives a run.

⚠ **Process-wide, and it has to be.** The consumers are static factories called from background tasks
with no editor to hand them. What makes that safe is that `Add` returns the removal: a plugin's scope
disposes it on unload. An editor that shut down without withdrawing would leave an importer naming a
type in an unloaded assembly.

⚠ **`Linked` is a second set on purpose (#1300).** A feature's importer — the VFX graph's, the shader
graph's — lives beside its documents rather than in the import pipeline, so the pipeline cannot name
it; each host that links that assembly (the editor, `vixen`, the asset-compiler worker) puts it in
`Linked` before it builds a registry. `Link` is idempotent by type, because a host's start-up runs
once per editor a test harness opens, and there is nothing to withdraw: a linked assembly is in the
process for its whole life.

What it deliberately does **not** do:

- **Validate a contribution.** Two importers claiming one extension is an error the *registry* raises
  when the set is assembled, with both names in the message — refusing at `Add` would mean a plugin
  failing to load over an importer that had already been withdrawn.
- **Forward `Linked` to a compiler worker.** A worker links the same assemblies itself, so forwarding
  would hand it a second copy and two importers claiming one extension. `Default` *is* forwarded: the
  worker pool names the files its contributed importers came out of on each worker's command line,
  and the worker loads them. ⚠ The one shape that cannot cross is an importer whose assembly has no
  file — a dynamic assembly — and `CompilerPool.UnreachableImporters` lists those rather than leaving
  them to be found as an asset that imported as a byte blob.
- **Belong to a session.** Unlike `IEditorRegistry`, there is one set per process; a host running two
  editors hands both the same object.

## Using it

Contribute in `Activate`, inside the module's scope, so an unload withdraws it:

```csharp no-compile="registering it, in Activate; WidgetImporter is the plugin's"
public void Activate(PluginContext context) {
    var importers = context.Services.Require<ImporterContributions>();

    context.Owns(importers.Add(new WidgetImporter()));
}
```

A host — not a plugin — links the importers out of the assemblies it references, before anything
builds a registry. This is the editor's own start-up:

```csharp no-compile="the editor host's static constructor and its link, as EditorModules writes them"
static EditorModules() => LinkImporters(ImporterContributions.Linked);

internal static void LinkImporters(ImporterContributions linked) {
    VfxGraphImporters.Link(linked);
    ShaderGraphImporters.Link(linked);
}
```

## Examples

Hearing that an extension is claimed twice, which surfaces when a registry is built and not when the
plugin contributes:

```csharp no-compile="a fragment; the message names both importers"
try {
    var registry = BuiltInImporters.Create();
} catch (InvalidOperationException clash) {
    Console.WriteLine(clash.Message);
}
```

## See also

- [Writing a plugin](writing-a-plugin.md) — `Require`, `Owns`, and the importer a plugin ships.
- [Editor scripts](editor-scripts.md) — why a project script cannot ship an importer.
- [The content library](content-library.md) — importing files, which is what runs these.
