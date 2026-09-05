---
title: Documents, dirty state and save
slug: ui/documents
kind: guide
area: Core
summary: A document below the editor — dirty, name and location as signals, save and revert answered through the command route by the nearest panel that hosts one, and a window title that follows both.
api: [T:Vixen.Ui.IEditableDocument, T:Vixen.Ui.EditableDocument, T:Vixen.Ui.DocumentCommands, T:Vixen.Ui.UiWindowTitle, T:Vixen.Ui.Controls.DocumentClosePrompt, T:Vixen.Ui.Controls.DocumentCloseAnswer]
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

## What it is for

An application whose windows hold something the user edits and may lose. `IEditableDocument` is the
dirty flag, the location, the save and the revert — the parts every such application writes the same
way, put where the framework can answer for them: the window title, the close prompt, and the two
commands.

⚠ **Below the editor deliberately.** `Vixen.Editor.Core` had all of this and nothing else could reach
it, so every application that was not the editor wrote it again or went without.

## Using it

Two steps and no registration: derive a document from `EditableDocument`, and set it on the view that
owns it with `HostedDocument`. Everything else — the title, the close prompt, `document.save` and
`document.revert` — reads that one property by walking up from wherever it is asked.

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

## The prompt on close

```csharp no-compile="a fragment; `application` is a UiApplication and `dialogs` its DialogService"
using var prompt = DocumentClosePrompt.Install(
    application.Document.Root,
    dialogs,
    () => application.Quit()
);
```

A close request that reaches a dirty document is refused, and Save / Don't Save / Cancel goes up in
its place. Saving writes and then calls `close`; discarding calls it without writing; cancelling and
backing out both leave the application exactly where it was. A `Save` that returns `false` — a full
disk, a read-only file — answers `DocumentCloseAnswer.SaveFailed` and does **not** close, which is
what the bool on `Save` is for.

⚠ **The document asked about is the one under the focus**, because a close request is raised there
and this walks up from `Source`. In a window with two panels and two documents, quitting asks about
the one being worked in.

⚠ **The retry re-enters the handler and "Don't Save" leaves the document dirty.** A prompt is
answered frames later, so `close` is the second ask — and the second ask meets the same dirty
document. `DocumentClosePrompt` latches over it; a hand-rolled version that does not is an
application whose Quit reopens the prompt for ever. Marking the document clean instead would be a lie
told to the window title and to `document.save`, which read the same signal.

## Examples

**Finding the document a control is inside.** The nearest one on the way up wins, so two panels
showing two documents each answer for their own and a field deep inside one needs to be told nothing:

```csharp no-compile="a fragment; `field` is a UiElement in a document"
var document = field.FindHostedDocument();
```

**Hosting one on the view that owns it.** This is the line that makes the title, the close prompt and
`document.save` work — all three read the same property:

```csharp no-compile="a fragment; `pane` and `note` are the application's own"
pane.HostedDocument = note;
```

### Working out dirty rather than announcing it

`MarkDirty()` is what an edit calls, and no application calls it from six places. A document holds a
snapshot of what it last wrote and an effect that compares the two, so a field edited back to the
saved value **cleans the document again** — which every editor does and which a hand-placed
`MarkDirty` cannot. `Samples/02-HelloUi/MaterialDocument.cs` is that shape in about forty lines.

⚠ **`MarkDirty` inside an effect is writing a signal that `DocumentCommands.Install`'s own effect is
reading, in the same flush.** That is allowed and tested — there is no cycle, because the watch reads
the model and not `IsDirty` — but it is the first thing to check if a document ever seems to settle a
frame late.

## What is deliberately not here yet

**Closing one document out of several.** `DocumentClosePrompt` asks about the document under the
focus and then hands the close to its caller, which in every host today means the application. A tab
that shuts on its own — asking about *its* document while the rest stay open — is the same three
buttons in front of a different action, and nothing offers it yet.

**A proxy icon and a recent-documents list.** Both are platform seams with no interface here.

**A window title bound to a document, from inside a component.** `UiWindowTitle.Bind` takes an
`IUiWindow`, and a document has no way to reach the window its surface is in — `UiDocument.Windows`
opens windows and does not name the main one. So the binding is the application head's to make, and
`Samples/02-HelloUi` does not make it.

**External-modification detection.** `EditorDocument` has it against an asset database; the
framework has no file watcher and does not want one in `Core/`.

**Undo is not owned by the document.** An element finds a manager with `FindUndoManager`, and a
document that wants to be where one lives sets `UndoManager` on the element that hosts it. Marking
clean when the undo stack returns to its loaded state is the document's own job — `MarkClean` is
public for exactly that.

## See also

- [Undo](../ui/undo.md) — the other thing a document owns, found by the same walk up the tree.
- [Commands and the responder chain](../ui/commands.md) — `document.save` and `document.revert` are
  ordinary commands, and grey out on the dirty flag.
- [Desktop applications](../ui/desktop-application.md) — the window title and the quit prompt, which
  are what a document is for from the host's side.
