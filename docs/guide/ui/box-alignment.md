---
title: Box alignment
slug: ui/box-alignment
kind: guide
area: Core
summary: What an alignment does when what it aligns does not fit — CSS Box Alignment §4.4's safe fallback, across all four of the store's algorithms — and the three legacy text-align keywords, which are the one alignment in this store that names a physical edge.
api: [T:Vixen.Ui.Layout.OverflowAlignment, T:Vixen.Ui.Layout.LegacyTextAlign]
tags: [ui, layout, css, alignment, overflow, text-align]
since: 0.2
status: preview
related: [ui/grid-layout, ui/inline-layout, ui/utility-composition]
---

## What it is

Every alignment property in CSS answers one question — *where in the leftover space does this box
sit* — and every one of them needs a second answer for the case where there is no leftover space.
Two small enums carry those second answers:

| Type | The question it answers |
|---|---|
| <xref:Vixen.Ui.Layout.OverflowAlignment> | What `align-*` and `justify-*` do when the thing they align is **bigger than the space** |
| <xref:Vixen.Ui.Layout.LegacyTextAlign> | Where a **block container** puts its block-level children on the inline axis |

Neither adds an algorithm. `OverflowAlignment` is one comparison at the six places CSS Box Alignment
§4.4 applies; `LegacyTextAlign` is one call where a block child's physical left edge is decided.

⚠ **`OverflowAlignment` has nothing to do with <xref:Vixen.Ui.Layout.Overflow>, despite the word.**
That enum is `overflow-x` and `overflow-y` — whether content is clipped or scrolled. This one is the
first half of an alignment value's grammar, `[ safe | unsafe ]? <position>`.

## What it is for

**`safe` is about reachability, not tidiness.** An alignment that overflows towards the *start* edge
pushes content out of the corner the reader begins at, and no scrollbar goes back for it: a toolbar
whose buttons are centred loses the first ones off the left when the window narrows, and there is no
way to get to them. `safe` spends the overflow at the end instead, where a scroll container can still
reach it. That is the whole of the rule, and it is why the fallback is *start* specifically rather
than "clamp the offset to zero".

**The legacy `text-align` keywords are for the layout `<center>` used to do.** `-webkit-left`,
`-webkit-center` and `-webkit-right` move a block container's child *boxes*, not just the text in
them. Pages depend on them, so browsers keep them; a layout store that reads a real stylesheet needs
them for the same reason.

## Using it

An alignment value is two things, and the setters take both, because CSS writes them as one
declaration and either half alone is only half a value:

```csharp compile
using Vixen.Ui.Layout;

public static class SafeToolbar {
    public static LayoutNodeId Build(LayoutTree tree) {
        var bar = tree.CreateNode();
        tree.SetFlexDirection(bar, FlexDirection.Row);
        tree.SetDimension(bar, Dimension.Width, StyleLength.Points(100));
        tree.SetDimension(bar, Dimension.Height, StyleLength.Points(40));

        // Centre the buttons — but pack them at the start rather than off the left
        // edge once there are more of them than the bar can hold.
        tree.SetJustifyContent(bar, Justify.Center, OverflowAlignment.Safe);

        return bar;
    }
}
```

Six properties carry a prefix, and it lives beside the position rather than inside it:

| Position | Overflow prefix |
|---|---|
| `LayoutStyle.JustifyContent` | `LayoutStyle.JustifyContentOverflow` |
| `LayoutStyle.AlignContent` | `LayoutStyle.AlignContentOverflow` |
| `LayoutStyle.AlignItems` | `LayoutStyle.AlignItemsOverflow` |
| `LayoutStyle.AlignSelf` | `LayoutStyle.AlignSelfOverflow` |
| `LayoutStyle.JustifyItems` | `LayoutStyle.JustifyItemsOverflow` |
| `LayoutStyle.JustifySelf` | `LayoutStyle.JustifySelfOverflow` |

