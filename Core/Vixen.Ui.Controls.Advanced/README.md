# Vixen.Ui.Controls.Advanced

The controls [docs/plan/09](../../docs/plan/09-ui-framework.md) § "Control library" calls "the ones
that prove the framework": `DockingHost`, `TreeView`, `PropertyGrid`, `DataGrid`, `NodeCanvas`,
`CodeEditor`, `Viewport`, `ColorPicker`, `CurveEditor`, `GradientEditor` and `Timeline`.

`Vixen.Ui.Controls` is a set of widgets. These are an application's worth of behaviour each, and
between them they are the reason the framework has reparenting, inline styles, virtualisation and
custom drawing at all — every one of those landed because one of these could not be written without
it.

## What is in it

| | |
|---|---|
| Shell | `DockingHost` — splitters, tab groups, float, drag-to-dock, layout that round-trips through YAML |
| Data | `TreeView`, `DataGrid`, `PropertyGrid` |
| Graphs | `NodeCanvas`, `CurveEditor`, `Timeline` |
| Colour | `ColorPicker`, `GradientEditor` |
| Text | `CodeEditor` |
| 3D | `Viewport` |

## Four decisions the whole set rests on

**Model and view are kept apart, always.** `DockLayout` is what is saved; `NodeGraph`,
`CodeBuffer`, `AnimationCurve` and `Gradient` are what is compiled, evaluated, diffed and checked
in. None of them needs a document, a stylesheet or a font, so a shader graph can be validated on a
build server that has no interface at all. Every structural change edits the model and the view
follows, so what is on screen and what would be saved cannot drift.

**Everything that repeats is a pool.** A tree's rows, a grid's rows *and its cells*, a code editor's
lines and their coloured runs, a canvas's nodes, a timeline's headers: the elements are the ones on
screen, rebound as the view moves. A hundred thousand rows is thirty elements; ten thousand nodes is
a few dozen. ⚠ **The pools only ever grow** — removal is final in this framework, so a pool that
shrank would have to create fresh elements the next time the view got taller.

**What is not a box is drawn.** A slider's thumb was the base set's escape hatch; here it is most of
the picture. Wires, curves, keyframes, the colour field, the gradient bar, the minimap and the axis
gizmo are all `OnDraw`, because each of them is a position that is a multiplication rather than a
layout, and because ten thousand of anything as elements is ten thousand style nodes for a picture
with no text in it. ⚠ **The theme still owns every colour**, through custom properties the control
reads out of the cascade.

**Painting order is document order, and that is a design tool.** A selection has to be *under* the
text and a caret *over* it, so `CodeEditor` puts three siblings either side of its lines. Wires have
to be over the group boxes and under the nodes, so `NodeCanvas` puts a layer between them. Neither
needs a z-index, because the tree already says what is on top.

## The controls

### DockingHost

Splitters, tab groups, float, drag-to-dock with guides and a preview, and a layout that round-trips
through YAML — the exit criterion doc 14 names for Phase 4e.

**A panel is created once and moved thereafter.** Before a rebuild every panel is reparented into a
hidden holder and afterwards into its group, so a panel torn out of one group and dropped into
another keeps its scroll position, its selection and whatever the user had half-typed. That is what
`UiDocument.Reparent` exists for.

**A splitter drag rebuilds nothing.** It writes `flex-grow` on two elements, so moving one is a
restyle of two rather than a tear-down at sixty hertz.

**A panel scrolls vertically by default, and it scrolls *itself*.** `DockPanel.Scrolls` is on unless
something says otherwise; the panel clips with `overflow: hidden` and slides its content children
with `OffsetY`, and grows one `ScrollBar` the first time it overflows. There is no `ScrollView`
inside a panel, and that is the decision rather than an omission:

- **A box would become the containing block for everything in every panel.** Panel content is laid
  out against the panel today — a profiler grid at `height: 34%`, a quad viewport at `height: 49%`,
  a timeline's lanes at `width: 100%`. `scroll-content` is `align-self: flex-start` with a
  shrink-to-fit height, so interposing one re-parents all of those percentages onto a box whose size
  is what the content asked for. `StandardFrameView` carries a written post-mortem of exactly that,
  measured on device.
