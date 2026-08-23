---
title: Compositing groups
slug: ui/compositing
kind: guide
area: Core
summary: How a translucent subtree is rendered into a surface of its own and blended back once — the offscreen pass behind `opacity`, `filter: blur()`, the seven colour functions, `drop-shadow()` and `mask-image`, why a group is not the same as fading each element, why a colour matrix and a mask cost neither a surface nor a pass where a blur and a drop shadow cost both, why a mask's seam is fixed on both executors where a matrix's is free, why a drop shadow's seam is fixed by arithmetic that does not commute, how a colour matrix with zero coefficients turns a surface into a tinted silhouette, how a list of mask layers is folded into one coverage and what `mask-composite` means for each, when the pass is skipped as an exact identity, what the surfaces cost, and what `backdrop-filter` and gradient text would each still need on top of it.
api: [T:Vixen.Ui.Rendering.UiLayer, T:Vixen.Ui.Rendering.UiColorMatrix, T:Vixen.Ui.Rendering.UiDropShadow, T:Vixen.Ui.Rendering.UiMask, T:Vixen.Ui.Rendering.MaskComposite]
tags: [ui, rendering, opacity, blur, filter, compositing, offscreen, filters, grayscale, colour-matrix, drop-shadow, backdrop-filter, mask, mask-image, mask-composite]
since: 0.2
status: preview
related: [ui/gradients, ui/utility-composition]
---

## What it is

A **composited group** is a subtree of the interface that is drawn into a surface of its own and then
blended back into the frame as a single image. `UiLayer` is the record that describes one: which
draws belong to it, how big its surface has to be, and what it is faded by.

Two things open a group: an element whose `opacity` is below one, and an element with a `filter`.
The difference between them is worth stating up front, because it is why the second one could not be
approximated while the compositor was being built. An opacity *can* be faded element by element —
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
| `DrawListBuilder` | Brackets a translucent element's subtree with `LayerPush` / `LayerPop` |
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

Only the **alpha** of the mask gradient is read — `mask-mode` resolves to `alpha` for every image
that is not an SVG `<mask>` — so `linear-gradient(to bottom, black, transparent)` and
`linear-gradient(to bottom, #ff0000, #00ff0000)` are the same mask. Linear, radial and conic all
work, with the same geometry `background-image` uses, so a mask and a background written with the
same gradient line up exactly.

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

⚠ **The entries ride a storage buffer, not push constants.** One entry is sixty-four bytes and
`ui-mask.frag` already pushes a colour matrix at forty-eight; with the vertex stage's sixteen that
came to exactly the 128 bytes Vulkan guarantees. So the draw pushes an index and a count and the
entries come through the descriptor binding `UiShape` already uses — see the renderer's README for
why that buffer has a fixed capacity.

⚠ **What is still absent, and now for one reason rather than two.** `mask-origin-*`,
`mask-position-*`, `mask-size-*` and `mask-repeat-*` describe placing a mask image inside a box it
does not already fill, which a gradient sized to the border box does not need. `mask-type-*` applies
to SVG `<mask>` elements, which this engine has none of. `bg-clip-text` is a separate matter again —
see doc 43, which names the text-coverage surface it is waiting on.

`UiRenderer.Masked` is what says a mask happened, and it is worth having for `Filtered`'s reason plus
one: `ui-mask.frag` serves masked groups *and* carries the colour matrix, so a renderer that picked
the colour pipeline by mistake would draw a correctly filtered, entirely unmasked group — and
`Filtered` would still count it.

## `drop-shadow`

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

### What `backdrop-*` would still need

**`backdrop-*` is a different feature wearing the same names, and the compositor cannot see what it
needs.** It reads the destination the group is about to composite *into*. At the moment a group's
surface is rendered that destination has not been drawn: `UiRenderer.Compose` records every group's
pass **before** the host's frame pass begins — it has to, because passes do not nest — so nothing
painted below the group exists yet. By the time the composite is submitted the destination is the
colour attachment being written, which is not sampleable without an input attachment or a copy out
of the pass, and this renderer's command list has neither.

