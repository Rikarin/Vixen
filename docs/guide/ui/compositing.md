---
title: Compositing groups
slug: ui/compositing
kind: guide
area: Core
summary: How a translucent subtree is rendered into a surface of its own and blended back once — the offscreen pass behind `opacity`, `filter: blur()`, the seven colour functions, `drop-shadow()` and `mask-image`, why a group is not the same as fading each element, why a colour matrix and a mask cost neither a surface nor a pass where a blur and a drop shadow cost both, why a mask's seam is fixed on both executors where a matrix's is free, why a drop shadow's seam is fixed by arithmetic that does not commute, how a colour matrix with zero coefficients turns a surface into a tinted silhouette, how a list of mask layers is folded into one coverage and what `mask-composite` means for each, when the pass is skipped as an exact identity, what the surfaces cost, how a backdrop filter is a replay of the draw-list prefix rather than a read-back and what that cost the compositor's walk, what gradient text would still need on top of it, how `rotate` and `scale` ride the composite quad's four vertices for the price of no shader at all, and why `mix-blend-mode` is the one group-wide effect that has to read its destination — free on the software rasteriser, and still owed on the device.
api: [T:Vixen.Ui.Rendering.UiLayer, T:Vixen.Ui.Rendering.UiBlend, T:Vixen.Ui.Rendering.UiBlendMode, T:Vixen.Ui.Rendering.UiColorMatrix, T:Vixen.Ui.Rendering.UiDropShadow, T:Vixen.Ui.Rendering.UiBackdrop, T:Vixen.Ui.Renderer.UiBackdropSource, T:Vixen.Ui.Rendering.UiMask, T:Vixen.Ui.Rendering.MaskComposite, T:Vixen.Ui.Rendering.UiTransform]
tags: [ui, rendering, opacity, blur, filter, compositing, offscreen, mix-blend-mode, blend, isolation, filters, grayscale, colour-matrix, drop-shadow, backdrop-filter, mask, mask-image, mask-composite, transform, rotate, scale]
since: 0.2
status: preview
related: [ui/gradients, ui/utility-composition]
---

## What it is

A **composited group** is a subtree of the interface that is drawn into a surface of its own and then
blended back into the frame as a single image. `UiLayer` is the record that describes one: which
draws belong to it, how big its surface has to be, and what it is faded by.

Seven things open a group: an element whose `opacity` is below one, an element with a `filter`, one
with a `mask`, one with a `backdrop-filter`, one with a `rotate` or a `scale`, one with a
`mix-blend-mode`, and one with `isolation: isolate`.

The difference between the first two is worth stating up front, because it is why the second could
not be approximated while the compositor was being built. An opacity *can* be faded element by
element —
badly, but visibly — whereas a filter is a function of the rasterised subtree. With no surface there
is nothing to convolve, and pushing a colour matrix down onto each command instead would be right on
a bare panel and wrong the moment two of the group's children overlap with partial coverage: CSS
transforms the group's *rendered result*, which is why Filter Effects 1 § 5 makes any `filter` other
than `none` a stacking context.

⚠ **The two filters cost wildly different things and the guide separates them throughout.** A blur
needs a scratch target, two more passes and a wider bound; a colour matrix needs a pipeline switch on
a draw that was happening anyway.

The pieces, top to bottom:

| Where | What it does |
|---|---|
| `DrawListBuilder` | Brackets a translucent, filtered, masked, transformed, blended or isolated subtree with `LayerPush` / `LayerPop` |
| `DrawBatcher` | Gives each bracket a `BatchKind.Layer` batch of its own, never merged |
| `UiGeometryBuilder` | Resolves the brackets into `UiGeometry.Layers`, outsets the bounds by a blur, and emits the compositing quad |
| `UiRenderer.Compose` | Renders each group's pass on the device, sweeps a blur over the ones that ask, and hands each group's colour matrix to its composite draw |
| `SoftwareUiRasterizer` | Executes the plan on the CPU, which is where the visual baselines render |

## What it is for

**A group is not the same as fading each element, and the difference is visible.** CSS Compositing 1
§ 3 renders a translucent element's subtree into an isolated surface and blends that surface once.
Multiplying each element's alpha instead lets a subtree show through *itself*: two overlapping
children of a half-opaque panel each become translucent, so the lower one is visible through the
upper one that is supposed to cover it.

The two answers agree exactly when the subtree draws one thing, and diverge as soon as it draws two
that overlap — which includes the very ordinary case of a box with both a background and a border.
That is why `:disabled`, which is `opacity: 0.55` on every control in `ControlTheme`, changed
appearance when this landed: a disabled button's border ring and its glyphs had been fading against
the background they sit on, and now the whole button is composed at full strength and faded once.

## Using it

Nothing to call. Write `opacity` in a stylesheet — or `opacity-50`, the utility — and the group is
formed for you:

```css
.panel { opacity: 0.5; }
```

What is worth understanding is **when a surface is actually allocated**, because it is not every
time:

- **A subtree that comes to one draw command is collapsed back to a plain fade.** Compositing a
  single premultiplied fragment `F` through a surface and blending it at `a` gives `a·F`; folding `a`
  into that one command's alpha before it is premultiplied gives the same `a·F`. This is an identity,
  not an approximation. It matters because a tree of a hundred outliner rows, each fading one icon,
  would otherwise ask for two hundred surfaces and two hundred render passes a frame.
- **A subtree that draws nothing opens no group**, and `opacity: 0` skips the subtree entirely rather
  than compositing it and throwing the result away.
- **A group that inks nothing the clip allows through is dropped**, because a zero-sized surface is a
  validation error rather than an empty picture.

`UiTest.Geometry` reports what actually happened, which a screenshot cannot: a group composited when
it did not need to be draws an identical picture and costs a pass.

```csharp no-compile="a fragment; `ui` is a `UiTest` the caller built and framed"
Assert.Empty(ui.Geometry.Layers);   // the fade was folded into the one command
```

### Two decisions that keep the renderers honest

The GPU renderer and `SoftwareUiRasterizer` execute the *same* plan — `UiGeometry.Layers`, computed
once in `Vixen.Ui` — rather than each deciding for itself which draws belong to which surface. Two
properties of that plan are load-bearing, and both exist to remove a way the two could disagree.

