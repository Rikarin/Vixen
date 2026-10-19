---
title: Documents and views
slug: editor/documents-and-views
kind: guide
area: Editor
summary: Opening an asset is two registrations — a DocumentKind with IDocumentHost says which files and how a document opens, a DocumentView with IWorkspace says what shows it — joined by the document's type and held together by a pairing test.
api: [T:Vixen.Editor.Core.IDocumentHost, T:Vixen.Editor.Core.DocumentHost, T:Vixen.Editor.Core.DocumentKind, T:Vixen.Editor.Core.DocumentRequest, T:Vixen.Editor.Ui.Documents.IWorkspace, T:Vixen.Editor.Ui.Documents.EditorWorkspace, T:Vixen.Editor.Ui.Documents.DocumentView, T:Vixen.Editor.Ui.Documents.DocumentView`1, T:Vixen.Editor.Ui.Documents.ITickingView, T:Vixen.Editor.SceneView.IViewportFrame, T:Vixen.Editor.Core.SettingsDocument]
tags: [editor, documents, plugins, services, panels]
since: 0.1
status: preview
related: [editor/index, editor/writing-a-plugin, ui/documents, editor/external-edits]
---

## What it is

Double-clicking an asset asks two questions, and since doc 50 § D4 (#1294) two registries answer
them:

| Question | Registered with | Lives in | Knows |
|---|---|---|---|
| Which files, and how does one open? | `IDocumentHost.Add(DocumentKind)` | `Vixen.Editor.Core` | the project and the document; no element |
| What shows the document? | `IWorkspace.Add(DocumentView)` | `Vixen.Editor.Ui` | the element; the document as `Vixen.Ui`'s `IEditableDocument` |

`IDocumentHost.TryOpen(asset, out document)` resolves the kind by extension, opens the document — or
brings forward the one already open — and raises `Opened`. `IWorkspace.Show(document, panel)` finds the
view by the document's type, builds it into the panel, makes the panel host the document, and raises
`Shown`. The editor publishes both through `PluginServices`.

## What it is for

The pair used to be one interface, `IAssetEditorFactory`, with `Open` and `CreateView` on it —
deliberately, because "a document with no view is a model nobody can reach and a view with no
document is a control with nothing behind it". That is right as a *product* rule and was wrong as a
*type*: `Open` speaks the Documents context's words and `CreateView` the Workspace's, so the interface
could live in no assembly the application did not have to reference by name. The rule is now a test,
`DocumentPairingTests`, over a running editor's registries — modules included.

What the two deliberately do **not** do:

- **`IDocumentHost` builds no element.** It knows the project and the document; what shows one is
  the workspace's. Nor does it open what is not an asset — it opens by asset id, so the project
  settings window opens its `SettingsDocument` itself.
- **`IWorkspace` knows no file.** It finds a view by the document's *type*, never by extension; which
  file becomes which document is the host's question.
- **Neither holds the panels or the layouts yet** — see Still owed.

## Using it

A kind declares every type it may open; a view is found for each by that type.

```csharp no-compile="a plugin's kind and view over a document type of its own"
sealed class NotesKind : DocumentKind {
    public override string Name => "Notes";
    public override IReadOnlyList<string> Extensions { get; } = [".notes"];
    public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(NotesDocument)];

    public override EditorDocument Open(DocumentRequest request) =>
        new NotesDocument(request.Project, request.Asset, request.Path);
}

