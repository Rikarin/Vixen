---
title: Section
slug: ui/section
kind: guide
area: Core
summary: Section is a titled part of a page — a region landmark named by a heading of its own, so a screen-reader user can reach it both by landmark and by heading. It draws its title and nothing else.
api: [T:Vixen.Ui.Controls.Section]
tags: [ui, controls, forms, accessibility, vxml]
since: 0.2
status: preview
related: [ui/group-box, ui/form, ui/labeled-content, ui/accessibility]
---

## What it is

`Section` is a heading and the controls it heads.

```vxml
<Section Title="Audio">
    <LabeledContent Label="Volume"><Slider /></LabeledContent>
    <LabeledContent Label="Output"><Select /></LabeledContent>
</Section>
```

It draws the title and nothing else — no border, no background. A settings window is a column of
sections, and one that framed every section would read as a stack of cards; what says where one
section ends is the next one's heading. The theme gives it a column, a heading with some weight, and a
column for the content.

## What it is for

The landmark half of doc 49 § 7.1's fourth rank. [`GroupBox`](group-box) says *these controls answer
one question*; [`Form`](form) says *these fields are sent together*; `Section` says *this is a part of
the page, and here is its name*. It is HTML's `<section aria-labelledby>` with its `<h2>`.

- ⚠ **Two nodes in the accessibility tree, on purpose.** A screen-reader user moves through a long
  page by landmark and by heading, and a section has to be reachable both ways. So the heading is a
  real `AccessibleRole.Heading` node, and the section is an `AccessibleRole.Region` *named by it*
  through `LabelledBy` — ARIA's own pattern. The title is one property and one copy: the words on
  screen are the words both nodes announce.

  ```text
  region "Audio"
    heading "Audio"
    slider "Volume" = "0.5"
  ```

- ⚠ **Unnamed, it is still a region, and the tree says so.** ARIA exposes a `<section>` with no name as
  a plain generic, and a control that did the same would hide the omission. `Section` keeps its role,
  and `AccessibilitySnapshot.Unnamed` reports an unnamed region — the one landmark ARIA requires a
  name of. A section that should be a landmark without a visible heading sets `AccessibleName`, which
  wins over the heading.
- **No title, no heading.** The heading part is hidden when there is no title, and its role goes with
  it — the accessibility tree walks hidden parts, so an empty heading left as one would be announced
  as a heading with no name.

## Using it

Nest controls in markup, and they land in `Content`. From C#, `section.Add<T>()` and
`section.Content.Add<T>()` are the same call: `Add` parents on the control's content host, which is
where a nested tag goes ([#1425](https://github.com/Rikarin/Vixen/issues/1425)). Writing `Content`
says so at the call site:

```csharp no-compile="a fragment; `panel` is the caller's own"
var audio = panel.Add<Section>();
audio.Title = "Audio";

var row = audio.Content.Add<LabeledContent>();
row.Label = "Volume";
row.Content.Add<Slider>();
```

`Title` is what the heading says and the section's name. `Heading` is the part it is drawn in, for a
theme or a test.

**Limits.**

- ⚠ **No heading level.** The accessibility model has no `aria-level`, so a section inside a section is
  announced as a heading exactly like its parent's. The theme draws every section heading at one size.
- **It does not collapse.** That is `Expander` (see [Markup panels](markup-panels)), whose header is a
  button saying what it opens.
- **It is not a form.** Put a [`Form`](form) inside a section, or sections inside a form, as the page
  needs — the two answer different questions and compose.

## Examples

**A settings page.** Each section a landmark, each heading a stop for heading navigation, each row a
named field:

```vxml
<Form AccessibleName="Settings">
    <Section Title="Display">
        <LabeledContent Label="Resolution"><Select /></LabeledContent>
    </Section>
    <Section Title="Audio">
        <LabeledContent Label="Volume"><Slider /></LabeledContent>
    </Section>
</Form>
```

**A heavier heading.** The heading is its own tag:

```css
section-heading { font-size: 1.3em; border-width: 0px 0px 1px 0px; border-color: var(--border); }
```

## See also

- [Group box](group-box) — the bordered box round controls that answer one question.
- [Form](form) — what submits the fields in a section, and refuses while one is wrong.
- [Labeled content](labeled-content) — the row that names one field.
- [Accessibility](accessibility) — roles, names, landmarks and the gates that hold them.
