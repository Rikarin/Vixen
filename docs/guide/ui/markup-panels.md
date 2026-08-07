---
title: Panels in markup
slug: ui/markup-panels
kind: guide
area: Core
summary: Writing a control in .vxml — @inherits for a class callers can hold and add, ref for the parts they read, and the @for key rule that decides whether a row updates at all.
api: [T:Vixen.Ui.Markup.Syntax.InheritsDirectiveSyntax]
tags: [ui, markup, vxml, controls, components, reactivity]
since: 0.2
status: preview
related: [editor/inspectors-in-markup, ui/key-value-list]
---

## What it is

A `.vxml` file compiles to a C# partial class. By default that class is a
[`Component`](/docs/api/vixen.ui.composition/component): it *builds* elements and is not one.

`@inherits` changes what the class is. With it the generated class derives from whatever the header
named — a `Control`, or any `UiElement` — and the markup describes that element's own tree. `ref`
hands a named element back to a member of the class, so the parts a caller reads are the parts the
markup already declares.

```vxml
@component ModelImportView
@inherits Vixen.Ui.Controls.Control
@tag model-editor

<TreeView ref="@Parts" />
<EmptyState ref="@Empty" Title="Not imported yet" />

@code {
    public TreeView Parts { get; private set; } = null!;
    public EmptyState Empty { get; private set; } = null!;
}
```

## What it is for

**`@inherits` is for a panel whose public surface is its parts.** A `Component` is not in the element
tree, so `parent.Add<T>()` cannot make one — its constraint is `where T : UiElement, new()` — and
walking the tree cannot find one. That is right for a view nobody reads the insides of and wrong for
an editor panel whose callers write `view.Tree`, `button.Disabled` or
`Assert.Equal(2, view.Parts.Root.Children.Count)`.

Whichever the file is, the reactive machinery is identical. An `@inherits` class gets the same
`BuildContext` a component's `Build` gets, so `@if`, keyed `@for`, effects and region-scoped disposal
are the same code rather than a second implementation.

**`ref` is for the parts that are populated imperatively.** A tree of sub-assets, a list a caller
fills, a control whose method you have to call — none of those is markup, and all of them need a
typed handle to the element the markup made.

⚠ **Nothing checks the member here.** `ref="@Parts"` emits `Parts = n0;` under the `.vxml`'s own
`#line`, so a member that does not exist, one that is readonly and one of the wrong type are all
reported by the C# compiler *on the name between the quotes*. That is the same bargain the tag name
and every parameter are emitted under.

## Using it

### The header

`@inherits` takes a type name, dotted, and it may rely on a `@using`. Absent, the class is a
`Component`. There is no generic form: `@inherits Row<T>` does not lex.

The generated partial has no accessibility modifier, which makes it `internal`. A panel another
assembly constructs needs a one-line companion file:

```csharp
public sealed partial class ModelImportView;
```

### Two hooks, both partial methods

`OnComposed` runs once the whole body has been built, which is where wiring belongs — every `ref` in
the file is assigned, including those under a live `@if` arm. It runs again after a hot reload.
`OnUnmounted` runs when an `@inherits` element leaves the tree, before its effects are disposed.

Neither is an override, so neither costs anything when nobody implements it. `@code` may not override
`OnCreated` or `OnRemoved`: the generated scaffold uses both, and these two run in the same places.

### Where a `ref` may go

| | |
|---|---|
| On an element or a component | Yes. On a capitalised tag it hands back the *component*, not the element it drew — `BuildContext.Host` is how you get that. |
| Inside `@if` | Yes. Null until the arm is live; stale, not cleared, when the arm leaves — ask `UiElement.IsRemoved`. |
| Inside `@for` | **No** — `VXML2010`. The body runs once per item and there is one member to assign. Put the `ref` on the element the loop is inside. |

### The `@for` key rule

**Key on the item's value when the item is immutable data. Key on the object only when that object
holds signals.**

`BuildContext.For` matches a key, reuses that item's region and **does not re-run the body** — which
is what makes focus, scroll offset and animation state survive a reorder. The consequence is that
every per-item binding stays closed over the item as it was when its key first appeared.

So a row of immutable data keyed on a stable field never updates again. `VXML2011` warns when a key
is a member access off the loop variable, which is the shape that mistake always takes; whether the
item holds signals is a question about its type, and the markup binder deliberately resolves none.

## Examples

A panel with three sections, which is where the nesting rules earn their keep. A child written inside
a capitalised tag hangs from that control's `ContentHost` — the viewport of a `ScrollView`, the
collapsible part of an `Expander` — so the nesting the markup draws is the nesting that exists.

```vxml
@component ImportSettingsView
@inherits Vixen.Ui.Controls.Control
@tag import-settings

<Alert ref="@Unknown" Title="This sidecar has settings this editor does not know" />

<ScrollView ref="@Scroll">
    <Expander Label="Import Settings" IsExpanded="true">
        <InspectorView ref="@Settings" />
    </Expander>

    <Expander Label="Addressable" IsExpanded="true">
        <InspectorView ref="@Addressable" />
    </Expander>
</ScrollView>

@code {
    public Alert Unknown { get; private set; } = null!;
    public ScrollView Scroll { get; private set; } = null!;
    public InspectorView Settings { get; private set; } = null!;
    public InspectorView Addressable { get; private set; } = null!;

    partial void OnComposed() => Unknown.AddClass("hidden");
}
```

And the key rule, both ways round:

```vxml
<!-- `StatisticRow` is a readonly record struct. Its value is its identity: change the count and
     the key changes, the old region goes, and a new row is built with the new number in it. -->
@for (var row in Rows) {
    <statistic-row key="@row">@row.Count</statistic-row>
}

<!-- `BackgroundTask`'s properties are signal-backed, so the object is stable and the bindings
     inside the row update themselves. This is what a stable key is for. -->
@for (var task in Tasks) {
    <task-row key="@task">@task.Progress.Value</task-row>
}
```

`key="@row.Label"` in the first loop would compile, draw the right number of rows in the right order,
and show the first reading for ever.

## See also

* [Inspectors in markup](../editor/inspectors-in-markup.md) — a `.vxml` bound to an editing target by name
* [Key/value lists](key-value-list.md) — the control the row loops above used to be written by hand
* [`BuildContext`](/docs/api/vixen.ui.composition/buildcontext) — what both flavours build with
* [`Component`](/docs/api/vixen.ui.composition/component) — the default base, and what it cannot do