sealed class NotesView : DocumentView<NotesDocument> {
    protected override UiElement Build(NotesDocument document, UiElement panel) {
        var view = panel.Add<NotesPanel>();
        view.Show(document);
        return view;
    }
}
```

Register both, in the module's scope, so an unload takes both out — see Examples.

## Things to know

⚠ **`DocumentTypes` is a list and the host enforces it.** One kind may open several types — the
markup kind opens a `MarkupDocument` or a `StyleSheetDocument` by extension, and has a view for each.
A kind whose `Open` returns a type it did not declare is refused: `TryOpen` closes the document and
throws, because the view lookup would otherwise fail somewhere nobody could see from the double-click.

⚠ **The type is not always enough.** The scene and the prefab kinds both open a `SceneDocument`,
which is sealed; what tells them apart is the writer each was given. So two views are registered under
one type and each overrides `Owns` to say which documents are its own. `IWorkspace.TryGetView` walks
the document's type and then its bases, and under one type takes the first view that accepts.

⚠ **A view runs again every time its panel is reopened.** Nothing durable may live in a view; what has
to survive a reopen belongs on the document.

⚠ **The panel hosts the document.** `Show` sets `UiElement.HostedDocument` and installs
`DocumentCommands`, so `document.save`, `document.revert` and `document.close` answer for the tab the
focus is in — the editor's documents are `IEditableDocument`s since #656 — and a failed write reaches
`EditorProject.SaveFailed`, which the editor shows as a notification. Ctrl+S, Ctrl+W and File ▸
Save / Revert / Close are bound to those ids (#1494), so they ask the tab the focus is in first; the
registry's commands of the same ids are the fallback for a focus in no document's panel — the
scene's save, Revert to Saved, Close Panel. ⚠ Revert in a tab asks first: the editor sets
`EditorWorkspace.Reverting`, which `Show` hands to `DocumentCommands.Install` in place of the
document's own revert.

⚠ **`Opened` is not raised for a document that was already open**, and not from the document's own
constructor, which would hand a subscriber a half-built document.

**Two contracts the application reads instead of naming a type** (#1297). A document the viewport
can draw through implements `IViewportFrame` — its expanded compositor and a `FrameChanged` — and
opening one points the panes at it. A view that moves by itself — a playing timeline, a simulating
effect — implements `ITickingView`, and the application steps every one it finds in the tree once a
frame. Both were type tests on the asset editors' own classes, and the two `case` patterns for the
VFX graph and the sequence were the names doc 36 found the compiler never reports when the
reference goes.

**The project's settings are a document too, and not an asset** (#1310). `SettingsDocument` is the
project settings window's: its rows edit through the editing pipeline onto its own stack, so Ctrl+Z
inside the window undoes a setting and undoing back to where it was applied is clean again. ⚠ It never
becomes the active document — that is what the viewport's Ctrl+Z reaches — so the window hosts its
stack as the undo manager the focus finds first, and nowhere else reaches it. A category's Reset, or
a plugin that writes a settings object and marks it, did not go through the stack, so it is the
document's `ExternallyModified`: saved by Save, asked about on close, not undone. It is opened by the
window rather than by `IDocumentHost`, which opens assets by id and the settings store is not one.

**A view that links to another asset asks `IWorkspace.Open`** — a material's "Open shader graph", a
group picked from the Addressables list — and the host puts the asset in a tab of its own.

**The arrangement's verbs are the workspace's too** (#1294). `Place` puts a document's panel in the
scene's group, `EditorWorkspace.PanelOf`/`TryReadPanel` are the asset panel id's one format,
`SaveLayout` and `ReloadStyles` are what `WorkspaceModule`'s two commands run (#1296), and
`ThemeTokens`/`ApplyThemeTokens` are the user's theme the Appearance page edits. `Recent` is the
editor's recent-documents list (#656) — `Vixen.Ui`'s `RecentDocuments`, noted for every document
`Show` builds, kept in the user's store, and File ▸ Recent Documents.

## Still owed

Opening an asset in a tab stays the host's, handed in: resolving the document is `IDocumentHost`'s
and the editor's open scenes', which this assembly cannot name. ⚠ Doc 50's other parked panel state
(§ F1) — the browser, the outliner, the inspectors, the viewport's cameras — is made of types this
assembly cannot name either, and belongs to those contexts' own models; #1294 records what is left.

## Examples

A module's `Activate`, registering a kind and its view inside its own scope, both or neither:

```csharp no-compile="a plugin's own Activate"
if (context.Services.TryGet<IDocumentHost>(out var documents)
    && context.Services.TryGet<IWorkspace>(out var workspace)) {
    context.Owns(documents.Add(new NotesKind()));
    context.Owns(workspace.Add(new NotesView()));
}
```

Hearing a document open, to hand it what only the module has:

```csharp no-compile="a module's own Activate"
var documents = context.Services.Require<IDocumentHost>();

void Bind(EditorDocument opened) {
    if (opened is NotesDocument notes) {
        notes.Glossary = glossary;
    }
}

documents.Opened += Bind;
context.OnUnload(() => documents.Opened -= Bind);
```

## See also

- [Writing a plugin](writing-a-plugin.md) — the contract both registries are reached through.
- [Documents, dirty state and save](../ui/documents.md) — the `IEditableDocument` a view is handed.
- [External edits](external-edits.md) — what happens to an open document when its file changes.
