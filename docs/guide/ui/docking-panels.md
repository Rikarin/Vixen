---
title: Docking panels
slug: ui/docking-panels
kind: guide
area: Core
summary: DockingHost is splitters, tab groups, tear-off windows and a layout that round-trips through YAML; DockPanel is one panel in it, and the thing most worth knowing about a panel is that it scrolls itself — no ScrollView is involved, and Fills is how a view that must not scroll says so.
api: [T:Vixen.Ui.Controls.Advanced.DockingHost, T:Vixen.Ui.Controls.Advanced.DockPanel]
tags: [ui, controls, docking, layout, scrolling, editor]
since: 0.2
status: preview
related: [ui/desktop-application]
---

## What it is

`DockingHost` is the arrangement an editor is: splitters, tab groups, drag-to-dock with guides and a
live preview, panels torn off into real OS windows, and a layout that saves and reloads as YAML.
`DockPanel` is one panel in it — a titled box with a tab, a close button and content somebody built.

```csharp no-compile="a fragment; `root` is the caller's element"
var host = root.Add<DockingHost>();

var hierarchy = host.AddPanel("hierarchy", "Hierarchy");
var inspector = host.AddPanel("inspector", "Inspector");

hierarchy.Add<TreeView>();
inspector.Add<PropertyGrid>();

// Panels added to a host land in one tab group. Docking is what puts them side by side.
host.Dock("inspector", host.Layout.Groups()[0], DockZone.Right);
```

⚠ **`AddPanel` alone gives you tabs, not a layout.** Five panels added and never docked are five tabs
of one group, four of them hidden and laying out to nothing. That is the correct default — a panel
has to be somewhere before it can be moved — but a screenshot of it looks like four panels that
failed to draw.

## What it is for

Anything whose arrangement belongs to the user rather than to the author. The editor is the case it
was built for, and it is the reason the panel is a *panel* rather than a group box: a panel is
created once and moved thereafter. Before every rebuild each panel is reparented into a hidden holder
and afterwards into its new group, so a panel dragged out of one group and dropped into another keeps
its scroll position, its selection and whatever the user had half-typed.

The same property is what makes tear-off windows affordable. A floating group gets a real OS window
where the platform has one, and that window is a `UiSurface` of the **same** document rather than a
document of its own — so moving a panel into it is the same reparent, and nothing is rebuilt. Where
there can be no second window (a browser tab, an Android activity, iOS) the same group is drawn as a
rectangle floating inside the host, with the same arrangement and the same saved file.

You do not want a docking host for a fixed three-pane tool. Splitters and tab strips are `Splitter`
and `TabView`; the host is for the case where the user gets to disagree with you about where things
go, and it costs a layout model, a drag protocol and a serialisation format to offer that.

## Using it

### A panel scrolls itself, and there is no `ScrollView` in it

`DockPanel.Scrolls` is on unless something says otherwise. The panel clips with `overflow: hidden`,
slides its content children with `OffsetY`, and grows one `ScrollBar` the first time the content
overflows. Between them — a theme declaration and a post-layout translation — that is the whole of
scrolling.

⚠ **That is a decision, not a missing feature, and three things follow from it.**

* **A `ScrollView` would become the containing block for everything in every panel.** Panel content is
  laid out against the *panel* today: a profiler grid at `height: 34%`, a quad viewport at
  `height: 49%`, a timeline's lanes at `width: 100%`. A scroll view's content box is
  `align-self: flex-start` with a shrink-to-fit height, so interposing one re-parents every one of
  those percentages onto a box whose size is whatever the content asked for.
* **A redirect could not have been transparent.** A panel's contract is `Action<DockPanel>` and every
  builder calls `panel.Add<T>()`, which is `UiElement.Add` and is not virtual. Two dozen asset editors
  are handed the panel typed as a bare `UiElement`, so an override on the panel type would be bypassed
  entirely. `Children` therefore still means what it always meant: what the builder put there, plus
  the bar once there has been something to scroll.
* **`Bar` is `null` until the first overflow and is kept afterwards**, hidden by a class rather than
  removed — a bar created and destroyed as content grew and shrank would restructure the tree on a
  layout pass and take the thumb out from under whoever was dragging it.

### `Fills` is the opt-out, and it is asked of the element

```csharp no-compile="a fragment; `view` is the element an editor built"
// Anything inside the panel, or the panel itself. Walks up and switches the panel's scrolling off.
DockPanel.Fills(view);
```