- **A redirect could not have been transparent.** A panel's whole contract is `Action<DockPanel>` and
  every builder calls `panel.Add<T>()`, which is `UiElement.Add` — not virtual. Two dozen asset
  editors are handed the panel typed as a bare `UiElement` through `IAssetEditorFactory.CreateView`
  and would bypass an override on the panel type entirely. So `Children` still means what it always
  meant: what the builder put there, plus the bar once there has been something to scroll.
- **It is the tab strip's judgement, one axis over.** `DockGroupView` slides its tabs inside a clipped
  viewport rather than holding a `ScrollView`, for the same reason: clipping is a theme declaration
  and an offset is a post-layout translation, and between them that is the whole of scrolling.

**The opt-out is `DockPanel.Fills(element)`**, which walks up to the panel an element is in — because
the thing that knows it fills its box is the view, and the view usually does not have a `DockPanel`.
Two kinds of content take it: anything that sizes a render target or a virtualised window from its
own laid-out box and hit-tests in its own space (a viewport, a node canvas, a timeline, the code
editor), where a scroll offset it does not know about is a constant error in every pick; and anything
that already owns a scroll region with a header deliberately kept outside it (the inspector, the
profiler, the import settings, the frame view, the undo history), where a second one is two bars and
a wheel that moves the wrong one.

**Every box between the host and a panel declares `flex-basis: 0px`.** `flex-grow` only shares out
what is left after the items are measured, so a growing item whose basis is `auto` starts at its
content's height and is never asked to shrink. Before this, a thousand-pixel console produced a
thousand-pixel surface, group, body and panel inside a correctly-sized host: nothing clipped, because
there was nothing to clip. It is the same declaration `DockSplitterView.Apply` already writes onto
the two halves of a split so that a ratio means the ratio it says.

**Floating groups get a real OS window where there can be one.** A window is a `UiSurface` of the
*same* document rather than a document of its own, which is the decision the whole feature rests on:
moving a panel into a torn-off window is then the `Reparent` above, so it keeps its scroll position,
its selection and its half-typed text on the way out. `IUiWindowHost` is the seam — declared in
`Vixen.Ui`, filled by `Vixen.Platform.Ui` — and `UiDocument.CanOpenWindows` is what this host asks.
Where the answer is no (a browser tab, an Android activity, iOS) the same group is drawn as a
rectangle floating inside the host, with the same arrangement and the same saved file.

Three things that follow, none of them obvious:

- **Windows are keyed on the `DockGroupNode`, not on its index in `Layout.Floating`.** Every
  structural change rebuilds the views and the index moves under it, so an index-keyed window blinks
  off and on again the first time you dock something somewhere else.
- **A drag is tracked in desktop space.** Two windows have two coordinate spaces, and a captured
  pointer keeps reporting positions relative to the window the press happened in even once the cursor
  has left it — so both the pointer and every group's rectangle are lifted through
  `IUiWindowHost.TryLocate`. With one window that lift is the identity. Where the host cannot answer,
  docking works inside each window and refuses to drag between them, which is the honest degradation.
- **Closing a torn-off window brings its panels home** rather than closing them. The window's close
  button is a foot away from the panel's, and one of them destroys work while the other rearranges it.

A tab dragged off the host entirely tears out; a drop inside it docks. Both are needed: the gaps
*inside* the arrangement are six-pixel splitters, and floating a panel when a drag misses one would
make a fumble cost the user their layout.

**A tab is a drag handle everywhere, including its title.** A press lands on the deepest element
under it, and a tab's title is a child element — it has to be, or a tab could not also have an icon
— so asking whether the drag's source *is* a `DockTab` left only the few pixels of padding around
the words draggable. The source is walked up to the tab it is in instead, and the walk stops at the
close button: that one is inside the tab too, and a press on it that wandered a few pixels before
letting go would dock the panel somewhere instead of closing it.

