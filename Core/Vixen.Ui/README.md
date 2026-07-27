# Vixen.Ui

The UI framework proper. Right now it is one thing: **the step between the cascade and layout**.

`Vixen.Ui.Styling` decides which declaration wins without knowing what a length measures.
`Vixen.Ui.Layout` measures without knowing where its numbers came from. Neither references the other,
which is what keeps a flexbox engine usable without a stylesheet and a cascade testable without a
layout — and it leaves a gap that something has to close. This is that something, and it is the first
thing doc 09 says `Vixen.Ui` owes.

## State

| | |
|---|---|
| `LengthContext` | What a relative length is relative to: the element's font size, the root's, the viewport. |
| `LayoutStyleBuilder` | `ComputedStyle` → `LayoutStyle`. Every layout-affecting property, the nine CSS edges, and the font-size chain. |
| Element tree, property system, event routing | ⏳ |
| Draw list, batching, clipping | ⏳ |

## What the bridge is for

**`em` on `font-size` means the parent's; everywhere else it means the element's own.** So font size is
resolved first and separately, and the caller walks the tree passing each element's resolved size to
its children. Conflating the two compounds: three nested `font-size: 1.2em` come out at 1.2× rather
than 1.728×, and the error grows with depth, so it reads as a rendering quirk rather than an
arithmetic one.

**Percentages are not resolved.** A percentage measures against the containing block, which only the
layout pass knows, so `50%` is handed on as `LayoutUnit.Percent` untouched. This is the one place
where doing less is the correct behaviour rather than an omission.

**An unparseable declaration leaves the initial value alone.** Zero is a perfectly good answer that
happens to be invisible, so using it for "I did not understand this" turns one typo into a missing
element with nothing said about it.

## What it found

⚠ **Yoga's initial values are not CSS's, and they differ in four places.** `flex-direction` is
`column` against `row`, `align-content` `flex-start` against `stretch`, `position` `relative` against
`static`, `box-sizing` `border-box` against `content-box`. `Vixen.Ui.Layout` is right to start where
Yoga starts — it is judged by Yoga's conformance suite — and this is the boundary where a VCSS
author's expectations take over, so `LayoutStyleBuilder.CssInitial` exists and `LayoutStyle.Default`
is not what an element with no declarations gets. Starting from the wrong one produces stylesheets
full of redundant declarations by an author who decided the engine was quirky and never reported it.

⚠ **ExCSS expands the box shorthands, and the gap that was predicted does not exist.** The bridge was
first written to expand `margin`, `padding`, `border-width`, `gap` and `flex` itself, on the
reasoning that the cascade stores shorthand and longhand as separate properties and the layout store
resolves edges by fixed precedence rather than document order — so `margin-left: 0; margin: 8px`
would give zero where a browser gives eight. **Its tests said every one of those paths was dead.**
ExCSS expands on parse, exactly as a browser does, so by the time the cascade runs that is two
`margin-left` declarations and the later one wins. The prediction was reasonable and wrong, and the
only reason it did not become a documented "known limitation" is that the test was written before the
claim was believed. `inset` is the exception, because ExCSS does not know the property.

⚠ **CSS has a unit that begins with the exponent character.** The value parser scanned `e` as part of
a number unconditionally, so `2em` scanned as `2e`, failed to parse, and came back `Unknown` — every
`em` in the document silently dropped. `1e2px` still has to work, so the fix is to test whether digits
follow rather than to drop the exponent.

⚠ **`aspect-ratio: 16 / 9` arrives as `16/9`.** ExCSS normalises the spaces away, so a parser that
splits on whitespace sees one token. Read here rather than by teaching `StyleValueParser` that `/`
separates values — it does in CSS, but making it a general separator changes how every shorthand
parses.

**And relative units belong in `StyleValue` after all.** They were deliberately left out, on the
argument that resolving them needs a context that does not exist at parse time. That was right about
resolution and wrong about representation, and **transitions settled it**: the animator interpolates
`StyleValue`, so a unit the type cannot express is a unit that cannot animate. `width: 2em` under a
`transition` snapped while its neighbours eased, with nothing said about it.

## How it is tested

Through the whole path — write CSS, read a `LayoutStyle` — rather than against a hand-built
`ComputedStyle`. A wire is worth testing with something plugged into both ends: a property name no
rule can ever set shows up as a test that will not pass.

Verified by sabotage. Starting from Yoga's defaults, resolving `font-size`'s `em` against the
element's own size, resolving percentages here, swapping `vw` and `vh`, and dropping the
leave-the-initial-value guard each fail it.

⚠ That last one took two attempts, and the failure is the interesting part. Written against a
stylesheet, `width: 4furlongs` never reaches the bridge at all — **ExCSS validates as it parses and
drops what it does not recognise** — so the test passed whatever the bridge did with a bad value,
including overwriting a good one. Rewritten against inline declarations, which are interned directly
and get no such vetting, it still passed: the value has to be one that *parses* but is not a length,
because an unparseable one is already filtered a step earlier. A bare `5` is the case that reaches
the code being tested.

Licensed under Apache-2.0.
