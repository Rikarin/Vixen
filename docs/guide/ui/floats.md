---
title: Floats and clear
slug: ui/floats
kind: guide
area: Core
summary: CSS 2.1 §9.5 floats over Vixen's layout store — the exclusion list a formatting context carries, why a flow root moves out from under a float and a plain block does not, how clearance spends a margin instead of adding to one, and the half of the feature that is not implemented.
api: [T:Vixen.Ui.Layout.FloatSide, T:Vixen.Ui.Layout.Clear]
tags: [ui, layout, float, clear, css, block]
since: 0.2
status: preview
related: [ui/inline-layout, ui/box-alignment, ui/utility-composition]
---

## What it is

A **float** is a box taken out of normal flow and pushed to one side of its containing block, where
the content that follows it flows *around* it. It is the oldest layout tool CSS has, and it is the
one whose behaviour is least like anything else in the box model: a float is out of flow, and unlike
an absolutely positioned box it still changes where other boxes go.

Setting <xref:Vixen.Ui.Layout.FloatSide> to anything but `None` does four things at once, and CSS 2.1
spreads them over three sections, which is why the property surprises people:

| | Rule |
|---|---|
| §9.7 | The box becomes **block-level**, whatever `Display` says. |
| §9.5 | It is taken **out of flow** — it no longer advances its container's content height directly. |
| §9.4.1 | It becomes a **block formatting context root**, so its margins collapse with nothing and the floats inside it are invisible outside it. |
| §10.3.5 | Its `auto` width becomes **shrink-to-fit** rather than fill. |

The last one is the one people trip over. A box with no width fills its container; float it and it
collapses to the width of its contents.

<xref:Vixen.Ui.Layout.Clear> is the other half. It moves the box that declares it, not the floats it
names: the box's top border edge is pushed down until it is below the bottom margin edge of every
earlier float on the named side.

## What it is for

Three jobs, and only the first is what the property is famous for.

**Wrapping text around a picture.** ⚠ This is the one that does **not** work yet — see *What is not
implemented* below. It is the reason to know the feature exists and the reason not to reach for it
here.

**Laying boxes out side by side until they run out of room.** A row of floated cards wraps to a new
row when the next one will not fit, without a flex container and without a media query. Flexbox and
grid do this better and are what a new panel should use; floats are what a stylesheet ported from the
web will already contain.

**Making a container hold what is inside it.** A formatting context root's automatic height is tall
enough to contain its floats. That is the whole content of `display: flow-root`, and the reason the
"clearfix" hack existed before the keyword did.

## Using it

Both properties are one setter each, and both take the physical CSS 2.1 keyword:

```csharp compile
using Vixen.Ui.Layout;

public static class FloatedSidebar {
    public static LayoutNodeId Build(LayoutTree tree) {
        // A flow root, so that it contains the float rather than collapsing to nothing.
        var page = tree.CreateNode();
        tree.SetDisplay(page, Display.FlowRoot);
        tree.SetDimension(page, Dimension.Width, StyleLength.Points(300));

        var sidebar = tree.CreateNode();
        tree.SetDisplay(sidebar, Display.Block);
        tree.SetFloat(sidebar, FloatSide.Left);
        tree.SetDimension(sidebar, Dimension.Width, StyleLength.Points(100));
        tree.SetDimension(sidebar, Dimension.Height, StyleLength.Points(80));
        tree.AddChild(page, sidebar);

        // An ordinary block: its border box ignores the float and starts at x = 0.
        var body = tree.CreateNode();
        tree.SetDisplay(body, Display.Block);
        tree.SetDimension(body, Dimension.Height, StyleLength.Points(40));
        tree.AddChild(page, body);

        // A formatting context root: §9.5 forbids the overlap, so this one narrows beside the float.
        var aside = tree.CreateNode();
        tree.SetDisplay(aside, Display.FlowRoot);
        tree.SetDimension(aside, Dimension.Height, StyleLength.Points(20));
        tree.AddChild(page, aside);

        // Clearance: below the float whatever the margins would otherwise have said.
        var footer = tree.CreateNode();
        tree.SetDisplay(footer, Display.Block);
        tree.SetClear(footer, Clear.Both);
        tree.SetDimension(footer, Dimension.Height, StyleLength.Points(30));
        tree.AddChild(page, footer);

        return page;
    }
}
```

In a `.vcss` the same thing is the CSS you would expect:

```css
#page    { display: flow-root; width: 300px; }
#sidebar { float: left; width: 100px; height: 80px; }
#aside   { display: flow-root; height: 20px; }
#footer  { clear: both; height: 30px; }
```

The utility classes are `float-left`, `float-right`, `float-start`, `float-end`, `float-none`,
`clear-left`, `clear-right`, `clear-start`, `clear-end`, `clear-both` and `clear-none`. The two
`-start` / `-end` pairs emit the flow-relative `inline-start` / `inline-end`, which are the left and
the right in LTR and the other way round in RTL.