**Five guide handles are offered over whatever group the drag is over**, the way Visual Studio's
diamond does, with the one the drop would use lit and the preview showing the rectangle it would
land in. They sit in the middle of the pane, and that is the point of them: proximity to an edge —
the quarter-deep rule `ZoneOf` applies — means the whole middle of a group is "stack it here", which
is right until somebody wants a split and has to guess how close to the edge counts. A handle is
that answer written down, and aiming at one is never the same gesture as aiming at an edge, so it
can be believed over it. Two things follow:

- **The handles' sizes are the code's, not the stylesheet's.** Which one a drop lands on is
  arithmetic against the group's rectangle — the pointer is captured by the tab for the whole drag,
  so nothing here is ever hit-tested — and a sheet that could resize a handle would move the one
  that is drawn away from the one that answers.
- **A pane too small for the cluster is offered none**, rather than handles hanging over its
  neighbours and docking a panel next door. Proximity still answers there, so a narrow pane is
  docked into exactly as it was before any of this existed.

**The tab strip scrolls, with an arrow at each end.** A group holds as many panels as somebody stacked
into it, and without somewhere for the tabs to go flexbox either shrinks every one until no title can
be read or pushes the last of them out of the box — in both cases the panels on the end are ones the
user cannot get back to. `Strip` is the row; `Tabs` inside it is the part that slides, by `OffsetX`
against a clipping viewport. The arrows are disabled rather than hidden at the ends, or the button
under the pointer would be a different button by the time it was pressed again.

- ⚠ **`flex-basis: 0px` on the viewport is what makes an overflow possible.** Without it the viewport
  takes its base size from its content, so it is always exactly as wide as the tabs and never
  overflows — twelve tabs produced a strip two thousand pixels wide, which propagated up through the
  group, the split, the surface and the host. A docking area wider than the window, with arrows that
  never appeared because nothing had overflowed anything. `min-width: 0px` on the host and the surface
  is the other half: a flex item's automatic minimum is its content.
- ⚠ **A group view subscribes to `LayoutFinished` directly rather than through `Control.WhenResized`**,
  which is the case that method documents as not being its own: whether the tabs fit depends on the
  *tabs*, not on the group. It unsubscribes on removal, and it must — every structural change rebuilds
  the views, so a handler left behind would leak one per dock, per drag, per rename.
- **Selecting a panel scrolls its tab into view**, asked for during the rebuild and honoured on the
  pass after it, because a tab that has just been created has no box to measure yet. A strip that
  showed the selected panel's body while its tab sat off the end reads as the selection having been
  lost.

### TreeView

Virtualised rows, lazy children, multi-select, rename in place, drag-reorder with a three-zone drop
indicator. Rows are absolutely positioned at a fixed height, because virtualisation has to know
where row 40 000 is without having measured the 39 999 above it.

### DataGrid

**Virtualised in both directions**, which is the half a tree does not have. A hundred columns of a
hundred thousand rows is ten million cells; what exists is the twenty-odd columns and thirty-odd
rows on screen, and neither pool is rebuilt when the other scrolls.

⚠ **A frozen cell is positioned against the scroll offset, not against the content.** The rows are
inside the scroller and the header is outside it, so the same visual result needs opposite signs —
and that one line is the entire freezing mechanism. There is no second scroller and no second tree.

⚠ **Frozen columns are the leading *n*, not a flag on a column.** Freezing an arbitrary subset raises
a question with no good answer — what happens when a frozen column is dragged to the middle — and
every grid that offers it ends up reordering columns behind the user's back.

⚠ **Sorting and grouping are a view, never a reorder of the items.** The list belongs to the caller,
who is very likely iterating it elsewhere. Through LINQ rather than `List.Sort`, because the
ordering has to be stable: two rows that compare equal must keep their order, or "sort by name, then
by level" stops being a two-key sort.

### PropertyGrid

Editors generated from `Vixen.Core.Reflection` descriptors, several objects at once, mixed-value
states, reset-to-default and search. Generated rather than reflective, so it reads and writes
arbitrary members after trimming and on iOS. Where the targets disagree the editor says so, and
writing into it sets every one of them.

