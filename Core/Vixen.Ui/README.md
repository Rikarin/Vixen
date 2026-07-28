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
| `FontRegistry`, `TextRun` | `font-family` → a face, shaping through a cache, measurement into layout, glyphs into the draw list. |
| `PathBuilder`, `OnDraw` | Lines, curves, fills and strokes for the controls a stylesheet cannot describe. |
| `DrawBatcher` | Contiguous, order-preserving, maximal runs a renderer can submit as one. |
| `UiDocument.Move` | Reordering a sibling in all three stores, so `:nth-child` moves with it. |
| `Component`, `BuildContext` | What a compiled `.vxml` calls: elements, effects, branches, keyed lists, events, slots. |
| `KeyEvent`, `TextInputEvent` | Keys routed from the focus outwards; typed text as its own event. Tab is the document's default, after the route. |
| `UiDocument.Track` | `:hover` and `:active` on the ancestor chain, `Entered`/`Exited` per element crossed, `:focus-visible` from how the focus arrived. |
| `WheelEvent` | Hit-tested and bubbling, so nested scrolling chains on `Handled` rather than on a rule. |
| `UiElement.OnCreated`, `TagName` | The constructor a control cannot have, and the element name a type answers to. |
| `UiElement.OffsetX/Y` | A translation applied after layout — scrolling, popups and drag previews, at the cost of a walk. |
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

⚠ **The registry is not font *fallback*.** The list in a declaration is tried until a *registered*
family is found; it is not tried per character until one with a glyph is found. A registered font
missing the code point draws `.notdef`. Weight and style matching is not there either — a name is a
face. Both owed, and said rather than half-implemented.

⚠ **An element with text cannot have children**, and the layout tree is what says so: a node that
measures itself and also has children has its size decided twice, by two rules that do not have to
agree. So a text element is a leaf, full stop, and mixed content is what the owed run list is for.

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

⚠ **ExCSS expands `border-color` and `border-radius` too**, the same way it expands `margin`. Written
against the shorthands, every border and every rounded corner in the document silently disappears —
the second time that assumption has cost something in this assembly.

⚠ **A corner radius arrives as two lengths** — `8px 8px`, the horizontal and vertical radii of an
ellipse — even when the stylesheet wrote one. `DrawCommand` carries a single radius where CSS has
four corners each with two, so the top-left horizontal one is taken and the rest dropped. Right for
every circular corner, wrong for an elliptical one; owed rather than approximated further, because a
half-right rounded corner reads as a bug in the renderer rather than a gap in the model.

## Input

**Front to back means the last child first.** A later sibling is painted over an earlier one, so it
is the one a click lands on; testing in document order returns whatever happens to be underneath.

⚠ **Being outside an element is not a reason to skip its children.** `overflow: visible` is CSS's
default and means precisely that a child may hang outside its parent and still be drawn — so it must
still be clickable. Returning early on a missed parent makes every dropdown, tooltip and popover
unhittable, and the bug looks like the click landing on whatever is behind them. The clip is asked
about on the *parent*, because it is the parent that clips and the child has no idea it is being cut.

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

The same gap stays open, narrowly, for the other inherited properties that take relative units —
`line-height`, `letter-spacing`, `word-spacing`, `text-indent`. None of them feeds back into the unit
it is written in, so the error is bounded at one level rather than growing. The general fix is a
computed-value stage, recorded in doc 14.

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

Verified by sabotage: reading glyph offsets as absolute fails 1, a quad that ignores the font size
fails 1, an unflipped baseline fails 1, a threshold range that does not scale fails 1, a nested clip
that replaces fails 1, a clip that is never popped fails 1, a box not parameterised from its centre
fails 1, emitting empty draws fails 3, and a dropped glyph that is silent fails 1.

**Owed:** paths. Filling one needs a tessellator and stroking one needs that plus a join and cap
model, so they are skipped rather than approximated — and skipped visibly, since a batch with no
indices produces no draw. Also owed: a wider index, because nothing is emitted past what a `ushort`
can reach and a dense editor frame could pass sixteen thousand quads. Refusing is what is honest
until then; running over is silent and looks like geometry from the top of the frame appearing in
the middle of it.

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

### Owed

Named slot projection (`slot="footer"` on a child), `scoped` actually scoping, a component's
stylesheet loaded once per type rather than per instance, and a
longest-increasing-subsequence pass so a reorder moves a minimal set rather than every surviving
item. The last one is correctness-neutral: a move that changes nothing returns immediately, so an
unchanged list already costs a walk and nothing else.