## Examples

### A formatting context root moves and a plain block does not

This is the rule that is not widely known, and it is the difference between the `body` and the
`aside` above. A float overlaps the border box of an ordinary block-level sibling and shortens only
the *line* boxes inside it; a sibling that establishes a formatting context of its own may not
overlap the float's margin box at all.

```csharp no-compile="a fragment; `tree` and `note` are the ones built above"
// Text wraps around the float; the box's own border box still starts at x = 0.
tree.SetDisplay(note, Display.Block);

// The box itself is pushed clear of the float, and narrows to what is left.
tree.SetDisplay(note, Display.FlowRoot);
```

If the box has a stated width and that width does not fit in the band beside the float, it goes
*below* the float instead of narrowing.

### Clearance is not a margin

`clear` inserts **clearance** between the box's top margin and its top border, and clearance behaves
differently from a margin in two ways that matter.

**It spends the margin rather than adding to it.** A box whose top margin had collapsed all the way
out through its container's top edge has a hypothetical position of zero, so clearance replaces the
whole collapsed set rather than being measured from the end of it. A 400-point top margin over a
50-point float puts the box at 50, not at 450.

**It stops a margin collapsing through.** A zero-height box normally lets the margins on either side
of it meet. Give it clearance and it no longer does: the collapsed margin keeps its full length, and
the clearance is inserted into the middle of it, with the box's border edges at the clear point.

## What is not implemented

⚠ **A text leaf does not break around a float's staircase.** §9.5's main clause — a line box is
shortened to the band the floats crossing it leave — landed, along with the shift-downward clause and
a float declared inside a run; what is left is the one piece that is structural rather than
unwritten. A text leaf reaches this store as a measure function and is one atomic item, so a
paragraph beside a float re-flows as whole leaves and a leaf's own first line is not shortened to the
room left beside the float. Breaking inside one would mean a second text wrapper below `Vixen.Ui`
disagreeing with `TextLayout` about UAX #14. ⚠ This sentence used to end "which is the same wall
§10.8's strut is behind", and that comparison is now wrong twice over: the strut's wall was font
*metrics*, which crossed the boundary as five numbers and is down, and this one is text *breaking*,
which is a protocol rather than a value — a measure function answers one size for one width, and a
staircase is a different width per line. They were never the same wall. ⚠ None
of the 92 Chrome-derived fixtures has any text in it, which is how the whole clause survived being
measured for as long as it did; the expectations for the part that landed had to be read out of
Chrome case by case instead.

⚠ **The logical keywords resolve against `Direction`, and nothing else.** `FloatSide.Left` and
`Clear.Left` are CSS 2.1's physical keywords and do not flip — a `float: left` is on the left in an
RTL container too. `FloatSide.InlineStart` / `InlineEnd` and their `Clear` counterparts are CSS
Logical Properties' flow-relative pair and do flip, and they exist *because* the physical pair does
not. This used to be refused on the grounds that resolving one needs a writing mode the store does
not carry; it needs the writing mode **and** the direction, and with no vertical writing mode the
inline axis is horizontal in every configuration the engine can be in.

⚠ **A float-bearing tree pays for the measurement cache.** A cache hit returns a node's size without
re-running its layout, and a block container's layout has the side effect of appending its floats to
the formatting context around it. So the cache is bypassed whenever the tree contains a `float` or a
`clear`, decided by one scan of the style array per pass. A tree with neither is unaffected.

## See also

- [Inline layout](inline-layout.md) — line boxes, and the half of §9.5 that belongs to them.
- [Box alignment](box-alignment.md) — the other rule that turns on which box establishes a
  formatting context.
- [Utility composition](utility-composition.md) — the `float-*` and `clear-*` classes, including the
  flow-relative `-start` / `-end` pairs.
- `Core/Vixen.Ui.Layout.Tests/Taffy/FloatKnownGaps.txt` — empty of failures, and mostly a page about
  why that is a weak result.
- `Core/Vixen.Ui.Layout.Tests/InlineKnownGaps.txt` — the line-box half, with the shape of the fix.

### What the corpora do and do not cover

92 Chrome-derived fixtures cover floats and all 92 pass: the 84 in `Taffy/Corpus/float.xml` and 8
`block_flow_root_*_float` families in the block corpus. All 16 RTL variants pass, and both
box-sizing variants of each.

⚠ `grep -c '<text' Taffy/Corpus/float.xml` is **0**. The corpus named after the feature is entirely
block-level, so the 92 are evidence about placement, clearance, containment and formatting-context
avoidance, and evidence about nothing else. There is no oracle anywhere in the 5 524 fixtures for a
float beside inline content.
