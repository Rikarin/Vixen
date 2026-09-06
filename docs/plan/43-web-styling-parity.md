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

⚠ **Both halves of that sentence have since stopped being true, in opposite directions.**
`UiDocument.ResolveText` reads all four text properties for real — `word-spacing` and `text-indent`
included — so they are consumers now. The *ids* went the other way: `WordSpacingId`,
`TextIndentId`, `LetterSpacingId`, `TryTextLength` and `TryLineHeight` were five public members with
no caller anywhere, two of them a second copy of rules `ResolveText` executes, and they were removed
(#457). The survey's point stands; the example is now history.

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
| Utility **roots** (the unit of this table) | **331** | 311 families |
| CSS properties the utilities can set | **258** (8 of them vendor-prefixed) | **106** (11 of them `--tw-*` fragments) |
| …of which something in the engine acts on | — | **89** |
| Variant keys | **88** | **54** |

⚠ **The family figure moves every week, which is why it is no longer typed here.** It has been quoted
as 43 (the helper calls in one region of `UtilityFamilies`' static constructor), then as 98 (the
registry that region builds, parsed properly), then as 128. Every one of those was right when it was
written. The number is a denominator, so it has to be right *now*, and the only way that holds is for
it to be read off the registry on the run that prints it.

⚠ **And for months it was not, in this paragraph, which is the one that says it must be.** The row
above said 128 while `ParityLedger.Measure` answered more than twice that — the argument was written,
agreed with, and then not applied to the sentence making it.
`ParityLedgerTests.The_rendered_summary_counts_are_read_off_the_registry` holds both cells of that row
against the registry and the ledger now, so a family that lands fails the run until the row moves. No
current number is named in this paragraph on purpose: a figure repeated in prose one line under a
checked table is a copy nothing checks, and it is exactly how 128 outlived the two counts it replaced.

### The five states, and why the four in the brief were not enough

| State | Meaning | Roots |
|---|--:|--:|
| **works** | Vixen emits it, and a consumer acts on every property it sets | **248** |
| **partial** | emitted and partly read — one property of several, one axis of two, or a keyword set narrower than Tailwind's | **24** |
| **inert** | resolves, computes a value, and nothing in the engine looks at it | **1** |
| **absent** | not emitted at all | **55** |
| **composed** | it sets a `--tw-*` that another utility assembles; judged through its assembler | **3** |

⚠ **There was a sixth, `unknown`, and it described a row rather than a state.** Exactly one row held
it: an aggregate the original script left behind, eight static classes from unrelated Tailwind roots
under one descriptive name, of which two resolved and six did not. Recording that was better than
picking whichever of the five flattered — but the row's own note asked to be split or dropped, and
splitting it is what makes every state true of something. `snap-mandatory` and `snap-proximity` are
`scroll-snap-type` and belong to the `snap` root; `space-x-px` and `space-y-px` belong to the
families that already emit them; the four `*-reverse` classes are two roots of their own, `absent`,
carrying the reason. ⚠ The state is gone from `ParityLedger.States` as well as from the file, because
a state nothing can produce is a state somebody will find a use for.

⚠ **The row came back, and every test in this suite stayed green** ([#710](https://github.com/Rikarin/Vixen/issues/710)).
A hand resolution of a merge conflict in the `.tsv` took the side that still had it, at `absent`; the
row is a legal row, `absent` is a legal state, and the counts add up because they are computed from
the rows as they now are. What was observable is that six of its eight classes were on two rows at
once, so that is the invariant — `ParityLedgerTests.Every_class_is_listed_by_exactly_one_row`. ⚠ Two
cheaper rules were considered and are refuted by this file: refusing a root whose `vixen_family` names
no registered family would fail the 58 refused roots, which have no family by construction, and
refusing a name containing parentheses or the word `only` would fail `sr-only`, `not-sr-only` and
every `… (keywords)` row. ⚠ And **regenerating the `.tsv` after a merge would not have caught it
either** — `VIXEN_REGENERATE=1` rewrites three computed columns of whatever rows it is handed, so it
can neither delete a row that returned nor restore the two classes the same resolution dropped off
`snap`. A `union` merge driver would have produced this state automatically rather than preventing it.
The invariant is the cure.

⚠ **`composed` fell from twelve to three, and eight of the nine moved for two different reasons.**
Five (`space-x/y-*`, `divide-*`, `divide-x/y-*`) were never composition at all: they are child-scoped
families that emit real declarations onto `> :not(:last-child)`, and they now measure `works`. Three
(`mask-radial-*`, `mask-radial-at-*`, `ring-offset-*`) are composition *in Tailwind* and Vixen
registered no family for them, which is `absent` — calling them `composed` read as "handled" for three
roots with nothing behind them. What is left is the three gradient-stop families, which are genuinely
fragments with a working assembler.

⚠ **`mask-radial-at-*` has since left that group and reads `works`**, which is the point of having
moved it out of `composed`: it was buildable all along and the flattering state was what hid it. A
`--tw-mask-radial-position` fragment defaulting to `center` — CSS's own default — feeds an
`at <position>` that `DrawListBuilder.MaskFrame` resolves, so an unmoved radial mask reaches the
shader as the record it always had. ⚠ **Its sibling `mask-radial-*` reads `works` now, and the two
halves of that row were blocked on entirely different things** — which is the whole reason it spent a
while at `partial`. The four ending *sizes* wanted a reader: `GradientReader` takes all four
keywords, `BackgroundGradient.Reach` is the closed form for each, and
`GradientPaintTests.A_circle_ending_is_round_on_a_box_that_is_not` is the pixel oracle — on an 80×40
box, because on a square one every ending draws the same circle. ⚠ **The blocker the row named was
wrong twice**, and the second time it was named "they land when `UiMask` carries a stated pair of
radii": `UiShape.Paint.zw` *is* a stated pair and every rasteriser already honoured an arbitrary one,
so nothing was ever waiting on a shader. ⚠ **The two ending *shapes* wanted nothing from gradients at
all** (#607): Tailwind spells `mask-circle` and `mask-ellipse` with the bare `mask` prefix, which is
already the `mask-repeat` family, and `Family.Alongside` belonged to a family rather than to a value
— so those two values could not carry the mask layer their siblings do while the four repeat values
must not. `Family.ValueAlongside` is exactly that difference and `--tw-mask-radial-shape` is the
second fragment, defaulting to CSS's own `ellipse` so that a class naming only the size still means
what it meant. ⚠ One prefix now emits two unrelated properties, which is Tailwind's spelling and not
a compromise: `Register` keeps the first family under a name and discards a second silently, so a
second registration was never available.

### The composition mechanism

**Landed.** `Core/Vixen.Ui.Styling.Utilities/UtilityComposition.cs`, with `from-*`, `via-*`, `to-*` and
`bg-linear-*` as the worked consumer. Three of the twelve `composed` roots are now emitted; the others
— `mask-radial-*`, `ring-offset-*` and the static set — are more surface on the same mechanism rather
than more mechanism. It is written up in [the guide](../guide/ui/utility-composition.md).

⚠ **Two of those twelve turned out not to be composition at all, and the survey had them in the wrong
column.** `space-x/y-*` and `divide-*` were counted `composed` because v4 sets a `--tw-*-reverse` on
them, but the fragment is only how v4 spells the `*-reverse` *variant*; the families themselves are a
rule over children — `& > :not(:last-child)` — which is a selector problem and not a value one. They
are emitted now, without any fragment, through `Family.Scope`; the reverse spellings stay absent —
⚠ **though for neither of the two reasons written here for months.** `StyleValueParser` folds a
`calc()` now, and the flag *is* read: `ReverseFlagTests` measures it and both halves come out
positive. What survives is the one-edge decision in `UtilityFamilies`, which is not about values at
all. See F9.

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

⚠ **Both divergences stand as of 2026-09-05, and both are now measured rather than asserted (#291).**
A paragraph saying a thing is a known quantity is the shape a stale note takes, so each half is a test:
`CompositionTests.A_fragment_set_on_a_box_is_visible_to_its_descendants` puts `from-accent` on a parent
and `bg-linear-to-r` on a child that carries no `from-*`, and the child paints the parent's colour —
with the unparented control beside it, because without that half the assertion would pass against an
engine that inherited nothing. `A_gradient_is_a_discrete_swap_rather_than_an_interpolation` pins the
second: a transitioned gradient does not fail, it **snaps at the halfway mark**, which is CSS's
discrete path for a value with no magnitude — and the colour either gradient is built from
interpolates perfectly well, so what is missing is a *type* saying a fragment holds one, not any
ability to blend. Both go red the day registration lands, which is what makes them this note's expiry
rather than a restatement of it. `InheritedProperties.IsCustomProperty` is the single line either
would change.

**What it gates, and the first thing it gated.** v4 uses the identical pattern for transforms (A7 /
#23), `box-shadow` and filters (A8 / #28). The five `--` placeholders the table counted — `--blur`,
`--rotate`, `--scale`, `--translate-x`, `--translate-y` — were this shape built without the second
half: a fragment nothing assembles.

**Three of the five are gone.** `--blur` joined them: `blur-*` sets a `--tw-blur` and assembles it
into a real `filter` declaration, which `DrawListBuilder` reads (A8 / #28, below).
`translate-x-*` and `translate-y-*` are composed now — a
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
- **`CheckShaders`** now compiles each project's own `.rvn` and compares every module they emit.
  It previously covered only the library shaders the editor loads while describing itself as covering
  these, so a `.rvn` edited and never recompiled could sit in a commit unremarked.

⚠ **This section said four files have to agree. It is eight, and the four it did not name are the
ones no grep for `UiShape` will find.** Written down because the estimate's one real miss was not the
maths and not the layout — it was the *census*:

| # | File | What it knows | Caught by |
|---|---|---|---|
| 1 | `Vixen.Ui.Rendering.UiShape` | the whole layout | — |
| 2 | `Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn` | the whole layout | `UiShapeLayoutTests`, `CheckShaders` |
| 3 | `UiBox.frag.spv` / `UiBox.reflect.json` | the whole layout | `UiShapeLayoutTests`, `CheckShaders` |
| 4 | `SoftwareUiRasterizer` | the whole layout | the UI suite's own pixel tests |
| 5 | **`UiRenderer`** | only the **size**, spelled `80` three times | `Vixen.Graphics.Golden.Tests` |
| 6 | **`Platform/Vixen.Graphics.Golden.Tests/Shaders/ui-box.frag`** | the whole layout, in GLSL | `UiShapeLayoutTests` (2026-09-05), and itself on a device |
| 7 | ~~`Samples/02-HelloUi/Shaders/ui-box.frag`~~ | — | **gone** |
| 8 | ~~`Tools/Vixen.Templates/.../Shaders/ui-box.frag`~~ | — | **gone** |

Five, six, seven and eight are invisible to a search for the type: number five spells the literal
`80` and mentions no type at all, and the three GLSL copies call the struct `Shape`. The host wrote
112-byte records into a buffer sized for 80-byte ones, and each shader indexed at the old stride, so
every box after the first read the previous record's tail — a frame of plausible rounded rectangles
with the wrong radii, which is the failure `UiShape`'s remark predicts almost word for word.

⚠ **What actually caught it was `Vixen.Graphics.Golden.Tests` on a real device, not either new test.**
`UiShapeLayoutTests` pins the record's *shape* against the shader's reflection and has nothing to say
about how a host sizes a buffer around it; `CheckShaders` compiles Raven and the GLSL copies are not
Raven. Nothing compiles those `.frag` files, and nothing should be made to — `TestShaders.cs`
records the decision not to require `glslc` on every CI leg. `UiRenderer` now derives its stride from
`Marshal.SizeOf<UiShape>()`, which removes number five from the list permanently.

⚠ **Three of the eight are gone and the last GLSL copy is measured now (2026-09-05, #286).** Seven
and eight were deleted — the sample and the `vixen-app` template draw through
`Vixen.Ui.Desktop.UiShaderLibrary` and carry no shaders at all — and number six is kept on purpose,
because the reference images beside it were rendered with it. What closes the gap for it is not a
compiler: `UiShapeLayoutTests.The_one_hand_written_GLSL_copy_declares_the_same_record` **parses** its
`struct Shape` and holds the lanes to `UiShape`'s, in order and by type, so a lane added on one side
is a red test on a laptop rather than a wrong picture caught on a device. ⚠ Compiling it was never
the property to test for anyway — desktop GLSL forgives what `#version 300 es` rejects, and a file
that compiles can still disagree with the record it indexes, which is precisely what happened.

⚠ **What is still uncovered — and "no text comparison can reach it" was too strong.** The only
*sufficient* check that the GLSL and the Raven agree is a golden image rendered through each, which
regenerates every reference image in the suite and belongs on its own; that stays owed. But one
**necessary** condition of the comparison is text, and is checked as of 2026-09-05:
`SharedUiShaderTests.EveryConstantInTheGlslCopyIsOneTheRavenHoldsToo` requires every number in
`ui-box.frag` to be a number `Ui.rvn` holds too — Ottosson's eighteen Oklab coefficients, sRGB's
five, and every threshold the shape and shadow paths branch on. Constants are the part of a
specification that survives translation between two languages unchanged, which is what makes them
checkable on a laptop; an expression rearranged around the same numbers still passes, which is why
this narrows the gap rather than closing it. ⚠ It compares them as `float` and not as text, and a
sabotage proved why that is right rather than lax: `0.5363325363` → `0.5363325364` stays green,
because `glslc` rounds the literal to the same single-precision value and the compiled module cannot
contain the difference. `EveryRavenCopyAgreesAboutTheShadersItShares` covers the other half: every
`Ui.rvn` in the tree, per shader rather than per file.

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

#### What the mask work settled, and why `bg-clip-text` still has to wait

⚠ **#28 landed, `mask-image` landed with it, and `bg-clip-text` is still blocked — by option 3's
half that neither of them built.** The offscreen path exists now: a group gets a viewport-sized
surface, it ends in `ShaderRead` holding premultiplied colour, and `ui-mask.frag` composites it
through a per-pixel coverage. It is tempting to read that as "the mask mechanism is here, so clip the
background to the text layer", and that reading is wrong.

**A layer surface holds rendered colour, and in the Tailwind idiom the glyphs have none.**
`bg-clip-text` is written `bg-linear-to-r from-x to-y bg-clip-text text-transparent` — the text is
*deliberately* transparent, so a group containing it composites to nothing at all. Using it as a
mask multiplies the background by zero everywhere and draws an empty box. The surface says what the
subtree *painted*; `bg-clip-text` needs what the subtree *covered*, and those are the same number
only for opaque ink.

So what is still missing is precisely the thing § 3 named and did not get: **a text-coverage target**
— the glyph run rendered as coverage, independent of the run's colour. That is a distinct capability
from `UiMask`, which is an analytic ramp over a box and has no way to express a glyph. Concretely it
needs a pass that binds `ui-text.frag` with the colour forced to white, a surface to put it in, and a
`UiLayer` that names a *coverage* source separately from its colour source.

One further thing is absent and would be needed for the general form, and it is recorded against its
own row: an ordered filter list on `UiLayer` (today it carries a `Blur`, a `Filter` and a `Mask` as
discrete fields, which is enough because their order is fixed by the specification).

⚠ **This paragraph named a second absence, `mask-composite`, and that half is refuted — it landed
with the mask list and the generated row has read `works` since.** `mask-add`, `mask-subtract`,
`mask-intersect` and `mask-exclude` are registered families writing the property,
`DrawListBuilder.MasksFor` reads it as a *list* — `mask-composite: add, intersect` is one operator
per layer — and `UiMask.Coverage` folds that list bottom-up through `UiMask.Compose`, which is the
Porter-Duff step both executors run at the composite draw. So two mask sources **can** be
intersected, and `bg-clip-text` is blocked on the text-coverage surface alone.
The lesson is the one this document keeps re-learning one section up: a count or a state repeated in
prose beside a generated table is a copy nothing checks, and it outlives the thing it described.

**Recommendation stands, with the blocker now named rather than implied: `bg-clip-text` is absent
until a text-coverage surface exists.** It is not a `mask-*` root, `bg-clip` is not a registered
family, and registering one would emit a `background-clip` the engine cannot read — which is the
shape the consumption gate exists to refuse. See the `background-clip` entry in the `shadowed_by`
refusal block, which already says so for the same reason.

### By category

| Category | roots | works | partial | inert | absent | composed |
|---|--:|--:|--:|--:|--:|--:|
| Layout | 49 | 35 | 2 | 0 | 9 | 3 |
| Interactivity | 40 | 30 | 0 | 1 | 9 | 0 |
| Borders | 34 | 28 | 2 | 0 | 4 | 0 |
| Effects | 35 | 30 | 2 | 0 | 3 | 0 |
| Flexbox and Grid | 34 | 30 | 2 | 0 | 2 | 0 |
| Typography | 34 | 23 | 2 | 0 | 9 | 0 |
| Spacing | 24 | 24 | 0 | 0 | 0 | 0 |
| Transforms | 23 | 10 | 3 | 0 | 10 | 0 |
| Filters | 20 | 10 | 10 | 0 | 0 | 0 |
| Sizing | 15 | 13 | 0 | 0 | 2 | 0 |
| Backgrounds | 11 | 6 | 1 | 0 | 4 | 0 |
| Transitions and Animation | 6 | 5 | 0 | 0 | 1 | 0 |
| SVG | 3 | 3 | 0 | 0 | 0 | 0 |
| Tables | 2 | 0 | 0 | 0 | 2 | 0 |
| Accessibility | 1 | 1 | 0 | 0 | 0 | 0 |
| **Total** | **331** | **248** | **24** | **1** | **55** | **3** |

Flexbox and Grid leads at 30 of 34, with only two absent roots left and both of those refused on
policy rather than owed; then Layout at 35 of 49, Interactivity at 30 of 40, Borders at 28 of 34,
and Effects at 27 of 34. ⚠ Accessibility is 1 of 1 as of 2026-09-06 — `forced-color-adjust` landed the
day its last blocker (#836) closed, and this paragraph said it had "no working root at all" for as long
as the refusal outlived its reason. **Tables** is the one category still at zero.

⚠ **No category is `complete`, and SVG — which this section called the first one to be — is 2 of 3.**
`stroke-none` is `absent`, refused on the same policy `inset-shadow-*` is: `stroke` is read, but only
as a colour. A refusal is still an absence in this table, which is the point of counting it here
rather than in the prose; the paragraph below records what SVG *did* close, which is the keyword
half of `fill`.

⚠ **SVG closed on a keyword rather than a property, which is the shape this table is least able to
show.** `fill` and `stroke` have measured `read` since A6, off the colour half — so a registration of
`fill-none` or `stroke-none` would have scored green while `Icon.Resolve` asked `ColorOf`, got the
`null` that `none` and an unset property both produce, and painted the glyph in the inherited colour.
The class would have hidden nothing. `none` is a *paint* in SVG 2 § 13.2 and not a colour, so the
close was a fourth reading on `UiDocument` — `KeywordOf`, beside `ColorOf`, `LengthOf` and
`NumberOf` — and `Icon.IsNone` at both draw paths.

⚠ **Flexbox and Grid went from 20 to 27 and every one of the seven was a family, not a reader.**
Five were `absent` roots whose consumers had been finished for weeks — `col-*` and `row-*` against
the `grid-column`/`grid-row` shorthand `ShorthandExpansion` already splits at load, and the three
`place-*` shorthands against six longhands the bridge already read. Two were `partial` roots
(`justify-items`, `justify-self`) waiting on one reader: the `[ safe | unsafe ]? <position>` grammar,
which the layout has honoured at six sites since safe alignment landed and which nothing crossing
`LayoutStyleBuilder` could ask for, because a two-word value arrives as a token list and
`TryKeyword` wants one keyword. That last is the shape this table is worst at showing — the *engine*
could do it and the *vocabulary* could not, so every measured column read exactly as it should.

⚠ **Interactivity went from 20 to 26 with no `partial` left, and the six that moved split three
ways, which is the part worth reading.** Four were renames — `scroll-mbs/mbe/pbs/pbe-*` emit the
*physical* longhand for the reason `inset-bs-*` does, because there is no writing mode for the block
axis to be anything but top-to-bottom. One was a reader that existed and was spelled differently:
`TextField` and `CodeEditor` had drawn the caret off Vixen's own `--caret-color` since they were
written, and asking the standard `caret-color` first is the whole of `caret-*`. One was a keyword:
`cursor-help` needed a `UiCursor.Help` and nothing else.

⚠ **And the twelve still absent are not twelve of the same thing.** Six are refusals with a named
blocker in their own row — `accent-*` (the three controls CSS means are drawn from a stylesheet, and
`var()` cannot read a standard property), `will-change-*` (no element-keyed retained surface),
`touch` (⚠ *not* "touch events never reach `UiDocument`", which is what this line said and what the
row said with it: `PlatformInput` routes them and has since — what is missing is the UA behaviour
`touch-action` would suppress, a touch pan in `ScrollView` and a pinch anything consumes),
`resize`, `appearance` and `field-sizing`.
✅ `snap` and `snap (keywords)` were the two sized-and-not-started ones, and both read `works` now —
with a third row, `snap (align)`, that this file had no entry for at all. The remaining four are
the scrollbar cluster, which is one feature and is owned elsewhere.

⚠ ~~**The one finding here that is nobody's root and everybody's problem: `UiDocument.Cursor` has no
consumer in the tree.** `cursor` measures `works` because the probe reads `CursorOf` directly, and it
is genuinely read — but nothing calls `SetCursor` on a window from it, so no `cursor-*` class of any
value changes what the user sees. That is a gap in the *host*, family-wide and equally true of
`cursor-pointer`, and this file has no column that can say so.~~

✅ **Closed 2026-08-24.** `Vixen.Platform.Ui.PlatformCursor.Apply` maps `UiDocument.Cursor` onto
`IWindow.CursorShape` — to the window the *hovered* element's surface is in, not the main one — and
both frame loops call it after `Document.Update()`. `cursor: none` hides the pointer through
`CursorMode`, and only from `Normal`, so a game in mouse-look keeps the pointer it took. ⚠ **The
finding's own lesson survives the fix and is the transferable part: a consumption probe that asks the
framework is not a consumption probe.** Every test for this is written against `IWindow.CursorShape`,
because a test that asked `document.Cursor` would have passed on every day of the year this was
broken. Not gated on `PlatformCapabilities.Cursor`, deliberately — the flag is about hiding and
confining, `CursorShape` is on every window, and the only platform a test can open a window on is the
headless one, which advertises `MultiWindow` and nothing else.

⚠ **Effects went from 12 of 33 to 24 of 33 on one change, and every root that moved was waiting on
the same thing: a mask *list*.** `mask-t-from-*` and its eleven siblings are per-edge ramps that only
mean anything combined, so none of them could be registered while `UiLayer` carried one mask —
registering them would have emitted a `mask-composite` nothing read, which is what the consumption
gate exists to catch. The list also closed `mask-*` itself, whose four `mask-composite` keywords had
been the one `partial` in the category. What it cost is a storage buffer for the entries, because
`ui-mask.frag`'s push constants were already at the 128 bytes Vulkan guarantees — see
`docs/guide/ui/compositing.md`.

⚠ **Filters is 10 of 20, and the three changes that got it there were three different sizes.** The
seven colour functions moved first and cost the frame nothing: `brightness-*`, `contrast-*`,
`grayscale-*`, `hue-rotate-*`, `invert-*`, `saturate-*` and `sepia-*` are a single 3×4 colour matrix
composed on the CPU and applied in the fragment stage of the composite draw a group already makes —
no second surface, no extra pass, forty-eight bytes of push constant.

⚠ **`filter-*` was a registration gap and nothing else.** `filter: none` was already read correctly —
`DrawListBuilder.Filter` refuses anything that is not a list, and two existing tests used it as their
control — and all that was missing was a family to spell it. Registered as the keyword rather than as
the eight functions at their identities: those draw the same picture and are not the same
declaration.

⚠ **`drop-shadow-*` cost a compositor change and is the ninth function.** It is a Gaussian over the
group's *alpha*, offset, tinted and composited *under* it — a second viewport-sized surface, two more
render passes and a second quad, on both executors. What it did **not** cost is a shader: a
`UiColorMatrix` with zero coefficients and the colour in its offsets evaluates `0·c + colour·a`,
which is the tinted silhouette, so `ui-colour.frag` draws it unchanged. It is written **last** in
`UtilityComposition.Filter` because it does not commute with `blur()` — the only pair in the list
that does not — which also means every filter declaration in the engine grew a ninth function whose
identity is a transparent shadow. See `docs/guide/ui/compositing.md`.

⚠ **The ten `backdrop-*` twins have landed, and what they cost was a change to the compositor's
walk rather than a capability the backend lacked.** They read what is *behind* the group, and
`UiRenderer.Compose` records every group's pass before the host's frame pass begins, so at that moment
nothing below the group exists; by composite time the destination is the colour attachment being
written. The fix was **not** a read-back — the prefix is replayable, and `UiRenderer.Submit` already
walked exactly the range that had to be replayed. So it took a `stop` argument; `Compose`'s
reverse-pre-order walk became post-order, so that everything painted behind a group is finished before
its capture runs; a capture pass per backdrop group renders the prefix into a surface of its own; and
`Compose` grew a public parameter carrying what the host had already painted — without which the
feature blurs the interface and not the scene under it, and composites a translucent copy over the
sharp original instead of replacing it. `docs/guide/ui/compositing.md` § *`backdrop-filter`* has the
whole of it.

⚠ **They read `partial` rather than `works`, and the gap is a fidelity one rather than a consumption
one.** CSS clips the filtered backdrop to the element's border box *including its radius*, and a
`UiLayer` carries no radius — so `rounded-2xl backdrop-blur-md bg-white/30`, which is the canonical
use of the feature, shows square corners just outside the rounded ones. The border box itself *is*
honoured: `UiLayer.BackdropBounds` carries it separately from the group's ink, because the ink is
grown by any child that overflows the element and filtering the backdrop over that would put blurred
scene outside the panel that asked for it. Closing the radius needs a rounded-rect signed distance in
three shipped fragment modules and their software transcription. That is now the whole of the owed
half: the radius alone.

⚠ **The second divergence closed on 2026-09-05 under #229, and it closed by the premise being
wrong rather than by the work being done.** An element that painted nothing of its own opened no
group and so got no backdrop, and the reason recorded for it was structural: both executors were said
to walk the layer list by matching a draw index, so a zero-width range would match its own start and
never advance. **Neither of them does that.** `SoftwareUiRasterizer` advances its `next` cursor as it
*enters* a group, so a zero-width range leaves the draw index where it was and the next turn of the
loop executes the composite quad standing there; `UiRenderer.Forest` takes a group's descendants as
the entries whose `First` is *strictly* inside its range, which an empty range has none of, and hands
the index straight on. What was actually dropping the group was the other guard —
`UiGeometryBuilder.Layer` refusing a layer whose ink is empty, which is real, because a zero-sized
surface is a validation error rather than an empty picture. A backdrop is the one thing a group
carries that is not a function of its own ink, and the rectangle it wants is the border box the
backdrop was going to be clipped to in any case. So an inkless group with a backdrop is bounded by
that box and every other inkless group is still discarded, which `BackdropFilterTests` pins from both
sides. The lesson is one this document has had before: a refusal whose reason is a claim about code
is worth re-reading against the code.

⛔ **The third divergence is permanent and is stated here as one, 2026-09-05.** A group carrying a
`transform` is refused the backdrop outright rather than given an approximation of it, in
`UiGeometryBuilder.Layer`. ⚠ **Every other thing a group does survives a transform for free** — a
blur convolves the surface, a colour matrix is per pixel, a mask is read from the composite's own
untransformed coordinate, and a drop shadow displaces in local space and is carried along — because
each of them happens in the surface's own space and the matrix is spent afterwards on the composite
quad. CSS orders them the same way, filter then mask then transform, so all four come out right
without being told. The backdrop is the one surface holding something the group *did not draw*:
`UiRenderer.Capture` replays the draw-list prefix into a surface and both executors read it at the
coordinates the quad covers, so a rotated backdrop quad would have to sample a rotated window of a
captured picture — four texture coordinates that are no longer an axis-aligned rectangle, a capture
region that is no longer `BackdropBounds`, and a border-box clip that is no longer a rectangle
either. Sampling the untransformed patch instead shows the scene from where the element *was* rather
than from where it is, which under a rotation is not an approximation but a different picture. So it
is dropped once, and ⚠ above the empty-group guard rather than beside it since #229, because that
guard now asks whether a backdrop survived this one. Neither executor learns that a backdrop can be
missing for a new reason. This is a refusal with its reason, not an owed item; the radius above it is
the owed half of A8's remainder.

⚠ **Vixen emits only `backdrop-filter` where Tailwind emits `-webkit-backdrop-filter` beside it.**
That copy is for Safari and there is no Safari here; emitting it would put a declaration into every
generated sheet that nothing could ever read, which is the exact shape of debt `InertProperties.txt`
exists to record — and one nobody could ever close. The ledger's `css` column still lists both,
because that column is about what Tailwind emits.

⚠ **Sizing was `0 works, 7 partial, 8 absent`, then `0 works, 13 partial, 2 absent`, then
`12 works, 1 partial, 2 absent`; it is now `13 works, 0 partial, 2 absent`.** Four separate things
happened to it, in four revisions, and the headline number was the least informative thing on the row
for three of them.

**The seven partials were one rule, and the rule is closed.** The previous revision of this paragraph
said Sizing read worse than it was because the mechanism, not the roots, had moved: `w-*`, `h-*`,
`size-*`, `min-w-*`, `min-h-*`, `max-w-*` and `max-h-*` were read on every property they emit and
were demoted only for the six viewport-relative keywords Tailwind ships. That was exactly right, and
it was *one* rule rather than seven: Tailwind names `svw`/`lvw`/`dvw` and `svh`/`lvh`/`dvh` after the
viewport axis being measured and not after the property being set, so `h-dvw` is `height: 100vw` and
`w-svh` is `width: 100vh`. The mapping therefore belongs on the value, in `TrySize`, and eight lines
there moved all seven roots at once.

⚠ **All six spellings collapse to two answers, and that is a fact about this engine rather than a
shortcut.** CSS Values 4 separates the small, large and dynamic viewports only by what a retracting
browser toolbar does to them. A Vixen surface has no retractable chrome: `LengthContext` is built
from `UiSurface`'s width and height and there is no second, smaller rectangle for the small viewport
to be. So all three name one measurement, `vw`/`vh` is it, and `100dvw` would have put a unit into
every generated sheet that `StyleValueParser` does not read — the inert-class shape the family table
is not allowed to add. The units themselves needed nothing: `StyleValueParser` has parsed `vw`/`vh`
since it existed and `LengthContext.PixelsPer` resolves them.

⚠ **They stayed `partial` on a second gap that the closing of the first one uncovered, and that gap
is now closed too.** Every sizing row lists a content keyword — `w-min`, `block-fit`,
`max-inline-max` — and every one of them used to resolve as a class and move nothing.
`LayoutStyleBuilder.ToEdgeLength` mapped the `auto` keyword and no other, so `width: min-content`
came back `StyleLength.Undefined` and `SetLength` left the dimension alone;
`LayoutUnit.MaxContent`/`FitContent` were declared and read by no production code, and there was no
`LayoutUnit.MinContent` at all. **That is the `value_gap` column doing the job it exists for, and it
is worth being precise about why no gate could have found it**: `width` is read, the class resolves,
and both halves of the per-property measurement are green over a declaration that does nothing —
`visibility`'s dead `collapse` one file over.

⚠ **The estimate that stood here was wrong in its central claim, and the way it was wrong is the
useful part.** It said the layout was already there and only a `LayoutUnit`-to-`SizingMode` mapping
was missing — "one new helper beside `HasDefiniteLength` and about eight call sites". The sizing modes
are real, but they describe *a request made of a node*, not *a size written on one*, and the gap
between those two is where the work actually was. A hook on the node's own layout was built first and
it fails on the commonest case there is: `WalkBlockChildren` asks `HasDefiniteLength(child, …)` and
settles the child's width **before** handing it down, so a substitution performed on the way into the
child arrives after the only reader that mattered. `ConstrainMinSizeForMode` and the grid track sizer
read a child's bounds equally early. There is no single seam downstream of every such read.

**What landed instead is a whole-tree pre-pass**, `Core/Vixen.Ui.Layout/LayoutTree.Intrinsic.cs`. It
runs at the top of `CalculateLayout`, walks the tree **bottom-up** — a node's content size is a fact
about its children, so a child still holding a keyword would be measured as though it had no size —
measures whatever asked to be measured, and writes each answer into the node's own style as an
ordinary `LayoutUnit.Point`. Flex, block, grid, inline, `BoundAxis` and the absolute path then read a
number and know nothing about any of it; the written values are put back from a `finally` at the end
of the pass, so `GetStyle` still answers with the declaration. Neutralising a node's own keywords
before its probes run is both the recursion guard and the specification — a box's min-content size is
measured with its own intrinsic bounds out of the way, or the definition eats itself. All six slots
are covered, which mattered: eight of the thirteen roots are a `min-*` or a `max-*`.

⚠ **On the block axis the three keywords are one number, and that is CSS Sizing § 5.1 rather than a
shortcut.** There is no narrowest height — only the height the content takes at the inline size it
has — so `h-min`, `h-max` and `h-fit` agree and the store measures once. Asking for a height with
nothing to spare would in any case be answered by `MeasureNodeWithFixedSize`, which returns zero for a
fixed request of zero.

⚠ **`LayoutUnit.Stretch` is carried and deliberately not resolved.** Nothing in
`Vixen.Ui.Styling.Utilities` emits it, and the two generated fixtures that set it disagree about what
it should mean: `Stretch_width` wants the containing block's width, `Stretch_flex_basis_column` wants
its own content's height. Both pass today because the keyword behaves as `undefined` and each falls
through to a different default. Resolving it would close one by opening the other, for no class.

⚠ **The corpora could not see any of this and still cannot.** Every content keyword in the Taffy
fixtures is either on `<viewport>`, where it means an indefinite available size that never reaches
`StyleLength`, or inside a `grid-template-*` track list, which the track sizer reads through
`GridTrackSize`. The layout suite is byte-identical across the change — 6234 passed, 0 skipped, 0
failed, before and after — and the cases that judge the feature are hand-written in
`ContentSizingTests` and `LayoutStyleBridgeTests`, on **resolved boxes**. A test that asserted the
keyword had reached `LayoutStyle` would have passed the whole time.

⚠ **`w-screen` and `h-screen` were both `100%` and are now `100vw` and `100vh`.** A percentage
resolves against the containing block and a viewport unit against the surface, so the two disagree
inside any ancestor that is not full size — which is exactly when an author reaches for the class.
`screen` is the one sizing value whose answer depends on the **property** rather than on the value, so
unlike the six viewport keywords it cannot live in `TrySize`, which never sees the property. It is
registered per family by `SizeToScreen`, through the keyword table that `Resolve` consults first.
`size-*` and the inset roots keep the percentage: they measure both axes at once, and Tailwind ships
no `screen` class for either.

**The six that moved from `absent` are the writing-mode-relative names, and all six came out
physical** — `inline-*`, `min-inline-*`, `max-inline-*` on the width trio and `block-*`,
`min-block-*`, `max-block-*` on the height trio. The block three are the `inset-bs-*` and
`scroll-mbs-*` argument unchanged: no writing mode, so the block axis is top-to-bottom in every
configuration and `block-size` would mean `height` on every element that ever resolved it.

⚠ **The inline three are physical for a stronger reason than the block three, not a weaker one, and
this is the part that the neighbouring precedent gets wrong if read too quickly.** `inset-s-*` and
`rounded-ss-*` keep v4's logical spelling because `direction: rtl` really does mirror them — an edge
and a corner are named by *which end* of the inline axis they sit at, and which end that is depends
on the direction. A size is not named that way. `inline-size` is the extent *along* the inline axis,
and mirroring an axis does not change how long it is; only a writing mode chooses which axis is
inline, and there is none. So `inline-size` is `width` under `ltr` and under `rtl` alike, where the
block mapping is merely safe in every configuration this engine currently has. **And the deciding
fact is in the code rather than in that reasoning**: `inline-size` and `block-size` occur in this tree
in exactly one place — `ContainerQuery`, as `container-type` values and query feature names — where
they are already mapped to width and height with no direction consulted. Nothing interns either as a
property, so emitting the logical longhand would have resolved, computed and moved nothing.

⚠ **`block` and `inline` are one family each and not two, which was the whole of the "family name
collides" note those rows used to carry.** Tailwind spells `display: block` and `block-size` with the
same prefix, and `UtilityFamilies.Register` keeps the first family registered under a name — so a
`Size("block", "height")` in the sizing section would have been discarded in silence and every
`block-*` class would have gone on being reported as an unrecognised typo. `StaticOrSize` is the
registration that holds both: the empty keyword answers the bare class and the value kind answers the
rest, which is the shape `flex` has had all along for the same reason.

**`max-block-*` was the one row in the category still `partial`, and it was `partial` on something
of its own.** `max-block-lh` is one line box, and `lh` was a unit `StyleValueParser` did not read and
`LengthContext` could not have resolved if it did — the context carried a font size and no line
height. ⚠ **Nothing in its `value_gap` said so**: the demotion came from the *measurement*, because
the `classes` column lists a class that does not resolve. That is the arrangement worth keeping in
mind when a row's prose and its state disagree — the state is the half that was measured.

✅ **Closed 2026-09-05, and it was three stages rather than a unit.** `StyleUnit.LineHeight` is
parsed, `LengthContext` carries the element's own line height, and `UiDocument.Apply` hands it in
beside the font size — a parser that read `lh` and stopped would have left every box the wrong size
with the class resolving perfectly, which is this row's own failure shape one level down.
⚠ **`line-height: normal` is the one approximation.** CSS settles it from the font's ascender,
descender and line gap — `TextRun.Height` does exactly that — and nothing that resolves a length has a
font, so `LengthContext` answers 1.2 times the font size and says so. It is never zero: a length unit
whose scale is zero would make `max-h-lh` a box of no height, which is `PixelsPer`'s own documented
trap. ⚠ **And `lh` had to be named in `NotNegatable` in the same change**, because `1lh` begins with a
digit exactly as `100%` and `100vh` do, so `-max-block-lh` would otherwise have emitted minus one line
box on the strength of that character — the third time that trap has been laid by a value becoming
resolvable.

⚠ **`basis-min`, `basis-max` and `basis-fit` are refused rather than swept in, and the `basis-*` row
now says why.** `TrySize` answers all three, but a flex basis is a size on the *container's* main
axis, which is not a fact about the declaration and which `LayoutTree.Intrinsic.cs` — resolving per
dimension — has nothing to hand back for. Mapping the keyword in the bridge would have put a value in
the slot that resolves to NaN, which is the dead-declaration shape this whole section is about. None
of the three is a documented Tailwind class in the first place.

⚠ **The table above is generated, and this paragraph is the third revision of a warning that it is
not.** It used to say the counts were a hand survey with a date on them, then that the C5 gate had
superseded them for the inert set only. Both were true and both rotted. The whole of the `state`,
`vixen_emits` and `engine_reads` columns is now computed on every test run by
`Core/Vixen.Ui.Styling.Utilities.Tests/ParityLedgerTests`, which drives the same consumption probe
`UtilityConsumptionGateTests` uses and fails when the file disagrees with it. **The numbers in this
section are asserted against that table by the same suite**, so prose and data cannot drift apart
either.

The live count is **5 properties emitted with no consumer** — `rotate`, `scale`, `user-select`,
`border-inline-start-color` and `border-inline-end-color` — every one of them on the expiring
allow-list in `InertProperties.txt` with the task that closes it. ⚠ It was six; `--blur` left the
list by being *replaced* rather than by gaining a reader, which is the same exit `--scale` and
`--rotate` are still waiting for and the reason the count can fall without a consumer being written.

### The columns

`category · root · kind · example · css · vixen_family · vixen_emits · engine_reads · inherit_only ·
state · shadowed_by · value_gap · note · classes`

⚠ **Three of the fourteen are computed and the other eleven are not, and knowing which is which is
how to read the file.** `vixen_emits`, `engine_reads` and `state` are measured on every test run and a
hand edit to them fails the build. Everything else is declared: what Tailwind is, which family answers
a root, and whether what is emitted is faithful.

**`shadowed_by`** names the Vixen family that swallows a Tailwind class whose own family does not
exist — `rounded-tl-lg` used to reach the family `rounded` with the value `tl-lg`, which no token
table answered, so the utility was dropped with no diagnostic. That was `absent` with a trap in it
rather than plain absence. The eight per-corner families have since landed and the column is empty for
them; it still holds for the rest. **`value_gap`** is the column for a root that emits and is read and
*still* does not do what it says.

⚠ **This paragraph used to give `mx-*` as its example — "`margin-left` + `margin-right`, not
`margin-inline`, identical in LTR and wrong in RTL" — and that example was false (2026-09-05, #282).**
An axis utility sets **both** ends of the axis to one value, and `direction` decides only which end
is called the start; `margin-inline: 8px` and `margin-left: 8px; margin-right: 8px` are the same
computed margins under `rtl` as under `ltr`. What could distinguish the two spellings is a *vertical
writing mode*, where the inline axis is the vertical one — and `Vixen.Ui.Layout` has none, which is
`inset-bs-*`'s argument one axis over. Eight rows carried that claim (`inset-x/y-*`, `mx-*`, `my-*`,
`px-*`, `py-*`, `border-x/y-*`) and all eight measure `works` now, with the reasoning moved to `note`
where a substitution belongs. ⚠ The refutation is a test rather than a paragraph:
`UtilityFamilyTests.An_axis_utility_sets_both_ends_so_no_direction_can_tell_it_from_the_logical_spelling`
asserts the pair differs in property and not in value, and pins the contrast — `ms-*` and `inset-s-*`
name a single *end*, keep v4's logical spelling and are mirrored by `LayoutStyleBuilder.EdgeNames`,
and must never be flattened the same way.

⚠ **The four rows that really were missing are registered in the same change**, which is what was
left of #282 once the eight above were re-measured: `mbs-*`, `mbe-*`, `pbs-*` and `pbe-*` emit
`margin-top`, `margin-bottom`, `padding-top` and `padding-bottom` — the fourth outing of the
`scroll-mbs-*` argument, and `SplitName`'s longest-prefix rule is what keeps `mbs-2` from eating
`mb-2`. **So the answer to "does `Vixen.Ui.Layout` gain a writing mode?" is no, and nothing in the
fourteen roots needed one.** What still does is a shorter list than this issue's, and it splits in
two: `float-start`/`float-end` and `clear-start`/`clear-end` want `FloatSide` and `Clear` to become
*direction*-relative — CSS 2.1 §9.5's keywords do not flip, and the whole `float_bfc_*` corpus ships
RTL variants with identical expectations, so this is a `direction` question and not a writing-mode
one — while grid's `grid_relayout_vertical_text` is the only fixture in the tree that asks for a real
`writing-mode` field.

This paragraph has now cited two Sizing gaps that were closed after it named them: first the six
viewport keywords `w-*` lacked, and then the content keywords every sizing root listed and none of
them honoured — `w-min` resolved to `width: min-content` and the bridge dropped it on the floor. Both
were invisible to every gate in the tree, both were written down here, and both were fixed from this
column. That is the column earning its keep rather than an argument against it.

⚠ **`value_gap` is hand-kept and it feeds the generated `state`, which is the one seam in the
mechanism worth naming.** A root whose every property is read is `works` unless something says
otherwise, and two things can: a listed class that does not resolve, which is measured, or a
`value_gap`, which is a judgement. So `state` is not purely computed — it is computed from a measured
read-ness and a declared fidelity. The alternative was to drop fidelity from the state entirely and
call `w-*` "works", which is how the file came to overstate the sizing category in the first place.

⚠ **What still needs `tailwindcss` installed, and therefore is not generated.** The Tailwind side of
the cross product — which roots exist, which classes each covers — is transcribed from
`__unstable__loadDesignSystem()` and is a measurement with a date on it: **`tailwindcss@4.3.3`,
2026-08-07**. `Tools/Vixen.TailwindParity`, reading a committed snapshot of the v4 registry, would
close that half too and is the remainder of exit criterion 1. The engine side no longer waits on it.

⚠ **And the join between the two vocabularies is declared, because they collide.** `vixen_family` is
the column a person maintains, and four names still mean different things on either side of it:
Tailwind's `bg`, `border`, `text` and `transition` static roots are `background-size`,
`border-collapse`, `text-wrap` and `transition-behavior`, none of which the like-named Vixen families
emit. Matching them by name would mark four roots supported that are not. The guard against the
column simply being forgotten is `Every_registered_family_is_claimed_by_a_row`: a family that lands
and is written into no row fails the run, which is exactly the drift that produced this revision.

⚠ **It was six names, and the two that left the list are the interesting ones — they were resolved
rather than dropped.** Tailwind's `block-*`/`inline-*` are `block-size`/`inline-size` and Vixen's
`block`/`inline` were `display` and nothing else, so joining them by name would have read `works`
over a root nothing supported. The families now answer *both* roots — the bare class is the display
value and the valued class is the size — so the join is legitimate in both directions and the
`display` row and the two sizing rows each claim them. **The lesson is that a name collision in this
column is a question and not a verdict**: it means somebody has to look, and looking twice found one
case where the honest answer was to make the collision true.

### The `note` column carries the expiry, and until now nothing could read it

**The most-repeated finding in this document, and the only one that had no mechanism behind it.** A
refusal is a verdict plus a condition. This file records the verdict in bold and the condition in a
subordinate clause, and six times in one month a refusal that was correct when written was false when
read — Transforms, whose closing sentence named a compositor that had landed a week earlier;
`origin-*`, refused as unobservable "until `scale` and `rotate` land"; `scale-x/y-*`, which cited
`scale`'s refusal and inherited its expiry; `mix-blend-mode`, half of whose premise the compositor
retired; `on:click`, whose note described a mapping `ControlMarkup` had replaced three weeks before;
and `StyleGen`, whose entire stated reason had evaporated.

⚠ **Four of the fourteen columns had no accessor in `ParityLedger`, and `note` was the one that
mattered** — a reason no test can see is a reason that expires unobserved.

**The mechanism, which is a clause in the note rather than a fifteenth column.** A refusal may end its
note with one of three bracketed clauses, and `Core/Vixen.Ui.Styling.Utilities.Tests/RefusalExpiryTests`
holds it to them:

- `[expires-with <root>]` — this refusal stands only while that ledger root is itself refused
  (`absent` or `inert`). The cited root's `state` is a **computed** column, so this needs no foresight
  from whoever writes it and cannot be spelt around: whatever closes the cited root changes its state,
  and that run is the run this fails on. Issue **#288**'s literal subject, and for that shape it is
  exact.
- `[expires-on <Namespace.Type>.<Member>]` — this refusal stands only while that member does not
  exist. ⚠ **A tripwire, not a proof**: whoever eventually builds the thing picks the name, and a
  different name leaves the clause green for ever. It is here because some refusals have no root to
  hang on — `mix-blend`'s surviving half was a fact about a struct — and where both are available the
  root is the one to name. ⚠ That first anchor has since fired exactly as designed: `UiLayer.Blend`
  landed, the clause resolved, and the run went red on a note nobody had thought about for a month.
- `[expires-when-read <css-property>]` — this refusal stands only while **nothing in the engine reads
  that property**. Exact, like the first, and for the same reason: its condition is the *same
  measurement* `InertProperties.txt` expires on, taken by `UtilityConsumptionProbe` from a real frame.
  ⚠ **This is the second file #288 names, and it is the half the mechanism was missing.** A note
  saying *"the width is read; the logical colour is not — InertProperties.txt #21"* is a refusal
  resting on an allow-list line, and nothing carried that line's verdict one dependency edge out — so
  the run that deleted the exemption would leave the ledger's sentence standing, which is exactly how
  `will-change-*` came to cite an `InertProperties.txt` block that closed with A7. ⚠ A row carrying
  this clause may be `partial`, unlike the two above: a half-read longhand *is* what a `partial` row
  usually is, and there are 29 of them.

**Why a clause in the prose and not another column.** The failure being prevented is that nobody
writes the condition down *as* a condition. Anything that makes recording one a separate act loses to
what it competes with, which is finishing the sentence you were already writing.

⚠ **Detecting an *undeclared* citation was tried first and abandoned, with a measurement.** Requiring
a note that mentions another root to declare it finds 106 mentions across 42 of the 47 refusal-shaped
notes — and almost all are English, because the roots include `list-*`, `from-*`, `text`, `block-*`,
`mask`, `scroll`, `transform`, `border`, `display` and `flex`. "The draw list", "read back from the
target" and "the border box" all match. The vocabulary collides with English by construction, which
the `vixen_family` paragraph above already says about four other names, so the declaration has to be
written and what a machine can do is check it.

**What it prints on the day it does not run.** `RefusalExpiry.txt` is the committed census of every
clause, derived and regenerated with `VIXEN_REGENERATE=1`, and it is held against the ledger as an
**exact set in both directions** — not a floor. A sweep that derives a set and checks its members
reports nothing when the set is empty, so a ledger whose notes lost their clauses would go green; a
floor does not help either, because "at least one" and "more than one distinct reason" are the two
guards this suite's neighbours have already had eaten by success. A malformed clause fails rather than
being skipped, for the same reason one level down: the opening bracket is counted with a pattern that
cannot be fooled by the contents, so `[expires-witth …]` is a red test and not a silent exemption.

⚠ **And the anti-typo half, which is how a check like this normally dies.** "Assert this symbol is
absent" is satisfied by a misspelt symbol, for ever. So an `expires-on` anchor is checked in two
halves: the *type* must resolve and the *member* must not exist. This repository has shipped the
misspelt-key bug three times this year in a navmesh, a security policy and a shader key.

**Seeded with nine clauses over eight refusals**, two of which — `mix-blend` and `isolation`, the two
this finding leads with — had an **entirely empty `note` column** until now, which is why neither was
ever re-read. ⚠ **Three more followed on 2026-09-05 with the `expires-when-read` kind**, and they are
the citations that were already written in English: `border-s-*` and `border-e-*` name the two logical
colours `InertProperties.txt` exempts under #21, and `select` names `user-select` under #24. A fourth
went on `ring-offset-*` the same day, when the refusal it carried lost one of its three blockers and
kept the other two; ⚠ **that clause is gone again on 2026-09-06, the root having closed, which is the
only way a clause is meant to leave this file.** Three of the nine share one anchor: `rotate-x-*`, `rotate-z-*` and `scale-z-*` are all
waiting on a representation for transform functions, and a refusal that is one of several resting on a
single premise is the one most likely to be read as settled.

---

## Part 1 — What the survey found that nothing in the tree said

Nine findings. Each is checkable, and the ones marked ⚠ contradict something currently written down.

### F1 · `border-l-2` changes the layout and paints nothing ✅ *closed — the draw list reads all four edges*

**What it was.** `LayoutStyleBuilder` interned all seven border-width names and the layout honoured
each edge; the draw list took **one** thickness — `Layout.GetComputedBorder(node, Edge.Top)` — and
**one** colour, `border-top-color`. So `border-l-2` inset the content box by two pixels on the left
and drew no border anywhere, and `border-t-2` inset the top by two and drew a two-pixel border on
**all four sides**. The widths were read by one consumer and ignored by the other, which is worse than
inert, because the geometry moved and the picture did not follow.

✅ **Closed.** All eight per-edge longhands — four widths and four colours — now move the paint
channel, measured by the consumption probe rather than read off the source. The six `border-*` roots
that were `partial` for this reason (`border-*`, `border-x/y-*`, `border-t/r/b/l-*`) are `works`.
The claim that the *right* consumer acted, which the probe cannot make, is
`A_per_edge_border_colour_paints_only_the_edge_it_names` in `UtilityFamilySupportTests`: it sets one
edge colour and asserts a single band, at the bottom, two pixels tall, the element's full width.

⚠ **What is left is the logical pair, and it is a smaller gap than it looks.**
`border-inline-start-color` and `border-inline-end-color` still reach nothing — the widths beside them
do — so `border-s-*` and `border-e-*` remain `partial`, on `InertProperties.txt` #21.

### F2 · `rounded` is uniform for the same reason, one level down ✅ *closed — eight per-corner families*

**What it was.** `DrawListBuilder` interned `border-top-left-radius` and applied it to all four
corners. ExCSS expands `border-radius`, so `rounded-md` worked; `rounded-tl-md` was not a family, was
swallowed by `rounded`, failed the radius lookup and was dropped.

✅ **Closed at both levels.** The eight per-corner families (`rounded-t/r/b/l-*` and
`rounded-tl/tr/br/bl-*`) are registered, each emits exactly the longhand it names, and all four corner
longhands are separately read — which is the measurement that matters, because a builder still
applying one radius to every corner would leave the other three moving nothing and they would measure
inert. The eight roots moved `absent` → `works` and their `shadowed_by` cells are empty.

The per-corner claim the probe cannot make is `A_per_corner_radius_rounds_only_the_corner_it_names`,
which asserts the other three corners are square *and* that the scalar `DrawCommand.Radius` stays
zero — the exact bug being guarded is a consumer reading only the scalar and rounding all four by it.
`UiShape`'s eight floats of elliptical corner radii were never the limitation; the property bridge
was, and it is built.

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
three off `works` was `overflow-clip`.

⚠ **`clip` is now registered and all three rows measure `works`, and it maps onto `Overflow.Hidden`
rather than onto a fourth enum member — which refutes what this document asked for.** CSS separates
the two by what `hidden` grants and `clip` does not: a scroll container, and programmatic scrolling.
In this engine `hidden` grants neither. `ScrollView` reads no `overflow` at all and says so at length;
only `Overflow.Scroll` reserves a gutter or changes a fit-content size; `ScrollTop` is a control's
property that any element's stylesheet is irrelevant to. So an `Overflow.Clip` member would split
every `!= Visible` test in the flex, block, grid and inline algorithms into `is Hidden or Clip` and no
consumer could ever tell the halves apart — the same distinction-with-no-reader this table already
refused for `auto`. The author's keyword survives in the computed style, so the day a scroll container
exists the two can be separated there, where the information is.

⚠ **And the defect the keyword was hiding was not the clip.** `overflow: clip` already clipped the
draw list before any of this, because `OverflowReader` tests anything that is not `visible` — so the
picture was right and the *layout* treated the box as visible, keeping the §4.5 content floor that the
`hidden` beside it drops. That is `overflow: auto`'s bug exactly, one keyword later, and it is why a
keyword falling out of `LayoutStyleBuilder`'s table is never neutral.

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

### F5 · `truncate` does not truncate ✅ *closed 2026-08-22 — single-line ellipsis; `line-clamp` split off*

Tailwind's `truncate` is three declarations: `overflow: hidden`, `text-overflow: ellipsis`,
`white-space: nowrap`. Vixen's emitted the first only, so the name promised an ellipsis the engine
could not draw and the wrapping the third suppresses went on happening. Both halves were re-measured
on 2026-08-21 and both held: the family emitted `overflow` and nothing else, **and**
`text-overflow: ellipsis` moved none of the probe's four channels — so the gap was the text layer and
not the bridge, and widening the family first would have bought nothing but a greener-looking table.

⚠ **One correction to the finding as written: the test it named, `Truncate_emits_neither_text_overflow_nor_nowrap`, never existed.**
A NUL-safe search returns nothing anywhere in the tree, and this line was its only mention. The claim
it stood for was true — it had simply never been written down as a test, which is the shape of debt
this document exists to stop. What replaced it asserts the opposite fact and asserts it about pixels:
`Core/Vixen.Ui.Tests/TextOverflowTests.cs`, eight `Fact`s, none of which asks the cascade anything.

**Built in the order the finding demanded: reader, scene, then family.**

1. **`Vixen.Ui` draws the ellipsis.** `UiDocument.EllipsisOf` reads the property and
   `UiElement.Ellipsized(contentWidth)` replaces the tail of any line too wide for its box with
   U+2026, re-shaping the kept text and the marker as one string so the shaper kerns across the join.
2. ⚠ **It happens at paint, and that is forced rather than chosen.** `truncate` sets
   `white-space: nowrap`, which hands the wrap pass an infinite width on purpose, so the box only
   learns the width it has to fit into after its parent has shrunk it. Measuring is *supposed* to
   report the untruncated width — that is what makes the parent shrink it — and an ellipsis applied
   during measure would report a box that always fits and therefore never needed one.
3. ⚠ **`Block()` is left alone, which is what keeps a text field working.** `TextField` and
   `CodeEditor` index into it for the caret and for hit testing, so truncation lives on a second
   cached block that only `DrawListBuilder.EmitText` takes. Pinned by
   `Truncating_the_picture_leaves_the_text_the_caret_reads_alone`.
4. ⚠ **`text-overflow` inherits here and does not in CSS.** CSS applies it to a block container,
   where it reaches a child span's glyphs by putting them on the container's *own* line box. Vixen has
   no line box shared between elements — `InlineKnownGaps.txt`'s one-node-one-box invariant — so
   inheritance is the only route from the container the class is written on to the element that owns
   the glyphs. It over-applies to a nested block container's text, which CSS would leave alone; the
   full argument is on `UiDocument.EllipsisOf`. Without it the property would resolve on the element
   the class was written on, find no text there, and measure inert with the feature working.
5. **The gate got eyes before the family got the property.** No scene could observe an ellipsis:
   every one either let the label wrap or clipped a container whose text lived in a child free to be
   any width. The `clipped` scene is the fifth time this file has had to record that — `gridded`,
   `inlined`, `primed` and `translated` are the others — and `The_clipped_scene_can_observe_an_ellipsis`
   is a control over the scene rather than over the engine. **No line was added to `InertProperties.txt`.**
6. **Then the family.** `truncate` emits all three declarations, and `text-ellipsis` / `text-clip`
   join the `text` keyword table — `text-clip` earning its place as the opt-out an inherited property
   needs. `overflow-ellipsis` is dropped from the row's classes: it is v3's spelling and v4 removed it.

Both roots are now `works`, measured rather than asserted: `truncate` from `partial` and
`text-overflow` from `absent`, which is the whole of Typography's 4 → 6.

⚠ **`line-clamp-*` is deliberately NOT in this, and the reason is not effort.** A single-line ellipsis
needs no fragmentation — one box, one line, glyphs replaced at the end — which is why it was reachable
at all. A clamp is not that shape. `-webkit-line-clamp` changes **how many lines there are**, so it
changes the element's *height*, which puts it in the measure pass — the one place the final width is
not yet known and the one place truncation was just proved not to belong. It also needs
`display: -webkit-box` and `-webkit-box-orient: vertical` to be modelled, which are two more absent
properties, and the ellipsis lands on the last *retained* line rather than on an overflowing one. That
is a different algorithm in a different pass, and it is filed as its own item rather than smuggled in
behind a class name that looks adjacent.

### F6 · Pseudo-element selectors compile and nothing consumes them ✅ *closed — refused, not built*

`SelectorCompiler` parsed `::before`/`::after`, interned the name and stored it on `Selector`. A
**NUL-safe** search (`rg --text`, after the `ShorthandExpansion` lesson above) for a reader of
`Selector.PseudoElement` across `Core/` and `Editor/` returned four hits: the compiler that wrote it,
the record declaration, and two assertions in `SelectorMatchingTests` that it *was* written. Nothing
in `SelectorMatcher`, `StyleRuleSet` or `StyleResolver` filtered on it. So a rule written for `p::before` was
matched and applied **to the `p`**, and doc 09's supported-selector list, which named `::before` and
`::after`, was ahead of the code. Seven Tailwind variants (`before`, `after`, `marker`, `placeholder`,
`selection`, `file`, `backdrop`) depend on this, and it needs a test before anything is built on it.

**Confirmed, then closed by refusing it.** The survey's reading held up: the matcher's `Matches`
never touches the field, and a sabotage run — putting the old case back and running the new test —
shows `p::before { color: red } p { color: rgb(0, 255, 0) }` resolving the paragraph to `(1, 0, 0, 1)`.
Two further checks: no `.cs` under `Core/Vixen.Ui*` carries a NUL byte, so grep was not blind here,
and no `.vcss`, `.vxml` or `.css` anywhere in the repository uses a pseudo-element — nothing depended
on the wrong behaviour and **no visual baseline moves**.

**Option (1) — materialise the boxes — was refused for today and is A12's, not this finding's.** A
pseudo-element is a box in the layout tree with no element behind it, which is the one-node-one-box
invariant `Core/Vixen.Ui.Layout.Tests/InlineKnownGaps.txt` names as the blocker for anonymous boxes
and inline fragmentation: a `LayoutResult` holds one position and one rectangle, and the rounding
pass, the absolute walk and hit testing all assume it. Generated boxes need that machinery, so A12's
0.5 is an underestimate while the invariant stands, and the anonymous-box work is its real
precondition.

⚠ **The invariant has since moved and A12's estimate should NOT be revised on that basis.** B3's
fragment arena relaxed *one node produces many boxes*; a generated box is the other direction — a
box with **no node** — and is not served by it, because a fragment takes its style from the node it
belongs to and a pseudo-element has a style of its own. The sentence above is right that
anonymous-box work is A12's real precondition. See `InlineKnownGaps.txt`, which costs fragmentation,
anonymous boxes and generated boxes as three separate things rather than one.

⚠ **That precondition is now met, and A12's estimate should still not be revised upward from it.**
Anonymous block boxes landed after the fragment arena and cost less than this file's own estimate
implied — they take initial values for every non-inherited property, so they are never painted or
hit-tested and need **no stored rectangle at all**, only a line walk over a sub-range of a
container's children. Nothing was added to `LayoutResult`. What that means for A12 is that it is
*reachable* rather than *cheaper*: the sub-range machinery serves a run of **real** children, and the
half A12 was always going to have to build itself — a second **style** slot for a box that is not one
of them — is untouched by it.

**Option (3) — match and contribute nothing — was refused for the reason `SelectorCompiler`'s own
remarks already give.** A selector that compiles, matches and does nothing is this document's
recurring defect shape, and it leaves the author with no output and no message. The compiler's
contract has said "dropped with a diagnostic rather than approximated" since it was written; the
pseudo-element was the one thing breaking it.

**What landed.** The `PseudoElementSelector` case refuses with the reason *"a pseudo-element generates
a box of its own, and Vixen has no box without an element behind it"*, naming the fragment the author
wrote (`::before`) the way the `:has()` refusal does. `Selector.PseudoElement` is **deleted** rather
than left always-absent — it was `PublicAPI.Unshipped`, so nothing outside broke, and a field that can
only hold a sentinel is the same defect one step removed. The diagnostic reaches the log through
`UiDocument`'s existing drain (event 7004), proven by
`StyleDiagnosticDrainTests.A_pseudo_element_rule_does_not_colour_the_element_it_was_written_against`,
which asserts the **colour** rather than the compiler's output. Doc 09's list is corrected there.
`A_refused_pseudo_element_leaves_the_rules_around_it_matching_and_weighed_the_same` covers the
specificity/renumbering hazard: a refusal after `:is()` has already written into the shared
`SelectorTable` leaves entries nothing points at, which is waste and not corruption because every
offset is captured at write time.

### F7 · Arbitrary *properties* are not supported, and arbitrary *values* are ✅ *closed 2026-08-22*

`w-[37px]` works and is well tested. `[mask-type:luminance]` — Tailwind's arbitrary-property escape
hatch — parsed to an arbitrary value with an empty utility name, and `UtilityParser.TryParse` returned
false on the empty name. The class was silently unknown. v4's CSS-variable shorthand `bg-(--brand)` was
likewise unsupported: the parser looked for `[` and nothing else, so `bg-(--brand)` reached the colour
lookup as the literal text `(--brand)` and was dropped.

✅ **Both are implemented.** An arbitrary property parses to an empty `Name` with the property in a new
`UtilityCandidate.Property`, and `UtilityFamilies.TryResolve` answers it before consulting the registry
— the one candidate that must not be looked up, because having no family is what it *is*. The variable
shorthand is rewritten in the parser into the `bg-[var(--brand)]` it stands for, so one arbitrary-value
path serves both spellings and they cannot drift.

⚠ **One clause of the finding was wrong.** It said the utilities README "lists the escape hatches and
does not mention this one is missing". The README documented both gaps explicitly. The documentation
half of F7 did not exist; the README now describes all three hatches as working.

⚠ **The scanner hazard was checked and is not real.** `CandidateScanner.ScanStyleSheet` skips from an
identifier-then-colon to the statement terminator, and an arbitrary property is *made of* that colon —
so the narrowing looked likely to eat it in a `.vcss`. It does not, structurally rather than by luck:
`IsDeclaration` requires the colon to follow identifier characters back to the statement's first
non-blank character, and an arbitrary property always begins with `[`, which is not one. Inside an
`@apply` the statement begins with `@`, which is not one either, so even `hover:[color:red]` — whose
first colon *does* follow a bare identifier — is safe wherever it can legitimately appear. C# and
`.vxml` input go through the unnarrowed `Scan` and were never at risk. Both paths are now tested.

**How an arbitrary property is exempt from the consumption gate without a hole in it.** Nothing
validates what the hatch emits — there is no family to say what it means — which collides with
exit criterion 2. **No exemption code was written and none is needed.** `UtilityConsumptionProbe`
enumerates `UtilityFamilies.Surface`, computed from the registry; an arbitrary property is never
registered, so it is not on the surface, contributes nothing to `Emitted`, and can appear in neither
`Inert` nor `InertProperties.txt`. A branch saying "skip the gate for this" would be the actual hole —
the gate is strong because its domain is defined *positively*, by what the registry holds, rather than
negatively by a list of escapes. Nor can the hatch launder a family's debt, which is the real test:
registering a `--tw-*` fragment *was* a way to move a property out of `Inert` and needed an explicit
guard, but writing an arbitrary property in a `.vxml` changes `Surface` by nothing — it never reads a
source file — and the only way off the surface is deleting a registration, which stops every use of
that family generating anywhere, loudly. What the gate protects is a *promise*: the registry saying
`p-4` exists promises that `p-4` does something, so a `p-4` that does nothing is a lie only a hand
survey catches. An arbitrary property promises nothing. The author typed the property name themselves,
and "emitted, and dropped by the cascade if no consumer interns it" is the hatch's documented
behaviour rather than a defect in it. `ArbitraryPropertyTests` pins the structural claim, so a probe
rewritten to scan generated sheets instead of the registry fails there rather than widening the gate.

**Malformed input produces no rule at all**, on both halves of the colon: `UtilityParser.IsPropertyName`
refuses `[1..:red]` and `[mask type:red]`, and `IsPlausibleValue` refuses the value half exactly as it
does for `w-[1..]`. A negated `-[color:red]` and an opacity-suffixed `[color:red]/50` are refused
rather than emitted with the sign or the opacity silently dropped.

### F8 · The overloads are Tailwind's, not Vixen's ⚠ *correcting the brief*

The brief asks what the `text-` and `border-` overloads cost, on the premise that they are a Vixen
compromise. They are not. In Tailwind v4, `text-*` resolves against `--text-*` for a size and
`--color-*` for a colour and `text-center` is a static utility — three meanings behind one prefix,
exactly as here, and a colour named `--color-lg` is exactly as unreachable there as it is here.
`border-*` is `border-width` **and** `border-color` in v4's own registry. `font-*` is `font-family`
**and** `font-weight`.

So the overload is not a defect and the resolution order is not a Vixen invention. What lives next
door is **the longest-prefix split having no fallback**: `SplitName` returns on the first name that
matches and never reconsiders, so a value the chosen family cannot answer is reported as an unknown
class rather than retried against a shorter prefix.

⚠ **Settled 2026-08-22, and this section had it wrong three revisions running. The fallback rescues
nothing. What F8 was worth is the diagnostic, and that needs no fallback.** The claim under it — that
the `shadowed_by` rows would resolve if the split retried — was never run against the registry, which
is why it kept changing size. Run, it is false, and the two records that disagreed about it are
corrected below.

**What was measured.** For a retry to rescue a class, the shorter family must *answer* the value the
longer one was handed. Swept over every nesting pair the registry contains — `bg`/`bg-linear`,
`border`/`border-t`, `rounded`/`rounded-tl`, `divide`/`divide-x`, `overflow`/`overflow-x` and the
twenty-nine others — against every colour, radius, shadow, size, weight, family and screen key in
*both* shipped themes, the set of classes a retry would rescue is **empty**. Over the 641 class names
this table's `classes` column lists, 463 do not resolve and **none** is rescuable by a shorter prefix.
`Vixen.Ui.Styling.Utilities.Tests.ShadowedFamilyTests.A_shorter_prefix_would_rescue_nothing` runs the
sweep on every build, so the day a registration or a theme token makes it non-empty, it fails and
names the classes — rather than this paragraph going stale a fourth time.

**Why it is empty, structurally.** Every shadowed root has *exactly one* registered prefix.
`rounded-ss-2xl` is taken by `rounded` because `rounded` is the only registered prefix it has; there
is no second candidate to fall back to. A retry would re-offer `inset-s-4` to `inset`, fail on `s-4`
again, and stop. The longer families that do exist were registered precisely *because* the shorter
one could not answer, so by construction the shorter one still cannot.

**The `shadowed_by` column is 38 rows, not 39, and its composition is not what this section said.**
The four groups named here — logical insets, logical radii, per-axis transforms, `border-spacing-*` —
are 19 of the 38. The other 19 are `border-bs/be-*`, `font-stretch-*`, `text-shadow-*`,
`inset-shadow-*`, `inset-ring-*` and `ring-offset-*` (all three registered by 2026-09-06 and out of
the column since), `max-w-screen-*`, `flex-shrink/grow-*`, the `bg`
keyword sets (`bg-clip`, `bg-origin`, `bg-blend`, `bg-repeat`), `stroke-none` and `content-none`.
Three of those 38 the column *calls* shadowed are not: `bg-size-[auto]`, `bg-position-[center]` and
`font-features-[normal]` carry an arbitrary value, and `UtilityParser` sets `Arbitrary` before
`SplitName` is consulted at all — so they parse to the names `bg-size`, `bg-position` and
`font-features`, which were unknown families rather than shadowed ones and are registered families
now. Their notes are corrected in
the `.tsv`, which leaves the column at **35**.

⚠ **Worked 2026-08-22, and "35 registrations" was the fourth wrong count in this section. It is six
registrations and twenty-nine refusals.** The sentence that used to stand here — *each row is one
`Register` call plus whatever engine work the property implies* — reads as arithmetic and is the
instruction that produces twenty-nine inert classes. The question each row actually asks is the one
the grid families answered eleven times over: **does the layout or the renderer read the property**,
measured on `UtilityConsumptionProbe` rather than assumed from the CSS name.

**The six that landed** are `inset-s/e/bs/be-*` and `border-bs/be-*`, and they are all one finding:
the logical *inline* pair is read and mirrors, and the logical *block* pair is interned by nobody
while its physical twin is read. So `inset-s-*` emits `inset-inline-start` and `inset-bs-*` emits
`top` — `Vixen.Ui.Layout` has no writing mode, the block axis is top-to-bottom in every configuration
the engine can be in, and the two spellings are the same declaration. That is `space-y-*`'s argument
reused, and the asymmetry is the whole of it: the same physical fallback applied to the six logical
*radii* would be wrong, because a radius corner is named on the inline axis and this engine really
does mirror that one.

**The twenty-nine refusals are four shapes, and only the first is one the consumption gate can see.**

1. *The property is inert, and a registration turns the gate red.* `border-spacing-*` and its two
   axes (there is no table layout at all), `font-stretch-*` (interned by `InheritedProperties`, read
   by nothing — the gate keeps a control for exactly this), `text-shadow-*`, the four `bg` keyword
   sets, and `content-none`, which additionally has nothing to apply to since F6 refused
   pseudo-elements rather than building them.
2. *The property is inert and already allow-listed, so the root inherits a debt rather than adding
   one.* ⚠ **This shape is now empty, and how it emptied is worth more than the category.** It held
   `scale-x/y/z-*` and `rotate-x/y/z-*` over `scale` and `rotate`, "both `#23`" — sound reasoning
   over a premise that had already expired, and the sentence "the compositing raster that landed this
   week did not change that" was written in the week it changed exactly that. `scale-x-*` and
   `scale-y-*` are registered now, composed onto one `scale` the way the two translations are. **A
   refusal that cites another refusal inherits its expiry date and nothing here checks it** — the
   allow-list's own expiry only fires once somebody has already written the reader, which is a
   different and later moment. What is left of the six is shape 4: `skew-*`, `scale-z-*`,
   `rotate-x/y/z-*` and `translate-z-*` are emitted by v4 through `transform: rotateX(45deg)`. ⚠
   **This paragraph used to end "and there is no `<transform-function>` parser here, so they are a
   parser away rather than a renderer away", and that was false when it was written** — see A7 and
   #585. `TransformReader.Functions` reads `matrix`, `translate/X/Y`, `scale/X/Y`, `rotate`,
   `rotateZ` and `skew/X/Y`, composed right to left and pinned in `TransformTests`. ⚠ **`skew-*`,
   `skew-x-*` and `skew-y-*` closed on 2026-09-05 (#227) and the registration is all they ever
   needed** — an angle fragment apiece and `skewX(…)`/`skewY(…)` in
   `UtilityComposition.Transform`, which is `rotate-z-*`'s mechanism unchanged. Second family in a
   row to close on a premise that had already expired, and this one is worse than `rotate-z-*`'s:
   those rows carried no note at all, so there was no refusal to expire and nothing for
   `RefusalExpiryTests` to catch. The three-dimensional four are a *vertex* away and not a parser
   away: `UiVertex` has nowhere to put a `w`, so a projective quad would be rasterised with affine
   barycentrics (#548) — which is what their own ledger rows now say, and this taxonomy is the half
   that had not caught up.
3. ⚠ *The property is **read** and the **value** is refused, so a registration keeps the gate green
   over a class that paints nothing.* The dangerous shape, and no per-property measurement can catch
   it. ⚠ **Two of this shape's three examples closed on 2026-09-06, and neither closed by being
   registered — each closed by the value becoming one the engine reads.** `inset-shadow-*` and
   `inset-ring-*` emit `box-shadow`, which has always been read, and `box-shadow: inset 0 2px 4px
   #000` moved no channel where the outer form moved paint — so registering either would have scored
   the gate green over a class that painted nothing. `EmitShadow` reads the keyword now, both roots
   are registered with a per-value pixel assertion apiece, and the ten-month-old refusal turned out to
   be a claim about the lanes `UiShape` has rather than about the record it is (#279).
   ⚠ **`ring-offset-*` was worse than
   inert and is not any more (2026-09-05, #279).** An offset ring is a two-shadow *list*, and
   `EmitShadow` refused lists — so a `ring-offset-2` beside a `ring-2` would have stopped the ring
   painting altogether. `EmitShadow` paints a list now, a command each, **last to first** because CSS
   paints the first shadow on top and this draw list paints later commands over earlier ones, and it
   refuses the *whole* declaration if any item fails, which is CSS's own rule and what stops half a
   list looking like it worked. ⚠ **The refusal was never the branch its own remark described**: the
   split had to be added over the declaration's text, counting parentheses, because
   `StyleValueParser` splits on top-level *whitespace* — and ⚠ **not even the note on that was right**:
   with hex colours a list did arrive as `Unknown`, but the cascade normalises `#000` to
   `rgb(0, 0, 0)` and the same declaration then parses as a perfectly ordinary eight-item list whose
   fourth item ends in a comma. Both drew nothing, so which one it was never mattered until it did.
   ⚠ **`calc()` has since gone the same way** — `StyleValueParser` folds one, on the fold-or-refuse
   rule that keeps `calc(100% - 10px)` `Unknown` because a `StyleValue` is one number and one unit.
   ⚠ **So the root closed on a DECISION rather than on a mechanism, and it is the only one of the
   three that did.** What was left after both expiries was v4's `--tw-ring-offset-color: #fff`, which
   is not a colour anybody chose but the assumption that the page behind the element is white — and
   this chrome is dark. `Canvas` is CSS's own name for that answer, `SystemPalette` resolves it per
   document and follows the platform appearance, and one stylesheet is then right in both. ⚠ **An
   opaque gap colour then forced a matching drop in the draw list**: the unset offset slot cannot be
   dropped for being invisible the way `--tw-shadow`'s is, so `EmitOneShadow` learned that an outer
   shadow with no offset, no blur and no spread renders nothing — which is what § 7.1.1 already says,
   because an outer shadow is clipped to outside the border box. Without it, registering the family
   would have put a page-coloured rectangle under every shadowed element in the editor.
   `stroke-none` is the same
   shape one file over — `stroke` is read only as a colour, and `Icon.Resolve` falls back to the
   foreground for anything that is not one.
4. *The class is v4 compatibility surface, and § D5 already says not to implement it.*
   `flex-shrink-*`, `flex-grow-*` and `max-w-screen-*` are in `compat/legacy-utilities.ts`:
   registered, undocumented, superseded by `shrink-*`, `grow-*` and the sizing scale, all of which
   are here and read. **These three would have registered cleanly and passed everything**, which is
   why their absence is a policy and not a measurement — and why it is written down here rather than
   left to be rediscovered as an oversight.

Every one of the twenty-nine carries its measurement in the `note` cell of its own row, and the four
shapes are restated at the foot of `UtilityFamilies`' constructor, where the next person adding a
family will be. None of it was blocked or unblocked by the split above.

⚠ **What *was* real, and is now done: the two refusals were indistinguishable.**
`UtilityFamilies.TryResolve` returns `false` both for "no such family" and for "that family has no
such value", and `UtilityGenerator` put both into one `Unrecognised` list. For `Vixen.Editor.Ui` that
list is **7 103** entries, because the scanner is over-inclusive on purpose — so `bg-clip-text`, a
real Tailwind class against a root this engine registers, sat among seven thousand English words with
nothing to mark it out. Indistinguishable-from-a-typo was the failure mode, and it is the one thing
under F8 that cost anything.

`UtilityGenerator.Unresolved` is now a separate channel carrying `UtilityRefusal(Candidate, Family,
Detail, Kind)`: the family that was consulted, the value it had nothing for, and whether the refusal
was of a value or of a variant. The same split covers the case one field over — a utility that
resolves and whose *variant* does not, which used to be filed as prose despite having survived
`TryResolve`. The `.unrecognised.txt` report is sectioned, news first, and `StyleGen`'s build line
carries both counts. For `Vixen.Editor.Ui` that is 43 against 7 060.

⚠ **43 is an improvement of two orders of magnitude and it is still not clean, which is worth
recording because the obvious next step is wrong.** A build message per refusal was written and then
measured: 34 of the 43 are a bare English word colliding with a registered family name — `left`,
`me`, `to`, `size`, `from` — and most of the rest are CSS property names and comment prose scanned
out of a `.vcss`. Printing them on every build is the unread list again, louder. No channel
downstream of the scanner can undo the scanner's over-inclusiveness, which is deliberate: a false
positive costs one unused rule and a false negative is a style that silently does not exist. So the
count is on standard output, where a number that moves is visible, and the sentences are in the
report, where somebody chasing one class can read them.

### F9 · Doc 09's own 1.0 family list was never finished ✅ *settled — two written, three struck*

Doc 09 § *The utility preprocessor* names the families for 1.0 and the document is marked ✅ built.
Five of the names in that list had no family: **`space`**, **`divide`**, **`mix-blend`**,
**`origin`**, **`scroll`**. This was not a Tailwind-parity gap; it was doc 09 disagreeing with the
code, which is the thing `docs/overview.md` exists to catch and did not.

**Settled per family, with the reason, which is what C6 asked for.** ⚠ Two were written, three were
struck from doc 09's list, and **all three of those refusals have since expired** — the split was
never a matter of effort, it is whether any consumer reads the property, and building the consumer is
what moved each of them. Each of the three was *measured* inert through `UtilityConsumptionProbe.Channels`, over
all twelve scenes and at every value the family could emit, rather than argued from a grep.

| root | verdict | why |
| --- | --- | --- |
| `space-x/y-*` | **written** | `margin-inline-end` and `margin-bottom` are read; the family needed a compound selector, not a reader |
| `divide-x/y-*`, `divide-<color>` | **written** | `border-inline-end-width`, `border-bottom-width` and the four `border-color` longhands are read |
| `mix-blend-*` | **written** ✅ | ⚠ Refused here as moving no channel, and every part of that refusal has now been retired — including the correction that replaced it. `UiLayer.Blend` carries all sixteen of CSS Compositing 1 § 5.1's modes, `DrawListBuilder` opens a group for one, and `SoftwareUiRasterizer` applies it. `isolation` came with it. The one thing still owed is the device composite, which is a shader variant rather than a channel. See Part 9, Bucket 2 |
| `origin-*` | **written** ✅ | ⚠ Refused here as *unobservable*, and the last clause of that refusal — "`scale` and `rotate` are refused under **#23**" — was its expiry condition. Both are implemented now, `TransformReader` reads `transform-origin` into the point they turn about, and the family is registered. The refusal also needed a *scene*: the property is invisible without a transform whose fixed point matters, so `translated` could never have seen it and the new `turned` scene is what does — the seventh entry on `UtilityConsumptionProbe`'s list of arrangements that were missing |
| `scroll-*` | **22 of 32 written** ✅ | Part 8 § 3, discharged by **A18**. `ScrollView` reads `scroll-margin-*`, `scroll-padding-*`, `scroll-behavior` and `overscroll-behavior*` now, so the roots are registered against real readers rather than as properties on a box. The four block roots stay absent (`space-y`'s reason); `snap-*` is registered now against the snapping behaviour A18 could not have used, and of `scrollbar-*` only `scrollbar` is written — see Part 8 § 3 |

⚠ **The `origin-*` refusal was the one worth reading, and it is worth more now that it has been
retired.** What it said: every other inert verdict here turned out at least *possibly* to be a missing
arrangement — `grid-template-columns`, `vertical-align` and `transition-property` were each inert
because the probe had no scene for them — but a scene could not fix this one. A translation moves
every point of a box by the same vector, so its result is independent of the origin *by definition*;
`transform-origin` was therefore not unobserved but **unobservable**, and `translated` reporting zero
channels at every value was a confirmation rather than a gap.

Every word of that was true, and the sentence that mattered was the next one: "the two transforms that
would notice are refused at the draw list and will stay refused until a compositor lands." ⚠ **A
refusal is a verdict plus a condition, and this file records the verdict in bold and the condition in
a subordinate clause.** The compositor landed with `opacity`, was extended four more times for
filters, masks, drop shadows and backdrops, and none of those changes was read as touching this page.
`rotate` and `scale` are implemented now; a rotation about a corner is a different picture from one
about the centre; `origin-*` is registered, and the `turned` scene is the arrangement in which the
question means anything. It is the seventh entry on `UtilityConsumptionProbe`'s list — and the first
one that was predicted in writing years before it was needed and still missed, because the prediction
was filed as a reason not to look again.

**Two divergences from v4 in what did land, both deliberate and both pinned in
`ChildScopedFamilyTests`.** `space-y-*` emits the physical `margin-bottom` where v4 emits
`margin-block-end`: `LayoutStyleBuilder.EdgeNames` interns `-left`, `-top`, `-right`, `-bottom`,
`-inline-start` and `-inline-end` and no block pair, so v4's spelling measures inert — and it is not
an approximation to substitute the physical one, because `Vixen.Ui.Layout` has no writing mode for the
two to differ in. ⚠ **The second divergence recorded here — the scope emitted bare rather than inside
`:where()` — is closed**, and both the sizing and the shape it was sized against were wrong.

It said `SelectorCompiler` "charges a class for `:where()` exactly as it does for `:is()`" and that
closing it was three lines there. Measured 2026-09-05 in
`Core/Vixen.Ui.Styling.Tests/WhereSelectorTests.cs`: **ExCSS 4.3.2 has no `:where()` at all.** A
selector containing one comes back as a single `UnknownSelector` covering the *whole* selector — not a
complex selector with one unknown part — so `SelectorCompiler` never saw a `MatchesSelector` to charge
anything for, and refused the rule entire with a diagnostic. There was nothing at that site to
subtract a class from.

What landed instead is a repair at the one place the author's text still exists: that unknown node
carries it verbatim, so the compiler splits it on its top-level commas, rewrites each `:where(` to
`:is(`, hands each part back to ExCSS, and takes one class off the result per top-level occurrence.
⚠ **And the fix is *not* to wrap the scope**, which is the other half the sizing got wrong:
`.space-y-4 > :where(:not(:last-child))` lands at `(0,1,0)` and merely ties with the child's own
`.mb-0`, leaving the winner to be decided by the generator's ordinal sort of class names. The whole
selector is wrapped — `:where(.space-y-4 > :not(:last-child))`, v4's own — so the rule is `(0,0,0)`
and the child wins whatever it is called.

**What is absent inside the two families, and why.** `space-x-reverse`, `space-y-reverse`,
`divide-x-reverse` and `divide-y-reverse` need `calc()` to multiply an edge by a `--tw-*-reverse`
flag. ⚠ **Both halves of that reason have now expired, and the measurement was taken before either
was lifted.** `StyleValueParser` folds a `calc()`, so the multiply resolves; and
`ReverseFlagTests` measures the other half end to end — a flag one class writes is read by another
class's declaration, it inherits down to the descendants the `> :not(:last-child)` rule actually
matches, and both arms of v4's arithmetic fold at both values of the flag. So these would not be the
inert roots Part 8 § 3 declines to add for `scroll-*`; the mechanism works.

⚠ **They stay absent anyway, and the surviving reason is one paragraph up rather than anywhere near
`calc()`.** `divide-x` and `space-x` write *one* edge on purpose, because writing the leading edge as
well would out-specify a child's own `border-s-2` and silently erase it — and v4's reverse flag works
by flipping which of *two* written edges carries the width. Writing that leading edge as
`calc(w * var(--tw-divide-x-reverse, 0))` instead of as a literal `0` changes nothing about the cost:
it is the declaration that out-specifies, not its value. So registering these four is a decision about
whether to reverse the one-edge choice and pay for it, not a decision about whether the machinery
exists. `ReverseFlagTests.The_four_reverse_spellings_are_still_absent` is what fails the day somebody
registers them without taking it.

⚠ **The five `divide-<style>` keywords were on that list and are not any more.** They were absent
because `border-style` had no reader — measured, like the rest — and A3 gave it one. They are merged
into the `divide` family rather than registered as a family of their own, which is what keeps the
`> :not(:last-child)` scope: a family of their own would have written `border-style` on the
*container*, dashing a border it does not have and leaving the dividers solid.

⚠ **The consumption gate had to grow eyes for this shape of family before any of it could be
trusted, and the blindness was total rather than partial.** `UtilityConsumptionProbe.Emissions`
measured what the family table puts on *the element carrying the class*, and a scoped family puts
nothing there — so `space-x-4` and `divide-y` returned no properties, entered neither `Consumers` nor
`Inert` nor `Composed`, and were unclassified in a file whose whole claim is that nothing escapes
classification. That is worse than the six times the scene list has been the thing missing: an inert
verdict is at least a verdict. The probe's element has two children now — two, because
`:not(:last-child)` matches nothing under one — and the bare baseline has two as well, or every
property the child *inherits* would be credited to the family. No new scene was needed: every
longhand these families emit was already emitted and already read by a `m*`/`border-*` family, which
is exactly why they were worth writing and exactly why the gate alone could not have told anyone
whether the selector matched. `ChildScopedFamilyTests` is what asserts that half, in a real cascade
and a real frame.

✅ **Closed.** Exit criterion 3 wants a row per root in
`Editor/Vixen.Editor.Ui.Tests/UtilityFamilySupportTests`, and the five new roots had none. Its
`Supported` theory puts the class on the element it then reads, which is the one arrangement a scoped
family is invisible in, so they got a pair of `Fact`s there rather than five table rows:
`The_five_child_scoped_roots_reach_every_child_but_the_last_and_never_their_own_element` carries the
five rows against the editor's own tokens, and `A_divider_paints_a_band_beside_every_child_but_the_last`
is the frame. The hole was in the inventory rather than in the coverage —
`ChildScopedFamilyTests` already asserted the mechanism — which is why the two new `Fact`s are written
against `EditorTheme`'s tokens rather than `UtilityFixture`'s: `divide-accent` has no meaning without a
theme to resolve `var(--accent)` out of, and that is the one claim the older file cannot make.

⚠ **And the obvious oracle for it was uninformative, which is worth more than the row.** The colour
assertion was first written as "the band is blue-dominant", copied from
`A_per_edge_border_colour_paints_only_the_edge_it_names` next door and from that test's comment "the
accent is blue-dominant; the surface and border tokens are not". The second clause is false and always
was: this palette's greys are *cool* greys — `--border` is `#a9adb4` light and `#1e1e20` dark, and B
exceeds R in both, as it does for `--surface` and `--text`. Swapping `divide-accent` for
`divide-border` left the new test green. Both tests now hold the painted colour against a separately
resolved value, and both fail on that swap.

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

⚠ **Three limitations found while proving it — the header said two and the list has always had
three, which is the smaller of the two things wrong with this paragraph.** All three were real and
all three are now closed.

- ✅ **A transition only ran where the previous computed style *also held the property* — closed.**
  `Observe` read the displayed value out of `before`, and a cascade with no computed-value stage had
  nothing to offer for a property the element did not previously declare — so fading `margin-left`
  from an implicit `0` did not happen, while fading it from a declared `0px` did.
  ⚠ **What made this look bigger than it is was reading the table as a design choice.** It is not:
  two checkable rules decide every entry. A property qualifies when **it does not inherit** — because
  `StyleResolver` materialises inheritance into the computed style, so an inherited property the
  element did not declare is *already there*, with the value it really computes to, and an entry for
  `color` would fade every label out of the user agent's black — and when **its initial value is one
  `StyleValue.Lerp` can travel from**, because a keyword initial (`left: auto`, `filter: none`)
  interpolates discretely in CSS too, so an entry would buy a jump at the halfway mark in place of no
  transition. `InitialValues` is the table; anything outside it keeps the snap it had.
  ⚠ **And the half that is more visible in a real interface was the reverse one.** `Observe` looped
  over the *new* style's properties, so a `:hover` rule that adds a property — which leaves it out of
  the computed style again the moment the pointer goes — faded in and never faded back. That
  asymmetry cannot appear in a fixture that only ever adds a class, which is what every transition
  fixture here did. `InitialValueTransitionTests` holds both halves and both sabotages.
- ✅ **The `transition` utility did nothing on its own — closed.** Vixen's family emitted
  `transition-property` and stopped; Tailwind's also emits a 150 ms duration and a timing function.
  ⚠ **The consumption gate could not have caught it and it is worth being exact about why**: that gate
  is per-*property*, `transition-property` measured `read` throughout off a scene that supplies a
  duration of its own, so the family could score `works` on everything it emitted and still be a class
  that moves nothing when written alone. It reads `var(--tw-duration, 150ms)` now rather than a
  literal, because `UtilityGenerator` orders rules by ordinal class name and `duration-1000` sorts
  before `transition` — a literal would have made `transition duration-1000` a 150 ms transition.
  ⚠ **Only the duration was missing**: CSS's initial timing function is already `ease`, so emitting
  one would buy nothing and would overwrite the `ease-*` beside it for the same ordering reason.
  `TransitionUtilityTests` holds all three claims, reading the width between the endpoints.
- ✅ **A fading inherited value did not reach the children — closed in the overlay pass.** The
  animator is a tier over the finished cascade: `StyleUpdater.Resolve` inherits from the parent's
  *cascaded* style (`styles[parent]`) and `UiDocument.Apply` overlays the running values per element
  afterwards. So a panel fading its `color` handed its descendants the destination on the first frame
  while the panel itself travelled, and a descendant could not start its own transition to cover it
  because `transition-*` do not inherit.
  ⚠ **The decision, and it went the other way from how this paragraph used to read.** Two closures
  were on the table — inheriting from the parent's *overlaid* style in `StyleUpdater.Resolve`, or
  pushing the parent's displayed value down in `UiDocument.Apply` — and this said the first was the
  obvious, expensive one and the second a heuristic. **The first does not work at all**, which is the
  half nobody had checked: a cascade is not a per-frame pass. `UiDocument.Tick` calls
  `InvalidatePositions` while a transition runs and deliberately never `Invalidate`, because a fade
  changes nothing the cascade decided — so nothing re-resolves between the frame a fade starts on and
  the frame something else changes. Inheriting the overlaid style would therefore hand each descendant
  whatever the parent was displaying *at the last cascade*, which is the fade's **start** value, held
  for the whole fade and kept after it ended. That is worse than the destination, which is at least
  where the frame is going. The predicted costs — a sharing cache that stops sharing and an `Announce`
  that re-targets every frame — are real and would have been paid for a broken picture.
  ⚠ **So the pushed-down version landed, and its heuristic is the only approximation in it.**
  `InheritedProperties.Descend` is called from `UiDocument.Apply`'s existing parent-to-child walk with
  the parent's cascaded and displayed styles in hand; it writes the parent's moving value over each
  inherited property whose value on this element **is** the parent's cascaded one. That test cannot
  tell "the child inherited this" from "the child declared the same value" — a `ComputedStyle` does
  not record which — so an element that declared its parent's colour is carried along with the fade.
  The alternative is a provenance bit per property in every computed style, paid on every element of
  every document to serve the frames where something is fading.
  ⚠ **What it cost is one pointer comparison per element per frame.** `Animator.Apply` returns the
  instance it was given when it overlays nothing, so `ReferenceEquals(parentCascaded, parentDisplayed)`
  is the whole test on a document with nothing in flight, and `Descend` is not called at all. An
  element running its own transition on the property keeps it, and hands *its* value on in turn, so a
  chain needs no state carried across the walk beyond the parent's two styles.
  ⚠ **The instrument is `Vixen.Ui.Tests.TransitionTests`, and it used to assert the defect.**
  `An_inherited_value_reaches_the_children_at_its_destination_rather_than_mid_fade` pinned the old
  behaviour and said in its own remarks that the right response to it going red was to rewrite the
  test. It is now `A_fading_inherited_value_reaches_the_children_mid_flight`, reading a colour that is
  neither endpoint on the panel, the child and the grandchild — the endpoints agree under both
  behaviours, so nothing weaker could see it. A second fixture holds the negative half: a child that
  declares `color: #ff0000` keeps it, and hands red rather than the fade down to *its* child.

### F11 · The whole of `@media` was evaluated against a surface that does not exist ✅ *closed*

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

⚠ **`@media` was decided at load and not at match**, which `StyleSheetLoader` said and gave the
reason for — so re-asking the question on a resize was somebody's job, and it was nobody's. The first
fix was `StyleEngine.SetMedia` guarded on the *verdicts* rather than on the context: the conditions
the loader saw were replayed against the old context and the new one, and the sheets were reloaded
only where one of them disagreed. Without that guard a window drag would have been a full ExCSS
re-parse of every sheet sixty times a second, and would have restarted every fade in the window each
time. **That is no longer how it works** — see the per-surface note below, which removed the reload
rather than guarding it.

⚠ **And it uncovered a latent crash that predates all of this.** `StyleUpdater` builds a
`StyleInvalidator` over `StyleEngine.Selectors` in its constructor and keeps a cursor into
`StyleEngine.Rules`; `StyleEngine.Reload` replaces both. A reload that produced fewer selectors read
somebody else's compound and invalidated the wrong subtree, and one that produced more read off the
end and threw — reachable through `UiDocument.ReloadStyles` and therefore through every hot edit of a
stylesheet, and invisible only because a hot edit rarely changes the rule count much. A breakpoint
being crossed turned a dropped block into rules, which adds selectors by construction, so the first
`@media` re-evaluation found it immediately. ⚠ **The finder is gone:** crossing a breakpoint no
longer reloads anything, so `StyleUpdater.Refresh` is now exercised only by `StyleEngine.Replace` —
a hot edit of a stylesheet — which is where the latent crash lived all along and where it would have
gone on living unnoticed.

✅ **Closed 2026-09-05.** `StyleUpdater.Refresh` landed with `47151dfe` and rebuilds the invalidator
whenever the engine has replaced its `SelectorTable`, which resets the rule cursor with it. ⚠ What
was still missing was anything that could tell you so: no test anywhere drove a `StyleUpdater` across
a `StyleEngine.Replace`, and the paragraph above went on saying the fix was owed.
`Core/Vixen.Ui.Styling.Tests/StyleUpdaterReloadTests.cs` now replaces a sheet in **both** directions,
and the two are not one test twice — the growing case throws out of `SelectorTable.Compound`, and the
shrinking case leaves the cursor past the end of the new rule set, so the read loop never runs, the
map still describes the sheet that is gone, and the element that should have restyled silently keeps
the style it had. Sabotage (`Refresh` returning unconditionally) is red on both, one per failure mode.

**Sized at 0.3 EM and landed with A20**, because it is the same shape of bug and the same seam.

✅ **Verified 2026-08-21.** `StyleEngine.SetMedia` is in the unshipped surface
(`PublicAPI.Unshipped.txt`) and `Core/Vixen.Ui.Tests/MediaContextTests` covers the re-evaluation. ⚠
The *guard* that paragraph described — replaying the loader's recorded conditions so a drag did not
re-parse every sheet — no longer exists, because the reload it was guarding no longer exists; see
below. The test that asserted it has been rewritten to assert the opposite, which is the behaviour
that is now correct.

✅ **Per-surface media landed 2026-08-22, and the obvious fix was the wrong one.** The account above
was right about the cause — `@media` produced rules, rules are shared by every surface of one
document, so the verdict lived in the rule set and there could only be one of it — and wrong about
the remedy. It said per-surface media "would mean a rule set per surface"; **it does not, and a rule
set per surface is unaffordable.** A reload was measured at **42 ms** for the editor's twelve sheets
(245 KB, 1 398 rules), so a four-window editor would have paid 170 ms of ExCSS on one drag, plus four
matchers, four interning caches and four animators for a set of windows whose whole point is that
they share a theme.

**What moved was the verdict, not the rules.** A `@media` block's rules are now loaded whatever the
condition says, each tagged with the `MediaConditions` group it came from — a conjunction, stored as
a link to its enclosing group so nesting still conjoins — and each surface carries a `MediaVerdicts`
vector in `MediaScopes`. An element carries its surface's scope on its `StyleTree` slot, inherited
from its parent at creation, and the cascade tests one integer before the matcher runs. The rules
stay shared; only the yes-or-no is per window.

Three consequences worth recording, because none of them was the goal:

- **Crossing a breakpoint went from 50 ms to 0.04 ms**, measured the same way, and is now
  indistinguishable from a resize that changes nothing. `SetMedia` no longer reloads at all, so the
  guard it needed is gone and so is the fade-restarting the guard existed to prevent.
- **A condition nobody can read is refused once, at load.** The loader used to drop a nested block
  *unread* when its enclosing condition was false, so a typo inside a breakpoint no window had
  reached stayed silent until somebody made a window wide enough. Every group is walked now.
- **`StyleRuleSet.SharingIsSound` had to become per-surface**, which is the regression this design
  would otherwise have shipped invisibly: a block that does not apply is now *in* the rule set, so
  one `:nth-child` sealed behind an unreached breakpoint would have turned the sharing cache off for
  the whole document, for ever, with every style still correct and only the restyle rate to show for
  it.

⚠ **One line was drawn rather than discovered, and it is the one to revisit if anybody complains.**
`@keyframes` and `@layer` inside a `@media` load unconditionally, because both are document-global by
construction — one keyframes table and one layer order per rule set, shared exactly as the rules are.
Neither does anything alone: a keyframes definition is inert until an `animation-name` names it, and
that declaration is in a rule and *is* gated. A keyframes table per surface would be a much larger
change for no case anybody has.

`EditorPane` publishes the gamut per pane now, which is only correct because of the above — a window
on a wide-gamut display next to one on sRGB gets its own answer, and the main window keeps its own.
`Core/Vixen.Ui.Tests/PerSurfaceMediaTests` is the coverage; every assertion is on a box or a colour
rather than on a rule count, which matters more than it used to, because a block that does not apply
now has rules in the set and anything counting them would pass.

⚠ **Nothing in the repository exercises any of this yet.** There is not one `@media` in any shipped
`.vcss` and not one responsive variant in any `.vxml`, which is why the original bug survived a
release and why this fix moved no screenshot baseline. It is correctness banked in advance, not a
defect anybody was hitting.

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
| Registered variant keys | 88 | 51 | 58 % |
| Arbitrary variant `[&>*]:` | ✅ | ✅ | |
| Arbitrary value `w-[37px]` | ✅ | ✅ | |
| Arbitrary property `[mask-type:luminance]` | ✅ | ✅ | F7 *closed* |
| CSS-variable shorthand `bg-(--brand)` | ✅ | ✅ | F7 *closed* |
| `/opacity` modifier | ✅ `color-mix` | ✅ `color-mix` | A9 *closed* |
| `!important` | **suffix** `bg-red-500!` | ✅ suffix | *matches v4, not v3* |
| Negative values `-mt-4` | ✅ | ✅ | |
| Prefix (`tw:flex`) | ✅ | ⛔ | |
| Two media variants on one utility | nests | ✅ nests | A15 |

The 54 Vixen covers: `hover focus focus-visible focus-within active disabled enabled checked first
last only odd even empty first-of-type last-of-type only-of-type read-only placeholder-shown
indeterminate dark ltr rtl group peer data aria
not nth nth-last nth-of-type nth-last-of-type motion-safe motion-reduce contrast-more contrast-less
forced-colors inverted-colors portrait landscape print noscript pointer-none pointer-coarse
pointer-fine any-pointer-none any-pointer-coarse any-pointer-fine has` plus the five breakpoint names
when the theme declares them. ⚠ It was 25 until A13's structural half and A14 landed, and 51 until
A13's three form states; this figure is hand-kept and nothing checks it, so it is spelled out as a
list rather than as a number for the reason two paragraphs of Part 0 give about the family count — a
bare figure beside a table nobody regenerates is the copy that rots.

⚠ **The bucket sizes below do not add up to the difference, and forcing them to would be the
dishonest arithmetic rather than the honest one.** The 88 is a count of Tailwind's variant
*families* and this is a count of registered *keys*: `pointer-*` is one line in v4's documentation
and three keys here. Each bucket says what it is about; none of them is a share of a total.

What is left falls into three quite different kinds, and lumping them together is how this gets
mis-sized:

- **Fourteen are a table entry plus an element-state bit** the control library has to set — and
  ⚠ **eleven of those fourteen are refusals rather than work**, which is the shape this bucket has
  never had written down. ⚠ **It said twenty-two and "none needs a matcher change", and both halves
  were wrong.** Five of the twenty-two — `empty`, `not-*`, `nth-*`, `nth-last-*` and `*-of-type` —
  are structural and are registered; the of-type family was precisely the one that *did* need a
  matcher change, because a child index is stored on every element and an of-type index is a position
  among the siblings sharing a tag, which nothing stored. It reads as a table entry because
  `:nth-of-type(n)` and `:nth-child(n)` agree on every document whose children all carry one tag.
  Three more are registered now — `read-only`, `placeholder-shown` and `indeterminate` — with bits on
  `ElementState`, arms in `SelectorCompiler` and writers in `TextField`, `CheckBox` and
  `ProgressBar`. ⚠ **A table entry is worth nothing without a writer**, which is the half the sizing
  missed: a pseudo-class compiled against a bit no control sets resolves, indexes and matches
  nothing, and passes every scene in `VariantCoverageTests` — so the writers have a test file of
  their own. What is left, per reason rather than as a count:
    - **no validation model anywhere in `Vixen.Ui.Controls`** — `required`, `optional`, `valid`,
      `invalid`, `user-valid`, `user-invalid`, `in-range`, `out-of-range`. Eight of the fourteen, one
      missing concept.
    - **no navigation model** — `visited` and `target`, ⛔ refused rather than owed.
    - **one each** — `autofill` needs a credential store, `default` needs a form, `inert` needs a
      subtree flag nothing carries.
    - ~~**`open` is a parser problem before it is a state bit**~~ — ⚠ **landed, and the refusal
      expired rather than being satisfied.** ExCSS 4.3.2 does still return `:open` as an
      `UnknownSelector` — measured again, not assumed — and that stopped being a blocker the day
      `:user-valid` shipped with the identical problem: `SelectorCompiler.TryRewrite` re-reads a
      selector ExCSS could not parse, and `:open` rides that scan. `ElementState.Open` is the bit,
      `Expander` and `SelectBase` are the writers, and `:open` is the first name on that scan that
      is a *prefix* of other names CSS spells — so its arm looks at what follows, which the two
      before it do not have to.
- **Seven need pseudo-elements to mean something** — F6.
- **The media features are done** — A14. ⚠ The bucket said "thirteen … each one condition in
  `MediaQuery`" and it listed twelve, of which **two needed no condition at all**: `portrait` and
  `landscape` are `(orientation: …)`, which has been answered from the surface's own width and
  height since media queries existed here, so they were a table entry and nothing more. The other
  ten are sixteen keys, because `pointer-*` and `any-pointer-*` are three each.
- **The rest are engine features**: `supports-*`, `starting` (`@starting-style`), `in-*`, `*`/`**`,
  and the whole container-query family `@`/`@min`/`@max` — Part 2. ⚠ `has-*` was the head of this
  list on doc 09's P2 deferral and is registered now — A17.

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
divergence. ⚠ **The first of those arrived on 2026-09-05 and the divergence stayed anyway**:
`StyleValueParser` folds exactly the `+ - * /` doc 09 describes, so the engine could now read v4's
own spelling — but build-time resolution is not made wrong by that, and changing it is a decision
about where the scale is resolved rather than a gap. Vixen's `SpacingBase` is already one number, so the *model* matches; only the emission
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

### D3. Container queries are a feature, not a variant 🟡 *the query answers in a live document — the variants are owed, and their blocker was never the wiring*

v4 builds container queries in: `@container` marks the container, and `@sm:`…`@7xl:`, `@max-*`,
`@min-[…]`, named `@container/main` + `@sm/main`, and stacked ranges `@sm:@max-md:` are variants over
it. For a **tool window this is the more correct question than a breakpoint**, and the editor's own
theme file says so already:

> No `screens`. A tool window is not a page: a panel is sized by the dock that holds it and not by the
> display, so a `md:` variant would be asking the wrong question.

That paragraph is an argument *for* container queries, written by someone who had none. It is the
reason this is not optional for an editor: a panel that must lay out differently at 300 px and 900 px
is the normal case, and the mechanism that answers it is `@container`, not `@media`.

**The shape the survey described was right and three of its four cost estimates were wrong**, two of
them cheap and one of them in the other direction. What follows is what the build established, then
what is left.

#### ⚠ It parsed all along, and the loader dropped it in silence

The survey assumed `@container` would need the treatment `@layer` got — a hand-written reader over
text ExCSS hands back unparsed. **It does not.** ExCSS 4.3.2 has a first-class `ContainerRule` with
`RuleType.Container`, and it splits the prelude for you: `Name` is `card`, `ConditionText` is
`(min-width: 400px)`, and the block's children arrive as ordinary `IStyleRule`s. `container-type`,
`container-name` and the `container` shorthand likewise come through as ordinary declarations. So the
parsing cost was zero, and item 4's grammar work is smaller than it looked.

**And that is exactly why it was broken.** Because a `ContainerRule` is not `RuleType.Unknown`, it
never reached `StyleSheetLoader.LoadUnknown` — it fell out of `LoadInto`'s `switch` through
`default:`, contributing nothing, **with no diagnostic at all**. Two places said otherwise in prose:
`StyleDiagnosticDrainTests`' remark listed "a `@container` query Vixen has not implemented" among the
at-rules that reach the log, and `docs/guide/ui/stylesheet-diagnostics.md` repeated it. Neither had a
test — the drain test asserts on `@nonsense` — so a rule that vanished without a word was documented
as one that warned. Both are corrected.

This is the section's own hazard arriving in the section: not a query that never matches, but a
*whole at-rule* that never loaded, with documentation asserting it was handled.

#### ⚠ The containment question, which is the one item that could not be sized from outside

The survey's item 1 said `size` containment "is a constraint the layout has to *enforce* and not
merely record, or the query is circular", and item 3 called the style↔layout cycle the concentrated
risk. Both are answerable now, and the answer is better than feared for the case the editor has and
worse than feared for the general one.

**The independence already exists and is already the default.** Vixen's layout is Yoga-derived, and
the sizing mode it resolves per axis is `SizingMode` (`Core/Vixen.Ui.Layout/FlexAxis.cs:109`):
`StretchFit`, `MaxContent`, `FitContent`. For a normal-flow block, `CalculateBlockLayoutImpl` takes
this branch (`Core/Vixen.Ui.Layout/LayoutTree.Block.cs:144-161`):

```csharp
if (widthSizingMode == SizingMode.StretchFit) {
    rawWidth = availableWidth - marginAxisRow;   // the parent's, with no child consulted
} else {
    ... DetermineBlockContentWidth(...)          // probes children
}
```

`width: auto` on a normal-flow block is **not** shrink-to-fit — the code says so in a comment at 138.
So a panel in a dock, a block filling its parent, a grid item in a fixed track: their inline size is
*already* a pure function of the parent's available size, and `container-type: inline-size` on one of
them is an assertion that is already true. **For the editor's actual containers there is no cycle and
nothing to enforce.**

The cycle is real for everything that escapes that branch: anything reaching
`DetermineBlockContentWidth` (`LayoutTree.Block.cs:746`), a flex item sized by its basis, a grid item
in an intrinsic track, `width: max-content` / `fit-content`. For those, `container-type` must either
coerce the axis to `StretchFit` or be refused. **Coercion cannot be expressed from outside
`Vixen.Ui.Layout`**: `LayoutUnit.Stretch` looks like the way to say it and is an *unimplemented enum
member* — `StyleLength.Resolve` handles only `Point` and `Percent`, and nothing in the tree references
`LayoutUnit.Stretch` at all. So the coercion is a change to the layout project, and refusal with a
diagnostic is the cheaper interim.

⚠ **The interim landed on 2026-09-05 and it is a *report* rather than a refusal, which is the weaker
of the two and the one that could be built without touching the layout.** `Settled` already said a
document had not reached a fixed point; what it could not say is which box to change, and an
interface has a dozen containers. `Recontain` now records every container whose own provided scope
moved during a walk, and the branch that gives up names them on log event **7007** with the box each
measured on the last pass. It is a measurement rather than a predicate: "is this container sized by
its contents" would have to know about flex bases, intrinsic grid tracks and four content keywords
and would be a guess in exactly the arrangements it was written for, whereas a box that changed
between two passes of one frame changed. ⚠ **The oscillation is unchanged** — the frame is still
drawn one pass stale — so the coercion is still the owed item and this only stops it being anonymous.
`ContainerWiringTests.A_container_sized_by_its_contents_never_settles_and_the_log_names_it` is the
first fixture in the repository that oscillates on purpose, and its twin asserts that a document
which settles writes nothing, because every container in a fresh document moves on its first pass.

**The ordering problem is already solved and already bounded.** `UiDocument.Update()` runs
`Restyle(); Arrange();` and then `Settle()`, which re-runs both up to `SettlePasses = 3` times while
handlers keep dirtying the document, and reports non-convergence on `Settled`
(`Core/Vixen.Ui/UiDocument.cs:1020`). A container-query re-cascade is that loop's existing shape, not
a new one — and where containment does hold, one extra pass is provably enough, because a contained
container's size cannot move in response to its descendants' styles.

#### ⚠ A scope per container element would have destroyed the sharing cache, silently

Not in the survey, and it decides the data structure. `StyleSharingKey` carries the media scope, and a
container scope has to join it or two rows in differently-sized containers share a computed style. The
obvious design — a scope per container *element*, which is what `MediaScopes` does — gives every row
of a thousand-row list a distinct scope id, so **no two rows ever share**: a document using one
container query would lose the sharing cache entirely, and only the documents big enough to need it
would notice. `ContainerScopes` therefore interns on the chain **by value** (`parent`, `name`, `box`),
which collapses a thousand identical rows to one scope and keeps sharing exactly as good as it was.
The cost is churn while a box is moving, and `Reset()` is the whole eviction policy today — see below.

#### What landed

All of it in `Core/Vixen.Ui.Styling`, which is the half that can be tested without a layout:

- **`ContainerConditions`** — the conjunction tree, the same shape as `MediaConditions`, with a
  **name** alongside the condition because the name selects *which box* the condition is asked of.
- **`ContainerScopes`** — the chains, interned by value, verdicts cached against `Revision`.
- **`ContainerQuery`** — `width`/`height`/`inline-size`/`block-size`/`aspect-ratio`/`orientation`, with
  `min-`/`max-`, and a **refusal** for every media-only feature. `@container (prefers-color-scheme:
  dark)` is a diagnostic, not a query answered off whatever surface the element happens to be on.
- **`StyleRule.Containers`** — a second, independent group id. `@media` and `@container` nest through
  each other in either order and both must hold; one tagged chain would have had to interleave two
  verdict tables.
- **Two slots on `StyleTree`**, not one: what an element *asks* and what it *provides*. A container is
  not inside itself (CSS Containment 3 § 5.1), and collapsing them is wrong in the direction that
  hides — a container answering its own query matches slightly too often, so every test of the common
  case still passes.
- **One integer test in the cascade**, before the matcher, next to the media one.

`ContainerQueryTests` is 34 cases, every one asserting a **resolved computed value** and none
asserting that a rule parsed — the distinction this section exists to make, since `@container` parsed
throughout the period it did nothing. Nearly all of them assert positively *and* negatively against
the same rule in a differently-sized box.

⚠ **Verified by sabotage, and the fifth one found a real gap.** Five deliberate breaks: the cascade's
verdict test removed (9 failures), a container made to answer its own query (1, the test that names
it), `inline-size` allowed to answer block-axis queries (2), the loader's `@container` arm made
unreachable (12) — and **relaxing the name test so a named query falls back to an *unnamed* container
was caught by nothing.** `@container card (…)` answering off whatever box is nearest is the worst
failure of the set, because it is right until somebody adds a wrapper.
`A_named_query_does_not_fall_back_to_an_unnamed_container` closes it and was re-checked against the
same sabotage.

#### ⚠ The wiring landed, and the convergence question has a number

`UiDocument.Recontain` (`Core/Vixen.Ui/Containers.cs`) is the caller `ContainerScopes.Enter` was
missing. It reads `container-type`, `container-name` **and the `container` shorthand** off each
element's computed style, enters a scope per container from its **content box**, re-assigns the
subtree, and invalidates when a scope moved. `ContainerWiringTests` — fifteen cases in
`Vixen.Ui.Tests` — asserts a resolved value on an element inside a container of a given size, and
mostly asserts a *box*, so a pass means the declaration reached the layout tree.

Four things the plan got slightly wrong, each cheap and each worth recording:

- **Not `UiDocument.Apply`.** `Apply` runs *before* `CalculateLayout`, so reading `container-type`
  there is reading the declaration in the one pass that cannot see the result of it. The walk goes at
  the **end of `Arrange()`**, off the same `ComputedStyle` `Apply` just wrote.
- **Inside `Arrange`, not beside its two callers**, so the settle loop's own pass re-enters the
  scopes. A walk called once per `Update` passes every one-level test and fails only where one
  container's query decides another container's size — which is the case
  `A_container_inside_a_container_resolves_and_costs_a_pass_per_level` exists for, and it was written
  because that sabotage was caught by nothing else.
- **`Invalidate()` and not `Forget()`.** A moved verdict changes which rules match, which changes the
  interned `ComputedStyle`, which changes the reference `Apply` compares — so an element whose style
  genuinely moved rebuilds and one whose style did not is left alone. `Forget()` would rebuild every
  layout style in the document for a query that repainted one panel. (`Remedia` still forgets; it
  predates the interning being trusted and does not need to either.)
- **`Settle()`'s early return had to go.** It returned immediately when nothing was listening to
  `LayoutFinished`, which was correct while a handler was the only thing that could dirty a document
  after a layout. The container walk is a second such thing and no application registers for it, so a
  document with a container query and no handler would have entered its scopes, marked itself dirty
  and gone home — every verdict one frame late. Restoring that one `if` fails twelve of the fifteen
  tests.

**The bound is one extra settle pass per level of container nesting**, measured rather than argued:
`SettlingPasses` is 1 for a `StretchFit` container and 2 for one container nested in another. The
second pass measures the same box, interns the same scope, moves nothing and stops — which is what
the `StretchFit` claim below *means* operationally. `SettlePasses = 3` is therefore also a nesting
depth limit of three, and a fourth level of size-dependent nesting reports `Settled` false rather
than hanging.

⚠ **Verified by sabotage, eight of them, and the sixth is the one that changed the test file.** The
name test relaxed so a named query falls back to an unnamed container — the break the cascade half's
own five missed — fails 1, the live twin of the test that closes it. The content-box subtraction
dropped fails 1. A container made to answer its own query fails 3. `Settle`'s early return restored
fails 12. `moved |=` made short-circuiting, so the walk stops at the first element that moved, fails
4. The fast path widened by one fails 12. `container-name` never read fails 3. And **calling
`Recontain` once per `Update` instead of at the end of every `Arrange` failed exactly nothing** until
`A_container_inside_a_container_resolves_and_costs_a_pass_per_level` was written for it — every other
case in the file is one container deep, and one container deep cannot tell the two placements apart.

**And the eviction policy `ContainerScopes` deferred is now a ceiling.** Scopes are interned by value,
so a container dragged wider interns one chain per pixel per frame and nothing removes one. A
generation stamp cannot be swept without renumbering — ids are list indices and elements hold them —
so `UiDocument.ContainerScopeCeiling` rebuilds the table wholesale at 4096 chains, in the one order
`Reset` is documented as safe in: reset, re-assign, re-cascade. `ContainerScopesEntered` reports the
churn, and is nought on a settled frame.

⚠ **That branch had never been executed by anything**, which is the state this document's own § F
calls a finished thing nothing calls: reaching the ceiling takes about a minute of continuous
dragging, so every test and every document in the tree took the branch beside it.
`The_scope_table_is_rebuilt_at_the_ceiling_and_the_document_still_answers` drags a window a pixel a
frame until it fires, and measuring it corrected two things the prose had asserted. The frame costs
**two** extra settle passes and not one — one for the drag and one for the rebuild. And the
re-assign inside the branch is an **optimisation rather than a correctness requirement**: removing it
leaves the document correct, because the settle loop's next pass re-assigns every scope anyway, and
what it costs is a third pass. Which is why the assertion is the pass count and not the computed
width — a test asserting the width alone passes that sabotage.

#### What is owed, and what it costs

**1. The containment coercion — ~0.15 EM, in `Vixen.Ui.Layout`.** Force `SizingMode.StretchFit` on a
`container-type: inline-size` node that would otherwise consult its contents, or refuse it with a
diagnostic. Until then a container sized by its contents can oscillate — and `Settled` already reports
that, so the failure is visible rather than silent, which is why the interim is tolerable.

**2. ✅ The variants — and all three blockers turned out to be one decision and two small ones.**
`@sm:`…`@7xl:`, `@max-*`, `@min-[…]`, `@sm/main`, and stacked ranges are registered. What each of the
three reasons this section used to give was actually worth:

- ⚠ **`@` is `.vxml`'s interpolation marker, and the doubled sigil is that dialect's own escape
  rather than a new one.** `VxmlLexer.StepAttributeValue` sends `@` to `LexInterpolation`, so
  `class="@sm:p-4"` interpolates an expression called `sm` — true, and unchanged. What was wrong is
  the conclusion: `@@` is not a spelling this family invented, it is the escape
  `MarkupDiagnostics` has always told authors to write, and the **binder decodes it**, so the
  element at run time carries `@sm:p-4` and the class name is v4's exactly. The friction is one
  doubled sigil in `.vxml` and nothing at all in `.vcss`, C# or a `class` set from code. What that
  costs is one line in `CandidateScanner.Take`, which reads the *file* rather than the tree and so
  has to fold `@@` back before it generates a rule for a class no element will ever carry.
  `StylesheetTests.Written` folds it for the same reason, so the family is inside the
  misspelt-utility gate rather than outside it.
- ✅ **`@` is a candidate character now, and the at-keywords are still refused by grammar rather
  than by a list.** An at-keyword never carries a colon and a container variant always does — the
  colon is what makes it a variant — so `Take` refuses a run containing `@` and no `:`, and
  `@apply`, `@theme`, `@layer` and `@container` stay out of `Unrecognised` without anybody
  enumerating them. Sabotage-verified: reading the run's first character instead of its colon lets
  `@apply` through.
- ✅ **`--container-*` exists, with v4's own scale.** This was the real blocker and the only one that
  was a *decision*: `sm` is a 40 rem window and `@sm` is a 24 rem box, so `Screens` could never have
  answered a container variant — every rule would have been valid CSS with a threshold no dockable
  panel reaches. `ThemeTokens.Containers` is the second dictionary, `vixen.default.vcss` ships the
  thirteen steps, and `VariantCoverageTests.The_themes_container_sizes_have_no_untested_entry`
  enumerates them end to end against a sized box. ⚠ The sabotage that matters here is pointing
  `TryContainer` at `Screens`: it turns every positive row of that enumeration red, which is the
  precise defect the namespace exists to prevent.

✅ **The divergence this section used to state is closed** (#609). `@max-*` emitted `max-width`,
which is `<=`, where v4's `(width < 24rem)` is `<` — an off-by-one pixel on every `max-` threshold in
the engine, silent because it reads as an author mis-picking a breakpoint. Both evaluators read Media
Queries 4 § 2.4's range syntax now — `(width < 24rem)`, `(width >= 600px)`,
`(400px <= width < 600px)` and the reversed one-sided form — out of one shared `FeatureRange`, and
`@max-*` emits the exclusive form. ⚠ **The assertions that prove it are all *at* the threshold**:
every other width answers identically under both readings, which is why the existing bracketing rows
could not see it. Sabotage: reading `Below` as `AtMost` takes eight rows red across the two suites,
and putting `max-width` back under `@max-*` takes only the new variant test red. `@min-*` stays
inclusive, which is v4's reading too and the one `Screens` gives every breakpoint. ⚠ Discrete
features — `orientation`, `prefers-color-scheme`, `color-gamut` — refuse an operator the way
`color-gamut` already refused the `min-` prefix, because Media Queries 5 § 2.4.2 gives them no range
type and equality is not what the author wrote.

The consumption gate turned out **not** to be the obstacle it was assumed to be: `UtilityFamilies.Surface`
enumerates the family registry, and a pure variant emits no new property, so `@sm:` is invisible to it.
`UtilityFixture.Computed` grew the sized container ancestor the way it grew `Probe` for `group-*`,
modelled on the styling project's `CascadeFixture.Contain`, and the scope goes on the *parent* —
putting it on the element under test would make every one of those rows pass and would hide the
family's commonest real defect.

**3. The `@container` *marker* family is still owed**, and it is the part that faces the gate: it
would need a fifteenth probe scene, because `container-type` moves none of the four channels unless
the scene contains a query that reacts to it. Until it lands a query container is declared in
hand-written CSS or from `EditorTheme`, which is where every container in this tree is declared today.

**4. `cqw`/`cqi`/`cqb` units and `style()` queries** — not started, not costed, and not needed by the
editor's case.

**Size: 0.25 EM remaining**, from 0.4. The variants landed; the coercion, the marker family and the
units are what is left.

### D6. The variants had almost no end-to-end coverage, and that was worth more than A15 ✅ *closed — and it has now found a second bug*

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

✅ **The audit above is spent — every family in it now has a computed-value scene, stacked chains
included** (`sm:md:`, `dark:md:` and `md:hover:focus:` all resolve against the cascade, not against
their text). Re-verified on the post-per-surface-media tree by sabotage rather than by reading:
nine deliberate breaks, each caught, each by the test that names it — a fourteenth entry added to
`Variants.States` (the enumeration gate), `:nth-child(2n)` → `:nth-child(2n+1)`, `peer`'s `~` → `+`,
`group`'s and `[dir=…]`'s trailing descendant combinator, `.dark `'s prefix, `aria-`'s attribute
family, and the leading-digit escape reverted to a backslash — which the breakpoint gate catches, so
`2xl:` cannot silently die a second time.

⚠ **And the coverage found a second live bug, which is the argument for D6 restated.** `aria-expanded:`
emitted `[aria-expanded]` — presence — by sharing `data-`'s shorthand. An ARIA state is not a
presence flag: its false is **spelled out**, so a collapsed disclosure carries `aria-expanded="false"`
and is styled identically to an expanded one. The two assertions the file already had could not see it
— `"true"` matched and *absent* did not match under both the right implementation and the wrong one,
and the negative that discriminates is `"false"`, which nothing asserted. The shorthand emits
`[aria-<state>="true"]` now, matching WAI-ARIA 1.2 § 6.3 and Tailwind's eight built-ins; the arbitrary
form `aria-[sort=ascending]:` stays verbatim, since a non-boolean state has no shorthand in either
system. Fixed in its own commit rather than inside the coverage change. Nothing in the tree authored an
`aria-*:` utility yet, so the blast radius was future authors only — which is exactly how long an
untested variant stays harmless.

⚠ **One residual, recorded rather than closed.** `group-*` and `peer-*` are proved to compose over the
state table for one entry each rather than for all thirteen. That is deliberate: both are the same
`States.TryGetValue` on the suffix behind the same prefix template, so the other twenty-four rows would
exercise one line twenty-four times. The real limit is the *fixture's* — the ancestor it builds is a
root, so `group-first:` and friends have no well-defined sibling position to be first among, and
`peer-last:`/`peer-only:` are unsatisfiable by construction because a peer precedes the element. Worth
knowing before anyone reads the one-each coverage as an oversight.

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

✅ **Closed, and the sentence that used to end this paragraph is refuted.** It said *"Vixen's
`rounded` token scale and the editor's `--radius-*` names must be re-pegged to v4's, or every
`rounded-sm` in the tree means something one step off what a Tailwind user expects"*, and it added
that this is the one item in the plan that moves pixels in existing markup. **Measured, and nothing
had to move.**

- ⚠ **The scales were pegged already, by C3 rather than by C2.** Transcribing v4.3.3's own `@theme`
  whole brought its *names* with its values, so `--radius-xs` is 2px, `--shadow-xs` is the one-pixel
  shadow v3 called `shadow-sm`, and the `2xs` and `4xl` ends v3 never had are both present.
  `UtilityFamilyTests.The_shipped_radius_and_shadow_scales_are_v4s_and_not_v3s` asserts the ends
  rather than the middle, because the ends are what tell the two vintages apart.
- ⚠ **The editor's `--radius-*` names are a different namespace, not a competing peg.**
  `--radius-panel`, `--radius-control` and `--radius-row` are *semantic*; they collide with none of
  v4's steps and clear none of them, and `vixen.ui.vcss` says so in its own comment. There was
  nothing to re-peg and no picture to re-record.
- ⚠ **`ring` is already 1px** (v3's three-pixel ring is `ring-3`), `outline-none` and
  `outline-hidden` are both registered with v4's meanings, and `bg-gradient-to-*` is kept as the
  alias v4 itself keeps in `compat/legacy-utilities.ts`.
- ⚠ **The one real gap was not a shift at all: `blur-*` had no named scale.** It resolved through
  `--spacing`, so `blur-8` worked and `blur-md` — the only shape v4 has for this family, and the one
  `UiGeometry`'s own remarks call canonical beside `rounded-2xl` — produced no rule. The `--blur-*`
  namespace ships now, `blur-*` and `backdrop-blur-*` both read it, and the arithmetic form is kept
  beside it because nothing v4 has can collide with a number. Additive, so still no picture moves.

⚠ **`ring-*` is not a rename but a change of meaning — and the correction below is a correction to
this document.** ✅ **Closed by A6.** What this paragraph used to say was that Vixen emitted
`outline-color`, "which is v3's reading". That was wrong, and wrong in the direction that mattered:
**no version of Tailwind has ever emitted `outline-color` for `ring-*`.** v3 is where the ring was
introduced *as a box-shadow*, and v3's `ring-<color>` set `--tw-ring-color`. So `outline-color` was
this engine's own invention, and the `InertProperties.txt` line filed under it could never have come
due — a reader for it would have closed the debt and changed nothing anybody could see. That is the
same failure as `grid-cols-3`'s `grid-template-columns: 3` and the transform families' `--scale`, and
it is the fourth instance: **a property nothing emits and a property nothing reads look identical
from inside the gate.** Recording the debt against a *plausible-sounding* property is what hides it.

`ring-*` emits v4's shape now — `box-shadow: 0 0 0 <width> <color>`, a width fragment and a colour
fragment with both classes assembling, the same arrangement as the two translations. **It needed no
new draw path**, which is the other thing worth writing down: an outline is drawn outside the box and
is invisible to layout, and `DrawListBuilder.EmitShadow` has done exactly that since it learned about
spread — the spread grows the command's rectangle and every corner radius, so a ring is a rounded box
painted behind the background. What it *did* need was `currentcolor`, which `NamedColors` did not
have and which is not a name but a reference to the computed `color`; `EmitShadow` resolves it
through `ForegroundOf` per CSS Color 4 § 6.2. Without it the fragment's initial would have had to be
`transparent` — making a bare `ring-2` resolve, cascade and paint nothing.

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
consuming project**, and errors if there is more than one — *"One project is one palette."* So
`Vixen.Editor.Profiler`, `Vixen.Editor.Debugger` and `Vixen.Editor.AssetEditors` produce no utility
sheet at all, and a panel ported to VXML in one of them has to fall back to tag-based theme rules.
That workaround has already been taken once.

⚠ **Correction, measured against the tree rather than reasoned about.** This section claimed the
shipped default had made the defect *worse* — that a project with no token file now generates v4's
palette against its own sources. **It does not.** The generation target's condition is
`'@(VixenUiTokens)' != '' OR '@(VixenStyleBase)' != ''`, so a project naming no token source does not
run the step at all: no sheet, no accessor, nothing. `ThemeTokens.CreateDefault()` is reached only
when StyleGen *is* launched with no `@theme` to read, which MSBuild never did. A throwaway project
importing the `.targets` with no `vixen.ui.vcss` was built to confirm it and produced no artefacts.
The failure was therefore the original one — absent and silent — and not a plausible-looking wrong
palette.

⚠ **And what the silence actually cost is not what it looks like either.** Exactly one project in the
tree imported the `.targets`. The two assemblies with ported VXML use almost no utilities at all —
`hidden` is the only one, in four `.vxml` attributes and two `AddClass` calls — and it *renders*,
because `Vixen.Ui.Controls.Advanced`'s `AdvancedTheme` hand-writes `.hidden { display: none; }` as an
unlayered rule. `AssetEditorTheme` says so in a comment. So no panel was visibly broken; the cost was
paid in advance, as a utility class name re-implemented by hand in a component sheet. ⚠ Worth noting
separately: the editor's own generated sheet is **25 rules**, and most of them — `.block`, `.inline`,
`.static`, `.ring`, `.truncate` — are bare English words the over-inclusive scanner lifted out of
prose and `overflow: hidden` declarations rather than deliberate usages. `.hidden` is among them. A
non-empty sheet is therefore very weak evidence that a project is wired up correctly, which is why
C4's canary is a *token-dependent* class and not a rule count.

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

✅ **C4 landed.** The missing sentence is a `VixenStyleTokens` item — a path to another project's
`@theme` sheet, joining `VixenUiTokens` on the theme option and emitted *before* it so a project can
extend the shared tokens rather than only inherit them. The two items stay distinct on purpose: a
theme found in the project is that project's own and still bound by "one project is one palette", a
theme named there belongs to another one, and a named path that stops resolving is an `Error` rather
than a silent skip. `Editor/Vixen.Editor.Ui/build/Vixen.Editor.Ui.Styling.targets` packages the whole
thing as one `Import` — the tokens, the step, and the build-order reference to the tool — so joining
the editor's theme is one line in a `.csproj`. The three assemblies this section named —
`Vixen.Editor.Profiler`, `Vixen.Editor.AssetEditors` and `Vixen.Editor.Debugger` — are wired, and
each loads its own sheet from the `…Theme.Install(UiDocument)` it already had. ⚠ **Nine or so others
are not**, deliberately: joining is one line in a `.csproj` and one in
`SharedThemeTests.Participants`, and that list is the ledger. There is no reflection over "every
assembly with a theme", because opting in is a decision — a project with a design of its own should
declare its own tokens rather than be swept into the editor's.

⚠ **Two incidental holes were closed with it.** The generation target's `Inputs` listed only the
files the step *reads*, so a change to a safelist, a namespace or a token source — arguments rather
than files — left a stale sheet looking up to date; `VixenStyleBuildLogic` now puts the declaring
`.targets` and the project file in the input set. And a project whose token source is removed no
longer merely regenerates an empty sheet: the accessor stops being produced and the assembly *fails
to compile*, which is a louder failure than the test.

**Guarding it is a cross-assembly test, because nothing inside one project can hold the claim.**
`SharedThemeTests` in `Vixen.Editor.App.Tests` — the only suite that sees every participant — asserts
for each that `bg-surface` resolves to `var(--surface)` and that `bg-blue-500` resolves to nothing.
The pair is deliberate: the first absent means the shared tokens never arrived, the second present
means Tailwind's arrived instead, and the editor's `@theme` empties the colour namespace precisely so
those two questions have different answers. Sabotage-verified both ways — cutting the token source
fails the build, pointing it at a themeless sheet builds silently and fails four assertions.

✅ **The ladder landed, and it is the correction to the sentence this whole part is written around.**
Every paragraph above says "the layer is what makes a component rule beat a utility" — and the
mechanism was real, and it was pointed the wrong way. `@layer utilities` was the only layer in the
tree; **unlayered beats every layer**, so a hand-written `button { padding: … }` beat `p-2` not
because a layer said so but because nothing else had joined the ladder. A lone utility layer is
strictly *worse* than no layers: the utility would have won that fight on specificity. The ten theme
sheets now open with `@layer base, components, utilities;` and put their rules in `components` —
`base` holds only `ControlTheme`'s reset and its nine default tokens, so a product's palette beats the
engine's by layer rather than by install order. **Editor chrome deliberately shares `components` with
the control looks** rather than getting a tier of its own: a layer is an unconditional claim, chrome
already beats controls on source order, and promoting it would newly overrule every deliberately
specific control rule with a bare tag selector — a class of movers nobody asked for. Three names, and
they are Tailwind's minus `theme`, because Vixen's `@theme` is a build-time construct and the root
rule the generator writes is deliberately unlayered-and-first.

⚠ **The statement is emitted by `UtilityGenerator` too, and that is not belt-and-braces.** Layer
*order* belongs to whoever names a layer first and is never revised, so a generated sheet reaching a
document before any theme sheet would make `utilities` layer zero and every later `@layer components`
beat it — the defect back, restored by load order alone and invisible. `SharedThemeTests` now asserts
the statement is present on every participant's sheet as well as that no participant opens a block
layer other than `utilities`. `Samples/14-Mmo/Mmo.Ui/Theme/hud.vcss` joined the ladder as the worked
example for a game's own sheet: an unlayered *author* sheet still beats the engine outright on origin,
which is right, but it also beats its own utilities, which is this same defect one origin up where
nothing the engine does can reach it. **Zero baselines moved** — the four editor-chrome renderings are
byte-identical before and after — and the one test that flipped is
`StylesheetTests.The_editors_own_rules_beat_the_utility_layer`, inverted and renamed, with its
sabotage twin re-pointed at the *chrome* sheet because unlayering the utility sheet no longer changes
the answer.

⚠ **Under v4 this gets simpler, not harder,** which is the argument for doing Part 2 § D1 first: if
tokens are an `@theme` block in a `.vcss`, then "share the tokens" is `@import` — a mechanism the
style engine already supports — and the MSBuild item is a path to a stylesheet rather than a new
concept.

⚠ **The guide page master added — [`docs/guide/editor/utility-styles.md`](../guide/editor/utility-styles.md)
§ Examples — documents the workaround rather than the shape.** *"Turning the step on in another project
is two lines and a file"*, and the file is a second `vixen.ui.vcss`: a second palette, which is the
failure the token model exists to prevent. It also still carries *"`overflow-auto` is in neither
column"*, which F3 has since made untrue. Both should be revised when C4 lands.

✅ **§ Examples revised with C4.** It now leads with the one-line `Import` and the `…Theme.Install`
that loads the resulting sheet, and says outright not to give a second editor assembly a
`vixen.ui.vcss`; the own-tokens example is kept and re-scoped to the case it is actually for, a game
or a plugin with a design of its own. ⚠ **The `overflow-auto` sentence was left alone**: F3 is not on
this base — no `auto` handling appears in the layout's overflow path here — so correcting it would
have been a change made on a claim rather than on the code. It should be revised when F3 lands.

**And it wants a diagnostic either way.** A class name that parses as a utility, in a project with no
token source, should be a build warning naming the project. The generator already writes an
`unrecognised.txt`; nobody reads it because in the normal case it is noise. In the *no tokens at all*
case it is the whole answer.

⚠ **C4 declined to build that warning, and the reason is worth recording.** To warn about utility
candidates in a project with no token source, the step has to *run* in that project — which means
launching a process per build for every project in the tree that has never heard of utilities, to
tell almost all of them nothing. The two failures it was aimed at are both covered more cheaply and
more precisely: a project that *was* wired and is no longer now fails to compile, and a project that
is wired to the *wrong* tokens fails `SharedThemeTests`. What remains uncovered is a project that
should have opted in and never did — and that is a cross-assembly question a per-project build step
could not have answered anyway. Adding an assembly to `SharedThemeTests.Participants` is the place
that decision is written down.

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

⚠ **Re-verified against the files, and it is a whole-stack oracle rather than a line-breaking one.**
`Roboto.csv` at Parley's `main` is 1 024 rows under `# font_family: Roboto` / `# chrome_version:
149.0.0.0`, with exactly the three columns above — and **the text is not in it**. So consuming a row
means agreeing with Chrome about three separate things: the ChaCha8 stream that turns a seed into a
string, the font, and the shaping that turns that string into an advance in sub-pixels. Neither
Roboto nor Arimo is committed to this repository (`git ls-files` over `*.ttf`/`*.otf` finds twenty
harness faces and neither of these), so **taking this source is a font decision and a shaping-parity
claim before it is a break-position oracle** — and a disagreement in any of the three reddens all
1 024 rows for a reason that is nothing to do with UAX #14. That is the cost to weigh against
re-recording, not the transcription.

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

**C · Families.** Every root in Part 0's table.

⚠ **C depends on A and B, and inverting that is how the present state came about.** `grid-cols-3`
exists as a family and emits `grid-template-columns` because a family is a line of a table and the
grid algorithm is a subsystem — so the cheap half was done and the class name has been available, and
inert, ever since. The same is true of `blur-*`, `fill-*`, `ring-*` and
`select-none`. **Eighteen of the 90 properties the utilities emit reach no consumer after ExCSS
expansion** (`blur-*` has since left that list), and each one is a class somebody can write today that does nothing:

```
--blur  --rotate  --scale  --translate-x  --translate-y
border-inline-end-color  border-inline-start-color
fill  stroke  grid-column  grid-template-columns
outline-color  user-select  vertical-align
transition-property  transition-duration  transition-timing-function
```

⚠ **That list is the survey's, kept as written.** Ten of the eighteen have since been retired —
`grid-column`, `grid-template-columns`, `vertical-align`, the three `transition-*`, the two
translations, `--blur` and now `outline-color`, which came off when the outline got a reader of its
own rather than by the route § D5 predicted: the debt was recorded against `ring-*`, `ring-*` stopped
emitting it, and the property then arrived for real as one of `outline`'s four longhands —
and two more changed their names rather than their state, because `--scale` and
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
| A3 ✅ | **Done, and it closed three of the four families it was filed for.** `border-*` reads all six keywords, `divide-<style>` all five, `outline-*` all five, and `decoration-*` four of five. ⚠ **Nothing read the four style longhands at all** — `border-style` resolved into them and moved no channel in any scene, which is why the four families were `partial` together. ⚠ **An absent style is `solid` here and `none` in CSS, deliberately**: a browser paints nothing for `border-width: 2px` alone, this engine has always painted, and v4 emits `outline-style: solid` beside every width class for exactly the reason we must not — CSS's reading would have blanked every border in the repository at once. ⚠ **`border-none` was a lie until this landed**: it emits only `border-style: none`, which nothing read, so it drew the ring anyway. **Three mechanisms and the third is a change of machinery.** `solid` is the command that was already there, byte for byte. `double` is two `Border` commands a third as thick with the middle third left out — still a distance field, so still exact at any radius. `dashed` and `dotted` cannot be either: a ring's fragment shader knows how far a pixel is from the outline and not how far *along* it, and a dash is an arc length — so a broken border is a `PathStroke` of the ring's centre line, which the tessellator, the solid pipeline and the software rasteriser already draw. No command kind, no shader, no second executor, which is A19's insight again. ⚠ **A divider is never the uniform case**, so the per-edge band path had to answer the broken styles too — `divide-*` writes a width on one edge and zero on three. ⚠ **A broken band used to drop the corner radii the solid one keeps**, on the reasoning that a mark is straight and an arc is not; the marks are cut out of the band rather than walked along it, so the first and last mark simply carry the two corners at their end of the run and the band path now rounds exactly where the ring does. ⚠ **`groove`, `ridge`, `inset` and `outset` are refused, not aliased**: all four are two-tone and the border record carries one colour; they draw solid, which is CSS's own fallback for a style the UA cannot render. `decoration-wavy` is refused for a reason the dash pattern does not touch — a wave is a stroked path where every other decoration is a rectangle, and CSS states neither its amplitude nor its period | `DrawListBuilder`, `Dashes`, `Rings` | done | 0.15 |
| A4 🟢 | `order` | `LayoutStyleBuilder` + flex line ordering | **#22** | 0.1 |
| A5 ✅ | `overflow-x`/`overflow-y`, and `auto` in the layout keyword table | `OverflowReader`, `LayoutStyleBuilder` | done | — |
| A6 🟡 | **Three of the four landed, in three different ways, and the fourth is refused.** `fill`/`stroke` were the honest case: the emission was already v4's and the renderer had had the channel all along — `IconPath` carries a fill paint and a stroke paint and `IconPaintKind.Foreground` is SVG's `currentColor` marker — so it was two `ColorOf` reads in `Icon.Resolve`, plus the two names in `InheritedProperties` without which the class only works written on the icon itself. `outline` is **gone rather than done**: `ring-*` emitted `outline-color`, a property no Tailwind has ever emitted for it (see § D5, corrected), and under v4's box-shadow shape the existing spread path paints it with no new rendering. That needed `currentcolor` in `EmitShadow`. ⚠ **`user-select` stays inert and is not waiting for a reader.** A selection model exists — `TextField` has `CaretIndex`/`SelectionAnchor`/`SelectWord` and drag-to-select, `CodeEditor` has its own — but both are per-control: each captures the pointer for its own drag and hit-tests only its own `TextLayout`. The *document-wide* selection `user-select` governs does not exist, so `select-none` on a button, which is what the class is for, has nothing to suppress. Teaching `TextField` to honour it would expire the allow-list line and leave that promise unkept. ⚠ `overflow-clip` is closed, and **refused in the shape this row asked for**: `clip` maps onto `Overflow.Hidden` rather than adding a member, because CSS separates the two by a scroll container and by programmatic scrolling and this engine grants `hidden` neither — see F3. The defect it was hiding was the layout half, not the clip. ⚠ And the parity gate could not see `fill`/`stroke` until `UtilityConsumptionProbe` could build an `Icon` — `grid-cols-3`'s missing grid again | `Icon`, `DrawListBuilder`, `InheritedProperties` | **#24** | 0.05 of 0.25 |
| A7 🟢 | **Transforms — the three independent properties are all read now.** `translate-x-*` and `translate-y-*` are composed (a `--tw-*` fragment per axis, one `translate` between them, both classes assemblers) and read by `TranslationReader` in `UiDocument.Accumulate` — the same sum that already carried `OffsetX`, so the draw list, the hit test and arrow navigation all read one translated position and *cannot* disagree. Lengths and percentages, percentages against the element's own border box per Transforms 1 §8; not layout, so siblings do not move; the subtree comes along; a translated clip moves with the box and is still a rectangle. Interpolatable for free, because `StyleValue` already lerps a two-part list. ⚠ **`scale` and `rotate` are read now, and the refusal that used to be here is the most useful thing in this row.** It said: a `DrawCommand` is an axis-aligned rectangle and the clip stack intersects rectangles, so a rotated box and a rotated clip cannot be represented; scale can scale the box and not the picture, because glyph advances are shaped at `run.Size` during *layout*. Every clause is still true. Its last sentence — "both need the offscreen compositor `DrawListBuilder`'s opacity remark already owes" — is the one that mattered, and that compositor landed with `opacity` and was extended four more times before anybody re-read this. ⚠ **A refusal whose premise names another feature has an expiry date, and until #288 nothing in the repository checked it**: `InertProperties.txt`'s expiry only fires once someone has already written the reader. `RefusalExpiryTests` is the half that fires before that — see Part 0 § *The `note` column carries the expiry*. **What it took:** a transform is the fifth thing to open a group, `TransformReader` composes `rotate`, `scale` and `transform-origin` into one `UiTransform` in `UiDocument.Accumulate`, and the matrix is spent on the composite quad's four vertex positions — so no `DrawCommand` was rotated, no clip stopped being a rectangle, and no glyph was re-shaped. It cost **no shader and no vertex format**: both executors already interpolate a quad's texture coordinate linearly, and an affine map is exactly the class for which that is exact. The hit test maps the pointer through the inverse at the top of the walk, one line, and nested transforms compose because the recursion does. ⚠ **And the `<transform-function>` parser this row records as owed has been written the whole time.** `TransformReader.Functions` reads `matrix`, `translate`, `translateX/Y`, `scale`, `scaleX/Y`, `rotate`, `rotateZ`, `skew` and `skewX/Y`, composed right to left, refusing the whole list if any function in it is unreadable, and pinned against pixels in `TransformTests` — including a `skewX(45deg)` probe. So `rotate-z-*` was never a parser away: it is registered now as an angle fragment assembled into one `transform`, and it measures `works`. ⚠ **The row's own expiry clause could not say so**, because it named `StyleValueKind.Function` and the parser was built over the declaration's *text* instead — a refusal can be satisfied without its named symbol ever arriving, which is one level worse than #288's finding and is the first time it has happened here. ⚠ **The three `skew` roots closed the same way on 2026-09-05 (#227), and they were worse off than `rotate-z-*` was**: `rotate-z-*` at least carried a refusal naming a condition, while `skew-*`, `skew-x-*` and `skew-y-*` sat `absent` with an **empty** note — nothing to expire, nothing for `RefusalExpiryTests` to fire on, and the only record that they were owed was this row's own sizing. Each is an angle fragment (`--tw-skew-x`, `--tw-skew-y`) with `skewX(…)`/`skewY(…)` in `UtilityComposition.Transform`; the bare `skew-*` writes **both** fragments rather than emitting CSS's two-argument `skew(a, b)`, because that is v4's own reading and because a `skew(…)` slot and a `skewY(…)` slot are different slots — the shorthand would otherwise take a whole axis with it when a `skew-y-*` was written beside it. `transform-none` is registered beside them and reads. ⛔ **`transform-cpu` and `transform-gpu` are refused rather than owed**: they are compositing hints with nothing to hint at, `will-change-*`'s argument one property over — and emitting what v4 emits would be *worse* than absent, since its `transform-gpu` prepends `translateZ(0)`, which `TransformReader` cannot read and which makes it refuse the list **whole**, silently unrotating anything beside it. `transform-flat`/`transform-3d` (`transform-style`) and the five `transform-box` keywords are different properties and are refused with the 3D family (#228). **Owed:** the 3D family, on which see below; and `backdrop-filter` on a transformed group, refused in `UiGeometryBuilder.Layer` rather than approximated (#229).

⚠ **The 3D family: decided 2026-09-04, and the decision is to build it.** The refusal was that `UiTransform` is a 2D affine and a perspective divide is projective — see `UiTransform.Apply`, whose whole argument is that the composite quad's texture coordinates are interpolated linearly, which is exact for an affine map and an approximation for a projective one. That argument is correct and it is not a reason to stop, because *a planar element under a 3D transform and a perspective is exactly a 2D homography*: the four corners project to four points with four `w`s, and a rasteriser that carries a `w` divides by it for nothing. So the type grows from six floats to a 3×3 and the shape of the change is known. ⚠ **The matrix landed on 2026-09-04 (#547).** `UiTransform` is a 3×3 with a `Project` that returns the homogeneous point, and the whole of the third column is still read by nothing outside the type — `UiVertex` is position, texture, colour and shape with nowhere to put a `w`, so `UiGeometryBuilder.Quad` calls `Apply` and drops it. All four ledger rows that named `UiTransform.M33` as their expiry came due the same day and not one of them closed, which is the distinction this table exists to keep: a premise expiring is not a refusal ending. They name `UiVertex.W` (#548) and each other now. ⚠ **And the parser is a shape change and not a table of nine more function names, which is what #550 reads as all that is left.** Checked 2026-09-05: reducing a 4×4 to a `UiTransform` keeps rows `x`,`y`,`w` against columns `x`,`y`,`1` and throws the `z` row and the `z` column away, so `R(A·B) = R(A)·R(B)` holds only where `A` has no `z` column or `B` no `z` row — and a `perspective()` is nothing *but* a `z` column while a `rotateX()` is nothing but a `z` row, which is the one pair a card flip is written from. `perspective(200px) rotateX(60deg)` sends a point 100 points below the origin to `y = 88.19` composed in 4×4 and reduced once, and to `y = 50` with `w = 1` reduced per function and composed — which is `rotateX` on its own, because `R(perspective)` **is** the identity on a plane at `z = 0`. So `TransformReader` has to compose the whole list in four dimensions and reduce once at the end; a `Function` that returns a `UiTransform` each is the plausible arrangement and it makes every `perspective()` silently do nothing. ⚠ **The bill is not the matrix.** It is perspective-correct barycentrics in `SoftwareUiRasterizer`, which `UiCompositingTests` compares against the device exactly, so every reference image in that suite moves; ⚠ **and #548 cannot be landed in two instalments at the vertex field**, checked 2026-09-05: `UiVertex.W` is what `perspective-*` expires *on*, and `RefusalExpiryTests.Every_clause_is_anchored_on_something_that_is_really_there` wants the member absent while the row is refused, so a vertex format that arrives ahead of the two executors turns that suite red on a `W` nothing reads. Either both executors move together or the row is re-anchored in the same change. ⚠ Nor can the two be held together by `CheckShaders`: that suite's executor is hand-written GLSL in `Platform/Vixen.Graphics.Golden.Tests/Shaders/` with its `.spv` committed beside it and no Raven in it, so the gate never recompiles a line of it and a divergence is a golden comparison or nothing — though `UiRavenAgreementTests` does render one frame through each and compare them exactly on a device, so the blind spot is narrower than "golden or nothing" and covers the box path already. ⚠ **And the device half is smaller than both this row and #548 say: it touches no fragment stage.** Checked 2026-09-05: `UiImage`, `UiColour` and `UiMask` read the `uv`, `colour` and `shape` *streams* and no vertex attribute, and `StageInterface.MustBeFlat` decorates a varying `flat` only for an integer type — so a `float` stream already takes SPIR-V's default interpolation, which is the perspective-correct one. Hand the vertex stage `float4(xy · w, 0, w)` and the hardware divides for nothing, with the seven committed `.frag.spv` unchanged. What moves is `UiVertex`'s attribute list and its one returned line, `UiVertex.vert.spv` and its reflection, `UiRenderer`'s stride and fifth attribute, `Vixen.Ui.Rendering.UiVertex`, and the hand-written GLSL twin `Shaders/ui.vert` with its `.spv` and its `modules.sha256` row — that fifth place being the one an audit that names only "the three compositing stages" would leave behind; <s>a projective inverse in the hit test that has to answer for points behind the eye</s> — **landed (#549, 2026-09-05)**: `UiDocument.HitTest` reads `UiTransform.Project` and refuses a non-positive `w` before the divide. ⚠ The failure it closes is not that the inverse blows up — it does not. A point on the half of the plane behind the eye inverts to a *finite* coordinate inside the border box, so the card was clickable in a band of the screen where nothing is drawn, at numbers nothing in the picture disputed; sabotage-tested against the old `Apply`, which returns the card for a pixel 133 points below it. The affine path is bit-identical, because on an affine that `w` is exactly `1f`; and `backface-visibility`, which needs the sign the flattening throws away. `transform-style: preserve-3d` is *not* in this decision and is the one part that is not a matrix change at all — children in a shared 3D space cannot be flattened per element, so it is a rendering model with its own sorting. Tracked as #228's follow-on issues | `TransformReader`, `UiTransform`, `UiDocument`, `UiGeometryBuilder` | **#23** | 0.35 |
| A8 🟢 | **`filter: blur()` is done and the rest of A8 is not.** `blur-*` emits a `--tw-blur` fragment assembled into a real `filter`, closing the `--blur` placeholder the same way the translations closed theirs; `DrawListBuilder` opens a composited group for it — *and never collapses one*, since the single-command peephole is an identity for opacity and nonsense for a filter — `UiGeometryBuilder` outsets the group's bounds by three sigma before the clip narrows them, and both executors convolve the finished surface with the same kernel from `UiLayer.KernelRadius`. On the device that is two extra passes and **one** shared scratch target for the whole frame, not one per blurred group. Measured at 1920×1080: the twelve composited groups an editor frame already had cost **1.10 ms**, and a blurred group adds 0.17 ms at σ=4 — ⚠ **the surfaces are the expensive part of this design and the blur is not**, which is the finding worth carrying into any future work here. ⚠ **Owed:** the rest of `filter` (`brightness`, `contrast`, `saturate`, `grayscale`, …), all of which are *absent* roots rather than inert ones and each of which is a constant, an initial and a slot in `UtilityComposition.Filter`; <s>`backdrop-filter`, which needs the frame *under* a group and the compositor does not keep it</s> — **landed**: the frame under a group is not kept, it is *re-rendered*, so `UiRenderer.Submit` took a `stop`, `Compose`'s walk became post-order and the host hands over what it had already painted; <s>and `Vixen.Editor.Host`, which supplies no blur stage because Raven's `[PushConstant]` cannot place a block at a byte offset</s> — **landed (#230)**, and ⚠ **both halves of that sentence were wrong**: the attribute *can* place a block at 16, by declaring a leading `reserved: float4` that is the vertex stage's projection, which `Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn` had been doing all along; and the editor's real blocker was a **duplicate `Ui.rvn`** carrying five of that file's eight shaders, hand-rolled into a five-module table beside a `UiShaderLibrary` the project already referenced. The copy is deleted, `EditorHost` calls `UiShaderLibrary.Load`, and `EditorUiCompositingDeviceTests` renders a blurred, a filtered and a masked group through the editor's table against the same table with the three stages cleared — see `Vixen.Ui.Renderer/README.md` | `DrawListBuilder`, `UiGeometryBuilder`, `UiRenderer`, `SoftwareUiRasterizer` | **#28** | 0.35 of 0.75 |
| A9 ✅ | `color-mix()` in `StyleValueParser` — four interpolation spaces (`srgb`, `srgb-linear`, `oklab`, `oklch`) with the four hue methods, premultiplied alpha, and the CSS Values 5 percentage normalisation. `UtilityFamilies.TryColor` emits one for `/opacity`, which retires **#12**'s colour half: an opacity on a token that is not a hex triple used to be dropped silently, and every token in the editor's palette is a `var()`. **Owed:** the interim out-of-gamut behaviour is *carry it unclamped* — see § D4 | `Vixen.Ui.Styling`, `ColorFunctions` | done | — |
| A10 ✅ | `oklch()`/`oklab()` colour syntax, both notations, `none`, and every angle unit | `Vixen.Ui.Styling` | done | — |
| A11 🟢 | Backgrounds. **`linear-gradient()`, `radial-gradient()` and `conic-gradient()` all paint**: `background-image` is parsed into `BoxStyle`, all eight direction keywords with CSS's corner rule, all four angle units, both colour notations, two or three stops, arbitrary stop positions inside or outside the box, `in srgb` / `in srgb-linear` / `in oklab`, and it layers over `background-color` as CSS does. `bg-radial` and `bg-conic` are assemblers now, and every assembler emits `in oklab` for v4 parity. Everything else is *refused loudly* rather than approximated — see `GradientRefusal`. `UiShape` grew 80 → 112 bytes; `UiShapeLayoutTests` and `CheckShaders` are what keep its four files in step. **Owed:** an explicit radial/conic centre, `bg-conic-<angle>` (the parser and shader do `from <angle>`; the *utility* needs a numeric family), `background-position`/`-size`/`-repeat`, and gradient text — see [what a third stop cost](#what-a-third-stop-cost) | `DrawListBuilder`, `BackgroundGradient`, `UiShape`, `Ui.rvn` | **#43** | 0.15 |
| A12 🟡 | Pseudo-elements materialised — `::before`/`::after` with `content`. Not started, and four passes now agree on why: the smallest slice anything calls is a second style slot for a box that is not one of the container's children, across `StyleRuleSet`, `UiDocument`, `Vixen.Ui.Layout` and the draw list, and every smaller one is a named failure mode — registering the seven variants first emits selectors `SelectorCompiler` refuses, which is F6's own defect. ⚠ **The lead the third pass left — that `::placeholder` and `::selection` name something that already exists and could land without the generated box — is half right, and the two halves are different problems.** `::placeholder` IS an element: `TextField` builds it as `Part("field-placeholder")`, a direct child with its own tag that `ControlTheme.vcss` already styles. But it cannot be a row in `Variants.States`, because `not-`, `has-`, `group-` and `peer-` all compose over that table — a descendant-shaped suffix makes `not-placeholder:` mean "an element that is not a placeholder" and `group-placeholder:` mean nothing at all, each of them valid CSS meaning something else, which is F6's failure mode one level up. `::selection` is not a box at all: `TextField` paints the highlight from `--selection-color` read off its own style, so `selection:bg-*` would have to rewrite the utility's PROPERTY, and `VariantEffect` is three strings that can only append to a selector, prepend to it, or wrap it in an at-rule. The missing piece there is a fourth variant shape rather than a generated box | `StyleRuleSet`, `UiDocument` | — | 0.5 |
| A13 🟡 | **Five structural, then three form states, and the fourteen left are not one item.** `empty`, `not-*`, `nth-*`, `nth-last-*` and the whole `*-of-type` family are registered, each with a positive and a negative computed-value scene. ⚠ **The item's own claim that "none needs a matcher change" was wrong, and wrong about exactly the family that looks most like a table entry**: an of-type index is a position among the siblings *sharing a tag*, which nothing stored, so `PositionTest` grew five members and `StyleTree` learned to count them. The trap it hides behind is that `:nth-of-type(n)` and `:nth-child(n)` pick the same element out of any run of one tag — so a fixture of five `li` proves nothing, and the scenes mix `p` and `div` for that reason. ⚠ **`not-*` is a bare-suffix negation only**: `not-sm:` is an at-rule in v4 and `not-group-hover:` an ancestor, so both are *not variants* rather than variants meaning something else. **Then `read-only`, `placeholder-shown` and `indeterminate`** — `ElementState` grew three bits, `SelectorCompiler` three arms plus `:read-write` as a negation of the first (`:enabled`'s arrangement), and `TextField`, `CheckBox` and `ProgressBar` write them. ⚠ **A table entry is worth nothing without a writer, which is what the sizing missed**: `:read-only` compiled against a bit no control sets resolves, indexes and matches nothing, and would pass every row in `VariantCoverageTests` — so `ElementStateBitTests` is the writer's side and the end-to-end one. ⚠ And two beliefs were refuted on the way: `TextField.ReadOnly` argued a class rather than a state because `ElementState` holds *transient* conditions, but `Disabled` and `Checked` are modes on the same terms and have been in the enum since it was written; and `:placeholder-shown` is **not** the `empty` class renamed — it needs a placeholder *and* no value, where the class is set on either. **Owed: five, and every one of them is a refusal rather than work.** ⚠ This sentence read "fourteen, and eleven of them are refusals" and named the eight form-validity keys as having nothing to be true of — `TextField` grew a validation model and all eight are registered, which happened without anyone coming back to this row. What is left: there is no navigation model, so `visited` and `target` are refused; `autofill` needs a credential store, `default` needs a form, `inert` needs a subtree flag nothing carries. ⚠ **`open` has landed, and its refusal expired rather than being satisfied**: the parser is still what it was — ExCSS 4.3.2 hands `:open` back as an `UnknownSelector`, re-measured — but `:user-valid` had the identical problem and was not answered by a parser upgrade, so `:open` rides `SelectorCompiler.TryRewrite` too. `ElementState.Open`, written by `Expander` and `SelectBase`, and read from the overlay's own notification rather than from `Open()`/`CloseList()` because a light dismiss closes the list without either being called. ⚠ It is also the first name on that scan that is a *prefix* of others CSS spells, so its arm checks what follows — without that, `expander:opened` compiles to a marker beside a stray type selector and matches every open expander. ⚠ **The five that are left were re-read at HEAD on 2026-09-06 and all five still hold — and each now declares the condition that reverses it, because a fourth hand audit is what this row has needed twice.** `Vixen.Ui.Controls/Forms.cs` is `LabeledContent` and not a form, and its `Navigation.cs` is a breadcrumb and a pager over `ButtonBase` with no URL, no history and no fragment behind them. `RefusalExpiryTests` now fails the run in which `ElementState.Visited`, `ElementState.Target`, `TextField.Autofilled`, `Button.IsDefault` or `UiElement.Inert` arrives. ⚠ The first two name the *bit* rather than the model, which is a limit of the clause grammar rather than a choice: `expires-on` needs its type half to resolve today, so a refusal waiting on a concept that has no type yet has nothing else to hang on | `Variants`, `ElementState`, `TextField`, `Toggles`, `Range`, `Disclosure`, `Selects` | — | 0.24 of 0.3 |
| A14 ✅ | **Done, as sixteen keys rather than thirteen conditions, and two of them were already answerable.** `portrait` and `landscape` are `(orientation: …)`, which `MediaQuery` has always derived from the surface's own width and height — a table entry and no condition. The rest brought five axes onto a new `MediaPreferences` value: reduced motion, contrast (⚠ four values, because `custom` is neither more nor less and collapsing it would apply every high-contrast rule to a palette the user chose), forced colours, inverted colours, and the two pointer families. ⚠ **`PointerCapability` needed a fourth member for a reason that is this repository's commonest bug in a new disguise**: CSS's `pointer: none` is the empty capability set, and the empty set is also what a field nobody assigned holds — so a zero meaning "no pointing device" would make `pointer-none:` the rule that always applies under `default(MediaContext)`. Zero is `Unspecified` and reads as a mouse; `NoDevice` carries a bit so the stated emptiness can be told from the unstated one. ⚠ **`print:` and `noscript:` resolve and can never match, deliberately** — paged media is out of scope for good (Part 8 § 1) and a Vixen document always scripts — so the gate names them as the two entries that must have a negative scene and must *not* have a positive one, and a separate test proves the class is still generated, which is what tells "always false" from "not a variant". ⚠ **Owed, and filed separately: nothing sets any of it.** `UiSurface.Preferences` is exposed and defaults to "nothing unusual", exactly as `UiSurface.ColorScheme` has since it was added — and `Vixen.Ui.Desktop` reads the swapchain's gamut and has never read the system appearance, so `dark:` under the media strategy has never been true in a real application either. That is one hole in the platform layer with two victims | `MediaQuery`, `MediaPreferences`, `UiSurface` | — | 0.2 |
| A15 ✅ | **Nested conditional-group rules — done, and for a tenth of the estimate, because the cascade already did it.** `StyleSheetLoader.LoadMedia` has always recursed into the rule it matched, so `@media A { @media B { … } }` loaded and conjoined; the thing that could not nest was `UtilityGenerator`, carrying one `string?` for the whole variant stack. It carries an ordered, deduplicated chain now and emits a trie over those chains, so `sm:md:p-4` and `dark:md:p-4` nest and share their outer wrapper with the shallower utilities. **Nesting cost the rule representation nothing at the time** — though a `StyleRule` carries a
conditional-group id since per-surface media landed; see F11. ⚠ The real finding was next door: see § D6 | cascade | — | done |
| A16 🟡 | Container queries. ⚠ **The cascade half landed** — `ContainerConditions`, `ContainerScopes`, `ContainerQuery`, a second group id on `StyleRule`, two scope slots on `StyleTree`, one integer test in the cascade, 34 computed-value tests. ⚠ **And it closed a silent drop**: ExCSS parses `@container` into a `ContainerRule`, so it never reached `LoadUnknown` and was discarded with no diagnostic, while two docs said it warned. ⚠ **And the `@sm:` variants were never gated on the wiring**: a pure variant emits no property, so the consumption gate never sees one — the blocker was that `--container-*` did not exist, and `Screens` under the same names means numbers two-thirds too big. They are registered now, off `ThemeTokens.Containers`. **Owed**: the layout coercion for containers sized by their contents — ⚠ of which only the *diagnostic* interim landed, log event 7007 naming the container that never settled, which reports the oscillation and does not stop it — the `@container` marker family (which *does* face the gate, and wants a fifteenth probe scene), and the `cq*` units. Containment is *free* for a normal-flow block, whose inline size is already `SizingMode.StretchFit`. See § D3 | cascade + layout | 0.15 | 0.6 |
| A17 ✅ | **Done, and the P2 deferral's premise was right about where the cost is and wrong that it was prohibitive.** Matching is a subtree walk with no bloom to shorten it — the ancestor bloom answers "could this name be above me", and a descendant bloom would have to be rebuilt on every insertion rather than inherited once at creation. Invalidation is the half doc 09 actually deferred on, and it is a *fourth direction*: `StyleInvalidator`'s own remarks said "nothing needs to look upward", and a name inside a `:has()` argument now carries `ReachesAncestors`, walks to the top of the tree and collects by the far end's names from there. ⚠ **The measurement, in work rather than milliseconds** (`HasInvalidationTests`): on a page of ten cards with ten cells each, `.card:has(.error) .cell` costs **103** elements resolved for one class added to one cell, against **11** for the same rule written `.card.flagged .cell` and driven from the card. The upward walk is narrowed by name and cannot be narrowed by depth, because the `:has()` may sit on the root. ⚠ **Two things the obvious implementation gets wrong and both were sabotage-tested.** The argument is restricted to a single compound: CSS anchors it at the element, so `:has(.a .b)` needs the `.a` inside the subtree, and testing the nested selector against every descendant also accepts an `.a` that is an *ancestor* — a rule matching more than it says, which is what F6 is about. And `:has()` blocks the style-sharing cache for `:empty`'s reason one level worse: a sharing key is parent, tag, classes and state, and two identical sibling cards can still differ in what their subtrees hold. ⚠ **`has-[>_.x]` is refused in `Variants` and not in the compiler, because ExCSS 4.3.2 parses `:has(> .x)` into the same node as `:has(.x)`** — the combinator is gone before any Vixen code sees it, so the variant table is the last place the text is intact. A hand-written `.vcss` rule is unguarded and is filed as **#711** | `SelectorMatcher`, `StyleInvalidator`, `StyleRuleSet` | doc 09 P2 — spent | 0.4 |
| A18 ✅ | **Scroll properties as `ScrollView` inputs rather than CSS — done, and the control it was waiting for had been there the whole time.** The deferral's premise was that "scrolling in this engine is `ScrollView`" and that the behaviour had to land first; `ScrollView` was already 397 lines with bars, wheel, keyboard, a focus hook and a `ScrollIntoView`, used by `TreeView`, `DataGrid`, `CodeEditor`, both virtualisers and six editor panels. What was absent was not the feature but the four *readers*, and they are four now: `scroll-margin-*` off the target and `scroll-padding-*` off the container (CSS Scroll Snap §6 — the two come off different elements, and a reader that took both off one passes every test where the numbers match), `scroll-behavior` as an exponential ease off `UiDocument.Ticked`, and `overscroll-behavior*` as the one thing that decides whether a wheel at the stop chains outwards. **22 roots, all `works`**; the four block roots (`scroll-mbs/mbe`, `scroll-pbs/pbe`) stay absent for `space-y`'s reason, and `snap-*` and `scrollbar-*` are still deferred. ⚠ **Two findings worth more than the families.** The insets emit four longhands where `m-*` emits one shorthand, because ExCSS expands `margin` while parsing and has never heard of `scroll-margin` — v4's spelling would have resolved, computed and moved nothing, which is `inset`'s hole and would have been invisible from the class. And the gate could not see any of it until the probe grew a `scrolled` scene with *nested* views and three driven phases: one scroll container measures half the properties inert because the declaration only lands on `#probe`, and one approach direction measures half the edges inert because `ScrollIntoView` moves the minimum and the other branch never runs | `ScrollView`, `UtilityFamilies` | — | done |
| A19 🟡 | **`text-decoration` is done and the other three are not.** Five properties, all five read: `text-decoration-line` (`underline`, `overline`, `line-through`, `no-underline`, and the space-separated list, so `underline overline` is one declaration and two bars), `-color`, `-style`, `-thickness` and `text-underline-offset`. ⚠ **Every position and every thickness comes out of the face**, through `FontFace.Decoration` and HarfBuzz's `hb_ot_metrics` — which was the point: across the twenty-two fonts this repository ships the underline thickness runs from 20 design units per em-square of 2048 to 184, a factor of nine, so any constant is wrong for one of two faces a document could mix. A zeroed `post` table is synthesised from rather than believed (`TestGSUBOne.otf` states 0 and 0), an `auto` thickness under one pixel is floored and an authored one is not, and an underline offset is the *centre* of the stem — FreeType's and Skia's reading of `post.underlinePosition`, which is the one the fonts were drawn against. ⚠ **It needed no command kind, no shader and no second executor**: a bar is a `DrawCommandKind.Rectangle` with a zero radius, so it batches as `Geometry` and the device and the software rasteriser draw it because they are drawing the same quad. One bar per *line* rather than per run — spanning `TextLine.Width`, so it follows the alignment and covers the gaps between faces — and it moves nothing that was measured, which is both CSS's rule and the only behaviour compatible with `TextLayout.Measure` reporting whole device pixels. ⚠ **The five properties are in `InheritedProperties` although CSS inherits none of them**, for `text-overflow`'s reason one step stronger: CSS *propagates* a decoration from the block box across its line boxes, one node produces one box here, and a `.vxml` interpolation emits its text as a child — so `<div class="underline">{Label}</div>` decorates nothing without it. ⚠ **`decoration-dotted` and `-dashed` were absent under the finding `divide-solid` was — there was no dash pattern in `Vixen.Ui` and `border-style` was read by nothing — and A3 closed both halves, so all four of CSS's drawable styles are registered.** A bar is the *easy* consumer of that pattern and it is easy structurally: an axis-aligned rectangle with no corner radius, so breaking it up is breaking up a length, and the marks batch as `Geometry` like the whole bar did. ⚠ `-wavy` stays absent under a reason the dash pattern does not touch: a wave is a stroked path where every other decoration is a rectangle, and CSS states neither its amplitude nor its period. ⚠ **Two things the gate could not see.** The consumption probe needed a `decorated` scene: `text-decoration-line` is observable everywhere there is text, and the four properties that *modify* a bar are observable nowhere, because the injected declaration is the only declaration and a thickness on undecorated text correctly moves nothing — four readers and a green gate that would have called them dead. And "the draw list changed" is satisfied by a bar in the wrong place, so the relations are asserted on pixels the software rasteriser produced, each chosen to fail for the neighbouring case; that is what caught the overline, which an earlier draft put with its top edge on the ascent and which therefore landed on the capitals of a face whose ascent (1556) barely clears its cap height (1493). ⚠ **Owed: `text-transform`, and it is not a keyword table.** It is a *shaping-time* transform, so it changes the measured width — but the blocker is narrower and worse: `straße` uppercases to `STRASSE` and `ﬁne` to `FINE`, so a case mapping changes the UTF-16 length, and `TextRun.Start`, `CaretOffset`, `CaretIndexAt`, `TextLine.Start`/`Length`, `Ellipsized` and `TextField`'s selection are all indices into the element's own string. Without a mapping between the two, `uppercase` on an editable field puts the caret in the wrong place silently. The property is already interned as inherited and waiting. Also owed: `font-variant-numeric` and `font-stretch` | `Vixen.Ui.Text`, `DrawListBuilder`, `TextRun`, `InheritedProperties` | — | 0.2 of 0.4 |
| A20 ✅ | **Run the `Animator`** — built on the style engine, `Observe` from the updater, `Advance` on the tick, `Apply` before the consumers read (F10). **Landed with F11**, which the same seam turned up: `UiDocument` never handed the cascade a `MediaContext` either, so every breakpoint, every `dark:` under the media strategy and every `color-gamut` query was dead | `StyleEngine`, `UiDocument` | **#46** | done |
| | | | **A total** | **6.4** |

### Track B — layout modes

| # | Item | Task | EM |
|---|---|---|--:|
| B0 ✅ | **`Tools/Vixen.TaffyTestGen`** — XML vetter and consolidator, the attribute map, and the Ahem measure. **Landed with 5 524 fixtures, not 5 272**: 884 block, 2 040 grid, 2 352 flex, 84 float, 56 leaf and 108 across three hybrid categories the estimate missed. Flex result: **2 002 of 2 208 runnable pass** | — | done |
| B1 🟢 | **`display: block` — landed.** Block formatting over the existing store: stacking, the inline-axis fill, CSS 2.1 §8.3.1 margin collapsing in full, auto margins, the intrinsic-width probe, RTL, relative insets, `align-content` over the stack. **All 912 `block`+`blockflex` fixtures pass; none fails and none is refused.** The last 72 refusals went in three batches: `scrollbar-width` (64), then `text-align`/`flow-root`/`safe` (24, of which 4 changed bucket rather than converting), then the final 8 with floats. ⚠ That was 768 with 124 refused across four causes, and three of the four are closed: legacy `text-align` (`LegacyTextAlign`, 16), `display: flow-root` (a `Display.FlowRoot` member and one clause in `EstablishesBlockFormattingContext`, 4 of 8) and `align-content: safe end` (`OverflowAlignment`, 4). All 24 pass. The 20 failures that used to be here were in the shared *absolute* path (`aspect-ratio` re-applied after clamping) rather than in block formatting, and closed with CSS Grid §9's auto margins. ⚠ **Still owed under B1**: `sticky`. `inline-block` landed with B3. Floats landed too — all 92 fixtures, including the 4 `block_flow_root_contains_float` families that had joined the bucket when `flow-root` landed — ⚠ and the clause that used to follow — *only for block-level content, so a paragraph beside a float still runs under it* — is stale: §9.5's main clause reached the line walk, and the expectations for it were read out of Chrome 148 case by case precisely because **no fixture in the float corpus has a line box in it**. What is still owed is narrower and is filed in `InlineKnownGaps.txt`: a text leaf breaking around a float's staircase. See Bucket 4 below. | **#25** | 0.35 |
| B2 🟡 | ⚠ **Grid landed and this row said 🔴 for months after it did.** `Vixen.Ui.Layout`'s README is the state: 2 038 of the 2 120 `grid`, `blockgrid` and `gridflex` fixtures pass, 40 are refused and 42 fail in named buckets, with placement, the bulk of track sizing, baseline alignment, the out-of-flow containing block and `grid-template-areas` all done. 🟡 rather than 🟢 because **named lines written into a track list** are still owed, which is the one part the README also names. The estimate below is what it cost, not what is left. **CSS Grid** — a separate algorithm; `grid-template-*`, `fr`, `minmax`, `repeat`, `auto-flow`, named lines and areas, placement, `justify/align-items/self`. Judged by B0's **2 040** plus WPT's 510 `check-layout` grid tests. ⚠ B0's corpus does **not** cover `grid-template-areas`: Taffy's own XML harness leaves it `Default::default()` and no fixture sets it, so named areas need their own oracle | **#27** | 3.5 |
| B3 🟡 | **Inline formatting — partially landed.** Line boxes over the existing store: atomic inlines (`inline`, `inline-block`, `inline-flex`), §10.3.9 shrink-to-fit, §9.4.2 line breaking, §10.8.1 baselines including the last-line-box and `overflow` clauses, three of `vertical-align`'s eight values, and **fragmentation**. ⚠ **The boundary used to be one invariant** — every algorithm in the store preserved *one node produces one box*, and a non-replaced `inline` box crossing a line break is fragmented into several. **That invariant has now been relaxed for one arena and three ints** (offset, count and capacity, addressed exactly as `ChildArena` and `TrackArena` are). `FragmentArena` is variable-length *output*, the shape `TrackArena` is on the input side; `FragmentCount == 0` still means "one box, and it is `Position`", so `GetLeft`, the absolute walk and all four of `UiElement`'s rectangle properties were untouched, and a fragmented node's own rectangle is the **union** — which is CSS 2.1 §10.1's containing block for an abspos descendant of an inline box, so the absolute walk needed nothing. The zero-allocation gate holds with a span re-fragmenting every frame. ⚠ **Still owed under B3**: fragmentation of *nested* spans and of spans with an out-of-flow child (both producer scope, not representation); anonymous block boxes and generated boxes — which are the **opposite** direction, a box with *no node*, and are **not** unblocked by the arena; the strut and therefore the five font-relative `vertical-align` values; `text-align`, `white-space`, `text-overflow: ellipsis`, `line-clamp`. ⚠ **Zero fixtures**, confirmed by enumeration — Taffy's `display` attribute takes five values across all eight files and none is inline. Oracle fetched from WPT (`css-flexbox/inline-flex.html`); fragmentation is arithmetic over explicitly sized boxes in `InlineFragmentationTests`. See `InlineKnownGaps.txt`. | **#26** | 2.3 of 3.0 |
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
| C0 ✅ | **The `SplitName` fallback (F8) — refused, measured, and replaced by the diagnostic it was standing in for.** A retry rescues zero classes over every nesting pair in the registry against both shipped themes, and every shadowed root has exactly one registered prefix, so there is nothing to fall back *to*; `ShadowedFamilyTests.A_shorter_prefix_would_rescue_nothing` re-measures that on each build. What was real is that "no such family" and "that family has no such value" were one `false` and one report list of 7 103 entries: `UtilityGenerator.Unresolved` splits them, and the `.unrecognised.txt` report is sectioned. ⛔ The 35 shadowed rows still want 35 registrations, which is C7 | 0.1 |
| C1 🟢 | Arbitrary properties, and v4's `bg-(--var)` shorthand | 0.15 |
| C2 ✅ | Re-peg the `shadow`/`blur`/`rounded` scales to v4's names (D5). ⚠ **Refuted and closed**: C3's transcription brought v4's names with its values, the editor's `--radius-*` are a semantic namespace that collides with none of them, and no picture moved. The real gap was a `--blur-*` namespace that had never shipped, so `blur-md` produced no rule — additive, and it ships now | 0.1 |
| C3 ✅ | `@theme` replaces `vixen.ui.yaml`; `ThemeTokens` reads a stylesheet, and v4.3.3's palette ships as the engine default in oklch (D1, D4) | 0.5 |
| C4 ✅ | Cross-assembly token sharing, shape C (Part 3) — `VixenStyleTokens` names another project's `@theme`; `Vixen.Editor.Ui.Styling.targets` makes joining the editor's theme one `Import`; guarded by `SharedThemeTests`, which is cross-assembly because no per-project suite can be | 0.3 |
| C5 🟡 | The gate: a family emitting a property no consumer **acts on** fails the build (#11) — ✅ landed as `UtilityConsumptionGateTests` with its expiring allow-list. ⛔ Still owed: `Tools/Vixen.TailwindParity` regenerating the TSV from a committed registry snapshot, which is the half that needs the Tailwind registry and cannot be a test | 0.2 |
| C6 ✅ | Doc 09's five missing families — `space` and `divide` written (a new `Family.Scope`, so the generator can emit `& > :not(:last-child)`); `mix-blend` and `origin` refused as measured-inert and struck from doc 09's list; `scroll` deferred to A18 per Part 8 § 3. ⚠ All three refusals have since expired and all five families are registered — `origin` in F9, `scroll` under A18, `mix-blend` (with `isolation`) in Part 9 Bucket 2. See F9 | 0.25 |
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

⚠ **This table is what the work was estimated at, not what is left, and the difference is now most of
it.** B1, B2 and B3 have all landed since it was written — block whole, grid to its named-lines gap,
inline to nested spans, anonymous boxes and the strut — so 18.8 is a figure about a plan rather than
about a remainder, and quoting it as owed work overstates by the size of track B. Re-cost it against
the per-row markers before it is quoted again (#674).

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
against. 746 of 912 fixtures at the time, and every failure in the shared absolute path rather
than in block.

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
`overscroll-*`, `scroll-behavior` and `scrollbar-*`. ✅ **Discharged for 23 of the 32 — 22 by A18 and
`scrollbar` with the layout store's scrollbar gutter; nine stay deferred.** The argument was right and one word of its premise was wrong, which is worth keeping
rather than editing away.

The argument: `scroll-margin` means something only to a scroll container that honours it, so writing
the families first would have added 32 inert roots — a tenth of the whole index — and that is exactly
the pattern this document exists to stop. That held, and the gate proved it would have: registered
against no reader, every one of these measures zero channels.

⚠ **The premise that was wrong was "the behaviour comes first".** It read as though `ScrollView` had
to be built. It did not — it was already the control this section names, 397 lines of it, with two
bars, wheel and keyboard handling, a routed focus hook and a `ScrollIntoView`, driven by `TreeView`,
`DataGrid`, `CodeEditor`, both virtualisers and six editor panels. **What was missing was never the
behaviour; it was four property reads inside a control that already did all of it.** The re-homing
was therefore the whole of A18 and the estimate was mostly spent on the *gate*, not the feature. The
lesson generalises past this section: "deferred until the feature lands" and "deferred until somebody
checks whether the feature landed" look identical in a table, and only one of them is a real
dependency.

**What is still out**, and each for its own reason rather than for this section's:

- **`scroll-mbs-*`, `scroll-mbe-*`, `scroll-pbs-*`, `scroll-pbe-*`** — the block pair, absent for
  `space-y-*`'s reason (§ F9): nothing interns a `-block-start`/`-block-end` longhand, and
  `Vixen.Ui.Layout` has no writing mode for one to differ from `-top`/`-bottom` in. The *inline* pair
  is in, because `ScrollView.InsetOf` folds it against `direction` itself.
- **`snap-*`** — `scroll-snap-type`, `scroll-snap-align` and `scroll-snap-stop` needed a snapping
  algorithm, which is a feature rather than a read: a scroll that comes to rest has to choose a
  snap position among the candidates in its subtree, and nothing computed candidates. ⚠ This one
  really was "the behaviour comes first", and it is the only one of the four that was.
  ✅ **The behaviour has landed** — `ScrollView` walks its subtree for `scroll-snap-align`, aligns
  against the snapport `scroll-padding` leaves, and honours mandatory/proximity, `scroll-snap-stop:
  always` and a re-snap after layout. ⚠ **And the algorithm was the easy half.** A snap is defined at
  the moment a scroll *comes to rest*, and neither gesture had an end: `ScrollBar` raised nothing when
  a thumb was released (`ScrollEnded` now does) and a wheel is a stream of deltas with no terminator
  in it at all (`ScrollView.SnapIdleSeconds`, on the tick clock — not the event's, which a platform
  head is free to stamp from a source the frame loop never reads). ✅ **The four roots are registered
  now**, and the order was the right one: registering them first would have measured all three
  properties inert. ⚠ **One family answers all four roots**, because Tailwind spells all twelve
  classes `snap-` and they set three different properties — `Register` keeps the first family under a
  name, so a family per property was never available, and the keyword table already carries a
  property per value. The strictness is the one part that could not be a plain declaration:
  `snap-y snap-mandatory` is two classes writing one `scroll-snap-type`, so the axis names the
  strictness through `--tw-scroll-snap-strictness` with CSS's own `proximity` as the fallback — which
  is also why `ScrollView.SnapType` reads that value as *text*, since what arrives there was
  assembled by the cascade rather than typed. ⚠ **And the probe needed a scene of its own rather than
  a bigger `scrolled` one**: a snap is defined only where a scroll comes to rest, and nothing in
  `scrolled` ever ends a gesture. `snapped` is that scene, and its three phases are not
  interchangeable — one ends ON `#probe` (which is what makes `scroll-snap-align` observable), one
  ends PAST it and nearer `#trail` (`scroll-snap-stop`, which decides nothing where the nearest
  candidate is already the one it would block on), and one turns the wheel over the inner view
  (`scroll-snap-type`, the only one that lands on `#probe` as a container). Deleting any one of the
  three takes exactly one property back to `inert`, which is how each was verified. ⚠ A fourth thing
  the scene had to learn: a frame between the wheel and the silence. A candidate's snap position is
  read off its `Bounds` at the offset the view is at *now*, so a terminator that fired before the
  layout caught up measured every candidate against the offset the gesture started at, landed on the
  one the view was already on, and left all three properties looking inert with the reader present.
- **`scrollbar-color` / `scrollbar-gutter`** — `ScrollBar` is a child element this control creates
  and themes through `scrollbar { … }`, `--track-color` and `--thumb-color`. A CSS property that
  restyled it would be a second way to say what the theme already says, so `scrollbar-thumb-*` and
  `scrollbar-track-*` stay absent. `scrollbar-gutter` is a distinction `Overflow` deliberately does
  not carry — `auto` and `stable` differ only in whether the gutter is reserved when there is
  nothing to scroll, and there is no `Overflow.Auto` to hang that on (see `LayoutEnums`).
- ⚠ **`scrollbar-width` was in that list and should not have been, and the reason is the one this
  document keeps rediscovering: the argument was about PAINTING.** A gutter is not a restyling of
  the bar; it is room taken out of the content box, and every size below a scroll container is
  computed against what is left. 180 of Taffy's fixtures asserted that and were being skipped.
  `LayoutStyle.ScrollbarWidth` reserves it now and the `scrollbar` root reads **works**.
  ⚠ It needed a new probe scene to be seen — `ControlTheme.vcss` gives `scroll-view`
  `overflow: hidden` with the bar laid over the top, so no scene in the gate had a scroll container
  in it and the property measured inert with 180 fixtures behind it.

**4 · `position: fixed` and `sticky`** — doc 09 excludes `fixed` on the grounds that there is no
viewport in a game overlay. That argument holds for `fixed` and **does not hold for `sticky`**: a
sticky table header inside a scroll container is a real editor requirement, has nothing to do with a
viewport, and `DataGrid` currently hand-rolls it. `sticky` is 🟡 **in**, sized inside B1.

**Everything else in the 223 absent roots is in.** Including the ones that look frivolous: `zoom-*`,
`field-sizing-*` and `scheme-*` are one property each and cost less than arguing about them, and an
inventory with an unexplained hole in it is how a subset gets rationalised the next time.

---

## Part 9 — The sixteen absent `Layout` roots, triaged

⚠ **They are not one feature, and the useful finding is that they are not even one *kind* of
absence.** Sixteen roots in the `Layout` category read `absent` *when this was written*; the count is
twelve now, and the four below are why the headline is left at sixteen — it is the triage's own
denominator and rewriting it would falsify what was triaged. Four of them belong to work in
flight elsewhere — `mask-radial-*`, `mask-radial-at-*`, `ring-offset-*` and `@container-*`; ⚠ of
those, **`mask-radial-at-*` has since landed and reads `works`**, and `mask-radial-*` reads
`partial`: its four ending sizes are registered and read, and its two ending shapes are held up by
this table's own shape rather than by `GradientRefusal.Extent`, which no longer refuses them. The
other twelve were triaged together, and they fall into four buckets that want four different
answers. **Only one bucket was buildable, and the other three are refusals with a measurement
behind each.** The costs below are the deliverable; the one implementation is the small part.

### Bucket 1 — the reader already existed. `visibility`. ✅ **Closed.**

⚠ **`absent` meant "nobody can spell it", not "the engine cannot do it", and that is the most
expensive way for this table to be wrong.** `DrawListBuilder` has honoured `visibility: hidden`
since the draw list existed — an inherited property, read per element, gating the shadow, the
background, the gradient, the border, the text and `OnDraw`, and deliberately *not* gating the clip
bracket or the child recursion, so a descendant that declares `visible` reappears inside a hidden
parent. What was missing was three classes and one keyword. `visible` / `invisible` / `collapse`
are registered, and the root now measures `works`.

Two things were genuinely broken and are fixed with it:

- **`collapse` parsed, cascaded, inherited — and painted.** The test was `mode != hidden`, so the
  third keyword fell through to "visible". CSS 2.1 §11.2 says `collapse` means `hidden` on every box
  that is not a table row, column or their groups, and this engine has **no table formatting
  context at all**, so `hidden` is the complete answer rather than an approximation. ⚠ This was the
  `box-shadow: inset` shape exactly — a property that is *read* while one of its *values* is
  refused, which no per-property gate can see, because the gate unions channels across every value
  the family emits and `hidden` moves paint. Registering `collapse` without fixing the keyword would
  have shipped an inert class under a green gate.
- **Hit testing ignored the property.** CSS UI §5.2 makes an invisible box untargetable; here it
  went on catching the clicks meant for whatever was behind it. `AdvancedTheme.vcss` has three
  `visibility: hidden` rules, and one of them shows what the gap cost: `code-metrics` is an
  absolutely positioned measurement probe pinned at the origin, invisible, and until now the first
  thing a click in the top-left corner of a code editor ever reached.

⚠ **The one divergence left, stated rather than hidden:** Flexbox §4.1 makes `visibility: collapse`
on a *flex item* a collapsed item — main size zero, cross-size contribution kept, a strut. That is a
layout effect and needs `LayoutStyle` to carry the keyword, which it does not. Suppressing the paint
is right in that case too and strictly closer than painting it in full, so this is a smaller gap
than the one it replaced, not a new one.

### Bucket 2 — nothing existed that could observe it. `isolation`, `mix-blend`. ✅ **Written.**

**What this bucket said, and what it turned out to be.** `isolation: isolate` looked available: the
compositor makes real groups, so "open a layer" is a line of code. ⚠ **It was available and
unobservable, which is not the same as working** — `isolation`'s only defined effect is on a
descendant's `mix-blend-mode`, and `mix-blend-mode` existed at no layer at all. Registering it would
have added a property that resolves, computes a value and moves no channel in any scene, which is
the defect this document exists to prevent. So the order was forced: the blend first, the isolation
after it.

**Both are written now.** `UiBlendMode` is CSS Compositing 1 § 5.1's sixteen modes, `UiBlend.Apply`
is their arithmetic (the separable twelve and the four non-separable ones, with § 5.3's `ClipColor`,
`SetLum` and `SetSat`), `UiLayer.Blend` is the channel, `DrawListBuilder` reads `mix-blend-mode` as a
sixth reason to open a group and `isolation: isolate` as a seventh, and `SoftwareUiRasterizer`
applies the blend at the composite draw.

⚠ **Three claims made while refusing this were wrong, and the third was made by the correction that
replaced the first two.**

1. *"There is no offscreen target to blend into."* Expired when the compositor landed.
2. *"There is no blend channel on a `DrawCommand`."* Named the wrong seam — a **per-command** blend
   is a defect, because § 5.1 blends an element's *rendered result*: a bordered element's background,
   border and text composite source-over with each other first and only the finished group blends.
   Every bordered element is two commands, so the two readings differ on the commonest element there
   is. `MixBlendModeTests.A_bordered_element_blends_its_result_rather_than_each_command` is that
   distinction as two pixels.
   ⚠ The correction drawn from it — "the channel is on `UiLayer` and **not** on `DrawCommand`" — is
   half wrong as stated. There *is* a `DrawCommand.Blend`, because a `LayerPush` is a `DrawCommand`
   and that is how every other group-wide effect reaches the geometry builder; what must not exist is
   a blend the *executors* read per command, which is a different statement about the same field.
3. *"The composite must read its destination, which on the GPU is a subpass input, a framebuffer
   fetch or a copy the UI pass does not have."* ⚠ **Refuted.** § 5.1 defines the whole feature as a
   change of *source* colour followed by an ordinary source-over —
   `Cs' = (1 − αb)·Cs + αb·B(Cb, Cs)` — so there is no read-modify-write and no second blend state
   anywhere. The software rasteriser reads the destination because it happens to own the buffer; on a
   device the backdrop can arrive as a **texture**, and `UiRenderer.Capture` already produces exactly
   that picture for `backdrop-filter`. The shape is there. What is missing is a composite fragment
   that samples two textures.

**`isolation` cost what its own refusal predicted.** A nested group's draws are executed into its
parent's surface, so a blended descendant of an isolated ancestor mixes with that ancestor's
accumulation and can never reach the page behind it — the boundary does the work, which is what CSS
Compositing 1 § 3 says a stacking context is for. ⚠ And where the ancestor painted nothing, that
accumulation is transparent black, which § 5.1 weights the blend to zero against: a blend against
nothing is `normal`. That is why an isolated wrapper makes a `mix-blend-multiply` child come out
*unblended* rather than come out black, and it is the assertion the isolation test is built on.

**Still owed: the device.** `UiRenderer` submits a blended composite source-over, so the picture on
the GPU path is the one the frame would have had without the declaration. That is the same bargain
`UiLayer.Blur` states for a consumer that ignores it, and it needs an observer for
`Backdropped`'s reason and a sharper version of it — a blend over a flat backdrop is frequently the
identity (`multiply` against white, `screen` against black), so neither a screenshot nor a comparison
of the two executors can tell. `UiRenderer.Unblended` counts it. The `mix-blend` row stays `partial`
until that number can be zero on a frame that asks for a blend.

⚠ **`background-blend-mode` is not this and stays refused.** It blends an element's background
*layers* with each other, and there is one background layer for them to blend.

### Bucket 3 — the code existed and an *input* did not. `object-fit`, `object-position` ✅; `contain` ⛔.

⚠ **The guess going in was that these are about the sampling rectangle. The sampling rectangle was
already there and already honoured** — `DrawCommand.Source` is a UV sub-rect, it survives to the
geometry builder, and negative extents work (`Viewport` flips vertically with one). Nine-slice
already relates a destination rect to a source rect, and `Icon.Fit` is `object-fit: contain` plus
`object-position: center` written out in path space. None of that was the blocker.

**The blocker was that `Vixen.Ui` cannot see the texture's intrinsic size,** and ⚠ **that made the
close an API decision rather than an algorithm.** `Image.Texture` is an opaque `ulong` the renderer
owns, and this assembly does not reference `Vixen.Graphics` — which is the whole bargain, not an
accident to route around. So the intrinsic size is **supplied**: `Image.IntrinsicSize`, in the
texture's own pixels, written by whoever registered the texture, because the asset layer is the layer
that knows. **Zero means unknown**, and unknown draws `fill` whatever the class says — which is not a
fallback for a missing feature but CSS's own answer, since Images 3 § 5.5 defines every other keyword
as a relation between the intrinsic ratio and the box and gives `fill` for content that has no
intrinsic dimensions.

⚠ **It sizes the picture and not the element, and that half of CSS's replaced-element model is
deliberately still absent.** A browser lets an `<img>` with no width take its box from the intrinsic
size; an unsized `Image` here is still a zero-height box, because sizing a replaced element from its
content needs a measure hook the control has no equivalent of. What landed changes what is drawn
*inside* a box, not what the box is.

**The arithmetic is one arrangement for all five keywords,** and the reason it can be is that the
answer is always "place a rectangle, then clip it to the box". The tempting shape is two cases —
shrink the *destination* for `contain`, narrow the *source* for `cover` — and it is two chances to
get the position arithmetic subtly different. ⚠ `none` is the case that makes the two-branch version
wrong outright: a 64 × 16 picture in a 40 × 40 box overflows on one axis and underfills on the other
at the same time.

⚠ **`object-position` carried a second blocker of its own, and sizing the pair as one item would have
missed it.** Four of Tailwind's nine position classes are *two-word* values — `object-left-top`
computes to `left top` — and `UiDocument.KeywordOf` answers `null` to anything that is not one bare
identifier, so those four were unreadable by every accessor `StyleAccess` had. The root wanted a
`<position>` **reading** and not another keyword table. `UiDocument.PositionOf` is the fifth accessor,
and it is the *same* parser `background-position` and `radial-gradient(at …)` use, because CSS Values
4 § 8.2 gives all three one grammar and two readers of it would be two sets of keyword handling to
keep in step. ⚠ The extents it resolves against are the **slack** rather than the box: a 50% offset
centres the concrete object size, which is half of what is left over, and passing the box's own size
would put the middle of a smaller picture at the middle of nothing.

⚠ **And the consumption gate needed a scene before it could see any of this.** `object-fit` and
`object-position` are the only properties in the registry read by a *replaced element*, and no probe
scene had one — the eighth entry on `UtilityConsumptionProbe`'s own tally of arrangements that were
missing, and the one that would have made a finished feature measure `inert`. The `pictured` scene's
`#probe` **is** an `Image` rather than holding one, because `object-fit` does not inherit, and it
declares `object-fit: contain` itself so the injected `object-position` has slack to move in —
`primed`'s lesson, one property along.

`contain` is refused for a related reason and a worse one: **there is no containment concept in the
layout store at all** — no property, no style slot, no branch. That claim was re-checked against the
tree rather than inherited: `LayoutStyle` has no field, `LayoutEnums` has no member, and no algorithm
file mentions the word. ⚠ **Correcting a claim made in passing during the container-query work:**
`LayoutUnit.Stretch` is *not* an enum member nothing references — `Tools/Vixen.YogaTestGen` emits it
and two generated fixtures set it. It is one nothing *resolves*: `StyleLength.IsResolvable` admits
only `Point` and `Percent`, so both fixtures pass with the keyword behaving as "undefined", and the
two disagree about what it should mean (`Stretch_width` wants the containing block's width,
`Stretch_flex_basis_column` wants its own content's height) — so resolving it closes one by opening
the other.

⚠ **And the paragraph that used to sit here said three keywords were unimplemented, which stopped
being true when the intrinsic pre-pass landed.** `MinContent`, `MaxContent` and `FitContent` *are*
resolved — `IsResolvable` is not the predicate that settles them, `StyleLength.IsContentBased` is,
and `LayoutTree.Intrinsic.cs`'s whole-tree bottom-up pre-pass measures the node and substitutes a
`Point` before the algorithm reads the slot. Reading a `NaN` out of `Resolve` as "unimplemented" is
the mistake, and it is one this file made about its own store. **`Stretch` is the one left**, and
`contain` is not behind it. This is also the layout half of the container-query coercion.

#### What containment should be here, sized per kind

⚠ **`contain` is one property with five independent effects, and the eight Tailwind classes are
combinations of them — so sizing the root as one item is the mistake to avoid.** `contain-content` is
`layout paint style`; `contain-strict` adds `size`. A registration that made three of the five real
would leave both aggregate classes half working, which is the shape this document exists to refuse.
Taken one at a time against this engine's own seams:

| kind | what CSS asks for | the seam here | verdict |
| --- | --- | --- | --- |
| **size** | the box sizes as if it had no contents | `LayoutTree.Layout.cs`'s `MeasureNodeWithoutChildren`, which is *already* that computation for a childless node, plus `LayoutTree.Intrinsic.cs` so a content keyword on a contained box measures zero | **buildable, and the interesting half** |
| **inline-size** | the same on one axis | the same seam, one axis | buildable with `size` |
| **layout** | independent formatting context; a containing block for descendants | `LayoutTree.Absolute.cs` already picks a containing block per node | **buildable, and largely already true** — a flex or grid item is an independent formatting context by construction here, so the observable part is the abspos containing block, which `position: relative` already gives |
| **paint** | descendants clipped to the padding box | `DrawListBuilder`'s `OverflowReader` and the clip stack | **buildable**, and the same clip `overflow: hidden` pushes |
| **style** | counters and quotes are scoped to the subtree | — | ⛔ **refuse**: there are no counters and no `content` quotes in this engine, so every value of it computes and moves nothing. A class emitted for it would be inert by construction |

**So the shape is: four of the five, and `style` refused in writing.** The order is forced by
observability rather than by effort — `size` is the only one whose absence is visible on a fixture
that does nothing else, and it is the one the aggregate classes are worth having for.

⚠ **Size containment is not "skip the children", and reading it that way is how it gets built
wrong.** A contained box still *lays its children out* — they are painted, they are hit-tested, they
scroll. What it must not do is let them decide its own box. In this store that is a real distinction:
`CalculateLayoutImpl` computes the node's measured dimensions and positions its children in one pass,
so the intervention is to settle the box from `MeasureNodeWithoutChildren` first and then run the
children against those dimensions as though they were `Exactly`. That is a change in the middle of
the flex algorithm rather than at either end of it, and it is why this is not a table entry.

⚠ **The instrument to build first, before any of it.** A contained box and an uncontained one draw
the *same picture* wherever the children happen to fit — which is most fixtures — so a test whose
box has an explicit `width` and `height` proves nothing about `contain: size` at all. The fixture has
to be an auto-sized box with children that overflow it, where containment collapses the box to zero
and its absence does not. `contain: paint` has the mirror-image trap: it is invisible unless a child
overflows.

**Sized:** `paint` is small and is the same clip that already exists. `layout` is small and is mostly
a statement about what is already true, which is a reason to be careful that its test can fail.
`size` and `inline-size` are the item, and they are a change inside the flex and block algorithms
with an intrinsic-pre-pass half beside it. `style` is a refusal. The four buildable kinds are filed
separately from this triage; the row stays `absent` until every keyword an aggregate class expands to
is real, because a half-real `contain-strict` is worse than an absent one.

### Bucket 4 — the algorithm was never written. `columns`, the three `break-*`, `box-decoration-break`, `float`, `clear`.

**Multi-column and the `break-*` roots are already permanently out of scope** under Part 8's first
exclusion, and this triage does not reopen that — but the *reachability* question it was asked to
settle has an answer, and the answer is no.

⚠ **Fragmentation landed this week and it is inline-axis fragmentation, which is a different thing
from what multi-column needs.** The evidence is narrow and decisive:

- `WriteFragments` has exactly **two** call sites, both in `LayoutTree.InlineItems`, plus clears.
  Nothing else in the store produces a fragment.
- A fragment is pure geometry — two rectangles and an `Ends` flag — and `LayoutFragmentEnds` is
  `Start`/`End` in the **inline** axis, for CSS Display §2.2's rule about which vertical edges to
  stroke. Block-direction fragmentation needs block-start/block-end ends and there is no member.
- **No fragment carries any association to a child.** `CommitInlineBoxFragments` rebases the span's
  children into the span's coordinates, and each child keeps exactly *one* position. Multi-column
  needs a block child that crosses a column boundary to be split with its own subtree distributed
  between the pieces, and there is nowhere to record which piece a descendant belongs to.
- A **fragmentainer** — the column box itself — is a box with no node behind it, and
  `LayoutTree.Fragments` says in terms that this is the *other* direction from what it serves and is
  not served by the arena.

So multi-column needs a second fragmentation machinery, not an extension of this one: fragmentainers,
block-direction breaking with a forced/avoid break model, subtree splitting, and column balancing
(an iterative height search). **Sized: comparable to a fourth layout algorithm.** The exclusion
stands, now for a measured reason as well as a scoping one.

⚠ **`box-decoration-break` is the one that is *not* print media, and it is refused for a third
reason worth separating out.** `slice` and `clone` are exactly the distinction `LayoutFragmentEnds`
already encodes — `clone` would give every fragment `Ends.Both` — so inline fragmentation does serve
it and it is a handful of lines. **But nothing paints fragments.** `GetFragment` is called only from
`InlineFragmentationTests`; no painter, no hit test, no consumer outside the layout assembly reads a
fragment at all. Registering `box-decoration-break` today would be inert, and it would be inert
*downstream of a feature that already shipped*. **The owed item is not the property — it is a draw
list that walks fragments**, which is also what makes a two-line `<span>`'s border correct today.
That is the real finding in this bucket and it is a gap in fragmentation, not in this root.

**`float` / `clear` — ⛔ not started, as instructed, and sized.** This is the largest single item in
the parity list and CSS 2.1 §9.5 is why. The measurements:

- **88 fixtures** are refused on it — the 84 in `Taffy/Corpus/float.xml`, pinned by
  `TaffyPendingCorporaTests` at `{ "float", 0, 0, 84 }`, plus 4 in the block corpus. It is the last
  pending corpus in the file, and unlike block and grid it was **never gated on a `display`
  keyword** — it is refused by name in `TaffyStyleMap`, so no keyword will ever release it.
- `LayoutTree.Block` already names the two entry points, and the note there is right: the
  intrinsic-width probe in `DetermineBlockContentWidth` would route a floated child's width into a
  left/right accumulator instead of the running maximum, and the in-flow walk would ask a float
  context for a content slot rather than taking the full inner width. **Both are additive** —
  nothing caches a "the inner width is the whole content box" assumption a float could not later
  narrow.
- ⚠ **What that note does not price is the float context itself**, which is the actual cost: a
  per-formatting-context list of outstanding floats with their block extents, a "find the first
  vertical band wide enough" query that every line box and every block box must consult, `clear`
  as a forced advance past the relevant floats' bottoms, the interaction with margin collapsing
  (§9.5 does not let a collapsed margin move a float's position), and the rule that a block box's
  border box ignores floats while its *line boxes* do not. It also touches the inline formatting
  context, which is where the line-narrowing lands.

**Sized: the largest remaining layout item, and the only one that changes both the block and the
inline algorithm at once.** It should be its own task with its own corpus target, and the honest
success measure is `{ "float", 84, 0, 0 }`.

#### ✅ Landed — and the honest success measure was the wrong measure

`{ "float", 84, 0, 0 }` was reached, along with the 8 in the block corpus rather than the 4 predicted
above. `FloatSide` and `Clear` on `LayoutStyle`, `LayoutTree.Floats.cs` for the exclusion list, four
sites in `LayoutTree.Block`, `float-*` and `clear-*` utility families, and the two roots in the
ledger move from `absent` to **`partial`**.

⚠ **The sizing was right about the cost and wrong about the shape, in a way worth recording.** Read
the third bullet again: it prices "a query that every line box and every block box must consult" and
says the work "touches the inline formatting context, which is where the line-narrowing lands". It
does not, and could not have, because **not one of the 84 fixtures contains a line box.**
`grep -c '<text' Taffy/Corpus/float.xml` is zero. The corpus named after the feature is entirely
block-level: floats placed against each other, formatting-context roots refusing to overlap them,
clearance, and containment. Every number in it was reachable without touching `LayoutTree.Inline` at
all, and none was touched.

So the item that landed is §9.5 minus its headline clause. A paragraph beside a float still runs
under it. That is filed in `InlineKnownGaps.txt` with the shape of the fix, and it has **no oracle in
any of the 5 524 fixtures** — which is the part to carry forward, because the corpus target that was
supposed to define "done" cannot see the half that is missing.

⚠ **Both roots are `partial` rather than `works`, and the gap is named rather than papered over.**
Tailwind's `float-start`/`float-end` and `clear-start`/`clear-end` emit the logical
`inline-start`/`inline-end`, which resolve against a writing mode. CSS 2.1's keywords are physical
and do not flip with `direction` — the float corpus proves it by shipping RTL variants of ten
`float_bfc_*` families with expectations identical to their LTR twins. Aliasing the logical keywords
onto `Left` would be right in LTR and wrong in RTL inside one declaration; accepting them and
dropping them would be a class that computes and does nothing. Neither is emitted, and `value_gap`
carries the reason.

⚠ **One cost the sizing did not have.** A cache hit returns a node's size without re-running its
layout, and a block container's layout has the side effect of appending its floats to the formatting
context around it. So the measurement cache is bypassed for any tree containing a `float` or a
`clear`, decided by one scan of the style array per pass. Float-free trees are unaffected, which is
asserted by construction rather than argued: with the flag clear, every float branch in
`WalkBlockChildren` is dead.

---

## Part 10 — The twenty-one absent `Typography` roots, triaged

⚠ **Typography was the largest remaining absent cluster — 21 of its 34 roots — and it is not one
feature either.** Triaged the way Part 9 triaged Layout, and the split came out the same shape and
in different proportions: **four roots were nothing but a missing family**, three are already
refused with a measurement behind them, and the remaining fourteen divide into *blocked on a
refusal this document already made* and *waiting for an algorithm nobody has written*. The costs
below are the deliverable; the four registrations are the small part.

⚠ **A second pass has since run against this triage and closed three more of the sized roots —
`word-break`, `indent-*` and `font-variant-numeric` — so Buckets 3 and 4 below are written in two
layers: what the triage predicted, and what the work cost.** They differ, in the same direction,
three times out of four, and the "Net" paragraph at the end of this section names the pattern. The
three still open carry a corrected estimate rather than the original one.

⚠ **Every keyword was checked against the reader by hand, and that is not thoroughness for its own
sake.** `UtilityConsumptionGateTests`' verdict is per **property** and unions over every value a
family emits, so one live keyword makes the whole family green — which is exactly how `visibility`
came to ship a `collapse` that parsed as the `box-shadow: inset` shape and painted normally, under a
gate that was green because `hidden` moved paint. Six of these roots are multi-keyword, and the
keyword most likely to be dead is the rare one no theme writes: the one the union hides. So each
keyword below was measured on its own through `UtilityConsumptionProbe.Channels(property, value)`,
and the results are stated per keyword rather than per family.

### Bucket 1 — the reader already existed. `font-style`, `overflow-wrap`, `text-wrap`. ✅ **Closed.**

⚠ **Part 9's most expensive way for this table to be wrong, three more times: `absent` meant "nobody
can spell it".** Every one of these three properties has a finished consumer, and two of them have a
*complete* one:

- **`font-style`** — `UiDocument.FontStyleOf` reads it, `font-style` is in `InheritedProperties`, and
  `FontRegistry.Slanted` implements CSS Fonts 4 § 5.2's italic → oblique → upright search in full.
  `italic` and `not-italic` are registered and the root measures `works`.
- **`overflow-wrap`** — `UiDocument.WrapModeOf` maps `anywhere` and `break-word` onto
  `TextWrapMode.Anywhere`, which `LineWrapper` applies at a **grapheme** boundary. `wrap-anywhere`,
  `wrap-break-word`, `wrap-normal` and `break-words` are registered; the root measures `works`.
- **`text-wrap`** — this one needed one line of engine. CSS Text 4 § 4 redefines `white-space` as a
  shorthand over a collapsing half and a wrapping half, and the wrapping half is precisely the third
  of `white-space` that `UiDocument.WrapsOf` was already answering. It now reads both, and either
  saying `nowrap` stops the wrapping — a choice, not the specification, because this cascade inherits
  *specified* values and does not expand the shorthand, so there is no declaration order to appeal to.

⚠ **Two of the three measured inert with the reader present, and the scenes were the reason both
times — the eighth and ninth instances of the lesson `UtilityConsumptionProbe` has now recorded
seven times.**

- **`font-style` had nothing to match.** `FontRegistry.Slanted`'s last resort is an upright, so a
  family with no italic variant resolves `italic` to its upright — correctly, and invisibly. The
  probe registered two weights of one face and no slant, so the property moved no channel with a
  finished reader *and* a finished matcher behind it. `Typeset` registers a third face now, exactly
  as it registers a second weight and for the same stated reason.
- **`overflow-wrap` had no long word.** `LineWrapper` consults the mode in one branch — "nothing
  fits: one unbreakable run is wider than the whole line" — and all fourteen scenes shared a label
  whose longest word is two characters, so the branch never ran in any of them. It is not a narrow
  box that was missing: `tiny`'s is two pixels. The `overlong` scene is what gave the gate eyes.

⚠ **And the gate could not have caught what a pixel test caught.** "The draw list changed" is
satisfied by a break placed inside a surrogate pair, by a word broken when it did not need to be, and
by a line that still runs off the edge after being told not to. `TextWrappingPixelTests` and
`FontSlantPixelTests` assert relations chosen to fail for the *neighbouring* case, on the software
rasteriser's output — the method A19's overline needed.

**Three deliberate non-registrations inside this bucket, each of which would have been a class that
resolves and does nothing:**

- ⚠ **`text-balance` and `text-pretty`.** Both are values of a property that is now read, so they
  would cascade, compute, reach `WrapsOf`, fall through to "wraps", and produce byte-identically the
  lines the default produces. **No per-property gate can see that** — the property passes — which is
  the `collapse` shape again, and the root is `partial` rather than `works` because of it. Both ask
  for a better *choice* of breaks and `LineWrapper` is greedy first-fit by an argued decision;
  `The_better_break_keywords_are_indistinguishable_from_the_default` is what would have to start
  failing before either class is worth having.
- ⚠ **`break-normal`'s second half.** v4 emits `overflow-wrap: normal; word-break: normal`. The
  second is the initial value of a property nothing reads — a no-op twice over — so the family emits
  the first alone and the row carries the value gap. Registered rather than skipped because the half
  that is there is a real opt-out: `overflow-wrap` inherits, so it is how a child escapes a
  `break-words` on its container, and `wrap-normal` is the same declaration under v4's spelling.
- **`anywhere` and `break-word` are one behaviour here, and both are registered anyway.** CSS Sizing
  § 5.2 separates them only by their min-content contribution, and `Vixen.Ui.Layout` has no stage
  that consults it. Asserted as one behaviour in a test rather than left as an unstated deviation,
  so the day the layout grows that stage something fails and says where the claim was written down.

### Bucket 2 — blocked on F6, which this document already refused. `list-*`, `list-image-*`, `list-style-position`, `placeholder-*`. ⛔ **Refused, and not independently.**

⚠ **All four want a box that does not exist, and F6 is why it does not.** A list marker is generated
content — CSS puts it in a `::marker` box — and a placeholder is styled through `::placeholder`.
`SelectorCompiler` refuses pseudo-element selectors outright, and refused them *for a reason*: a
compiled `::before` used to style the originating element, so `p::before { color: red }` turned the
paragraph red.

So `list-style-type` would compute a keyword with nowhere to draw it; `list-style-image` needs that
same absent box *and* `background-image`'s painter aimed at it; `list-style-position` describes where
the box sits relative to the line box. And `placeholder-*` has no element to match even if the
selector compiled: `TextField.Placeholder` is a C# property the control draws itself, not a child in
the tree. **None of these four is worth costing separately** — three of them are one generated-content
box behind `list-*`, and the fourth is F6. A `--placeholder-color` custom property the control read
would work, and would not be the class Tailwind means.

### Bucket 3 — `word-break` and `indent-*` ✅ **closed**; `tab-*`, `hyphens`, `line-clamp-*` 🟡 **re-sized.**

⚠ **Two of the five closed and the other three were re-sized against what the first two actually
cost, which is the point of taking them in order.** Both of the closed pair needed *more* than this
section predicted, and in the same direction: the sizing named the stage the feature belongs to and
missed the consumers downstream of it.

- **`word-break` closed, and it is not two new `TextWrapMode` values.** ✅ That was this section's
  prediction and it is wrong, because the two properties are read at *different stages* and
  therefore compose. `overflow-wrap` is consulted in one branch of `LineWrapper` — "nothing fits" —
  so it can never move a break that had somewhere else to go; `word-break` changes which breaks
  exist. Merging them into one enum forces a winner for `word-break: keep-all` with
  `overflow-wrap: anywhere`, which CSS defines and a narrow CJK column actually wants. So
  `WordBreakMode` is its own enum, threaded beside the other one.
  ⚠ **And `break-all` is not "a break at every grapheme boundary"**, which is the cheap
  implementation and breaks four UAX#14 rules at once: a line may not begin with a comma (LB15d), a
  closing bracket (LB13) or a small kana (LB21), nor end with an opening bracket (LB14). It is
  implemented as a *class substitution* — every letter resolves to `ID` — so the letters behave like
  Chinese, which is exactly what the property asks for, and every rule written against the
  punctuation classes goes on holding. `keep-all` is a filter over the finished opportunity list.
  The pair landed together, as this section said it must; `break-normal` emits both of Tailwind's
  declarations now and its value gap closed with them.
- **`indent-*` closed, and the caret warning was the load-bearing half.** ✅ `LineWrapper.Wrap` takes
  an `indent`, and the first line is measured against `maxAdvance - indent` while every line after it
  is measured against the full width. The offset then travels on the line as `TextLine.Offset`,
  which is *inside* `PenOf`, `Place`, `CaretOffset` and `CaretIndexAt` — so the draw list, the caret
  and the hit test honour it without knowing it exists, which is the only arrangement where it cannot
  be right in one place and wrong in another. What the sizing missed: `TextLayout.Width` has to
  maximise over `Offset + Width` or a shrink-to-fit box comes out an indent too narrow; the
  alignment has to *subtract* the indent from the room rather than add it to the result, or a
  right-aligned first line hangs over the edge; and `Ellipsized` has to allow for it. Asserted as a
  caret round trip on both the indented line and the one below it, and a sabotage of either constant
  fails it.
- **`tab-*` is re-sized upwards, by about three times, and the earlier estimate named a pass that
  reaches neither the glyphs nor the caret.** "A pass between shaping and wrapping that resets the
  pen" fixes where the lines break and nothing else: `TextRun.Place` walks the shaped glyphs,
  `TextRun.CaretOffset` delegates to `ShapedText`, and `TextRun.Width` is `Shaped.Advance * Scale`.
  A tab's advance is *position-dependent*, and every one of those is a prefix sum over advances that
  are not. Left there, a tab breaks in the right place, draws in the wrong one, and puts the caret a
  tab-width out on any line holding one — the failure `text-transform` is split off to avoid.
  ⚠ **The clean seam is `TextLine`'s pen array**, which already exists for mixed fonts and is what
  all four consumers are expressed in terms of: split a run at each tab, give the line a stop size
  and a per-run width override, suppress the tab run's glyph. Plus the wrapper's circular problem —
  a tab's width depends on where its line starts, and where a line starts depends on widths. Two to
  three days, with the caret asserted the way `indent-*`'s was.
- **`hyphens` splits by keyword, and the measurement is new.** `LineBreaker.Opportunities` on
  `"sup"` + U+00AD + `"ply"` returns `[4, 7]` — byte-identical to `"sup-ply"` — so a soft hyphen
  already offers a break. ⚠ **And the hyphen is not drawn.** U+00AD is `Default_Ignorable`,
  `TextShaper` sets `RemoveDefaultIgnorables`, and the string shapes to six glyphs for seven
  characters *even though the face has a soft-hyphen glyph*. So Vixen breaks `sup|ply` and shows
  nothing there, which is `hyphens: manual` with its visible half missing — a defect rather than a
  gap, and the smallest real piece of work in this bucket: substitute U+2010 for a trailing U+00AD
  when `UiElement.Wrap` re-shapes a line, one character for one, so no index moves. `none` is then a
  suppression filter on the opportunity list, which is the shape `keep-all` just landed in and about
  the same size. Half a day for the pair. **`auto` stays refused**, unchanged: it needs a
  per-language Liang/TeX pattern set *and* a language to choose it with, and `TextShaper` leaves
  HarfBuzz's language unset on purpose so that shaping does not depend on the machine's locale.
  Closing `none` and `manual` alone leaves the root `partial` with `auto` named, which is why it was
  not done on this pass.
- **`line-clamp-*` is unchanged in its blocker and smaller in its remainder.** Re-measured rather
  than re-asserted: `-webkit-box` is not among `LayoutStyleBuilder`'s eight `display` keywords —
  flex, none, block, grid, inline, inline-block, inline-flex, flow-root — and `-webkit-box-orient` is
  interned by nobody, so two of the utility's four declarations are still a box model Vixen does not
  have. What got cheaper is the clamp: `TextLine` carries an `Offset` now and `Ellipsized` already
  truncates a line with the marker, so "draw the first N and mark the last kept one" is a budget
  parameter on a path that exists. ⚠ What is unresolved is the *measurement* — a clamped block
  reports N lines, so the budget reaches `TextLayout.Measure` and `Block`'s cache key, and it would
  be the first thing in this engine whose measured height is not its content's.

### Bucket 4 — one missing input, shared. `font-variant-numeric` ✅ **closed and composed**; `font-features-*` ✅ **closed, and the blocker was in neither place this bucket named.**

⚠ **The blocker was one argument in one call and it is gone.** `TextShaper.ShapeRun` ended
`font.Shaper.Shape(buffer, [])`; it takes a `FontFeatureSet` now, resolved once per style pass in
`UiDocument.ResolveText` beside `line-height` and `letter-spacing`. Every keyword of
`font-variant-numeric` is one OpenType feature — `ordn`, `zero`, `lnum`, `onum`, `pnum`, `tnum`,
`frac`, `afrc` — so the high-level property is a table and the low-level one is a parser for the same
thing, exactly as this section said.

⚠ **`ShapingCache`'s key was the half that would have shipped broken, and it is worth stating what
the failure looks like.** The key was the font and the string, which was right for as long as shaping
was a function of those two. Without the feature set in it the second paragraph is a *hit*: a table
of tabular figures beside a paragraph of proportional ones silently shares whichever was shaped
first — a correct-looking answer that depends on draw order and is invisible on either label alone.
`FontFeatureTests.The_cache_does_not_serve_one_feature_set_from_another` asserts the **miss count**,
which is the only thing that can tell a right answer from a lucky one.

⚠ **And the probe needed a *font*, which is the ninth instance of this document's most repeated
lesson and the first time it arrived as a font rather than as a shape or a control state.** Not one
of the twenty-two Consortium faces implements a numeric feature, and no scene's text contains a
digit — so the family would have measured inert with the reader finished, the array threaded, and
HarfBuzz correctly ignoring a tag the face has never heard of. The `figured` scene registers Open
Sans, linked from where the editor already ships it, and gives it `0123456789` to apply them to.

**✅ The value gap this section recorded — two of them on one element keep the last — is closed, and
it was a silent wrong answer rather than a refusal.** Each class emitted the whole property, so
`class="tabular-nums slashed-zero"` kept whichever declaration the cascade picked second and the
other did nothing: no diagnostic, no unrecognised candidate, nothing for an author to look up. The
eight write `--tw-*` fragments now and each emits the same assembled `font-variant-numeric` beside
it.

⚠ **Five fragments for eight classes, which is CSS's grammar rather than a compression.** CSS
Fonts 4 § 6.6 takes at most one keyword from each of the figure, spacing and fraction sets, plus the
two independent flags — so `lining-nums oldstyle-nums` is not something an author can mean, and a
fragment per class would let both be set and emit an invalid declaration. A fragment per *set* makes
the later class win inside its set and leave the others alone. v4 does the same, for the same reason.

⚠ **And their initial value is the empty string, the first in `UtilityComposition` with no identity
to fall back to.** A translation unset is `0px` and a scale unset is `1`, because those properties
need a value; this one needs a *shorter list*, and CSS's spelling of "no tokens at all" is the empty
fallback `var(--tw-ordinal,)` — which `VarSubstitution` already distinguishes from a missing one.
`CompositionTests.Every_fragment_carries_an_initial_value` asserted a non-*empty* initial and had to
be corrected: the invariant that makes a reference safe is the comma, never the text after it.
`normal-nums` stays a whole declaration, because `normal` is the one keyword CSS forbids beside any
other.

**✅ `font-features-*` is registered, and the reason it was not turned out to be in neither of the
two places this section named.** The row said the remaining cost was "class-name escaping in
`UtilityConsumptionProbe.Emissions` and in the generator's selector, plus the one-line registration".
Both escaping claims are **refuted, measured**: `UtilityGenerator.Escape` already backslashes a
quote, the probe puts the class on the element directly rather than through markup, and the very same
selector matches perfectly well when its rule is not inside a cascade layer.

⚠ **The blocker was `LayerRuleParser.FindMatchingBrace`, two assemblies away and nothing to do with
this family.** It skips strings so that a `content: "}"` cannot cut a layer in half, and it did not
know that a backslash escapes the character after it *outside* a string — which is exactly what a
generated class name is made of. So the `\"` in `.font-features-\["onum"_1\]` opened a string that
ran to the quote in the declaration, the braces after it were counted inside a string that was not
there, and the layer body was cut in the wrong place. ⚠ **The rule survived with an empty value
rather than failing**, which is why nothing reported it in a year — and every arbitrary value
carrying a quoted string went through it, `content-['x']` and `bg-[url("a.png")]` as much as this
family.

**What was true is the instrument half.** v4's root is arbitrary-only — there is no
`font-features-tnum` — so the family enumerates nothing into `UtilityFamilies.Surface`, and ⚠ **a
family with no surface is one `UtilityConsumptionGateTests` never meets**: it would pass vacuously,
for ever, while the ledger's emission column stayed empty and the row read `absent`. `ValueKind`
carries a `FontFeatures` kind whose probe is `["onum"_1]`, exactly as `Placement`'s is `[25%_75%]`.
⚠ `onum` and not `tnum`, because the probe's face already draws tabular figures — the probe would
have measured a read property inert, which is `bg-conic-0`'s trap arriving through a font.

⚠ **And the earlier note that the family "cannot be spelled" was already known to be wrong**: the
parser hands the whole prefix over as the name and looks it up verbatim. That is now asserted rather
than measured once, and asserted on the *parser's* answer — `SplitName` would agree today for the
uninteresting reason that the family is registered.


### Bucket 5 — no channel to point a reader at. `font-smoothing`, `scheme`. ⛔ **Refused.**

- **`font-smoothing`** — glyphs are rasterised into a distance field and antialiased by the shader
  from it. There is no coverage-versus-LCD switch anywhere in `Vixen.Ui.Text.Rasterizing` and no
  subpixel filter at all, so `antialiased` and `subpixel-antialiased` are the same picture *by
  construction* rather than by omission. Closing it is an RGB-decimated raster path and a second
  sampler in both executors — a rendering feature, and a very small one to want.
  ⚠ **Re-confirmed 2026-09-05 (#542), and the impossibility is one line rather than an absence.**
  `ui-text.frag` collapses the field to a single scalar `coverage` and writes
  `vec4(colour.rgb * colour.a * coverage, colour.a * coverage)` — one number for all three channels,
  so no value of any property could make them differ. ⚠ **And the atlas's three channels are already
  spent, on something else**: it is a *multi-channel* distance field whose median reconstructs the
  corner a single channel rounds off, which is the trap this refusal has to name. A reader who sees
  `.rgb` in the sampler and concludes a subpixel path is half-built is reading three distances as
  three coverages. Closing this needs a fourth thing to sample, not a switch over the three that are
  there — so the refusal stands, unchanged in shape and now grounded in the shader rather than in the
  rasteriser's file list.
- **`scheme`** — ⚠ **and this is Bucket 2 of Part 9 in its purest form.** `color-scheme` tells a
  *user agent* which schemes an element's UA-rendered widgets, scrollbars and canvas support. Every
  control in Vixen is drawn by the engine from CSS somebody wrote, so there is no UA rendering for
  the property to govern. What `dark:` asks is `prefers-color-scheme`, a media *feature*, which
  `MediaQuery` has read since F11. Part 8 lists `scheme-*` under "costs less than arguing about it";
  that was written before anyone asked what it would do, and the answer is nothing.

### The three already refused, re-measured rather than taken on trust

`font-stretch-*`, `text-shadow-*` and `content-none` were refused earlier with a measurement behind
each. All three were re-measured through the probe on this pass and all three still move no channel
at any value a utility can give them — `font-stretch` at both `50%` and `condensed`, which is the
pair a keyword table would hide. Their rows are unchanged.

⚠ **`font-stretch` re-confirmed again on 2026-09-05 (#541), and what stands is a *face* rather than a
matcher.** Every font committed here was re-scanned for an `fvar` width axis and for
`OS/2.usWidthClass`: 24 files, not one carrying `wdth`, and every single face `usWidthClass = 5` —
`TestGVARFour`'s `cntr` is contrast and is not it. So the width step CSS Fonts 4 § 5.2 asks for, added
to `FontRegistry` beside the `Slanted` search, would have nothing to select and nothing to interpolate:
it would resolve, compute a value, and paint byte-identical pixels. Step 1 is committing a face, and it
gates the other three. ⚠ **And the obligation the issue attaches to closing it is not real.** #541 says
`UtilityConsumptionGateTests` keeps `font-stretch` as its interned-but-unread *control* and that a
replacement must be supplied before the row closes. It does not: that theory's `[InlineData]` was
`word-spacing`, then `text-indent`, and is `tab-size` today, and `font-stretch` appears nowhere in its
history. Being *an instance of the kind of case* that gate controls for — which is what the row's note
says — is not being the control.

### `text-transform` stays split off, and the reason is unchanged

A19 records it and nothing here weakens it: `straße` uppercases to `STRASSE` and `ﬁne` to `FINE`, so
a case mapping changes the UTF-16 **length**, and `TextRun.Start`, `CaretOffset`, `CaretIndexAt`,
`TextLine.Start`/`Length`, `Ellipsized` and `TextField`'s selection are every one of them indices
into the element's own string. The deliverable is the index mapping; the four classes are the easy
part, and shipping them first would put the caret in the wrong place on an editable field, silently.

**Net over the two passes: Typography moves 21 absent → 14, and 9 works → 14.** The first pass moved
the four registrations and left `break-normal` and `text` as `partial`; the second closed
`word-break`, `indent-*` and `font-variant-numeric`, and `break-normal` went `partial → works` when
`word-break` gained a reader and the missing half of its declaration became a real opt-out.
`font-variant-numeric` is `partial` rather than `works` for the composition gap above, and `text`
still is because two of its four keywords are deliberately unregistered — both recorded as value
gaps rather than rounded up.

⚠ **Three of the four sizings in Buckets 3 and 4 were wrong in the same direction, and the pattern is
worth naming because it will recur.** Each one correctly identified the *stage* a feature belongs to
— the opportunity list, the wrapper's width, the shaper's argument — and each one stopped there. What
they missed every time was the set of consumers *downstream* of that stage that read a number the
change makes wrong: `word-break` needed a second enum because `overflow-wrap` is read at a different
stage and composes with it; `indent-*` needed the block's measured width, the alignment and the
ellipsis as well as the wrapper; `font-variant-numeric` needed the cache key; and `tab-*`, still
open, needs three more consumers than its note claimed. **The question a sizing in this area has to
ask is not "where is this read" but "what else reads the number it changes".**

---

## Exit criteria (measured)

1. **Every one of the table's roots is `works`, or carries an open task number, or is one of the four
   exclusions in Part 8.** Checked by regenerating the TSV; the states are computed, not asserted.
2. ✅ **No family emits a property no consumer *acts on***, except entries on the allow-list, each of
   which names a task this document contains. `UtilityConsumptionGateTests` fails otherwise — a test
   rather than `CheckArchitecture`, for the reason Part 5 gives, and "acts on" rather than "interns"
   for the reason Part 0 measured at seven properties. Today: **5 properties, 5 allow-listed** out of
   171 emitted, with 118 acted on and 48 composed — and the allow-list expires on its condition.
3. **`UtilityFamilySupportTests` has a row per root, resolved against a real element**, and its
   `Inert` table is empty or every entry names its task. It is the only artefact in this survey built
   by resolving elements rather than by reading source, and it is where a finding goes to become a
   fact: F1 and F5 were derived from source and are now three `Fact`s in that file.
   ⚠ **This was checked by eye until #604, and what it printed on the day a root was missing was
   `Passed!`.** `Every_registered_root_is_claimed_by_a_row_or_named_here` enumerates the registry
   against the four hand-maintained tables now, and the first run of it found **121 roots with no row
   anywhere** — against the five that #276 had made anybody look for. They are named one by one in
   `UtilityFamilySupportTests.Uncovered`, grouped by the reason they have none (#629); only the first
   group, the `Family.Scope` roots, can never be rows. ⚠ The exemptions are a **list of names and never a
   predicate**, because a rule of the shape "roots whose family has a `Scope` are exempt" is
   satisfied by exactly the defect it is meant to catch. And the walk asserts its own **count** as
   well as its membership: an enumeration that returns nothing agrees with every table ever written.
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
There is no case in any row of that table for a Vixen-specific styling concept, and adding one would be the
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
