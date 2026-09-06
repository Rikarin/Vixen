---
title: Group box
slug: ui/group-box
kind: guide
area: Core
summary: GroupBox is the titled box round a set of related controls — HTML's fieldset and legend. Card draws the same picture; what only this one does is tell a screen reader that the controls inside answer one question.
api: [T:Vixen.Ui.Controls.GroupBox]
tags: [ui, controls, forms, accessibility, vxml]
since: 0.2
status: preview
related: [ui/labeled-content, ui/markup-panels, ui/accessibility]
---

## What it is

`GroupBox` is a bordered box with a caption on it, holding controls that belong together.

```vxml
<GroupBox Label="Shadows">
    <LabeledContent Label="Resolution"><NumericInput /></LabeledContent>
    <LabeledContent Label="Distance"><NumericInput /></LabeledContent>
    <CheckBox Label="Soft edges" />
</GroupBox>
```

Two parts: `Legend` holds the words and `Content` holds the controls. `Content` is the content host,
so a nested tag lands inside without being told to, and the legend hides itself when `Label` is
`null`.

## What it is for

The container half of doc 49 § 7.1's fourth rank. [`LabeledContent`](labeled-content) is the row; this
is what a set of rows goes inside.

⚠ **`Card` and `Panel` already draw this picture, and they are exempt from the accessibility sweep
for a good reason: they are layout.** A tree that announced every bordered box would read a four-field
form as thirty nested groups, which is how an accessibility tree becomes complete and useless. That
is the right decision for a box that happens to have a border, and the wrong one for a box whose
whole purpose is to say *these belong together*.

⚠ **The role is what this type is, not the border.** A `GroupBox` reports
`AccessibleRole.Group` — HTML's `<fieldset>` — named by its legend, so a reader entering it hears the
caption and then the controls. A `Card` with a `TextBlock` in its header draws exactly the same thing
and says nothing at all: those words are read when a reader walks *past* them and never again, and
somebody who tabbed straight into the third field never walked past them.

Reach for `Panel` or `Card` when the box is arrangement. Reach for this one when a person who cannot
see the border still needs to know the controls are one question.

## Using it

```csharp no-compile="a fragment; `panel` is the caller's own"
var group = panel.Add<GroupBox>();

group.Label = "Shadows";

var resolution = group.Content.Add<NumericInput>();
```

⚠ `Add<T>` does not go near `ContentHost` — that routes a *nested tag* in markup — so a C# caller adds
to `Content` and a markup caller nests, and both land in the same place.

The caption is one property and one copy of the words. `Label` writes the legend and is also what the
group answers as its accessible name, so there is no relation to keep in step and nothing to forget:

```csharp no-compile="a fragment; `group` is from above"
group.Label = "Ambient occlusion";   // the legend and the announced name, together
```

⚠ **An unnamed group is still a group.** Leaving `Label` unset hides the legend and leaves the role
where it is; the role never moves under a property, because nothing could rely on one that did. A
group with nothing to say is a caller who wanted `Panel`.

## Examples

**A settings section with named fields inside it.** The group is the context and the row is the name,
and they compose — the field is announced as "Resolution, in the Shadows group" rather than as either
one alone.

```csharp no-compile="a fragment; `panel` is the caller's own"
var group = panel.Add<GroupBox>();
group.Label = "Shadows";

var row = group.Content.Add<LabeledContent>();
row.Label = "Resolution";
row.Content.Add<NumericInput>();
```

**A set of alternatives.** A radio group inside a group box is the arrangement `<fieldset>` was
invented for: the question is on the box, the answers are inside it, and neither has to repeat the
other.

```csharp no-compile="a fragment; `panel` is the caller's own"
var group = panel.Add<GroupBox>();
group.Label = "Shadow filtering";

var choice = group.Content.Add<RadioGroup>();

choice.AddOption("hard", "Hard");
choice.AddOption("pcf", "PCF");
choice.AddOption("pcss", "PCSS");
```

⚠ In markup a `RadioGroup` is filled through `ref` and `OnComposed` rather than by nesting — it has no
content host — so this one is C#. The group box itself nests either way.

**Styling it.** The tag draws the border and the legend is a bare part, so a sheet can move either
without the control knowing:

```css
group-box { border-color: var(--accent); }
group-legend { font-size: 0.9em; text-transform: uppercase; }
```

## See also

- [Labeled content](labeled-content) — the row that names one field, and the relations it writes.
- [Markup panels](markup-panels) — `Panel`, `Card` and the boxes that are deliberately not announced.
- [Accessibility](accessibility) — roles, names and relations, and what a sweep holds them to.
