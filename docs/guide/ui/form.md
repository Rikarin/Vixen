---
title: Form
slug: ui/form
kind: guide
area: Core
summary: Form is a set of fields submitted as one. Enter in a field or a click on the default button submits it, and a submission with a wrong field is refused with the keyboard put in the first one — HTML's rules, so an OK button no longer has to know every field it sits under.
api: [T:Vixen.Ui.Controls.Form]
tags: [ui, controls, forms, validation, accessibility, vxml]
since: 0.2
status: preview
related: [ui/labeled-content, ui/group-box, ui/section, ui/date-picker, ui/text-input, ui/accessibility]
---

## What it is

`Form` is a container whose fields are submitted together.

```vxml
<Form ref="@Account" AccessibleName="Account">
    <LabeledContent Label="Name"><TextBox Required="true" /></LabeledContent>
    <LabeledContent Label="Email"><TextBox Required="true" /></LabeledContent>
    <Button Label="Create" IsDefault="true" />
</Form>
```

It draws nothing of its own — no border and no caption. A form's heading is the panel's, and what a
form looks like is its rows ([`LabeledContent`](labeled-content)) and its groups
([`GroupBox`](group-box)). The theme gives it one rule: a column, since an element nothing styles lays
its children out in a row.

## What it is for

The submission half of doc 49 § 7.1's fourth rank. Every field here already validates itself —
`TextField` (and so `TextBox`, `NumericInput`, `SecureTextBox`, `TextArea`), `Select`, `MultiSelect`,
`ComboBox`, `CheckBox`, `RadioGroup` and [`DatePicker`](date-picker) each answer `IsValid` and
`Revalidate()` — and nothing asked all of them at once. An application's OK button had to hold a reference to every field above it.

A form follows HTML's three rules:

- ⚠ **Enter in a field submits.** This is *implicit submission*, heard as the routed `SubmitEvent` a
  field raises — so the field decides what Enter means, and a `TextArea` keeps its line breaks and
  submits on Ctrl-Enter (⌘-Enter on a Mac).
- **A click on the default button submits.** A button with `IsDefault` inside the form; any other
  button does not.
- ⚠ **A submission with a wrong field is refused.** `Submitted` is not raised, the keyboard is put in
  the *first* field that refused, in document order, and every field is marked as having been through
  a submission. That mark is what lets a `:user-invalid` rule match the required field the user never
  reached, as a browser's does after the first press of Submit. (The shipped theme rings a wrong field
  from `.invalid`, which is there from the start; `:user-invalid` is the selector for an application
  that wants a fresh form to stay quiet until it is submitted.)

## Using it

Take the answer from `Submitted`, which is raised only for a submission every field accepted:

```csharp no-compile="a fragment; `Account` is the form's `ref` from above and `Save` the caller's own"
Account.Submitted += form => Save();
```

`Submit()` is public for the submissions that are neither a key nor a button — a menu command, a
toolbar button outside the form, a test — and returns whether it was accepted.

`Fields` is every validating control under the form, in document order, walked fresh each time.

- ⚠ **A field belongs to the nearest form above it** — HTML's *form owner*. A form inside a form, which
  HTML forbids and a composed panel can still produce, keeps its fields to itself: the outer form does
  not validate them, and Enter in one of them submits the inner form only.
- ⚠ **A disabled field is not asked.** A field with `Disabled`, or one under a disabled control such as
  a disabled `GroupBox`, is barred from validation as a disabled `<input>` is — a setting nobody can
  change cannot stop the form.
- A combo box answers once, for itself and its editor together.

A `Form` reports `AccessibleRole.Form` — the first producer of that role in the tree — and is not a
tab stop; its fields are. ARIA exposes a `form` as a landmark only when it has a name, so set
`AccessibleName` when a screen-reader user should be able to jump to it. There is no `Label`: the name
is not drawn, and every element already has the property.

**Limits.**

- ⚠ **`on:submit` on a `Form` hears the fields, not the form.** It subscribes the routed `SubmitEvent`
  every field raises on Enter, before the form has validated anything, so it fires for a submission
  the form is about to refuse. Take the form's answer from `Submitted` through a `ref`.
- **The key is not marked handled.** A dialog that closes on its field's own `Submitted`, or an
  ancestor listening for the routed event, still hears Enter. Whether a dialog should close over a
  refused form is the dialog's question.
- ⚠ **A default button answers Return for its whole window**, not only for its form — the key
  equivalent is a fallback for any Return nothing else wanted. Give a form a default button when the
  form *is* the window or the sheet.
- **No reset**, and no values of its own: the fields hold the values, bound to the application's model.

## Examples

**Built in C#, with a submission from outside the form.** A toolbar's Save is not in the form, so it
submits by calling it:

```csharp no-compile="a fragment; `panel` and `toolbar` are the caller's own"
var form = panel.Add<Form>();
form.AccessibleName = "Project settings";

var row = form.Add<LabeledContent>();
row.Label = "Name";

var name = row.Content.Add<TextBox>();
name.Required = true;

var save = toolbar.Add<Button>();
save.Label = "Save";
save.AddHandler<ClickEvent>((_, _) => form.Submit());
```

**Styling a form that stays quiet until it is sent.** The fields carry both states; the application
picks the one it wants:

```css
form textbox.invalid { border-color: var(--border); }
form textbox:user-invalid { border-color: var(--danger); }
```

## See also

- [Labeled content](labeled-content) — the row that names one field and carries its message.
- [Group box](group-box) — the box round the fields that answer one question.
- [Section](section) — the titled part of a page a form or its groups sit in.
- [Text input](text-input) — `Required`, `Validator` and the message a refused field carries.
- [Accessibility](accessibility) — roles, names and landmarks.
