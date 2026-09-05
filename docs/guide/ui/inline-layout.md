---
title: Inline layout
slug: ui/inline-layout
kind: guide
area: Core
summary: Line boxes over Vixen's layout store — inline-block and inline-flex, shrink-to-fit sizing, baseline alignment and vertical-align, and the one invariant an inline formatting context asks the store to give up.
api: [T:Vixen.Ui.Layout.VerticalAlign, T:Vixen.Ui.Layout.TextAlign, T:Vixen.Ui.Layout.LayoutFragmentEnds]
tags: [ui, layout, inline, css, line-boxes, baseline]
since: 0.2
status: preview
related: [ui/grid-layout, ui/box-alignment, ui/utility-composition, ui/markup-panels]
---

## What it is

An **inline formatting context** is the layout store's fourth algorithm, after flexbox, block and
grid. Where those three each arrange a container's children as *boxes*, an inline formatting context
arranges them onto **line boxes**: items are placed along the inline axis until one does not fit, a
new line begins, and everything on a line is aligned vertically against a shared **baseline**.

It arrives with three new `Display` keywords and one new property:

| Keyword | Outer display | Inner display |
|---|---|---|
| `Display.Inline` | inline-level | flow |
| `Display.InlineBlock` | inline-level | flow (a block container) |
| `Display.InlineFlex` | inline-level | flex |

CSS Display §2.1 splits `display` into an **outer** type — how a box relates to its siblings — and an
**inner** type, which is the algorithm that runs inside it. The two are genuinely independent, and
that is the whole shape of this feature: `inline-flex` is *the flex algorithm you already have*, in a
box that shares its line instead of taking one.

⚠ **`inline-block` exists to not take the whole line, and that is why it was left unmapped for two
plan items rather than aliased onto `Block`.** A block-level box with `width: auto` fills its
containing block (CSS 2.1 §10.3.3). An inline-level one resolves the same `width: auto` by §10.3.9's
**shrink-to-fit** — it is as wide as its contents — and sits beside whatever comes before and after
it. An alias would have looked like support and behaved like a bug.

## What it is for

Anything that has to sit *in a row with other things and be no bigger than it needs to be*: a badge
beside a label, a row of chips that wraps, an icon that lines up with the text next to it, a group of
inline controls that flow onto a second line when the panel narrows.

Reach for **flexbox** when you want to control distribution and growth along one axis. Reach for
**inline** when the natural behaviour you want is "these are as wide as their contents, they sit next
to each other, and they wrap when they run out of room" — which is a paragraph's behaviour, applied
to boxes.

⚠ **A container flows rather than stacks when *every* one of its in-flow children is inline-level.**
It is a question about the children, not about the container: the same `display: block` box stacks or
flows depending entirely on what is in it.

⚠ **Mixed content is not an exception to that, it is CSS 2.1 §9.2.1.1.** A container holding both
kinds of child stacks — but each *run* of its inline-level children is wrapped in an **anonymous
block box** and flowed onto lines inside it, so `text`, `<p>`, `more text` is three block-level
boxes and the two runs each get their lines. An anonymous block box has no node and takes initial
values for everything, so nothing is painted for it and nothing addresses it; all you see is that
the boxes in a run share a line and the container is as tall as the runs made it.

## Using it

Set the display keyword on the children; the container needs nothing but `block`.

```csharp compile
using Vixen.Ui.Layout;

public static class ChipRow {
    public static LayoutNodeId Build(LayoutTree tree) {
        var row = tree.CreateNode();
        tree.SetDisplay(row, Display.Block);
        tree.SetDimension(row, Dimension.Width, StyleLength.Points(200));

        // Three chips, each as tall as it says and as wide as it says. Give them no width and
        // §10.3.9 makes each one as wide as its own contents instead.
        foreach (var width in new[] { 40f, 60f, 50f }) {
            var chip = tree.CreateNode();
            tree.SetDisplay(chip, Display.InlineBlock);
            tree.SetVerticalAlign(chip, VerticalAlign.Top);
            tree.SetDimension(chip, Dimension.Width, StyleLength.Points(width));
            tree.SetDimension(chip, Dimension.Height, StyleLength.Points(20));
            tree.AddChild(row, chip);
        }

        tree.CalculateLayout(row, 200f, float.NaN, Direction.Ltr);

        return row;
    }
}
```

