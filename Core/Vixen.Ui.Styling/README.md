# Vixen.Ui.Styling

VCSS: stylesheets parsed by ExCSS, and everything after parsing written here. ADR-009 draws the line
exactly there — ExCSS is a parser, not a style engine — and the spike that checked the assumption
before anything was built on it is
[docs/plan/spikes/vcss-excss](../../docs/plan/spikes/vcss-excss/RESULT.md).

## State

**Matching, the cascade, invalidation and transitions are built, and every gate is green.**

| | |
|---|---|
| `StyleTree` | The element store a selector asks questions of: tag, id, classes, attributes, state, links, and the ancestor bloom. |
| `SelectorCompiler` | ExCSS's selector tree → the flat form the matcher runs. A visitor, not a parser. |
| `SelectorMatcher` | Right-to-left matching with backtracking, and the bloom in front of every descendant combinator. |
| `RuleIndex` | Bucketing by the rightmost compound's most selective part. |
| `StyleSheetLoader` | Rules, `@layer` (Vixen's own — ExCSS does not parse it), `@media` evaluated at load, and the conditions it saw kept so a resize can re-ask them. |
| `CascadePrecedence` | Origin, importance, layer, specificity, source order — as one comparable key. |
| `ComputedStyle` | Immutable, interned, reference-compared. |
| `StyleResolver` | The cascade, inheritance, `var()`, and the style-sharing cache. |
| `StyleInvalidator` | What changing one name on one element can reach, derived from the rule set. |
| `StyleUpdater` | The restyle pass, cold and incremental. |
| `StyleValue` | The typed, interpolatable value. Numbers, lengths, colours, keywords, lists. |
| `ColorFunctions` | The arithmetic behind `oklab()`, `oklch()` and `color-mix()`. `StyleValueParser` owns the grammar. |
| `TimingFunction` | `cubic-bezier`, `steps`, and the `spring()` Vixen extension. |
| `Animator` | Transitions and `@keyframes` animations, over the cascade. Lives on `StyleEngine.Animations`; the document supplies the clock. |
| `Vixen.Ui.Styling.Utilities` | ⏳ its own project |

⚠ **`StyleUpdater` is what the frame pass runs, and for two phases it was not.** `UiDocument.Update`
called `StyleEngine.ResolveAll` — the cold pass — so every gate in this project about incremental
restyling was green against an object nothing in the running framework called. It is wired now, and
the gate that says so lives in `Vixen.Ui.Tests` rather than here, because a test that reaches for the
updater directly passes with the wiring deleted. `StyleEngine.ResolveAll` survives as the plain
whole-tree cascade the sharing and cascade oracles are written against.

⚠ **The same thing was true of `Animator`, and of `MediaContext`, and both are fixed — but the pattern
is now three for three and is the thing to read this table with.** Every object in it is finished,
tested and correct; being *called* is a separate fact, held somewhere else, and this project's own
tests cannot see it. `Animator` was constructed nowhere outside `Vixen.Ui.Styling.Tests`, so no
declared transition had ever run. `StyleEngine.Load` has always taken a `MediaContext` and
`UiDocument` passed none, so every `@media` block in every real document was judged against a surface
nought pixels wide — which killed every responsive variant, `dark:` under the media strategy, and
`@media (color-gamut: p3)` outright. Both were found by *measuring a frame* rather than by reading
this project, and both proofs deliberately live in `Vixen.Ui.Tests`
(`TransitionTests`, `MediaContextTests`) for the reason the paragraph above gives: a test that reaches
for the component directly passes with the wiring deleted. See doc 43 F10 and F11.

⚠ **Conditional group rules nest, and this project has always let them.** `LoadMedia` recurses into
the rule it has just matched, so `@media A { @media B { … } }` loads and the two conditions conjoin —
CSS Conditional 5 § 3 — and the same is true through `@layer` in either order. That is worth stating
because doc 43 § D3 recorded the opposite and sized a cascade change against the belief; the thing that
could not nest was `UtilityGenerator`, one layer up, which is why `sm:md:p-4` was dropped. **Nesting
cost the rule representation nothing**: a `StyleRule` still carries no condition, because `@media` is
still evaluated at load and never at match.

⚠ **What nesting does touch is `SetMedia`'s guard, and it stays correct for a reason worth writing
down.** `StyleSheetLoader.MediaConditions` records each condition *individually* rather than recording
the conjunction, and only records the ones the loader actually reached. Both halves matter. Individual
recording means a drag that flips only an inner condition still reloads — the outer one holds at both
ends and would report no change. Recording only what was reached means a condition sealed behind a
false outer one costs nothing, because the block could not have applied either way. The pair is
asserted in `Vixen.Ui.Tests.MediaContextTests`, one test each; the second is the one that fails if the
guard is replaced by "reload on every resize", which the pre-existing no-`@media` test structurally
cannot catch.

## The three ideas

**Right to left.** `.sidebar .row .cell` read left to right means finding every `.sidebar`, then every
`.row` under it — work proportional to the document. Read right to left it asks "is this element a
`.cell`?", which is almost always no and costs one test. Every browser does this and so does this.

**Bucket by the rightmost compound.** Naive matching is O(elements × rules). Each rule is filed under
the most selective thing in its rightmost compound — its id, else a class, else a tag — and an
element is handed only the buckets its own names reach. Thousands of candidates become single digits.
The rightmost compound is the correct key precisely *because* matching runs right to left: it is the
only part guaranteed to be tested against the element itself.

**A 128-bit ancestor bloom.** Matching a descendant combinator means climbing to the root looking for
an ancestor that fits, and in a deep tree that is most of the cost — paid for every rule that happens
to end in `.cell`, nearly all of which will not match. Each element carries a bloom of everything its
ancestors are called; asking it first turns "climb and find nothing" into two loads. A false positive
costs the walk that would have happened anyway; a false negative is impossible. Gecko and Servo do
the same.

## The cascade

Four steps, in this order and no other. **Collect** the candidates the index hands over and keep the
ones that match. **Cascade** them — for each property, the declaration with the highest precedence.
**Inherit** what the cascade did not settle. **Substitute** `var()`, last, because a custom property
can itself be inherited and cannot be resolved before inheritance has run.

Precedence is one comparable key rather than four nested `if`s, because four nested `if`s is how a
subtle tie-break gets left out. Read in order: **origin and importance**, then **layer**, then
**specificity**, then **source order**. Specificity is third — which is the thing most people who
write CSS have backwards, and the reason a utility layer works at all.

The shape worth noticing is the **mirror**. Normal declarations run user-agent → user → author →
inline; important ones run inline → author → user → user-agent. That is not decoration: a player's
accessibility override has to be able to beat a game that also insisted, and if `!important` merely
meant "wins" it could not. Layers mirror the same way, and unlayered styles sit at the strong end of
whichever direction is in force.

`@layer` is Vixen's to parse. ExCSS 4.3.2 predates cascade layers and hands the rule back unparsed
with its text intact — established by the spike, deliberately, before anything depended on it. Both
forms are read here: the statement `@layer a, b;` that fixes the order without contributing rules,
and the block that contributes them.

## Invalidation

Recomputing is not an option: a `DataGrid` restyles when a row is selected, and if that cost a pass
over ten thousand cells the grid would be unusable. So the question is never "what changed" but
"what could a rule have noticed", and that is a static property of the stylesheet — for every name a
rule mentions, does it appear against the element itself, as an ancestor, or before a sibling
combinator, and *what does the far end of that rule test*. The last part is what turns "restyle the
subtree" into "restyle the `.cell`s in the subtree".

Nothing needs to look upward, because Vixen does not support `:has()`. That is the second thing doc
09's P2 decision buys, after match cost.

Then the pass descends, and the stopping rule is the whole design: re-resolving gives back an
*interned* style, so the question at each element is whether the properties a child would have
**inherited** differ. Not whether anything differs — that coarser test is what made selecting one
row restyle its hundred cells, since a highlight setting `background` changes the row and cannot
possibly reach a cell.

Two mechanisms therefore bound what invalidation can do, and they are worth keeping apart. The
dependency map bounds what the *rules* reach; inheritance bounds what a *changed value* reaches, and
no dependency map can see it. A `.selected` that sets `background` touches one element; the same
highlight written with `color` touches the row and every cell, and that is correct.

### `:empty` is not a child count

CSS means "no child *nodes*", and in the DOM a run of text is a node — which is why a paragraph with
words in it is not empty. Vixen hangs text off the element instead of giving it a node
(`UiElement.Text`, and doc 09 records why), so the obvious translation is the wrong one: a `:empty`
that counted children alone would match every label in the document.

Both rules in this repository that wanted `:empty` make that concrete. `node-search-port:empty` and
`node-port-lane:empty` are on leaves whose entire content is text — a search row's port name, a
vector lane's letter — and both are written to hide the element *when there is nothing to say*.
Counting children would have matched them always instead of never: the dead rule replaced by a
backwards one, which is worse.

So `StyleTree` carries a has-text bit beside the child count, `Vixen.Ui` sets it from
`OnTextChanged`, and the matcher reads both. Whitespace counts as text, matching Selectors 3 and —
more to the point — matching the `string.IsNullOrEmpty` test the layout tree already makes
load-bearing for the same element, since two subsystems disagreeing about whether one label has text
is a worse bug than either answer.

Nothing is owed to the invalidator. A text change goes through `Document.Invalidate`, which is a cold
pass, and so does every structural change; the incremental path only narrows class and state
changes, and neither can move `:empty`. What *is* owed is the sharing key: `:empty` asks what an
element holds and the key says only what it is, so it joins position and attribute selectors in
turning sharing off for the sheet.

## Transitions and animations

The cascade works on interned strings and is right to — deciding *which* declaration wins needs no
opinion about what `spring(1, 100, 10)` means. Animation is where that stops being enough, because a
string cannot be interpolated, so `StyleValue` types the values that are actually being animated and
nothing else.

Colours interpolate in **Oklab**, per doc 09, which is what stops a fade to white detouring through
purple. Fading to `transparent` keeps the other endpoint's hue rather than travelling through black —
CSS's rule, applied here rather than in `Oklab.Lerp`, which has no way to know it is looking at a
colour from a stylesheet.

**Springs** are Vixen's extension and are solved in closed form rather than integrated. That buys
more than accuracy: a value depending only on elapsed time cannot drift, so a dropped frame does not
change where the spring ends up. A spring has no duration of its own, so one is derived — the time by
which the oscillation envelope has decayed to a thousandth — which is what lets it sit where CSS
expects a timing function and be driven by the same machinery as every other easing.

**Interrupting a transition** is the case that separates a good implementation from a bad one.
Reversing halfway through a fade starts from where the element actually is, and takes the half-
duration it has left rather than a full one — otherwise moving a pointer on and off a button
repeatedly makes it drift further behind with every pass.

**Several animations per element** run at once: `animation: spin 1s infinite, pulse 2s infinite` is
two, and every `animation-*` longhand is a list matched by position against `animation-name` and
*cycled* where it is shorter. One duration for two names gives both that duration; two for three
gives the third the first one back. Where two animations set the same property, the one closer to
the end of the list wins — and every animation is still asked, because one that says nothing about a
property must not silence an earlier one that does. The running list is matched by *position*, so
changing the second name leaves the first where it was in its cycle.

⚠ **A bare `0` interpolates with a length and takes its unit.** CSS Values 4 makes a zero a valid
length, and ExCSS serialises `0px` back out as `0` — so without that rule `from { width: 0 }` to
`to { width: 100px }` has no midpoint and swaps at the halfway mark, which looks like an animation
that does not run.

Time is passed in, never read. The animator has no clock, which is what lets a test step through a
fade deterministically and what lets the engine drive it from `Vixen.Engine`'s fixed step without
this project knowing that exists.

## The gates

`SelectorOracleTests` is the one [doc 14](../../docs/plan/14-roadmap.md) names for 4b: over four
hundred randomised trees and stylesheets, **the set of rules the bucketed-and-bloomed path finds is
the set a brute-force pass over every rule finds**. Both filters sit in front of one shared matcher,
so the property is exact rather than approximate — and only one direction of failure is a bug. A
filter that is too permissive costs a tree walk; one that is too aggressive silently unstyles the UI.

Two things the oracle cannot say, so they are tested separately:

- **Whether either path is right about CSS.** `SelectorMatchingTests` is what says that — one test
  per selector kind, per combinator, per attribute operator, and for specificity.
- **Whether the index is any use.** An index that returned every rule would pass the oracle. So a
  test builds fifteen hundred rules and asserts an element reaches exactly three of them.

`StyleSharingOracleTests` is the same shape one layer up, for the cascade. Sharing skips the whole
cascade for an element on the grounds that another element with the same key already ran it, so over
three hundred randomised trees and stylesheets, **resolving with the cache produces exactly what
resolving every element separately produces**. Both halves again: a test that the cache actually
fires (one that never hit would pass the oracle), and — the guard that matters most — an assertion
inside the property that sharing was *enabled* for the generated stylesheet, since one position-
dependent rule turns it off and would leave the oracle comparing one code path with itself.

`IncrementalRestyleOracleTests` is the third: after any sequence of class and state changes, **every
element's computed style equals what a pass from scratch would have produced**. Every element, not
just the invalidated ones — the elements an invalidator wrongly skips are precisely the ones it did
not think to look at. `InvalidationTests` is the count half, and doc 14's named gate: toggling a
class restyles exactly N elements. Both halves are needed and neither substitutes for the other. An
invalidator that gave up and restyled everything passes the oracle; one that skipped too much passes
the counts by producing a smaller number.

Cascade ordering has no oracle. CSS Cascading 5 §6 is the specification and `CascadeOrderTests` is
its clauses written as assertions; a browser would be a real oracle and running one inside a unit
test is not a trade worth making. So each test names *one* tie-break and ties every other one.

## What it found

**A defaulted struct id silently meant "element zero".** `CreateElement(tag, parent = default, …)`
reads well and is a trap: `default(StyleNodeId)` is index 0, a perfectly valid element, so every root
created without an explicit parent became a child of the first element ever made. Four matching tests
disagreed with each other about a tree nobody had built. The parameter is nullable now, and the
reason is written where the next person will hit it.

**Nested selectors interleaved with the ranges being built around them.** A compound reserves a
contiguous run of simple selectors; a `:not()` inside it compiles *its* simples into the same table on
the way past, landing in the middle of that run. Both levels now buffer and flush in one go.

**A diagnostic named a type nobody who writes CSS has heard of.** `:has()` was dropped, correctly,
with the message `HasSelector is not supported` — a class name internal to a third-party parser,
handed to someone whose stylesheet says `.bad:has(.x)`. A diagnostic that does not name the thing the
author wrote is not much better than no diagnostic. Every one of them quotes the selector now.

> **Correction.** The commit that introduced this file, and the first version of this section, said
> that `:has()` had been *compiled as* `:not()` — both carry an `.Inner`, so shape-matching rather
> than type-matching would have made a selector mean its own opposite. That reads well and it is not
> true. `HasSelector` and `NotSelector` are siblings under `StylesheetNode` in ExCSS 4.3.2, neither
> derived from the other, so `case NotSelector` never caught a `:has()` and could not have. The
> defect was the message and only the message. The commit message is on a pushed branch and stays as
> written; this is the record that corrects it.

**A range that did not say which arena it indexed silently read the wrong one.** Inline declarations
live in a different store from rules' — a stylesheet reload throws the rules away, and inline styles
belong to elements that outlive it. Both stores held `Declaration`s, so a `DeclarationRange` into one
was assignment-compatible with a range into the other, and the resolver read inline styles out of the
rule store, finding whatever happened to sit at that offset. Every test passed but the one that asked
what an inline style did. `InlineStyleId` is a distinct type now, which makes the mistake
unrepresentable rather than merely fixed — the same remedy, and the same lesson, as the defaulted
`StyleNodeId` above.

**A cascade test was asserting document order and calling it importance.** Flattening every important
origin to one rank left the whole suite green: the test loaded the author sheet before the user one,
so with origins tied the user sheet won on source order and the assertion still held. Found only by
sabotage, and it is the exact failure the file's own header warns about — *a test that asserts a
winner where two rules differ in three respects will pass with two of the three implemented*. The
sheets are loaded in the losing order now.

**An oracle that reached its answer the same way the thing it was checking did.** The incremental
restyle oracle first built its cold reference by *replaying the same mutations* on a second tree.
That reads like a fair comparison and is not one: both sides then reach their final state through
the same mutation code, so anything that code gets wrong is wrong identically on both and the
comparison sees nothing. Deleting the ancestor-bloom propagation in `AddClass` — which breaks
matching outright — left the whole property green. The oracle builds its tree directly in the final
state now, so its blooms are right by construction. **An oracle that shares an implementation with
its subject is not an oracle.**

**A curve solver that terminated on the wrong quantity.** Inverting a cubic Bézier — CSS asks for
*y* at a given *x*, so *t* has to be solved for first — stopped when `|x(t) − x|` was small. That
pins nothing wherever the curve is flat in x, and `cubic-bezier(0, y, 0, y)`, an ordinary slow-start
easing, is exactly that near the origin: `x(t) = t³`, so 1e-6 of error in x is 1e-2 of error in t and
the y that comes back is visibly wrong for the first frames of every transition using it. Bisection
to a tolerance on **t** now. Found by a property test and not by inspection, which is the case for
one — the curves it fails on are a thin slice of the parameter space and every hand-picked easing
passed.

**A comma split that cut a function call in half.** `transition: transform 400ms spring(2, 180, 12)`
splits into entries on commas, and `spring()` has commas inside it — so the naive split produced
three fragments, none of them a timing function. `spring()` is both the reason ExCSS cannot expand
the shorthand *and* the only value in it with commas, so the two findings are the same feature biting
twice. The same shape as matching braces inside an `@layer` body.

**A property test that could not reach the thing it was meant to test.** Every stylesheet the same
generator produced contained a sibling or position selector, which turns style sharing off for the
entire rule set — so a sabotage that left the sharing cache stale across passes sailed through 300
iterations. Sharing-safe stylesheets now get their own property, which asserts sharing was actually
*enabled* before believing anything it observed. Generators need coverage assertions for the same
reason tests do.

## What the front end leaves to Vixen

Four things now, and the pattern is the same each time: ExCSS handles the common case, Vixen owns
the general one, and the seam stops at the loader.

- **`@layer`** — not parsed at all. Both forms are read here, with brace matching that skips strings
  and comments.
- **The `transition` shorthand** — expanded into longhands *only when ExCSS recognises every part*.
  So `transition: opacity 200ms ease-in` arrives as four declarations and
  `transition: opacity 200ms spring(1, 100, 10)` arrives as one unexpanded string. Whether the
  longhands exist depends on whether the author used a Vixen extension, which is not a distinction
  anything downstream should have to know about. Both forms are read.
- **A shorthand holding a `var()`** — expanded by `ShorthandExpansion`, because ExCSS cannot: what
  `border-color: var(--border)` expands *to* is not known until the custom property is resolved, so
  it hands the declaration back whole. ⚠ **The failure that caused was silent.** Everything
  downstream reads longhands — the layout bridge says so in its own remarks, and the draw list asks
  for `border-top-color` by name — so the declaration cascaded, substituted, and arrived under a name
  nothing asks for. Every sheet in the repository writes `border-color: var(--border)`, so no control
  in the framework drew a border: no diagnostic, no missing value, just flat boxes. The expansion is
  at load rather than after substitution so that the cascade still picks between a shorthand and a
  longhand the way it does for a var-free one, and the tests assert against ExCSS's own output rather
  than against a table — the bug was a difference between the two forms, so a test stating what the
  longhands *ought* to be would have agreed with itself and with nothing else.
- **`@keyframes`** — ExCSS *does* parse these, with `from`/`to` already normalised. Established by
  probing rather than assumed, and it saved the work `@layer` needed.

## Where doc 09 was wrong

**The style-sharing key cannot hold the parent's computed style.** Doc 09 specifies
`(tag, class set, inline style, parent computed style, pseudo state)`, and that is unsound. Two
parents can hold the *same* computed style and still be told apart by a selector: given
`.a { color: red }` and `.b { color: red }`, an `.a` and a `.b` intern to one identical style — and
then `.a .row` matches one of their children and not the other's. Keyed on the parent style, both
children share and one of them is wrong.

The key holds the parent **element** instead. Sharing then happens between siblings, and every
descendant and child combinator is sound for free because the two elements have literally the same
ancestor chain. Gecko does this, for this reason. Doc 09 has been corrected.

It is a narrower key, and what that costs is worth being precise about. **Interning** is what gives
every identical grid cell the same `ComputedStyle` reference, and it is untouched — ten thousand
cells across a hundred rows still hold one object, so the reference-compared invalidation doc 09 is
really after still works. **Sharing** is what lets the cascade be *skipped*, and that now happens per
row rather than per grid: a hundred rows of a hundred cells cost 102 cascades rather than 10 001.
Cheaper than being wrong.

Sharing is also refused outright when any rule in the sheet matches on something the key cannot
carry — a position pseudo-class, a sibling combinator, an attribute selector, or `:empty`, which
asks what an element *holds* where the key says only what it *is*. Coarser than a
browser, which decides per element, and deliberately so: the per-element version wants the
invalidation machinery that is not written yet, and a sharing cache that is subtly wrong is far worse
than no sharing cache.

## Colours

Five syntaxes, all decoded to **linear** `Color4` on the way in, because everything past the cascade
works in linear and doing the decode once here is the difference between a correct fade and one that
darkens: hex, the named-colour table, `rgb()`/`rgba()`, `oklab()`/`oklch()`, and `color-mix()`.

The last two are [doc 43](../../docs/plan/43-web-styling-parity.md) § D2 and § D4. `oklch()` is what
makes Tailwind v4's default palette expressible at all — every colour it ships is written that way —
and `color-mix()` is how v4 says "this colour, at half alpha" without rewriting the colour, which is
what the `/opacity` modifier compiles to.

Three things are worth knowing before touching any of it.

**The mix is evaluated after `var()` substitution, and needs no knowledge of it.**
`StyleResolver.Substitute` rewrites the value's *text* during `Build` and re-interns the result;
`StyleValueParser` only ever runs on what a `ComputedStyle` ended up holding. So
`color-mix(in oklab, var(--accent) 50%, transparent)` reaches the parser character-for-character
identical to the same mix written with the hex code in place. ⚠ The consequence that bites is the
other direction: what substitution hands over is `#4f7cff`, not `rgb(79, 124, 255)`, and ExCSS does
not normalise inside a function it does not know either — so a `color-mix()` that accepted only
`rgb()` endpoints would work on every literal and fail on every variable.

**The percentages are an algorithm with a name**, CSS Values 5 § "normalize mix percentages" called
with the force-normalization flag. One omitted is the other's complement; both given and not summing
to 100 are *scaled to* 100, with any shortfall multiplying the result's alpha, so `red 20%, blue 60%`
is a 25/75 mix at 80% alpha; and ⚠ both zero is **transparent black**, not invalid — an older draft
said invalid and the claim is still widely repeated.

⚠ **Nothing is clamped to a gamut, deliberately.** `oklch(0.7 0.4 30)` is outside sRGB and its linear
triple has a negative channel, which is carried through intact. Clipping per channel would shift the
hue — a vivid red clips towards orange — and the correct repair is chroma reduction holding lightness
and hue, against the gamut of the *display*, which this assembly cannot know. Doc 43 § D4 owns it,
and carrying the value costs nothing to change later; clamping here would already have destroyed what
the mapper needs. Nothing downstream produces NaN from it, because the sRGB transfer function is the
exact piecewise one whose linear segment handles negatives.

⚠ **That includes the way back out, and it is the direction that used to fail.** `StyleValue.ToCss`
is how the animator hands an interpolated value back to a cascade that works in interned strings, and
`rgba()` cannot spell a colour outside sRGB — it clamps to `[0, 1]` and quantises to eight bits. So a
colour survived parse, resolve and draw and then lost its gamut the first frame a transition touched
it. It now writes `color(srgb-linear r g b / a)` with round-trippable channels when any linear
channel is outside `[0, 1]`, and keeps the short `rgba()` spelling — one small interned string per
colour — for everything else. The test is on the colour and not on alpha on purpose: a spring
overshoots alpha past `1`, and `rgba()` carries that where `color()` would clamp it.

## What the spike did not say

**ExCSS normalises what it can see, and it cannot see through a `var()`.** `color: red` reaches Vixen
as `rgb(255, 0, 0)`; `color: var(--c)` with `--c: red` reaches it as `red`, because anything
containing a `var()` is left verbatim. Both are correct and they are not the same string, so every
value parser downstream has to accept both forms. Cheap to know now.

## Deliberately not supported

`:has()` and container queries — [doc 09](../../docs/plan/09-ui-framework.md) marks both P2 and gives
the reason: both are expensive to match *incrementally*, which is the only way a UI can afford to
match at all. Anything else Vixen does not understand is dropped with a diagnostic naming the
selector, never approximated. A rule that silently matches more than it says produces a UI that is
wrong everywhere nobody looked; a rule that does not load produces a message.

Licensed under Apache-2.0.

## Reloading a stylesheet

The engine keeps the text of every sheet it loaded, and `Replace` rebuilds the rule set from them.

⚠ **Everything reloads, not just the sheet that changed.** Rules are appended and never removed — an
index, a layer order and a declaration arena all assume it — so a sheet cannot be lifted out of the
middle of a set. Rebuilding is a few milliseconds for a stylesheet a human just typed, and it is the
difference between a reload and an *overlay*: replaying the sheets is what makes a deleted rule stop
applying.

What survives is what elements hold handles to: the name tables the style tree interned its tags and
classes against, the inline-style store, and the tree. What does not is the rule set and everything
derived from it, the interning cache included — so a computed style from before a reload is a
different object from the identical one after, and a caller has to forget what it applied rather
than compare against it.

⚠ **The text the engine keeps is the text it was handed, not the text the parser saw.**
`StyleEngine.Preprocessor` is a transform every sheet goes through on its way to ExCSS, and a reload
runs it again — which is the whole reason it exists as a seam here rather than as something a caller
does before calling `Load`. A caller's transform is applied once and then silently dropped by the
next resize, because `SetMedia` replays `SheetText`. Keeping the original is also what
`HotReloadHost`'s rollback needs: putting a sheet back means putting back what somebody wrote.

The one filler today is `UiDocument`, which points it at `ApplyExpander` so that `@apply` means
something in a hand-written sheet — see `Core/Vixen.Ui/StyleApply.cs`. This assembly deliberately
does not know what a utility is, which is why the seam is a `Func<string, string>` rather than an
interface naming a vocabulary that lives above it.

## Diagnostics go somewhere now

`StyleSheetLoader.Diagnostics` and `SelectorCompiler.Diagnostics` are still what they were: lists of
what could not be loaded and why, appended to as the sheets arrive. What changed is that something
reads them. `UiDocument` drains both onto the log after every load and every reload — event 7004,
`docs/manual/log-events.md` — so an at-rule Vixen does not understand, or a selector it cannot
compile, now says so in the editor's Console panel and in a game's log ring instead of being dropped
in silence.

⚠ **Both lists restart when the engine reloads**, because `Build` constructs a new loader and a new
compiler. A reader that watches them has to key its position on the *object* and not on a count;
`UiDocument.Drain` does, and the reason is written down there.
