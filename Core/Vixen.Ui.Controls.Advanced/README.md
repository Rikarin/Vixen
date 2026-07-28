# Vixen.Ui.Controls.Advanced

The three controls [docs/plan/09](../../docs/plan/09-ui-framework.md) § "Control library" calls "the
ones that prove the framework": `DockingHost`, `TreeView` and `PropertyGrid`.

`Vixen.Ui.Controls` is a set of widgets. These are three applications' worth of behaviour each, and
between them they are the reason the framework has reparenting, inline styles and virtualisation at
all — every one of those landed because one of these could not be written without it.

## DockingHost

Splitters, tab groups, float, drag-to-dock with a preview, and a layout that round-trips through
YAML — the exit criterion doc 14 names for Phase 4e.

**Two things kept apart on purpose.** `DockLayout` is the arrangement: a tree of binary splits and
tab groups that is saved, restored, reset to a preset and compared. `DockingHost` is the elements
that show it. Every structural change edits the model and rebuilds the views from it, so what is on
screen and what would be saved cannot drift.

**A panel is created once and moved thereafter.** Before a rebuild every panel is reparented into a
hidden holder and afterwards into its group, so a panel torn out of one group and dropped into
another keeps its scroll position, its selection and whatever the user had half-typed. That is what
`UiDocument.Reparent` exists for, and a host that rebuilt its panels would pass every structural test
in the suite while losing the user's work.

**A splitter drag rebuilds nothing.** It writes `flex-grow` on two elements — the halves are flex
items and the ratio is their grow factor — so moving one is a restyle of two elements rather than a
tear-down at sixty hertz.

Floating groups float *within the document*. Doc 11 asks the editor for "undock to a separate OS
window"; a second window is a second surface, swapchain and input queue, which belong to
`Vixen.Platform` and the app head. `DockFloat` is the record such a head would be handed.

## TreeView

Virtualised rows, lazy children, multi-select, rename in place, and drag-reorder with a three-zone
drop indicator.

**A hundred thousand nodes is a hundred thousand `TreeNode`s and about thirty `TreeRow`s.** The rows
are a pool the size of the viewport, rebound as the view scrolls — the row that leaves the top is the
row that appears at the bottom, with a different node in it.

Rows are absolutely positioned at a fixed height, because virtualisation has to know where row 40 000
is without having measured the 39 999 above it. Variable-height rows need a running-sum index and are
a different control.

The three drop zones — the top quarter, the middle half, the bottom quarter of a row — are what let a
drag say "beside this" rather than only "inside this", which is most of what reordering a hierarchy
is.

## PropertyGrid

Editors generated from `Vixen.Core.Reflection` descriptors, several objects at once, mixed-value
states, reset-to-default and search.

**Generated, not reflective.** A `MemberDescriptor`'s accessors are lambdas over a cast, so this
reads and writes arbitrary members after trimming and on iOS. An inspector built on
`PropertyInfo.GetValue` cannot.

**Where the targets disagree, the editor says so** — an indeterminate checkbox, an empty field with
an em dash for a placeholder — and writing into it sets every one of them. Showing the first object's
value as though it were the answer is the bug this exists to avoid.

The type decides the editor and the presentation refines it: a `float` is a numeric field, a `float`
with both ends of a range declared is a slider. Anything with no editor is shown read-only rather
than omitted.

## What the framework grew to make these possible

- **`UiDocument.Reparent`** — moving an element and its subtree to a different parent. The style
  slots are rebuilt under the new parent (slot order is depth order, and three passes read it that
  way); the elements, their handlers, their children and their layout nodes are untouched.
- **`UiElement.SetStyle`** — declarations written on an element. A splitter at 37% and a virtualised
  row at y = 880 000 are lengths no stylesheet was given. The store replaces a block in place when
  the set of properties has not changed, so a drag does not allocate per frame.

## Two flexbox traps worth knowing

Both cost an afternoon here and both are silent:

- **A flex item's base size is its content**, so a `ScrollView` that is meant to fill its parent needs
  `flex-basis: 0px` as well as a `flex-grow`. Without it the viewport grows to the height of
  everything inside it, nothing ever overflows, the scroll range is zero — and a virtualiser realises
  every row there is. The tree looks right and the process runs out of memory.
- **A minimum size is applied before the free space is shared out**, so a dock group with
  `min-width: 48px` gets 48 pixels *plus* its share of the remainder, and a splitter saved at 25%
  comes back at 28%. What keeps a half from being dragged to nothing is `DockSplitNode`'s ratio
  clamp, which guards without distorting.

## Known gaps

- **Rows and scroll ranges are one layout pass behind a resize.** `Refresh()` is the answer today;
  the real fix is a "layout finished" callback on `UiDocument`, which `ScrollView` wants for the same
  reason.
- **`StyleTree.AppendChild` is O(children) per append**, so an element with tens of thousands of
  children is quadratic. Virtualisation keeps every control here well clear of it; a `DataGrid` with
  frozen columns may not be.
- **Nested struct members are shown read-only.** The descriptor's accessors pass values as `object`,
  so editing one would edit a box nothing holds. Closing it needs `ref` accessors.
- **`DataGrid`, `NodeCanvas`, `Timeline`, `CurveEditor`, `ColorPicker`, `GradientEditor`, `Viewport`
  and `CodeEditor`** are the rest of doc 09's advanced table and belong to Phase 6.