⚠ **The trap it sets is that `SoftwareUiRasterizer` could do it in three lines**, because its
recursion already holds the parent's buffer while it runs the group's. A backdrop filter written
there alone would look implemented and would surface as a *compositing divergence* in
`UiCompositingTests` rather than as a missing feature.

#### The shape of the change, sized

Re-measured, and the answer is not a slot in `UiLayer`. Four things have to move, and the third and
fourth are why this is a branch of its own rather than a rider on the drop shadow.

**1. Every group here is already a backdrop root, which is the one piece of luck.** Filter Effects 2
says an element forms a *backdrop root* if it has a filter, an opacity below one, a mask or a
clip-path — and a `UiLayer` exists in this engine for precisely those reasons and no other. So the
backdrop of a group nested in another group is **the parent's own surface content so far**, and never
an accumulation up the ancestor chain. There is no recursion to write.

**2. The capture is a re-render of the prefix, not a read-back.** For a group `g` with parent `p`, the
backdrop is what `Submit(self: p)` would draw from `p.First` up to `g.First`. `Submit` already walks
exactly that range and already skips nested groups in favour of their composites; it needs one extra
`stop` argument and the caller needs `p`, which is the nearest preceding layer whose range contains
`g`'s. A pass per backdrop group, confined to `g`'s bounds, into a surface of its own — then the same
blur and matrix machinery a drop shadow now uses, and a second quad drawn under the group's, which is
the arrangement `UiLayer.ShadowImage` demonstrates.

**3. `Compose`'s walk has to change order, and it is a working, subtle loop.** It runs *reverse
pre-order*, which puts a group's children before it and a group's **later** siblings before its
earlier ones. A capture needs the opposite of that second half: everything painted behind `g` must be
finished before `g`'s capture pass runs. **Post-order satisfies both** — each child subtree in
document order, then the parent — so the loop is replaceable rather than extendable, and every
barrier and surface-state argument in it has to be re-read against the new order.

**4. The host's content is not the compositor's, and this is the part that is a public API change.**
`Record` draws into a pass the host has already begun; the UI's own draw list is all `Compose` can
re-render. A capture built from it alone is *the interface behind the element and nothing else* — so
a glass panel over a 3D scene would blur nothing, which is the single commonest reason to reach for
the feature. It is worse than incomplete: the captured backdrop is then not opaque, and compositing a
blurred translucent copy **over** the sharp original is a double image along every edge, where CSS
*replaces* the backdrop within the element's bounds. Both problems close the same way and only that
way — the host hands over what it has already painted, as
`Compose(commands, geometry, surface, scale, TextureViewHandle? beneath)`, and the capture pass draws
it full-screen before re-rendering the prefix over it. That is a new public parameter, an entry in
`PublicAPI.Unshipped.txt`, and wiring in `Vixen.Editor.Host`, `Samples/02-HelloUi` and the app
template. A host that passes nothing gets the degraded reading and has to be told so.

⚠ **And one fidelity gap that has no cheap answer**: CSS clips the filtered backdrop to the element's
border box *including its radius*, and `UiLayer.Bounds` is a plain rectangle with no radius in it.
`rounded-2xl backdrop-blur-md bg-white/30` is the canonical use of this feature, and a rectangular
backdrop shows blurred corners outside the rounded ones. The mask machinery is the nearest existing
tool; a rounded-rect SDF in the composite fragment is the other.

**What is *not* needed**, and was assumed to be: an input attachment, a `vkCmdCopyImage`, or any
read-back of a colour attachment. The prefix is replayable, and replaying it is what turns this from a
capability the backend lacks into a scheduling problem the compositor can solve.

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

### What a group does not change

**Positions.** `UiDocument.Accumulate` is where the draw list, hit testing and arrow navigation agree
about where an element is. A composited subtree keeps its document coordinates — that is a direct
consequence of the viewport-sized surface above — so a click still lands on the element it looks like
it landed on. There is a test for it rather than only an argument, because the argument stops being
true the moment somebody shrinks a surface to its group to save memory.

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
