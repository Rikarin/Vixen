# Vixen.Ui

The UI framework proper: an element tree, the stylesheets that describe it, and the pass that turns
one into geometry.

`Vixen.Ui.Styling` decides which declaration wins without knowing what a length measures.
`Vixen.Ui.Layout` measures without knowing where its numbers came from. Neither references the other,
which is what keeps a flexbox engine usable without a stylesheet and a cascade testable without a
layout — and it leaves a gap that something has to close. This closes it, and then puts a tree on
top.

## State

| | |
|---|---|
| `LengthContext` | What a relative length is relative to: the element's font size, the root's, the viewport. |
| `LayoutStyleBuilder` | `ComputedStyle` → `LayoutStyle`. Every layout-affecting property, the nine CSS edges, and the font-size chain. |
| `UiElement` | One node. A class, holding no geometry and no style — a handle into the two stores that do. |
| `UiDocument` | The tree, its stylesheets, and the four-walk pass. |
| `[UiProperty]` | Generated properties with defaults, coercion, change callbacks, inheritance and a runtime identity. |
| `UiDocument.HitTest` | What is under a point, front to back, with `pointer-events` and `overflow` honoured. |
| `EventRouter` | Capture, target, bubble, `Handled`, and pointer capture. |
| `DrawList`, `DrawListBuilder` | Backgrounds, borders, radii and clips as commands, diffed frame to frame. |
| `UiDocument.Focus`, `MoveFocus` | Focus, focus scopes, and HTML's tab order. |
| `UiDocument.FindInDirection` | Arrow navigation over the layout, by the beam model. |
| `GestureRecognizer` | Taps with a count, long presses and drags from one pointer; pinch and rotation from two, as one `TransformEvent`. |
| `visibility`, `opacity` | Honoured by the draw list: hidden elements are not painted but keep their space and their subtree; opacity multiplies down the tree. |
| `FontRegistry`, `TextRun`, `TextLine`, `TextLayout` | `font-family` → a fallback chain, a face per character, shaping through a cache, measurement into layout, glyphs into the draw list. |
| `PathBuilder`, `OnDraw` | Lines, curves, fills and strokes for the controls a stylesheet cannot describe. |
| `DrawBatcher` | Contiguous, order-preserving, maximal runs a renderer can submit as one. |
| `UiDocument.Move` | Reordering a sibling in all three stores, so `:nth-child` moves with it. |
| `Component`, `BuildContext` | What a compiled `.vxml` calls: elements, effects, branches, keyed lists, events, slots. |
| `KeyEvent`, `TextInputEvent` | Keys routed from the focus outwards; typed text as its own event. Tab is the document's default, after the route. |
| `UiDocument.Track` | `:hover` and `:active` on the ancestor chain, `Entered`/`Exited` per element crossed, `:focus-visible` from how the focus arrived. |
| `WheelEvent` | Hit-tested and bubbling, so nested scrolling chains on `Handled` rather than on a rule. Carries `Modifiers`, because Ctrl-wheel means zoom in every canvas and timeline ever written. |
| `UiElement.OnCreated`, `TagName` | The constructor a control cannot have, and the element name a type answers to. |
| `UiElement.OffsetX/Y` | A translation applied after layout — scrolling, popups and drag previews, at the cost of a walk. |
| `translate` (CSS) | The declarative half of the same idea, resolved by `TranslationReader` and added into the same sum. Separate from `OffsetX` on purpose: a stylesheet must not be able to erase a scroll position. `scale` and `rotate` are refused — a `DrawCommand` is an axis-aligned rectangle. |
| `UiElement.SetStyle` | Declarations written on an element, for the lengths no stylesheet was given: a splitter's ratio, a virtualised row's position. |
| `UiDocument.Reparent` | Moving a subtree to a different parent: fresh style slots, the same elements. What docking and drag-and-drop between lists are made of. |
| Access keys | ⏳ |

## Focus

`Focusable`, `TabIndex` and `IsFocusScope` are themselves `[UiProperty]`s, which is the property
system's first real user rather than a test of it.

**HTML's tab order, implemented faithfully rather than sanely.** A positive `TabIndex` comes before
*every* zero, in numeric order — so one element written at the bottom of a form jumps to the front of
it. Zero is document order; negative is focusable but not a stop. A framework that quietly
reinterprets this produces a tab order nobody can predict from the markup.

The sort is stable, and that is not decoration: two elements sharing a positive index must stay in
document order relative to each other, or the tab order changes with how many elements are on the
page — a bug nobody can reproduce.

**Tab stays inside the innermost focus scope and wraps there**, which is what makes a dialog modal to
the keyboard.

`:focus` and `:focus-within` are set on the style tree, so a focus ring is a stylesheet's business
rather than a special case in the renderer.

**A press that lands on nothing focusable takes the focus away.** Which control a press *gives* the
focus to is that control's own decision — some decline it, a `NumericInput` being scrubbed among
them — but the other half of the rule belongs to the document, because no control is in a position
to notice that the user has clicked somewhere else. The test is the whole ancestor chain rather than
the element under the pointer: a press on a field lands on the part that draws its text, which is not
itself focusable. A press that captures the pointer is exempt too — capture is how a control says
the press *began* something, and a field must not lose its caret because the scrollbar beside it was
dragged.

## Arrow navigation

Tab walks an *order* — a list the document decides in advance. An arrow walks a *layout*, decided by
where things actually ended up. Two different questions that move the same focus, which is why
`NavigationDirection` is its own enum rather than two more members of `FocusDirection`.

**The beam model, and it has no constant to tune.** A candidate has to start past the edge the arrow
points at. Among those, the ones whose other axis overlaps this element's are *in the beam*, and any
of them beats any candidate outside it however close that one is; inside the beam nearest along the
axis wins, outside it nearest by straight line between the two rectangles.

The alternative is a weighted score — distance along plus some multiple of distance across — and the
multiple is the problem. It has no principled value, so it gets tuned until the layouts someone
happened to test behave, and Down drifts diagonally in the layouts they did not.

⚠ **Touching is not overlapping.** The beam test is a strictly positive overlap, and it has to be:
two cells of a grid share an edge exactly, so a non-strict test puts the diagonal neighbour in the
beam alongside the one directly below and the grid navigates sideways.

**An element's own focusable children are in no direction from it**, and nothing had to say so — they
are inside it, so they are past none of its edges. Entering a group is a separate idea from moving
between things, and conflating them makes Right mean two things.

**Arrows do not wrap.** Tab is a cycle because an order has no far end; an arrow points at somewhere,
and running out of somewhere is a wall. Holding Down in a list that wrapped would never settle.

⚠ Distance is measured between the *rectangles*, not between their centres. The centre metric says a
wide element eighty pixels away is nearer than a narrow one ten pixels away, because distance to a
shape is distance to the shape.

## Gestures

**Time arrives on the event rather than from a clock the recogniser reads.** One that calls
`DateTime.Now` cannot be tested without sleeping, cannot replay a recorded trace, and reports a
different gesture when a breakpoint holds the frame — and the platform layer already knows what time
the input happened, which is a better answer than what time anything downstream got round to asking.

**A long press is the one gesture that fires because nothing happened**, so `Tick` exists: a
recogniser fed only by input cannot produce it, because there is no input to be fed.

⚠ **Slop is one-way.** Once a press has wandered far enough to be a drag it can never be a tap again,
even when the pointer comes back to where it started — which it does at the end of every flick that
overshoots and settles. Asking how far the pointer is from the press *now* fires a tap at the end of
a scroll, which is a list that scrolls and then opens whatever stopped under the finger.

