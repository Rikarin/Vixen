---
title: Pointer devices
slug: ui/pointer-devices
kind: guide
area: Core
summary: How a handler finds out whether the press it is holding came from a finger, a mouse or a stylus — the field that says so, why its default is `Unknown` rather than `Mouse`, and why the pointer id cannot answer the question.
api: [T:Vixen.Ui.PointerType]
tags: [ui, input, pointer, touch]
since: 0.2
status: preview
related: [ui/cursors]
---

## What it is

`PointerEvent.PointerType` says what kind of device produced the event: `Mouse`, `Touch`, `Pen`, or
`Unknown` when the producer did not say.

```csharp no-compile="a fragment; `args` is the handler's own PointerEvent"
if (args.PointerType == PointerType.Touch) {
    // A finger. There is no cursor to follow and no hover to inherit.
}
```

Every event a platform produces carries it. `PlatformInput` sets `Mouse` on the mouse arms and
`Touch` on the touch arms, and the `Entered` and `Exited` crossings the document works out for
itself carry forward whichever device caused them.

## What it is for

**Deciding what a gesture may do.** `touch-action` governs touch and nothing else — that is the
whole of what the property is. A reader that consulted it without knowing the device would apply it
to the mouse too, so `touch-action: none` on a map would stop the map responding to a *mouse* drag,
which no browser does and no author expects.

**Hover.** A finger produces no hover. A control that dims itself on `:hover` is showing a state a
touch user can never leave, and the device is what separates the two.

**Hit targets.** A finger is imprecise and a stylus is not, so a control that wants to grow its
target under a finger has to be able to ask.

⚠ **The pointer id cannot answer any of these.** `PlatformInput` numbers the mouse zero and fingers
one upwards, which is a *collision-avoidance* measure — without it a finger and the mouse would be
the same pointer to `GestureRecognizer`, and a press would be closed by the other device's release.
It is not a device taxonomy: a second mouse, or a host that numbers its pens, breaks any reader that
inferred the device from the number.

## Using it

Read it on the event. There is nothing to enable and nothing to configure.

⚠ **`Unknown` is zero, and that is deliberate rather than tidy.** The value exists to be trusted at
an arbitration point, so an unset field has to read as "nobody said" rather than as an answer. A
default of `Mouse` would make every producer that has not been updated *claim* to be a mouse, which
is precisely the failure a default of this kind cannot be allowed to have. So a reader that cares
tests for the device it means, and never for the absence of another one.

```csharp no-compile="a fragment; `args` is the handler's own PointerEvent"
// Right: names the device it is about.
var coarse = args.PointerType == PointerType.Touch;

// Wrong: `Unknown` is not a mouse, and this treats it as one.
var fine = args.PointerType != PointerType.Touch;
```

A test harness is not an unset field: `UiTest.PointerType` defaults to `Mouse`, because a harness
knows what it is driving. Set it to `Touch` for the span of a test that means a finger.

## Examples

Growing a hit target for a finger, and leaving it alone for a cursor:

```csharp no-compile="a fragment; `row` is the application's own element"
row.AddHandler<PointerEvent>((element, args) => {
    if (args.Action != PointerAction.Pressed) {
        return;
    }

    var padding = args.PointerType switch {
        PointerType.Touch => "12px",
        PointerType.Pen => "2px",
        _ => "4px"
    };

    element.SetStyle("padding", padding);
});
```

Driving a touch press from a test:

```csharp no-compile="a fragment; `test` is the fixture's own UiTest"
test.PointerType = PointerType.Touch;
test.MovePointer(20f, 20f);
test.PressPointer();
```

## See also

* [Cursors](cursors.md) — what the pointer *looks* like, which only a device with a cursor has
