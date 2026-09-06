---
title: Toolbar, status bar and segmented control
slug: ui/application-bars
kind: guide
area: Core
summary: The three application-shaped controls the editor drew out of bare elements and no application could reach — a toolbar that is one tab stop, a status bar that is a live region, and a row of joined buttons that is one question with several answers.
api: [T:Vixen.Ui.Controls.Toolbar, T:Vixen.Ui.Controls.StatusBar, T:Vixen.Ui.Controls.SegmentedControl, T:Vixen.Ui.Controls.Segment]
tags: [ui, controls, toolbar, accessibility, application]
since: 0.2
status: preview
related: [ui/split-view, ui/commands, ui/accessibility, ui/shortcut-formatting]
---

## What it is

Three strips every desktop application has, each of which existed in the editor as a bare
`UiElement` with a tag name and a stylesheet — visually right, and with no keyboard and no
accessible structure at all.

| Control | Tag | Role | The behaviour that is not CSS |
|---|---|---|---|
| `Toolbar` | `toolbar` | `toolbar` | One tab stop; the arrows move inside it |
| `StatusBar` | `status-bar` | `status` | A live region: a change is announced without moving the focus |
| `SegmentedControl` | `segmented-control` | `radiogroup` | Exclusive choice, wrapping arrows, roving index |

⚠ **`AccessibleRole.Toolbar` had existed with nothing to carry it.** A role in an enum that no
control reports is a role no screen reader ever hears, which is this repository's commonest defect
wearing an accessibility tree.

## What it is for

The three strips a desktop application has, with the behaviour that is not CSS: one tab stop rather
than one per button, arrow keys that move inside the strip, an exclusive choice that wraps, and a
status line a screen reader announces without the focus moving to it.

⚠ **Each existed already as a bare `UiElement` with a tag and a stylesheet** — right to look at, and
with no keyboard and no accessible structure at all. What these controls add is the half a stylesheet
cannot express.

## Using it

Each is a control rather than a tag, so the behaviour comes with it: add one, put its items inside,
and the keyboard, the roles and the live region are already there. The three sections below are the
three strips in turn.

## Toolbar

```csharp no-compile="a fragment; `shell` is a UiElement in a document"
var bar = shell.Add<Toolbar>();
bar.Add<Button>().Label = "Open";
bar.Add<Button>().Label = "Save";
bar.Add<Separator>();
bar.Add<SegmentedControl>();
```

⚠ **One tab stop for the whole strip, and that is the whole reason it is a control.** Fifteen
buttons that are each a tab stop puts fifteen presses between a keyboard user and the document.
Which item the stop is on is `Active`, and it follows the focus however the focus arrived — a user
who clicked the fourth button and tabbed away comes back to the fourth button, which a roving index
maintained only by the arrow keys does not do.

**The arrows move along the strip and wrap**, Left/Right when it runs across and Up/Down when
`Orientation` is `Vertical`. ⚠ It answers only the pair that matches its axis: a strip that took all
four would quietly take Left and Right away from whatever they meant in the view behind it.

`Items` is every focusable descendant in order, so buttons inside a non-focusable group still count.
⚠ **It does not descend into a nested `Toolbar`** — two strips sharing one roving index fight, and
the inner one loses silently.

**No overflow menu and no customisation.** `NSToolbar` has both, and they are what turn a toolbar
from a control into a project. An application that needs overflow puts a button at the end and opens
its own menu. A half-built overflow would be worse than none: a strip that silently drops its last
three buttons at a narrow width is a strip whose verbs vanish with nowhere to look for them.

## StatusBar

```csharp no-compile="a fragment; `shell` is a UiElement in a document"
var status = shell.Add<StatusBar>();
status.Message = "Ready";
status.Trailing.Add<Label>().Text = "Ln 42, Col 7";
```

⚠ **`status` is a live region and that is the entire point.** A screen reader announces a change to
one *without* moving the focus, which is the behaviour a status bar exists to have and which a bare
element with a stylesheet does not get.

It is a container rather than a label because every real status bar has cells: the message is
`Message`, everything else goes in `Trailing`, and `Trailing` is the `ContentHost` so a nested markup
tag lands after the message rather than on top of it.

⚠ **`ContentHost` redirects a nested markup tag and not `element.Add`.** `status.Add<Panel>()` in C#
puts a child on the status bar itself; `status.Trailing.Add<Panel>()` is what a caller means.

## SegmentedControl

```csharp no-compile="a fragment; `bar` is a Toolbar"
var view = bar.Add<SegmentedControl>();
view.AddSegment("list", "List");
view.AddSegment("grid", "Grid");
view.Value = "grid";
view.ValueChanged += (_, value) => Show(value);
```

⚠ **A `radiogroup` of `radio`s, not a row of toggle buttons.** They look identical and are not the
same thing to a screen reader: three toggle buttons are announced as three independent pressed-or-not
buttons, where a segmented control is one question with three answers and has to say "two of three".

⚠ **Clicking the chosen segment again leaves it chosen**, for a radio's reason — otherwise the strip
reaches a state with nothing selected that the keyboard cannot get out of.

**Single selection only.** Multiple selection is a strip of `ToggleButton`s in a `Toolbar`, which is
already reachable; a mode flag here would make `Value` mean two things.

⚠ **`AddSegment`, not `Add`.** A derived one-string overload beats `UiElement.Add(string)` by C#'s
own rule, so `strip.Add("div")` would quietly make a segment labelled "div". Every container in this
set names its own method for the same reason.

## Examples

**A toolbar that is one tab stop.** The arrows move between the buttons; Tab leaves the strip, which
is what stops a twelve-button toolbar from costing twelve presses to walk past:

```vxml no-compile="a fragment; the handlers are the shell's own"
<Toolbar>
  <IconButton Icon="Play" on:Click={Run} />
  <IconButton Icon="Pause" on:Click={Pause} />
</Toolbar>
```

**A status line that announces itself.** It is a live region, so writing to it is the whole of it —
nothing takes the focus away from what the user was doing:

```csharp no-compile="a fragment; `status` is the shell's own"
status.Text = $"Imported {count} assets";
```

## The joins are the group's, not the segments'

`segmented-control` has the border, the radius and `overflow: hidden`; a segment has only a left
border and no radius at all. A per-segment `:first-child`/`:last-child` radius rounds the two ends
*and* leaves the middle segments' corners showing through the group's border.

⚠ **`flex-shrink: 0` on the toolbar and the status bar is not redundant.** `flex-shrink`'s CSS
initial is 1, so a strip with a declared height in a column that runs out of room now *can* be
squeezed to nothing — which looks exactly like the strip never having been built.

## See also

- [Split view](../ui/split-view.md) — the other structural control a shell is built from.
- [Commands and the responder chain](../ui/commands.md) — what a toolbar button sends, and why it
  greys itself out without asking anyone.
- [Accessibility](../ui/accessibility.md) — the roles these report, and why a role nothing carries is
  a role no screen reader hears.
