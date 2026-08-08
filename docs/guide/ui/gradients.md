---
title: Gradients
slug: ui/gradients
kind: guide
area: Core
summary: The three gradient shapes a box can be filled with, where their stops sit, and which space the colours are interpolated in — including why an unhinted CSS gradient is sRGB, why Tailwind's are Oklab, and why the engine's own programmatic gradients stayed linear.
api: [T:Vixen.Ui.GradientShape, T:Vixen.Ui.GradientSpace, T:Vixen.Ui.GradientStops]
tags: [ui, styling, gradients, colour, oklab, vcss, tailwind]
since: 0.2
status: preview
related: [ui/utility-composition, ui/compositing, core/gamut-mapping]
---

## What it is

Three small types that say everything about a gradient beyond its colours: `GradientShape` is
whether it runs along a line, out from the centre, or around it; `GradientStops` is where its three
stops sit along that ramp; and `GradientSpace` is which colour space the stops are interpolated in.

They live on [`BoxStyle`](../../../Core/Vixen.Ui/BoxStyle.cs), the draw list's side-buffer entry for a
box that needs more than a colour and a radius, and they are written into `UiShape` for the shader
to read.

## What it is for

A gradient has to survive three different authors without any of them being surprised.

- **CSS.** `background-image: linear-gradient(…)` is parsed into these types. All three shapes, three
  stops, arbitrary stop positions, and the `in <space>` hint are honoured; anything else is refused
  outright and the box is painted flat, because a gradient that is subtly the wrong shape is
  indistinguishable from one somebody authored badly.
- **Utilities.** `bg-linear-to-r from-accent via-muted to-surface-3` composes into exactly that CSS,
  through the `--tw-*` fragments described in [composed utilities](utility-composition.md).
- **Code.** `BoxStyle.Vertical(colour)` builds one directly, with no CSS text anywhere.

The reason there are three separate types rather than a bag of floats is `UiShape`: the record the
box shader reads is seven `Vector4`s and every lane has one meaning. A shape, a stop list and a space
are the three things that could not be inferred from the colours.

## Using it

Most of the time nothing here is named explicitly — the CSS path fills all three in.

```csharp no-compile="a fragment; `end` and `middle` are the caller's colours"
// A gradient built in code. Shape is inferred as Linear from the axis, and Space stays Linear —
// which is what the shader did before it had a choice, so no existing picture moved.
var plain = BoxStyle.Vertical(Color4.Black);

// And one that says everything.
var full = new BoxStyle(CornerRadii.Uniform(4f), end, new Vector2(1f, 0f)) {
    Shape = GradientShape.Linear,
    Space = GradientSpace.Oklab,
    GradientVia = middle,
    HasVia = true,
    Stops = new GradientStops(0.1f, 0.4f, 0.9f)
};
```

Three of those members normalise their zero when they are read, so `default(BoxStyle)` is a sensible
value: an unset `Shape` is `Linear` when there is an axis and `None` without one, and an unset
`Stops` is `GradientStops.Default` — the natural 0 / 50% / 100% ramp. `GradientStops.OrNatural()` is
that rule on its own, and it is a method rather than a property because a record's generated
`ToString` walks its instance properties and one returning its own type recurses forever.

### Which space, and why three

⚠ **This is the one decision here that changes pictures rather than describing them.** Three answers
were already in the tree and none of them was wrong on its own terms:

| Source | What it means | Why |
|---|---|---|
| A `.vcss` rule with no hint | `Srgb` | CSS's default. A hand-written rule should match a browser. |
| Anything Tailwind generates | `Oklab` | v4 writes `in oklab` on every gradient, and the engine's palette ships as v4.3.3's, quoted in `oklch`. |
| `BoxStyle.Vertical` and friends | `Linear` | No CSS text, so no hint to honour — and this is what the shader always did. |

`in srgb-linear` is CSS's name for `Linear`, so the engine's own behaviour is still spellable from a
stylesheet.

The three separate visibly, and only at the midpoint. A black-to-white ramp is 0.5 linear in
`Linear`, 0.214 linear in `Srgb`, and 0.125 linear in `Oklab` — and between two complements the
difference is a colour against a grey dead zone, which is exactly the uniformity an `oklch` palette
is chosen for.

### What is refused

Painted: all three shapes, two or three stops, stop positions inside or outside the box, and
`in srgb` / `in srgb-linear` / `in oklab`.

Refused, and the box painted flat: a fourth stop, `repeating-*-gradient()`, a polar interpolation
space (`in oklch`, `longer hue`), a stop position that is a length or a `calc()`, and an explicit
centre or ending shape on a radial or conic gradient. The last of those is the trade that let the
whole feature fit: CSS's defaults are *at center* with an extent that is a function of the box, so
the common case needs no centre in the record at all.

## Examples

```css
/* All three shapes, from a stylesheet. */
.fade   { background-image: linear-gradient(to right in oklab, #4f7cff 0%, #1f1f26 100%); }
.halo   { background-image: radial-gradient(in oklab, #4f7cff, transparent); }
.dial   { background-image: conic-gradient(from 45deg in oklab, #4f7cff, #8a8a99, #4f7cff); }

/* A ramp that is flat for the first 40% and the last 40%. */
.band   { background-image: linear-gradient(to bottom, #ff0000 40%, #0000ff 60%); }
```

```html
<!-- And the same first one, composed. -->
<div class="bg-linear-to-r from-accent to-surface-3"></div>
<div class="bg-radial from-accent to-transparent"></div>
```

## See also

- [Composed utilities](utility-composition.md) — how `from-*`, `via-*` and `to-*` become one gradient.
- [Gamut mapping](../core/gamut-mapping.md) — what happens to a stop outside the surface's gamut.
- `docs/plan/43-web-styling-parity.md` § A11 — the measurement this was built from.