**A double tap raises `TapEvent` twice, counting up**, rather than raising a different event.
Splitting them forces every handler to answer "is a double tap also two taps", which has no general
answer — a button wants both, a rename wants only the second. This is what the web does, for the same
reason.

**Every gesture goes to the element the press landed on**, for its whole life. That is pointer
capture's rule and it is here for pointer capture's reason; the two coexist rather than duplicate,
because capture redirects raw events and this remembers a target already decided.

**One pointer taps, presses and drags; two transform.** Two fingers on two different controls stay
two independent gestures, which is right. Two that start moving *relative to each other* become one
`TransformEvent` carrying a scale and a rotation — one event rather than a pinch and a rotate,
because they are computed from the same pair on the same frame and cannot occur apart.

⚠ **Starting one cancels the drags those fingers had begun**, and neither produces a tap or a long
press afterwards. A map that both panned and zoomed from the same two fingers moves twice as far as
either gesture asked for, so the suppression is as much of the feature as the arithmetic.

⚠ **The gesture goes to the nearest element containing both fingers**, not to the first one's target.
Two fingers pinching a map land on two different tiles, and a gesture delivered to one tile is one
the map never hears about.

⚠ **The rotation is accumulated, not wrapped.** `Atan2` returns in (-π, π], so an angle measured
against the start jumps a full turn when the fingers pass the wrap point; each sample is unwrapped
against the previous one instead, and a gesture spun twice round reports 4π.

⚠ **Two, not more.** A third finger arriving during a transform is ignored rather than folded in.
Three-finger gestures have no agreed meaning across platforms, and averaging an arbitrary number of
pointers into one scale is an approximation worse than the gap.

## Text

`font-family` names a face in the `FontRegistry`, the string is shaped through the document's cache,
the layout tree asks the shaping how big it is, and the draw list gets a `Text` command naming a
range of one glyph buffer. Four things that were built separately, joined.

**Registered rather than discovered.** Nothing walks the machine's font directories: a game ships its
fonts, and an interface laid out by whatever the operating system happened to have installed lays out
differently on every machine it runs on.

**Three types, one for each thing a line of text is made of.** A `TextRun` is one face; a `TextLine`
is the runs sharing a baseline; a `TextLayout` is the lines stacked down the page. Most text is one
line of one run and costs no more than it did when that was the only shape available.

