---
title: Undo
slug: ui/undo
kind: guide
area: Core
summary: A control finds an undo manager on the way up rather than owning one — what the seam is, where a manager is installed, why a run of typing is one entry and not one per keystroke, and why a text field that finds nothing deliberately lets ⌘Z go past it.
api: [T:Vixen.Ui.IUndoManager, T:Vixen.Ui.UndoManager]
tags: [ui, controls, undo, editing, text, commands]
since: 0.2
status: preview
related: [ui/text-input, ui/commands, ui/clipboard]
---

## What it is

`IUndoManager` is two delegates per edit and four questions:

```csharp no-compile="a fragment; `field` and `before` are the caller's own"
manager.Register("Typing", () => field.Value = before, () => field.Value = after);
```

`CanUndo`, `CanRedo`, `Undo()`, `Redo()` — and `IsPerforming`, which is the one nobody expects to
need. ⚠ Undoing **re-runs** the code that made the edit, so a registrant that did not check would
record the undo as a new edit and the second ⌘Z would put the text back rather than going further
into the past. The guard lives in the manager, so every implementation answers it and the check is in
one place instead of in every control.

## What it is for

`CodeBuffer` argues, correctly, that a text control must not own an undo stack: undo has to interleave
with everything else an application does — a rename that touched three files, a refactor, a move — and
a stack inside a control can only ever undo typing.

⚠ **What that argument does not settle is where a control should *look*, and until this existed the
answer was nowhere.** `git grep "IUndo\|UndoManager"` returned no hits in the repository, so a
dialog's text box had no ⌘Z in any Vixen application, the editor included.

AppKit resolves it exactly: `NSResponder.undoManager` walks the chain, so a control *finds* a manager
rather than owning one. Here that is `UiElement.FindUndoManager()` — this element, then its ancestors,
then `UiDocument.UndoManager`.

```csharp no-compile="a fragment; `panel` is a view that owns a document object"
panel.UndoManager = document.Edits;
```

Everything inside `panel` now registers with that document's stack rather than with the application's.

## Installing one

`UiApplication` puts a `UndoManager` on the document before `UiApplicationOptions.Configure` runs, so a
plain dialog text box is undoable in a program that has no documents at all, and an application with
its own stack replaces it in `Configure`.

⚠ **Null is a real answer and is the important case.** A field that finds no manager registers nothing
and leaves ⌘Z **unhandled**, so the chord climbs to whatever else was listening. That is what stops a
text box from shadowing an application's own Undo for as long as it has the focus — which, for a
search field, is most of the time.

## Coalescing, which is what makes ⌘Z useful

A run of typing is one entry. ⚠ **Decided by shape, not by a clock**: a wall-clock typing window
calibrated on an idle machine is this repository's largest flake source. Two keystrokes are one edit
when the second inserted where the first ended, with nothing selected and no line broken. Anything
else — a delete, a paste, a caret move, a newline — starts a fresh entry.

Undo restores the **selection** as well as the value and the caret. An undo of a cut that leaves the
user to re-select what came back is an undo that only half happened.

## What is still owed

`Editor/Vixen.Editor.Core/CommandStack.cs` **is** an `IUndoManager` now, so an edit a control
registered and an edit a command made are one history and ⌘Z steps back through both in the order
they happened. ⚠ `Register` is the opposite of `Execute`: the edit has already been applied, so it is
recorded and not run — and it is ignored inside a transaction, whose entry is built out of commands
the transaction ran itself.

⚠ **What is still owed is the install.** Nothing sets `UiDocument.UndoManager` or
`UiElement.UndoManager` to a document's stack, so a text field in the editor still finds nothing and
still leaves ⌘Z to the editor's global `edit.undo`. That wants the panel hosting the active document
to set its own `UndoManager` — which is a real feature rather than a line, because the active
document changes as the user switches tabs. `CodeEditor` registers nothing either: it has the
`CodeBuffer.Changed` seam and a consumer in `CodeDocument`, so wiring it is that document's call.

## See also

- [Text input and the input method](text-input.md) — the editing keymap ⌘Z and Ctrl-Y resolve through
- [Commands](commands.md) — `edit.undo` and `edit.redo` as ids an application's menu binds
- `Core/Vixen.Ui.Controls.Advanced/CodeBuffer.cs` — the argument this seam is the missing half of
