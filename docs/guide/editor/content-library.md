---
title: The content library service
slug: editor/content-library
kind: guide
area: Editor
summary: What a plugin can do to the project's assets by asking for IContentLibrary — import, create, rename, move, delete, walk references, export a package, file into collections — and what the browser's model is.
api: [T:Vixen.Editor.Assets.IContentLibrary, T:Vixen.Editor.Assets.ShownContent]
tags: [editor, assets, project-browser, plugins, services]
since: 0.1
status: preview
related: [editor/index, editor/writing-a-plugin, editor/project-session, editor/scene-editing, editor/collections, editor/saved-filters]
---

## What it is

`IContentLibrary` is the editor's Content Library context as one service: the things a person does
to the project's assets, as methods. The editor publishes it through `PluginServices`, so a module
gets the same object the Assets menu, the content browser's OS drop and the Create ▸ submenu use:

| Verb | What it does |
|---|---|
| `Import(paths, folder)` | Copies files and folders from the user's disk into the project, scans, and imports. Refuses a path already inside the project, and never overwrites. |
| `Create(kind)` | Writes a `NewAssetKind`'s starter file beside the selection, scans it in, and returns its `AssetId`. |
| `Rename(asset, name)` / `Move(assets, folder)` | Renames in place or moves into a project folder, each with its `.meta` sidecar, so the GUID — and every reference to it — survives. |
| `Delete(assets)` | Deletes assets and their sidecars, **without asking**; the Assets menu's Delete asks first, with the referrers in the question. |
| `ReimportAll()` / `IsImporting` | Imports everything again on the content tasks' worker, and whether one is running. |
| `Referrers(assets)` / `Dependencies(assets)` | One step along the reference graph, each way — what Find References and Select Dependencies select. |
| `Closure(roots)` | Everything some assets reach, transitively — what a package carries. |
| `ExportPackage(path, assets)` | Writes a `.vxpackage` archive, each asset with its `.meta` sidecar. |
| `AddToCollection` / `RemoveFromCollection` / `ForgetCollection` | The project's [collections](collections.md), written to `ProjectSettings/` on every change. |
| `SaveFilter` / `ForgetFilter` | The user's [saved filters](saved-filters.md), written to their preferences. |

`Shown` is a `ShownContent`: what the browser is looking at — the folder or collection, the search,
the kind, tiles or a tree and the tile size. Three events, `Arrived`, `Rearranged` and
`CollectionsChanged`, say when something was brought in, renamed, moved or deleted, or a collection
was written.

⚠ **Rename, move and delete are not undoable**, as the menu verbs never were: they are operations on
files, not edits to a document, and no command stack records them. `Rearranged` is raised on a
refusal too, because the browser has already drawn what was typed or dropped.

## What it is for

Doc 50 § D2's rule is that a use case is a method on a service and a menu line is only a name for
it. Before this existed, import, package export and the collection verbs were private methods of the
application; a plugin that wanted "bring these files in the way a drop does" had to copy, scan and
save beside the editor — a second path that disagreed with the first about refusals, sidecars and
notifications.

⚠ **Collections and saved filters are one API over two files, on purpose.** A collection is a set
of `AssetId`s, which mean nothing in another project, so it lives in the project; a filter is a
query, which means something in every project, so it lives in the user's preferences.

⚠ **`Shown` outlives the panel, which is the point of it.** A panel's factory runs again on every
reopen, so state kept in the panel is lost when somebody closes the Project tab. The browser reads
`Shown` when it is built and writes through it. Its folder and search last the session; its view —
tiles or tree and the tile size — is read from and written to the user's preferences on every
access, because the settings window's Revert replaces the preferences object.

What it deliberately does **not** do:

- **Open anything.** `Create` returns the new asset's identity and stops; opening a document is the
  Documents context's, and needs an asset editor that claims the extension.
- **Rescan for you.** The full rescan — scan, save the index, rebuild references — is
  [`IProjectSession.Rescan`](project-session.md). `Import` and `Create` scan what they wrote.
- **Hold the asset tree.** The browser's tree is a projection of `AssetDatabase`'s flat list,
  rebuilt on demand; there is no second copy of it here.

## Using it

Ask for it in `Activate`, the way any module asks for a service:

```csharp no-compile="a fragment of a module's Activate — the paths and the menu are the plugin's"
public void Activate(PluginContext context) {
    var library = context.Services.Require<IContentLibrary>();

    context.AddCommand("mine.bring-textures", new StringId("mine.bring-textures", "Bring Textures"), () =>
        library.Import(Directory.GetFiles(@"D:\Scans", "*.png"), "Assets/Textures"));
}
```

Every refusal and every success is reported through the editor's notifications by the service, so a
caller does not report twice. `Import` returns how many paths were copied in; a refused path has
already said why.

A kind a plugin adds through `IEditorRegistry` appears in Create ▸ and is made by `Create` — the
editor's own kinds come through the same door, registered by its `LibraryModule`:

```csharp no-compile="a fragment; the extension and the starter text are the plugin's"
context.Owns(context.Services.Require<IEditorRegistry>().Add(
    new NewAssetKind("mine.create-recipe", "Recipe", ".recipe", "New Recipe", "steps: []\n")));
```

## Examples

Selecting everything that points at the selection, which is exactly what Find References runs:

```csharp no-compile="a fragment; `library` is the service from Require"
var found = library.Referrers(library.Project.Selection);

if (found.Count > 0) {
    library.Project.Selection.Set(found);
}
```

Exporting a self-contained package of a material and everything it names:

```csharp no-compile="a fragment; `material` is an AssetId the caller has"
library.ExportPackage(@"D:\Out\Rock.vxpackage", library.Closure([material]));
```

## See also

- [The project session](project-session.md) — the rescan, recents and the request to open another project.
- [Scene editing](scene-editing.md) — the same shape for the scene's entity verbs.
- [Writing a plugin](writing-a-plugin.md) — `Require`, `Owns` and the registration scope.