From a stylesheet, the same three keywords and `vertical-align` cross the bridge:

```css
.row  { display: block; width: 200px; }
.chip { display: inline-block; height: 20px; vertical-align: top; }
```

### Baselines

A line's baseline is the deepest **ascent** among the boxes aligned to it, and each box's ascent is
the distance from its top edge down to its own baseline. For an atomic inline that baseline comes
from CSS 2.1 §10.8.1:

- a box with line boxes of its own uses its **last** line box's baseline — last, not first, which is
  why a two-line `inline-block` pushes its neighbours *down*;
- ⚠ **unless it clips.** A box whose `overflow` is anything but `visible` synthesises its baseline at
  its **bottom margin edge**, because content that can scroll away has no business anchoring the line
  outside it. Cards, badges and chips almost always declare `overflow: hidden`, so this branch fires
  constantly.

### `vertical-align`

Three of the eight values are implemented, and the split is not arbitrary — it is exactly the line
between values defined against the **line box** and values defined against a **font**:

| Value | State |
|---|---|
| `Baseline` | **done** — the initial value |
| `Top`, `Bottom` | **done** — measured from the line box's edges; they grow the line without moving its baseline |
| `Middle`, `TextTop`, `TextBottom`, `Sub`, `Super` | **refused** — each is defined against the parent's *strut* |

⚠ A strut is font metrics, and `Vixen.Ui.Layout` has no font: it is a geometry store, and
`FontRegistry` lives a layer out in `Vixen.Ui`. The five are dropped at the stylesheet bridge rather
than approximated, because rounding `middle` to `baseline` looks almost right and reads as a
rendering quirk.

### `text-align`

Where the items on a line box sit along the **inline** axis, which is the half of the CSS property
that <xref:Vixen.Ui.Layout.LegacyTextAlign> is not:

```csharp no-compile="a fragment; `panel` is a container whose children are inline-level"
tree.SetTextAlign(panel, TextAlign.Center);
```

| Value | State |
|---|---|
| `Start`, `End` | **done** — resolved against `direction`, like every other logical edge |
| `Left`, `Right` | **done** — physical, and they do **not** flip with `direction` |
| `Center` | **done** |
| `justify` | **refused** — dropped at the stylesheet bridge |

⚠ **The slack it distributes is the *line's*, not the container's.** A line beside a float has less
of it, so a centred line centres in the band the float left rather than in the content box — and
negative slack is left alone, so content wider than its line still overflows past the end edge.

⚠ **`justify` is refused rather than aliased**, for the reason the five font-relative
`vertical-align` values are: it asks for the slack to be spread *between* a line's words, and a text
leaf is one atomic item here. Spreading it between whole inline-level boxes instead would look
convincing and be a different feature. Dropped at the bridge, so the value falls back to `Start` —
which is where CSS puts a justified block's last line anyway.

⚠ **This moves boxes, never glyphs.** Text inside a leaf is aligned a layer out, by `Vixen.Ui`'s
`TextAlignShift`, because that needs the shaped line's width and this store has no font. The two
compose the way CSS says: a centred line box holding a shrink-to-fit leaf whose own lines are
centred inside it.

### Fragmentation: when one node is several boxes

Every other algorithm in this store preserves an invariant it never had to state: **one node
produces one box**. A `LayoutResult` holds one rectangle, and that is what makes a hundred thousand
nodes four allocations.

⚠ **A non-replaced `inline` box is the exception.** CSS Display §2.2 *fragments* a `span` that
crosses a line break into one box per line — with the horizontal border and padding drawn at the two
real ends and **not** at the breaks. A `span` with box children that wraps is therefore several
rectangles, and asking for them is two calls:

```csharp no-compile="a fragment; `span` is a node the caller made and `Paint` is the caller's own"
for (var i = 0; i < tree.GetFragmentCount(span); i++) {
    var (left, top, width, height, ends) = tree.GetFragment(span, i);

    // `left` and `top` are relative to the span itself, so add them to wherever the walk has
    // already got to. A box that did not fragment answers 1 and gives you (0, 0, width, height).
    Paint(absoluteLeft + left, absoluteTop + top, width, height, ends);
}
```

`ends` is a <xref:Vixen.Ui.Layout.LayoutFragmentEnds>: `Start`, `End`,
`Both`, or `None` for a middle fragment with a line break on either side. The *rectangle* already
includes the border and padding at whichever ends are real, so a background needs no reference to
the flag; what the flag decides is **which vertical border to stroke**, and drawing both on every
fragment is what a naive painter does.