⚠ **The filter box is named as well as prompted.** Its words went to `Placeholder`, and a
`TextField`'s accessible name is `null` on purpose — a placeholder is a hint and it vanishes the
moment there is a value — so the box showed a translated prompt and announced nothing at all. It
reads the same `StringId` for both, on the line under, which is what makes the words shown and the
words announced impossible to have in two different languages.

### NodeCanvas

Infinite pan and zoom, bezier wires, marquee select, snapping, groups and a minimap.

⚠ **Zoom is arithmetic, not a transform.** Nothing in `Vixen.Ui` scales a subtree, so the canvas
converts graph coordinates itself and writes the answer as a position and a size — and writes the
node's `font-size` with it, so that everything the theme expresses in `em` scales too. One number
carries the whole scale.

⚠ **A wire's endpoint is arithmetic too**, from the node's rectangle and the port's index rather
than from a laid-out port's box. The node at the far end of a wire is usually culled and has no
elements at all, so an endpoint read from layout would collapse to the origin exactly when the wire
left the viewport.

⚠ **`NodeGraph` refuses a cycle.** A shader graph is evaluated by walking back from the outputs, and
a cycle is not a graph with a mistake in it but a walk that does not terminate. The moment of
connection is the only place the user can be told which wire was the problem.

**A port may carry a `PortEditor`**, which is a box of digits or a tick in the port's own row for
the value it takes when nothing is wired to it. What the number *is* belongs to whatever document is
being drawn — the canvas holds a picture of it and raises `PortEdited` after the field has already
written it, the same optimistic bargain the wire drag makes.

⚠ **A connected input's editor is hidden rather than dropped**, because the canvas is the thing that
knows a wire was dropped a frame before anything above it recorded anything — and it is what makes
pulling the wire off bring the old number back rather than a zero.

⚠ **The editor lives inside the port row and must not change its height.** The wire arithmetic above
is over one header height and one port pitch; a row that grew for the value in it is a row every wire
on the node misses. The boxes therefore go beside the port's name, the name shrinks and is clipped
before the number is, and the input column takes the width the output column does not need.

⚠ **Two of the canvas's own gestures stand down for it.** A primary press inside a `NodePortEditor`
is left entirely alone — the press lands on the `NodePortView`, so the untouched version starts a
wire from the port the box belongs to — and no keyboard shortcut is claimed while the focus is in a
`TextField`, or Backspace in a value box deletes the node the box is on.

⚠ **The arrows walk the graph and never move a node**, and the node they are on is not the
selection. `Cursor` is where the keyboard is; Enter or Space chooses, with Control and Shift meaning
what they mean to a click. Folding the two together would make a journey across six nodes six
selection changes, and in an editor six entries on an undo stack. Scored by the distance along the
direction plus **twice** the offset across it, which is what makes Right reach the node a reader
would call the one to the right rather than whatever is nearest the corner; moving the cursor pans
the view the least it can, and never zooms.

⚠ **A node is not a tab stop and must not become one.** A `NodeItem` is pooled and rebound as the
canvas pans, so a focus resting on one would be on a different node a moment later — the parking
hazard a `ColorSwatch` palette does not have. The focus stays on the canvas, which points
`aria-activedescendant` at the element currently showing the cursor's node and **re-points it at the
end of every realise**, because which element that is changes without the cursor moving at all.

### CodeEditor

Virtualised lines, pluggable highlighting, line numbers, indentation folding, a diagnostics gutter
and an autocomplete popup.

**Monospace by construction.** A column becomes an x by multiplying, which is what makes hit
testing, the caret, the selection and the scroll width arithmetic. The character cell is *measured*
from a shaped glyph in whatever face the theme chose, rather than declared, so it agrees with the
picture by construction.

⚠ **Highlighting state is cached per line and invalidated from the edit downwards.** A block comment
opened on line 3 changes what line 4 000 is, so the state has to be carried forward — and
recomputing the whole file per keystroke is what makes a highlighter feel slow.

⚠ **Folding is lines missing from the row list**, so virtualisation, the caret's row and the scroll
range all work without knowing that folding exists.

