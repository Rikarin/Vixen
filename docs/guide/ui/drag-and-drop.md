---
title: Files dragged in from outside
slug: ui/drag-and-drop
kind: guide
area: Core
summary: A file dragged from Finder or Explorer onto a Vixen window arrives as a routed event at the element it was let go over, hit-tested and bubbling like a wheel — and what is deliberately not here yet is the in-app drag model, whose interesting half an OS drop cannot fill in.
api: [T:Vixen.Ui.DropEvent]
tags: [ui, input, drag-and-drop, files, platform]
since: 0.2
status: preview
related: [ui/commands, ui/accessibility, ui/desktop-application]
---

## What it is

One event. `DropEvent` is raised on the element under the point where something dragged in from
another application was let go — a path in `Files`, or a string in `Text`.

| What | Where it comes from |
|---|---|
| `Files` | The native paths that were dropped. Empty when this was text |
| `Text` | The dropped string, or `null` when this was a file |
| `X`, `Y` | Where it was let go, in the surface's space |
| `Timestamp` | The same monotonic clock every other input event is on |

⚠ **These are native paths, not virtual ones.** They come from outside anything the engine has
mounted, so they are what the operating system calls the file and do not resolve through a
`VirtualFileSystem` mount until something has imported them.

## What it is for

Dropping a texture onto a material slot, a scene onto a window, a folder onto a project. It is the
one route into an application that does not go through a file dialog, and on macOS it is the one
users reach for first.

⚠ **It was produced and dropped for as long as both halves existed.** `PlatformEventKind.DropFile`
and `DropText` are emitted by the SDL backend and by the browser backend, both are covered by their
own backends' tests, and `PlatformInput.Dispatch` had no arm for either — so every drop fell through
its `default` and dragging a file onto a window did nothing at all, on every platform, with nothing
logged. Both halves were tested; the join was neither.

## Using it

Handle it like any other routed event, on the element that should accept it:

```csharp no-compile="a fragment; `panel` is a UiElement in a document"
panel.AddHandler<DropEvent>((element, args) => {
    foreach (var path in args.Files) {
        Import(path);
    }

    args.Handled = true;
});
```

**It bubbles, and that is what makes it usable.** The element under the pointer when a file is let go
over a panel is almost never the panel — it is a label, a row background, whichever leaf the layout
happened to put there. Setting `Handled` stops it, so an inner drop target wins over the window's.

**The host does the dispatching.** An application built on `UiApplication` gets this for free; one
pumping the platform itself passes each event to `PlatformInput.Dispatch`, which turns a `DropFile`
or `DropText` into a `DropEvent` on the surface the window id names.

## What is deliberately not here yet

**An in-app drag model.** There is no `DataObject`, no `AllowDrop`, no `on:dragenter`/`dragover`, and
`DropEvent` carries both of its representations directly rather than a negotiated payload. That is
not an oversight in this event: a payload a source *offers* and a target *negotiates* — several
flavours, a preferred one, a promise resolved only if the drop is accepted — is the model an in-app
drag needs, where both ends are elements in one tree and the negotiation is the useful part. An OS
drag-in has neither end. The source is another process, the flavours were settled before this
application was involved, and what arrives is a path or a string, so a negotiated payload here would
be a type whose interesting half no producer could fill in.

⚠ **One event per file.** SDL 2 posts one `SDL_DROPFILE` per path and brackets a group with
`SDL_DROPBEGIN`/`SDL_DROPCOMPLETE`, which the desktop backend does not yet forward — so a five-file
drop arrives as five `DropEvent`s and a handler that creates a document per drop creates five.
`Files` is a list because that is the shape the grouping will arrive in, not because anything fills
it with more than one today.

⚠ **The drop position is queried, not reported.** SDL 2's drop event carries no coordinates, and
`Vector2.Zero` is not a neutral answer — it is the top-left corner, which would deliver every file in
the application to whatever sits there. The desktop backend asks the window system where the pointer
is at the moment of the drop instead; it cannot use the last motion event, because while a native
drag is in progress the window system owns the pointer and SDL delivers no motion at all.