**A layer's surface is the size of the whole viewport, not of the group.** A surface sized to the
group would need every vertex inside it translated by the group's origin, on both paths, in the same
direction, with the same rounding. A disagreement there is a subtree drawn a pixel off — which no
unit test would be looking at, and which the committed screenshots would report as a diff somewhere
else entirely. At viewport size there is no translation to get wrong. It costs memory on any frame
that has a translucent group at all.

**A layer's bounds are the ink, not the element's box.** Opacity isolates a subtree without bounding
it, so a child that overflows its half-opaque parent is still part of the group. The bounds are read
back from the vertices the group emitted — the only complete account of what it drew — and rounded
*outward* to whole pixels, because a group's edge is antialiased and a bound rounded to the nearest
pixel would clip a hairline off whichever side the fraction fell the wrong way.

### `filter: blur()`

`blur-2`, or the CSS it assembles into, puts a Gaussian over the group's finished surface:

```css
.panel { filter: blur(4px); }
```

The length is the Gaussian's **standard deviation**, which is what Filter Effects 1 § 8.4 says and
*not* what `box-shadow`'s third length means — that one is the total distance an edge fades over and
is halved on the way to the shader. Three things follow from putting the filter on the surface
rather than on each primitive:

- **The group's bounds grow by three sigma before the clip narrows them.** A blur moves coverage to
  pixels no vertex of the group ever touched, so the compositing quad has to be wider than the ink or
  the halo is cut off flush with the unblurred silhouette — a soft edge with a hard line across it.
  The outset happens first and the clip second, because an ancestor's `overflow: hidden` clips the
  *filtered* result.
- **A blurred group is never collapsed.** The single-command peephole above is an identity for
  opacity and nonsense for a filter, and a `blur-*` on a bare panel is exactly one background
  rectangle — the common case, not a corner of one.
- **On the device it is two more render passes and one shared scratch target.** A separable blur
  cannot read and write one attachment, so the surface is swept across into a scratch and back down
  into itself. The scratch is borrowed and handed back between the group's own pass and its
  `ShaderRead` barrier, so *one* is enough for the whole frame however many groups are blurred.

`UiRenderer.Blurred` is what says it happened. A blur has three ways of quietly not happening — no
`UiShaders.Blur` handed over, no `UiLayer.Blur` on the geometry, a kernel radius that came out zero —
and all three produce a correct, composited, sharp picture.

### The seven colour functions

`brightness`, `contrast`, `grayscale`, `invert`, `saturate`, `sepia` and `hue-rotate` are read too,
and they are **not** the same kind of thing as a blur:

```css
.thumbnail:disabled { filter: grayscale(1) brightness(.8); }
```

Each is a per-pixel colour transform, so several of them in one declaration compose into a single
`UiColorMatrix` — three rows of four floats, the coefficients and an offset per output channel — and
that matrix rides on `UiLayer.Filter`. What follows from *not* being a neighbourhood operation is the
whole design:

- **No second surface and no extra pass.** The matrix is applied in the fragment stage of the
  composite draw the group was going to make anyway. On the device that is `ui-colour.frag` bound
  instead of `ui-image.frag`, with the matrix in forty-eight bytes of push constant; in
  `SoftwareUiRasterizer` it is a pass over the finished surface at the same seam the blur runs at.
  The two places differ and the picture does not — see below.
- **No bounds outset.** A colour matrix moves no coverage, so `UiLayer.Bounds` is the ink and nothing
  more. `UiColorMatrix.Apply` maps transparent black to transparent black, so growing the rectangle
  would only add pixels that are provably empty.
- **A filtered group is never collapsed**, for the same reason a blurred one is not: a grey
  rectangle is not a fainter rectangle.
- **The order among them is honoured.** `invert(1) brightness(2)` is not `brightness(2) invert(1)`,
  and `UiColorMatrix.Then` composes left to right, which is CSS's order. ⚠ The order between a
  colour function and a `blur()` is *not* carried, because it cannot matter: a Gaussian is a weighted
  sum whose weights total one and the matrix is affine in premultiplied colour, so the two commute
  exactly.

`UiRenderer.Filtered` is what says it happened, and it is worth more than `Blurred` is. A blur that
did not run leaves a sharp picture — a different picture. A matrix that did not run leaves the
*right* picture wherever the group's colours happen to sit near the matrix's fixed points, so no
screenshot and no comparison of the two executors can see it.

⚠ **The arithmetic is in the engine's linear working space and a browser's is in sRGB.** Filter
Effects 1 § 8.5 runs the shorthand functions with `color-interpolation-filters: sRGB`; Vixen is
linear from the parser down. A `grayscale-50` here is therefore slightly darker than the same class
in a browser. Matching exactly would mean an encode and a decode per pixel on both executors, to
reproduce a rule the spec itself calls a legacy default — and it would cost the linearity the
paragraph above spends on commuting with the blur.

⚠ **A `filter` carrying a function the engine cannot run refuses the whole declaration.**
`filter: drop-shadow(2px 2px 4px black) blur(4px)` draws unfiltered rather than
blurred-and-missing-a-shadow, which is the rule `box-shadow` already keeps. So does
`brightness(-1)`, which is invalid CSS, and `hue-rotate(90)`, which is a bare number where an angle
is required.

### `mask-image`

A gradient can fade a group out instead of dimming it:

```css
.overflowing { mask-image: linear-gradient(to bottom, black 70%, transparent); }
```

```html
<div class="mask-linear-from-70%">…</div>
```

By default only the **alpha** of the mask gradient is read — `mask-mode` resolves to `alpha` for
every image that is not an SVG `<mask>` — so `linear-gradient(to bottom, black, transparent)` and
`linear-gradient(to bottom, #ff0000, #00ff0000)` are the same mask. Linear, radial and conic all
work, with the same geometry `background-image` uses, so a mask and a background written with the
same gradient line up exactly.

`mask-luminance` (`mask-mode: luminance`) reads the stops' **brightness** instead, which CSS Masking
1 § 7.2 defines as `luminance(rgb) × a`. ⚠ **That is a scalar per stop, so it costs no lane in the
entry and no branch in either executor**: `DrawListBuilder.MaskAlphas` computes it from colours it
has already read and writes it into the same three floats the alpha reading fills. The two modes are
not slightly different numbers — a ramp between two *opaque* colours is the identity under `alpha`
and a full ramp under `luminance`. `mask-alpha` is the opt-out, and `mask-match` (`match-source`) is
the alpha reading, because there is no SVG `<mask>` element here for it to resolve against.