`ICodeTokenizer` is where a `Vixen.Core.Syntax`-backed highlighter plugs in. It is not the default,
because a control assembly that referenced a parser would drag one language's grammar into every
application that wanted a text box with colours in it — and because an editor has to colour a file
that does not parse, which is most of them most of the time somebody is looking at one.

### Viewport

Three jobs, and none of them is drawing a scene: *where and how big* in render pixels, *when that
changed* — which is when a render target has to be recreated — and the input that happens inside it,
in the coordinates a camera controller wants.

⚠ **The size is in render pixels, not layout pixels.** A viewport that handed a renderer its layout
size would produce a soft image on every scaled display and a sharp one on the developer's.

⚠ **Capture does not lock the pointer** and cannot: a pointer lock is a platform request, and this
assembly has no platform. `PointerLockRequested` is what an app head answers.

### ColorPicker

An HSV or OkLCh field, a hue band, an alpha band over a chequerboard, a hexadecimal field, a
palette, an eyedropper and an HDR intensity.

⚠ **The model is the source of truth, not the RGB.** Grey has no hue and black has no saturation, so
a picker that recomputed its axes from the colour would lose which hue the user was on the moment
they dragged the value to nothing — and snap back to red when they dragged it out again. Every
picker that has had that bug has had it for that reason.

**HDR is a multiplier beside a colour, not a colour with big numbers in it.** An artist picks a hue
and then says how bright the light is; keeping the two apart means changing the intensity does not
move the picker and the chromaticity survives a round trip through a value of forty.

⚠ **The eyedropper cannot read the screen and does not pretend to.** Sampling a pixel needs a screen
capture permission on macOS and a compositor protocol on Wayland. `EyedropperRequested` asks;
`Pick` answers.

⚠ **The OkLCh plane is built once per hue, and it used to be built 512 conversions at a time on
every draw.** Chroma is the column, lightness is the row and the hue is the only other input, so a
plane drawn again with the hue unmoved is the same colours — and the loop recomputed all of them,
most through `GamutMap.Map`'s binary search rather than a clamp, because the plane spans chroma to
`MaximumChroma` and much of that is not a colour a monitor can make: at hue 0, 169 of the plane's
272 distinct colours are outside sRGB by construction. A row's bottom stop is also the row below's
top stop, so 512 stops were only ever 272 colours. `PlaneConversions` and `PlaneRebuilds` are what
say so; they are counts and not milliseconds, because a wall-clock budget calibrated on an idle
machine is the thing that flakes.

⚠ **The intensity slider is `LabelledBy` its caption.** The caption is a separate element carrying
a localised string, and a slider beside words nothing related it to announces nothing — the
translation was on screen and unreachable. One relation is the whole fix, and because a relation is
read on demand rather than copied, a re-labelled caption re-labels the slider with no second write.

### CurveEditor

Cubic Hermite between keys, five tangent modes, presets, a pannable graph and a per-pixel-column
sampled curve.

**Tangents are slopes, not control points**, so a key dragged sideways keeps the shape either side.
⚠ **Outside the first and last key the curve holds rather than extrapolating** — a cubic run past
its last key reaches infinity within a second, and an animation sampled one frame past its end would
send whatever it drives into the next county.

⚠ **The value axis points up.** It is the one place in an interface where the mathematical
convention wins, because a graph with its value axis upside down is unreadable.

### GradientEditor

Two rails of stops, a sampled bar, three interpolation spaces and a picker beside the selection.

⚠ **Colour and alpha are separate lists.** A particle that fades out at the end has one alpha stop;
sharing one list would mean duplicating every colour stop to carry the alpha. Every tool that tried
the single list ended up here.

⚠ **Three spaces because there are three right answers.** sRGB is what the designer's tool showed
them, linear is what light does, Oklab is what looks like the fade they drew — and they disagree
visibly, so a gradient that did not record which one it meant could not be reproduced.

### Timeline

Tracks, keyframes, a curve trace, a playhead, a 1-2-5 ruler, frame snapping, drag and marquee.

⚠ **Time and pixels are related by one number.** `PixelsPerSecond` is the zoom and `TimeStart` is
the pan; everything goes through the two, because a timeline that also kept a visible range would
have three numbers that could disagree.

