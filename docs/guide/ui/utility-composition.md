---
title: Composed utilities
slug: ui/utility-composition
kind: guide
area: Core
summary: How from-*, via-* and to-* build one gradient — utilities that set a --tw-* fragment instead of a declaration, why the cascade assembles them rather than the generator, and the initial value that stops a missing fragment erasing the whole rule.
api: [T:Vixen.Ui.Styling.Utilities.UtilityComposition]
tags: [ui, styling, utilities, tailwind, vcss, gradients, custom-properties]
since: 0.2
status: preview
related: [editor/utility-styles, ui/markup-panels, core/gamut-mapping, ui/gradients, ui/grid-layout, ui/stylesheet-diagnostics, ui/cursors]
---

## What it is

Most utilities emit a declaration. `p-4` is `padding: 16px`, and that is the whole of it.

A **composed** utility emits none. `from-accent` sets a custom property — `--tw-gradient-from` — and
nothing else; `bg-linear-to-r` is what emits `background-image`, and it builds the gradient by
referring to the fragments. Neither class does anything useful alone, and together they are one
declaration that the cascade assembles at the moment the element is styled.

```vxml
<preview class="bg-linear-to-r from-accent via-muted to-surface-3" />
```

`UtilityComposition` is the table of fragments: what each one is called, and what it is worth when
nobody set it.

## What it is for

**Utilities that have to combine.** CSS has one `background-image`, one `transform` and one
`box-shadow`, and Tailwind gives you a separate class for each part. `translate-x-2 scale-95 rotate-3`
is three classes and one `transform` property; `from-*`/`via-*`/`to-*` is three classes and one
gradient. A utility system that emits one declaration per class cannot express any of that, because
the last class written would overwrite the others.

Twelve of the 328 Tailwind roots in `docs/plan/43-web-styling-parity.md` are this shape, and
the pattern is also how v4 does transforms, `box-shadow` and filters — so the mechanism matters well
beyond the family that proves it. The translations below are the first of those to arrive.

**The translation is the second family to use this, and the first whose two halves assemble into one
property.** `translate-x-2 translate-y-4` is two classes and one `translate: 8px 16px`, built out of
`--tw-translate-x` and `--tw-translate-y`. It differs from the gradient in one way worth copying:
**both classes are assemblers**, so each emits the `translate` declaration beside its own fragment and
`translate-x-2` on its own works. Tailwind v3 required a separate `transform` class the gradient way
and dropped it in v4, because a forgotten assembler is indistinguishable from a broken utility.

**The blur is the third, and it is the one that shows what the mechanism buys before there is
anything to compose with.** `blur-2` sets `--tw-blur` and assembles `filter: blur(var(--tw-blur, 0px))`
beside it. There is exactly one function in that list today, so a plain `filter: blur(8px)` would have
been shorter — and would have to be unpicked the moment a second filter family arrives, because CSS's
`filter` is an *ordered list* and two families each writing the whole declaration is the cascade
picking one and silently dropping the other. That is precisely the failure the translations had. The
fragment's initial is `0px` rather than empty for the same reason `--tw-gradient-stops` has one: a
zero-width blur is the identity, where an empty string is not a filter at all.

⚠ **`--rotate` and `--scale` were this shape built with the second half missing, and `--blur` was the
third of them until #28.** They are custom properties nothing assembles, so `rotate-45` resolves,
computes a value and turns nothing. They are deliberately *not* registered as fragments, which is why
the parity gate goes on calling them inert and `InertProperties.txt` goes on recording what is owed.

⚠ **And two of those five were worse than unassembled: they were unspellable.** `--scale` and
`--rotate` are not CSS properties, so no engine anywhere — this one or a browser — would ever have
read them, and a reader arriving could not have closed the debt because there was nothing to read.
`scale-*` and `rotate-*` emit `scale` and `rotate` now, at Tailwind's own values (`scale-150` is
`scale: 150%`, a ratio), and remain inert for a reason that is not a missing reader: see below.

## Using it

**Writing markup, there is nothing to know** beyond the fact that an assembler is required.
`from-accent` on its own paints nothing, because no class on the element emits `background-image`.
That is the same rule as `border-accent` needing a `border` width, and it is why `bg-linear-*` reads
as the thing you are turning on.

**Adding a composed family** is two registrations. The fragment goes in `UtilityComposition` *with its
initial value*, and the families that set it and read it go in `UtilityFamilies` as usual:

```vcss
/* what `from-accent to-surface-3 bg-linear-to-r` generates */
.from-accent    { --tw-gradient-from: #4f7cff; }
.to-surface-3   { --tw-gradient-to: #1f1f26; }
.bg-linear-to-r { background-image: linear-gradient(to right,
                      var(--tw-gradient-stops,
                          var(--tw-gradient-from, transparent) var(--tw-gradient-from-position, 0%),
                          var(--tw-gradient-to, transparent) var(--tw-gradient-to-position, 100%))); }
```

