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

⚠ **`partial` is a fifth state the brief did not ask for, and collapsing it in either direction would
be the same mistake this survey exists to catch.** `border-t-2` is the case that forces it: the layout
reads `border-top-width` and insets the content box, and the draw list paints nothing, because
`DrawListBuilder` takes its one thickness from `Edge.Top` and its one colour from
`border-top-color`. Calling that "works" is the conflation the brief warns about; calling it "inert"
is false, because the box really does get narrower. There are 29 of these and they are the most
expensive rows in the table, because each is a utility that *half* does what it says.

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

### F4 · `display` is `{ Flex, None }` ⚠

`LayoutEnums.cs` — the enum has two members. Seven of Tailwind's 21 display keywords are emitted
(`block`, `inline`, `inline-block`, `flex`, `inline-flex`, `grid`, `hidden`) and **two** are read. The
resolved-element suite proves it the only way that is honest: two children of an element carrying
`block` still sit side by side. This is the root of Track B and the reason `grid-cols-3` is inert
rather than broken — nothing is broken, there is simply no grid.

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
| Two media variants on one utility | nests | dropped | known |

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
container queries as P2 and unsupported. Worse, Vixen's `@media` **does not nest** — which is why two
media variants on one utility are dropped rather than nested — and a container query is a conditional
group rule that must nest inside a media query and inside `@layer utilities`. So the prerequisite is
not `@container` itself but a **conditional-group rule model in the cascade** that can carry a stack
of conditions rather than one. That is the same change that fixes `sm:md:p-4`, and it should be done
once for both.

The evaluation side is cheaper than it looks: a container query resolves against the nearest ancestor
with `container-type`, whose size the layout already computed. It is a second style pass over a
subtree whose containing block changed — the same shape as the invalidation the cascade already does.

**Size: 1.25 EM** — 0.5 for nested conditional groups, 0.5 for `container-type`/`container-name` and
the resolution walk, 0.25 for the variant table and the `@sm/name` grammar.

### D4. oklch, and what it costs

v4's default palette is `oklch()`, 22 hues × 11 steps. Vixen can keep hex tokens and lose nothing
functionally, but three things follow from adopting the v4 shape:

1. `oklch()` must parse — D2.
2. Interpolation in Oklab is already what the animator does, so a transition between two oklch
   colours is correct for free.
3. The gamut question is real and is not the styling layer's: `oklch(0.7 0.2 30)` can be outside
   sRGB, and the UI renderer's swapchain format decides what happens. Clamping in `Color4` is the
   honest default and it is what a browser on an sRGB display does.

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
`**/*.vxml;**/*.vcss` **within the consuming project**, finds `**/vixen.ui.yaml` **within the
consuming project**, and errors if there is more than one — *"One project is one palette."* The
generation target does not even run without a token file. So `Vixen.Editor.Profiler`,
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
is two lines and a file"*, and the file is a second `vixen.ui.yaml`: a second palette, which is the
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
inert, ever since. The same is true of `translate-x-*`, `blur-*`, `fill-*`, `ring-*` and
`select-none`. **Eighteen of the 90 properties the utilities emit reach no consumer after ExCSS
expansion**, and each one is a class somebody can write today that does nothing:

```
--blur  --rotate  --scale  --translate-x  --translate-y
border-bottom-color  border-inline-end-color  border-inline-start-color
border-left-color  border-right-color
fill  stroke  grid-column  grid-template-columns  order
outline-color  user-select  vertical-align
```

It was twenty a week ago; `overflow-x` and `overflow-y` came off it when F3 landed, which is what the
list is for.

That list is the gate task #11 asks for, and it is small enough to be a hard failure rather than a
warning: **`CheckArchitecture` fails when a family emits a property no consumer interns, unless the
property is on an allow-list carrying the task number it is waiting for.** The allow-list is the
honest form of the README's "a utility waiting for an engine feature" — same sentence, but it expires.

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
| A7 🟡 | Transforms: a real `transform` property, decomposed, animatable | layout + draw + `Animator` | **#23** | 0.6 |
| A8 🟡 | `filter` and `backdrop-filter`, blur first | UI renderer | **#28** | 0.75 |
| A9 🟢 | `color-mix()` in `StyleValueParser` | `Vixen.Ui.Styling` | **#12** | 0.25 |
| A10 🟢 | `oklch()`/`oklab()` colour syntax | `Vixen.Ui.Styling` | **#12** | 0.25 |
| A11 🟡 | Backgrounds: `background-image`, gradients, position, size, repeat | `DrawListBuilder`, `UiShape` | — | 0.75 |
| A12 🟡 | Pseudo-elements materialised — `::before`/`::after` with `content` | `StyleRuleSet`, `UiDocument` | — | 0.5 |
| A13 🟢 | The 22 selector-only variants (`empty`, `nth-*`, `*-of-type`, form states) | `Variants`, `ElementState` | — | 0.3 |
| A14 🟢 | The 13 media-feature variants | `MediaQuery` | — | 0.2 |
| A15 🟡 | Nested conditional-group rules — the `sm:md:` fix and `@container`'s prerequisite | cascade | — | 0.5 |
| A16 🟡 | Container queries: `container-type`, the resolution walk, the `@` variants | cascade + layout | — | 0.75 |
| A17 🟢 | `has-*` | `SelectorMatcher` + invalidation | doc 09 P2 | 0.4 |
| A18 🟢 | Scroll properties as `ScrollView` inputs rather than CSS | `Vixen.Ui.Controls` | — | 0.3 |
| A19 🟢 | `text-decoration`, `text-transform`, `font-variant-numeric`, `font-stretch` | `Vixen.Ui.Text` | — | 0.4 |
| | | | **A total** | **6.7** |