Like a colour matrix and unlike a blur, a mask is per pixel: it adds **no second surface, no extra
pass and no bounds outset**, and it rides the composite draw as `ui-mask.frag`. A masked group is
never collapsed, for the reason a filtered one is not — a faded-out rectangle is not a fainter
rectangle.

⚠ **The one thing a mask does that a colour matrix does not is fail to commute.** A matrix is the
same affine map at every pixel, so it passes through a Gaussian and through a bilinear tap, which is
why the two executors are free to apply it in different places. A mask is a scalar that *varies*
with position: `m(p)·Σ wᵢsᵢ` is not `Σ wᵢ·m(pᵢ)·sᵢ` wherever the ramp is not flat across the kernel —
which is exactly over a blurred edge. So the seam is fixed rather than free. **Both executors apply
the mask at the composite draw, after the blur and after the matrix**, reading the same texture
coordinate. Folding it into the surface on either path would draw a ring of the wrong brightness just
inside a blurred edge, and `UiCompositingTests` is the only thing that would notice.

⚠ **The mask box is the element's border box, not `UiLayer.Bounds`.** The bounds are the group's ink
and a blur has already outset them; resolving the ramp against them would slide the gradient sideways
the moment somebody added `blur-sm` beside the mask.

⚠ **A mask the engine cannot resolve masks nothing**, which is the opposite of what an unpaintable
`background-image` does. A missing background leaves the element its own colour; a mask that failed
*closed* would erase it, and a blank rectangle is indistinguishable from a layout collapse. Masking 1
§ 4.1 says the same.

### A list of masks, and `mask-composite`

`mask-image` is a **list**, and so is `UiLayer`: it carries a range of `UiGeometry.Masks` rather than
one mask. `mask-composite` says how each layer meets the layers below it.

```css
.corners {
    mask-image: linear-gradient(to top, #000 50%, transparent),
                linear-gradient(to left, #000 50%, transparent);
    mask-composite: intersect;
}
```

```html
<div class="mask-t-from-50% mask-l-from-50%">…</div>
```

The four operators are Porter-Duff on the coverage alone, with the layer as the source and the
already-composed layers under it as the backdrop: `add` is `s + b(1 - s)`, `subtract` is `s(1 - b)`,
`intersect` is `s·b`, `exclude` is `s(1 - b) + b(1 - s)`.

⚠ **The default is `add`, which is CSS's initial value and not what generated stylesheets look
like.** Every `mask-*` utility writes `intersect` explicitly, so a reader who learned the property
from Tailwind's output would guess wrong. The reason the utilities write it is worth knowing, because
it is what makes them compose at all: each class fills one layer of the same three-layer `mask-image`
and the layers nobody filled resolve to a fully opaque gradient — and an opaque layer is the identity
under `intersect` and *only* under `intersect`. Under `add` an opaque layer forces full coverage
everywhere, so the mask would do nothing at all.

⚠ **The list is folded bottom-up, and only `subtract` can tell.** CSS lists mask layers topmost
first, and Masking 1 § 5.4 gives each layer's operator the composed layers below it as its backdrop —
so the walk starts at the last entry. Three of the four operators are symmetric in their two
arguments, so a fold run the wrong way would produce the identical picture under `add`, `intersect`
and `exclude`; `s(1 - b)` is the one that is not `b(1 - s)`.

⚠ **The bottom entry is taken as itself, which departs from one sentence of the specification.** Read
literally, the bottom layer composites against a transparent-black backdrop, which makes `intersect`
— `s·0` — erase everything. That cannot be what browsers do, because `mask-composite: intersect` is
what Tailwind emits for every one of its edge ramps and those ramps visibly work. Starting from the
bottom entry itself makes all four operators the identity on a list of one, which is the property
that has to hold: adding `mask-composite` to a single-layer mask must not change the picture.

⚠ **Layers that provably say nothing are dropped before the group is opened, not in an executor.** A
mask is what *makes* an element a composited group, so a list that reduces to nothing has to reduce
to nothing while the group is still being decided on — otherwise a `mask-t-from-*` that happened to be
fully opaque would cost a viewport-sized surface and a composite pass to draw a picture identical to
the one that needed neither. `DrawListBuilder.Reduce` does it, and it does the two reductions that are
true of every input: an opaque `intersect` layer is the identity wherever it sits, and an opaque
*bottom* layer is the identity when the layer above it intersects.

⚠ **A list is capped at eight layers and one unreadable layer refuses the whole declaration.** Six is
what the utility layer can generate — four edge ramps plus a radial and a conic — and past eight the
list is refused rather than truncated, because truncation drops the layers at one end and produces a
mask that nearly works. Dropping one bad layer out of the middle is worse still: it changes the
arithmetic of every operator around it, and a missing `subtract` leaves the thing it was meant to
punch out.

⚠ **The entries ride a storage buffer, not push constants.** One entry is eighty bytes and
`ui-mask.frag` already pushes a colour matrix at forty-eight; with the vertex stage's sixteen that
came to more than the 128 bytes Vulkan guarantees before the placement lane was added at all. So the
draw pushes an index and a count and the entries come through the descriptor binding `UiShape`
already uses — see the renderer's README for why that buffer has a fixed capacity.

### Placing the mask

`mask-size-*`, `mask-position-*` and `mask-repeat-*` place a **tile** inside the mask box, through
the same two parsers `background-size` and `background-position` use — Masking 1 § 4 defers to
Backgrounds 3 for all three grammars, so a mask and a background written the same way put their
layers in the same place. The tile is one pair of lanes on the entry: `AreaCentre`, and `AreaHalf`
with `mask-repeat` encoded in its **sign** — positive tiles with a period of twice the component,
negative paints one tile and gives *zero* coverage outside it, which is what CSS means by not
painting a layer there. All zero means the tile is the mask box, which is what every entry written
before the lane existed meant.