⚠ **A pair, and not four more members of <xref:Vixen.Ui.Layout.Align>.** `safe end` is not a third
place to sit; it is `end` with a condition attached. Folding it into the position enum would put a
new arm in every `switch` that reads one, and each of those would answer `start` by falling through
a `default` — whether or not anything actually overflowed, which is the one case the keyword exists
for.

## Examples

**It asks about the free space, not about the offset.** A `safe` alignment with room to spare is
indistinguishable from an `unsafe` one, which is why this is not a clamp:

```csharp no-compile="a fragment; `tree` and `container` are the ones built above"
tree.SetAlignItems(container, Align.FlexEnd, OverflowAlignment.Safe);

// A 50-point item in a 100-point container has 50 points of room, so `end` means end.
//   → tree.GetTop(item) == 50

// A 150-point item in the same container has −50, so it falls back to the start.
//   → tree.GetTop(item) == 0
```

**The prefix travels with the position it modifies.** A child whose `align-self` is `Auto` inherits
its container's `align-items` *whole*: reading the position from the container and the prefix from
the child would silently drop the `safe`. A child that states its own `align-self` replaces both
halves, so a container saying `safe end` and a child saying `end` is a child that overflows.

**The legacy keywords are physical and do not flip with `direction`:**

```csharp no-compile="a fragment; `panel` is a block container the caller made"
tree.SetLegacyTextAlign(panel, LegacyTextAlign.Left);

// A 100-point child of a 200-point RTL panel sits at x = 100 under CSS 2.1 §10.3.3,
// and at x = 0 under `-webkit-left`. `LegacyTextAlign.None`, the initial value, is
// the only one that consults `direction` at all.
```

⚠ **`LegacyTextAlign` is not `text-align`, and one field could not be.** Plain `text-align: center`
centres a block container's *inline content* and leaves its block-level children exactly where
§10.3.3 put them; only the three legacy keywords move the boxes. A field holding both sets would have
to mean two different things depending on which value was in it, so this one holds only the set the
store implements — and the stylesheet bridge refuses an unprefixed value rather than folding it in.
Distributing the items on a *line box* is still owed, has no oracle in either conformance corpus, and
is `InlineKnownGaps.txt`'s entry.

**An out-of-flow child is not the container's content**, and this is the one asymmetry that looks
like a bug from either side until the alignment *subject* is named. §4.4 falls back when the subject
overflows. `align-self`'s subject is the box itself; `justify-content`'s is the container's in-flow
content, which an absolutely positioned child is not part of — so the line its static position is
read off holds no items, nothing overflows, and `end` is honoured. Chrome agrees, on two fixtures
that are otherwise the same box in the same container:

| Fixture | Declaration | Chrome |
|---|---|--:|
| `absolute_safe_align_self_end_overflow` | `align-self: safe end` | `y = 0` |
| `absolute_safe_justify_content_end_overflow` | `justify-content: safe end` | `x = −100` |

## See also

- [Grid layout](grid-layout.md) — where `justify-self` and `align-self` place an item in its area,
  and where §4.4 is applied on a grid.
- [Inline layout](inline-layout.md) — line boxes, and the `text-align` that is still owed.
- `Core/Vixen.Ui.Layout.Tests/Taffy/UnsupportedFixtures.txt` — the census both of these came out of,
  and why a fixture that asserts nothing is worth telling apart from one that fails.

### What the corpora do and do not cover

76 Chrome-derived fixtures across the flex, grid and block categories cover `safe`, and all 76 pass;
16 cover the legacy keywords, and all 16 pass. Fifteen of the nineteen `safe` families are named
`_overflow`, which is what makes mapping `safe end` onto plain `end` wrong on precisely the fixtures
that test it.

⚠ **Two of the six properties have no fixture at all.** The corpus never writes `safe` on
`align-items` or `justify-items` — the container-level halves, reached by a different line of code
from the four it does exercise. `SafeAlignmentTests` is their only oracle; deleting that line leaves
all 76 corpus fixtures green.
