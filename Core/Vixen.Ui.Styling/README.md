# Vixen.Ui.Styling

VCSS: stylesheets parsed by ExCSS, and everything after parsing written here. ADR-009 draws the line
exactly there — ExCSS is a parser, not a style engine — and the spike that checked the assumption
before anything was built on it is
[docs/plan/spikes/vcss-excss](../../docs/plan/spikes/vcss-excss/RESULT.md).

## State

**The selector engine is built and its gate is green. The cascade is not.**

| | |
|---|---|
| `StyleTree` | The element store a selector asks questions of: tag, id, classes, attributes, state, links, and the ancestor bloom. |
| `SelectorCompiler` | ExCSS's selector tree → the flat form the matcher runs. A visitor, not a parser. |
| `SelectorMatcher` | Right-to-left matching with backtracking, and the bloom in front of every descendant combinator. |
| `RuleIndex` | Bucketing by the rightmost compound's most selective part. |
| Cascade, `ComputedStyle`, invalidation, transitions | ⏳ next |
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

## The gate

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

## What it found

**A defaulted struct id silently meant "element zero".** `CreateElement(tag, parent = default, …)`
reads well and is a trap: `default(StyleNodeId)` is index 0, a perfectly valid element, so every root
created without an explicit parent became a child of the first element ever made. Four matching tests
disagreed with each other about a tree nobody had built. The parameter is nullable now, and the
reason is written where the next person will hit it.

**Nested selectors interleaved with the ranges being built around them.** A compound reserves a
contiguous run of simple selectors; a `:not()` inside it compiles *its* simples into the same table on
the way past, landing in the middle of that run. Both levels now buffer and flush in one go.

**`:has()` compiled as `:not()` until a test asked.** Both carry an `.Inner`, and pattern-matching on
the shape rather than the type is how a selector ends up meaning the opposite of what it says. That
is the failure mode the compiler's "drop it with a diagnostic" rule exists to prevent, and it very
nearly got past it.

## Deliberately not supported

`:has()` and container queries — [doc 09](../../docs/plan/09-ui-framework.md) marks both P2 and gives
the reason: both are expensive to match *incrementally*, which is the only way a UI can afford to
match at all. Anything else Vixen does not understand is dropped with a diagnostic naming the
selector, never approximated. A rule that silently matches more than it says produces a UI that is
wrong everywhere nobody looked; a rule that does not load produces a message.

Licensed under Apache-2.0.
