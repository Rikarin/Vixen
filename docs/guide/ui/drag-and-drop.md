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

### Driving one from the keyboard

**The pointer is one driver of a drag and the focus is the other.** Moving the focus moves the drag:
the target becomes the nearest `AllowDrop` element at or above whatever is now focused, and it hears
the same `Entered` / `Left` a pointer crossing it would have raised. **Enter drops.**

⚠ **A pointer drag is unaffected, and that is why there is no second mode.** A pointer drag does not
move the focus — the press that preceded it already did, and the source holds the capture — so
following the focus costs one null check per focus change and buys the whole keyboard gesture.

⚠ **Enter alone, and not Space.** Space is what activates the button, checkbox or row that has the
focus, so a drop bound to it would be two gestures on one key. Enter with a modifier is somebody
else's verb and is left alone. And with no drag running, Enter is not intercepted at all.

⚠ **Enter with nothing under the focus leaves the drag running.** Tabbing past the last target and
pressing Enter drops nowhere rather than cancelling: Enter is the key the user pressed to *complete*
the gesture, and Escape is already how one is abandoned.

### Getting out of one

**Escape cancels the drag**, and it is the only key the document answers before the route. A drag is a
modal gesture — the pointer is captured by its source and the application is showing feedback for it —
so while one is running Escape belongs to the drag rather than to whatever holds the focus. Offered
after the route instead, a text field or an open menu would take it and the drag would still be
running underneath. Once the drag is over, Escape reaches the focus as it always did.

⚠ **Removing the source cancels the drag too.** `DragSession.Source` is what a target reads as
`DropEvent.DragSource`, and every path off a removed element throws rather than answering — so a panel
rebuilt mid-drag (an undo, a reload, a virtualised row leaving its pool) would otherwise leave a
session naming a dead element, and the exception would land in the *target's* drop handler, which had
done nothing wrong. Either way the target is told it lost the drag, so a gap that was opened closes.

`UiDocument.CancelDrag()` is the same thing said in code, for a source that decides mid-gesture that
this is not a drag after all.

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

⚠ **A handler that wants the event says so with a typed lambda, and this page used to show a
spelling that does not compile.** An `on:` attribute is an `@` expression whose value is an `Action`,
so a bare `@Accept` binds only a *parameterless* method — a handler declared to take a `DropEvent` is
`CS1503: cannot convert from 'method group' to 'System.Action'`, and the same is true of every other
`on:` name on the page. The lambda is what carries the arguments across:

```vxml no-compile="a fragment; the handlers are the panel's own"
<drop-zone on:dragover="@((DragOverEvent args) => Highlight(args))"
           on:dragleave="@((DragOverEvent args) => Unhighlight(args))"
           on:drop="@((DropEvent args) => Accept(args))">
  <label>Drop a texture here</label>
</drop-zone>
```

```csharp no-compile="a fragment; `Import` is the application's own"
void Accept(DropEvent args) {
    foreach (var path in args.Files) {
        Import(path);
    }

    // A drop this panel took is not one an ancestor should take as well.
    args.Handled = true;
}
```

⚠ **`AllowDrop` is not an attribute a lowercase tag can set.** On a lowercase tag every
non-directive attribute becomes data a selector can match and nothing reads, so `AllowDrop="true"`
compiles, matches `[AllowDrop]` and does nothing; set it from `OnComposed` through a `ref`, or put
the handlers on a capitalised control tag, whose properties are real assignments. `on:` is a
directive and is unaffected either way. The OS drop needs none of this — it is hit-tested and bubbles
like a wheel — but an in-app drag will not stop on an element that has not opted in.

**A row dragged onto another row inside one document.** The source's three names and the target's
four are the same event stream seen from the two ends, so a reorder needs both halves and nothing
else:

```vxml no-compile="a fragment; `Row` is the panel's own model"
<row on:dragstart="@((DragEvent args) => Grab(args))"
     on:dragend="@((DragEvent args) => Release(args))"
     on:dragover="@((DragOverEvent args) => ShowLine(args))"
     on:drop="@((DropEvent args) => Reorder(args))" />
```

⚠ `on:drag` is the middle stage only — the moves between the grab and the release. A handler written
there and nowhere else compiles, runs on every move, and never sees the beginning or the end.

## What is deliberately not here yet

**A cross-process drag out.** `BeginDrag` is a drag inside one document; dragging a Vixen row *into*
Finder needs the platform's own drag session and a promise the receiving application resolves, and
neither the seam nor a backend exists.

**A drag image.** A source draws its own ghost from `UiDocument.CurrentDrag` and `UiElement.OffsetX/Y`;
nothing carries a picture for it.

⚠ **Both halves have a consumer now, and the in-app one had never had a producer at all.**
`UiDocument.BeginDrag` is what fills a `DataObject` and starts a session, and outside its own tests
the only thing in the repository that named it was a comment saying what a port *would* do — so
`AllowDrop`, the effect negotiation, the Escape that cancels and the Enter that drops had only ever
been driven by a line of C# inside an assertion. `Samples/02-HelloUi`'s Hierarchy picks its selected
rows up from `on:dragstart` and its Inspector takes them on a `<PropertyGrid AllowDrop="true">`.

⚠ **`AllowDrop` as an attribute means two different things and only one of them works.** On a
lowercase tag every non-directive attribute is inert — it becomes data a selector can match — so
`AllowDrop="true"` on `<drop-zone>` compiles, matches `[AllowDrop]` and leaves the element out of
the hit test; on a control tag it is the `[UiProperty]` itself. `on:` is a directive and is not
affected either way. `Core/Vixen.Ui.Controls.Tests/Markup/DropSheet.vxml` writes both spellings so
the difference is asserted rather than remembered.

The editor's two drags — `TreeView`'s node reordering and
`Editor/Vixen.Editor.App/AssetFieldDrop.cs` — still hit-test by hand, and the *reason* the second one
gives has expired: its remarks say a field cannot hear its own drop because "a drag belongs to the
element the press landed on for its whole life", which is exactly what `TrackDrag`'s hit test past
`Captured` was written to stop being true. Porting it is a behaviour change in three places at once —
`ProjectBrowser` would `BeginDrag` a `DataObject` instead of raising its own event, and the editor's
rule that *a field which refused a drop still consumes it* has to become a refusal the route can
express — so the note lives beside the code rather than here.

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
