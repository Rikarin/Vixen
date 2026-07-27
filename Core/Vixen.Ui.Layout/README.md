# Vixen.Ui.Layout

CSS flexbox over a struct-of-arrays node store. Per
[ADR-006](../../docs/plan/01-technology-decisions.md#adr-006--flexbox-port-the-yoga-algorithm-not-the-flexbox-library)
this is Yoga's *algorithm* re-implemented against Vixen's own data model, judged by Yoga's own
conformance suite — not a port of the `ru-ace/Flexbox` library, whose `class Node` with
`List<Node>` children and `class Style` of boxed values is one heap object per node per style per
result. A Blender-class UI has 10⁴–10⁵ nodes; that allocation profile is disqualifying and the
algorithm is the valuable part.

## State

**The store, the public API and the conformance suite are here. The algorithm is not yet.**

| | |
|---|---|
| `LayoutTree` | The store: styles, results, links and flags as parallel `NativeArray`s, plus the tree operations and the whole style surface. ✅ |
| `LayoutStyle`, `StyleLength` | Every length as a `(value, unit)` pair, all nine CSS edges kept apart. ✅ |
| `StyleResolution` | Edge precedence, percentage resolution, box sizing, `flex` shorthand resolution. ✅ |
| `FlexAxis` | Flow-relative to physical translation. ✅ |
| `Vixen.Ui.Layout.Tests/Generated/` | **534 conformance fixtures**, translated from Yoga by `Tools/Vixen.YogaTestGen`. ⏳ committed, not yet compiled |
| `LayoutTree.CalculateLayout` | ⏳ the port itself |

The suite is committed before the implementation deliberately. Sequencing rule 4 in
[doc 14](../../docs/plan/14-roadmap.md) says so in as many words, and the reason is that a red suite
driving an implementation is a completely different experience from writing three thousand lines and
then finding out. It is excluded from compilation by an `ItemGroup` in the test project that says
why; removing that `ItemGroup` is the last step of the port, and until every fixture passes it is a
build error rather than something anyone can forget.

Every expected number in those fixtures came out of a real browser laying out a real HTML fixture.
That is what makes this a *conformance* suite rather than a regression suite, and it is the specific
defence doc 14 names against the failure mode of AI-assisted work — code that reads plausibly and is
wrong.

## The store

Five parallel arrays indexed by a dense `int`:

| Array | What it holds |
|---|---|
| `LayoutStyle` | What was written. ~400 bytes. |
| `LayoutResult` | What was computed, plus the measurement cache. |
| `LayoutLinks` | Parent, and a `(offset, count, capacity)` into the shared child arena. |
| `LayoutNodeState` | Live, dirty, has-new-layout, has-measure-function. |
| `ChildArena` | Every node's child ids, in one array, in power-of-two blocks with free lists. |

Three decisions worth naming.

**Children are a contiguous run, not a linked list.** Doc 09's `LayoutLinks` sketch implies
`firstChild`/`nextSibling`. The algorithm addresses children by index inside its inner loops — a
flex line *is* a range of them — and a linked list makes each of those a walk, turning several O(n)
passes into O(n²) on the widest nodes in the tree.

**All nine edges are stored, including the shorthands.** CSS resolves `margin-left`,
`margin-inline-start`, `margin-horizontal` and `margin` by a fixed precedence at read time, not by
expansion at write time: `padding: 5` then `padding-left: 9` is not the same document as the
reverse, and a store that expanded on write could not tell them apart.

**A style is ~400 bytes, not doc 09's 120.** That estimate was made before the edge shorthands and
the writing-mode-relative pair were counted. A hundred thousand nodes is therefore about 40 MB in
five allocations — against the reference port's several hundred thousand heap objects for the same
tree, which is the comparison ADR-006 was actually making.

## Regenerating the conformance suite

The fixtures are committed because CI has no reference clone. To re-translate after updating the
clone:

```bash
dotnet run --project Tools/Vixen.YogaTestGen -- references/yoga Core/Vixen.Ui.Layout.Tests/Generated
```

It reports every fixture it could not translate and why. Nine are skipped today, all of them
`display: contents`, which is outside the algorithm scope
[doc 09](../../docs/plan/09-ui-framework.md) states.

## Deliberately not here

**CSS Grid**, which doc 09 schedules as a separate algorithm over this same store. It is a harder
specification than flexbox and it does not share the flex line machinery, so it lands as its own
piece rather than as a variation on this one.

**Parallel layout.** Independent subtrees with a fixed available size are jobs, and text measurement
of siblings is where the win is. It needs the algorithm first, and it needs a measurement of the
serial version to beat.

Licensed under Apache-2.0.
