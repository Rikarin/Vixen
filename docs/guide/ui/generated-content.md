---
title: Generated content
slug: ui/generated-content
kind: guide
area: Core
summary: "::before and ::after with a string content — a box the sheet adds as an element's first or last child box, laid out, painted and hit as part of that element, and never an element of its own."
api: [T:Vixen.Ui.Styling.PseudoElement]
tags: [ui, styling, vcss, pseudo-elements, content, before, after]
since: 0.2
status: preview
related: [ui/cascade-layers, ui/stylesheet-diagnostics, ui/inline-layout, ui/accessibility]
---

## What it is

A `::before` or `::after` rule adds a box to an element without adding an element to the document.
The box exists exactly when `content` computes to a string — `""` included, which is the usual way to
draw a shape — and goes away when it computes to `none`, to `normal`, or to nothing, or when the box's
own `display` is `none`.

## What it is for

Decoration that belongs to the theme rather than to the markup: a dot in front of every unread row, a
separator between steps, a rule under a heading. The markup stays the list of things it is, and a
class change that turns the rule on or off adds or removes the box on the next frame.

## Using it

### Where the box is

- **In the box tree**: `::before` is the element's first child box and `::after` its last, so it is laid
  out with the element's children, pushes them along, and is painted in that order. `z-index` and
  `order` move it like any child.
- **Not in the element tree**: it is not in `Children`, it does not count for `:first-child`, `:empty`
  or `:nth-child`, it takes no focus and it is never an event's target. A pointer on a `::before` is a
  pointer on the element that generated it.
- **Not an accessibility node**: its words join the element's accessible name where the element's role
  takes its name from its content (a button, a link, a tab, a cell — the roles ARIA lists), so a
  `button::before { content: "★ " }` is announced as one button called "★ Favourites". A text box, a
  slider or a dialog never takes its name from content, so a theme's decorative icon in front of a field
  cannot name it.

The box inherits from the element, as every CSS property says it should: a `color` on the element is the
`::before`'s colour unless the rule says otherwise.

### What it does not do yet

- **Beside an element's own text, the words are in a box of their own.** A `label` with
  `label::after { content: "*" }` and its text on the label itself draws `Name*`: while the element
  generates a box, its `Text` is measured and drawn in an anonymous inline box between the two, which
  carries the element's inherited properties and nothing else. So a property that does not inherit —
  `text-overflow`, for one — written on the element does not reach its words while it generates a
  box. And a text field's caret and hit testing read the text layout of its `field-text` part
  directly, so a `::before` on that part (not on the field, which has no text of its own) would put
  the caret beside where the words are drawn rather than on them.
- **Strings only.** `attr()`, counters, quotes and images in `content` give no box rather than their
  literal text.
- **No transitions on the box.** Its style is recomputed with the element's, and nothing animates it.
- **`::marker`, `::first-letter`, `::first-line`** and the rest are still refused, with a message in the
  stylesheet log. `::placeholder`, `::selection` and `::backdrop` are reached through the
  `placeholder:`, `selection:` and `backdrop:` variants, which target parts and properties that already
  exist.
- **Display defaults to Vixen's, not CSS's.** A generated box is `display: flex` unless the rule says
  otherwise, like every other Vixen box; on the web it would be `inline`.

## Examples

```vcss
.unread::before {
    content: "";
    width: 6px;
    height: 6px;
    margin-right: 6px;
    border-radius: 3px;
    background-color: var(--accent);
}

.crumb + .crumb::before { content: "/"; margin: 0 4px; color: var(--text-muted); }

/* A CSS escape: six hex digits at most, and one space after them is swallowed. */
.count::after { content: " \2022 new"; }
```

## See also

- [Stylesheet diagnostics](stylesheet-diagnostics.md) — where a refused pseudo-element is reported.
- [Inline layout](inline-layout.md) — how a generated box shares a line with inline siblings.
- [Accessibility](accessibility.md) — names from content, and the roles that take them.
- [Cascade layers](cascade-layers.md) — a `::before` rule is ranked like any other.