**Wrapping happens here because the widths do.** A paragraph in two faces has no single design-unit
scale, so `Vixen.Ui.Text`'s `LineWrapper` cannot measure it from a `ShapedText` — the per-character
advances are assembled a run at a time in pixels and handed to the overload that takes them. Break
opportunities are UAX#14's and are about the characters. `white-space: nowrap` and `overflow-wrap:
anywhere` reach it from the cascade.

⚠ **Each wrapped line is re-shaped on its own**, which is what a line break *is*: a ligature does not
cross one and an Arabic word unjoins at one.

**A declaration is a fallback chain, and a line is a list of runs.** `font-family: Inter, Noto Sans
JP` means both faces in that order, and `FontRegistry.Cover` hands each grapheme cluster to the first
that draws all of it — so one element's text can be in several faces at once. `TextLine` is that list
and `TextRun` is one face of it; a draw command names one font, so a mixed line is a command each.

⚠ **Composition happens in pixels, and that is not an implementation detail.** A 1000-unit face and a
2048-unit face measure an em differently, so two advances from different fonts cannot be added at
all. It is why `Vixen.Ui.Text`'s size-independent `ShapedText` stays single-font and the run list
lives up here.

⚠ **Per cluster, not per code point.** Splitting a base letter from its combining mark puts the
accent at a pen position derived from another font's em — a floating accent, where one visible tofu
would have been the better failure. A cluster no face covers whole goes to the head of the chain.

`AddFallback` is the tail behind every declaration: the emoji or CJK face an application wants
everywhere and should not have to write into each rule. `Default` keeps its narrower meaning — a
substitute for a declaration that named nothing registered, in *front* of the fallbacks rather than
behind them.

⚠ **Registering a face re-measures the text that is already laid out.** `FontRegistry.Revision`
moves, `UiElement.Line` drops the runs it shaped against the old chain, and `UiDocument.Update`
dirties the layout node of every element that measures its own text — before its "is anything dirty"
check, because a registration is the one change that leaves the document otherwise clean. All three
are needed and the last is the one that is easy to miss: a line is rebuilt only when the measure
function asks for one, and the measure function runs only for a dirty node, so without it a host that
builds its interface and *then* installs a font gets labels that measured zero and keep the zero for
the life of the document — the right strings, the right colour, nought pixels wide.

**A family is a set of variants, and a face's weight and slant are stated rather than sniffed.** They
could be read from the file's `OS/2` table, and that would be the same mistake in miniature as
walking the font directories: a shipped asset whose metadata disagrees with what the designer meant
would silently pick the wrong face, and the fix would be editing a binary.

**Matching is CSS Fonts 4 §5.2, not nearest-neighbour** — which is what everybody writes first and is
wrong in the middle of the scale. The slant is settled before the weight, because an italic at the
wrong weight answers `italic` better than an upright at the right one. Then: an exact weight wins;
below 500 the search runs downwards before upwards and above 500 the other way; and ⚠ **400 checks
500 first**, so a family with a 300 and a 500 answers `font-weight: normal` with the *500*. That last
one is the asymmetry nearest-neighbour gets wrong half the time, and it has a test of its own.

⚠ **`lighter` and `bolder` are not read** and fall through to regular. They are relative to the
parent's *computed* weight, which this cascade does not have — it inherits specified values, so the
parent's declaration might itself be `bolder` and the chain has no bottom. Owed with the
computed-value stage, and left out rather than approximated as "one step from 400", which would be
right only for an element whose parent said nothing.

⚠ **An element with text cannot have children**, and the layout tree is what says so: a node that
measures itself and also has children has its size decided twice, by two rules that do not have to
agree. So a text element is a leaf, full stop, and mixed content is what the owed run list is for.

**`text-align` is an offset on the run's origin**, which works precisely because a run is one line:
there is one origin to move and its width is already measured. It stops working the day text wraps,
and at that point alignment belongs to whatever breaks the lines. `start` and `end` resolve against
`direction`, the same property the layout resolves its logical edges with, so `text-end` and `pe-2`
land on the same side of a mirrored panel. Negative slack is left alone — text wider than its box
overflows from the start edge whatever the alignment says, because centring it would hide the
beginning of the string, and the beginning is what a reader needs to see what was cut off.

⚠ **`letter-spacing` is added per cluster, not per glyph.** A combining mark is its own glyph and the
same cluster as the letter it sits on, so the per-glyph version spaces an accented `é` as two
characters and pushes the accent off the letter. It is also invisible in Latin: against `AB` the two
implementations agree exactly, which is why the test for it uses a Kannada syllable whose five code
points shape to more glyphs than clusters. Tracking reaches the *measure* as well as the drawing, or
an element sized from text it then drew wider would clip its own last letter.

⚠ **Tracking is added after the last character too**, which is what CSS specifies and every browser
does — so centred text with a wide tracking sits half a step left of true centre. Matched rather than
corrected, on the grounds that a toolkit which quietly disagrees with the specification is harder to
reason about than one that reproduces a known wart.

**A line box is `line-height` tall and the glyphs sit in the middle of it.** CSS's *half-leading*:
the text occupies ascender-plus-descender and the difference between that and the line box is split
evenly above and below. Putting it all underneath is what makes a generous `line-height` look like a
top margin, which then gets called a padding bug for a week. Half of a *negative* leading is negative
and that is correct too — a line height smaller than the glyphs crops them evenly at both ends.

## The cursor

`UiDocument.Cursor` is what the pointer should look like where it is, resolved from the hovered
element's computed style and read once a frame by whoever owns the window.

**`UiCursor` rather than the platform's `CursorShape`,** because this assembly cannot see
`Vixen.Platform` and should not — a UI tree that knew about windows would be a tree that could only
be shown in one. The mapping is not one to one in either direction either: `col-resize` and
`ew-resize` are one shape on every desktop and two different statements in a stylesheet.

**No walk up the tree, because `cursor` inherits.** The hovered element's computed style already
carries whatever its nearest ancestor with a declaration said. So an element covering its parent does
*not* get the parent's cursor unless it inherited it — which is exactly the CSS rule, and is why a
button inside a draggable panel can say `cursor: pointer` and be believed.

⚠ **The frame diff has to cover the side buffer.** A command names a *range* of the glyph array, so
two frames whose text changed from one word to another of the same length hold byte-identical
commands and completely different glyphs. Comparing commands alone, the label changes and the version
does not.

**The y is negated on the way in.** Shaping puts y positive upwards, because that is how a font's
design grid is drawn; the draw list is in document space. Invisible on Latin — every glyph sits on
the baseline at a zero offset — and it flips every mark in Arabic, Devanagari and Tai Tham to the
wrong side of its letter. The test that guards it is written in Tai Tham for exactly that reason; in
Latin it passed with the negation deleted.

⚠ **One line.** Nothing breaks a paragraph, so a string wider than its element overflows rather than
wrapping, and the measure function ignores the width it is offered. `Vixen.Ui.Text` already has the
UAX#14 line breaker this needs.

A glyph's position is relative to the start of its run and the command carries where that is, so two
labels saying the same thing in different places hold identical glyph runs — which is what will let
the batcher notice.

## Paths and custom drawing

A stylesheet describes boxes, and most of an interface is boxes. A chart, a sparkline, a knob and a
hand-drawn icon are not, and there is no property for those. `UiElement.OnDraw` is where a control
draws itself and `DrawContext` is what it draws with — called after the element's background, border
and text and before its children, which is where CSS puts an element's own content.

⚠ **Curves are kept as curves.** How finely to flatten a Bézier depends on how large it will be on
screen, which is a device scale the draw list does not know. Flattened here, a path built once and
drawn at two zoom levels is faceted at one of them, and nothing downstream can recover the curve to
do better.

**One fixed-size struct per verb**, points a verb does not use left at zero. Skia's design is a verb
array beside a point array, which is smaller — a line costs one point rather than three — and needs
two ranges on the command and two cursors to walk it. One array keeps the frame diff a comparison and
the command's reference one range, which is worth more here than the bytes.

**Fill and stroke are two commands over one path range**, not one command with a flag: they are
different draws, and a shape that is both still describes its outline once.

⚠ **`Close` carries the point it closes to.** A stroked path's closing join is drawn differently from
a line back to the same place, so the verb has to survive — and a second contour has to close to its
own `MoveTo`, which is what makes a path with a hole in it possible at all.

`PathFillRule.EvenOdd` is there because it is how most icon sets punch the hole in a letter `o`, and
a renderer that only knew non-zero would fill it in.

## Nine-slice

`DrawContext.DrawNineSlice` is what turns one small texture into a panel, a button and a tooltip at
three different sizes with the same corners. The cut itself is `NineSlice` in
`Vixen.Core.Mathematics` — shared with `Vixen.Rendering`'s sprites, because a stretched panel and a
stretched sprite are the same nine pairs of rectangles and the two assemblies cannot see each other.

**Not a command kind, and that is the point.** A nine-sliced image carries the same
`DrawCommandKind.Image` as a stretched one, so it goes through the same pipeline and batches with the
images around it: a panel and the icon on top of it are one draw as long as they come from the same
sheet. Nine quads instead of one, in a run that was going to exist anyway. A kind of its own would
have split every batch it appeared in, to describe geometry the renderer never sees.

⚠ **Two insets, and the second one is in UVs.** A border is a distance on the screen and a distance
into the texture at once, and those are the same number only when the image is drawn at its own pixel
size. Converting between them needs the texture's dimensions, which is the one thing this assembly
refuses to know — the same bargain `DrawCommand.Source` makes — so the caller who registered the
texture divides.

⚠ **Stretched, never tiled**, for the same reason: a repeat count is destination ÷ natural size, and
the natural size is in texels. Tiling lives where the pixel size does, in `SpriteGeometry`.

The destination inset is fitted to the box and the source inset is not, so a panel drawn narrower
than its own two corners shows them compressed rather than quietly reading different texels — which
would look like the artwork changed rather than like the box got small.

## Batching

**Runs of consecutive commands, and never a reordering.** Worth being blunt about, because reordering
is what batching means everywhere else: a 3D renderer sorts draws by material because a depth buffer
decides what ends up in front. A user interface has no depth buffer. Order *is* the answer to what is
in front, so moving two runs of the same font together across the panel between them draws the text
over the panel that was supposed to cover it.

So the win is bounded and honest. A hundred alternating labels and boxes batch into two hundred
batches, and that is the correct answer rather than a failure to optimise — what improves it is
emitting fewer interleavings, which is a question for whoever writes the controls.

**The batches partition the commands**: every command is in exactly one, in order, so a consumer
walks the batches alone and never has to fall back to the command list to find what it missed. That
is why a clip gets a batch of its own rather than being skipped.

**Behind the frame diff, not beside it.** Batching walks every command in the interface, and a frame
that drew the same thing has the same batches by construction — so the cached command buffer the
version exists to protect keeps its batches with it. `Batched` counts the rebuilds, because a claim
about work avoided that cannot be measured is one nobody can check.

⚠ **`BatchKind` is a coarse stand-in for two of the four things that decide a pipeline.**
`Vixen.Rendering` already answers this: a pipeline is keyed on the effect, the stage, the vertex
layout and the render output. Only two of those are a draw list's to know — which shader and which
vertex format — because the stage carries blend, depth and raster state and the output carries
attachment formats, and both belong to the compositor. Rectangles and borders are grouped as
signed-distance quads, filled and stroked paths separated as different tessellation work; both are
claims about a shader nobody has written. **What this must not do is grow to describe the other two.**

⚠ **The renderer does not use a batch list, and the difference is the point.** `MeshRenderFeature`
walks its nodes in sorted order and re-binds only when the pipeline handle changes — the same runs,
with two locals and no array. That is right for a mesh, whose nodes are rebuilt from culling every
frame so nothing precomputed would survive. A UI is the opposite case: most frames draw exactly what
the last one drew, so the runs are worked out behind the frame diff and a still interface pays
nothing. If the UI render feature binds on change anyway, this is what stops it regrouping every
frame; if it does not, this is the thing to delete.

⚠ **`RenderSortMode.ByGroup` already says "for UI and anything else already ordered"** — depth left
out, sorted on a group value alone, stably. So the UI render feature has to make that group *be* the
painting order: a group meaning a material or a texture would reorder the interface on the way to the
screen and undo everything above. The batch index is that number.

What is *not* a guess is that batches are contiguous, ordered and maximal, which holds whatever the
grouping turns out to be.

## The draw list

The last step of the chain: the cascade said what applies, the bridge turned it into lengths, flexbox
turned those into rectangles, and this turns the rectangles into commands. Nothing here decides
anything — it reads.

**Three ways to be unpainted, and they are not the same.** `display: none` arrives as a zero
rectangle from flexbox and takes the subtree with it. `visibility: hidden` skips the element's own
background, border and text but still descends — it is inherited, so a child is hidden by having
inherited the value, and a child that declares `visibility: visible` reappears inside a hidden
parent. `opacity: 0` skips the subtree outright, because opacity multiplies and nothing below can
bring it back.

⚠ **An element's own text is inside its own clip, and for a long time it was not.** `overflow` clips
an element's *content*; the background and the border are the two things it does not clip, which is
why the `ClipPush` sits between them and the text rather than above all three. Emitting the text
first meant `overflow: hidden` clipped an element's children and never its own string — so a label
too long for a fixed-width column drew straight across whatever was beside it, and five panels in
the editor had written `overflow: hidden` on a text-bearing element believing otherwise.

**It survived every kind of test this framework had**, and the shape of that is worth copying: *a
clip is invisible to the element tree*. Every rectangle was the right size, every string was the
right string, and the glyphs went somewhere nothing was looking. It was found by taking a picture of
a key/value row, and the regression test is an assertion about the *order* of the commands, which is
the whole of what a clip is.

⚠ **Opacity is carried down as a multiplier rather than composited as a group, and the difference is
visible.** CSS renders a translucent element's subtree into its own surface and blends that once, so
two overlapping children of a half-opaque panel do *not* show through each other. Multiplying each
element's alpha instead makes them show through. The two agree exactly whenever the subtree does not
overlap itself — most interfaces, and all of the ones a fade-in is applied to. The correct version
needs an offscreen target per translucent subtree, which is a compositor decision rather than a draw
list's, so it is **owed**. Said plainly because a half-right opacity reads as a bug in the renderer
rather than a gap in the model.

**Painting order is document order**, and hit testing walks it in reverse. The two have to agree: the
element drawn last is on top, so it is the one a click lands on, and any rule that made them disagree
would be a UI where things are not where they look. One test asserts both at once.

**The frame diff is against the previous content, not against a dirty flag.** A flag says what the
framework believes changed; the content says what actually did, and the two part company exactly when
something is invalidated too eagerly — which is the failure a cache is supposed to absorb rather than
propagate. `Version` changes when the drawing changes and not when it is merely rebuilt, so a
renderer compares one integer.

⚠ **`border-color` and `border-radius` are expanded before they are interned**, the same way `margin`
is — by ExCSS while parsing when the value is literal, and by `ShorthandExpansion` at load when it
holds a `var()`, which ExCSS is obliged to hand back whole. Written against the shorthands, every
border and every rounded corner in the document silently disappears.

⚠ **All eight longhands are read, and reading only the first of each set was worse than a subset.**
The builder used to intern `border-top-color` and `border-top-left-radius` alone. That made
`border-b-<colour>` inert, as you would expect — but it also made `border-top-width` paint a ring on
all four edges, made the other three widths paint nothing at all, and made `border-top-left-radius`
round the whole box while the other three corners were ignored. Twenty-one rules in the editor's own
themes were written against the three that drew nothing, including the selected-tab underline.

**A box whose four corners agree stays cheap.** `DrawCommand.Radius` is one `float` and it is still
what nearly every box uses; only a box whose corners differ, or whose corners are elliptical, gets an
entry in `DrawList.Boxes`. `CornerRadii.IsUniformCircular` is the test, and it insists on circular
rather than merely equal because four equal ellipses are still not one number.

**A border whose four edges agree stays one command.** Equal widths and equal colours are a single
`Border` — one quad, one distance field, one antialiased outer edge shared with the fill beneath it.
Edges that differ are drawn as up to four `Rectangle` bands instead, because the box shader resolves
a ring from one thickness and one colour and has no per-pixel notion of which edge a fragment belongs
to. ⚠ The bands are mitre-less: the horizontal edges take the corner squares and the vertical ones
are inset between them, which is the join CSS draws whenever the two edges meeting at a corner are
the same colour. The difference shows only where two adjacent edges are both thick *and* differently
coloured. Giving the shader a real mitre means four more colours and four more thicknesses in
`UiShape` — eighty more bytes on a record every box in the frame writes, to describe something almost
none of them have.

**`opacity` is multiplied down the walk, not read from the cascade.** It does not inherit — it makes
a group, and every descendant is in it whatever its own value says, so an element's alpha is the
product of its ancestors' and its own. A fully transparent subtree is skipped entirely, which is the
one case where the cheapest thing to do is also exactly right.

⚠ **And it is applied per command rather than per group.** CSS composites an element and its
descendants into a layer and fades that *once*, so two overlapping children of a half-transparent
parent show the background through both together; here each command carries the multiplied alpha and
the overlap is drawn twice, coming out darker than a browser would draw it. Doing it properly needs
an offscreen target per element that has an opacity, which is a renderer feature rather than a
builder one. Said plainly because the difference is invisible until something overlaps, and then it
looks like a blending bug rather than a known limit.

Fading is `alpha` on the colour and not `colour * alpha` — the operator scales all four components,
which is right in premultiplied space and would darken towards black here.

**A `box-shadow` is the same quad and the same distance field as a box**, with the one-pixel edge
widened to a blur radius. The offset and the spread are folded into the command's rectangle and the
spread into its radius, so what reaches the geometry is an ordinary rounded box that happens to be
soft — which is why a shadow needs no fields on `DrawCommand` that a box does not have, and why it
batches with its own background instead of splitting the frame.

⚠ **The quad is grown by twice the blur.** Coverage reaches zero a blur out from the boundary, but
the falloff is centred on it, so the visible tail runs a blur *beyond* where the edge sits. One blur
of margin leaves a faint straight line where the quad ends, which reads as a crease in the shadow.

⚠ **The command's thickness is half the CSS blur radius.** CSS's blur is the total distance the edge
fades over and the shader's is the half-extent either side of it; passing the whole radius through
makes every shadow twice as soft as it was asked to be, which reads as a blurry renderer rather than
as a unit mistake.

⚠ **One shadow, outer only, and not clipped to outside the border box.** CSS takes a comma-separated
list and an `inset` keyword; a list would be a command each, which is easy, and `inset` is a
different distance field, which is not — so both are refused rather than half-applied, because the
first shadow of a list being drawn and the rest silently dropped looks like it worked. And CSS
punches the box out of its own shadow, where here the blurred box is drawn whole with the background
on top: visible only under a background that is not opaque.

## Input

**Front to back means the last child first.** A later sibling is painted over an earlier one, so it
is the one a click lands on; testing in document order returns whatever happens to be underneath.

**`UiElement.PaintOrder` is the one place that order is decided**, read forwards by the draw list and
backwards by hit testing. The two have to agree — an element drawn on top must be the one a click
lands on — and the cheapest way to guarantee that is for neither of them to have its own opinion.
Document order costs nothing: with no `z-index` among the children it *is* the children list, not a
copy, and the sorted list is built only when some child has an index and cached until something
changes. The sort is stable, so lifting one child leaves every other exactly where it was.

⚠ **`z-index` orders siblings, and only siblings.** CSS lets a positioned descendant paint above an
element that is not its parent's sibling — what a dropdown escaping its row relies on — and that
needs stacking contexts, which needs the whole of CSS 2.1 Appendix E. Here a high index lifts a child
above its brothers and no further, so an overlay that must cover the window belongs to a container
near the root rather than to the row that opened it. The two models agree until the moment they
matter, which is why this is written down rather than left to be discovered.

⚠ **And it applies to every element, not only positioned ones.** The CSS restriction exists because a
static element establishes no stacking context for the index to be measured in; sibling ordering
needs no such thing, and demanding `position: relative` before `z-10` did anything would be a rule
with no reason behind it here.

⚠ **Being outside an element is not a reason to skip its children.** `overflow: visible` is CSS's
default and means precisely that a child may hang outside its parent and still be drawn — so it must
still be clickable. Returning early on a missed parent makes every dropdown, tooltip and popover
unhittable, and the bug looks like the click landing on whatever is behind them. The clip is asked
about on the *parent*, because it is the parent that clips and the child has no idea it is being cut.

⚠ **And it is outside *on a clipped axis*, not outside at all.** `overflow-x` and `overflow-y` are
real, so an element can cut one pair of edges and not the other — a point beside an `overflow-y:
hidden` panel is inside the part of the plane that panel draws in and has to stay clickable. Painting
and hit testing resolve the three properties through one object, `OverflowReader`, for the reason two
copies of one rule always eventually give: a control that is visibly clipped and invisibly clickable,
or the reverse. Written on the clip stack, an unclipped axis is a pair of edges at
`DrawListBuilder.UnboundedClip` — a finite stand-in for infinity, because the stack intersects
rectangles and `float.MaxValue + float.MaxValue` is an infinity that becomes a NaN and unclips
everything below it. The stand-in is exact rather than approximate: the stack starts at the viewport
and only ever narrows, so an edge past the viewport *is* the viewport.

⚠ **Neither axis coerces the other, where CSS's would.** A browser computes a `visible` to `auto`
when its partner is not visible, because a scrollport is one rectangle and painting outside it on one
axis alone is undefined there. There is no scrollport here — the clip is a rectangle and one axis
alone is expressible — so `overflow-x: auto` means what its author wrote instead of also hiding
everything below the box. The other departure is order: nothing expands `overflow` into its two
longhands on the way in, so the computed style holds whichever of the three a rule set and no record
of which came last, and a named axis therefore wins unconditionally.

⚠ **`overflow: auto` is `Overflow.Scroll` to the layout, and it used to be nothing at all.** The draw
list clips on any value that is not `visible`, so `auto` always clipped; the layout's keyword table
had `visible`, `hidden` and `scroll` and not `auto`, so flexbox went on treating the box as visible —
half a property, silently. `auto` and `scroll` are one layout mode in CSS too, differing only in
whether a scrollbar gutter is reserved, and nothing here draws a scrollbar of its own. What the
layout does with it is the CSS Flexbox §4.5 opt-out and the fit-content size of a scroll container,
each on **one axis**: §4.5 is about the main axis, so `overflow-y` on an item in a row says nothing
about whether it may be squeezed sideways.

⚠ **A clip is still not a scrollbar.** `overflow-y: auto` on a plain element cuts its content off and
offers nothing to scroll it — `ScrollView` is the control that owns bars and offsets content, and it
deliberately reads no `overflow` of its own.

⚠ **`pointer-events: none` is transparent without making its children so.** That asymmetry is what
makes an overlay usable; treating the subtree as one unit either blocks everything under a
full-screen layer or lets clicks through a modal.

**A captured pointer goes to the capturing element wherever it is** — a drag that leaves the
scrollbar it started on must keep reaching the scrollbar. Hit-testing during a drag is the bug
capture exists to prevent.

Handlers are invoked by index with the count re-read each step, because unsubscribing from inside a
handler is the ordinary case and a `foreach` throws part-way through delivering the event that
caused it.

Doc 09 asks for a quadtree over the top level. This descends the tree instead, entering only subtrees
that contain the point. The doc says the simple version was "measured to be sufficient"; **that
measurement has not been taken here**, and it should be before the quadtree is written.

## The property system

A plain C# property is invisible to everything that has to find it at runtime — a stylesheet naming
it, an animation targeting it, a binding writing it, an inspector listing it. `[UiProperty]` gives it
an identity without giving up the typed accessor: `element.Radius` is a field read, and
`key.SetValue(element, 4f)` reaches the same field.

Generated rather than reflected, and generated rather than rewritten — Stride builds the equivalent
with a runtime `DependencyPropertyFactory`, and ADR-002 rejects that whole category.

⚠ **Storage is a field, not a sparse table**, which is the opposite of what WPF does. A
dependency-property table pays a dictionary probe per read to save memory on the hundreds of
properties a WPF element declares and never sets; a Vixen control declares perhaps a dozen, there are
10⁴ elements, and reads happen every frame. The table is the more famous design and the slower one.

⚠ **Inheritance is a typed walk, not a name lookup.** Each inheriting property emits its own loop
testing `ancestor is TOwner`, so `Panel.Tint` finds the nearest `Panel` — and an `Overlay` that also
declares a `Tint` is not it. Keyed on the name, it would have found the wrong one and been
confidently right-looking.

⚠ **The old value is read through the property, not out of the field.** On an element that has only
ever inherited, the field is still empty, so comparing against it reports a change from zero on the
first write that agrees with the parent — a spurious invalidation on every element that matches its
ancestor.

**Construction and registration are two steps.** An element must be registered with both trees, which
needs a document; a base constructor taking one plus two internal node handles would put those
handles in every subclass's signature, in assemblies where they are not visible. So `UiElement()` is
parameterless and `UiDocument.Create<T>` binds it afterwards — which is also the shape markup needs,
since a generated `new Button()` cannot know a document either.

## The pass

Four walks, and they cannot be merged. The cascade needs parents resolved before children because
inheritance reads the parent's resolved table. Font size needs the same order for the same reason and
cannot fold into the cascade, because it is a *computed* value the cascade has no opinion about. The
layout style depends on the font size. And layout is the flexbox algorithm, which is not a walk.

**An unchanged document does nothing on the next frame**, and one changed class rebuilds one element.
That is what `ComputedStyle` being interned buys: two elements that resolved alike hold the same
object, so the test is a pointer comparison rather than a walk of a property table. `StylesApplied`
reports the count, because a claim about work avoided that cannot be measured is a claim nobody can
check.

⚠ The font size has to be part of that test as well as the style. An element whose own declarations
did not change still needs rebuilding when an ancestor's font size did, because every `em` on it
measures against a different number now — its computed style is the same interned object, so a check
on the style alone skips it and `2em` keeps meaning twenty pixels while the text around it doubles.

⚠ **`StylesApplied` is not `StylesResolved`, and reading the first as the second hid a defect for two
phases.** `StylesApplied` counts elements whose *layout style was rebuilt*, which the interning makes
a pointer comparison — so it reads 1 for a one-class change however the styles were arrived at.
`StylesResolved` counts the cascade that produced them. The claim "one changed class rebuilds one
element" was true the whole time the pass underneath it was cascading all ten thousand.

## The cascade, incrementally

The document **records what changed rather than that something did**. A class change and a state
change are the two mutations `StyleUpdater` can narrow, so `AddClass`, `RemoveClass` and `State` put
an entry in a log; the next pass replays it through the updater, which restyles what a rule could
have noticed and stops descending wherever the resolved style came back as the same interned object.

Everything else — a new element, a removal, a move, a reparent, an inline style, a stylesheet — comes
through `UiDocument.Invalidate` and costs a cold pass over every live node. That is correct for all
of them: the updater narrows a change to *an existing element's* names or state and cannot express
any of the others, and an element created this frame has no resolved style for an invalidation root
to reach. Widening `StyleChangeKind` is how the remaining ones would get their own path.

⚠ **Each recorded change is replayed as its own pass, not merged into one.** The invalidator answers
"what could *this* have reached", and the sharing cache has to be cleared between them because an
entry cached while resolving the first knows nothing about the second having happened. It is correct
to replay them in a batch only because the tree is fully mutated before any of them run — every pass
resolves against the final state, so the union of what they reach is what a cold pass would have
produced. Replaying against a tree being mutated in step would not be.

⚠ **A scroll resolves nothing at all.** An offset moves where boxes are drawn and cannot change what
any selector matches, so `OffsetX`/`OffsetY` ask for a pass with `InvalidatePositions` rather than
`Invalidate`. Before that existed the only way to ask for a frame was the conservative door, and
every frame of a scroll re-resolved the document.

**Gated through `UiDocument` rather than through `StyleUpdater`.** `Vixen.Ui.Styling.Tests` has run
this same property against the updater since Phase 4b — and stayed green for two phases while
`Update` called `StyleEngine.ResolveAll` and never touched the updater at all. A test that reaches
for the updater passes with the wiring deleted, so `IncrementalDocumentTests` drives the document:
mutate a tree, compare every resolved style against a second document built directly in the final
state, and assert the pass really was incremental.

## Surfaces: several windows, one document

A `UiSurface` is a rectangle the document is laid out into, drawn onto and clicked in. A new document
has one — the primary, whose root *is* `Document.Root` — and `CreateSurface` adds another for every
extra window.

**A window is a surface rather than a document of its own, and that is the load-bearing decision.**
A panel dragged from the main window into a torn-off one has to keep its scroll offset, its
selection and whatever the user has half-typed, and the only operation that preserves those is
`Reparent`, which is *within* a document by construction. Making a window a surface turns "move a
panel to another window" into the reparent a docking host already performs.

So every surface after the first is an ordinary element under `Root`. One style tree: a torn-off
panel inherits the theme, matches the same stylesheets and resolves `rem` against the same root. One
focus, one pointer capture, one gesture recogniser — which is what lets a drag that starts in one
window finish in another. What a surface root does *not* do is take part in its parent's flex
layout: it is removed from the layout tree's child list and laid out on its own, against its own
size. Three passes stop at that boundary — the accumulator, the hit test and the draw list — and
`UiElement.SurfaceRoot` is what they ask.

`vw`, `vh` and `%` are the surface's own. `50vw` in a torn-off inspector means half of *that*
window; resolving it against the main one would size a 400-pixel palette against a 3840-pixel
display.

**DPI is per surface, because two windows are routinely on two displays.** It is not a scale
anything above the renderer applies — lengths stay in logical points everywhere — it is the grid the
finished layout is snapped to, written into `LayoutTree.PointScaleFactor` before each surface's
layout call. ⚠ Changing it also needs `LayoutTree.Invalidate`: nothing an element *declared* changed
when a window was dragged onto a 2× display, so `SetStyle` compares equal, nothing is dirty, and the
rounding pass — which is what reads the grid — never runs.

A real operating-system window is asked for through `IUiWindowHost`, which this assembly declares and
cannot fill: `Vixen.Platform` is a layer above `Core/`, and a UI framework that referenced it would
stop being usable with no backend at all. `Vixen.Platform.Ui` is what fills it. `CanOpenWindows` is
false on a browser tab, an Android activity and iOS, and a control that wanted a second window is
expected to have something to do instead.

## Removal

`UiElement.Remove()` takes an element and its subtree out of all three stores at once — which is why
it lives on the document rather than in any of them. One that left either store behind would keep
matching selectors or keep taking up space in a flex line while being gone from the document.

⚠ **A removed style slot is tombstoned and never reused**, and that is the design decision rather
than a shortcut. The obvious implementation is a free list, and it would quietly break three separate
things that all rest on one unwritten invariant — *a parent's index is lower than its children's*.
`ResolveAll` walks slots ascending because that is parents-before-children and inheritance needs it;
the incremental pass uses the index as a queue priority for the same reason; and the bloom sweep
gives up the moment a climb passes below the ancestor's index. Fill a hole with a new child of a
later parent, and the first two resolve a child before its parent while the third answers "not a
descendant" about something that is — a descendant selector that silently stops matching.

So slots leak, `StyleTree.DeadCount` says by how much, and **compaction is the fix rather than
reuse**: rebuilding the arrays without the dead slots preserves relative order, which is exactly what
reuse does not. Owed.

⚠ **The layout tree reuses its slots and the style tree cannot**, and the asymmetry is not an
oversight. The layout algorithm descends from the root, so it never cared what order the slots were
in; the cascade walks the array by index and reads each parent's resolved table, so for it the slot
number *is* the ordering.

⚠ **`IndexInParent` has to come down with the removal.** It is what `:nth-child` and the sibling
combinators read, so a stale one leaves the third item of a list still believing it is the fourth
after the second is deleted.

**Whatever was pointing at it has to stop.** The focus, a captured pointer and a gesture in progress
each name an element and each outlives it unless something says otherwise — and each has to be
checked against the whole subtree, not the element itself, because a dialog closing takes the focused
field inside it. A drag whose target is removed ends *silently* rather than as a cancellation: a
cancelled drag tells its target to put back what it was carrying, and the target is the thing being
deleted.

**A removed element throws rather than answering.** Its node ids address slots the layout tree has
already handed to someone else, so answering means reading another element's width and restyling a
stranger — a wrong answer rather than an absent one.

⚠ **The frame pass walks the tree rather than a list in creation order**, which removal forced and
which should have been there anyway. The list version was correct only because elements were created
parents-first and never removed, so its index order happened to be its depth order. The property the
pass needs is "parents before children", and a descent is that by construction rather than by
coincidence — and it deletes two parallel arrays, since what each element had applied last time now
lives on the element.

## What the bridge is for

**`em` on `font-size` means the parent's; everywhere else it means the element's own.** So font size is
resolved first and separately, and the caller walks the tree passing each element's resolved size to
its children. Conflating the two compounds: three nested `font-size: 1.2em` come out at 1.2× rather
than 1.728×, and the error grows with depth, so it reads as a rendering quirk rather than an
arithmetic one.

**Percentages are not resolved.** A percentage measures against the containing block, which only the
layout pass knows, so `50%` is handed on as `LayoutUnit.Percent` untouched. This is the one place
where doing less is the correct behaviour rather than an omission.

**An unparseable declaration leaves the initial value alone.** Zero is a perfectly good answer that
happens to be invisible, so using it for "I did not understand this" turns one typo into a missing
element with nothing said about it.

## What it found

⚠ **Yoga's initial values are not CSS's, and they differ in four places.** `flex-direction` is
`column` against `row`, `align-content` `flex-start` against `stretch`, `position` `relative` against
`static`, `box-sizing` `border-box` against `content-box`. `Vixen.Ui.Layout` is right to start where
Yoga starts — it is judged by Yoga's conformance suite — and this is the boundary where a VCSS
author's expectations take over, so `LayoutStyleBuilder.CssInitial` exists and `LayoutStyle.Default`
is not what an element with no declarations gets. Starting from the wrong one produces stylesheets
full of redundant declarations by an author who decided the engine was quirky and never reported it.

⚠ **ExCSS expands the box shorthands, and the gap that was predicted does not exist.** The bridge was
first written to expand `margin`, `padding`, `border-width`, `gap` and `flex` itself, on the
reasoning that the cascade stores shorthand and longhand as separate properties and the layout store
resolves edges by fixed precedence rather than document order — so `margin-left: 0; margin: 8px`
would give zero where a browser gives eight. **Its tests said every one of those paths was dead.**
ExCSS expands on parse, exactly as a browser does, so by the time the cascade runs that is two
`margin-left` declarations and the later one wins. The prediction was reasonable and wrong, and the
only reason it did not become a documented "known limitation" is that the test was written before the
claim was believed. `inset` is the exception, because ExCSS does not know the property.

⚠ **CSS has a unit that begins with the exponent character.** The value parser scanned `e` as part of
a number unconditionally, so `2em` scanned as `2e`, failed to parse, and came back `Unknown` — every
`em` in the document silently dropped. `1e2px` still has to work, so the fix is to test whether digits
follow rather than to drop the exponent.

⚠ **`aspect-ratio: 16 / 9` arrives as `16/9`.** ExCSS normalises the spaces away, so a parser that
splits on whitespace sees one token. Read here rather than by teaching `StyleValueParser` that `/`
separates values — it does in CSS, but making it a general separator changes how every shorthand
parses.

⚠ **This cascade inherits specified values; CSS inherits computed ones.** A child inheriting the text
`font-size: 1.5em` resolves that `em` against its own parent a second time, so a size meant to apply
once compounds at every level — two deep comes out at 2.25× where CSS says 1.5×, and the error grows
with depth. CSS avoids it by computing `font-size` to an absolute length before anyone inherits it,
so `font-size` was removed from `InheritedProperties` and is inherited here in computed form instead.
An element that declares none simply keeps its parent's resolved pixel size, which is both what CSS
means and simpler than what was there.

**`line-height` and `letter-spacing` have since joined it**, computed and inherited by the same
mechanism — `UiElement.LineHeight` and `UiElement.LetterSpacing` are the resolved pixels, and an
element that declares neither passes its parent's straight through. Both are read by the text layout,
so the bounded one-level error they used to carry was one the renderer could see.

⚠ **`line-height` is the one where computing is not simply resolving.** A *unitless* `1.5` inherits as
the number and is multiplied by each descendant's own font size; `1.5em` and `150%` inherit as the
length the ancestor resolved once. That distinction is the entire reason the unitless form exists, so
the computed value carries which of the two it is rather than collapsing both to pixels. A 10px panel
with a 30px label inside gives the label 45 from the first and 15 from the second.

⚠ **And percentages are resolved here rather than by `LengthContext`**, which deliberately refuses
them: there a percentage means the containing block, which only layout knows. On `line-height` it
means the font size, which the pass has in its hand.

⚠ **Changing them has to dirty the layout node by hand.** They are inherited outside the cascade, so
a label whose *parent* changed `line-height` has an unchanged — and still reference-equal — computed
style. The pass's usual test passes, `SetStyle` is never reached, and the label would keep measuring
itself at the old height for the rest of its life. Only nodes that measure themselves are marked,
which is what `MarkDirty` insists on and what having text means.

The gap stays open for `word-spacing` and `text-indent`, which nothing reads yet. Computing a value
no consumer looks at would be work with no way to be wrong.

**And relative units belong in `StyleValue` after all.** They were deliberately left out, on the
argument that resolving them needs a context that does not exist at parse time. That was right about
resolution and wrong about representation, and **transitions settled it**: the animator interpolates
`StyleValue`, so a unit the type cannot express is a unit that cannot animate. `width: 2em` under a
`transition` snapped while its neighbours eased, with nothing said about it.

## How it is tested

Through the whole path — write CSS, read a `LayoutStyle` — rather than against a hand-built
`ComputedStyle`. A wire is worth testing with something plugged into both ends: a property name no
rule can ever set shows up as a test that will not pass.

Verified by sabotage. Starting from Yoga's defaults, resolving `font-size`'s `em` against the
element's own size, resolving percentages here, swapping `vw` and `vh`, and dropping the
leave-the-initial-value guard each fail it.

⚠ That last one took two attempts, and the failure is the interesting part. Written against a
stylesheet, `width: 4furlongs` never reaches the bridge at all — **ExCSS validates as it parses and
drops what it does not recognise** — so the test passed whatever the bridge did with a bad value,
including overwriting a good one. Rewritten against inline declarations, which are interned directly
and get no such vetting, it still passed: the value has to be one that *parses* but is not a length,
because an unparseable one is already filtered a step earlier. A bare `5` is the case that reaches
the code being tested.

## The geometry a renderer submits

`UiGeometryBuilder` is the last step that is still the interface's own: a draw list in, vertices
out. Everything below it is a pipeline, a buffer and a scissor. Being a pure function of a draw list
is what lets all of it be checked without a device.

**Boxes are one quad each, not a tessellated outline.** A rounded rectangle and its border are both
a signed distance the shader evaluates per pixel, so a corner is exact at any radius and costs four
vertices — where tessellating one costs vertices in proportion to the radius and is still faceted.
That is also why the two share a batch kind: one shader draws both, and the thickness decides
whether the inside is filled. The texture coordinate is the offset from the box's own centre, which
is the space a signed distance to a rounded box is written in, so the shader needs no uniform per
box.

⚠ **Clips are resolved here rather than replayed.** A draw list pushes and pops; a renderer sets a
scissor. Carrying the resolved rectangle on each draw means the renderer holds no stack and cannot
be caught out by a batch it skipped having left one behind. A nested push **intersects** rather than
replaces — setting the scissor outright would let a child draw outside the panel containing it.

⚠ **A glyph's position is an offset along its run, not a place on the surface.** The command carries
where the line starts, which is what lets two identical labels in different places hold identical
glyph runs — and therefore what lets the batcher and the frame diff notice they are the same.
Reading the offset as absolute puts every label wherever the first one was; found while writing the
tests, because the first fixture had its run at the origin, where the two are the same thing.

⚠ **The placement is in ems and the pen is in pixels**, so the font size multiplies one and not the
other — and the threshold range with it, or text blurs as it grows and aliases as it shrinks. A
font's y runs up from the baseline and a surface's runs down, so a glyph's top edge is a subtraction.

⚠ **Every glyph the frame needs is packed before a single quad reads a region.** A quad reads its
region the moment it is written, and two things move a region afterwards: compaction moves all of
them at once, and eviction hands one glyph's slot to the next. Interleaved packing and reading
therefore lets the fortieth glyph of a label silently relocate the first thirty-nine, and what draws
is the right letters out of the wrong places — a glyph that was evicted mid-run comes back as
whichever letter took its slot. So `Build` resolves the whole frame's glyphs first and emits second;
the only packing that can happen during emission is none.

⚠ **What that cannot cover is reported rather than retried.** A frame wanting more distinct glyphs
at once than the atlas holds evicts, while resolving, what it is about to draw — so emission puts
them back and can take another one's slot doing it. `AtlasChanged` says so, and it watches the
atlas's *revision* rather than its version, because a version misses the eviction case entirely. A
retry has nothing to converge on: the second pass evicts the way the first did. The answer is a
bigger atlas or a lower field resolution, which belongs to whoever built the cache.

Verified by sabotage: reading glyph offsets as absolute fails 1, a quad that ignores the font size
fails 1, an unflipped baseline fails 1, a threshold range that does not scale fails 1, a nested clip
that replaces fails 1, a clip that is never popped fails 1, a box not parameterised from its centre
fails 1, emitting empty draws fails 3, a dropped glyph that is silent fails 1, removing the resolve
pass fails 2, and an `AtlasChanged` that never fires fails 3.

~~**Owed:** paths.~~ `PathFlattener` and `PathTessellator` are here — curves to contours at a
tolerance the caller chooses, contours to triangles by a trapezoid sweep, filled or stroked, with an
antialiasing fringe. See [Paths and custom drawing](#paths-and-custom-drawing) above.

~~Also owed: a wider index.~~ `UiGeometry.Indices` is `uint`. It was `ushort`, and the builder
refused to emit past 65 535 vertices rather than wrap — which was honest while the index was narrow
and was not a fix, because a dense editor frame really can pass sixteen thousand quads and the
symptom of dropping the rest is a frame missing its bottom half. Thirty-two bits not because a frame
is expected to need them, but because the one that does wraps *silently*, drawing geometry from the
top of the frame in the middle of it.

Licensed under Apache-2.0.

## Composition

`Vixen.Ui.Composition` is the runtime a `.vxml` compiles into, and it is the same API somebody
writing a component by hand would use — the generated half is ordinary, steppable C# rather than
magic (ADR-002).

**`Build` runs once.** That is the whole of ADR-010: no render function to call again, no virtual
DOM, and no reason to walk a tree that did not change. What changes later changes because an effect
ran, and an effect assigns exactly the property it was written for.

**`@if` and `@switch` are one primitive.** `ctx.Switch` takes a selector saying which arm is live and
a builder that constructs it; a condition chain and a pattern match differ only in how the number is
produced. Two constructs for swapping a subtree in and out would be two places to get the disposal
of a branch's effects wrong.

**Keys buy identity, and identity is what a list is for.** An item whose key survives keeps its
element — and so its focus, its scroll offset and its animation state. Without a key the fallback is
the item itself, never the index: an index makes every element after an insertion compare unequal,
which is exactly the failure `VXML2004` warns about.

### A capitalised tag is a component *or* a control

`<Counter />` names a `Component` and `<ProgressBar />` names a `UiElement`, and they are written
identically because the markup compiler resolves no types and is not going to start. `ctx.Child<T>`
takes both, and which one it got is settled by C# overload resolution at the use site rather than by
a registry: `IComposable` is the constraint, and it exists so that a tag naming a type that is
neither is an error on the tag the author wrote.

The two differ in exactly two places, and `BuildContext.Host` and `BuildContext.Inner` are those
places. An attribute on a component's tag applies to the element the component *drew*; on a
control's tag it applies to the control, which is already an element. Children written inside a
component's tag are content projected into its slot; inside a control's tag they are its children.
Both are static overloads that inline away, which is what lets the emitter write one call for a
distinction it cannot see.

⚠ **`on:click` therefore has to mean two things, and `Vixen.Ui`'s table only knows one of them.**
An element's click is a tap; a *control's* click is its activation — Space, Enter, an access key and
a tap, which is what `ClickEvent` exists to be. So `BuildContext.Subscribe` lets a control library
say so, and `Vixen.Ui.Controls` does it from a module initializer. Without that a `<Button on:click>`
works for everybody who tests with a mouse and for nobody who does not use one.

**A component names its host tag.** `Component.TagName` defaults to the type's name in lower case
and is overridable for the reason `UiElement.TagName` is: a default taken from a type name cannot
produce a hyphen, and `task-center` is not spelled `taskcenter` in anybody's stylesheet. In markup
that is the `@tag` header.

**`Literals` is how a quoted attribute becomes something that is not a string.** `Variant="Subtle"`
is an enum member and `Value="0.5"` is a float; the markup compiler knows neither, so it writes
`Literals.Of(n1.Variant, "Subtle")` and lets overload resolution pick the conversion from the type
of the property being assigned. ⚠ **The first argument exists to be inferred from and is never
read** — C# infers nothing from an assignment target — so a property whose getter does work does it
once at build time, and a property that cannot be read is one to write with `@expr`. A type nothing
converts to is a *compile* error on the attribute, which is what an `object Convert(Type, string)`
would have turned into a run-time surprise.

### A mounted component is findable, and therefore alive

`UiDocument.ComponentAt` answers "which component drew this element", for the elements that are a
component's host. A control *is* the element a caller reaches for; a component is an object beside
the elements it drew, and without this there is no way back from the tree to it — which a test
harness needs, a debugger wants, and a future inspector will.

⚠ **The table is weak on the element, and that is what makes it free.** A component is reachable
for exactly as long as its host is, so a branch that leaves the tree takes its component with it and
nothing has to be told. It also retires the owed item below it: a mounted component's *effects* are
not in the document, so before this the only thing keeping a panel's bindings alive was whatever
reference the caller happened to hold.

### Content goes where the element says

`UiElement.ContentHost` is the control-side mirror of `Component.Content`: itself for most elements,
and the viewport for a `ScrollView`, the panel for a `Popover`. Markup written inside a
`<ScrollView>` means the rows are what scrolls; hung off the control they would sit beside the
scrollbars, laid out by neither.

### Effects belong to the document

`UiDocument.Effects` is where every binding queues, and a frame drains it in one place. The
scheduler's own default is per *thread*, which is the wrong granularity for a document: an editor
has several — a shell, a preview pane, a floating window — and a test process has one per test.

⚠ **This was found rather than reasoned about.** Flushing the thread's queue from one shell's tick
ran the bindings of every other document on that thread, disposed ones included, and turned a
ten-second test run into one that did not finish.

### Regions, and the question "where"

An `@if` in the middle of a `<div>` has siblings on both sides, and the element tree only appends.
So a region knows what it comes *after* and asks: an element answers "one past me", another region
answers "wherever I end", and an empty region defers to its host. Nothing is stored that can go
stale.

⚠ **That last case is not decoration.** A branch that *opens* a loop item follows no element, and
where its item starts is not known until the list is in its final order. An earlier version
snapshotted the position at build time and put every leading branch at index zero of the parent — in
somebody else's item. It is asked for now, and there is a test whose whole job is that shape.

The alternative was an anchor element, which is what the DOM frameworks use. Here it would have to
be a real element in all three stores, and a real element is counted by `:nth-child`. Rows that
stripe wrongly because of a hidden marker is a worse bug than this is complexity.

### Scoped styles

`scoped` on a component's `<style>` puts a class on every element the component built and welds the
same class onto the end of every selector: `.row { … }` becomes `.row.v-1f2e { … }`. So a
component's `.row` cannot reach a caller's `.row` that happens to be inside it, which is the whole
content of the keyword.

⚠ **Welded to the end, not prefixed to the front.** A descendant prefix — `.v-1f2e .row` — reads as
the obvious implementation and is wrong twice: it misses the component's own root, which is the
element a stylesheet most often wants, and it matches a caller's `.row` projected into a slot, which
is exactly what scoping is for.

⚠ **The scope is per type, and so is the stylesheet.** Every instance shares one class because they
share one sheet; a per-instance scope would mean a rule set per row of a list, which is the cost that
made loading it once per instance worth fixing in the first place. `ScopedStyles.ScopeOf` derives the
class from the **full** type name, because two components called `Row` in different namespaces are
two components.

⚠ **Nothing inside `@keyframes` is touched.** Its blocks are keyed by `from`, `to` and percentages,
which are not selectors — appending a class to `50%` produces a rule that parses and never matches,
and the animation quietly loses its middle.

### Owed

Named slot projection (`slot="footer"` on a child), and a longest-increasing-subsequence pass so a
reorder moves a minimal set rather than every surviving item. The last one is correctness-neutral: a
move that changes nothing returns immediately, so an unchanged list already costs a walk and nothing
else.

*(Both of the items that used to be here — a component rooted by its caller, and no teardown hook —
are done; see above and below.)*

### A component leaves when its branch does

⚠ **It did not, and the shape of the bug is worth keeping.** A component's own build goes into a
region hanging off its *host*, which is not a slot of the region being built — so clearing the
enclosing branch removed the host element and never reached what the component put inside it. Its
effects went on running against elements that were no longer in the document, which is precisely
what regions exist to prevent, and `A_branch_that_leaves_takes_its_effects_with_it` had tested it
since regions existed — for plain elements only, which is the one case markup never produces on its
own.

The teardown is a *subscription* on the enclosing region rather than a slot in it, because slot
order is how a region computes indices within one parent element and this region's parent is the
host. `Region.Clear` disposes subscriptions before it removes elements, so the ordering falls out.

`Component.OnUnmounted` hangs off the same call, and runs **before** the teardown: a panel saving a
scroll offset or a selection needs its elements to still be there. It is where a component gives
back what the runtime did not give it — a handler on a model, which nothing else knows exists. An
unmount is not a dispose: the object survives, because a hot reload re-mounts the same instance.
