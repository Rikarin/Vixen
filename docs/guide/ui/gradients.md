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
box shader reads is nine `Vector4`s and every lane has one meaning. A shape, a stop list and a space
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

Also painted: an explicit `at <position>` on a radial or conic gradient, and the three properties
that place the layer — `background-position`, `background-size` and `background-repeat`
(`repeat`, `no-repeat`, `repeat-x`, `repeat-y`).

Refused, and the box painted flat: a fourth stop, `repeating-*-gradient()`, a polar interpolation
space (`in oklch`, `longer hue`), a stop position that is a length or a `calc()`, and an explicit
ending *shape* on a radial gradient — `circle`, `closest-side` and its three siblings, or a stated
radius. Each of those names a different ellipse from the `farthest-corner` one this engine computes,
so drawing them as farthest-corner is a ramp that ends in the wrong place. The centre is not among
them any more, because moving a farthest-corner ellipse's centre leaves it a farthest-corner
ellipse — see below.

⚠ **`background-size: auto`, `cover` and `contain` are one picture here, and that is CSS.**
Backgrounds 3 § 3.9 resolves all three against the image's intrinsic dimensions and ratio, and a
gradient has neither: `auto` is 100%, and both keywords are the positioning area. So `bg-auto`,
`bg-cover` and `bg-contain` are deliberately not registered as utilities — three classes that differ
from each other and from the default in name only. `bg-size-[<length>]` is the spelling that does
something. `background-repeat: round` and `space` are refused for a different reason: each is a
second size computed from the box rather than a flag, and `space`'s gaps are not a period the
shader's `mod` can express.

### Two frames, and why a moved centre changes the reach

`background-position` and `background-size` place a **tile** inside the border box; `at <position>`
moves the **ramp** inside that tile. They are separate lanes — `UiShape.Area` and `UiShape.Paint` —
because they are separate frames, and neither is written at all unless something said so.

⚠ **A radial gradient's reach is the farthest-*side* distance from its centre, and that is exact.**
CSS's default ending shape is `farthest-corner`: the `farthest-side` ellipse scaled to pass through
the farthest corner. Both farthest-side distances are `max(c, extent − c)` per axis, and the farthest
corner maximises each axis independently — so the corner always sits at `(fs.x, fs.y)` and the scale
is always √2, wherever the centre is. The shader already reads `length(offset / reach) / √2`, so the
reach it wants *is* the farthest-side pair, `tile + abs(centre)`, and the centred case reduces to the
half size it used before the lane existed.

⚠ **`background-repeat` is only observable beside a `background-size`.** With the tile equal to the
border box every one of its keywords draws the same picture, which is why the parity ledger carried
it as *refused, measured* until the placement lanes landed — a true measurement of a scene that could
not tell the keywords apart. The sign of `UiShape.Area.zw` is the per-axis answer: positive tiles with
a period of twice the component, negative paints one tile and clips outside it.

## Examples

```css
/* All three shapes, from a stylesheet. */
.fade   { background-image: linear-gradient(to right in oklab, #4f7cff 0%, #1f1f26 100%); }
.halo   { background-image: radial-gradient(in oklab, #4f7cff, transparent); }
.dial   { background-image: conic-gradient(from 45deg in oklab, #4f7cff, #8a8a99, #4f7cff); }

/* A ramp that is flat for the first 40% and the last 40%. */
.band   { background-image: linear-gradient(to bottom, #ff0000 40%, #0000ff 60%); }

/* An off-centre glow, and a stripe tiled across the box. */
.spot   { background-image: radial-gradient(at 25% 75% in oklab, #4f7cff, transparent); }
.stripe { background-image: linear-gradient(to right, #4f7cff, transparent);
          background-size: 12px 100%; }

/* One tile, in the bottom right, with the background colour showing everywhere else. */
.badge  { background-color: #1f1f26;
          background-image: radial-gradient(#4f7cff, transparent);
          background-size: 24px 24px;
          background-position: 100% 100%;
          background-repeat: no-repeat; }
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
