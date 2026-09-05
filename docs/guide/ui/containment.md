---
title: Containment
slug: ui/containment
kind: guide
area: Core
summary: CSS Containment 2's `contain` over Vixen's layout store — why size containment is not "skip the children", how the inline-size half differs from the size half, why paint containment is the clip `overflow` already pushes rather than a second one, and why there is no `style` member.
api: [T:Vixen.Ui.Layout.Containment]
tags: [ui, layout, contain, containment, css, performance]
since: 0.2
status: preview
related: [ui/floats, ui/inline-layout, ui/box-alignment]
---

## What it is

**Containment** is a promise an element makes about its contents so that the rest of the document may
stop consulting them. CSS Containment 2 spells it `contain`, and
<xref:Vixen.Ui.Layout.Containment> is how it reaches the layout store.

⚠ It is **five independent effects behind one property**, not one switch with five settings, and the
useful spellings are combinations:

| Value | What it promises | Where it lives |
|---|---|---|
| `size` | The box sizes as if it had no contents, on both axes | `CalculateLayoutImpl` |
| `inline-size` | The same, across the inline axis only | `CalculateLayoutImpl` |
| `layout` | An independent formatting context, and a containing block for out-of-flow descendants | `LayoutTree.Absolute`, `EstablishesBlockFormattingContext` |
| `paint` | Descendants are clipped to the box, plus everything `layout` promises | `OverflowReader`, and so the draw list and the hit test |
| `style` | Counters and quotes are scoped to the subtree | ⛔ nowhere — see below |

`contain: content` is `layout paint style` and `contain: strict` is that plus `size`. Both are
accepted as whole values; CSS forbids either beside another keyword and so does this reader.

⚠ **Size containment is not "skip the children", and reading it that way is how it gets built
wrong.** § 3.2 says the box is sized as if it were empty. It goes on laying its contents out,
painting them, hit-testing them and scrolling them — it only refuses to let them decide its own box.
The difference is invisible in any fixture where the children happen to fit, which is most of them.

## What it is for

**Stopping a subtree's size from escaping.** A panel whose contents change every frame makes its
whole ancestor chain re-measure. `contain: size` with a stated width and height cuts that chain: the
box is what its own style says, whatever is inside it.

**Getting a containing block without `position: relative`.** `contain: layout` makes the box the
containing block of every absolutely positioned descendant. That is what `position: relative` is
usually written for, and this says it without also changing how the box paints or how `z-index`
resolves around it.

**Clipping without claiming to scroll.** `contain: paint` cuts descendants at the box exactly as
`overflow: hidden` does, and says why: it is a promise about painting rather than a scroll container
with its scrolling turned off.

⚠ **What it is not for here is performance.** In a browser, containment is a hint that lets an engine
skip work. This engine takes it as a *correctness* declaration — the box really does size and clip
differently — and does not yet use it to prune any pass. A stylesheet that writes `contain: strict`
on a hundred panels gets the specified geometry, not a faster frame.

## Using it

From the layout store, one setter taking the flags:

```csharp compile
using Vixen.Ui.Layout;

public static class ContainedPanel {
    public static LayoutNodeId Build(LayoutTree tree) {
        var page = tree.CreateNode();
        tree.SetDisplay(page, Display.Block);
        tree.SetDimension(page, Dimension.Width, StyleLength.Points(300));
        tree.SetDimension(page, Dimension.Height, StyleLength.Points(300));

        // Sized as if empty, so it is 40 across and 40 down whatever is inside it — and it is the
        // containing block of any absolutely positioned descendant, without being `relative`.
        var panel = tree.CreateNode();
        tree.SetDisplay(panel, Display.Block);
        tree.SetPositionType(panel, PositionType.Static);
        tree.SetContainment(panel, Containment.Size | Containment.Layout | Containment.Paint);
        tree.SetDimension(panel, Dimension.Width, StyleLength.Points(40));
        tree.SetDimension(panel, Dimension.Height, StyleLength.Points(40));
        tree.AddChild(page, panel);

        // Still laid out, still painted, still hit-tested — and it hangs outside the panel, where
        // paint containment cuts it.
        var contents = tree.CreateNode();
        tree.SetDisplay(contents, Display.Block);
        tree.SetDimension(contents, Dimension.Width, StyleLength.Points(200));
        tree.SetDimension(contents, Dimension.Height, StyleLength.Points(200));
        tree.AddChild(panel, contents);

        return page;
    }
}
```

In a `.vcss` it is the declaration CSS uses, and the reader takes a list of keywords:

```css
#panel { contain: strict; width: 40px; height: 40px; }
#card  { contain: layout paint; }
#row   { contain: inline-size; }
```

There are no `contain-*` utility classes yet: the parity ledger's row stays `absent` until the
family is registered, which is tracked separately from the property being read.

## Examples

### An auto-sized box collapsing

The only arrangement in which size containment can be *seen*. A box with a stated width and height is
the same box either way, so a fixture that states them proves nothing at all.

```css
/* 40 tall: the child decides. */
.box   { display: block; }
.child { display: block; width: 60px; height: 40px; }

/* 0 tall, with the child still 60 by 40 and still at the box's own origin. */
.box   { display: block; contain: size; }
```

Add padding and the collapsed box is the padding, not zero — § 3.2 removes the *contents* from the
sizing, and a box's own padding and border are not contents.

### `inline-size` is one axis, and that is the whole keyword

```css
/* Zero across, and still as tall as its contents laid out at that width. */
.row { display: block; contain: inline-size; }
```

An implementation that treated this as `size` gets the height wrong; one that ignored it gets the
width wrong. Both halves have to be asserted or the keyword is indistinguishable from its neighbour.

### A containing block with nothing positioned

```css
.box    { display: block; position: static; width: 100px; contain: layout; }
.pinned { position: absolute; right: 0; width: 40px; }
```

Without the declaration, `right: 0` resolves against whatever ancestor is positioned — the root, in a
document where nothing else is. With it, the box itself is the containing block. ⚠ Neither element is
`position: relative`, and a fixture where either one is cannot fail.

## What is not implemented

⛔ **`style` containment is refused, and the refusal is measured rather than pending.** § 3.4 scopes
counters and quotes to the subtree, and this engine has neither — so every value of it would parse,
compute and move nothing. The keyword is therefore *understood* and inert rather than rejected: that
distinction is the difference between `contain: layout style` still containing layout and the whole
declaration being dropped. An unrecognised word does drop the declaration, which is what CSS does
with a value it cannot parse.

⚠ **Paint containment clips at the border box, where CSS says the padding box.** That is the
rectangle `overflow` already cuts at in this engine, and painting, hit testing and sticky positioning
all read it from one place — <xref:Vixen.Ui.Layout.Containment> is folded into that single answer
rather than given a clip of its own. Correcting one of the two without the other would produce
exactly the disagreement that arrangement exists to prevent.

⚠ **Containment prunes no work.** Nothing skips a measurement, a layout pass or a draw-list walk
because of a promise made here; the property changes what the answer *is*, not how long it takes to
get. `contain: size` still lays its subtree out.

⚠ **No `contain-*` utility classes.** The property is reachable from a hand-written `.vcss` and from
the layout store, and the utility family is a separate piece of work.

## See also

- [Floats and clear](floats.md) — the other property whose main effect is establishing a formatting
  context, and the one whose `flow-root` keyword does half of what `contain: layout` does.
- [Inline layout](inline-layout.md) — what an independent formatting context means for the boxes
  inside it.
- <xref:Vixen.Ui.Layout.Containment> — the flags, one remark per value.
