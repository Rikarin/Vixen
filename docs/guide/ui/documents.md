---
title: Documents, dirty state and save
slug: ui/documents
kind: guide
area: Core
summary: A document below the editor — dirty, name and location as signals, save and revert answered through the command route by the nearest panel that hosts one, and a window title that follows both.
api: [T:Vixen.Ui.IEditableDocument, T:Vixen.Ui.EditableDocument, T:Vixen.Ui.DocumentCommands, T:Vixen.Ui.UiWindowTitle]
tags: [ui, documents, commands, save, windows]
since: 0.2
status: preview
related: [ui/commands, ui/undo, ui/desktop-application]
---

## What it is

`UiDocument` is an element tree, its stylesheets and the four-walk pass. It is not the *user's*
document — the thing with a name, a file, unsaved changes and a prompt on close. That is
`IEditableDocument`, and an application supplies one.

| What | Type | For |
|---|---|---|
| `Name` | `IReadOnlySignal<string>` | The window title, a tab's label |
| `Location` | `IReadOnlySignal<string?>` | Where it lives. `null` means Save has to ask |
| `IsDirty` | `IReadOnlySignal<bool>` | The title's marker, whether Save is greyed, whether closing prompts |
| `Save()`, `Revert()` | `bool` | Returning `false` leaves it dirty |

**All three are signals, and that is the point.** Every surface that shows a document's state reads
one of them through the reactive graph, so there is no "raise the changed event" for a control to
forget. A `bool IsDirty` with a `Changed` event beside it — which is what the editor's own document
has — is a subscription in every consumer and a raise the producer can miss.

## Writing one

```csharp no-compile="a fragment; `File` stands in for whatever storage the application has"
sealed class TextDocument(string name, string path) : EditableDocument(name, path) {
    public string Body { get; private set; } = File.ReadAllText(path);

    public void Edit(string body) {
        Body = body;
        MarkDirty();
    }

    protected override bool OnSave() {
        File.WriteAllText(Location.Value!, Body);
        return true;
    }

    protected override bool OnRevert() {
        Body = File.ReadAllText(Location.Value!);
        return true;
    }
}
```

⚠ **`MarkDirty` is called by whoever made the edit and is never inferred.** A document knows what a
change to *it* is and the framework does not: a selection move is not an edit, and a scroll position
may or may not be one.

⚠ **A save that could not write leaves the document dirty.** `EditableDocument.Save` marks clean only
after `OnSave` said it wrote. The helpful version — marking clean regardless — turns a full disk into
a document that says it is saved and is not, and what happens next is the user closing it without a
prompt.

**`Save()` writes whether or not it is dirty; `IsDirty` is what greys the menu item.** Save As on an
unchanged document must still write, so the two are not the same question.

## Save and revert are commands

An element declares itself the host of a document, and one call makes it answer for it:

```csharp no-compile="a fragment; `panel` is a UiElement in a document"
panel.EditedDocument = text;
DocumentCommands.Install(panel);
```

⚠ **Answered through the command route rather than by a service, and that is the whole reason they
live here.** An application with two documents open in two panels has two answers to ⌘S, and the
right one is decided by where the focus is. The route already walks focus → parents → root and takes
the nearest handler, so each panel answers for its own document and nothing has to know how many
panels there are. A save routed to "the application's document" writes the wrong file, quietly.

Both are greyed while there is nothing to write, read live out of `IsDirty`.

⚠ **A greyed item does not re-enable itself when a signal changes.** Command state is *pulled* by
whatever is showing it, once per raise, so `Install` also stands up an effect that reads `IsDirty`
and calls `UiDocument.InvalidateCommands` when it moves. Without it, typing into a clean document
leaves Save greyed until something unrelated invalidates — which looks exactly like Save being
broken. The raise is coalesced to one per frame and the frame is what `Tick` opens, so a test that
only calls `Update` never sees one.

`UiElement.FindEditedDocument()` is the walk any control can make: nearest host, then the
`UiDocument`'s own, then nothing. It is walked on every ask rather than cached, for
`FindUndoManager`'s reason — a panel torn off into its own window would otherwise keep the answer
that was nearest when it was built.

## The window title

```csharp no-compile="a fragment; `window` is an IUiWindow"
using var title = UiWindowTitle.Bind(window, text, document.Effects);
```

The title becomes the document's name, with a marker in front while it is dirty. Disposing the
binding stops it following, which is what a closing window does.

⚠ **The first run is queued, not immediate.** `Effect` schedules in its constructor rather than
running there, so the title is whatever the window was opened with until the next flush — one frame
in an application, an explicit `Update` in a test.

## What is deliberately not here yet

**A close prompt.** Save / Don't Save / Cancel on closing a dirty document needs `DialogService` and
`IUiWindow.CloseRequested` joined up in the application head, and nothing does it yet;
`UiApplication.Pump` still ends the loop outright.

**A proxy icon and a recent-documents list.** Both are platform seams with no interface here.

**External-modification detection.** `EditorDocument` has it against an asset database; the
framework has no file watcher and does not want one in `Core/`.

**Undo is not owned by the document.** An element finds a manager with `FindUndoManager`, and a
document that wants to be where one lives sets `UndoManager` on the element that hosts it. Marking
clean when the undo stack returns to its loaded state is the document's own job — `MarkClean` is
public for exactly that.