⚠ **Nothing is written unless a size or a position was stated, and that guard is load-bearing
twice.** With the tile equal to the mask box every keyword of `mask-repeat` draws the same picture,
so the lane would say nothing; and because a clipping tile is never opaque, a lane written anyway
would stop `Reduce` dropping the five opaque layers every Tailwind mask emits — turning a one-entry
list into six and opening a group for each.

`mask-radial-at-*` is a different frame again: it moves the **ramp** inside the tile rather than the
tile inside the box, and it grows the reach with the centre, because CSS's default ending shape is
`farthest-corner`. It is per *layer*, where the tile is per element — `mask-image` is a list and each
layer carries its own gradient function.

⚠ **What is still absent, and each for a stated reason.** `mask-origin-*` and `mask-clip-*` both
default to `border-box`, which is the only rectangle this engine has — there is no padding, content,
fill, stroke or view box to resolve the other values against, so every one of them would draw the
same picture. `mask-type-*` applies to SVG `<mask>` elements, which this engine has none of.
⚠ **The radial *ending shapes* used to be listed here and no longer belong**: `mask-circle`,
`mask-ellipse`, `mask-radial-closest-side` and its three siblings are all read, and
`BackgroundGradient.Reach` is the closed form for each — the refusal that stood here named a blocker
(a stated pair of radii) that had been satisfied all along. What is still refused is an ending
size stated as a `<length>{1,2}` pair, which needs two lanes the record does not have; a refused
layer is no mask at all rather than a slightly wrong one, which is why that one stays refused rather
than being approximated. `bg-clip-text` is a separate matter again — see doc 43, which names the text-coverage surface it is waiting on.

`UiRenderer.Masked` is what says a mask happened, and it is worth having for `Filtered`'s reason plus
one: `ui-mask.frag` serves masked groups *and* carries the colour matrix, so a renderer that picked
the colour pipeline by mistake would draw a correctly filtered, entirely unmasked group — and
`Filtered` would still count it.

### `drop-shadow`

A **drop shadow** is a Gaussian over the group's *alpha channel*, displaced, tinted, and composited
**under** the group rather than over it. `UiLayer.Shadow` carries it, as an offset, a standard
deviation and a straight colour.

It is not `box-shadow` and shares no code with it. `DrawListBuilder.EmitShadow` draws a rounded
rectangle the shape of the border box and lets `ui-box.frag` resolve the falloff analytically,
because a box's silhouette is known in closed form. A drop shadow's is not: it is whatever the
subtree rasterised to — text, an icon's path, a partly transparent image, a masked child — so it can
only be had by blurring the coverage that was actually drawn. On a filled panel the two are the same
picture, which is exactly why a fixture made of filled panels cannot tell them apart.

**It needed no new shader, and the reason is worth knowing.** `UiColorMatrix.Apply` evaluates
`c' = M·c + o·a` on premultiplied colour, so a matrix with **zero coefficients and the shadow's
colour in its offsets** leaves `c' = colour·a` — the shadow's colour at exactly the coverage the
surface had, which *is* the tinted silhouette. `UiDropShadow.Tint` is that matrix, and
`ui-colour.frag` draws it without being told it is drawing a shadow. What the three-row matrix cannot
do is scale alpha, so a translucent shadow colour rides the shadow quad's own vertex alpha instead,
where `UiLayer.Alpha` already rides; the two multiply.

**The seam is fixed by arithmetic, and it is fixed more tightly than the mask's.** A colour matrix
may be applied wherever it is cheap, because it commutes with the Gaussian and the sampler. A mask
may not, because it varies with position. A drop shadow may not either, and for a third reason:
`blur(σ) drop-shadow(τ)` blurs the alpha channel twice and `drop-shadow(τ) blur(σ)` blurs a picture
that already has a shadow under it. So **both executors cast the shadow from the group's finished
surface, after its own blur** — `UiRenderer.ShadowSurface` and the seam in
`SoftwareUiRasterizer.Run`. `UtilityComposition.Filter` writes `drop-shadow()` last for the same
reason, which is also v4's order.

**What it costs** is a second viewport-sized surface, two more render passes, and a second quad. The
passes reuse the blur's shared scratch: the group's surface sweeps across into the scratch and down
into the shadow's, so unlike a blur — which has to land back where it started — nothing is copied. A
shadow with no blur at all is one pass rather than two, and that pass is a single-tap convolution.

**Bounds** are outset by the *wider* of the group's blur and the shadow's, not their sum: the shadow
reads the surface and not the shadow, so the two reaches do not compose. The **offset is not in the
bounds at all** — the group's surface has not moved, only the shadow's quad has, and it carries the
displacement in its vertices with texture coordinates taken from where it would have been.

⚠ **A `mask` on the same element cuts the shadow in the shadow's own frame rather than where it
lands**, which is a stated divergence. CSS applies `filter` first and `mask` to the result, so a
browser cuts the shadow at its real position; both executors here recover a mask's point as
`uv × size`, and the shadow quad's UV is deliberately the un-displaced one. Exact when the offset is
zero. The alternative — leaving the shadow unmasked — is worse rather than differently wrong: a faded
element casting a hard-edged shadow is ink escaping a mask. Closing it properly needs the
displacement in a push constant so the fragment can evaluate the fold at `uv × size + offset`.

⚠ **One shadow per element.** CSS lets `filter` hold any number of them and each is a surface and two
passes; a second is refused, and refusing takes the whole declaration with it. That is the rule
`DrawListBuilder.Filter` keeps for every function it cannot execute, and it is what stops a
half-applied filter looking like a working one.

### `backdrop-filter`

**`backdrop-*` is a different feature wearing the same names, and what it reads is the destination the
group is about to composite *into*.** At the moment a group's surface is rendered that destination has
not been drawn: `UiRenderer.Compose` records every group's pass **before** the host's frame pass begins
— it has to, because passes do not nest — so nothing painted below the group exists yet. By the time
the composite is submitted the destination is the colour attachment being written, which is not
sampleable without an input attachment or a copy out of the pass, and this renderer's command list has
neither.

**None of which turned out to matter, because a read-back is not what this needs.** The draw-list
prefix behind a group is *replayable*, and replaying it is what turned this from a capability the
backend lacks into a scheduling problem the compositor solves. Four things moved, and the last two are
why it was a branch of its own rather than a rider on the drop shadow.