⚠ **It walks up rather than being a property, because the caller usually does not have a
`DockPanel`.** An asset editor's `CreateView` is handed "where the controls go" as a bare
`UiElement`, which is right — an editor view has no business knowing it is in a dock — and a factory
that had to cast would silently stop opting out the day somebody hosted it in a splitter instead. The
question is asked about an element because the thing that knows it fills its box is the view, not the
box.

Two kinds of content take it:

* **Anything that sizes a render target or a virtualised window from its own laid-out box** and
  hit-tests in its own space — a viewport, a node canvas, a timeline, the code editor. A scroll offset
  it does not know about is a constant error in every pick.
* **Anything that already owns a scroll region** with a header deliberately kept outside it — the
  inspector, the profiler, the import settings, the frame view, the undo history. A second one is two
  bars and a wheel that moves the wrong one.

⚠ **The class follows the property.** The clipping and the containing block are theme rules keyed on
`.scrolls`, so a panel whose class said one thing while its offsets said the other would slide its
content out from under an unclipped box and draw it over its neighbours. Set `Scrolls`; never set the
class.

### Every box between the host and a panel declares `flex-basis: 0px`

`flex-grow` only shares out what is left *after* the items are measured, so a growing item whose basis
is `auto` starts at its content's height and is never asked to shrink. Before this declaration
existed, a thousand-pixel console produced a thousand-pixel surface, group, body and panel inside a
correctly-sized host — and nothing clipped, because there was nothing to clip. If you interpose a box
of your own between the host and a panel, it needs the same declaration:

```vcss
.my-dock-wrapper { flex-basis: 0px; flex-grow: 1; }
```

### Saving and restoring the arrangement

```csharp no-compile="a fragment; `settings` is the caller's store"
settings.Layout = host.Save();

// …and on the way back in. Panels are matched to the saved tree by their id.
host.Load(settings.Layout);
```

`Save` and `Load` go through `DockLayout`, so the file names panel ids and the tree they sit in — not
element identities. A panel the saved layout names and the application no longer creates is dropped;
one the application creates and the layout does not name lands in the default group.

## Examples

**An editor shell**, which is the composition the framework's own performance gate is built on: five
docked panels, a viewport, a graph and a virtualised table, all in one document.

```csharp no-compile="a fragment; the panel contents are the caller's controls"
var host = document.Root.Add<DockingHost>();

foreach (var (id, title) in Panels) {
    host.AddPanel(id, title);
}

var centre = host.Layout.Groups()[0];

host.Dock("inspector", centre, DockZone.Right);
host.Dock("table", host.Layout.Groups()[0], DockZone.Bottom);
host.Dock("graph", host.Layout.Groups()[0], DockZone.Right);
```

⚠ Each `Dock` rebuilds the tree, so `Layout.Groups()[0]` is re-read between calls rather than held —
the group object from before a dock is not the group that is there after it.

**A viewport panel that must not scroll**, which is the commonest thing to get wrong. The viewport
sizes its render target from its own box, so the opt-out has to happen when the view is built and not
when it first overflows:

```csharp no-compile="a fragment; `Viewport` is Vixen.Ui.Controls.Advanced.Viewport"
var panel = host.AddPanel("scene", "Scene");
var viewport = panel.Add<Viewport>();

DockPanel.Fills(viewport);
```

**Scrolling a panel programmatically**, which is what a search result or a validation error wants:

```csharp no-compile="a fragment; `row` is an element somewhere in the panel"
panel.Reveal(row);
```

`Reveal` scrolls the least distance that brings the element into view — the minimum movement rather
than centring, because centring on every focus change makes a form jump under somebody tabbing down
it one field at a time. It returns immediately when the panel does not overflow. `ScrollTo` and
`Scroll` are the absolute and relative forms underneath it, both clamped to `MaximumScroll`.

⚠ **A panel that opts out *after* it has scrolled is put back**, which is exactly what a view calling
`Fills` from its own creation looks like — one pass after the panel was made. `Refresh` resets the
offset in that case rather than merely doing nothing, because otherwise the content would stay pushed
up with no bar left to bring it back.

## See also

* [Running a UI application](desktop-application.md) — the host and the frame loop a docking host
  lives inside.
* [`DockLayout`](/docs/api/vixen.ui.controls.advanced/docklayout) — the tree `Save` and `Load`
  round-trip.
* [`ScrollBar`](/docs/api/vixen.ui.controls/scrollbar) — the bar a panel grows, and the one control
  the scrolling contract does use.
