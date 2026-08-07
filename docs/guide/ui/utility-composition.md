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
related: [editor/utility-styles, ui/markup-panels]
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
beyond the family that proves it.

⚠ **`--blur`, `--rotate`, `--scale` and `--translate-x`/`-y` are this shape built with the second half
missing.** They are custom properties nothing assembles, so `rotate-2` resolves, computes a value and
turns nothing. They are deliberately *not* registered as fragments, which is why the parity gate goes
on calling them inert and `InertProperties.txt` goes on recording what is owed. Giving them an
assembler is what those tasks are.

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
| `via-*` — a middle stop | ❌ refused: `BoxStyle` has a start and one end |
| `from-10%` / `to-90%` — stop positions | ❌ refused: the shader's parameter has no remap |
| `bg-radial-*`, `bg-conic-*` | ❌ no such assembler, and no shader mode |

⚠ **Refused means nothing is painted, not that the nearest supported gradient is.** A three-stop
declaration draws no gradient at all rather than a two-stop approximation of one, because a gradient
of the right two colours and the wrong shape reads as a rendering bug rather than as a missing
feature. The `background-color` underneath is unaffected — the image is a second layer over it, as in
CSS — so a refused gradient leaves a flat element and not an invisible one.

So the `from-10% … to-90%` example above composes exactly as shown and currently paints nothing. See
`GradientRefusal` for the reasons enumerated, and `docs/plan/43-web-styling-parity.md` § A11 for what
the remaining four cost — they all need the same growth in `UiShape`, so they will most likely arrive
together.

## See also

- [Utility styles](../editor/utility-styles.md) — the build step, the palette, and which families the
  engine actually reads.
- `docs/plan/43-web-styling-parity.md` — the twelve `composed` roots, the
  (a)/(b) argument in full, and what the mechanism gates.
- `UtilityFamilies`, `UtilityGenerator` — the registry a composed family is registered in, and the
  generator that writes one rule per class.
- `VarSubstitution` — where `var()` is resolved, and where an unset fragment with no fallback becomes
  a dropped declaration.