### Track B — layout modes

| # | Item | Task | EM |
|---|---|---|--:|
| B0 🟢 | **`Tools/Vixen.TaffyTestGen`** — XML reader, style-attribute map, emitter, plus the 40-line Ahem measure stub. Yields 868 block, 1 960 grid and 2 268 flex fixtures, all Chrome-derived | — | 0.4 |
| B1 🟡 | `display: block` and `inline-block` — block formatting over the existing store, judged by B0's 868 | **#25** | 1.0 |
| B2 🔴 | **CSS Grid** — a separate algorithm; `grid-template-*`, `fr`, `minmax`, `repeat`, `auto-flow`, named lines and areas, placement, `justify/align-items/self`. Judged by B0's 1 960 plus WPT's 510 `check-layout` grid tests | **#27** | 3.5 |
| B3 🔴 | **Inline formatting** — line boxes, inline-block, vertical alignment, `text-overflow: ellipsis`, `line-clamp`. ⚠ The one with no ready oracle: WPT is reftest-only here, and Parley has no ellipsis | **#26** | 3.0 |
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
| C3 🟢 | `@theme` replaces `vixen.ui.yaml`; `ThemeTokens` reads a stylesheet (D1) | 0.5 |
| C4 🟢 | Cross-assembly token sharing, shape C (Part 3) | 0.3 |
| C5 🟢 | The gate: a family emitting an uninterned property fails the build (#11), and `Tools/Vixen.TailwindParity` regenerating the TSV from a committed registry snapshot | 0.3 |
| C6 🟢 | Doc 09's five missing families — `space`, `divide`, `mix-blend`, `origin`, `scroll` | 0.25 |
| C7 🟢 | The ~120 families that are a table line each, once A and B land | 0.75 |
| C8 🟡 | The families that are their own small feature: `mask-*`, gradients, `animate-*` | 0.75 |
| | **C total** | **3.2** |

### Cost

| Track | EM |
|---|--:|
| A — properties | 6.7 |
| B — layout modes | 9.4 |
| C — families | 3.2 |
| **Total** | **19.3** |

⚠ **Two thirds of that is B2 and B3.** Everything else together is about six engineer-months, and it
is the two thirds that decides whether this is a year or a quarter.

⚠ **B0 is the highest-leverage 0.4 EM in the document and it should be built before B1.** It is the
same bet ADR-006 made and won: 530 of Yoga's 534 passed on the first run, and of the four that did
not, one was a real specification rule the port had missed. Taffy's corpus is ten times the size, is
XML rather than C++, and covers the two modes Vixen has no tests for at all. Building grid without it
is choosing to re-run the experiment that already has an answer.

---

## Part 7 — The sequence

**Wave 0 — the survey's own consequences.** C0 (prefix fallback), C5 (the gate and its expiring
allow-list) and the README correction — A5 landed while this was written. Nothing depends on these and everything is
cheaper after them. **0.3 EM.**

**Wave 1 — the token model, before #6 and #7 build the old one.** C3, then C4, then A9 and A10. This
is the one ordering constraint that is urgent rather than logical: task #6 is queued and would
otherwise produce three `.vcss` files that a v4 `@theme` then has to re-do. **1.3 EM.**

**Wave 2 — the cheap properties, in parallel.** A1, A2, A3, A4, A6, A13, A14, A19, C1, C2, C6. Eleven
independent items, no shared file except `DrawListBuilder` between A1–A3. **1.9 EM, parallel.**

**Wave 3 — the two cascade features.** A15 then A16 (`@container`), A17 (`has-*`), A12
(pseudo-elements). Sequential within the cascade; parallel with wave 4. **2.15 EM.**

**Wave 4 — the oracle, then block, then grid.** B0 first and alone: 0.4 EM buys 3 096 Chrome-derived
fixtures for the two modes that have none, and it is cheap enough that finding out it does not work
costs a week. Then B1 — the smaller of the two algorithms, it makes `display` a real enum, and it
proves the store can carry a second algorithm at all. Then B2. B3 and B3a in parallel with B2 if there
is a second pair of hands: they share nothing but the store, and B3 is the one whose *first* task is
building its own oracle. **8.4 EM.**

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
2. **No family emits a property no consumer interns**, except entries on the allow-list, each of which
   names a task. `CheckArchitecture` fails otherwise. Today: 18 properties, 0 allow-listed.
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
