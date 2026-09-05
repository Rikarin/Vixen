<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Platform.Ui

The join between `Vixen.Platform` and `Vixen.Ui`. Four files, and none of them could live on
either side.

`Vixen.Ui` is a `Core/` assembly and a window is not: doc 00's layering says `Core` may not reference
`Platform`, which is what keeps a UI framework usable with no backend at all — and what leaves two
things with nowhere to be written.

## `PlatformInput`

Turns a `PlatformEvent` into the document's own events. It existed twice before this assembly did,
once in `Samples/02-HelloUi` and once in `Vixen.Editor.App`, and each copy carried a comment saying
that a `Vixen.Platform.Ui` was where it belonged once there was a second consumer. There was. There
are three now — the third is `Vixen.Ui.Desktop`, which is where the sample's copy of the *frame loop*
went for the same reason.

Two things in it are an afternoon each and are worth not re-deriving:

- The timestamps are **Stopwatch ticks**, not milliseconds. Converting by the wrong constant gives a
  gesture recogniser whose double-tap window is either eternity or nothing.
- Pointer positions are **not scaled by the DPI factor**. The platform reports logical points, which
  is the space the document is laid out in. Dividing put every click at a fraction of where it was
  made, and read as a hit-testing bug in the framework rather than an arithmetic one in the host.

Pointer and wheel events are routed to a **surface**; key and text events are not, because they go to
the focus and the focus is the document's.

## `PlatformWindowHost`

Fills `IUiWindowHost`, so that a control in `Core/` can ask for a real window without knowing what one
is. The main window is *registered* — the head made it and is presenting to it — and every other one
is opened here, each with a surface of the same document.

Three things it deliberately does not do:

- **It does not own a swapchain.** `PlatformUiWindow.Window` is public precisely so a renderer can
  build one; this assembly references no graphics at all.
- **It does not consume resize events.** The head still has a swapchain to rebuild, and a resize
  eaten here would be a window that lays out at its new size and presents at its old one.
- **It does not close the main window.** Closing the last window is the application quitting, which
  is the head's decision. A close on a torn-off window is a request, raised as `CloseRequested`, so
  that a docking host can bring the panels home first.

`TryLocate` is the small, load-bearing one: it reports where a surface's top-left corner is on the
**desktop**. Two surfaces have two coordinate spaces with nothing in common, and only the thing that
placed the windows knows where each starts — so this is what lets a tab be dragged out of one window
and dropped into another. A platform without `WindowPositioning` answers `false`, and docking degrades
to working within each window rather than to dropping panels somewhere arbitrary.

## `PlatformCursor` and `PlatformClipboard`

The two small ones, and both are the same story: a capability implemented on every desktop that
nothing above the platform layer ever called.

`PlatformCursor.Apply` turns what the cascade resolved for the hovered element into the window's
stock cursor. `UiDocument.Cursor` answered `cursor: pointer` correctly for a year with no reader, so
every `cursor-*` utility scored *works* against a probe that asked the document.

⚠ `PlatformClipboard` fills `IUiClipboard`, and its absence was worse: `IClipboard` has had real
backends on macOS, Windows and Linux since Phase 1 and **no caller above `Vixen.Platform` at all**,
so ⌘C in a Vixen text box put nothing anywhere — in every application including the editor, whose
`PropertyClipboard` is an in-process object store that never touches the OS pasteboard.
`Install(document, platform)` is the one line a head calls, and both heads — `UiApplication` and
`EditorHost` — call it. A platform without `PlatformCapabilities.Clipboard` leaves the document's
clipboard null, which greys the three verbs out rather than pretending.

## Multi-window and DPI

Both are the document's, not this assembly's: a `UiDocument` has `Surfaces`, each with its own size,
its own `DpiScale` and its own draw list, and `UiSurface`'s own remarks say why a window is a surface
rather than a document. What is here is the part that needs an operating system.