**1. Every group here is already a backdrop root, which is the one piece of luck.** Filter Effects 2
says an element forms a *backdrop root* if it has a filter, an opacity below one, a mask or a
clip-path — and a `UiLayer` exists in this engine for precisely those reasons and no other. So the
backdrop of a group nested in another group is **the parent's own surface content so far**, and never
an accumulation up the ancestor chain. There is no recursion, and — the part that matters at execution
time — a nested group's backdrop starts from *transparent black*, because that is what its parent's
surface starts from. Only a top-level group's backdrop has ever seen the host's frame.

**2. The capture is a re-render of the prefix, not a read-back.** For a group `g` with parent `p`, the
backdrop is what `Submit(self: p)` would draw from `p.First` up to `g.First`. `Submit` already walked
exactly that range and already skipped nested groups in favour of their composites; it needed one extra
`stop` argument. `UiRenderer.Capture` is that call, wrapped in a pass of its own, confined to the
group's border box outset by the backdrop's kernel — then the same blur machinery a drop shadow uses,
and a second quad drawn under the group's, which is the arrangement `UiLayer.ShadowImage` demonstrates.

**3. `Compose`'s walk is post-order, where it used to be reverse pre-order.** The old loop put a
group's children before it — which is the one ordering it needed, since an outer group's pass samples
its children's surfaces — and a group's **later** siblings before its earlier ones. A capture needs the
opposite of that second half: everything painted behind `g` must be finished before `g`'s capture pass
runs. **Post-order satisfies both** — each child subtree in document order, then the parent —
so `UiRenderer.Forest` replaced the loop rather than extending it.

⚠ **Nothing else in that loop depended on the old order, and it was checked by running the fixture
without a backdrop in it rather than by reading the code.** Each group's turn is self-contained: one
barrier in, a pass, an optional blur and shadow that borrow the shared scratch and hand it back, one
barrier out. The only state two turns share is that scratch, whose borrow begins and ends inside one of
them, and each surface's own `ResourceState`. Post-order is therefore a strict tightening of the
constraint the reverse walk met, not a different one.

**4. The host's content is not the compositor's, and that is the public API change.** `Record` draws
into a pass the host has already begun; the UI's own draw list is all `Compose` can re-render. A
capture built from it alone is *the interface behind the element and nothing else* — so a glass panel
over a 3D scene would blur nothing, which is the single commonest reason to reach for the feature. It
is worse than incomplete: the captured backdrop is then not opaque, and compositing a blurred
translucent copy **over** the sharp original is a double image along every edge, where CSS *replaces*
the backdrop within the element's bounds. So the host hands over what it has already painted, as
`Compose(commands, geometry, surface, scale, UiBackdropSource beneath)`.

⚠ **A colour *and* a texture, where the design said a texture.** All three call sites in this
repository begin the interface's pass with a `LoadAction.Clear` and have painted nothing else:
`Vixen.Editor.Host`, the app template and `UiCompositingTests`. What they have "already painted" is a
colour, and a colour is also the only thing that can reach `SoftwareUiRasterizer` — whose capture is a
clone of a buffer that already holds the background, so the two executors agree only if the device's
capture starts from the same ground. `UiBackdropSource.Image` is the other half, for a host that has
genuinely rendered a scene into a texture of the interface's size; it is drawn full-screen into the
capture before the prefix is replayed over it. **A host that passes nothing gets the degraded reading**
and `UiRenderer.Backdropped` is what says the capture happened at all.

⚠ **`UiRenderer.Backdropped` matters more than any other counter on the class, because a backdrop
filter over a flat field is the identity.** Blurring a uniform colour returns it; greying an
already-grey one returns it. So a fixture whose panels sit on plain background cannot tell a working
capture from no capture — and neither can `UiCompositingTests`, because the software path would be
reproducing the same nothing. That is a second vacuity on top of the one every `filter` has, and it is
worse: the colour matrix's picture is at least a function of the group's own contents.

⚠ **The two blurs are convolved through different quads and one of them is not a quad at all.** A
group's own blur sweeps through its composite quad, which `UiGeometryBuilder.Layer` has already outset
by the kernel — so the second axis never reads outside itself for a pixel that matters. A backdrop's
quad is the border box with **no** outset, because that is what the result is clipped to, so sweeping
through it makes the second axis read the cleared scratch just outside the box and darkens the panel's
whole rim. Measured before it was fixed: twenty pixels along the top edge of the compositing fixture's
glass panel, up to ten levels dark. The backdrop's sweeps therefore run over the whole confined region,
through four vertices of the renderer's own — `UiRenderer.Fullscreen`, the only geometry in that file
that is not the frame's.

⚠ **`opacity()` is a backdrop function and `drop-shadow()` is not, which is the mirror of `filter`.**
`backdrop-opacity-*` is one of Tailwind's ten roots and `UiColorMatrix` has three rows and cannot scale
alpha, so it lands on `UiBackdrop.Alpha` and rides the backdrop quad's own vertex alpha — exactly where
a drop shadow's colour alpha rides. `drop-shadow()` inside a `backdrop-filter` is refused, and takes
the whole declaration with it: a shadow of the backdrop is a silhouette composited under a picture that
is already behind everything.

⚠ **A backdrop costs a second pass over everything behind the element, and the counters say so out
loud.** The capture replays the prefix, so a composite in that prefix carrying a colour matrix is
submitted twice and `UiRenderer.Filtered` counts it twice; the same goes for `Masked`. Neither is
bounded by the layer count any more. That is the price of the feature rather than a bookkeeping
artefact, and it is what makes the price visible.

⚠ **The filtered backdrop is clipped to the border box and *not* to its corner radius, which is a
stated divergence.** `UiLayer.Bounds` is the group's *ink* — a child overflowing the element makes it
bigger — so a rectangle taken from there would put blurred scene outside the panel that asked for it;
`UiLayer.BackdropBounds` carries the border box instead, which closes that half. The radius is not
closed: a `UiLayer` carries none, and `rounded-2xl backdrop-blur-md bg-white/30` is the canonical use
of this feature, so the blurred picture shows square corners just outside the rounded ones. The mask
machinery cannot express a rounded rectangle — its shapes are linear, radial and conic ramps — so
closing it needs a rounded-rect signed distance in the composite fragment, which is a change to
`ui-image.frag`, `ui-colour.frag` and `ui-mask.frag`, to the three committed copies of each, and to
`SoftwareUiRasterizer.Composite`. The ten `backdrop-*` roots read **partial** in `docs/plan/43` for
this reason and this reason alone.

