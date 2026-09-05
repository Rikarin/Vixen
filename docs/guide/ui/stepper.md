---
title: Stepper
slug: ui/stepper
kind: guide
area: Core
summary: Stepper is a NumericInput with the two arrows on it — the buttons every other toolkit has and this control set did not. They are the field's own Nudge, so a step is proportional to the value; they are not tab stops; and they grey out at the ends of the range.
api: [T:Vixen.Ui.Controls.Stepper]
tags: [ui, controls, numbers, accessibility, vxml]
since: 0.2
status: preview
related: [ui/text-input, ui/accessibility]
---

## What it is

`Stepper` is a `NumericInput` with an up and a down arrow inside the box.

```vxml
<Stepper Minimum="1" Maximum="99" Step="1" bind:Number="@Model.Copies.Value" />
```

Everything else about it is the numeric field: the text, the caret, `Minimum`/`Maximum`, `Step`,
`RelativeStep`, `Decimals`, the Up and Down keys, and the drag-to-scrub gesture. The arrows are two
`IconButton` parts, reachable as `IncrementButton` and `DecrementButton`.

## What it is for

A number a person adjusts by one — a copy count, a page number, a font size — rather than types. The
mechanism was already here: `NumericInput.Nudge` has driven the arrow keys and the scrub since the
field was written, and there was simply nothing to press with a mouse. An application that wanted the
two small arrows had to draw them and wire them up itself.

⚠ **The field's summary and the theme both described spinners that did not exist.** `NumericInput`'s
own documentation said "with arrows, spinners and a drag to scrub it", and the control theme's
read-only rule named "`numeric-input`'s spinners" as children it applies to. Two out of three were
true. Both now say `stepper`.

## Using it

```csharp no-compile="a fragment; `panel` and the model are the caller's own"
var stepper = panel.Add<Stepper>();

stepper.Minimum = 0;
stepper.Maximum = 10;
stepper.Number = 3;
stepper.NumberChanged += (_, value) => Model.Copies = (int) value;
```

**A press is worth `Nudge(1)`, not `Number + Step`.** That matters as soon as a value is large:
`Step` is a floor and `RelativeStep` — one hundredth by default — takes over above a hundred times
it, so an arrow moves a hundred thousand lux by a thousand and a count of four by one. A stepper that
added `Step` would be an arrow that does nothing to a light. Shift multiplies the step by ten and Alt
divides it by ten, read off the click exactly as they are read off the arrow keys.

**The arrows are not tab stops.** They do what Up and Down already do in the field they are in, so
`TabIndex` is `-1` on both: Tab moves from the number to the next question, not through two buttons
first. They still take a click, and they are still in the accessibility tree with names of their own
(`Increase` and `Decrease`, from `ControlStrings`).

**They disable at the ends of the range.** `Number` is clamped, so an arrow at the end has always
done nothing; what the disabled state adds is that the person — and the screen reader — can tell.
Anything that moves `Minimum`, `Maximum`, `Number` or `ReadOnly` re-decides both arrows.

⚠ **A read-only stepper's arrows are disabled, but a read-only `NumericInput`'s arrow *keys* are
not** — that is [#826](https://github.com/Rikarin/Vixen/issues/826), and it is the field's bug rather
than this control's.

## Examples

A quantity in a form, in markup:

```vxml no-compile="a fragment; the model is the application's own"
<KeyValueList>
    <KeyValueRow Key="Copies">
        <Stepper Minimum="1" Maximum="99" bind:Number="@Model.Copies.Value" />
    </KeyValueRow>
</KeyValueList>
```

An inspector row that keeps the proportional step, which is what makes one arrow press useful across
a whole range of magnitudes:

```csharp no-compile="a fragment; `row` is the caller's own"
var intensity = row.Add<Stepper>();

intensity.Decimals = 3;
intensity.RelativeStep = 0.01;   // one press is a percent of whatever it has reached
intensity.Number = 100_000;      // daylight, in lux

// One press of the up arrow: 101 000, not 100 001.
```

Reaching a part, for a shell that wants a tooltip on one of the arrows:

```csharp no-compile="a fragment; `panel` and `stepper` are the caller's own"
var hint = panel.Add<Tooltip>();

hint.Label = "Brighter";
hint.Attach(stepper.IncrementButton);
```

## See also

* [Text input](text-input.md) — the field underneath, its `Coerce`/`Shown`/`Validate` seams and the
  numeric field's typing rules
* [Secure text input](secure-text-input.md) — the other field in this family that is one override on
  `TextField`
* [Accessibility](accessibility.md) — why the arrows carry names and a disabled state rather than
  only a picture