⚠ **Snapping is to frames, not to a grid.** An animation plays back at a frame rate and a key
between two frames plays on one of them anyway, so a pixel grid would put keys where no frame is.

## What the framework grew to make these possible

- **`UiDocument.Reparent`** — moving an element and its subtree to a different parent, rebuilding
  the style slots under the new parent and touching nothing else.
- **`UiElement.SetStyle`** — declarations written on an element, for the lengths no stylesheet was
  given. The store replaces a block in place when the set of properties has not changed, so a drag
  does not allocate per frame.
- **`WheelEvent.Modifiers`** — because Shift-wheel and Ctrl-wheel mean something in every canvas,
  graph and timeline, and a control that had to ask a keyboard what was held *now* would get the
  wrong answer for any event it dealt with a frame later.

## Four flexbox traps, all silent

Each of these cost an afternoon and none of them shows up as an error:

- **A flex item's base size is its content**, so a `ScrollView` meant to fill its parent needs
  `flex-basis: 0px` as well as a `flex-grow`. Without it the viewport grows to the height of
  everything inside it, nothing overflows, the scroll range is zero — and a virtualiser realises
  every row there is. The tree looks right and the process runs out of memory.
- **This layout engine takes Yoga's `flex-shrink` default of zero, not CSS's of one.** A control
  whose content is wider than its parent therefore grows straight out of the window unless it says
  `flex-shrink: 1`. Measured: a `DataGrid` of two hundred columns made itself twenty-four thousand
  pixels wide, which switched its own column virtualiser off without saying so.
- **`min-width` is the same trap on the other axis.** A viewport containing a two-thousand-character
  line is stretched to two thousand characters wide unless something says `min-width: 0px`.
- **A minimum size is applied before free space is shared out**, so a dock group with
  `min-width: 48px` gets 48 pixels *plus* its share of the remainder, and a splitter saved at 25%
  comes back at 28%. What keeps a half from being dragged to nothing is `DockSplitNode`'s ratio
  clamp, which guards without distorting.

## What these say to a screen reader

All eleven controls carry a role, and it costs each of them a **virtual member** rather than a field
— see [docs/guide/ui/accessibility](../../docs/guide/ui/accessibility.md).

| Control | Role | And |
|---|---|---|
| `TreeView` / `TreeRow` | `tree` / `treeitem` | Expandable only where there are children, selected from the row's own `:checked` |
| `DataGrid` | `grid`, and **`treegrid` once it is grouped** | Rows are `row`, cells `gridcell`, headers `columnheader` |
| `DockingHost` | — | The tab strip is a `tablist`, a `DockTab` is a `tab`, a `DockPanel` is a `tabpanel`, and the two point at each other |
| `PropertyGrid` | — | Every generated editor is `LabelledBy` its row, and every reset button is `DescribedBy` it |
| `ColorPicker` | `group` | The hex field, the intensity slider and the eyedropper are all named |
| `ColorInput` | `button` | Expandable, owning its popup |
| `CodeEditor` | `textbox` | Editable, multi-line, read-only when it is |
| `Viewport`, `NodeCanvas`, `CurveEditor`, `GradientEditor`, `Timeline` | `application` | |
| `NodeItem` | `option`, in the `listbox` the canvas's surface carries | Named by the node's title, selected from its own `:checked`, and taken out of the tree entirely while it is parked |

⚠ **`application` is a role with a cost, and it is worth paying for exactly those five.** It asks
assistive technology to stop intercepting the keyboard and pass every key through, which is right for
a surface with a keyboard model no widget vocabulary describes. ⚠ **`CodeEditor` is deliberately not
one**: it is a multi-line text field whose keyboard is the one a screen-reader user already has, and
announcing it as an application would turn off exactly the reading and review commands that make text
editable at all.

⚠ **The grouped grid's role change is not decoration.** `treegrid` is the role whose keyboard model a
screen reader announces the Left and Right arrows for; staying `grid` while the rows collapse leaves
a screen-reader user with no way to know the groups can be opened.

