---
title: Key/value lists
slug: ui/key-value-list
kind: guide
area: Core
summary: One control for the label-and-value rows a debugger, a memory panel and a settings pane were each drawing by hand — equal halves, alternating shades, and a value slot that takes an editor.
api: [T:Vixen.Ui.Controls.KeyValueList, T:Vixen.Ui.Controls.KeyValueRow]
tags: [ui, controls, layout, theming, vxml]
since: 0.2
status: preview
related: [editor/inspectors-in-markup]
---

## What it is

`KeyValueList` is a column of rows; `KeyValueRow` is one of them. Each row has a name on the left and
a value on the right, the two halves are equal, and the rows alternate between the surface colour and
the sunken one.

The value half is an *element*, not a string — so a row can hold `42`, or it can hold a `Slider`.

```vxml
<KeyValueList>
    <KeyValueRow Key="Draw calls" Value="1 204" />
    <KeyValueRow Key="Triangles" Value="98 331" />

    <KeyValueRow Key="Volume">
        <Slider Minimum="0" Maximum="1" Value="0.5" />
    </KeyValueRow>
</KeyValueList>
```

## What it is for

Two things that keep being written by hand and keep coming out slightly different.

**Facts, read-only.** A frame debugger's bound state, a memory panel's arenas, a build analysis. The
editor grew five of these independently and every one of them wrote the same pooling loop, the same
pair of child elements and a different set of column widths — 45%, 40%, 200px, 170px — so no two
panels lined up with each other.

**Settings, editable.** A key with an editor beside it is what a settings pane is. `Content<T>()`
puts a control in the value half and the theme makes the field-shaped ones fill it, so a form is one
row per setting and no layout.

You do not want it for an inspector. [`PropertyGrid`](/docs/api/vixen.ui.controls.advanced/propertygrid)
builds its rows by asking a *type* about itself and gives each one the editor its member's type asks
for, a reset button, and a mixed state across a multi-selection. A `KeyValueList` knows nothing about
any of that; if the rows come from a type's members, the grid is the control.

## Using it

**Rows must be direct children, and that is a rule rather than a habit.** The stripe is
`key-value-row:nth-child(even)`, so anything else parented in the list — a caption, a separator, an
empty state — takes a position in the alternation and shifts every row after it. Put those beside the
list.

The reason for `:nth-child` rather than a class applied when the row is built is what happens next: a
row removed from the middle has to re-stripe everything below it, and the cascade does that for
nothing. A class does not, and the symptom is two shaded rows against each other after a refresh.

**Filling a list that is refreshed** goes through the pool rather than through rebuilding:

```csharp no-compile="a fragment; `snapshot` is the caller's model"
var slot = 0;

foreach (var fact in snapshot.Facts) {
    var row = list.Row(slot++);

    row.IsHeading = false;
    row.Key = fact.Name;
    row.Value = fact.Value;
}

list.Trim(slot);
```

`Row(index)` grows the list and unparks; `Trim(count)` hides the surplus and keeps it. `Clear()` is
the other case — a list whose content changed *shape*, where a reused row would still be holding the
previous thing's editor and the handler that wrote to it.

⚠ **`Value` and a child element are exclusive**, and the framework rather than the control is what
says so: an element with text measures itself, and the layout treats a node that measures itself as a
leaf and never lays out its children. So a slot holding both would draw the string and place the
editor nowhere. Setting `Value` empties the slot and `Content<T>()` clears the value; either
direction is safe, which is what a pooled row switching between the two needs.

⚠ **A key too long for its column is clipped, not ellipsised.** This renderer implements no
`text-overflow`, so `overflow: hidden` cutting the glyphs at the column edge is the whole of the
behaviour. What the theme does prevent is the worse outcome — a key that *wraps*, which turns one row
of a uniform list into seven lines of one.

**Restyling it** is rules against the tags, in an author stylesheet, which beats the user-agent sheet
at equal specificity:

```vcss
key-value-row { min-height: 24px; }
key-value-key { color: var(--text); }
key-value-row:nth-child(even) { background-color: transparent; }
```

The three colours it uses are `--surface-sunken` for the stripe and the heading, `--border` under a
heading, and `--text-muted` for the key — so it follows a retheme without a rule of its own.

## Examples

**A panel of facts, pooled**, which is the shape the frame debugger's state pane has: headings
interleaved with rows, refreshed whenever the selection moves.

```csharp no-compile="a fragment against a live panel; `capture` is the caller's model"
void ShowState() {
    var slot = 0;
    string? group = null;

    foreach (var row in capture.StateAt(selected).Rows()) {
        if (!string.Equals(group, row.Group, StringComparison.Ordinal)) {
            group = row.Group;

            var heading = StatePane.Row(slot++);
            heading.IsHeading = true;
            heading.Key = row.Group;
            heading.Value = null;
        }

        var line = StatePane.Row(slot++);
        line.IsHeading = false;
        line.Key = row.Label;
        line.Value = row.Value;
    }

    StatePane.Trim(slot);
}
```

**A settings pane**, which is the other half of what it is for. Each row's value is a real control and
the theme decides which of them fill their half:

```csharp no-compile="a fragment; `settings` is the caller's model"
var list = panel.Add<KeyValueList>();

list.AddRow("Name").Content<TextBox>().Value = settings.Name;
list.AddRow("Enabled").Content<Switch>().IsChecked = settings.Enabled;

var volume = list.AddRow("Volume").Content<Slider>();
volume.Minimum = 0f;
volume.Maximum = 1f;
volume.Value = settings.Volume;
```

**And the same thing in markup**, where a row's children go into its value half because
`ContentHost` points there — markup cannot name a part, so the control has to say which one content
belongs to:

```vxml
@component AudioSettings
@using Vixen.Ui.Controls

<KeyValueList>
    @for (var setting in Sliders) {
        <KeyValueRow key="@setting" Key="@setting.Label">
            <Slider Minimum="0" Maximum="1" />
        </KeyValueRow>
    }
</KeyValueList>
```

⚠ The loop's `key` and the row's `Key` are two different attributes and both are wanted here. The
binder classifies attribute names with an ordinal comparison, so lowercase `key` is the loop's
identity and `Key` is a property assignment on the row.

## See also

* [Inspectors in markup](../editor/inspectors-in-markup.md) — when the rows come from a type's members
* [`PropertyGrid`](/docs/api/vixen.ui.controls.advanced/propertygrid) — the reflection-driven relative
* [`ControlTheme`](/docs/api/vixen.ui.controls/controltheme) — the user-agent sheet these rules live in
