---
title: Labeled content
slug: ui/labeled-content
kind: guide
area: Core
summary: LabeledContent is the form row — a caption, the field it names, and the line under it. It writes the LabelledBy and DescribedBy relations for you, which is the part a stylesheet cannot do and the part every hand-built form was missing.
api: [T:Vixen.Ui.Controls.LabeledContent]
tags: [ui, controls, forms, accessibility, vxml]
since: 0.2
status: preview
related: [ui/text-input, ui/key-value-list, ui/accessibility, ui/group-box]
---

## What it is

`LabeledContent` is one row of a form: a caption, whatever control the caption names, and a line
underneath for a hint or an error.

```vxml
<LabeledContent Label="Project name" Description="Letters, numbers and dashes.">
    <TextBox Placeholder="my-game" />
</LabeledContent>
```

Three parts, all reachable: `Caption` holds the words, `Content` holds the field, and `Message` holds
the line under it. `Content` is the content host, so a nested tag lands in it without being told to.

## What it is for

A form. Doc 49 § 7.1 ranks `LabeledContent` with `GroupBox`, `Form` and `Section` as the fourth of the
missing controls, and notes that `Card` and `KeyValueList` approximate it — they approximate the
*picture*.

⚠ **What none of them has is the join, and without it a form is a column of unnamed boxes.** A
`TextBox`, a `NumericInput`, a `Slider` and a `Select` all deliberately answer `null` to their own
accessible name: a placeholder is a hint that vanishes the moment there is a value, and a number is
not a name. So eight fields beside eight words is eight anonymous widgets, and nothing in the
accessibility tree says which word belongs to which. `PropertyGrid` has always known this and writes
`AddAccessibleRelation(LabelledBy, row.Label)` by hand for every row it builds. Outside that grid
there was no way to.

This control is that line, made the container's job so that it cannot be the caller's to forget.

## Using it

```csharp no-compile="a fragment; `panel` is the caller's own"
var row = panel.Add<LabeledContent>();

row.Label = "Project name";
row.Description = "Letters, numbers and dashes.";

// ⚠ `Content`, not `row`. `Add<T>` puts the child exactly where it is told; `ContentHost` is what
// routes a nested tag in markup.
var field = row.Content.Add<TextBox>();
```

**The message is one element, for the hint and for the error.** They are the same line in the same
place saying the same kind of thing, and a row with both would put two lines under one field where
the second contradicts the first. A form that has just been refused writes the field's
`ValidationMessage` into `Description` and puts the hint back when it clears.

**The message is pointed at rather than copied.** `TextField.ValidationMessage` is deliberately not
written into the accessibility tree — ARIA pairs `aria-invalid` with a *separate* element holding the
words, reached by `aria-describedby`, and folding the string into `AccessibleDescription` would
overwrite whatever the application had put there. `Message` is that element and the relation to it is
written for you.

**Clicking the caption focuses the field**, which is `<label for>`'s whole affordance and the reason a
tick box with three words beside it is not a four-pixel target. Only the caption: a click anywhere in
the row would take a drag that started on a slider's track and would fight a text field's own caret
placement.

⚠ **A field *reparented* into a row is not joined by itself.** `UiElement.OnChildAdded` is creation
only and says so — a hook that also fired on a move would register the same child once per drag of a
docking host. So a row built by moving an existing control into it calls `Adopt` afterwards; the
relations refuse duplicates, so calling it on a field that is already joined costs nothing.

```csharp no-compile="a fragment; `row` and `existing` are the caller's own"
document.Reparent(existing, row.Content);
row.Adopt(existing);
```

**The row has no `Required` and computes no verdict.** Those belong to the field, which already
reports both — see [text input](text-input.md). A row that wants an asterisk beside its caption reads
`:required` off its field with a selector rather than keeping a second copy of the answer.

**Stacked by default, and the axis is CSS.** There is no `Orientation` property: unlike a `Toolbar`,
whose axis also decides which arrow keys walk it, a form row's axis decides nothing but where the
caption sits. A row of captions down the left is two rules in the application's own sheet:

```vcss
labeled-content { flex-direction: row; align-items: center; gap: 12px }
field-label { width: 140px }
field-content { flex-grow: 1 }
```

## Examples

A short form, in markup:

```vxml no-compile="a fragment; the model is the application's own"
<Panel class="flex flex-col gap-3">
    <LabeledContent Label="Name">
        <TextBox bind:Value="@Model.Name.Value" />
    </LabeledContent>

    <LabeledContent Label="Mass" Description="Kilograms.">
        <Stepper Minimum="0" bind:Number="@Model.Mass.Value" />
    </LabeledContent>

    <LabeledContent Label="Material">
        <Select bind:Value="@Model.Material.Value" />
    </LabeledContent>
</Panel>
```

Showing a rejected value's reason on the row that owns it:

```csharp no-compile="a fragment; `row` and `field` are the caller's own"
field.Required = true;

field.ValueChanged += (box, _) =>
    row.Description = box.IsValid ? "Letters, numbers and dashes." : box.ValidationMessage;
```

Colouring the message when the field beside it is refused, from the application's sheet — the control
theme leaves it muted, because the same element carries the hint that was there before the form was
refused:

```vcss
labeled-content:has(.invalid) > field-message { color: var(--danger) }
```

## See also

* [Text input](text-input.md) — the field the row usually holds, and where `Required`, the verdict
  and `ValidationMessage` live
* [Key-value list](key-value-list.md) — the two-column layout for a list of rows, where this is one
  row on its own
* [Accessibility](accessibility.md) — `LabelledBy`, `DescribedBy` and why a relation is not a copied
  string
