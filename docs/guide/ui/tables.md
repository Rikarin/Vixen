---
title: Tables
slug: ui/tables
kind: guide
area: Core
summary: CSS 2.1 §17's table formatting context over Vixen's layout store — `display: table`, `table-row` and `table-cell`, the fixed and automatic column algorithms, `border-spacing`, and the parts of the chapter that are not laid out yet.
api: [T:Vixen.Ui.Layout.TableLayout]
tags: [ui, layout, table, css, tailwind]
since: 0.2
status: preview
related: [ui/containment, ui/inline-layout]
---

## What it is

A table lines its cells up in **columns** and **rows**: every cell in a column is as wide as the
column, and every cell in a row is as tall as the row. `display: table`, `table-row` and `table-cell`
reach a layout algorithm of their own in `Vixen.Ui.Layout` — not an alias for `block` — and
<xref:Vixen.Ui.Layout.TableLayout> picks which of CSS 2.1 §17.5.2's two column algorithms it runs.

## What it is for

Data that has to line up across rows whose content differs: a price list, a key-binding sheet, a
property inspector with a name column. A stack of blocks, a flex row or a grid of independent items
cannot make the second row's cells follow the first row's widths without the author computing them.
Where the columns are known in advance, a grid is usually the simpler tool; a table earns its place
when the content decides them.

## Using it

**Columns.**

- **`table-layout: fixed`** (§17.5.2.1) reads the **first row only**. A first-row cell with a `width`
  decides its column; the other columns share what is left of the table's own `width` equally. A
  cell in a later row decides nothing, which is what makes the algorithm fast and predictable. If the
  decided columns need more than the table's width, the table grows; if they need less and every
  column is decided, they share the extra in proportion.
- **`table-layout: auto`** (the initial value) reads **every cell**. Each column lies between the
  widest min-content and the widest max-content of its cells. An `auto`-width table is its columns'
  max-content sum, capped by the room it is offered and never below the min-content sum.

⚠ **`table-fixed` on a table with no `width` is laid out automatically.** The fixed algorithm needs a
width to share out; CSS Tables 3 and every engine fall back the same way.

**Rows and spacing.** A row is as tall as its tallest cell, or its own `height` if that is larger,
and every cell in the row is stretched to it. `border-spacing` — one length for both axes, or
horizontal then vertical — sits between columns and rows *and* between the outer cells and the
table's padding edge, so `n` columns have `n + 1` gutters. A table's `height` is a minimum, and a
table taller than its rows shares the extra among them in proportion to their heights. In an `rtl`
table the first column is the rightmost.

**What is not laid out yet** (doc 43 § B4):

- **Anonymous boxes.** A child of a table that is not a `table-row` stands in for its own one-cell
  row, and a child of a row that is not a `table-cell` is a cell where it stands. CSS wraps runs of
  such children in one anonymous box; here each gets its own.
- `inline-table`, row groups (`table-header-group` and the rest), columns, captions,
  `colspan`/`rowspan`, and `border-collapse: collapse`. Their `display` keywords are dropped rather
  than aliased, so the element keeps the default.
- `vertical-align` inside a cell: the content sits at the top of the row.
- `border-spacing` is read on the table that declares it and is not inherited.

## Examples

```css
.prices { display: table; table-layout: fixed; width: 300px; border-spacing: 8px; }
.prices > .row { display: table-row; }
.prices .cell  { display: table-cell; }
.prices .name  { width: 120px; }
```

Three gutters of 8 leave 276 for two columns: the name column is 120 and the other is 156, in every
row, whatever the later rows' cells contain.

The same table in utility classes: `table table-fixed border-spacing-2` on the table, `table-row` on
each row, `table-cell` on each cell, and `w-30` on the first row's name cell. `border-spacing-x-*` and
`border-spacing-y-*` set one axis each and compose.

## See also

- [Containment](containment.md) — the other way to stop a box's contents deciding its size.
- [Inline layout](inline-layout.md) — what a cell's text runs on.
