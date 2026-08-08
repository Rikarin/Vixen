<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 43 — Web styling and Tailwind parity

⚠️ **Extends [09](09-ui-framework.md) § Styling and § Layout, and corrects a rationalisation in
`Core/Vixen.Ui.Styling.Utilities/README.md`.** The requirement is one sentence and it has been stated
twice: **a UI system with full web styling support, and Tailwind-like utilities equivalent to
Tailwind, not a basic subset.** What is built is a subset — a good one, tested against resolved
elements rather than against expected text, and honest about several of its own holes — but a subset,
and the reason it stayed one is worth naming precisely, because the wrong reason is written down in
the codebase as though it were a decision.

⚠ **The utilities README says a family "is worth having when the engine reads what it sets", and that
the set "is chosen against `LayoutStyleBuilder` and `DrawListBuilder` rather than against Tailwind's
index". That is not a design principle. It is a description of a constraint, promoted to a principle
after the fact.** Read forwards it says: the utility layer is allowed to be as small as the renderer
happens to be, and the renderer's shape is not itself under review. Read the other way — which is the
way the requirement reads — a family that emits a property nothing reads is *a named gap in the
engine*, and the right response is a task against the engine, not a smaller family table. This
document takes the second reading. The README has been corrected alongside it.

**The claim this document has to earn.** Tailwind's utility index is the specification. Every root in
it is either implemented, or inert with a named task against the engine feature it is waiting for, or
explicitly out of scope with an argument. There is no fourth category, and "the renderer does not read
it" is a task, not an exclusion.

---

## Part 0 — The measurement, first

Everything below rests on one three-way cross product, checked in beside this document as
[`43-web-styling-parity.tsv`](43-web-styling-parity.tsv). It is a table and not prose because the
interesting question — *how much of Tailwind is there* — has a number, and every previous answer to it
in this repository was an impression.

| Axis | Source | How it was taken |
|---|---|---|
| **What Tailwind is** | `tailwindcss@4.3.3`, the package | `__unstable__loadDesignSystem().utilities.keys()` and `.variants.keys()`, cross-checked against the v4 docs |
| **What Vixen emits** | `UtilityFamilies.cs`, `Variants.cs`, `UtilityGenerator.cs` | parsed from the registration table, plus the shorthands ExCSS expands while parsing |
| **What Vixen reads** | every `Properties.Intern` / `PropertyId` call site in `Core/` and `Editor/` | transcribed per consumer: `LayoutStyleBuilder`, `DrawListBuilder`, `UiDocument`, `Cursor`, `Animator`, `InheritedProperties`, `TransitionSpec` |

⚠ **Two of those seven consumers were not consumers, and transcribing call sites is how they got into
the list.** `Animator` and `TransitionSpec` intern and read exactly what this table says they do — and
nothing constructed an `Animator`, so no frame had ever asked either of them anything. See **F10**,
closed by A20. A call site is evidence that a property *would* be read by whoever ran that code; it is
not evidence that anybody runs it, and that is the second time in this document the same mistake
appears in a different disguise. It appears a third time in **F11**: `StyleEngine.Load` takes a
`MediaContext`, every caller in the survey passed one, and the only caller that mattered —
`UiDocument` — was not in the survey at all, because a *missing* call site leaves no text to
transcribe.

⚠ **"Interned" is not "read", and that distinction had to be made twice.** `InheritedProperties`
interns seven names — `font-stretch`, `font-variant`, `text-transform`, `word-break`, `word-spacing`,
`text-indent`, `tint` — purely so the cascade knows they inherit; no consumer acts on any of them.
`LayoutStyleBuilder` interns `word-spacing` and `text-indent` and exposes their ids as
`WordSpacingId`/`TextIndentId`, and **nothing in the repository reads either property**. So the
useful count is not the interning count.

| | |
|---|---|
| CSS properties interned anywhere | **93** |
| …of which a consumer actually acts on | **86** |
| …interned only so the cascade knows they inherit | **7** |

⚠ **And a grep that finds no callers is a claim about the tool, not about the code.** A raw NUL byte
in a `.cs` string literal makes the file *binary* to `grep`, which skips it silently and exits 1 —
which is how `Vixen.Ui.Styling`'s `ShorthandExpansion` was called dead code in an earlier draft of
this survey when it is wired into `StyleSheetLoader` and load-bearing for everything in this table
that depends on `border-radius` or `border-color` reaching a longhand. Every "nothing reads this"
claim below was re-checked by reading the consumer rather than by the absence of a match.

### The rendered summary

| | Tailwind v4.3.3 | Vixen |
|---|--:|--:|
| Utility registry keys | 1 205 (890 static + 315 functional) | — |
| Utility **roots** (the unit of this table) | **328** | 98 families |
| CSS properties the utilities can set | **258** (8 of them vendor-prefixed) | **90** (5 of them `--` placeholders) |
| …of which something in the engine acts on | — | **72** |
| Variant keys | **88** | **25** |

⚠ **98 families, not 43.** The working figure that has been quoted — 43 registrations, ~239 emitted
tokens — counts the helper calls in one region of `UtilityFamilies`' static constructor rather than
the registry it builds. Parsed properly, the constructor registers **98 distinct family names**
emitting **90 distinct CSS properties** (five of them `--` placeholders). The direction of the error
does not change the conclusion; the number does need to be right before it is used as a denominator.

### The five states, and why the four in the brief were not enough

| State | Meaning | Roots |
|---|--:|--:|
| **works** | Vixen emits it, and a consumer acts on every property it sets | **51** |
| **partial** | emitted and partly read — one property of several, one axis of two, or a keyword set narrower than Tailwind's | **29** |
| **inert** | resolves, computes a value, and nothing in the engine looks at it | **13** |
| **absent** | not emitted at all | **223** |
| **composed** | in Tailwind it sets a `--tw-*` that another utility assembles; not a property row | **12** |

### The composition mechanism

**Landed.** `Core/Vixen.Ui.Styling.Utilities/UtilityComposition.cs`, with `from-*`, `via-*`, `to-*` and
`bg-linear-*` as the worked consumer. Three of the twelve `composed` roots are now emitted; the other
nine — `space-x/y-*`, `divide-*`, `mask-radial-*`, `ring-offset-*` and the static set — are more
surface on the same mechanism rather than more mechanism. It is written up in
[the guide](../guide/ui/utility-composition.md).

**Two designs, and the argument that settled it.** (a) the utilities really set custom properties and
the cascade resolves the `var()` references at use time; (b) the generator folds the fragments into
one declaration as it emits. (b) is cheaper and it is wrong, **because of variants**:
`from-accent hover:from-accent-hover` is two rules with two different selectors, and which one
supplies the colour depends on where the pointer is *now*. The generator resolves one candidate at a
time and writes one rule per class name, so composing at emit time would have to either drop the
variant silently — the failure this whole document exists to eliminate — or emit a rule whose selector
names two classes at once, `.bg-linear-to-r.hover\:from-accent-hover:hover`, a cross-product growing
as assemblers × fragment-bearing classes × variants and not enumerable until the whole candidate set
has been seen. `CompositionTests` holds both halves as tests, including the two computed values that
differ, which is the proof by contradiction that no single emitted declaration could have been both.

⚠ **An unset custom property poisons the whole declaration, and this was the trap.** Per CSS a `var()`
that resolves to nothing and carries no fallback makes the declaration *invalid at computed-value
time* — `VarSubstitution` already implements exactly that, by returning null — so the naive
`linear-gradient(var(--tw-gradient-from), var(--tw-gradient-via), var(--tw-gradient-to))` makes
`from-red to-blue` with no `via` paint **no gradient at all**, silently. The answer is the `var()`
fallback chain, which the engine has had since `VarSubstitution` was written: every fragment is
declared with an initial value and is only ever mentioned through `UtilityComposition.Reference`, so
the two-stop list is what `--tw-gradient-stops` is worth when nobody set it. `--tw-gradient-stops`'
initial value *is* the two-stop list, which is why only `via-*` has to override it.

**`@property` was not needed, and its absence is a known quantity rather than a discovery.** Vixen has
no registered custom properties. Two things registration would still buy, neither of them blocking:
`inherits: false`, without which a fragment set on a box is visible to its descendants — correct CSS
for an unregistered custom property, and a divergence from Tailwind, which registers them precisely to
stop the leak; and a *type*, which is what would let a gradient be transitioned. Both are refinements
to a mechanism that works without them, so neither is a prerequisite task.

