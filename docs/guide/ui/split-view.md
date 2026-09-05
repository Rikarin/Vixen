---
title: Split views
slug: ui/split-view
kind: guide
area: Core
summary: Two panes and a bar that resizes both — the sidebar-and-detail shape, as a control an application can reach without adopting a docking model. The ratio is a fraction, a drag writes two declarations and rebuilds nothing, and flex-basis is the half that makes the number mean what it says.
api: [T:Vixen.Ui.Controls.SplitView]
tags: [ui, controls, layout, vxml]
since: 0.2
status: preview
related: [ui/docking-panels]
---

## What it is

`SplitView` is two panes with a draggable bar between them. `Ratio` says how much of the space the
first pane takes, `Orientation` says which axis they are stacked on, and `MinimumRatio` says how far
the bar may be pulled.

```vxml
<SplitView Ratio="0.28">
    <TreeView />

    <ScrollView slot="second">
        <Inspector Model="@Model" />
    </ScrollView>
</SplitView>
```

Unmarked children go into the first pane; `slot="second"` reaches the other one. One slot name rather
than two, because [`ContentHost`](/docs/api/vixen.ui/uielement) already answers for the first — and a
`<SplitView>` whose children all needed labelling would be one whose unlabelled children silently
went nowhere.

## What it is for

The shape half of desktop applications start from: a list beside a detail, a navigator beside a
document, a palette beside a canvas. `NavigationSplitView` and `NSSplitView` are the same control.

⚠ **A draggable two-pane divider already existed and could not be reached.** It was written once,
welded inside `DockingHost` as `DockSplitterView`, so an application that wanted a sidebar had to
adopt the whole docking model — a layout tree, panel identities, tab groups, a save format and
drag-and-drop between groups — to get one bar it could pull. That is the right trade for an editor and
the wrong one for a two-pane application, which is why this is a separate control rather than a mode
of that one.

You want [`DockingHost`](/docs/api/vixen.ui.controls.advanced/dockinghost) instead as soon as the
panes need to become tabs, be dragged between each other, be torn off into their own windows, or come
back exactly as somebody left them. `SplitView` does none of those and is not on the way to doing
them.

## Using it

**The ratio is a fraction of what the two panes share**, which is the split minus the bar. So a
`Ratio` of `0.25` in a 400-pixel split with a 6-pixel bar puts the boundary at 98.5 pixels rather
than at 100 — the bar belongs to neither pane and cannot be counted in either.

⚠ **A fraction rather than a pixel width, and that is a decision rather than a shortcut.** A pixel
minimum has to be re-clamped every time the split itself is resized, and nothing here is told when
that happens: the ratio is applied through the cascade and the layout follows. A fraction means the
same thing at every size, which is also what makes it the number worth saving to a settings file.

**`MinimumRatio` clamps in both directions and applies retroactively.** Widening it past where the bar
already is moves the bar; a minimum that only applied to the next assignment would leave a split
sitting outside its own minimum with nothing that ever fixes it.

```csharp no-compile="a fragment; `root` is the caller's element"
var split = root.Add<SplitView>();

split.Ratio = 0.28f;
split.MinimumRatio = 0.15f;
split.RatioChanged += (_, ratio) => settings.SidebarRatio = ratio;

split.First.Add<TreeView>();
split.Second.Add<ScrollView>();
```

**A drag writes two declarations and nothing else** — no rebuild, no reparent, no measurement pass of
its own. That is `DockSplitterView`'s arrangement kept whole, and it is the reason a splitter feels
attached to the pointer rather than a frame behind it.

**Restyling it** is rules against the three tags:

```vcss
split-bar { width: 3px; }
split-bar:hover { background-color: var(--accent); }
split-pane { padding: 0px; }
```

## Two traps

⚠ **`flex-basis: 0px` on both panes is what makes the ratio mean the ratio.** A flex item's basis is
its content by default, and the grow factors share out only what is *left over* after the contents
have been measured — so a pane holding something wide takes its content plus its share, and a split
set to a quarter comes out at a half. The control writes the basis beside the grow factor, together
or not at all. If you replace a pane's `flex` yourself, call `Apply()` afterwards.

⚠ **The panes carry `min-width: 0px`, and without it the bar stops early.** A flex item's automatic
minimum is its content, so a pane holding anything that does not wrap — a long path, a wide table —
refuses to shrink below it and the bar comes to a halt well before the ratio says it should. The pane
is also where `overflow: hidden` belongs: the ratio decides the size and the contents are what has to
give way.

## Accessibility

The split reports ARIA `group` and the bar reports `separator`. ⚠ Not the *focusable* kind of
separator: the bar cannot be reached by Tab and there is no keyboard resize, so a screen-reader user
learns where one pane ends and the next begins and cannot yet move the boundary. That is a real gap
and it is stated rather than papered over — a focusable separator carries a value and a naming
obligation, and giving it the role without the behaviour would be a promise the control does not keep.

## See also

* [Docking panels](docking-panels.md) — when the panes want to be tabs, tear off, and be saved
* [`ControlTheme`](/docs/api/vixen.ui.controls/controltheme) — the user-agent sheet these rules live in
