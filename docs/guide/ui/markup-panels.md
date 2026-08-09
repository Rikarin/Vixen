---
title: Panels in markup
slug: ui/markup-panels
kind: guide
area: Core
summary: Writing a control in .vxml — @inherits for a class callers can hold and add, ref for the parts they read, and the @for key rule that decides whether a row updates at all.
api: [T:Vixen.Ui.Markup.Syntax.InheritsDirectiveSyntax, T:Vixen.Ui.Styling.InlineDeclaration]
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

```csharp no-compile="the companion file; the other half is the generated partial — Editor/Vixen.Editor.AssetEditors/Importing/ModelImportView.cs"
public sealed partial class ModelImportView;
```

### Two hooks, both partial methods

`OnComposed` runs once the whole body has been built, which is where wiring belongs — every `ref` in
the file is assigned, including those under a live `@if` arm. It runs again after a hot reload.
`OnUnmounted` runs when the panel leaves the tree, before its effects are disposed — for an
`@inherits` element when the element is removed, and for a `Component` when the element it drew
itself into is removed or the branch that built it closes. Whichever comes first; it runs once.

Neither is an override, so neither costs anything when nobody implements it. `@code` may not override
`OnCreated` or `OnRemoved`: the generated scaffold uses both, and these two run in the same places.

### The three universal attributes

`class`, `style` and `binding-path` mean the same on a component tag as on an element, and are never
assigned as properties. On a capitalised tag they reach the element the control drew.

`style` is an *inline style*: a cascade origin that beats every rule, not an attribute a selector can
match. Use it for the lengths no stylesheet was given.

```vxml no-compile="a bar whose left edge is a measured width times a timestamp"
<gpu-lanes style="height: @Length(Chart.Height)">
    @for (var bar in Chart.Bars) {
        <gpu-bar key="@bar" class="@bar.Hue" style="@bar.Geometry">@bar.Caption</gpu-bar>
    }
</gpu-lanes>
```

The value may be a bound expression, which is the point of it. It goes through the same parser a rule
body does, so `style="padding: 4px 8px"` becomes the four longhands the layout reads; a brace in the
value is refused with a diagnostic; and re-evaluating a binding to the text it already had costs no
parse.

It takes back only the properties it wrote. A control positions its own parts with
`UiElement.SetStyle` — a `DataGrid` row's `top`, a `DockingHost` pane's `flex-grow` — and a `style`
attribute never reaches those.

⚠ **It is the escape hatch, not the first answer.** Anything a rule can say belongs in a rule: a
`display` toggle is a class, and `OffsetX` is cheaper than either when moving a box is enough. And
because the parse is real, something writing one number every frame should call `SetStyle` directly.

There is deliberately **no `id`**. Styling one element is a class; getting one in C# is `ref`, which
hands back the object rather than a name and is checked by the compiler.

### Controls with two places for content

A child written inside a capitalised tag hangs from that control's `ContentHost`. `Tabs` has two
places a child could go — the strip and the panels — and both are reachable by nesting:

```vxml no-compile="Editor/Vixen.Editor.AssetEditors/Prefabs/PrefabView.vxml"
<Tabs class="document-tabs">
    <TabItem ref="@HierarchyTab" Label="Hierarchy" />
    <TabItem Label="Compiled">
        <CompiledSceneView ref="@Compiled" />
    </TabItem>
</Tabs>
```

`Tabs.ContentHost` is the strip, so `<TabItem>`s land there; `TabItem.ContentHost` is its panel, so a
tab's own content lands where it shows. A tab pairs itself with a panel in `OnCreated` and unregisters
in `OnRemoved`, which is what lets an `@if` or `@for` add and remove tabs — markup removes elements
without calling `RemoveTab`.

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