⚠ **Everything that does not care keeps working unchanged.** `GetLeft`, `GetTop`, `GetWidth` and
`GetHeight` return the **union** of a fragmented box's rectangles — which is not a compromise but
CSS 2.1 §10.1's own answer, since the containing block of an absolutely positioned descendant of an
inline box *is* the bounding box of its first and last fragments. `GetFragmentCount` answers `1` for
every ordinary node.

### Limits

Fragmentation is one level deep: a `span` **inside** another `span` is still laid out atomically,
and so is one with an out-of-flow child. Both are limits of the walk rather than of the
representation, and both are written up in `Core/Vixen.Ui.Layout.Tests/InlineKnownGaps.txt`.

Also absent, each with its reason in the same file: generated `::before`/`::after` boxes, the strut,
`white-space`, `text-overflow: ellipsis`, `line-clamp` and bidirectional reordering.
⚠ A generated box is the *opposite* direction from fragmentation — a box with no node behind it —
and the fragment arena does not help. Anonymous block boxes were the other half of that direction
and have landed; what a generated box still needs is a **style** of its own, which is a second style
slot rather than a second rectangle.

⚠ **Text is not re-wrapped here.** `Vixen.Ui`'s `TextLayout` already breaks a string into lines across
a font-fallback chain and reaches the store the way every leaf does — as a measure function. This
algorithm treats such a leaf as one atomic item and asks it exactly the question the measure cache is
keyed on. A second wrapper would disagree with the first about kerning, fallback and UAX #14 the
moment either changed. The cost is stated rather than hidden: a text leaf's first line is not
shortened to the space left on the line it lands on. ⚠ That is still true now that fragmentation has
landed, and the two were filed as the same blocker but are not: there is somewhere to put a
shortened first line, and the reason it was refused was never storage.

## Examples

Three inline-level boxes on one line, the middle one a flex container — re-expressed from
`web-platform-tests`' `css/css-flexbox/inline-flex.html`:

```csharp no-compile="A fragment: `InlineBox` is this page's shorthand for the four setter calls above, and `root` comes from the same elided setup."
var first  = InlineBox(tree, root, Display.InlineBlock, 50f, 50f);
var middle = InlineBox(tree, root, Display.InlineFlex,  50f, 50f);
var last   = InlineBox(tree, root, Display.InlineBlock, 50f, 50f);

// ⚠ CSS's initial flex-direction is `row`; this store's is `column`.
tree.SetFlexDirection(middle, FlexDirection.Row);

tree.CalculateLayout(root, 300f, float.NaN, Direction.Ltr);

// 0, 50, 100 — one line, and the flex container still lays out inside.
```

Boxes of unequal height hanging from a common baseline. Each synthesises its baseline at its own
bottom edge, so their **bottoms** line up — which is why a row of differently-sized badges sits level
along its underside:

```csharp no-compile="The same fragment and the same elided helper."
var short_    = InlineBox(tree, root, Display.InlineBlock, 20f, 10f);  // top = 20
var tall      = InlineBox(tree, root, Display.InlineBlock, 40f, 10f);  // top =  0
var middling  = InlineBox(tree, root, Display.InlineBlock, 30f, 10f);  // top = 10
```

Pinning one of them to the top of the line instead, which leaves the baseline where the tall box put
it:

```csharp no-compile="One line continuing the fragment above."
tree.SetVerticalAlign(short_, VerticalAlign.Top);     // top = 0, not 20
```

## See also

- [Floats and clear](floats.md) — ⚠ implemented for block-level content only. A line box does
  **not** yet shorten as it passes a float, so text beside one runs under it.
- [Grid layout](grid-layout.md) — the store's third algorithm, and what *it* cost.
- [Utility composition](utility-composition.md) — the `inline`, `inline-block`, `inline-flex` and
  `align-*` utilities.
- `Core/Vixen.Ui.Layout/README.md` — what each algorithm cost the store, in order.
- `Core/Vixen.Ui.Layout.Tests/InlineKnownGaps.txt` — every rule that is absent, and why.
- `Core/Vixen.Ui.Layout.Tests/InlineFormattingTests.cs` — the WPT-derived oracle.