The gate is `AccessibilityTreeTests.cs`, whose last test is a **reflection sweep over the assembly's
type list** rather than a window: every public control with a parameterless constructor built and
held to "a tab stop must be in the accessibility tree". It found three unnamed fields while it was
being written.

## Known gaps

Said out loud rather than left to be discovered:

- ~~**Rows and scroll ranges are one layout pass behind a resize.**~~ `UiDocument.LayoutFinished`
  exists and all six controls are on it. `ScrollView` subscribes directly, because its range is a
  fact about its content rather than about itself; `TreeView`, `DataGrid`, `CodeEditor`, `NodeCanvas`
  and `Viewport` go through `Control.WhenResized`, which gates on the box actually having changed
  size — `CodeEditor.Refresh` walks every line in the buffer, and a frame where nothing moved should
  cost two float comparisons. `Refresh()` stays public on all of them for the caller who has just
  filled a control and wants to read a box before the next pass.
- **No undo, anywhere** — still true of *this* assembly, and ⚠ **the claim that used to follow it,
  that nothing subscribes to the four seams, is refuted.** An undo stack inside a text control can
  only undo typing, and every application that has one wants it to cover more; `CodeBuffer.Changed`,
  `NodeGraph.Changed`, `AnimationCurve.Changed` and `Gradient.Changed` are the seams such a stack
  subscribes to, and the editor's `CommandStack` reaches three of the four:
  - `CodeBuffer.Changed` **is** subscribed, by `CodeDocument` — every keystroke becomes a
    `TextEditCommand` on the document's stack, merged per line.
  - `AnimationCurve.Changed` is not, and does not need to be: an edit reaches the stack through the
    *control*'s `CurveChanged`, which `CurveDrawer` turns into one snapshot per edit session and
    `AnimationClipView` into a `SetCurve`. ⚠ That is not an oversight — a curve is a reference type
    edited in place, so the model event says "it changed" and cannot say what it was before.
  - `NodeGraph.Changed` is subscribed only by `NodeCanvas` reprojecting itself. The editor's
    *editable* graph is `NodeGraphModel`, a different type with a `CommandStack` of its own; this one
    is reached in production only by the two read-only AI projections.
  - ⚠ `Gradient.Changed` is the one real gap, and it is one layer up from undo: **`GradientEditor` has
    no production consumer at all** and no type in the engine has a `Gradient`-typed member, so
    nothing in the editor can edit a gradient, undoably or otherwise.
- ~~**`Viewport` draws a placeholder.**~~ It draws `RenderTarget` through the draw list's image
  command, and falls back to the placeholder colour only when nothing has been rendered into it yet.
  ⚠ `FlipVertically` is **off** by default, and used to be on for a reason that was already handled
  somewhere else: a scene does render with y up and an interface does draw with y down, but both
  backends resolve that where the API is — Vulkan with a negative-height viewport, OpenGL by flipping
  the viewport origin — so a colour target's row zero is already the top of the view. Flipping it
  again mirrored every scene about its horizon, which is nearly invisible in a symmetric scene and
  which broke everything that *measures* the pane: gizmo hit-testing, picking rays and vertical
  camera drags all go through the unmirrored projection, so their error was zero at the centreline
  and the full height of the pane at its edges.
- ~~**`CodeEditor` does not wrap.**~~ `WordWrap` does, and ⚠ **not through `TextLayout`, which is
  what the previous version of this bullet said it was waiting for.** This control is a monospace
  grid by construction — a column becomes an x by multiplying — so the wrap column is
  `viewport ÷ CharacterWidth` exactly, with nothing to measure and nothing to disagree with;
  handing the line to the text stack would measure a width the caret, the selection and the hit test
  do not believe in, and the caret would land beside the character it is on. ⚠ **The seam is the
  row list, and folding had already proved it works**: a collapsed fold is lines missing from `Rows`
  and a wrapped line is a line appearing in it more than once, so the virtualiser, the scroll range,
  the caret's row and the gutter are untouched by either feature. `RowStarts` is which column each
  row begins at, and is all zeroes while wrapping is off — which is what makes every formula reduce
  to the one it had before. Off by default: code is written with the column mattering, and a diff, a
  compiler's column number and a ruler all agree with the unwrapped one.