⚠ **An element that paints nothing of its own used to get no backdrop, and the reason recorded for it
was a claim about these two executors that was not true of either.** The claim was that both walk the
layer list by matching a draw index, so a group with `Count == 0` would match its own start and never
advance. `SoftwareUiRasterizer` advances its `next` cursor as it *enters* a group, so a zero-width
range leaves the draw index where it was and the next turn of the loop executes the composite quad
standing at it; `UiRenderer.Forest` takes a group's descendants as the entries whose `First` is
*strictly* inside its range, which an empty range has none of, and hands the index straight on.
`Confine` already falls back to the whole attachment for a degenerate rectangle, and `Submit` over an
empty range is a pass that clears and draws nothing.

What was really dropping the group is the guard in `UiGeometryBuilder.Layer` that refuses a layer
whose ink is empty — and *that* one is real: a zero-sized surface is a validation error rather than an
empty picture. A backdrop is the one thing a group carries that is not a function of its own ink, and
the rectangle it wants is `BackdropBounds`, the border box it was going to be clipped to anyway. So an
inkless group keeps its layer when a backdrop survived, bounded by that box, and every other inkless
group is still discarded — a blur, a colour matrix, a mask and a drop shadow are each a function of
ink there is none of. `BackdropFilterTests` pins both halves.

⚠ **The trap this feature sets is that `SoftwareUiRasterizer` can do it in three lines**, because its
recursion already holds the parent's buffer while it runs the group's. A backdrop filter written there
alone would look implemented and would surface as a *compositing divergence* in `UiCompositingTests`
rather than as a missing feature. Both halves landed together, and the fixture there carries a fourth
group whose backdrop straddles the busiest part of the frame — which is also the only thing in the
repository that can see the post-order change, since the other three groups are correct in either
order.

### What the surfaces cost

Measured rather than argued, because nothing had measured it. A `UiRenderer` at **1920 × 1080** on an
Apple M-series GPU through MoltenVK, timed with `GpuProfiler` around `Compose` alone, best of ten
frames:

| Frame | Extra passes | `Compose` |
|---|--:|--:|
| 12 groups, none blurred | 12 | **1.10 ms** |
| 12 groups, one blurred at σ = 4 | 14 | 1.27 ms |
| 12 groups, one blurred at σ = 16 | 14 | 1.64 ms |
| 12 groups, all blurred at σ = 4 | 36 | 2.30 ms |
| 12 groups, all blurred at σ = 16 | 36 | 5.88 ms |

Twelve groups is the editor's opening frame. ⚠ **The first row is the number worth looking at**: a
seventh of a 60 Hz frame, spent almost entirely clearing and storing twelve viewport-sized targets
that each hold one panel — and it was there before blur was. Memory is the same story: twelve
`Rgba8UNorm` targets at this size are **95 MiB**, kept between frames.

What the blur adds on top of that is a **shared scratch target — one, 7.9 MiB** — and roughly
**0.17 ms per blurred group at σ = 4, 0.40 ms at σ = 16**. The one-per-frame scratch is the part that
was expected to be worse: the obvious reading of "a separable blur needs a second surface" is a
second surface *per blurred group*, which at twelve would have been 190 MiB rather than 103 MiB.

The honest summary is that **blur is not the expensive part of this design and the surfaces are.**

### Confining the passes to the group's bounds

Half of that first row has since been taken back, and **not** by shrinking the surfaces. Each
group's pass is given a **render area** of `UiLayer.Bounds` — `UiRenderer.Confine` — so the clear and
the store touch the three hundred by four hundred pixels the panel occupies instead of the whole
attachment. The allocation is untouched and still viewport-sized, so every correctness argument in
`UiLayer`'s remarks survives word for word: there is still no origin to translate, and both executors
still composite through the same rectangle.

⚠ **A render area and not a scissor.** A scissor confines draws; the clear happens when the pass
begins, before any draw, and obeys the render area alone. The same distinction cost the virtual
shadow atlas every page but the last drawn.

Measured the same way as the table above — `GpuProfiler` around `Compose` alone at 1920 × 1080,
MoltenVK on an Apple M-series GPU — but reported as the **10th percentile of forty-eight frames**
rather than the best of ten, because a scope absorbs a queue stall and the upper half of the
distribution is machine state rather than work. Two runs of each build, and the first configuration
in a process is discarded: it pays the clock ramp and reads fifty to a hundred per cent high.

| Frame | Before | After | |
|---|--:|--:|--:|
| 6 groups, none blurred | 0.45 ms | 0.25 ms | **−45 %** |
| 12 groups, none blurred | 1.00 ms | 0.53 ms | **−47 %** |
| 24 groups, none blurred | 2.07 ms | 1.09 ms | **−47 %** |
| 12 groups, all blurred at σ = 4 | 5.45 ms | 4.90 ms | **−10 %** |

⚠ **"Almost all of it is clear-and-store bandwidth" was the premise, and it is an overstatement — the
lever is worth about half, not about all.** The same twelve passes over a **480 × 270** attachment
cost 0.42 ms before the change and 0.42 ms after it: at that size there is no bandwidth left to save
and what remains is fixed per-pass cost — the encoder, the two barriers, the bindings — at roughly
**34 µs a pass**. Twelve of those is 0.41 ms, which is the floor the 0.53 ms above is sitting on.
Content makes almost no difference either way: twenty-four rows per panel instead of one costs 0.10 ms
more, before and after alike, which is what says the cost is per-target and not per-draw.

A blurred group gains far less because its two sweeps were never viewport-wide to begin with — the
composite quad already bounded them — so all the render area removes there is the group's own content
pass, about 0.04 ms out of 0.45.

**The 95 MiB is untouched and is the harder half.** It needs bounds-*sized* surfaces, which is the
change `UiLayer` argues against, and it cannot be pooled away: every group's surface is still live
when the frame's own pass samples it. What has changed is the *case* for making it — the time
argument is now mostly spent, so what is left to buy is memory alone.

### `transform`, `rotate` and `scale`

A transform is the fifth thing that opens a group, and the only one where the group is not an
isolation but a change of coordinates:

