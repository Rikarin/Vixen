---
title: Touch action
slug: ui/touch-action
kind: guide
area: Core
summary: How an element keeps a finger from scrolling the view around it — the `touch-action` property, the flags it reads as, the chain the document intersects to answer a scroll view, and why a mouse is never governed by it.
api: [T:Vixen.Ui.TouchAction]
tags: [ui, input, pointer, touch, styling, scrolling]
since: 0.2
status: preview
related: [ui/pointer-devices, ui/cursors, ui/utility-composition]
---

## What it is

`touch-action` is CSS's answer to a question every touch interface has: when a finger lands on a
slider inside a list and moves, who gets it? The user agent's default — here a `ScrollView`
dragging its content under any finger — is what the property withholds.

```vcss
.slider { touch-action: none; }      /* the finger is the slider's; the list never scrolls */
.carousel { touch-action: pan-y; }   /* a vertical swipe scrolls the page, a horizontal one is ours */
```

The cascade resolves it like any other declaration, and `UiDocument.TouchActionBetween` is the one
reader: it intersects the property over the chain from the element a finger landed on up to the one
that would scroll. ⚠ **There is no separate single-element reader, deliberately.** The property does
not inherit, so a chain that starts and ends at the same element — `TouchActionBetween(el, el)` — is
exactly the value the cascade resolved on it, and a second method spelling that was a public API
nothing called.

## What it is for

**Keeping a gesture.** A control that drags — a slider, a colour wheel, a canvas — declares `none`
and the scroll view above it declines the finger. Without the property the view took every touch
drag, and a control's only recourse was to capture the pointer on the press, which stops a *tap*
on it from scrolling too.

**Splitting the axes.** `pan-y` on a horizontal carousel means a diagonal swipe scrolls the page
straight down and the horizontal half goes nowhere; a horizontal swipe never becomes a scroll at all
and is the carousel's to take. `pan-x` is the mirror.

**Splitting a direction.** `pan-right` beside `pan-y` is swipe-to-reveal: a leftward swipe never
becomes a scroll and opens the panel, a rightward one is an ordinary scroll that may wobble back.

⚠ **The directional keywords are a test on how the gesture begins, not a boundary check.** Pointer
Events § 6 says the user agent may consider the touch "only for the purposes of scrolling that
starts in the given direction", and the recogniser has always carried that number — the slop travel
that made the press a drag, `DragEvent.TotalX`/`TotalY`. `pan-left` admits a finger travelling
*right*, because that is the gesture whose content scrolls toward the left; once admitted, the whole
axis is open for the rest of the gesture.

## Using it

Declare it in a stylesheet, or write the utility: `touch-none`, `touch-pan-x`, `touch-pan-left`,
`touch-pan-right`, `touch-pan-y`, `touch-pan-up`, `touch-pan-down`, `touch-manipulation`,
`touch-auto`.

⚠ **It does not inherit, and an ancestor's declaration still reaches the finger.** The document
intersects `touch-action` over every element from the touched one up to and including the scroll
view, which is the specification's rule — so `none` on a panel declines the scroll for any child,
and a scroll view that says `none` on itself has declined its own default.

⚠ **Touch and pen only.** A mouse drag on a view that asked for `ScrollView.DragToScroll` scrolls
straight through a `none` element. No browser lets the property stop a mouse and no author expects
it to, and `PointerEvent.PointerType` is what makes the two distinguishable at the arbitration
point — see [Pointer devices](pointer-devices.md).

⚠ **Declined is not handled.** A scroll view that declines a drag leaves the event unhandled, so it
goes on bubbling to whatever is above; an outer scroll view walks the same chain and finds the same
answer.

### What the control theme already declares

`ControlTheme.vcss` writes the property on the controls that own a finger, so a panel that puts one
of these inside a `ScrollView` needs no rule of its own:

```vcss
slider, range-slider, scrollbar, split-bar { touch-action: none; }
numeric-input { touch-action: pan-y; }
```

⚠ **Capturing the pointer is not enough, which is why these rules have to exist.** A capture
redirects the raw pointer events; the `DragEvent` the recogniser reads out of them is raised on the
pressed control and bubbles past it. Before these declarations a finger dragging a slider inside a
list moved the thumb *and* scrolled the list, and a finger on a scrollbar moved the same offset
twice.

⚠ **`numeric-input` is `pan-y` rather than `none`, and the difference is the whole point of the axis
keywords.** The scrub reads the horizontal travel and nothing else, so the vertical half of a finger's
travel is still the list's. A blanket `none` there would make a form of numeric fields unscrollable
from anywhere a finger naturally lands.

`touch-pinch-zoom` is deliberately not a utility. The keyword parses (`TouchAction.PinchZoom`), but
nothing here performs a pinch as a user-agent default, so the class would resolve and configure
nothing.

## Examples

Reading the set a scroll view would see for a press on an element:

```csharp no-compile="a fragment; `document`, `knob` and `list` are the application's own"
var allowed = document.TouchActionBetween(knob, list);

if ((allowed & TouchAction.PanY) == 0) {
    // The list will not scroll vertically from a finger on the knob.
}
```

Driving a finger from a test and asserting the view stayed put:

```csharp no-compile="a fragment; `test` is the fixture's own UiTest and `list` the ScrollView under it"
test.PointerType = PointerType.Touch;
test.MovePointer(50f, 50f);
test.PressPointer();
test.MovePointer(50f, 20f);
test.ReleasePointer();

Assert.Equal(0f, list.ScrollTop);
```

## See also

* [Pointer devices](pointer-devices.md) — the field that says whether the press came from a finger
* [Cursors](cursors.md) — what the pointer looks like, which only a device with a cursor has