- ~~**`CodeEditor` has no caret blink.** Blinking needs a host tick, which `Tooltip` and `ToastHost`
  get from `UiDocument.Ticked`.~~ It blinks, and ⚠ **not from `UiDocument.Ticked`, which is what this
  bullet used to say it was waiting for.** A tooltip subscribes because the moment it is waiting for
  arrives when nothing is happening; a caret is only interesting on a frame that is being drawn
  anyway, and `UiDocument.Draw` rebuilds the list and diffs it every frame. So `DrawCaret` reads
  `Document.Now` and decides, which costs nothing on the frames it is not reached — and it is not
  reached at all unless the editor has the focus. A subscription would have made every frame of a
  shell with an unfocused code pane on it differ, and `EditorStillnessTests` measures that it does
  not. ⚠ **`TextField` did not blink either** and nothing had written that down; both do now, four
  lines each rather than through a shared base. The phase is measured from the last time the caret
  moved, so typing holds it solid; `CaretBlink` is the half period and `TimeSpan.Zero` is a solid
  caret, which is what a reduced-motion setting wants.
- ~~**`OkLch.ToSrgb` clamps per channel**, which shifts the hue rather than reducing the chroma.~~
  **It maps.** ⚠ **The mapper had been written, tested and in production use the whole time** —
  `Vixen.Core.Mathematics/GamutMap.cs`, CSS Color 4's binary search with local MINDE, already called
  from `UiGeometryBuilder` and `StyleValueParser` — and this file clamped beside it with a comment
  saying one was owed. The commonest defect in this tree, in miniature: not a missing algorithm, a
  missing call. `IsInGamut` still says whether a colour needed the repair. The clamp that survives
  bounds a channel at `1 + 1e-5`, which is where the search settles, and is arithmetic rather than a
  colour decision.
- ~~**`StyleTree.AppendChild` is O(children) per append**, so an element with tens of thousands of
  children is quadratic.~~ **Fixed, and this bullet outlived the fix.** A child run reserves capacity
  beyond its count and doubles on overflow — a run that is last in the arena grows where it stands
  with no copy at any size, and one that is boxed in relocates at twice the room — so the copies fall
  off geometrically and the amortised cost is constant (`Vixen.Ui.Styling/StyleTree.cs`, `AppendChild`).
  ⚠ **The field for it had been there the whole time**: `ChildCapacity` existed, was set to zero and
  was read by nothing. `Vixen.Ui.Styling.Tests/ChildArenaTests.cs` counts arena slots rather than
  timing the loop, because a clock would fail on a loaded machine and pass on an idle one.
- ~~**Nested struct members are shown read-only in `PropertyGrid`.**~~ They are editable, and the
  `ref`-accessor argument this used to make was wrong. An accessor takes its instance as `object`, so
  writing into the box a struct member comes back in changes a copy — true, and not the end of it:
  setting the leaf and then writing each *owner* into its own owner, innermost first, is
  read-modify-write and needs nothing from the descriptor. What was missing is that the grid kept a
  member where it needed a path, so `PropertyRow` carries the chain from the target to the member and
  a member with a registered descriptor that nothing else claimed expands into rows of its own.
- **`Canvas2D` is not here.** It is doc 09's P2 with no editor consumer; see
  `Samples/06-CanvasStress`.

## The theme

**The sheet is `AdvancedTheme.vcss`, a file beside the loader**, embedded by the `**/*.vcss` glob in
`Vixen.Ui.targets` and read back by `AdvancedTheme.Css`. It was 969 lines of CSS in a `const string`
until it was moved out byte for byte; `AdvancedTheme.cs` is now the loader and nothing else.

`AdvancedTheme.Install` loads this sheet and **not** the base theme. Every colour in it is a
`var(--…)` against a token `ControlTheme` declares, so an application needs both, in that order.

⚠ **`.hidden { display: none }` is declared here and is reached from other assemblies** — the
editor's import views use it by class name. `SharedThemeTests` records that as the price of an
assembly boundary in a token system, so renaming it is not a local change.

Licensed under Apache-2.0.