```css
.badge { rotate: -12deg; scale: 110%; }
.card  { transform: translate(20px) rotate(-6deg) skewX(4deg); }
```

CSS Transforms 2 splits the same idea two ways: three independent properties — `translate`, `rotate`,
`scale` — and one `transform` taking a list of functions. Both are read, and both end up in the same
`UiTransform`. What this engine reads inside a list is `matrix()`, `translate()`/`translateX()`/
`translateY()`, `scale()`/`scaleX()`/`scaleY()`, `rotate()` and `skew()`/`skewX()`/`skewY()`.

⚠ **Three orderings decide the picture, and none of them is the one a reader guesses.**

- **The last function in a list is applied to a point first.** `transform: A B` is the matrix product
  `A · B`, so `rotate(90deg) translate(40px)` moves the element along its own *turned* axis while
  `translate(40px) rotate(90deg)` moves it across the screen. Invisible on every one-function
  declaration, which is most of them.
- **`transform` is applied before the three independent properties**, per Transforms 2 § 3, which
  builds the matrix as translate, then rotate, then scale, then `transform` — again as multiplications,
  so the list is the innermost factor.
- **A list shares one `transform-origin`.** It is composed first and re-centred once; re-centring each
  function separately is a different picture as soon as a translation is among them.

⚠ **A list this engine cannot read is dropped whole, and only the list.** `rotateX`, `translate3d`
and `perspective` are legal CSS and there is no third axis here — reading the functions that happen
to be flat and skipping the rest turns a card flip into a card that never moves, which is a different
picture rather than a degraded one. The `rotate` or `scale` beside it still applies, because CSS drops
an invalid declaration and leaves its neighbours alone.

⚠ **A percentage inside `translate()` is of the element's own border box**, per Transforms 1 § 8 —
the opposite of every percentage in the box model, and the same rule the `translate` property follows.

**A `DrawCommand` is an axis-aligned rectangle, and none of them was rotated.** That is worth stating
first because it was the standing reason this could not be done. The subtree rasterises into the
group's surface exactly as it always did — upright, every command a rectangle, every clip a rectangle
— and the matrix is spent on the four vertex positions of the *composite quad*. CSS agrees that this
is the seam: Transforms 1 § 3 makes any transform other than `none` a stacking context, in the
sentence shape Filter Effects uses for `filter`.

⚠ **It costs no shader and no vertex format.** Both executors already interpolate a quad's texture
coordinate linearly across its two triangles, and an affine map is exactly the class for which that
interpolation is *exact* rather than approximate — so the two triangles agree along the shared
diagonal and no seam appears. Moving four positions and leaving four coordinates alone is the whole
of it. `perspective` is a different feature rather than a bigger one, for the same reason: a
projective map needs a `w` this vertex format has nowhere to put, which is why `UiTransform` is a 2D
affine and cannot express one.

**Layout never sees it, and neither does the subtree's own geometry.** `UiDocument.Accumulate`
composes the matrix per element and deliberately does not pass it down — children accumulate from the
untransformed position and are carried along by the group. A scaled element keeps the space layout
gave it, so `scale: 150%` overflows its row rather than widening it, which is what Transforms 1 § 3
requires. It is also what keeps glyphs out of it: text is shaped once at its layout size and the
*surface* is scaled, never re-shaped.

⚠ **The pointer is transformed too, and in one line.** `UiDocument.HitTest` maps the point through
the inverse at the top of the walk, before anything looks at it, so `Contains` and `Cut` go on
comparing the absolute rectangles they always compared. Nested transforms compose because the
recursion does. A transformed element whose hit test was untransformed is a control you can see and
cannot click, which is worse than an unimplemented one — `Vixen.Ui.Tests.TransformTests` is what holds
the two together.

**A degenerate transform paints nothing.** `scale: 0` has no inverse, so the subtree is skipped
outright — the same treatment `opacity: 0` gets, and the hit test refuses it through the same
singular matrix rather than leaving an invisible control taking clicks.

#### What a transform costs, and the two places it is not free

⚠ **A viewport-sized surface and a render pass per transformed element.** This is the real price and
it is the same one `opacity` pays. It is fine for a panel and expensive for a list of rotated
chevrons; there is no per-command fast path, because there is no rotated `DrawCommand` to fast-path
to. An identity — `rotate: 0deg`, `scale: 100%` — is collapsed back to nothing before a group is
opened, which matters because those are written constantly.

⚠ **A blur or a drop shadow on a transformed group takes a slower sweep.** `UiRenderer.BlurSurface`
and `ShadowSurface` convolve a group's surface by drawing *through* its composite quad, which is
correct only while the quad and the surface share a space — under a transform they do not. Both fall
back to the full-region sweep already written for backdrops: correct at any transform, and it costs
the whole target rather than the group's rectangle. `SoftwareUiRasterizer` convolves the whole buffer
either way, so this is a divergence only `UiCompositingTests` can see, and it is in that fixture.

⚠ **`backdrop-filter` is refused on a transformed group** rather than approximated. It is the one
surface holding something the group did not draw, so a rotated backdrop quad would have to sample a
rotated window of a captured picture — four texture coordinates that are no longer an axis-aligned
rectangle, and a capture region that is no longer the border box. Sampling the untransformed patch
instead would show the scene from where the element *was*. `UiGeometryBuilder.Layer` drops the
backdrop and the group composites normally.

⚠ **A transform cannot bring on-screen what was never rasterised.** The surface is the viewport's
size and holds the group at the coordinates it always had, so an element mostly outside the viewport
and scaled down to fit shows only the part that was already visible. An *ancestor's* clip does not
have this problem — the clip is pulled back through the transform before it narrows the group's
bounds, so a rotated panel near a clipped edge keeps the corner the rotation swings into view.

**Not implemented:** `transform` itself, and `skew-*` with it. There is no `<transform-function>`
parser — no `matrix()`, `rotate()`, `scale()` or `skew()`, and no list-of-functions in `StyleValue` —
so those are a parser away rather than a renderer away. The 3D family (`perspective`,
`transform-style`, `backface-visibility`, the `-z` axes) needs a third axis and a projective
composite as well.

### Blending a group with what is under it

