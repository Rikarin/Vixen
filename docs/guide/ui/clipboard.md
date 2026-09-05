---
title: Cut, copy and paste
slug: ui/clipboard
kind: guide
area: Core
summary: How a text control reaches the operating system's pasteboard — the seam a Core assembly is allowed to name, the one call a host makes for any of it to work, the four command ids both text controls answer, and why a field with nothing selected deliberately does not consume ⌘C.
api: [T:Vixen.Ui.IUiClipboard, T:Vixen.Platform.Ui.PlatformClipboard, T:Vixen.Platform.IClipboard]
tags: [ui, controls, clipboard, pasteboard, commands, hosting, text]
since: 0.2
status: preview
related: [ui/commands, ui/text-input, ui/desktop-application]
---

## What it is

`IUiClipboard` is the system pasteboard as a text control needs it: `HasText`, `TryGetText`,
`SetText`. It lives in `Vixen.Ui` and a document carries one:

```csharp no-compile="a fragment; `document` is the application's own"
document.Clipboard = new PlatformClipboard(platform.Clipboard);
```

⚠ **It is not `Vixen.Platform.IClipboard`, and it cannot be.** `Vixen.Ui` is a `Core/` assembly, and
doc 00's layering forbids `Core/` a reference to `Platform/` — the rule that keeps a UI framework
usable with no backend behind it at all. So the seam is declared in `Vixen.Ui` and filled in by
`Vixen.Platform.Ui`, which is the assembly that exists to join the two, beside `PlatformInput` and
`PlatformCursor`.

The platform interface carries images and arbitrary formats and all three desktop backends implement
them. This one carries text, because text is what a text control can do something with; an
application that wants the image flavours asks `IPlatform.Clipboard` directly and always could.

## What it is for

`TextField` — so `TextBox`, `TextArea`, `SearchField`, `NumericInput` — and `CodeEditor` register
four command handlers each:

| Id | Chord | What it does |
|---|---|---|
| `edit.cut` | ⌘X / Ctrl-X | Writes the selection and removes it |
| `edit.copy` | ⌘C / Ctrl-C | Writes the selection |
| `edit.paste` | ⌘V / Ctrl-V | Replaces the selection with the clipboard's text |
| `edit.select-all` | ⌘A / Ctrl-A | Selects the value |

Because they are *ids*, a `MenuItem` bound to `edit.copy` reaches whichever control has the focus
through `CommandRoute` and greys itself out when nothing answers — an application writes no
enablement rule. See [Commands](commands.md) for the route.

The chords are handled directly as well, so a field answers them in an application with no keymap
installed at all.

## Wiring a host

⚠ **One line, and its absence is the whole of what this fixed.** `IClipboard` had real backends on
macOS, Windows and Linux from Phase 1 and *no caller above `Vixen.Platform`* — so cut, copy and
paste did nothing in every Vixen text box, in every application, including the editor. The editor's
own `PropertyClipboard` and `NodeGraphClipboard` are in-process object stores that never reach the OS
pasteboard, and never claimed to.

```csharp no-compile="a fragment; a host's own `document` and `platform`"
PlatformClipboard.Install(document, platform);
```

`UiApplication` and `EditorHost` both call it. ⚠ **Both**: a wire added to one host and not the
other is silently absent from the other, which is the failure this codebase has met most often.

A platform without `PlatformCapabilities.Clipboard` leaves `Document.Clipboard` null, which is not a
failure: `HasClipboard` is false, the three verbs grey out, and `Copy`, `Cut` and `Paste` answer
`false` rather than throwing.

## Two decisions worth knowing

**A cut writes first and deletes second.** Another application can own the pasteboard and refuse the
write; a field that erased first would have thrown the text away with nowhere to get it back from. A
read-only field therefore copies and *refuses* to cut — not "cuts without deleting", which leaves the
user unable to tell which of the two happened.

⚠ **A chord that did nothing is not handled.** A field with an empty selection lets ⌘C climb to
whatever else was listening — a list that wanted to copy its selection, a document that wanted to
copy the whole of itself. Marking the chord handled regardless is how a text box silently eats an
application's own Copy for as long as an empty search field has the focus.

**A single-line field flattens a multi-line paste to spaces.** Dropping the breaks welds the last
word of one line to the first of the next, and truncating at the first break loses text the user
watched themselves copy. `TextArea` and `CodeEditor` keep the breaks, and both normalise CRLF so a
paste from a Windows editor does not leave a carriage return in a value that is later compared,
serialised and diffed.

## Testing it

Give the document a fake and drive real key events:

```csharp no-compile="a fragment; `FakeClipboard` is the test's own IUiClipboard"
fixture.Document.Clipboard = clipboard;

field.MoveCaret(0);
field.MoveCaret(4, extend: true);
fixture.Type(InputKey.C, ModifierKeys.Control);

Assert.Equal("Love", clipboard.Text);
```

The tests live in `Vixen.Ui.Controls.Tests` and `Vixen.Ui.Controls.Advanced.Tests`, which reference
no `Editor/` assembly — the point being that everything here is available to an application that has
only the control set.

## See also

* [Commands](commands.md) — the route the four ids resolve through
* [Text input and the input method](text-input.md) — how typed text reaches the same controls
* [`IClipboard`](/docs/api/vixen.platform/iclipboard) — the platform's own, with images and custom formats
