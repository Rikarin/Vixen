---
title: Inspectors in markup
slug: editor/inspectors-in-markup
kind: guide
area: Editor
summary: Writing a custom inspector as a .vxml file with no code-behind, bound to the editing pipeline by name after the tree is built.
api: [T:Vixen.Editor.Inspector.PropertyField, T:Vixen.Editor.Inspector.MarkupBinding, T:Vixen.Editor.Inspector.MarkupInspector, T:Vixen.Editor.Inspector.InspectorTarget]
tags: [editor, inspector, markup, vxml, plugins, hot-reload]
since: 0.1
status: preview
related: [editor/editing-pipeline, editor/writing-a-plugin, editor/index]
---

## What it is

A custom inspector can be a `.vxml` file with no C# in it. The markup names members by string; the
editor joins them to what is being edited *after* the tree is built.

```html no-compile="the whole of a shipped inspector — Editor/Vixen.Editor.Terrain/TerrainBrushInspector.vxml"
@component TerrainBrushInspector
@namespace Vixen.Editor.Terrain
@tag terrain-brush-inspector
@using Vixen.Editor.Inspector
@using Vixen.Ui.Controls

<Expander Label="Shape" IsExpanded="true">
    <PropertyField Path="Radius" />
    <PropertyField Path="Falloff" />
</Expander>
```

## What it is for

**Order and grouping, mostly.** The generated inspector draws a type's members in declaration order
because that is the only order it has. A brush is a shape, a stroke and a pattern; `Spacing` belongs
with `Rotation` rather than with `Falloff`, and no attribute on the type can say that.

⚠ **It is not for drawing rows by hand.** A `<PropertyField>` produces exactly the row the default
inspector would have — the drawer that claims the member, the reset button, the tooltip, the prefab
override bar, the mixed state across a multi-selection, and the undo. An author who reached for a
slider and a label instead would be reimplementing that, badly, once per member.

## Using it

**Two ways to name a member**, because they answer different questions.

| | |
|---|---|
| `<PropertyField Path="Radius" />` | draw this member the way you would have anyway |
| `<Slider binding-path="Radius" />` | I have chosen the control; join it up |

`binding-path` is universal — it works on any tag, intrinsic or control, the way `class` does — and
the join is two-way: the control shows the member and writing the control writes the member, through
the same [editing pipeline](editing-pipeline.md) an inspector row uses. The controls it knows are
`NumericInput`, `Slider`, `CheckBox`, `Switch`, the text fields, and `TextBlock` as a read-only
display. Anything else wants a `<PropertyField>`.

**Registering it** is a `CustomInspector` contribution, from the same `Activate` a plugin registers
everything else in:

```csharp no-compile="the registration; `context.Services` publishes the reload host"
registry.Add(
    new CustomInspector(
        typeof(TerrainBrushSettings),
        MarkupInspector.Of<TerrainBrushInspector>(
            context.Services.TryGet<HotReloadHost>(out var reload) ? reload : null
        )
    )
);
```

⚠ **Mount through the reload host if there is one.** A declarative layout you have to restart the
editor to see is a slower way to write C#; what makes markup worth adopting is the loop — change the
file, the panel is different a second later. `MarkupInspector.Of` mounts through the host and binds
the tree again after a reload, because a reload keeps the component and throws its elements away.

⚠ **A path is a string the compiler never sees.** A renamed member leaves markup that resolves to
nothing, so a `<PropertyField>` whose path finds no member draws the name it could not find rather
than disappearing — a row that quietly vanishes reads as the value being unset.

## What a custom inspector replaces

The generated rows, entirely. Two halves of one panel disagreeing about the order a type's fields go
in is the thing an author writes a custom inspector to fix, so it replaces rather than adds.

⚠ **It is looked up before a descriptor is required**, which is the case that most needs one: a
plugin's own type, compiled outside the solution and therefore with no generated description at all,
still gets the inspector its author wrote.

## See also

* [The editing pipeline](editing-pipeline.md) — what a `binding-path` is bound *to*
* [Writing a plugin](writing-a-plugin.md) — where the registration goes
* [The editor shell](index.md)
