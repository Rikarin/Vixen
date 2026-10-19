---
title: Links out of the editor
slug: editor/external-links
kind: guide
area: Editor
summary: Two facts the editor host publishes for a module that links out — IExternalLinks opens a URL or a folder in whatever the operating system opens it with, and EditorDataDirectory says where the user's editor data lives.
api: [T:Vixen.Editor.Core.IExternalLinks, T:Vixen.Editor.Core.EditorDataDirectory]
tags: [editor, plugins, services, help]
since: 0.1
status: preview
related: [editor/index, editor/writing-a-plugin, editor/content-library, editor/player-delivery]
---

## What it is

Two things the editor host publishes through `PluginServices` that are not one of doc 50's context
services but are what a module needs to point somewhere outside the editor:

| Service | What a module gets |
|---|---|
| `IExternalLinks` | `Open(url)` — a web page, or a `file://` folder, opened by the operating system — and `CanOpen`, whether this host can open anything at all. |
| `EditorDataDirectory` | `Path`: the user's editor data directory — preferences, layouts, recents and the second plugin folder. It need not exist yet. |

The editor's own `HelpModule` is the first user of both: its five Help lines are links, and *Show Log
Folder* is the data directory as a `file://` URL. `LibraryModule`'s *Show in Explorer* opens through
the same `IExternalLinks`, and the diagnostics report starts its save picker in the data directory.

## What it is for

The Help menu, Show in Explorer and a console line's reveal all went through one private `Browse` on
the application, over the platform's URL opener (#1296). Moving the Help menu into a module meant the
module needed that one verb from the host and nothing else, so it was published; the application's
own callers go through the same object.

`EditorDataDirectory` was the one fact that kept the diagnostics report on the application (#1298):
everything else the report reads — the project, the scene, the log ring, the capture — a module could
already be handed.

⚠ **`EditorDataDirectory` is a type of its own rather than a `string`**, because services are keyed by
type: a string published once would be the only string any plugin could ever be handed, and whoever
asked for it would get this by accident.

What they deliberately do **not** do:

- **Return whether a link opened.** A link that did not open is a warning notification naming the
  link — every caller showed the same one — so `Open` says it and the caller has nothing to decide.
  `CanOpen` is for greying a menu line on a host that has no opener at all, not for asking first.
- **Create the directory.** `EditorDataDirectory.Path` is where the data goes; it may not exist on a
  first run, and a caller that writes there makes it.
- **Know about projects.** Neither is about the open project: a project's own folders are
  `EditorProject.Paths`, and opening a file *inside* the editor is
  [the documents host](documents-and-views.md)'s.

## Using it

A command that links out, greyed where nothing can open it — the shape every Help line has:

```csharp no-compile="a fragment of a module's Activate; the id, title and URL are the plugin's"
public void Activate(PluginContext context) {
    var links = context.Services.Require<IExternalLinks>();

    context.AddCommand(
        new EditorCommand("mine.docs", new StringId("mine.docs", "My Plugin's Manual"), () => links.Open("https://example.com/manual")) {
            Enablement = () => links.CanOpen
        }
    );
}
```

A module that can do without the data directory — a save picker that would only start somewhere
else — asks with `TryGet`, as the diagnostics module does:

```csharp no-compile="a fragment; the options are the platform's file dialog's"
context.Services.TryGet(out EditorDataDirectory? data);

var options = new FileDialogOptions {
    InitialDirectory = data?.Path
};
```

## Examples

Revealing the data directory, which is what *Show Log Folder* does:

```csharp no-compile="a fragment; `links` and `data` are the two services from Require"
links.Open(new Uri(data.Path).AbsoluteUri);
```

## See also

- [Writing a plugin](writing-a-plugin.md) — `Require`, `TryGet` and the registration scope.
- [The content library](content-library.md) — the Assets menu's verbs, Show in Explorer among them.
- [The player delivery service](player-delivery.md) — another service a module reaches the host through.
