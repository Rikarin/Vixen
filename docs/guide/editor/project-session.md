---
title: The project session service
slug: editor/project-session
kind: guide
area: Editor
summary: What a plugin gets from IProjectSession — the open project, a full rescan, the recent list, New Project's scaffold and the request to reopen the editor over another project.
api: [T:Vixen.Editor.Core.IProjectSession, T:Vixen.Editor.Core.RecentProject]
tags: [editor, project, plugins, services, recents]
since: 0.1
status: preview
related: [editor/index, editor/writing-a-plugin, editor/content-library, editor/external-edits, engine/getting-started]
---

## What it is

`IProjectSession` is the editor's Project context as a service, published through `PluginServices`:

| Member | What it is |
|---|---|
| `Project` | The open `EditorProject`. |
| `Rescan()` | Scans the asset database, saves its index and rebuilds the reference index, then raises `Rescanned` with the `ScanReport`. |
| `Recent` | The projects this user has opened, newest first, as `RecentProject` rows — a path, when it was last opened, its folder name and whether it is still there. |
| `CreateProject(root)` | Writes the `game` template into a directory through `ProjectScaffold` and asks to reopen there. |
| `RequestProject(root)` | Asks to close this project and reopen the editor over another. |
| `PendingProject` | Where the editor will reopen, or `null`. |

## What it is for

The three calls that make a rescan used to be repeated by whoever needed them — the content browser scanned
and saved the database itself, and the disk watcher did it again when the browser was closed. They
are one method now, and the browser's Refresh, the watcher and a plugin all reach it.

⚠ **Opening another project is a request, not a call.** The editor is *rebuilt* over the new root by
the host — the same path every launch takes — rather than having a world, a scene, an asset database
and every open document swapped under it. So `RequestProject` sets `PendingProject` and asks to
close, through the same unsaved-work prompt the window's close button uses; declining the prompt
clears the request, so the *next* close does not silently act on it.

⚠ **The recent list is the user's, not the project's.** It lives in the user's data directory beside
the layout and the keymap. A row whose directory has gone is kept and reported by `Exists` rather than
pruned — a project on an unplugged drive is one somebody wants to see greyed, not forgotten.

What it deliberately does **not** do:

- **Open a project in place.** There is no `Open(path)` that returns a new project; see above.
- **Save only when the scan changed something.** `Rescan` saves the index every time, unlike
  `EditorProject.Open`: an in-editor rename changes the database in memory and then rescans, and a
  guard measuring the scan alone would leave a stale index on disk.
- **Import.** A rescan finds files; bringing files in from outside the project is
  [`IContentLibrary.Import`](content-library.md).

## Using it

```csharp no-compile="a fragment of a module's Activate"
var session = context.Services.Require<IProjectSession>();

session.Rescanned += report => Log($"{report.Assets} assets after the rescan");

context.AddCommand("mine.rescan", new StringId("mine.rescan", "Rescan Project"), () => session.Rescan());
```

A plugin listing the recent projects reads the same list File ▸ Open Recent and the startup chooser
show:

```csharp no-compile="a fragment; `menu` is the plugin's own"
foreach (var recent in session.Recent.Where(recent => recent.Exists)) {
    menu.AddItem(recent.Name).Clicked += _ => session.RequestProject(recent.Path);
}
```

## Examples

New Project from a plugin's own wizard writes what `vixen new game` writes and reopens there. A
directory that already holds some of the template's files is opened rather than overwritten, and the
editor says how many it left alone:

```csharp no-compile="a fragment; the folder is whatever the wizard chose"
session.CreateProject(@"D:\Games\Marble");
```

## See also

- [The content library](content-library.md) — import, create and the browser's model.
- [External edits](external-edits.md) — what the editor does when a file changes behind it.
- [Writing a plugin](writing-a-plugin.md) — `Require` and the registration scope.
