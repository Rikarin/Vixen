---
title: Grid layout
slug: ui/grid-layout
kind: guide
area: Core
summary: CSS Grid over Vixen's layout store — track sizing functions, minmax and fr, automatic repetitions, item placement and spans, and the one thing grid needed from the store that flexbox and block did not.
api: [T:Vixen.Ui.Layout.GridTrackSize, T:Vixen.Ui.Layout.GridSizingFunction, T:Vixen.Ui.Layout.GridSizingKind, T:Vixen.Ui.Layout.GridPlacement, T:Vixen.Ui.Layout.GridPlacementKind, T:Vixen.Ui.Layout.GridAutoFlow, T:Vixen.Ui.Layout.GridAutoRepeat]
tags: [ui, layout, grid, css, tracks, placement]
since: 0.2
status: preview
related: [ui/utility-composition, ui/markup-panels]
---

## What it is

`Display.Grid` is the layout store's **third algorithm**, after flexbox and block. A grid container
places its children into a two-dimensional set of **tracks** — columns and rows — sizes those tracks
by what is in them, and then aligns each item inside the rectangle it landed in.

It implements CSS Grid §7 (the track sizing functions), §8 (placement) and §12 (the track sizing
algorithm) against the same struct-of-arrays store the other two algorithms run on. Nothing about
the child arena, the dirty propagation, the measure cache or the rounding pass changed to make room
for it.

⚠ **A track is a minimum and a maximum, never a size.** §12 grows a *base size* and a *growth limit*
towards each other through five numbered phases, and the used size only exists at the end. That is
why `100px` is stored as `minmax(100px, 100px)` and a bare `1fr` as `minmax(auto, 1fr)` — the second
being the one people are surprised by, and the reason a `1fr` column refuses to shrink below its
content.

## What it is for

Two-dimensional layout that flexbox cannot express: anything where a thing in one row has to line up
with a thing in another. An inspector whose labels share a column with labels three rows down, a
toolbar whose sections keep their widths as the window resizes, a gallery whose columns are "as many
150-point ones as fit".

Reach for flexbox when the children are a sequence that wraps. Reach for grid when the *container*
owns the shape and the children fill it in.

## Using it

Tracks are set on the container, as a span of <xref:Vixen.Ui.Layout.GridTrackSize>:

```csharp compile
using Vixen.Ui.Layout;

public static class SidebarGrid {
    public static LayoutNodeId Build(LayoutTree tree) {
        var root = tree.CreateNode();
        tree.SetDisplay(root, Display.Grid);
        tree.SetDimension(root, Dimension.Width, StyleLength.Points(300));

        // A 100-point sidebar, then whatever is left over.
        tree.SetGridTemplateColumns(root, [
            GridTrackSize.Single(GridSizingFunction.Points(100)),
            GridTrackSize.Single(GridSizingFunction.Flex(1))
        ]);

        // Two rows: as tall as their contents, then a fixed footer.
        tree.SetGridTemplateRows(root, [
            GridTrackSize.Auto,
            GridTrackSize.Single(GridSizingFunction.Points(40))
        ]);

        return root;
    }
}
```

Each of the six sizing kinds is a <xref:Vixen.Ui.Layout.GridSizingFunction>:

| Written as | Built with |
|---|---|
| `100px` | `GridSizingFunction.Points(100)` |
| `50%` | `GridSizingFunction.Percent(50)` |
| `1fr` | `GridSizingFunction.Flex(1)` |
| `auto` | `GridSizingFunction.Auto` |
| `min-content` | `GridSizingFunction.MinContent` |
| `max-content` | `GridSizingFunction.MaxContent` |
| `minmax(a, b)` | `GridTrackSize.MinMax(a, b)` |
| `fit-content(x)` | `GridTrackSize.FitContent(x, isPercent: false)` |

Items are placed with <xref:Vixen.Ui.Layout.GridPlacement>, one property per edge:

```csharp no-compile="a fragment; `tree` and `root` are the ones built above"
var header = tree.CreateNode();
tree.AddChild(root, header);

// grid-column: 1 / span 2  —  Left is column-start, Right is column-end.
tree.SetGridPlacement(header, Edge.Left, GridPlacement.Line(1));
tree.SetGridPlacement(header, Edge.Right, GridPlacement.Span(2));
```

An item that says nothing is placed by the auto-placement cursor, whose direction and appetite come
from <xref:Vixen.Ui.Layout.GridAutoFlow>. Tracks the cursor has to invent take their sizes from
`SetGridAutoRows` / `SetGridAutoColumns`, which are **cycling lists** rather than single values.

## Examples

**As many columns as fit, with the empty ones removed.** The single stored repetition is written
inline and marked; how many times it actually repeats is worked out per pass from the container's
size, because it is not a property of the stylesheet.

```csharp no-compile="a fragment; `gallery` is a node the caller made"
// grid-template-columns: repeat(auto-fit, minmax(150px, 1fr))
tree.SetGridTemplateColumns(
    gallery,
    [GridTrackSize.MinMax(GridSizingFunction.Points(150), GridSizingFunction.Flex(1))],
    GridAutoRepeat.AutoFit,
    autoRepeatIndex: 0,
    autoRepeatCount: 1
);
```

⚠ `AutoFill` and `AutoFit` generate the *same number* of repetitions. The difference is that
`AutoFit` then **collapses** every generated track no item landed in — and a collapsed track takes
its gutter with it. A grid whose items fill every track cannot tell the two apart, which is why an
`auto-fit` mistake hides until the last row is short.

**A negative line counts from the end of the explicit grid**, and it is resolved before any implicit
track exists — so adding a column on the right does not move what `-1` pointed at:

```csharp no-compile="a fragment; `item` is a node the caller made"
// grid-column: -1  —  the last line of the explicit grid, whatever is implicit around it.
tree.SetGridPlacement(item, Edge.Left, GridPlacement.Line(-1));
```

**Dense packing is a different algorithm, not a tie-breaker.** `GridAutoFlow.RowDense` restarts the
cursor from the first line for every item, which fills holes a wide item left behind — and, as
§8.5 says in as many words, may put an item visually before one that precedes it in the document.

## See also

- [Composed utilities](utility-composition.md) — how a stylesheet reaches the layout store.
  ⚠ `display: grid` crosses that bridge today; `grid-template-columns` does **not**, because a track
  list is variable-length and a `LayoutStyle` is a fixed-size struct. See the note below.
- `Core/Vixen.Ui.Layout/README.md` — the store, the conformance corpora, and `GridKnownGaps.txt`,
  which is the honest list of what grid does not yet get right.
- `docs/plan/43-web-styling-parity.md` § B2 — the plan this landed against, and the sizing it was
  given.

### What grid needed that the other two did not

Flexbox needs nothing out of a child's layout but its size. Block needed three *outputs* — a
collapsible margin at each end and a collapse-through flag — because a child's top margin may belong
to its parent.

Grid needed neither of those: it is a barrier to margin collapsing exactly as a flex container is,
so its answer to all three is "my own margin, and no". What it needed is on the **input** side — a
style field that is not a fixed number of bytes. `grid-template-columns` is an arbitrary-length list
and `LayoutStyle` is an unmanaged struct in a `NativeArray`, so the four track-list properties live
in a second arena (`TrackArena`) and the style carries a handle into it, exactly as children live in
`ChildArena`.

That is also why the styling bridge is unfinished: `LayoutStyleBuilder.Build` returns a
`LayoutStyle` and never sees a node id, so there is nowhere in it for a track list to go. Closing
that means a production track-list parser and a second call where the node is in hand.