⚠ **An unset custom property poisons the whole declaration, and the initial value is what stops it.**
Per CSS a `var()` that resolves to nothing and carries no fallback makes the declaration *invalid at
computed-value time*, and the property behaves as though nothing had set it —
[`VarSubstitution`](/docs/api/vixen.ui.styling/varsubstitution) implements exactly that. So the
tempting short form is a trap:

```vcss
/* ⛔ `from-red to-blue` with no `via-*` paints NOTHING, silently. */
background-image: linear-gradient(to right, var(--tw-gradient-from), var(--tw-gradient-via), var(--tw-gradient-to));
```

The web's two answers are `@property` with an `initial-value`, or a `var()` fallback chain. Vixen has
no `@property`; it has had the fallback chain since `VarSubstitution` was written. So every fragment is
declared with what it is worth unset, and is only ever mentioned through `UtilityComposition.Reference`,
which welds the two together — the bare `var(--tw-gradient-to)` is never written by hand anywhere.

**`--tw-gradient-stops`' own initial value is the two-stop list**, which is the trick worth copying: the
degraded form is what happens when nobody says otherwise, so only `via-*` has to override it, and a
missing `via` cannot be a bug because nothing had to remember to handle it.

⚠ **Composed values are never normalised.** `bg-accent` reaches the cascade as `background-color:
#4f7cff` and comes back as `rgb(79, 124, 255)`, because ExCSS parsed it. A value containing a `var()`
is left verbatim by design, so the substitution happens after the only step that would have normalised
it, and a composed gradient's stops arrive as whatever the theme wrote. Anything reading one has to
accept both spellings.

## Examples

**The decisive case, and the reason the cascade assembles this rather than the generator.** Two
classes set the same fragment under different selectors:

```vxml
<row class="bg-linear-to-r from-accent hover:from-accent-hover to-surface-3" />
```

