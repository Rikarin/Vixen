# Vixen.Ui.Styling

VCSS: stylesheets parsed by ExCSS, and everything after parsing written here. ADR-009 draws the line
exactly there — ExCSS is a parser, not a style engine — and the spike that checked the assumption
before anything was built on it is
[docs/plan/spikes/vcss-excss](../../docs/plan/spikes/vcss-excss/RESULT.md).

## State

**Matching and the cascade are built, and both of their gates are green. Invalidation is not.**

| | |
|---|---|
| `StyleTree` | The element store a selector asks questions of: tag, id, classes, attributes, state, links, and the ancestor bloom. |
| `SelectorCompiler` | ExCSS's selector tree → the flat form the matcher runs. A visitor, not a parser. |
| `SelectorMatcher` | Right-to-left matching with backtracking, and the bloom in front of every descendant combinator. |
| `RuleIndex` | Bucketing by the rightmost compound's most selective part. |
| `StyleSheetLoader` | Rules, `@layer` (Vixen's own — ExCSS does not parse it), `@media` evaluated at load. |
| `CascadePrecedence` | Origin, importance, layer, specificity, source order — as one comparable key. |
| `ComputedStyle` | Immutable, interned, reference-compared. |
| `StyleResolver` | The cascade, inheritance, `var()`, and the style-sharing cache. |
| Invalidation, transitions, keyframes | ⏳ next |
| `Vixen.Ui.Styling.Utilities` | ⏳ its own project |

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
carry — a position pseudo-class, a sibling combinator, or an attribute selector. Coarser than a
browser, which decides per element, and deliberately so: the per-element version wants the
invalidation machinery that is not written yet, and a sharing cache that is subtly wrong is far worse
than no sharing cache.

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
