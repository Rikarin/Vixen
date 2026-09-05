---
title: Drag and drop
slug: ui/drag-and-drop
kind: guide
area: Core
summary: A file dragged from Finder or Explorer arrives as a routed event at the element it was let go over; a drag that starts inside the application carries a payload offered in several formats, is addressed to the nearest element that allows drops, and negotiates what the drop would do before anything is let go.
api: [T:Vixen.Ui.DropEvent, T:Vixen.Ui.DataObject, T:Vixen.Ui.DataFormats, T:Vixen.Ui.DragOverEvent, T:Vixen.Ui.DragOverStage, T:Vixen.Ui.DragSession, T:Vixen.Ui.DropEffect]
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

## A drag that starts inside the application

The other direction has the half an OS drag-in cannot have: the payload exists *before* the button
comes up, so a target can say what it would do with it and show the user.

A source starts one from its `dragstart` handler — from `dragstart` and not from a press, because the
gesture recogniser is what decides a press has wandered far enough to be a drag rather than a wobble:

```csharp no-compile="a fragment; `row` is a UiElement in a document"
row.AddHandler<DragEvent>((element, args) => {
    if (args.Stage != DragStage.Started) {
        return;
    }

    var data = new DataObject();
    data.Set("vixen.asset-id", asset);
    data.SetText(asset.Name);

    element.Document.BeginDrag(element, data, DropEffect.Move | DropEffect.Copy);
});
```

**`DataObject` offers as many representations as the source can produce, best first.** The same row
is an asset id to a material slot, a path to a file field and its own name to a text box, and each
target asks for the one it understands. `Formats` comes back in the order they were offered, so a
target that can take several gets what the source would rather it had.

⚠ **The format names are `IClipboard`'s vocabulary; the values are not bytes.** Both ends of an
in-app drag are objects in one heap, so serialising an `AssetId` so the panel next door can parse it
back is cost with nothing bought. The names match so that a drag which one day leaves the process
needs no new vocabulary.

A target opts in once, with `AllowDrop`:

```csharp no-compile="a fragment; `slot` is a UiElement in a document"
slot.AllowDrop = true;

slot.AddHandler<DragOverEvent>((element, args) => {
    if (!args.Data.Has("vixen.asset-id")) {
        args.Effect = DropEffect.None;   // refuse this payload, stay a target
        return;
    }

    args.Effect = DropEffect.Copy;       // narrow move-or-copy to copy
    element.AddClass("would-accept");
});
```

⚠ **`DragOverEvent` arrives already accepting, unlike the DOM's `dragover`.** The web starts from a
refusal because every element is a potential target and only `preventDefault` distinguishes them;
here `AllowDrop` is already that opt-in, so a second one would mean a target that declared itself a
target and silently was not. Writing `DropEffect.None` is how a target refuses one payload.

⚠ **Enter, over and leave are addressed to the drop target, not to the hit-test result.** The element
under the pointer while a drag crosses a row is whichever label, icon or background the layout put
there; raised on each of those, enter and leave would arrive dozens of times crossing one row and a
target that opened a gap on enter would flicker it. The document walks up from the hit-test result to
the nearest ancestor with `AllowDrop` and addresses the event there — and it bubbles from there like
everything else.

⚠ **The target lookup hit-tests past `Captured`, which nothing else positional does.** A source
almost always captures the pointer when a drag starts, because that is how it keeps receiving moves
once the cursor has left it. Asking the capture where the pointer is answers "on the source", for
ever — the drag that can never be dropped anywhere.

`DropEvent.Effect` carries what the target chose, which is what a source reads to find out whether it
has to remove the original; `DropEvent.DragSource` is the element the drag started on, and `null` is
exactly the test for "this came from another application".

## The markup spelling

| Name | Event | Fires on |
|---|---|---|
| `on:dragstart`, `on:drag`, `on:dragend` | `DragEvent` | The **source**: the grab, each move, the release |
| `on:dragenter`, `on:dragover`, `on:dragleave` | `DragOverEvent` | The **target**, while a drag is passing over it |
| `on:drop` | `DropEvent` | The target, when it is let go — from either kind of drag |

⚠ **The target's four names did not exist for as long as `DropEvent` did.** A name absent from
`BuildContext`'s subscription table is an `on:` the binder rejects, so a file dragged out of Finder
was routed to an element and bubbled correctly and no `.vxml` in the tree could hear it.

## Examples

**Accepting files dropped from the desktop.** The four target names are the half that did not exist
until recently, so this is the spelling to reach for rather than a hand-rolled walk up from
`args.Source`:

```vxml no-compile="a fragment; the handlers are the panel's own"
<panel on:dragover={Highlight} on:dragleave={Unhighlight} on:drop={Accept}>
  <label>Drop a texture here</label>
</panel>
```

```csharp no-compile="a fragment; `Import` is the application's own"
void Accept(UiElement source, DropEvent args) {
    foreach (var path in args.Files) {
        Import(path);
    }
}
```

**A row dragged onto another row inside one document.** The source's three names and the target's
four are the same event stream seen from the two ends, so a reorder needs both halves and nothing
else:

```vxml no-compile="a fragment; `Row` is the panel's own model"
<row on:dragstart={Grab} on:dragend={Release} on:dragover={ShowLine} on:drop={Reorder} />
```

⚠ `on:drag` is the middle stage only — the moves between the grab and the release. A handler written
there and nowhere else compiles, runs on every move, and never sees the beginning or the end.

## What is deliberately not here yet

**A cross-process drag out.** `BeginDrag` is a drag inside one document; dragging a Vixen row *into*
Finder needs the platform's own drag session and a promise the receiving application resolves, and
neither the seam nor a backend exists.

**A drag image.** A source draws its own ghost from `UiDocument.CurrentDrag` and `UiElement.OffsetX/Y`;
nothing carries a picture for it.

**Keyboard drag and drop.** There is no way to move something without a pointer, which is the
accessibility gap this feature ships with.

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

## See also

- [Commands and the responder chain](../ui/commands.md) — the four target names are subscriptions on
  the same table the command ids use, and a name missing from it is an `on:` the binder rejects.
- [Accessibility](../ui/accessibility.md) — a drop target reachable only by pointer is a drop target
  half the users cannot use.
- [Desktop applications](../ui/desktop-application.md) — where the platform drag session is installed,
  and why a host that installs it on one of its two entry points has installed it on neither.
