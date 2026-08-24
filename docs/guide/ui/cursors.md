---
title: Cursors
slug: ui/cursors
kind: guide
area: Core
summary: What `cursor: pointer` does to the pointer — the cascade's answer, the stock shape it maps to, the one call a host has to make for any of it to be visible, and why every test for it is written against the window rather than against the document.
api: [T:Vixen.Ui.UiCursor, T:Vixen.Platform.Ui.PlatformCursor, T:Vixen.Platform.CursorShape, T:Vixen.Platform.CursorMode]
tags: [ui, styling, platform, windowing, pointer, hosting]
since: 0.2
status: preview
related: [ui/utility-composition, ui/markup-panels]
---

## What it is

`cursor` is an ordinary inherited CSS property. A stylesheet says what the pointer should look like
over an element, the cascade resolves it like any other declaration, and `UiDocument.Cursor` is the
answer for wherever the pointer currently is.

```vcss
.grip { cursor: col-resize; }
.card { cursor: pointer; }
.disabled-drop { cursor: not-allowed; }
```

The utility classes are the same set: `cursor-pointer`, `cursor-col-resize`, `cursor-not-allowed`
and the fourteen others `UiCursor` has a reading of.

Three types stand between the declaration and the pointer, and they are three rather than one for a
reason worth knowing:

* **`UiCursor`** is CSS's question, in `Vixen.Ui`. A UI tree that knew about windows could only be
  shown in one, so the cascade's answer stops here as an *intent*.
* **`CursorShape`** is the platform's stock cursor, in `Vixen.Platform`. It is the operating
  system's own pointer — themed, sized and scaled by the user's settings, which a bitmap of ours
  would not be.
* **`PlatformCursor`** is the join, in `Vixen.Platform.Ui`, beside `PlatformInput`. It is the same
  seam and the other direction: input comes in through one, the cursor goes out through the other.

## What it is for

Telling the user what the thing under the pointer will do before they press it. A splitter that
shows a resize cursor is discoverable; the identical splitter without one is a two-pixel gap nobody
finds.

⚠ **The mapping is not one to one in either direction, which is why there are two enums rather than
a cast.** `cursor: col-resize` and `cursor: ew-resize` are two statements in a stylesheet and one
shape on every desktop. `grab`, `grabbing`, `progress` and `help` have no stock cursor in
`CursorShape` at all and fall back to the nearest one that does — which is what a browser does on
the platforms where they are missing. `PlatformCursor.ToShape` is that table, and it is a method with
a test rather than an arithmetic conversion because of it.

## Using it

**A host has to make one call per frame**, after the update that resolved the styles:

```csharp no-compile="a fragment of a frame loop; `windows` is the host's PlatformWindowHost"
Document.Update();
PlatformCursor.Apply(windows);
Document.Draw();
```

That is all of it. `Apply` finds the hovered element, asks which surface it is in, asks the host
which window shows that surface, and writes the shape there. It answers the window it told, or
`null` when the pointer is over nothing.

`UiApplication` and the editor's `EditorHost` both make the call, so an application built on either
needs nothing. A host with a frame loop of its own does.

⚠ **The window the *hovered* element is in, not the main one.** A document can be shown in several
windows and only one of them has the pointer over it. Writing the cursor to the main window would
give a torn-off panel the main window's arrow and the main window a resize cursor for something
nobody is over.

⚠ **After `Update`, not before.** The cursor is a computed style and the hover the pointer moved
this frame is what decides whose. Called before, it is one frame behind every pointer movement,
which is exactly the case a splitter is noticed in.

**`cursor: none` hides the pointer**, which is a `CursorMode` rather than a shape — and it is moved
only between `Normal` and `Hidden`. A game in `CursorMode.Relative` for mouse-look owns the pointer,
and an interface drawn over the top of one that dragged it back out between frames would be a camera
that stops turning while a menu is open.

⚠ **Not gated on `PlatformCapabilities.Cursor`.** That flag is about hiding, confining and relative
mode; drawing a stock cursor is `IWindow.CursorShape`, which every window implements — and which the
platforms with no pointer at all implement as a setter that does nothing. A gate there would also
make the whole path untestable, because the only platform a test can open a window on is the headless
one and it advertises `MultiWindow` and nothing else.

## Examples

**A splitter**, which is the case the property exists for. Nothing in C#:

```vcss
dock-splitter { cursor: col-resize; }
dock-splitter.horizontal { cursor: row-resize; }
```

**A whole card that is clickable**, in markup, with the utility class rather than a rule:

```vxml
@component RecentProject

<Card class="cursor-pointer" on:click="@Open">
    <TextBlock Text="@Name" />
</Card>
```

## Testing it

⚠ **Assert on the window, never on the document.** This is the transferable lesson from the year
this path did not exist: `UiDocument.Cursor` and `UiDocument.CursorOf` resolved `cursor: pointer`
correctly the whole time and nothing read either, so `cursor-*` scored *works* against a probe that
asked the framework and changed nothing a user could see. `cursor-pointer` was in exactly the same
position as `cursor-help`.

```csharp no-compile="a fragment; `document` and `host` come from the test's own fixture"
document.Dispatch(new PointerEvent { X = 10f, Y = 10f, Action = PointerAction.Moved });
PlatformCursor.Apply(host);

Assert.Equal(CursorShape.ResizeHorizontal, host.Main.CursorShape);
```

The headless platform's window stores what it is told, so the whole path — cascade, hover, surface,
window — runs in an ordinary unit test with no display server.

## See also

* [Utility composition](utility-composition.md) — where the `cursor-*` classes come from
* [`UiDocument`](/docs/api/vixen.ui/uidocument) — `Cursor` and `CursorOf`, the cascade's half
* [`IWindow`](/docs/api/vixen.platform/iwindow) — `CursorShape` and `CursorMode`, the platform's