**What it gates, and the first thing it gated.** v4 uses the identical pattern for transforms (A7 /
#23), `box-shadow` and filters (A8 / #28). The five `--` placeholders the table counted — `--blur`,
`--rotate`, `--scale`, `--translate-x`, `--translate-y` — were this shape built without the second
half: a fragment nothing assembles.

**Two of the five are gone.** `translate-x-*` and `translate-y-*` are composed now — a
`--tw-translate-x`/`--tw-translate-y` fragment each, assembled into one `translate` — and the engine
reads the assembly. ⚠ Their shape differs from the gradient's in a way worth carrying forward: **both
axes are assemblers**, each emitting the `translate` declaration beside its own fragment, so
`translate-x-2` alone works. v3 required a separate `transform` class the gradient way and v4 dropped
it, because a forgotten assembler is indistinguishable from a broken utility.

⚠ **And the placeholders were worse than unassembled — they were unspellable.** `--scale` is not a CSS
property. No engine anywhere would ever have read it, so unlike `background-image` the debt could not
have been closed by a reader arriving: the emission was wrong as well, and the gate could not say so,
because a property nothing emits and a property nothing reads look identical from inside it. That is
`grid-cols-3`'s failure a second time. `scale-*` and `rotate-*` emit `scale` and `rotate` now, at
Tailwind's own values, and `InertProperties.txt` records the debt under those names — where the expiry
check can reach it.

⚠ **`partial` is a fifth state the brief did not ask for, and collapsing it in either direction would
be the same mistake this survey exists to catch.** `border-t-2` is the case that forces it: the layout
reads `border-top-width` and insets the content box, and the draw list paints nothing, because
`DrawListBuilder` takes its one thickness from `Edge.Top` and its one colour from
`border-top-color`. Calling that "works" is the conflation the brief warns about; calling it "inert"
is false, because the box really does get narrower. There are 29 of these and they are the most
expensive rows in the table, because each is a utility that *half* does what it says.

### What a third stop cost

**Built.** `via-*`, stop positions, `bg-radial`, `bg-conic` and the interpolation space all landed in
one change, for the reason this section predicted: they were one piece of work. The prediction below
is kept as written, with what actually happened beside it, because the estimate was close enough to be
worth calibrating against and wrong in two places that are worth naming.

**They all bottom out in `UiShape`.** The record the box shader read was five `Vector4`s — half
extent, thickness and a gradient flag; the four horizontal radii; the four vertical; the axis, a
shadow's blur and one lane of padding; and the end colour. There was exactly **one** free float in it,
`Axis.w`. So:

| Owed | Predicted cost in `UiShape` | What it actually took |
|---|---|---|
| `via-*` — a third stop | a colour (4 floats) | 4, as predicted — one `Vector4` |
| stop positions — `from-10%` | 3 floats, or 2 if the ends are implied | 3, plus 1 for "is there a middle" |
| `bg-radial-*` | a centre (2) and a radius (1) | **0** |
| `bg-conic-*` | a centre (2) and a start angle (1) | **0** |
| the interpolation space | not counted | 1, in `Axis.w` |

**80 bytes to 112, exactly as predicted**, and the two zeroes are why it fit. CSS's defaults for both
round shapes are *at center* with an extent that is a function of the box — `farthest-corner` for a
radial is the box's own aspect scaled by root two, and a conic's sweep starts at twelve o'clock — so
neither needs a centre or a radius stored at all. A conic's `from <angle>` rides the *existing* axis
lane, because the host already writes an angle there as `(sin θ, -cos θ)` and the shader recovers it
with `atan2(x, -y)`. What that buys is refusing an explicit `at <position>`, `circle` or
`closest-side`, which is `GradientRefusal.Extent`, and which no theme in this repository writes.

⚠ **The layout change is the risky part and the shader maths is not** — and the specific danger turned
out to be sharper than "a field in the wrong place". Both new lanes were **appended**, and both
repurposed lanes were previously **zero**: `Size.w`'s gradient flag became the shape, whose `1` is
`Linear`, and `Axis.w`'s declared padding became the space, whose `0` is the linear-RGB lerp the
shader already did. Every existing offset therefore stayed put — which is exactly why drift here is
*silent*. A stale module reads a current record and still draws two-stop linear gradients perfectly,
ignoring everything past offset eighty. There is no garbage frame to notice.

So the mitigation is two tests rather than care:

- **`UiShapeLayoutTests`** (`Core/Vixen.Ui.Tests`) reads the committed `UiBox.reflect.json` and
  compares it with `Vixen.Ui.Rendering.UiShape` field by field — size, per-lane offset, lane count,
  and the actual bytes `MemoryMarshal` produces. Swapping two same-sized lanes, which passes an
  offsets-only check and paints a plausible picture, fails two of its assertions.
- **`CheckShaders`** now compiles the editor's own four `.rvn` and compares every module they emit.
  It previously covered only the library shaders the editor loads while describing itself as covering
  these, so a `.rvn` edited and never recompiled could sit in a commit unremarked.

⚠ **This section said four files have to agree. It is eight, and the four it did not name are the
ones no grep for `UiShape` will find.** Written down because the estimate's one real miss was not the
maths and not the layout — it was the *census*:

| # | File | What it knows | Caught by |
|---|---|---|---|
| 1 | `Vixen.Ui.Rendering.UiShape` | the whole layout | — |
| 2 | `Editor/Vixen.Editor.Host/Shaders/Ui.rvn` | the whole layout | `UiShapeLayoutTests`, `CheckShaders` |
| 3 | `UiBox.frag.spv` / `UiBox.reflect.json` | the whole layout | `UiShapeLayoutTests`, `CheckShaders` |
| 4 | `SoftwareUiRasterizer` | the whole layout | the UI suite's own pixel tests |
| 5 | **`UiRenderer`** | only the **size**, spelled `80` three times | `Vixen.Graphics.Golden.Tests` |
| 6 | **`Platform/Vixen.Graphics.Golden.Tests/Shaders/ui-box.frag`** | the whole layout, in GLSL | itself |
| 7 | **`Samples/02-HelloUi/Shaders/ui-box.frag`** | ditto | nothing |
| 8 | **`Tools/Vixen.Templates/.../Shaders/ui-box.frag`** | ditto | nothing |

Five, six, seven and eight are invisible to a search for the type: number five spells the literal
`80` and mentions no type at all, and the three GLSL copies call the struct `Shape`. The host wrote
112-byte records into a buffer sized for 80-byte ones, and each shader indexed at the old stride, so
every box after the first read the previous record's tail — a frame of plausible rounded rectangles
with the wrong radii, which is the failure `UiShape`'s remark predicts almost word for word.

⚠ **What actually caught it was `Vixen.Graphics.Golden.Tests` on a real device, not either new test.**
`UiShapeLayoutTests` pins the record's *shape* against the shader's reflection and has nothing to say
about how a host sizes a buffer around it; `CheckShaders` compiles Raven and the GLSL copies are not
Raven. Nothing compiles those three `.frag` files, and nothing should be made to — `TestShaders.cs`
records the decision not to require `glslc` on every CI leg. `UiRenderer` now derives its stride from
`Marshal.SizeOf<UiShape>()`, which removes number five from the list permanently; six, seven and eight
remain, and the honest answer to them is
[`Core/Vixen.Ui.Renderer/README.md`](../../Core/Vixen.Ui.Renderer/README.md)'s standing point that
three hand-maintained copies of one shader is not a design anybody chose.

⚠ **`Gradient.rvn` and `RoundedRect.rvn` are not the shader to edit, and both look like it.**
`Raven/Library/Ui/Gradient.rvn` already has radial and conic modes and a perceptual interpolation
option; `RoundedRect.rvn` has a `Gradient` permutation. Neither reads the `UiShape` buffer, so
neither is on the path a `background-image` takes. `Ui.rvn` is. `RoundedRect.rvn` is also a
cautionary note rather than a starting point: its gradient is hardcoded to `localPx.y / size.y` and
ignores the axis entirely, so it draws every gradient vertically whatever it is asked for.

⚠ **`Gradient.rvn`'s maths: partly liftable, and the honest breakdown matters more than the verdict**,
because two of the three look further from correct than they are and the third looks closer.

- **Radial — the same family, different inputs.** `length(local - axis) / radius` over a 0..1 UV is a
  circle in *normalised box space*, which is an ellipse with the box's own aspect — exactly what
  `Ui.rvn` now does with `length(point / halfSize)`. What is not liftable is the two `var`s: a stored
  centre and a stored radius are the lanes this design deliberately does not carry, and the `radius`
  default of `0.5` is `farthest-side` where CSS's default is `farthest-corner`, a factor of root two.
- **Conic — right direction, wrong origin.** `atan2(d.y, d.x) / 2π + 0.5` does sweep *clockwise* on
  screen, because y-down flips the usual sense; the easy assumption that it runs backwards is wrong.
  Its zero sits at nine o'clock, though, where CSS's is at twelve — so it is a quarter turn out, not a
  mirror. `atan2(x, -y)` is the same expression written in CSS's convention.
- **`Perceptual` — not Oklab, and not close.** It is a per-channel cube root with no LMS matrix and no
  Oklab basis anywhere: a plausible-looking lightness curve, and not the space `in oklab` names. This
  one genuinely had to be replaced, with Ottosson's two matrices written out to match the host's
  `Vixen.Core.Mathematics.Oklab`.

So the shapes were a rewrite of two short expressions rather than a lift, and the interpolation was a
real reimplementation. Nothing was lost by not lifting; what would have been lost is the hour spent
believing a working implementation was already there.

### Which space a gradient interpolates in

Three answers coexisted and each was right on its own terms: the engine paints in linear RGB and the
shader lerped there; CSS's default for an unhinted gradient is sRGB; Tailwind v4 emits `in oklab` on
everything it generates. **All three are kept, and which one applies is decided by who wrote the
gradient** — `GradientSpace` travels in the record.

| Source | Space | Why |
|---|---|---|
| A `.vcss` rule with no hint | `Srgb` | CSS's rule. A hand-written gradient should match a browser. |
| Anything the utility generator emits | `Oklab` | v4 parity, and the palette ships as v4.3.3's `oklch`. |
| `BoxStyle.Vertical` and the rest of the C# API | `Linear` | No CSS text, so no hint — and it is what the shader already did. |

⚠ **`Linear` being the enum's zero is what let 43 committed screenshots stay put.** Making sRGB the
universal default would have been the tidier-sounding decision and would have moved every
programmatic gradient in the tree for no reason anybody asked for. `in srgb-linear` is CSS's own
spelling for it, so a stylesheet can still ask.

The three separate visibly only at the midpoint, which is the only place it matters. A black-to-white
ramp is 0.5 linear under `Linear`, 0.214 under `Srgb` and 0.125 under `Oklab` — and between two
complements the difference is a colour against a grey dead zone.

⚠ **Refused rather than approximated: the polar spaces.** `in oklch`, `in hsl`, `longer hue` and the
rest interpolate along a hue *arc*, which is not a lerp in any three lanes — two colours plus a
direction round the wheel is a different shader. `lab` and `display-p3` are rectangular and could be
two more transfer functions; they are refused because nothing asks and an untested space is worse than
an honest gap.

### Gradient text needs an offscreen pass, and should wait for one

Tailwind draws gradient text as `bg-clip-text` plus `text-transparent`: paint the background, clip it
to the glyph coverage, and make the glyphs themselves invisible. The middle step is the whole problem.

**The engine has no text mask to clip against.** The glyph path takes an MSDF sample, reconstructs
coverage from the median of three channels, multiplies it by the run's colour and outputs it — the
coverage exists for the length of one expression inside `Ui.rvn`'s text shader and is never a surface
anything else can read. `bg-clip-text` needs it as a *mask*: the background has to be sampled where
the glyphs are, which means the glyph coverage has to outlive the fragment that computed it.

Three ways it could go, in increasing order of honesty:

1. **Per-glyph gradient in the text shader.** Cheapest — pass the gradient down the text path and
   evaluate it per fragment instead of using the run colour. ⚠ **And wrong in a way that looks
   right:** CSS clips the background to the *element's* box, so a gradient across a heading is one
   ramp across the whole heading, not a fresh ramp inside every letter. This would draw the second,
   which is a well-known wrong answer that is only visible on words long enough to notice.
2. **Element-space gradient in the text shader.** Same path, but the gradient parameter comes from
   the element's box rather than the glyph's quad. This is genuinely correct for the common case and
   costs a per-run rectangle. It stops being correct as soon as `background-clip` has to apply to
   anything that is not a plain gradient — an image, a `background-position`, a filter under it.
3. **A real offscreen pass.** Render the text run's coverage to a target, then composite the
   background through it. This is the general answer, and it is the same machinery `filter: blur()`
   (A8 / #28) needs: a scratch target, a way to name it, and a composite step in the UI renderer.

**Recommendation: do not build 1. Build 2 only if it can be spelled as a special case that 3 would
delete, and prefer waiting for #28.** The reason is that gradient text is a small feature and an
offscreen path is a large one, so gradient text is a bad forcing function for the design — a mask
mechanism invented to serve it would be shaped by the easiest consumer rather than the hardest.
#28 has to build the general thing anyway. The precedent is F5: `text-overflow: ellipsis` was left
undone rather than approximated, and the cost of that decision has been zero.

### By category

| Category | roots | works | partial | inert | absent | composed |
|---|--:|--:|--:|--:|--:|--:|
| Layout | 49 | 9 | 8 | 0 | 20 | 12 |
| Interactivity | 39 | 2 | 0 | 1 | 36 | 0 |
| Flexbox and Grid | 34 | 10 | 3 | 3 | 18 | 0 |
| Typography | 34 | 4 | 3 | 1 | 26 | 0 |
| Borders | 34 | 1 | 9 | 0 | 24 | 0 |
| Effects | 33 | 2 | 0 | 1 | 30 | 0 |
| Spacing | 24 | 14 | 4 | 0 | 6 | 0 |
| Transforms | 23 | 0 | 0 | 4 | 19 | 0 |
| Filters | 20 | 0 | 0 | 1 | 19 | 0 |
| Sizing | 15 | 7 | 0 | 0 | 8 | 0 |
| Backgrounds | 11 | 0 | 1 | 0 | 10 | 0 |
| Transitions and Animation | 6 | 2 | 1 | 0 | 3 | 0 |
| SVG | 3 | 0 | 0 | 2 | 1 | 0 |
| Tables | 2 | 0 | 0 | 0 | 2 | 0 |
| Accessibility | 1 | 0 | 0 | 0 | 1 | 0 |
| **Total** | **328** | **51** | **29** | **13** | **223** | **12** |

Spacing and Sizing are the two categories that are genuinely done. Everything else is between a
quarter and nothing, and three categories — Transforms, Filters, Tables — have **no working root at
all**.

⚠ **The table above is the hand survey of 2026-08-07 and it is no longer the measurement.** C5 has
landed as `Core/Vixen.Ui.Styling.Utilities.Tests/UtilityConsumptionGateTests`, which computes the
inert set on every test run by resolving real elements and watching what the engine does with them —
so from here on, the numbers to believe are the ones that run. Three things it found on its first
pass, all of which the table above gets wrong in one direction or the other:

- **Four rows have since moved to `works`** and the survey predates them: `border-bottom-color`,
  `border-left-color` and `border-right-color` are painted by the draw list now, and `order` is read
  by both the layout and the paint sequence. The Borders and Flexbox rows are that much better than
  they read.
- **Transitions and Animation was `0 works, 0 partial, 3 inert`, not `2 / 1 / 0`** — F10 below. The
  row was derived from the cascade computing a value, which is the conflation this whole document is
  about, and it got past the survey. ✅ It is `3 works` now that A20 has landed, arrived at from the
  other direction: a fade measured mid-flight rather than a value found in a table.
- **`font-weight` is read** and the survey's own consumer walk did not say otherwise; it is recorded
  here because it is the one property the *gate* got wrong first time round, for a reason worth
  knowing. See F10's second half.

The live count is **11 properties emitted with no consumer**, every one of them on the expiring
allow-list in `InertProperties.txt` with the task that closes it.

### The columns

`category · root · kind · example · css · vixen_family · vixen_emits · engine_reads · inherit_only ·
state · shadowed_by · value_gap · note · classes`

Two of those are the ones to read first. **`shadowed_by`** names the Vixen family that swallows a
Tailwind class whose own family does not exist — `rounded-tl-lg` reaches the family `rounded` with
the value `tl-lg`, which no token table answers, so the utility is dropped with no diagnostic. That
is `absent` with a trap in it rather than plain absence, and there are dozens. **`value_gap`** is the
column for a root that emits and is read and *still* does not do what it says, `display` and
`overflow` being the two the resolved-element suite proves.

⚠ **The table was generated once, by a script, and the script is not in the tree.** Two of its three
inputs need `tailwindcss` installed to dump the registry, so it is not something `./build.sh` can run.
Making it one — `Tools/Vixen.TailwindParity`, reading a committed snapshot of the v4 registry and the
same interning call sites the C5 gate walks — is the honest form of exit criterion 1, and it is
grouped with C5 below rather than left as an intention. Until then the table is a measurement with a
date on it: **`tailwindcss@4.3.3`, 2026-08-07.**

---

## Part 1 — What the survey found that nothing in the tree said

Nine findings. Each is checkable, and the ones marked ⚠ contradict something currently written down.

### F1 · `border-l-2` changes the layout and paints nothing ⚠

`LayoutStyleBuilder` interns all seven border-width names and the layout honours each edge. The draw
list takes **one** thickness — `Layout.GetComputedBorder(node, Edge.Top)` — and **one** colour,
`border-top-color`. So:

- `border-l-2` insets the content box by two pixels on the left and draws no border anywhere.
- `border-t-2` insets the top by two and draws a two-pixel border on **all four sides**.

Both are now proved by resolving real elements rather than by reading source —
`A_left_border_insets_the_layout_and_paints_nothing` and `A_top_border_paints_the_whole_box` in
`UtilityFamilySupportTests`. The first asserts the child's position *and* the absence of any
`DrawCommandKind.Border`; the second asserts the stroke's rectangle is the element's own box, because
a thickness assertion alone would pass either way.

The utilities README says per-edge border *colours* are inert. That is true and it is the smaller
half: the widths are read by one consumer and ignored by the other, which is worse than inert,
because the geometry moves and the picture does not follow. Nine of the 34 Borders roots are
`partial` for this reason.

### F2 · `rounded` is uniform for the same reason, one level down

`DrawListBuilder` interns `border-top-left-radius` and applies it to all four corners. ExCSS expands
`border-radius`, so `rounded-md` works. `rounded-tl-md` does not exist as a family, is swallowed by
`rounded`, fails the radius lookup and is dropped. **Fourteen** per-corner roots, all absent. The
draw list underneath is not the limitation — `UiShape` already carries eight floats of elliptical
corner radii; the *property bridge* is.

### F3 · The per-axis overflow was the same bug twice, and it is fixed ✅

Recorded because it is the worked example of the whole document, and because it closed while this was
being written. `overflow-x` and `overflow-y` were interned by nobody, so `overflow-y-auto` resolved
cleanly and did nothing; and `overflow-auto` clipped in the draw list while the layout's keyword
table — `visible`, `hidden`, `scroll`, no `auto` — went on treating the box as visible. Half a
property, in four editor panels.

Both are now read. `Vixen.Ui.OverflowReader` is the single place all three names resolve, for the
clip stack and the hit test alike — two copies of one rule being how a control ends up visibly
clipped and invisibly clickable — and `LayoutStyleBuilder` maps `auto` onto `Overflow.Scroll`, which
is the layout CSS gives it, since the only thing `auto` and `scroll` disagree about is a scrollbar
gutter nothing here draws. Two rows moved from `inert` to `partial`, and the only thing keeping all
three off `works` is `overflow-clip`, which Vixen does not emit.

⚠ **And the caveat it records is the shape of the next problem: a clip is not a scrollbar.**
`overflow-y-auto` cuts the content off and nothing offers to scroll it. Scrolling is `ScrollView`, a
control that owns its bars and offsets its content. That is exactly the argument Part 8 § 3 makes for
re-homing the 32 scroll-container roots against `ScrollView` rather than emitting them as properties.

### F4 · `display` was `{ Flex, None }` ✅ closed by B1, B2 and B3

`LayoutEnums.cs` had two members. Seven of Tailwind's 21 display keywords are emitted (`block`,
`inline`, `inline-block`, `flex`, `inline-flex`, `grid`, `hidden`) and **two** were read. The
resolved-element suite proved it the only way that is honest: two children of an element carrying
`block` still sat side by side. This is the root of Track B and the reason `grid-cols-3` is inert
rather than broken — nothing is broken, there is simply no grid.

✅ **All seven emitted keywords are now read.** `block` arrived with B1, `grid` with B2, and
`inline`, `inline-block` and `inline-flex` with B3 — the last three behind an actual inline
formatting context rather than the alias this section warned against. ⚠ The warning was right and is
worth keeping: `inline-block` mapped onto `Block` would have taken the whole line, and
`An_inline_block_shares_its_line_instead_of_taking_it` is that sentence turned into a measurement.
⚠ `inline` is **atomic** here — see B3's row for the invariant that decides it.

✅ **`block` is the third member and it is a real one.** B1 landed the algorithm behind it, not an
alias, so a `block` element stacks its children, fills the line across them and **collapses their
vertical margins** — and `UtilityFamilySupportTests.Display_block_does_not_stop_an_element_being_a_flex_row`
has been *inverted* rather than deleted, because a family moving from `Inert` to `Supported` is what
that file exists to record. Four keywords remain unread: `grid` waits on B2, and `inline`,
`inline-block` and `inline-flex` wait on B3. ⚠ The last two are unmapped **deliberately**: they
differ from their block-level twins only inside an inline formatting context, and mapping them onto
`Block` and `Flex` would give `inline-block` the whole line, which is precisely what an author writes
it to avoid. An alias would look like support and behave like a bug.

### F5 · `truncate` does not truncate

Tailwind's `truncate` is three declarations: `overflow: hidden`, `text-overflow: ellipsis`,
`white-space: nowrap`. Vixen's emits the first — `Truncate_emits_neither_text_overflow_nor_nowrap`
resolves an element and finds both of the other two absent. Nothing in `Vixen.Ui.Text` implements
`text-overflow`, so the name promises an ellipsis the engine cannot draw, and the wrapping the third
would have suppressed still happens. `line-clamp-*` is absent for the same reason one level up.

### F6 · Pseudo-element selectors compile and nothing consumes them ⚠

`SelectorCompiler` parses `::before`/`::after`, interns the name and stores it on `Selector`. A
**NUL-safe** search (`rg --text`, after the `ShorthandExpansion` lesson above) for a reader of
`Selector.PseudoElement` across `Core/` and `Editor/` returns four hits: the compiler that writes it,
the record declaration, and two assertions in `SelectorMatchingTests` that it *was* written. Nothing
in `SelectorMatcher`, `StyleRuleSet` or `StyleResolver` filters on it. So a rule written for `p::before` is
matched and applied **to the `p`**, and doc 09's supported-selector list, which names `::before` and
`::after`, is ahead of the code. Seven Tailwind variants (`before`, `after`, `marker`, `placeholder`,
`selection`, `file`, `backdrop`) depend on this, and it needs a test before anything is built on it.

### F7 · Arbitrary *properties* are not supported, and arbitrary *values* are ⚠

`w-[37px]` works and is well tested. `[mask-type:luminance]` — Tailwind's arbitrary-property escape
hatch — parses to an arbitrary value with an empty utility name, and `UtilityParser.TryParse` returns
false on the empty name. The class is silently unknown. The utilities README lists the escape hatches
and does not mention this one is missing. v4's CSS-variable shorthand `bg-(--brand)` is likewise
unsupported: the parser looks for `[` and nothing else, so `bg-(--brand)` reaches the colour lookup as
the literal text `(--brand)` and is dropped.

### F8 · The overloads are Tailwind's, not Vixen's ⚠ *correcting the brief*

The brief asks what the `text-` and `border-` overloads cost, on the premise that they are a Vixen
compromise. They are not. In Tailwind v4, `text-*` resolves against `--text-*` for a size and
`--color-*` for a colour and `text-center` is a static utility — three meanings behind one prefix,
exactly as here, and a colour named `--color-lg` is exactly as unreachable there as it is here.
`border-*` is `border-width` **and** `border-color` in v4's own registry. `font-*` is `font-family`
**and** `font-weight`.

So the overload is not a defect and the resolution order is not a Vixen invention. What *is* a Vixen
defect is a different thing that lives next door: **the longest-prefix split has no fallback**. When
`rounded-tl-lg` fails inside the family `rounded`, Tailwind would go on to try `rounded-tl` as a root;
Vixen has already committed to `rounded` and reports the class as unknown. Every `shadowed_by` row in
the table is an instance. That is one function's worth of work — try the next-longest prefix on
failure — and it is what makes adding the per-corner and per-axis families safe.

### F9 · Doc 09's own 1.0 family list was never finished

Doc 09 § *The utility preprocessor* names the families for 1.0 and the document is marked ✅ built.
Five of the names in that list have no family: **`space`**, **`divide`**, **`mix-blend`**,
**`origin`**, **`scroll`**. This is not a Tailwind-parity gap; it is doc 09 disagreeing with the code,
which is the thing `docs/overview.md` exists to catch and did not.

### F10 · Nothing ever builds an `Animator`, so no CSS transition has ever run ✅ *closed by A20*

The gate's first pass found it and a NUL-safe search confirms it: **`Animator` is constructed in
exactly one place in the repository, `Core/Vixen.Ui.Styling.Tests`.** No `UiDocument`, no
`StyleEngine`, no `StyleUpdater`, no control and no editor host ever makes one, and nothing anywhere
calls `Animator.Observe` or `Animator.Advance` outside those tests. `TransitionSpec` and
`TransitionParser` have the same two callers: the animator, and the animator's tests.

So the whole transition and `@keyframes` machinery is a well-tested component with no socket. A
document that declares `transition: all 200ms` and then changes a class jumps straight to the new
value — proved by resolving real elements and running frames either side of a class change, and
finding the frames byte-identical with the declaration and without it.

⚠ **This document said the opposite, and the way it got there is the exact failure it was written to
name.** Part 0's consumer list names `Animator` as one of the seven readers "transcribed", and the
category table gives Transitions and Animation two `works` and one `partial`. Both were derived from
the cascade holding a value for `transition-property` — which it does, correctly — rather than from
anything happening as a result. `UtilityFamilySupportTests` carried the same three rows in
`Supported` for the same reason, in the file whose own remark warns against precisely this. They
have moved to `Inert`.

**Sized as A20 / task #46 below.** It is small — the animator is finished, and what is missing is a
field on the style engine, a call to `Observe` where a computed style is replaced, a call to
`Advance` from the frame's tick, and `Apply` on the way to the consumers. What makes it worth its own
task rather than a line in another is that it is the seam that decides whether `Vixen.Ui`'s frame
loop has a place for a time-varying style at all.

✅ **Landed, and the estimate was right: four wires, exactly where this said.** `StyleEngine.Animations`
is built with the rest of the derived state so a reload forgets what was in flight; `StyleUpdater`
announces every replaced style to it, from the cold pass as well as the incremental one, stamped with
a `Now` the document writes; `UiDocument.Tick` advances it and marks the document dirty through
`InvalidatePositions` rather than `Invalidate`, because a fade changes nothing the cascade decided;
and `UiDocument.Apply` overlays it before anything reads a style, which puts the transition tier above
`!important` where CSS Cascading 5 § 6.2 wants it. `UiDocument.CompactStyles` remaps it, which is the
one of the five that is load-bearing rather than insurance — a running transition is the only
per-element state a cold pass does not rewrite.

⚠ **The proof is a value read *between* the endpoints, and nothing weaker would have done.**
`Vixen.Ui.Tests.TransitionTests` asserts a width that is neither ten nor a hundred and ten and a
colour that is neither of the two the stylesheet names. Each of the four wires was removed in turn and
the suite went red for each; a fifth sabotage — pinning `StyleUpdater.Now` to zero — passed the first
draft, because every test in it started its fade at `t = 0`, which is also where a clock that is never
advanced is. `A_transition_started_late_in_the_session_still_takes_its_full_duration` is what that
sabotage bought, and the bug it guards is real: every transition in a process that had been running
for a while would otherwise begin already finished.

⚠ **The gate needed a tenth scene before it could see the third property, which is the third time.**
Wiring the animator made `transition-duration` and `transition-timing-function` consumers
immediately and left `transition-property` measuring inert — not because it is unread but because
`transition-duration` defaults to `0s`, so the property alone moves nothing in a plain scene, and the
`animated` scene already declared the family's only emitted value (`all`). The comment on that scene
asserted the opposite and was wrong. The `primed` scene — a duration and a timing function aimed at a
property the mutation does not touch — is where injecting `all` finally changes a frame. Same lesson
as `gridded` and `inlined`: a green gate is a claim about the scenes as much as about the engine.

⚠ **Two limitations found while proving it, both real and neither fixed here.**

- **A transition only runs where the previous computed style *also held the property*.** `Observe`
  reads the displayed value out of `before`, and a cascade with no computed-value stage has nothing
  to offer for a property the element did not previously declare — so fading `margin-left` from an
  implicit `0` does not happen, while fading it from a declared `0px` does. That is why the three
  rows come back as `paint` consumers and not `layout` ones: the probe's mutation adds a `margin-left`
  that was not there before, and only its `background-color` change had both ends.
- **The `transition` utility still does nothing on its own.** Vixen's family emits
  `transition-property` and stops; Tailwind's also emits a 150 ms duration and a timing function. The
  property is read, so the row belongs in `Supported`; the class needs a `duration-*` beside it. A
  family gap rather than a property gap, recorded on the `Supported` table.
- **A fading inherited value does not reach the children.** The animator is a tier over the finished
  cascade, so `StyleUpdater` inherits from the parent's *cascaded* style and the overlay is applied
  per element afterwards — a panel fading its `color` hands its descendants the destination on the
  first frame while the panel itself travels, and a descendant cannot start its own transition
  because `transition-*` do not inherit. Fixing it means the overlay participating in inheritance,
  which is a change to the order of the pass rather than to the animator, and is not A20's.

### F11 · The whole of `@media` was evaluated against a surface that does not exist ⚠ *found while closing F10*

`StyleEngine.Load` has taken a `MediaContext` since the cascade was written. **`UiDocument.Load`
passed nothing**, and nothing else in `Core/` or `Editor/` constructed one — the only callers outside
`Vixen.Ui.Styling.Tests` were tests. So every stylesheet in every real document was evaluated against
`default(MediaContext)`: a surface nought pixels wide, nought high, at 1×, with no colour-scheme
preference and an sRGB gamut.

**That is the same shape as F10 and it is bigger.** The scope, in descending order of how much it
matters:

- **Every responsive variant was dead.** `md:p-4` compiles to `@media (min-width: 768px)`, and
  `0 ≥ 768` is false at every window size, so the block was dropped at load and the class matched
  nothing. `sm:`, `md:`, `lg:`, `xl:`, `2xl:` and any breakpoint a theme names, all of them, always.
  Nothing in the repository writes one — which is why it had never been noticed, and is also why
  fixing it moved no screenshot baseline.
- **`dark:` under the `media` strategy was dead**, for the same reason. The `class` strategy compiles
  to a `.dark` ancestor and was unaffected, and the editor uses the `class` strategy.
- **`@media (color-gamut: p3)` could never match**, which was the entry point: the swapchain reports
  the gamut it was *granted*, `UiGeometryBuilder` is already told and maps every colour it emits
  against it, and the same fact never reached the cascade.

⚠ **`@media` is decided at load and not at match**, which `StyleSheetLoader` says and gives the reason
for — so re-asking the question on a resize is somebody's job, and it was nobody's. It is
`StyleEngine.SetMedia` now, guarded on the *verdicts* rather than on the context: the conditions the
loader saw are replayed against the old context and the new one, and the sheets are reloaded only
where one of them disagrees. Without that guard a window drag would be a full ExCSS re-parse of every
sheet sixty times a second, and would restart every fade in the window each time.

⚠ **And it uncovered a latent crash that predates all of this.** `StyleUpdater` builds a
`StyleInvalidator` over `StyleEngine.Selectors` in its constructor and keeps a cursor into
`StyleEngine.Rules`; `StyleEngine.Reload` replaces both. A reload that produced fewer selectors read
somebody else's compound and invalidated the wrong subtree, and one that produced more read off the
end and threw — reachable through `UiDocument.ReloadStyles` and therefore through every hot edit of a
stylesheet, and invisible only because a hot edit rarely changes the rule count much. A breakpoint
being crossed turns a dropped block into rules, which adds selectors by construction, so the first
`@media` re-evaluation found it immediately.

**Sized at 0.3 EM and landed with A20**, because it is the same shape of bug and the same seam. What
is still owed is **per-surface media**: `@media` produces rules, rules are shared by every surface of
one document — that is what keeps one theme across a torn-off window — so `max-width` cannot yet
answer differently in two windows, and the context is read off the primary surface. `EditorPane`
publishes the gamut from the main window's swapchain only, for the same reason.

⚠ **And the second half is about the instrument rather than the engine: `font-weight` read as inert
and is not.** The weight reaches `FontRegistry.Resolve` and selects a different face; the gate could
not see it because `DrawList` deliberately does not compare `Fonts` between frames — its argument
being that a command drawn in a different face refers to it by a different index, which is true of a
frame using several faces and false of a frame that swapped its only one. The gate's paint signature
now includes the face names. Worth recording because the same reasoning is in `DrawList.Differs`,
where the consequence is a version that would not bump; in practice two real faces produce different
glyph advances and the glyph comparison catches it, so this is a note and not a bug report.

### Variants and modifiers

| | Tailwind v4 | Vixen | |
|---|---|---|---|
| Registered variant keys | 88 | 25 | 28 % |
| Arbitrary variant `[&>*]:` | ✅ | ✅ | |
| Arbitrary value `w-[37px]` | ✅ | ✅ | |
| Arbitrary property `[mask-type:luminance]` | ✅ | ⛔ | F7 |
| CSS-variable shorthand `bg-(--brand)` | ✅ | ⛔ | F7 |
| `/opacity` modifier | ✅ `color-mix` | 🟡 hex only | Part 2 |
| `!important` | **suffix** `bg-red-500!` | ✅ suffix | *matches v4, not v3* |
| Negative values `-mt-4` | ✅ | ✅ | |
| Prefix (`tw:flex`) | ✅ | ⛔ | |
| Two media variants on one utility | nests | ✅ nests | A15 |

The 25 Vixen covers: `hover focus focus-visible focus-within active disabled enabled checked first
last only odd even dark ltr rtl group peer data aria` plus the five breakpoint names when the theme
declares them.

The 63 it does not fall into three quite different buckets, and lumping them together is how this
gets mis-sized:

- **Twenty-two are a table entry each**, because the selector already compiles: `empty`, `not-*`,
  `nth-*`, `nth-last-*`, `*-of-type`, `target`, `open`, `required`, `optional`, `valid`, `invalid`,
  `read-only`, `placeholder-shown`, `indeterminate`, `default`, `autofill`, `in-range`,
  `out-of-range`, `visited`, `inert`, `user-valid`, `user-invalid`. Some need an element-state bit
  set by the control library; none needs a matcher change.
- **Seven need pseudo-elements to mean something** — F6.
- **Thirteen are media features** (`motion-safe`, `motion-reduce`, `contrast-more`, `contrast-less`,
  `forced-colors`, `inverted-colors`, `portrait`, `landscape`, `print`, `noscript`, `pointer-*`,
  `any-pointer-*`), each one condition in `MediaQuery`.
- **The rest are engine features**: `has-*` (doc 09 defers it to P2 on incremental-match cost),
  `supports-*`, `starting` (`@starting-style`), `in-*`, `*`/`**`, and the whole container-query
  family `@`/`@min`/`@max` — Part 2.

---

## Part 2 — Tailwind v4 is the target, and it changes the token model

**Decision: v4.** Not a version bump — v4's configuration model is CSS-first, and Vixen's is a YAML
asset compiled by a build step. Those are different designs, and two queued tasks are about to build
the v3 one more solidly.

### D1. `@theme` is a stylesheet, and `vixen.ui.yaml` should become one

In v4 a design token *is* a CSS custom property, declared in an `@theme { … }` block, and declaring it
does two things at once: it emits the variable, and it **tells the compiler which utility classes
exist**. `--color-mint-500: oklch(…)` creates `bg-mint-500`, `text-mint-500`, `border-mint-500` and
the rest. There is no configuration file.

Vixen's editor theme has already arrived at half of this by a different road, and the half it
arrived at is the awkward half. `Editor/Vixen.Editor.Ui/Theming/vixen.ui.yaml` declares **no colours
of its own** — every entry is `var(--…)` pointing at a custom property `EditorTheme` puts on the
root — precisely so that there is one palette and not two. So the YAML is already a *table of
pointers into a stylesheet*, and the stylesheet is where the values are. That is `@theme` with an
extra file in front of it.

⚠ **And the extra file is exactly where the token model breaks.** Its own comments say so:

> `radius` is missing on purpose … `ThemeTokens.Radius` is a `Dictionary<string, float>`: it parses
> numbers, so `var(--radius-row)` is rejected with a diagnostic rather than stored.

> An opacity modifier on one of these does nothing. `TryColor` turns `bg-accent/50` into `rgba(…)` by
> parsing the colour as a hex triple, and `var(--accent)` is not one.

Both are the same bug: `ThemeTokens` stores *parsed values* where the theme holds *references*. Under
v4 that class of bug cannot occur, because a token is a string in a stylesheet and the cascade
resolves it.

**What this means for `ThemeTokens`.** It survives, and it shrinks. It stops being a parsed token
store and becomes a reader of an `@theme` block that keeps names and text: `Dictionary<string,float>`
becomes `Dictionary<string,string>` for radius, spacing gains the v4 semantics below, and the
`Colors` dictionary already holds strings. The generator's job changes from "resolve a token to a
value" to "resolve a token *name* to `var(--name)`", which is what the editor's YAML is faking today.

**What this means for the two queued tasks.**

- **#6 — extract the three theme sheets from C# constants to `.vcss`.** Right shape under v4, and it
  becomes the *foundation* rather than a tidy-up: the `.vcss` those constants become should carry the
  `@theme` block, and `vixen.ui.yaml` should be deleted rather than ported. Do #6 first and do it as
  `@theme`.
- **#7 — fold `.vxml`/`.vcss`/`vixen.ui.yaml` into `Vixen.Sdk`.** Two of the three still apply. The
  third should not be folded in; it should be removed. If the SDK ships a `vixen.ui.yaml` convention
  it will be supported for as long as anyone has one.

⚠ **The cost is not the parser.** Reading `@theme` out of a `.vcss` is a day. The cost is that
`--spacing` in v4 is **one number and the scale is unbounded**: `p-4` compiles to
`padding: calc(var(--spacing) * 4)`, and `p-137` is a valid class needing no configuration. Vixen
resolves spacing at generate time to a pixel string, so it needs either `calc()` in the style engine
(which doc 09 lists as supported for `+ - * /` on compatible units — this is a multiply by a unitless
scalar, the easy case) or continued build-time resolution, which is a documented, defensible
divergence. Vixen's `SpacingBase` is already one number, so the *model* matches; only the emission
does not.

✅ **Landed, and `vixen.ui.yaml` is gone from the tree.** `ThemeTokens.Parse` reads `@theme` blocks
out of stylesheet text, `CreateDefault()` is v4.3.3's own theme embedded as
`Core/Vixen.Ui.Styling.Utilities/Theme/vixen.default.vcss`, and a project's blocks layer over it with
v4's `initial` semantics — namespace, everything, or one token. Both token files in the tree are now
`vixen.ui.vcss`, and the `.targets` glob is the only MSBuild change. Six things the sizing above did
not know:

- **`ThemeTokens.Radius` really did dissolve, and nothing else had to move.** It is
  `Dictionary<string,string>`, `TryRadius` emits what it holds, and the editor's three radii are
  ordinary tokens spelled `--radius-row: var(--radius-row)`. What that spelling costs is one rule
  nobody would guess: **a theme token whose value references its own name must never be emitted**,
  because the `root` rule the generator writes lands *after* the sheet that declares the real value
  and would shadow it with a self-reference resolving to nothing. Every radius in the editor goes
  square, from a declaration that reads like a tautology. `RootRuleFor` skips them and
  `A_token_that_references_its_own_name_is_never_emitted` pins it.
- ⚠ **The generator does *not* resolve a name to `var(--name)`, and the paragraph above should not
  have said it would.** It emits the token's value, as it always did — which is what keeps the
  editor's output byte-identical, because the editor's values are already `var(--surface)` and
  friends. The variable is emitted *as well*, into a `root` rule holding only what a sheet actually
  references, closed transitively. Emitting all 347 to serve the handful anyone says `var()` against
  is three hundred interned strings on every document's root for nothing.
- **The YAML dependency was the whole of why `Tools/Vixen.StyleGen` is a process**, and it is gone.
  `Vixen.Ui.Styling.Utilities` now has no package references at all, so the "an analyzer's
  dependencies do not travel with it" argument that runs through that project file, its README and
  the runner's remarks no longer applies. Making the step an `IIncrementalGenerator` is a separate
  change; the blocker is what lifted.
- ⚠ **"22 hues × 11 steps" is v4.0's count and is stale.** v4.3.3 ships **26** ramps — `mauve`,
  `olive`, `mist` and `taupe` arrived after 4.0 — for 288 colour declarations and 347 in total.
- **A namespace can look like a member of another one, and two do.** `--text-shadow-*` falls inside
  `--text-*` and would become a font size called `shadow-sm` whose value is a box-shadow;
  `--font-weight-*` falls inside `--font-*` and would become a font stack called `weight-bold`. Both
  are guarded, and both would have parsed cleanly and produced a utility that resolved to nonsense.
- **Reading is not a cost anyone will notice.** 347 declarations parse in **≈ 0.45 ms**, and the
  candidate space does not grow with the palette: the generator emits only what was scanned, and
  `UtilityFamilies.Probes` takes the ordinally first key of each scale rather than the cross product.
  The editor's whole generated sheet is 23 rules.

### D2. `color-mix()` dissolves task #12 and opens a smaller one

Confirmed by compiling it rather than recalling it — `bg-blue-500/50` in v4.3.3 is:

```css
background-color: color-mix(in srgb, oklch(62.3% 0.214 259.815) 50%, transparent);
@supports (color: color-mix(in lab, red, red)) {
  background-color: color-mix(in oklab, var(--color-blue-500) 50%, transparent);
}
```

The variable is **inside** the mix. That is precisely the shape `TryColor` cannot produce today and
the reason the editor's YAML carries a warning about it. So the answer to "`bg-accent/50` drops the
opacity" is not "teach `ThemeTokens` to hold a `var()`" — it is "emit a `color-mix()`", and the token
stays a reference.

⚠ **`Core/Vixen.Ui.Styling` has no `color-mix()`.** `StyleValueParser.ParseFunction` recognises
`rgb`/`rgba` and nothing else; hex and the named-colour table cover the rest. `oklch()` and `oklab()`
are not parsed either, though **`Vixen.Core.Mathematics.Oklab` exists and is checked against
Ottosson's published values** — the maths is there and the CSS surface is not. So:

- **`color-mix()` in `StyleValueParser` is a prerequisite**, not a nicety. One function, two colour
  arguments with percentages, and an interpolation space; `in oklab` is the only space that matters
  and Vixen already has it. **0.25 EM**, and it retires task #12's colour half entirely.
- **`oklch()`/`oklab()` parsing** is the other half, and it is what makes v4's default palette
  expressible at all — every shipped colour in v4 is `oklch(…)`. **0.25 EM**, sharing the conversion
  code with the above.

Once both land, `ThemeTokens.Colors` holding `var(--accent)` stops being a limitation.

✅ **Both landed** — `ColorFunctions` and `StyleValueParser`, A9 and A10 in Part 6. Four things the
sizing above did not know, all of them checked rather than reasoned about:

- **The order of operations is already right and needed no change.** `StyleResolver.Substitute`
  rewrites the value's text and re-interns it during `Build`; `StyleValueParser` only ever runs on
  what a `ComputedStyle` holds. So `color-mix(in oklab, var(--accent) 50%, transparent)` and the same
  mix with the hex written in place arrive as *byte-identical* text. The mix needs no notion of
  `var()`. ⚠ What it does need is the other half of the same fact: ExCSS does not normalise inside a
  function it does not know, so the endpoints arrive as `#4f7cff` and as `red` rather than as
  `rgb(…)`. A mix that accepted only `rgb()` endpoints would have passed every literal test and
  failed on every variable — which is the shape this whole section exists to fix.
- ⚠ **"Both percentages zero is invalid" is wrong.** An older CSS Color 5 draft said so and the claim
  is widely repeated; the current CSS Values 5 algorithm produces **transparent black**, and produces
  it without a special case. The three cases implemented are: one omitted is the other's complement;
  both given are scaled *to* 100% with any shortfall multiplying the result's alpha (`red 20%,
  blue 60%` is 25/75 at 80% alpha); both zero is `rgba(0, 0, 0, 0)`.
- **Premultiplied alpha is the whole mechanism**, CSS Color 4 § 12.3, and without it
  `color-mix(in oklab, blue 50%, transparent)` gives a *dark* blue rather than a translucent one —
  invisible against a dark background. ⚠ Which is also why the opacity modifier must say `in oklab`:
  hue is not premultiplied, `transparent` is black at hue 0°, and `in oklch` therefore rotates every
  colour towards red on its way to being translucent. Browsers do the same; it is why v4's own
  emission names the rectangular space.
- **Changing `TryColor` moved exactly one assertion and zero pixels**, because nothing outside the
  tests uses `/opacity` today — and nothing does *because* the editor's token file carried a warning
  saying it did not work. Every colour in `Editor/Vixen.Editor.Ui/Theming/vixen.ui.yaml` is a
  `var()`, so the whole editor palette was in the silently-dropped class. The warning is gone.

### D3. Container queries are a feature, not a variant ⚠

v4 builds container queries in: `@container` marks the container, and `@sm:`…`@7xl:`, `@max-*`,
`@min-[…]`, named `@container/main` + `@sm/main`, and stacked ranges `@sm:@max-md:` are variants over
it. For a **tool window this is the more correct question than a breakpoint**, and the editor's own
theme file says so already:

> No `screens`. A tool window is not a page: a panel is sized by the dock that holds it and not by the
> display, so a `md:` variant would be asking the wrong question.

That paragraph is an argument *for* container queries, written by someone who had none. It is the
reason this is not optional for an editor: a panel that must lay out differently at 300 px and 900 px
is the normal case, and the mechanism that answers it is `@container`, not `@media`.

⚠ **It needs engine work, and the size is set by a constraint two levels down.** Doc 09 lists
container queries as P2 and unsupported.

⚠ ~~Worse, Vixen's `@media` **does not nest**~~ — **this was wrong, and A15 established that it was
wrong before spending anything on it.** `StyleSheetLoader.LoadMedia` recurses into the rule it has just
matched, so a conditional group rule inside another has always loaded and always conjoined, in either
order with `@layer`. The 0.5 EM budgeted for "a conditional-group rule model in the cascade" bought a
`List<string>` in `UtilityGenerator` and a trie in its emitter, because **the cascade never carried one
condition per rule at all** — `@media` is evaluated at load and discarded, so a `StyleRule` has nowhere
for a condition to live and needed none. The prerequisite existed; what did not exist was a test, and
the belief survived because nothing had ever written a nested query.

**So what is left for `@container` is only `@container`,** and the shape is genuinely different from
`@media`'s in the one way that matters: `@media` is answered **once per document** at load, and
`@container` must be answered **per element**, because the same rule applies to one panel and not to
its neighbour. That is the whole cost, and it is not a parsing cost:

1. **`container-type` / `container-name` as real properties**, read by the layout, plus the
   containment they imply — `size` containment means the container's own size must not depend on its
   contents, which is a constraint the layout has to *enforce* and not merely record, or the query is
   circular.
2. **The resolution walk**: nearest ancestor with a matching `container-type`/`container-name`, whose
   size the layout has already computed. Cheap in itself.
3. **The load-time/match-time split has to move.** Everything else in this cascade is decided at load
   because it is a property of the document; a container query is a property of an *element's
   ancestry*, so either the rule carries its condition to match time — which is the `StyleRule` change
   A15 turned out not to need — or the cascade runs a second pass over the subtree whose containing
   block changed. The second is the same shape as the invalidation the cascade already does and is
   probably right, but it is a real ordering problem: layout depends on style, and a container query
   makes style depend on layout. That cycle is the risk, not the syntax.
4. **The variant table and the `@sm/name` grammar** — `@sm:`…`@7xl:`, `@max-*`, `@min-[…]`, named
   `@container/main`, and stacked ranges `@sm:@max-md:`. Stacking is free now: it is the same at-rule
   chain `sm:md:` uses, and the emitter does not care that a link in the chain is `@container`.

**Size: 0.75 EM**, down from 1.25 — the 0.5 for nested conditional groups is spent and was nearly free.
0.5 for `container-type`/`container-name`, the containment constraint and the resolution walk; 0.25 for
the variant table and the grammar. ⚠ The risk moved rather than shrank: it is now concentrated in item
3, the style↔layout cycle, which is the item this document cannot size from the outside.

### D6. The variants had almost no end-to-end coverage, and that was worth more than A15 ⚠

A15's scope note asked whether the utility system's variants had *any* proof that an element under a
variant computes a different value in a real document — as opposed to a generator test proving the
selector text is spelled right. The audit's answer, family by family, was **four out of twenty-odd**:
`hover:`, `focus:` (only ever stacked with `hover:`), `md:` (only `md:`, none of the other four
breakpoints) and `[&>*]:`. Everything else was `Assert.Contains` on the emitted string, or nothing:

- **Nothing at all**: `peer-*` and `aria-*` — the strings appeared in `Variants.cs` and in no test.
- **Text only**: `dark:` (both strategies), `ltr:`/`rtl:`, `group-*`, `data-*`.
- **Nothing, not even text**: `focus-visible`, `focus-within`, `active`, `disabled`, `enabled`,
  `checked`, `first`, `last`, `only`, `odd`, `even` — eleven of the thirteen entries in one dictionary.
- **Four of five breakpoints**: `sm:`, `lg:`, `xl:`, `2xl:` appear nowhere in any test.

The mechanical cause was one signature: the only end-to-end helper took an `ElementState` and a
`MediaContext` and nothing else, so it could not set an attribute, add an ancestor, or add a sibling —
which is precisely the set of variants that went untested. Fixing the fixture is most of fixing the
coverage.

⚠ **And the gap was hiding a live bug.** `2xl:` emitted `.2xl\:p-4`, which is not a selector: CSS
Syntax 3 § 4.3.8 requires a leading digit to be escaped as a code point (`.\32 xl\:p-4`), because `\2`
begins a hex escape. ExCSS refused the rule and contributed nothing, silently, in every project using
the shipped theme — `--breakpoint-2xl` has been the engine default since C3. One of five breakpoints
tested, and it was not the one whose shape differed.

`VariantCoverageTests` is the answer and it is enumerated rather than listed: it walks
`Variants.StateVariants` and `ThemeTokens.Screens`, checked in both directions, so a variant added
without a scene fails the build and a scene for a variant that no longer exists fails it too. Every
case asserts a computed value positively **and** negatively, because a rule that applied
unconditionally passes every positive assertion ever written about it.

### D4. oklch, and what it costs

v4's default palette is `oklch()`, 22 hues × 11 steps. Vixen can keep hex tokens and lose nothing
functionally, but three things follow from adopting the v4 shape:

1. `oklch()` must parse — D2.
2. Interpolation in Oklab is already what the animator does, so a transition between two oklch
   colours is correct for free.
3. The gamut question is real and is not the styling layer's: `oklch(0.7 0.2 30)` can be outside
   sRGB, and the UI renderer's swapchain format decides what happens. Clamping in `Color4` is the
   honest default and it is what a browser on an sRGB display does.

⚠ **And the gamut question is not academic for this palette — it is load-bearing for most of it.**
Three v4 colours, taken from its `theme.css` and run through the parser:

| | as v4 ships it | out of sRGB by |
|---|---|---|
| `blue-500` | `oklch(62.3% 0.214 259.815)` | linear blue **+1.053** — past white |
| `emerald-500` | `oklch(69.6% 0.17 162.48)` | linear red **−0.039** — past black |
| `red-500` | `oklch(63.7% 0.237 25.331)` | in gamut, and the only one of the three |

Two in three, before anyone writes a vivid colour by hand. So "adopt the v4 palette" and "support
wide-gamut displays" are not two decisions, they are one: on an sRGB display these are clamped and
match v4's own generated hex fallbacks, and on a P3 display they are colours that can actually be
shown and must not be. Which raises the value of doing this properly and lowers the value of any
placeholder that throws the chroma away early.

⚠ **The interim behaviour, now that the parsing has landed: nothing is clamped, and the out-of-gamut
linear triple is carried through with its negative channels intact.** Three reasons, and the third is
the one that matters for whoever picks this up.

*Per-channel clipping is not a smaller version of the right answer, it is a different answer.* Clip
`oklch(0.7 0.4 30)` channel-wise and the hue moves — a vivid red clips towards orange — whereas the
specified repair reduces chroma while holding lightness and hue. A parser that clipped would be
shipping a wrong colour under the name of a placeholder.

*It needs a gamut this assembly does not have.* The repair is against the **display's** gamut, not
sRGB's: on a P3 panel `oklch(0.7 0.3 30)` is in gamut and must not be touched at all. Wide-gamut
support is a stated goal, so a parse-time decision would have to be undone.

*And carrying it makes the real fix strictly easier.* An unclamped value still holds the chroma the
mapper needs; a clamped one has already destroyed it, and no downstream pass can recover a colour
from its own clipping. Nothing breaks in the meantime: `ColorSpace.LinearToSrgb` is the exact
piecewise transfer function, whose linear segment handles negatives without producing NaN — a
`pow()` approximation there would not, which is how "carry it unclamped" would otherwise turn into a
black element three layers downstream. `ColorFunctionTests` pins both halves of that.

✅ **One place used to clamp, and it no longer does.** `StyleValue.ToCss` wrote every colour back as
`rgba()` — channels clamped *and* quantised to eight bits — because that is how the animator hands an
interpolated value back to a cascade that works in interned strings. An out-of-gamut colour therefore
survived being parsed, resolved and drawn, and did **not** survive being animated: one round trip
flattened it to the sRGB byte grid, which for two of every three colours in the table above is a
deletion rather than a rounding. `ToCss` now writes `color(srgb-linear r g b / a)` with unclamped,
round-trippable channels whenever any linear channel is outside `[0, 1]`, and keeps `rgba()` for
everything else. `StyleValueTests` pins both branches, the exact round trip on the first and the
short spelling on the second.

✅ **The mapper, the swapchain rule and the two CSS surfaces have landed.**
`Vixen.Core.Mathematics.GamutMap` implements CSS Color 4 § 14.2.1's binary search with local MINDE
against `ColorGamut.Srgb`/`DisplayP3`/`Rec2020`, with the gamut matrices derived from the
chromaticities in Media Queries 5 § 5.4's table rather than transcribed. Measured on this
implementation: per-channel clipping moves the hue by up to **42.5°** at `L = 0.65, C = 0.37`, where
chroma reduction holds it to **5.5°**. `@media (color-gamut: srgb|p3|rec2020)` matches *ascending*,
and `color(display-p3 …)`, `color(srgb …)` and their `-linear` forms parse into the working space
unclamped; `a98-rgb`, `prophoto-rgb` and `rec2020` are refused rather than decoded with sRGB's
transfer curve, which is theirs to have and not sRGB's.

✅ **And the mapper is now called at presentation, which is what made the switch shippable.**
`UiGeometryBuilder.Gamut` carries the swapchain's *granted* gamut, and every colour the builder emits
— a quad's, a path's, a gradient's far stop — goes through `GamutMap.Map` on its way into a vertex.
`EditorPane` sets it from `ISwapChain.Gamut` on create and on recreate, so a window dragged onto a
wide display picks the wider gamut up with the surface.

**CPU per colour, not per pixel in the shader, and the argument is convexity rather than cost alone.**
Mapping the stops and interpolating afterwards is *not* the same operation as interpolating and
mapping each pixel — but it is sufficient for the property that matters. The UI shader's only colour
combinations are the gradient's `lerp`, premultiplication by coverage and the destination blend, all
convex combinations in the working space; each of the three gamuts is a linear image of the unit cube
and therefore convex; a convex combination of points inside a convex set is inside it. So once the
stops are showable, every pixel between them is. The per-pixel version would differ only in how
chroma is *distributed* along a ramp whose stops were both outside, and would cost a twelve-iteration
search with a cube root per iteration on every fragment of a full-screen surface.

**Measured, Release, per colour:** the `InGamut` early-out costs **6–11 ns**; `Oklab.FromLinear`,
which the specification's ordering paid *before* asking, costs **12 ns**; a full search on a colour
that really is out of gamut costs **≈ 1 060 ns**. So the reorder roughly halves the common path, and
a search costs about a hundred times the question — which is the entire case for caching repeats and
for not caching anything else.

⚠ **`GamutMap.Map` now asks `InGamut` before it converts to Oklab**, reversing the specification's
order. That is the difference between this being affordable per colour per frame and not: a showable
colour used to pay three cube roots to discover it needed nothing, where the test that says so is six
comparisons. It is sound only because no colour inside any of these three gamuts has a lightness
outside `(0, 1)` — they share D65 and normalise white to `L = 1` — so the branches being hopped over
are unreachable for in-gamut input. `Map_agrees_with_the_specification_ordering` pins it against the
original order over generated colours rather than leaving it as an argument.

⚠ **This changes rendering on ordinary sRGB hardware, and no screenshot baseline moved — for a
reason that expires.** An sRGB surface now *maps* where it used to let the `UNORM` attachment clip.
All 43 committed baselines are byte-identical, because no stylesheet in the tree authors `oklch()`
yet: every theme colour is a hex token, in gamut by construction, and the early-out returns it
untouched. **The baselines will move when this palette lands**, and that movement is the fix
arriving, not a regression — the pixels that change are the ones that were being clipped.

⚠ ✅ **The palette landed and the baselines still did not move, and the reason is worth writing down
because it is the opposite of what the paragraph above predicted.** All 43 are byte-identical after
the shipped `@theme`. Shipping a palette is not the same act as *drawing* with one: the only two
suites that take pictures are `Vixen.Ui.Controls.Tests`, whose projects have no theme file and never
run the generator at all, and `Vixen.Editor.Ui.Tests`, whose theme clears `--color-*` and keeps the
hex ramp `EditorTheme` was designed around. Not one element in either carries a class that resolves
to an oklch token. **What expires the prediction is a stylesheet that paints with a v4 colour**, and
nothing in the tree does yet — the palette is reachable, which was the deliverable, and the first
panel that writes `bg-blue-500` is what will move a picture. The spacing change went the same way for
a duller reason: the editor's base moved from 2 to 4, and its markup writes exactly two spacing
classes, `min-w-0` and a `p-3` that appears only in a comment.

⚠ **Three things a reader should not take on trust from the above.** First, the specification now
offers **three** gamut mapping algorithms — binary search, EdgeSeeker, ray-trace — and lets an
implementation choose; the one implemented is the only one whose constants the prose pins down.
Second, the algorithm *ends in a per-channel clip*: the search reduces chroma until a clip of the
candidate is within one JND, then returns the clipped colour. "Not clipping" describes the strategy,
not the last step, and the 5.5° residual is exactly that step. Third, `VK_EXT_swapchain_colorspace`
is now enabled on the instance, and **without it a surface reports only sRGB however capable the
display is** — which is why this could have looked implemented and done nothing.

✅ **`StyleValue.ToCss` no longer clamps.** Once `color(srgb-linear …)` parsed, the fix was a spelling
change rather than a change to the cascade's interchange format, which is why it was cheap enough to
take here. Two details are load-bearing and neither is obvious. **The branch is on the colour, not on
alpha:** a spring overshoots past `1` and `rgba()` carries that through, where `ParsePredefined`
clamps alpha on the way back in — so an in-gamut colour mid-overshoot must stay on the `rgba()` side
or the fix would introduce the bug it removes. **And it uses `float.ToString()`, not the shared
`"0.####"`:** four decimals is a grid too, finer than eight bits and still a grid, and "lossless" is
the entire reason this branch exists. The comparison is `< 0 || > 1`, so a NaN channel — which cannot
be spelled in CSS at all — stays on the `rgba()` path that already absorbs it.

### D5. What v4 removed, renamed, and added

A parity inventory written from v3 memory would be wrong in both directions, so the table was built
from the v4.3.3 registry rather than from recall.

**Removed** (all superseded by the opacity modifier or a rename): `bg-opacity-*`, `text-opacity-*`,
`border-opacity-*`, `divide-opacity-*`, `ring-opacity-*`, `placeholder-opacity-*`.
⚠ **A second group is documented as removed and is still registered** in
`compat/legacy-utilities.ts` — `flex-grow-*`, `flex-shrink-*`, `overflow-ellipsis`,
`decoration-slice`, `decoration-clone`, `bg-gradient-to-*`, `max-w-screen`, `order-none`,
`break-words`, `start-*`/`end-*`. They compile and are undocumented. Vixen should implement the
documented name and not the compatibility one.

**Renamed**, and this one bites: the whole `shadow`/`blur`/`rounded` scale shifted by one step —
v3 `shadow-sm` is v4 `shadow-xs`, v3 `shadow` is v4 `shadow-sm`, and the same for `blur-*`,
`backdrop-blur-*`, `drop-shadow-*` and `rounded-*`. Also `outline-none` → `outline-hidden` (with
`outline-none` re-taking the literal meaning), `ring` → `ring-3`, `bg-gradient-*` → `bg-linear-*`.
**Vixen's `rounded` token scale and the editor's `--radius-*` names must be re-pegged to v4's**, or
every `rounded-sm` in the tree means something one step off what a Tailwind user expects.

⚠ **`ring-*` is not a rename but a change of meaning, and Vixen implements the old one.** v3's
`ring-<color>` set a ring colour; v4's `ring-*` is a **box-shadow** with a width. Vixen's `ring`
family emits `outline-color`, which is v3's reading and also inert. Under v4, `ring-2` should emit a
`box-shadow` — which the draw list already paints.

**Added since v4.0, and easy to miss**: 3D transforms (`rotate-x/y/z-*`, `translate-z-*`, `scale-z-*`,
`perspective-*`, `transform-3d`, `backface-*`), container queries, `inset-shadow-*`, `inset-ring-*`,
`text-shadow-*`, the whole `mask-*` family, `field-sizing-*`, `scheme-*`, `font-stretch-*`,
`zoom-*`, `tab-*`, `scrollbar-*`, `font-features-*`, the logical-property sets (`mbs/mbe`, `pbs/pbe`,
`inset-s/e/bs/be`, `inline-*`/`block-*` sizing), the `-safe` alignment family, `items-baseline-last`,
and the variants `not-*`, `in-*`, `nth-*`, `starting`, `inert`, `*`/`**`.

⚠ **One correction to the brief:** `!important` moved to a **suffix** in v4 (`bg-red-500!`), not a
prefix. Vixen's parser already reads a trailing `!`, so Vixen matches v4 here and would have had to
change to match v3.

---

## Part 3 — The scanner is per-project, and that is a design question

⚠ **A utility class written outside `Vixen.Editor.Ui` resolves to nothing, with no diagnostic.**
`Core/Vixen.Ui.Styling.Utilities/build/Vixen.Ui.Styling.Utilities.targets` globs `@(Compile)` plus
`**/*.vxml;**/*.vcss` **within the consuming project**, finds `**/vixen.ui.vcss` **within the
consuming project**, and errors if there is more than one — *"One project is one palette."* ⚠ The
shipped default has made this **worse rather than better**: the target used to not run at all without
a token file, which was at least loud, and a project with none now generates v4's palette against its
own sources — so an assembly that meant to share the editor's tokens and forgot gets Tailwind's
instead of nothing. So `Vixen.Editor.Profiler`,
`Vixen.Editor.Debugger` and `Vixen.Editor.AssetEditors` produce no utility sheet at all, and a panel
ported to VXML in one of them has to fall back to tag-based theme rules. That workaround has already
been taken once.

"One project is one palette" is the right invariant and the wrong unit. The unit is the **theme**, and
the editor is one theme spanning a dozen assemblies.

**Three shapes, and the third is the one to build.**

| | How | Why not |
|---|---|---|
| **A · One sheet, scanned across the solution** | a build step that globs every project | breaks incremental build and project independence; a plugin outside the tree cannot take part |
| **B · Per-assembly sheet, per-assembly tokens** | copy the yaml | two palettes, which is the failure the token model exists to prevent |
| **C · Per-assembly sheet, shared token source** | `VixenStyleTokens` is a *reference*, not a file in the project | ✅ |

Under **C**, `Vixen.Editor.Ui` owns the tokens and every other editor assembly names it. Each project
still scans only its own sources and emits only its own rules — incrementality intact — and every
sheet lands in `@layer utilities` where document order does not matter, so the union behaves as one
sheet at runtime. The targets file already has the seam: `VixenStyleBase` lets a project name base
sheets, and the generation target's condition is `@(VixenUiTokens) != '' OR @(VixenStyleBase) != ''`.
What is missing is a way to say *"my tokens are that project's"*.

⚠ **Under v4 this gets simpler, not harder,** which is the argument for doing Part 2 § D1 first: if
tokens are an `@theme` block in a `.vcss`, then "share the tokens" is `@import` — a mechanism the
style engine already supports — and the MSBuild item is a path to a stylesheet rather than a new
concept.

⚠ **The guide page master added — [`docs/guide/editor/utility-styles.md`](../guide/editor/utility-styles.md)
§ Examples — documents the workaround rather than the shape.** *"Turning the step on in another project
is two lines and a file"*, and the file is a second `vixen.ui.vcss`: a second palette, which is the
failure the token model exists to prevent. It also still carries *"`overflow-auto` is in neither
column"*, which F3 has since made untrue. Both should be revised when C4 lands.

**And it wants a diagnostic either way.** A class name that parses as a utility, in a project with no
token source, should be a build warning naming the project. The generator already writes an
`unrecognised.txt`; nobody reads it because in the normal case it is noise. In the *no tokens at all*
case it is the whole answer.

---

## Part 4 — The reference implementations

Three were proposed. Each claim was checked against the project's own repository rather than
accepted, and **four of the claims are wrong** — one of them in a way that changes the licence
paperwork and one in a way that removes the reason the reference was proposed at all.

The methodology this is being judged against already exists here. ADR-006 re-implements Yoga's
*algorithm* against a struct-of-arrays store and judges it by 534 fixtures translated from Yoga's
C++ by `Tools/Vixen.YogaTestGen`, every expected number originally out of a real browser. The
question for each reference is therefore not "is the code good" but **"is its corpus translatable the
same way"** — and `Core/Vixen.Ui.Layout/README.md` records what that methodology does not buy:
deleting CSS Flexbox §4.5's automatic minimum size leaves all 534 green, because Yoga's generator
emits no fixture that shrinks a measured leaf past its content. An oracle answers the questions it
was built to ask.

### T1 · Taffy — confirmed for grid and block, and the corpus is better than Yoga's

⚠ **Licence claim refuted: Taffy is MIT only, not dual MIT/Apache-2.0.** One `LICENSE` file, plain
MIT; `Cargo.toml` says `license = "MIT"`; no Apache text in the repository. The copyright line is
`Copyright (c) 2018 Visly Inc.` — Visly owned Stretch, so the lineage is in the licence header. The
practical difference from the claim is that MIT carries **no express patent grant**, which is the
specific thing ADR-015 says Apache-2.0 is worth more than MIT *for*. It does not block anything —
Yoga is MIT too and is already a reference — but it is not the dual licence it was said to be.

**Flexbox + grid + block over one tree model: confirmed**, and floats as well, which the claim did not
mention. `src/compute/` holds `block.rs`, `flexbox.rs`, `grid/` (10 files), `float.rs`, `leaf.rs`,
over `src/tree/`. Fifty source files.

**Stretch/Yoga descent: true in substance, not where the claim said.** The README has no history
section; the lineage is stated in `scripts/import-yoga-tests/README.md` — *"Taffy's predecessor
Stretch was originally descended from"* Yoga, *"whose tests are compatible with Taffy's generated test
infrastructure"* — and in the CHANGELOG's note that the crate was renamed from `stretch2`. ⚠ Stretch's
own README never described itself as a Yoga port, so the "port" framing is Taffy's characterisation
rather than Stretch's. It is enough for what matters: the data model is the same lineage as the one
`LayoutTree` was built against.

⚠ **The fixtures are generated from Chrome, and they are no longer Rust — they are XML.** That is the
finding that changes the plan. `just gentest` downloads a matched Chrome-for-Testing + ChromeDriver
pair, drives it over WebDriver, injects a DOM walker that captures the computed style plus **three**
geometry readings per node (unrounded `getBoundingClientRect`, naive `offsetWidth`, and a
round-the-edges variant), and emits each fixture under **four** configurations —
`border-box`/`content-box` × `ltr`/`rtl`.

| | Yoga | Taffy |
|---|---|---|
| Fixture sources | 25 HTML files | **1 335** HTML files (17 disabled) |
| Generated tests | 543 C++ | **5 272** XML (1 318 × 4) |
| Emitted as | language-specific source | **language-neutral XML** |
| Coverage | flexbox | flex 2 268 · **grid 1 960** · **block 868** · float 12 · mixed 108 |
| Text measurement | Ahem font | a 40-line Ahem *stub* — `H_WIDTH = 10` |

```xml
<test name="chrome_issue_325928327__border_box_ltr" use-rounding="true">
  <viewport width="max-content" height="max-content"/>
  <input><div display="grid" justify-items="center" width="100%" height="40px">…</div></input>
  <expectations><node x="0" y="0" width="40" height="40">…</node></expectations>
</test>
```

**So `Tools/Vixen.TaffyTestGen` is a smaller thing than `Vixen.YogaTestGen`, not a bigger one.**
`CppTranslator.cs` is a 265-line line-by-line translator that is defensible only because its input is
machine-generated C++ with no control flow — and it drops a whole fixture rather than guess at a line
it does not recognise. Against XML there is nothing to translate: an `XmlReader`, a map from the ~90
style attributes onto `LayoutStyle` setters (`StyleSetters.cs` is already that map, for Yoga's
setter names), and an emitter. The `<expectations>` tree maps onto the same assertions the existing
generated tests make. Estimate **0.4 EM**, against a corpus ten times the size, and it subsumes the
Yoga suite rather than replacing it — keep both, because 534 green fixtures are 534 green fixtures.

⚠ **And the Ahem stub is why this is possible at all.** Running Taffy's corpus does not need a text
engine: `TestMeasureData::AhemText` is `H_WIDTH = 10.0`, `H_HEIGHT = 10.0`, split on U+200B,
min-content is the longest segment × 10, max-content the whole length × 10. Forty lines of C#, and
the grid and block suites run before `Vixen.Ui.Text` is involved at all.

**Consuming rather than porting is not an option.** The library is 100 % Rust; the C bindings (PR
#404) and WASM bindings (PR #394) are both still open drafts. So this is a port of the algorithm from
`src/compute/`, on exactly ADR-006's terms — and the corpus is the prize, and the corpus needs no port.

### T2 · Parley — confirmed as a design reference, **refuted for the role it was proposed for**

⚠ **`text-overflow: ellipsis` is the one thing Parley does not do. The word "ellipsis" does not appear
anywhere in the repository.** It was proposed for inline formatting *and* ellipsis; the second half is
simply absent, and that has to be sourced elsewhere.

**Licence confirmed: `Apache-2.0 OR MIT`**, `LICENSE-APACHE` and `LICENSE-MIT` at the root and in each
of the eight published crates, per-file SPDX headers agreeing.

⚠ **The shaping layer is HarfRust, not swash** — the CHANGELOG records the switch, and text analysis
moved to icu4x. That is *better* for Vixen than the claim: HarfRust is a Rust port of HarfBuzz, so its
shaping is behaviourally aligned with `Vixen.Ui.Text`'s HarfBuzzSharp. ⚠ `doc/design.md` is stale and
still describes swash and druid; do not read it as current.

**Scope is broader than the claim, in the useful direction.** "Only text runs" is refuted:
`parley/src/inline_box.rs` has `InlineBox { id, kind, index, width, height, baseline }` with
`InlineBoxKind::{InFlow, OutOfFlow, CustomOutOfFlow}`, documented against `display: inline-block` and
`position: absolute`, with baseline alignment across mixed-size boxes tested. That is the atomic-inline
model — you supply the box's measurements and Parley places it — and it is the right model for Vixen,
where an inline image or a nested control is a `UiElement` the layout has already measured.

⚠ **Floats it does not do.** `parley_tests/tests/floats.rs` imports `taffy::{Clear, FloatContext,
FloatDirection}`: Parley yields to the caller and the test uses **Taffy's** float context. The two are
designed to compose, which is a second argument for taking both from the same architecture.

**The test corpus is not thin — and it is still nearly useless as an oracle.** 161 tests over 20
files, driving **281 PNG snapshots** — and those snapshots are pixel comparisons against Vello
renders, encoding Parley's own rasteriser, hinting and font stack. Worthless to a C# implementation,
for the same reason WPT's reftests are.

⚠ **The exception is worth the whole reference.** `parley_tests/linebreaking_browser_recorder/data/`
holds `Roboto.csv` and `Arimo.csv`, 1 024 rows each, recorded from **Chrome 149**, columns
`seed,width_subpixels,first_line_chars`. It is the only artefact found anywhere that oracles *actual
break positions at actual widths in actual fonts against a real browser*, and both fonts are in-repo
and openly licensed. ⚠ Its inputs are regenerated from the seed by a **ChaCha8** generator, so reusing
the CSVs verbatim means reimplementing that generator bit-exactly; re-recording with Vixen's own
generator through their `index.html` harness is the cheaper path.

⚠ **And the single most valuable file is prose.** `parley_engine/src/break_overrides.rs` documents,
with line-level citations into Chromium, exactly where browsers knowingly deviate from UAX #14 —
Chrome always allowing a break after a space run in violation of LB13, the hyphen-before-digit rule,
the nine cases where Chromium and Safari differ, and that Firefox defers to stock ICU. There is a test
named `chromium_ignores_uax_14_lb13`. Vixen already has `LineBreakConformanceTests` green against the
Unicode suite, which means **Vixen currently implements UAX #14 as written and browsers do not** —
that file is the list of deltas, and it is not obtainable anywhere else.

### T3 · web-platform-tests — confirmed as an oracle, and scoped much more narrowly than proposed

**Licence confirmed: BSD-3-Clause.** One `LICENSE.md` at the root, titled "The 3-Clause BSD License",
no per-directory exceptions anywhere under `css/` — all 55 854 entries checked; the only "license"
matches are WOFF2 test *filenames*.

⚠ **`css/` is three-quarters reftests, and a reftest needs a renderer.** From the real `MANIFEST.json`
(39.5 MB, schema version 9):

| type | whole suite | under `css/` |
|---|--:|--:|
| reftest | 27 495 | **24 552** (76.7 % of runnable css/) |
| testharness | 30 828 | **7 464** (23.3 %) |
| visual · manual · crashtest · print-reftest | 8 136 | 5 383 |

⚠ **And the testharness number overstates the oracle by a factor of seven.** Of the 7 464, three
renderer-free families are usable: ~914 use `support/parsing-testcommon.js` (property grammar and
serialisation), ~456 use `support/computed-testcommon.js` (computed values), and **1 044** use
`resources/check-layout-th.js`, which is the one that asserts geometry — and asserts it from
attributes written **inline in the HTML**: `data-expected-width`, `data-offset-x`,
`data-expected-scroll-width`, `data-expected-padding-*`, at 1 px tolerance. Those are statically
parseable with no browser, no renderer and no JS engine: structurally the same artefact as Taffy's XML.

⚠ **Where those 1 044 live decides what this reference is good for.**

| directory | check-layout tests |
|---|--:|
| css-grid | 510 |
| css-flexbox | 215 |
| css-align | 101 |
| css-sizing | 89 |
| css-tables · css-box | 84 |
| css-overflow · CSS2 · css-multicol · css-position | 58 |
| **css-inline** | **14** |
| **css-text** | **4** |
| **css-writing-modes · css-display** | **0** |

Seventy per cent of it is grid and flexbox — Taffy's domain, where it is a *second* oracle rather than
the only one. For **block, inline, writing modes and text, WPT is effectively reftest-only**, which is
exactly the half Vixen has no oracle for. `CSS2` is 6 221 reftests to 63 testharness.

`MANIFEST.json` is the machine-readable index (`./wpt manifest`, or pregenerated from
`https://wpt.fyi/api/manifest?sha=latest`); `items.testharness` is walkable, which is what jsdom does.
Per-property mapping is not in it — that needs `./wpt spec` → `SPEC_MANIFEST.json`, whose entries
carry the `<link rel=help>` targets.

⚠ **The realism check, and it is the most useful number in this part.** **Blitz** — a Rust engine built
on exactly the architecture proposed here, **Stylo + Taffy + Parley** — publishes a WPT report. Computed
from it: **10 611 / 25 150 = 42.2 %** of `css/` passing, split reftests 43.0 % and attribute tests
20.2 %; css-ui 90.5 %, css-flexbox 60.1 %, CSS2 55.7 %, css-grid 33.0 %. Servo, which has been at this
for a decade, carries **18 807 expectation files and 139 917 `expected: FAIL` lines**. Neither number
is an argument against WPT; both are an argument against ever quoting a WPT percentage as a goal.

⚠ **And when a runner is eventually needed, do not write one.** Blitz's is ~53 KB
(`wpt/runner/src/main.rs`): it globs the filesystem and sniffs the type by regex — `<link rel=match>`
is a reftest, exactly one `checkLayout('sel')` call is an "attr test" walked against the layout tree
**with no JS at all**, everything else is skipped. That is the blueprint. The alternative is upstream
`wptrunner` with `--test-types testharness`, a first-class mode that does **not** require WebDriver:
`executorwktr.py` is a stdin/stdout line protocol — send a URL, read a text block and an image block,
`#EOF` — and since 2025 a product can live out-of-tree as a pip package.

### T4 · What the three do not cover, and where it comes from

Inline formatting and `text-overflow: ellipsis` are Vixen's weakest area and are the area all three
references are weakest on. Four sources close it, and the licence gradient decides which are read and
which are transcribed.

⚠ **Vixen already has the first one and should not be told it is new.** `LineBreakConformanceTests`
and `BidiConformanceTests` run the Unicode Consortium's own `LineBreakTest.txt` (19 338 cases) and
`BidiCharacterTest.txt`, fetched by the recipe in `references/README.md` and read by
`Tools/Vixen.UnicodeTableGen`. What is *not* covered is the **CSS tailoring** of line breaking, and
there is exactly one plain-data oracle for it anywhere: **ICU4X's**
`components/segmenter/tests/css_line_break.rs` (72 assertions) and `css_word_break.rs` (31), which
encode `line-break: loose/normal/strict/anywhere` and `word-break: keep-all/break-all` with `ja`/`zh`
content locales. Unicode-3.0, permissive, and small enough to transcribe by hand.

**For ellipsis specifically**, since Parley has nothing: the reference implementation is Chromium's
`third_party/blink/renderer/core/layout/inline/line_truncator.cc`, and the directory is
**BSD-3-Clause** with 22 `*_test.cc` files written as `SetBodyInnerHTML(…)` then asserting exact line
strings — hand-transcribable into C# fixtures. ⚠ **The licence trap is one directory up**:
`layout/layout_text.cc` and its siblings are **LGPL** (`(C) 1999 Lars Knoll`). Stay inside `inline/`.
The oracle is **Gecko's 68 `text-overflow` reftests** and 39 `line-breaking` reftests — the best
corpora found anywhere for these two, with per-test fuzz tolerances in machine-readable
`reftest.list` manifests, but ⚠ **mixed public-domain and MPL-2.0 per file**, so a translated fixture
derived from an MPL file is itself MPL and each file needs checking before it is transcribed.

**Servo's `components/layout/flow/inline/`** is the best free prose description of a three-phase
HarfBuzz-shaping inline engine and names Vixen's exact problem shape — atomic inlines,
`LineItem::Float`, deferred baseline resolution. ⚠ Its in-repo inline test corpus is **2.4 KB, one
test, thirteen assertions**; everything else is WPT. Read it; expect no oracle from it. Same for
**Stylo**, which is a style engine rather than a layout one and is the reference for the parsing and
computed-value layer that WPT's 914 + 456 tests target.

⚠ **cosmic-text was considered and is excluded.** MIT/Apache-2.0 and it sounds relevant, but it is not
a CSS inline engine — no floats, no inline-block, no `vertical-align`, no CSS — its roadmap still has
`Ellipsize` unchecked, and its 24 golden PNGs are git-lfs pointer stubs encoding its own rasteriser.

### T5 · Licences, `NOTICE` and ADR-015

Vixen is Apache-2.0 (ADR-015). ADR-015's table already distinguishes a dependency from *reference
material*, and Yoga's row — `*Reference material:* Yoga | MIT | algorithm + conformance suite
(ADR-006)` — is the precedent for every row below.

⚠ **"Reading" and "porting" are not the two cases. There are three, and the third is the one that
carries the obligation:** translating a corpus and committing the result. Vixen's 534 generated
fixtures are a derivative work of Yoga's MIT-licensed fixtures, redistributed in every clone. The same
will be true of Taffy's.

| Artefact | Licence | What we do | `NOTICE` / ADR-015 |
|---|---|---|---|
| **Taffy** | **MIT** *(not dual)* | port the grid and block algorithms; **translate and commit 5 272 fixtures** | ⚠ ADR-015 reference-material row **and** a `NOTICE` entry — the fixtures are redistributed. Modification notice (§4b) on ported files, as for Yoga |
| **Parley** | Apache-2.0 OR MIT | read `break_overrides.rs` and `inline_box.rs`; possibly commit the two Chrome CSVs | ADR-015 row. A `NOTICE` entry **only if the CSVs are committed** — take Apache-2.0, matching Vixen |
| **web-platform-tests** | BSD-3-Clause | read; translate the ~1 500 renderer-free `check-layout`/`computed` tests | ⚠ BSD-3 requires the copyright notice **and the disclaimer** to travel with a redistribution, so a `NOTICE` entry is required the moment a translated fixture lands |
| **ICU4X segmenter tests** | Unicode-3.0 | transcribe ~100 CSS-tailoring assertions | `NOTICE` entry; Unicode-3.0 is MIT-like |
| **Chromium `layout/inline/`** | BSD-3-Clause | read; transcribe test cases | ADR-015 row. ⚠ Rule: `inline/` only — the parent directory is LGPL |
| **Gecko `text-overflow` reftests** | mixed public domain / **MPL-2.0** | transcribe, **per-file check first** | ⚠ MPL-2.0 is file-level copyleft: a fixture derived from an MPL file is MPL. Prefer the public-domain ones; record which |
| **Servo · Stylo · Blitz's `stylo_taffy`** | **MPL-2.0** | ⛔ read only, never port | ADR-015 row marked read-only, as `stride` already is |
| **Blitz** | MIT OR Apache-2.0 | read its WPT runner as a blueprint | ADR-015 row if any of it is adapted |
| **Unicode UCD** | Unicode-3.0 | already used | already in the tree |

⚠ **The licence gradient should shape the architecture and not be discovered afterwards.** Taffy (MIT)
and Parley (Apache/MIT) are freely portable. Servo, Stylo and Gecko are MPL-2.0 file-level copyleft.
Blink is BSD in the new directories and LGPL in the old ones. **Port algorithms only from the
MIT/Apache sources; read the copyleft ones for understanding and transcribe only test cases whose file
is clear.** That rule costs nothing here, because the two references that are permissive are also the
two whose corpora are worth having.

All of these belong in `references/`, which already exists for exactly this and is cloned as a local
decision rather than committed — [`references/README.md`](../../references/README.md) is where the
clone lines go.

---

## Part 5 — Three tracks, and why the ordering is forced

**A · Properties.** The 86 the engine acts on become the 258 the utilities name — fewer in practice,
since 8 are vendor-prefixed shims and a further group belongs to the modes in Track B. Each item is a
consumer change — a name interned, a value parsed, a draw command emitted — and most are independent
of each other.

**B · Layout modes.** `display` is `{ Flex, None }`. Block, grid and inline formatting are three
algorithms over the existing store.

**C · Families.** The 328 roots.

⚠ **C depends on A and B, and inverting that is how the present state came about.** `grid-cols-3`
exists as a family and emits `grid-template-columns` because a family is a line of a table and the
grid algorithm is a subsystem — so the cheap half was done and the class name has been available, and
inert, ever since. The same is true of `blur-*`, `fill-*`, `ring-*` and
`select-none`. **Eighteen of the 90 properties the utilities emit reach no consumer after ExCSS
expansion**, and each one is a class somebody can write today that does nothing:

```
--blur  --rotate  --scale  --translate-x  --translate-y
border-inline-end-color  border-inline-start-color
fill  stroke  grid-column  grid-template-columns
outline-color  user-select  vertical-align
transition-property  transition-duration  transition-timing-function
```

⚠ **That list is the survey's, kept as written.** Eight of the eighteen have since been retired —
`grid-column`, `grid-template-columns`, `vertical-align`, the three `transition-*` and the two
translations — and two more changed their names rather than their state, because `--scale` and
`--rotate` were never properties any engine would read. `InertProperties.txt` is the live version; this
is what the survey found.

✅ **That list is no longer written down here. It is measured, and the block above is what the
measurement currently says** — eleven, printed by
`Core/Vixen.Ui.Styling.Utilities.Tests/UtilityConsumptionGateTests` on every run and mirrored line for
line in `InertProperties.txt`, each with the task that closes it. It was twenty when the survey was
taken: `overflow-x` and `overflow-y` came off when F3 landed, `order` and three of the five per-edge
border colours when the draw list learned the rest of the longhands, the grid pair and
`vertical-align` came off with B2 and B3, and the three `transition-*` names went **on** when F10
found that nothing runs the animator — and **off again** when A20 made one run, which makes them the
only names to have been in the file twice.

⚠ **The gate is a test and not `CheckArchitecture`, and the reason is the difference between
"interned" and "acted on".** This document's own § Part 0 measured that gap at seven properties —
`word-spacing` and `text-indent` have ids in `LayoutStyleBuilder` that nothing reads — so a check that
looked for an `Intern("…")` call would pass both, and pass every future one. Establishing consumption
means *running a frame*: building a document, resolving real elements, changing one declaration and
comparing the layout, the draw list, the cursor and the hit test either side of it. `CheckArchitecture`
is a walk of `.csproj` XML with no compilation and no runtime, and the static alternative — reading IL
for a load of the property id — needs Mono.Cecil, which ADR-002 bans by name in that very file. So the
gate lives where the engine can be run, and `./build.sh Test` is the same gate CI runs.

The allow-list is the honest form of the README's "a utility waiting for an engine feature" — same
sentence, but **it expires, on its condition rather than on a date**: the run in which a consumer
starts acting on an allow-listed property is the run that fails on its exemption, and a line must name
a task number this document contains or the build fails on the line itself. That is the mechanism
`docs/DocsExempt.txt` never had, which is why its written instruction not to abuse it did not hold.

---

## Part 6 — The work, sized

Effort in engineer-months, on the same scale as docs 41 and 42. Items marked 🟢 are an afternoon to a
few days; 🟡 is a week or two; 🔴 is a subsystem.

### Track A — properties

| # | Item | Consumer | Task | EM |
|---|---|---|---|--:|
| A1 🟢 | Per-edge border widths and colours in the draw list | `DrawListBuilder` | **#21** | 0.15 |
| A2 🟢 | Per-corner radii in the draw list (`UiShape` already carries them) | `DrawListBuilder` | **#21** | 0.1 |
| A3 🟢 | `border-style` — solid, dashed, dotted, double, none | `DrawListBuilder` | — | 0.15 |
| A4 🟢 | `order` | `LayoutStyleBuilder` + flex line ordering | **#22** | 0.1 |
| A5 ✅ | `overflow-x`/`overflow-y`, and `auto` in the layout keyword table | `OverflowReader`, `LayoutStyleBuilder` | done | — |
| A6 🟢 | `user-select`, `outline`, `fill`/`stroke` on `OnDraw` paths, and `overflow-clip` | `UiDocument`, `DrawContext` | **#24** | 0.25 |
| A7 🟢 | **Transforms — the translation is done and the other two are refused, on purpose.** `translate-x-*` and `translate-y-*` are composed (a `--tw-*` fragment per axis, one `translate` between them, both classes assemblers) and read by `TranslationReader` in `UiDocument.Accumulate` — the same sum that already carried `OffsetX`, so the draw list, the hit test and arrow navigation all read one translated position and *cannot* disagree. Lengths and percentages, percentages against the element's own border box per Transforms 1 §8; not layout, so siblings do not move; the subtree comes along; a translated clip moves with the box and is still a rectangle. Interpolatable for free, because `StyleValue` already lerps a two-part list. ⚠ **Owed: `scale` and `rotate`, and neither is waiting for a reader.** A `DrawCommand` is an axis-aligned rectangle and the clip stack intersects rectangles, so a rotated box — and a rotated clip — cannot be represented at all, and a bounding-box approximation would draw a 45-point square where a 32-point one was asked for. Scale can scale the box and not the picture: glyph advances are shaped at `run.Size` during *layout*, so a scaled subtree needs re-shaping, which is the one thing §3 forbids. Both need the offscreen compositor `DrawListBuilder`'s opacity remark already owes | `TranslationReader`, `UiDocument` | **#23** | 0.35 |
| A8 🟡 | `filter` and `backdrop-filter`, blur first | UI renderer | **#28** | 0.75 |
| A9 ✅ | `color-mix()` in `StyleValueParser` — four interpolation spaces (`srgb`, `srgb-linear`, `oklab`, `oklch`) with the four hue methods, premultiplied alpha, and the CSS Values 5 percentage normalisation. `UtilityFamilies.TryColor` emits one for `/opacity`, which retires **#12**'s colour half: an opacity on a token that is not a hex triple used to be dropped silently, and every token in the editor's palette is a `var()`. **Owed:** the interim out-of-gamut behaviour is *carry it unclamped* — see § D4 | `Vixen.Ui.Styling`, `ColorFunctions` | done | — |
| A10 ✅ | `oklch()`/`oklab()` colour syntax, both notations, `none`, and every angle unit | `Vixen.Ui.Styling` | done | — |
| A11 🟢 | Backgrounds. **`linear-gradient()`, `radial-gradient()` and `conic-gradient()` all paint**: `background-image` is parsed into `BoxStyle`, all eight direction keywords with CSS's corner rule, all four angle units, both colour notations, two or three stops, arbitrary stop positions inside or outside the box, `in srgb` / `in srgb-linear` / `in oklab`, and it layers over `background-color` as CSS does. `bg-radial` and `bg-conic` are assemblers now, and every assembler emits `in oklab` for v4 parity. Everything else is *refused loudly* rather than approximated — see `GradientRefusal`. `UiShape` grew 80 → 112 bytes; `UiShapeLayoutTests` and `CheckShaders` are what keep its four files in step. **Owed:** an explicit radial/conic centre, `bg-conic-<angle>` (the parser and shader do `from <angle>`; the *utility* needs a numeric family), `background-position`/`-size`/`-repeat`, and gradient text — see [what a third stop cost](#what-a-third-stop-cost) | `DrawListBuilder`, `BackgroundGradient`, `UiShape`, `Ui.rvn` | **#43** | 0.15 |
| A12 🟡 | Pseudo-elements materialised — `::before`/`::after` with `content` | `StyleRuleSet`, `UiDocument` | — | 0.5 |
| A13 🟢 | The 22 selector-only variants (`empty`, `nth-*`, `*-of-type`, form states) | `Variants`, `ElementState` | — | 0.3 |
| A14 🟢 | The 13 media-feature variants | `MediaQuery` | — | 0.2 |
| A15 ✅ | **Nested conditional-group rules — done, and for a tenth of the estimate, because the cascade already did it.** `StyleSheetLoader.LoadMedia` has always recursed into the rule it matched, so `@media A { @media B { … } }` loaded and conjoined; the thing that could not nest was `UtilityGenerator`, carrying one `string?` for the whole variant stack. It carries an ordered, deduplicated chain now and emits a trie over those chains, so `sm:md:p-4` and `dark:md:p-4` nest and share their outer wrapper with the shallower utilities. **Nesting cost the rule representation nothing** — a `StyleRule` still carries no condition. ⚠ The real finding was next door: see § D6 | cascade | — | done |
| A16 🟡 | Container queries: `container-type` and its containment constraint, the resolution walk, the `@` variants. ⚠ **Re-sized from 0.75 by A15**: nested conditional groups are done and the at-rule chain does not care that a link is `@container`, so the remaining risk is one item — a container query makes style depend on layout, and layout already depends on style. See § D3 | cascade + layout | — | 0.75 |
| A17 🟢 | `has-*` | `SelectorMatcher` + invalidation | doc 09 P2 | 0.4 |
| A18 🟢 | Scroll properties as `ScrollView` inputs rather than CSS | `Vixen.Ui.Controls` | — | 0.3 |
| A19 🟢 | `text-decoration`, `text-transform`, `font-variant-numeric`, `font-stretch` | `Vixen.Ui.Text` | — | 0.4 |
| A20 ✅ | **Run the `Animator`** — built on the style engine, `Observe` from the updater, `Advance` on the tick, `Apply` before the consumers read (F10). **Landed with F11**, which the same seam turned up: `UiDocument` never handed the cascade a `MediaContext` either, so every breakpoint, every `dark:` under the media strategy and every `color-gamut` query was dead | `StyleEngine`, `UiDocument` | **#46** | done |
| | | | **A total** | **6.4** |

### Track B — layout modes

| # | Item | Task | EM |
|---|---|---|--:|
| B0 ✅ | **`Tools/Vixen.TaffyTestGen`** — XML vetter and consolidator, the attribute map, and the Ahem measure. **Landed with 5 524 fixtures, not 5 272**: 884 block, 2 040 grid, 2 352 flex, 84 float, 56 leaf and 108 across three hybrid categories the estimate missed. Flex result: **2 002 of 2 208 runnable pass** | — | done |
| B1 🟢 | **`display: block` — landed.** Block formatting over the existing store: stacking, the inline-axis fill, CSS 2.1 §8.3.1 margin collapsing in full, auto margins, the intrinsic-width probe, RTL, relative insets, `align-content` over the stack. **746 of the 912 `block`+`blockflex` fixtures pass**; 124 are refused for `scrollbar-width`/`text-align`/`flow-root`/`float`, and all 42 failures are in the shared *absolute* path (auto margins on abspos, and `aspect-ratio` re-applied after clamping) rather than in block formatting. ⚠ **Still owed under B1**: `inline-block` — deliberately unmapped, because without an inline formatting context it would take the whole line (B3); the 84 `float` fixtures, which were never gated on `display`; and `sticky`. | **#25** | 0.35 |
| B2 🔴 | **CSS Grid** — a separate algorithm; `grid-template-*`, `fr`, `minmax`, `repeat`, `auto-flow`, named lines and areas, placement, `justify/align-items/self`. Judged by B0's **2 040** plus WPT's 510 `check-layout` grid tests. ⚠ B0's corpus does **not** cover `grid-template-areas`: Taffy's own XML harness leaves it `Default::default()` and no fixture sets it, so named areas need their own oracle | **#27** | 3.5 |
| B3 🟡 | **Inline formatting — partially landed.** Line boxes over the existing store: atomic inlines (`inline`, `inline-block`, `inline-flex`), §10.3.9 shrink-to-fit, §9.4.2 line breaking, §10.8.1 baselines including the last-line-box and `overflow` clauses, and three of `vertical-align`'s eight values. ⚠ **The boundary is one invariant, not a feature list**: every algorithm in the store preserved *one node produces one box*, and a non-replaced `inline` box crossing a line break is **fragmented** into several — a `LayoutResult` holds one rectangle. So atomic inlines are done and non-atomic ones are not. ⚠ **Still owed under B3**: fragmentation, anonymous block boxes (so mixed content stacks), the strut and therefore the five font-relative `vertical-align` values, `text-align`, `white-space`, `text-overflow: ellipsis`, `line-clamp`. ⚠ **Zero fixtures**, confirmed by enumeration — Taffy's `display` attribute takes five values across all eight files and none is inline. Oracle fetched from WPT (`css-flexbox/inline-flex.html`); see `InlineKnownGaps.txt`. | **#26** | 1.9 of 3.0 |
| B3a 🟡 | The inline oracle: ICU4X's CSS line-break tailorings, Parley's 2 048 Chrome break cases, and Gecko's 68 `text-overflow` reftests transcribed | — | 0.5 |
| B4 🟡 | `display: table` and the four table utilities | — | 1.0 |
| | | **B total** | **9.4** |

⚠ **B2 and B3 are each a subsystem and flattening them into a list of families would be the second
version of the mistake this document is about.** CSS Grid is a harder specification than flexbox, and
doc 09 has said so since the beginning. Inline formatting is the one doc 09 explicitly scoped *out* —
"a full CSS inline formatting context is out of scope and stated as such" — and that scope line is
what has to be reopened, because `truncate`, `line-clamp-*`, `align-*`, `text-overflow` and every
mixed-content paragraph sit behind it.

### Track C — families

| # | Item | EM |
|---|---|--:|
| C0 🟢 | The next-longest-prefix fallback in `SplitName` (F8) — unblocks every per-edge/per-corner family | 0.1 |
| C1 🟢 | Arbitrary properties, and v4's `bg-(--var)` shorthand | 0.15 |
| C2 🟢 | Re-peg the `shadow`/`blur`/`rounded` scales to v4's names (D5) | 0.1 |
| C3 ✅ | `@theme` replaces `vixen.ui.yaml`; `ThemeTokens` reads a stylesheet, and v4.3.3's palette ships as the engine default in oklch (D1, D4) | 0.5 |
| C4 🟢 | Cross-assembly token sharing, shape C (Part 3) | 0.3 |
| C5 🟡 | The gate: a family emitting a property no consumer **acts on** fails the build (#11) — ✅ landed as `UtilityConsumptionGateTests` with its expiring allow-list. ⛔ Still owed: `Tools/Vixen.TailwindParity` regenerating the TSV from a committed registry snapshot, which is the half that needs the Tailwind registry and cannot be a test | 0.2 |
| C6 🟢 | Doc 09's five missing families — `space`, `divide`, `mix-blend`, `origin`, `scroll` | 0.25 |
| C7 🟢 | The ~120 families that are a table line each, once A and B land | 0.75 |
| C8 🟡 | The families that are their own small feature: `mask-*`, gradients, `animate-*` | 0.75 |
| | **C total** | **3.2** |

### Cost

| Track | EM |
|---|--:|
| A — properties | 6.2 |
| B — layout modes | 9.4 |
| C — families | 3.2 |
| **Total** | **18.8** |

⚠ **Two thirds of that is B2 and B3.** Everything else together is about six engineer-months, and it
is the two thirds that decides whether this is a year or a quarter.

⚠ **B0 was the highest-leverage 0.4 EM in the document and it has landed.** It was the same bet
ADR-006 made and won, and it paid the same way. 2 002 of the 2 208 runnable flex fixtures passed
against a store built entirely on Yoga's corpus, which is what makes the harness trustworthy enough
to judge grid; thirteen fixtures' worth of *harness* bugs surfaced where they could still be told
apart from algorithm bugs, which was the whole reason for running flex first; and the 206 remaining
failures are a real catalogue, led by CSS Flexbox §4.5's automatic minimum size not being applied to
flex items that are themselves containers — a gap `Generated/` structurally could not see.

⚠ **And the new corpus turned out to have a blind spot of its own.** It sets `direction` on all
22 776 of its nodes, so `Direction.Inherit` is never exercised: breaking direction inheritance leaves
every Taffy test green and fails 374 of Yoga's 534. Ten times the size is not a superset. Both suites
stay.

---

## Part 7 — The sequence

**Wave 0 — the survey's own consequences.** C0 (prefix fallback), C5 (the gate and its expiring
allow-list) and the README correction — A5 landed while this was written, and ✅ C5's gate half has
landed since, taking F10 with it. What is left of wave 0 is C0 and `Tools/Vixen.TailwindParity`.
Nothing depends on these and everything is cheaper after them. **0.2 EM.**

**Wave 1 — the token model, before #6 and #7 build the old one.** C3, then C4, then A9 and A10. This
is the one ordering constraint that is urgent rather than logical: task #6 is queued and would
otherwise produce three `.vcss` files that a v4 `@theme` then has to re-do. **1.3 EM.**

**Wave 2 — the cheap properties, in parallel.** A1, A2, A3, A4, A6, A13, A14, A19, C1, C2, C6. Eleven
independent items, no shared file except `DrawListBuilder` between A1–A3. **1.9 EM, parallel.**

**Wave 3 — the two cascade features.** ✅ A15 is done, and it cost almost nothing because the cascade
already nested; what it bought instead was the discovery that the variant table had four end-to-end
tests in it (§ D6). Then A16 (`@container`), A17 (`has-*`), A12 (pseudo-elements). Sequential within
the cascade; parallel with wave 4. **1.65 EM.**

**Wave 4 — the oracle, then block, then grid.** ✅ B0 first and alone, and it is done: 0.4 EM bought
**3 116** Chrome-derived fixtures for the two modes that had none — 884 block, 84 float, 28
blockflex, 2 040 grid, 24 gridflex and 56 blockgrid — and it worked.

✅ **B1 is done too, and it answered the question it was sequenced to answer.** The store carries a
second algorithm for **three fields on `LayoutResult`, three on `CachedMeasurement`, and one
dispatch** — margin collapsing is the only thing block layout needs out of a child that flexbox does
not, and it needs it as an *output* beside the size. The arena, the dirty flags, the measure cache's
shape, the rounding pass and the absolute walk were untouched, and the collapsibility *input* needed
no plumbing at all because it is derivable from the two styles. That is the number grid should budget
against. 746 of 912 fixtures, and every failure in the shared absolute path rather than in block.

Then B2. B3 and B3a in parallel with B2 if there
is a second pair of hands: they share nothing but the store, and B3 is the one whose *first* task is
building its own oracle. **8.0 EM.**

**Wave 5 — the rest.** A7, A8, A11, B4, C7, C8. **3.1 EM.**

Grid and inline formatting are the long poles and they can run concurrently with everything in waves
2, 3 and 5. Sequenced for one engineer this is about eighteen months; with three tracks running it is
closer to eight.

---

## Part 8 — What is out of scope, and the argument

The bar is high, because "a basic, almost unusable subset" has been explicitly rejected. Four
exclusions, and only the first is unconditional.

**1 · Print and paged media** — `break-before`, `break-inside`, `break-after`, `columns-*`, and the
`print:` variant. **Four roots and one variant.** There is no paged medium: a game overlay and a tool
window are not printed, and `columns` is a multi-column *fragmentation* algorithm whose only consumer
would be a paginated document nothing in this engine produces. ⛔ **Out, permanently.** `print:` is one
media feature and costs nothing, so it stays in A14 as a condition that will never match.
⚠ `caption-side` is **not** in this exclusion — it is a table property and belongs with B4.

**2 · The `-webkit-` and `-moz-` prefixed *declarations*** — 8 property names across 14 roots. These
are vendor compatibility shims for browsers, and Vixen has no browsers. ⛔ **The declarations are out**
and almost none of the roots are: 13 of the 14 also set an unprefixed property that is the real one
(`select-*`, `hyphens-*`, `line-clamp-*`, the ten `backdrop-*`), so the root is in and the shim is
dropped. **Exactly one root sets nothing else** — `antialiased`/`subpixel-antialiased` — and it is
still in, mapped onto the glyph rasteriser's own switch rather than onto the CSS name, because
choosing between grayscale and subpixel AA is a real thing an editor theme wants to say.

**3 · Scroll-container CSS** — **32 roots**: the 22 `scroll-m-*`/`scroll-p-*`, plus `snap-*`,
`overscroll-*`, `scroll-behavior` and `scrollbar-*`. 🟡 **Deferred, not excluded, and re-homed.**
Scrolling in this engine is `ScrollView`, a control, not a property on a box; `scroll-margin` means
something only to a scroll container that honours it. A18 implements the *behaviour* against
`ScrollView` and the utilities become properties it reads. Writing the families first would add 32
inert roots — a tenth of the whole index — which is precisely the pattern this document exists to
stop.

**4 · `position: fixed` and `sticky`** — doc 09 excludes `fixed` on the grounds that there is no
viewport in a game overlay. That argument holds for `fixed` and **does not hold for `sticky`**: a
sticky table header inside a scroll container is a real editor requirement, has nothing to do with a
viewport, and `DataGrid` currently hand-rolls it. `sticky` is 🟡 **in**, sized inside B1.

**Everything else in the 223 absent roots is in.** Including the ones that look frivolous: `zoom-*`,
`field-sizing-*` and `scheme-*` are one property each and cost less than arguing about them, and an
inventory with an unexplained hole in it is how a subset gets rationalised the next time.

---

## Exit criteria (measured)

1. **Every one of the 328 roots is `works`, or carries an open task number, or is one of the four
   exclusions in Part 8.** Checked by regenerating the TSV; the states are computed, not asserted.
2. ✅ **No family emits a property no consumer *acts on***, except entries on the allow-list, each of
   which names a task this document contains. `UtilityConsumptionGateTests` fails otherwise — a test
   rather than `CheckArchitecture`, for the reason Part 5 gives, and "acts on" rather than "interns"
   for the reason Part 0 measured at seven properties. Today: **11 properties, 11 allow-listed**, and
   the allow-list expires on its condition.
3. **`UtilityFamilySupportTests` has a row per root, resolved against a real element**, and its
   `Inert` table is empty or every entry names its task. It is the only artefact in this survey built
   by resolving elements rather than by reading source, and it is where a finding goes to become a
   fact: F1 and F5 were derived from source and are now three `Fact`s in that file.
4. **The layout conformance suite is green against a second oracle.** Yoga's 534 stay. Taffy's
   translated corpus is added: **868 block, 1 960 grid, 2 268 flex**, every expected number out of
   Chrome, run behind the Ahem measure stub so no text engine is involved. Failures are listed by name
   with a reason, as `Vixen.YogaTestGen` already does for its nine `display: contents` skips.
5. **The sections the oracle does not reach have hand-written tests naming that fact.** `Vixen.Ui.Layout`'s
   README already carries the worked example — deleting CSS Flexbox §4.5 leaves all 534 green. Grid and
   inline each need their equivalent of `AutomaticMinimumSizeTests`, and the WPT `check-layout`
   subset is the cheapest way to find where to look.
6. **A utility class written in any editor assembly resolves.** One test that puts a class in a
   project other than `Vixen.Editor.Ui` and asserts the computed value.
7. **`text-lg` means what a Tailwind user expects it to mean**, and so does `rounded-sm` — the v4
   scale, checked against the published defaults.

---

## What this does not become

**A browser.** No `float`, no paged media, no `content-visibility`, no shadow DOM. The specification
being matched is Tailwind's utility index, which is a much smaller and better-defined thing than CSS.

**A second styling language.** Every gap here closes by making the *existing* property bridge wider.
There is no case in the 328 rows for a Vixen-specific styling concept, and adding one would be the
third version of the mistake in the README.

**A promise that a Tailwind stylesheet drops in.** Class names and semantics match; the generator is
Vixen's, the theme is a `.vcss`, and the cascade is Vixen's. Parity is of the *vocabulary*, so that
what a person knows transfers, and what they write behaves.

---

## See also

- [09 — UI Framework](09-ui-framework.md) § Styling, § Layout, § The utility preprocessor
- [`Core/Vixen.Ui.Styling.Utilities/README.md`](../../Core/Vixen.Ui.Styling.Utilities/README.md)
- [`Core/Vixen.Ui.Layout/README.md`](../../Core/Vixen.Ui.Layout/README.md) — what the ported Yoga
  suite does *not* cover, which is the worked example of an oracle's blind spot
- [01 — Technology Decisions](01-technology-decisions.md) ADR-006 (Yoga as an algorithm reference),
  ADR-015 (the dependency licence audit)
- [`43-web-styling-parity.tsv`](43-web-styling-parity.tsv) — the inventory