At rest that row's `background-image` computes to `linear-gradient(to right, #4f7cff 0%, #1f1f26
100%)`, and under the pointer to `linear-gradient(to right, #6a91ff 0%, #1f1f26 100%)`. **No single
emitted declaration could be both**, so a generator that folded the fragments together when it emitted
would have to either drop the hover half — silently — or invent a rule whose selector names two classes
at once, `.bg-linear-to-r.hover\:from-accent-hover:hover`, which it cannot even enumerate until it has
seen every class in the project and which grows as assemblers × fragments × variants. Emitting the
fragments and letting the cascade choose costs one rule per class and gets both answers right.

**Leaving a fragment out degrades rather than erasing.** Each of these is a real computed value:

| Classes | `background-image` |
|---|---|
| `bg-linear-to-r from-accent via-muted to-surface-3` | `linear-gradient(to right, #4f7cff 0%, #8a8a99 50%, #1f1f26 100%)` |
| `bg-linear-to-r from-accent to-surface-3` | `linear-gradient(to right, #4f7cff 0%, #1f1f26 100%)` |
| `bg-linear-to-r from-accent` | `linear-gradient(to right, #4f7cff 0%, transparent 100%)` |
| `bg-linear-to-r` | `linear-gradient(to right, transparent 0%, transparent 100%)` |

The last row is a gradient that draws nothing, which is the right answer — a *dropped* declaration
would have been indistinguishable from having misspelt the class.

**Positions are separate classes**, so `from-accent from-10%` sets a colour and where it sits, and
either may be written without the other:

```vxml
<bar class="bg-linear-to-b from-accent from-10% to-surface-1 to-90%" />
```

⚠ **A fragment inherits.** Vixen has no `@property`, so `--tw-*` behaves like any unregistered CSS
custom property and is visible to descendants — a child carrying `bg-linear-to-r` and no stops of its
own picks up its parent's. That is correct CSS and a divergence from Tailwind, which registers these
precisely to stop the leak. Set the fragment on the same element as the assembler and it cannot bite.

## What the renderer paints today

⚠ **Composing a gradient and painting one are two different questions, and this page is about the
first.** Everything above describes what the cascade computes; `DrawListBuilder` is what turns that
into pixels, and it understands a subset:

| Composed | Paints |
|---|---|
| `bg-linear-*` with `from-*` and `to-*` | ✅ all eight directions, both colour notations |
| `bg-linear-[<angle>]` in `deg`, `turn`, `rad` or `grad` | ✅ |
| `via-*` — a middle stop | ✅ |
| `from-10%` / `to-90%` — stop positions | ✅ including positions outside the box |
| `bg-radial`, `bg-conic` | ✅ at CSS's default geometry, which is what those two classes mean |
| `bg-radial-[at_…]`, `bg-conic-<angle>`, `bg-linear-<angle>` | ✅ the centre is `UiShape.Paint`, the angle rides the axis lane |
| `bg-size-[…]`, `bg-position-[…]`, `bg-repeat`/`bg-no-repeat`/`bg-repeat-x`/`bg-repeat-y` | ✅ |
| `bg-auto`, `bg-cover`, `bg-contain` | ❌ refused — for a gradient all three *are* the positioning area |
| `bg-repeat-round`, `bg-repeat-space` | ❌ refused — a second size computed from the box, not a flag |
| `radial-gradient(circle …)`, `closest-side`, an explicit radius | ❌ refused — a different ellipse |
| `translate-x-*`, `translate-y-*` — one or both axes, in lengths or percentages | ✅ drawn, clipped and hit-tested in the new place |
| `scale-*`, `rotate-*` | ❌ refused — see below |

⚠ **A translation is the one transform an axis-aligned draw list can have, and that is why it is the
only one.** It is resolved in `UiDocument`'s accumulation pass, into the same sum that already carried
`UiElement.OffsetX` — so it lands in `AbsoluteLeft`/`AbsoluteTop`, and the draw list, the hit test and
arrow navigation all read the result rather than the property. A translated element therefore *cannot*
draw in the new place and be clickable in the old one, which is the classic way this feature goes
wrong; there is no second copy of the arithmetic to get out of step. The clip a translated element
pushes moves with it and is still a rectangle.

⚠ **It is not layout.** Per CSS Transforms 1 §3 a transform is applied after layout: a translated
element keeps the space flexbox gave it, its siblings do not move, and it may overflow anything. A
percentage is of the element's **own** border box, not its container — the opposite of every other
percentage in the box model, and what makes `-translate-x-full` the idiom for sliding a drawer exactly
its own width off the edge.

⚠ **`rotate` and `scale` are refused, and neither is waiting for a reader.** A `DrawCommand` is an
axis-aligned rectangle and the clip stack intersects rectangles. A rotated box is not a rectangle and
a rotated *clip* is not one either — the per-axis `overflow` trick of pushing one pair of edges past
the viewport works precisely because what comes out is still axis-aligned — so approximating a
rotation by its bounding box would draw a 45-point square where a 32-point one was asked for. Scale
can scale the box in four multiplications and cannot scale the picture: glyph advances are shaped at
the run's size during layout, so a scaled subtree needs re-shaping, which would make a transform
affect layout. Both need the renderer to composite a transformed subtree into an offscreen target —
the same compositor `DrawListBuilder`'s opacity remark already says is owed.

⚠ **Every assembler emits `in oklab`, which is Tailwind v4's behaviour and not CSS's default.** An
unhinted gradient is sRGB in CSS, and a `.vcss` rule that writes one gets sRGB; the composed classes
ask for a perceptual space explicitly, because the palette ships as v4.3.3's `oklch` values and
interpolating two of them anywhere else throws the uniformity away at the midpoint. See
[gradients](gradients.md).

⚠ **A gradient's centre and the box it is painted in are two different frames, and both are lanes
now.** `background-position` and `background-size` place a *tile* in the border box;
`at <position>` moves the ramp inside that tile. `UiShape.Area` carries the first and `UiShape.Paint`
the second, and neither is written at all unless something said so — with the tile equal to the box
the clip would run along the box's own antialiased edge and darken every gradient in the interface by
a pixel. That guard is also why `background-repeat` measured inert for a year: while the tile is the
box, every one of its keywords is the same picture.

⚠ **A moved radial centre changes the *reach* and not only the origin, and the closed form is
surprisingly small.** CSS's default ending shape is `farthest-corner` — the `farthest-side` ellipse
scaled to pass through the farthest corner — and because that corner maximises each axis
independently, the scale is always root two wherever the centre is. The shader's parameterisation is
already `length(offset / reach) / √2`, so the reach it wants *is* the farthest-side pair,
`tile + abs(centre)`. Storing the centre and leaving the reach alone draws a ramp that finishes early
on one side and late on the other, which reads as a gradient somebody positioned oddly.

⚠ **Refused still means nothing is painted, not that the nearest supported gradient is.** A
declaration with an explicit ending *shape* — `circle`, `closest-side`, an explicit radius — draws no
gradient at all rather than a farthest-corner approximation of one, because a gradient of the right
colours ending in the wrong place reads as a rendering bug rather than as a missing feature. The `background-color` underneath is unaffected — the image is a
second layer over it, as in CSS — so a refused gradient leaves a flat element and not an invisible
one. See `GradientRefusal` for the reasons enumerated.

## See also

- [Utility styles](../editor/utility-styles.md) — the build step, the palette, and which families the
  engine actually reads.
- `docs/plan/43-web-styling-parity.md` — the twelve `composed` roots, the
  (a)/(b) argument in full, and what the mechanism gates.
- `UtilityFamilies`, `UtilityGenerator` — the registry a composed family is registered in, and the
  generator that writes one rule per class.
- `VarSubstitution` — where `var()` is resolved, and where an unset fragment with no fallback becomes
  a dropped declaration.