`mix-blend-mode` is the sixth reason to open a group and the only one whose answer is a function of
**two** pictures. Everything above it — the fade, the Gaussian, the colour matrix, the mask, the
transform — is computable from the group's own surface, which is why each of them can be applied at
the seam where that surface is finished and no executor has to know where the composite quad will
land. A blend cannot: its second operand is the backdrop.

⚠ **It is nevertheless not a second blend state, and that is what makes it cheap where it is
implemented at all.** CSS Compositing 1 § 5.1 defines the whole feature as a change of *source*
colour followed by an ordinary source-over:

```
Cs' = (1 - αb)·Cs + αb·B(Cb, Cs)
```

So `UiBlend.Apply` takes the group's premultiplied fragment and the backdrop's, and returns the
premultiplied colour to composite exactly as an unblended group's would be. `SoftwareUiRasterizer`
owns its destination buffer, so the entire cost there is one read per pixel of the composite quad.

⚠ **The blend is done on the values the surface holds, which in this engine are linear, and a browser
blends in sRGB.** That is a stated divergence rather than an oversight — `multiply` of two mid-greys
is perceptibly darker here than in a browser. Closing it means an encode and a decode per pixel in
both composites and a decision about what the interface's compositing space *is*, which is a
colour-management question rather than a blending one.

⚠ **`UiRenderer` does not implement it, and says so.** The device has no read of the attachment the
UI pass is writing — no subpass input, no framebuffer fetch, no copy — so a blended group is
submitted source-over and the picture is the one the frame would have had without the declaration.
`UiRenderer.Unblended` counts exactly that, and it needs to: a blend over a flat backdrop is often
the identity (`multiply` against white, `screen` against black), so neither a screenshot nor a
comparison of the two executors can tell. **Closing it is a shader change and not a pass change** —
the capture `UiRenderer.Capture` already performs for `backdrop-filter` is precisely the backdrop
picture the formula wants, so the missing piece is a composite variant that samples two textures.

### Isolating which backdrop a blend reaches

`isolation: isolate` is the seventh reason to open a group and it changes no pixel of the group it
opens. Its only defined effect is on a **descendant's** `mix-blend-mode`, and it bounds it by being a
boundary: a nested group's draws are executed into its parent's surface, so a blended descendant of
an isolated ancestor mixes with that ancestor's accumulation and can never reach the page behind it.

⚠ Where the ancestor has painted nothing, that accumulation is transparent black — and § 5.1 weights
the blend by the backdrop's alpha, so a blend against nothing is `normal`. That is why an isolated
wrapper makes a `mix-blend-multiply` child come out unblended rather than come out black.

⚠ The peephole in `DrawList.Collapse` deliberately does **not** exclude an isolated group, where it
does exclude a blended one. Isolation is only observable through a descendant that opened a group of
its own, and such a descendant contributes at least three commands — so the collapse's own
`drawn == 1` test is already the statement that there is nothing to isolate.

### What a group does not change

**Positions.** `UiDocument.Accumulate` is where the draw list, hit testing and arrow navigation agree
about where an element is. A composited subtree keeps its document coordinates — that is a direct
consequence of the viewport-sized surface above — so a click still lands on the element it looks like
it landed on. There is a test for it rather than only an argument, because the argument stops being
true the moment somebody shrinks a surface to its group to save memory.

⚠ `rotate` and `scale` are the exception and prove the rule: they *do* move where a click lands, and
they are the only group cause that does. What keeps them honest is not that positions are untouched
but that one matrix is applied in both places — see the section above.

**The clip stack.** A group's compositing quad carries the scissor that was in force where the group
opened, so a group inside `overflow: hidden` composites inside it. A group's own `overflow` clip is
pushed and popped entirely within the bracket, so the stack is balanced across a group and the clip
at the pop is the same one as at the push.

## Examples

A half-opaque panel with two overlapping children — the case that separates the two models:

```css
.group { opacity: 0.5; }
.lower { position: absolute; inset: 0 0 auto 0; height: 20px; background-color: #ff0000; }
.upper { position: absolute; inset: 0 0 auto 0; height: 10px; background-color: #00ff00; }
```

Where the two children overlap, the red is *covered* — it is opaque green over opaque red inside the
surface — and only the result is faded. Fading each child separately would leave a visible red
component of about 64 showing through the green.

Nested groups multiply, because each is its own surface and each is blended once:

```css
.outer { opacity: 0.5; }
.inner { opacity: 0.5; }   /* composited at 0.25 of the frame */
```

## See also

- [Gradients](gradients.md) — `BoxStyle` and the shader record a box carries.
- [Utility composition](utility-composition.md) — where `opacity-50` comes from.
- **The three features this pass was built for. <s>None of which is implemented yet.</s> One of them
  is.**
  `filter: blur()` <s>needs</s> **needed** a second pass over a finished layer, with a separable
  kernel and a bounds expansion by the blur radius — and that is what it now is, above. The scoping
  was right except in one detail, which is worth recording because it was the detail that decided
  the memory cost: a blurred group needs a second *target*, not a second *surface per group*. The
  two sweeps finish inside one group's turn in `Compose`, so a single scratch serves the whole frame.

  `bg-clip-text` ⚠ **is further away than "use the layer as a mask" makes it sound, and the layer is
  the wrong thing to sample.** A layer surface holds the group's *rendered colour* — glyphs already
  multiplied by their run colour — and Tailwind draws gradient text as `bg-clip-text` plus
  `text-transparent`, so the surface a mask path would sample is the one where the glyphs were drawn
  invisible. What the feature needs is the glyph **coverage** rendered to a target of its own, which
  is a second way of drawing text rather than a second way of reading a layer. There is also no
  entry point to it: `bg-clip` is not a registered family, `background-clip` is not a property
  anything parses, and `bg-clip-text` is this repository's standing *example* of a class that does
  not resolve — `ShadowedFamilyTests`, `StyleGenTests` and `docs/guide/ui/stylesheet-diagnostics.md`
  all assert it as a refusal. It is not on `InertProperties.txt` and could not be: nothing emits it.

  `scale-*` and `rotate-*` remain refused: a scaled subtree needs re-shaping because glyph advances
  are shaped at layout time, and a rotated clip cannot be the axis-aligned rectangle the clip stack
  requires — a surface makes the *raster* transformable but changes neither of those two facts, and
  a second surface existing changes neither of them either.
