---
title: Compositing groups
slug: ui/compositing
kind: guide
area: Core
summary: How a translucent subtree is rendered into a surface of its own and blended back once — the offscreen pass behind `opacity` and `filter: blur()`, why a group is not the same as fading each element, when the pass is skipped as an exact identity, what the surfaces cost, and what a transform and gradient text would each still need on top of it.
api: [T:Vixen.Ui.Rendering.UiLayer]
tags: [ui, rendering, opacity, blur, filter, compositing, offscreen, filters]
since: 0.2
status: preview
related: [ui/gradients, ui/utility-composition]
---

## What it is

A **composited group** is a subtree of the interface that is drawn into a surface of its own and then
blended back into the frame as a single image. `UiLayer` is the record that describes one: which
draws belong to it, how big its surface has to be, and what it is faded by.

Two things open a group: an element whose `opacity` is below one, and an element with a
`filter: blur()`. The difference between them is worth stating up front, because it is why the
second one could not be approximated while the compositor was being built. An opacity *can* be
faded element by element — badly, but visibly — whereas a blur is a function of the rasterised
subtree, so with no surface there is nothing to convolve and the only honest answer is the
unblurred picture.

The pieces, top to bottom:

| Where | What it does |
|---|---|
| `DrawListBuilder` | Brackets a translucent element's subtree with `LayerPush` / `LayerPop` |
| `DrawBatcher` | Gives each bracket a `BatchKind.Layer` batch of its own, never merged |
| `UiGeometryBuilder` | Resolves the brackets into `UiGeometry.Layers`, outsets the bounds by a blur, and emits the compositing quad |
| `UiRenderer.Compose` | Renders each group's pass on the device, and sweeps a blur over the ones that ask |
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

⚠ **Only a lone `blur()` is read, and anything else in a `filter` refuses the whole declaration.**
`filter: brightness(.5) blur(4px)` draws unfiltered rather than blurred-at-the-wrong-exposure, which
is the rule `box-shadow` already keeps. The rest of `filter` and all of `backdrop-filter` are absent
roots in `docs/plan/43`; `backdrop-filter` in particular needs the frame *under* a group, which the
compositor does not keep.

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
