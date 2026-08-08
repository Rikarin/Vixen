---
title: Compositing groups
slug: ui/compositing
kind: guide
area: Core
summary: How a translucent subtree is rendered into a surface of its own and blended back once — the offscreen pass behind `opacity`, why a group is not the same as fading each element, when the pass is skipped as an exact identity, and what a filter, a transform and gradient text would each still need on top of it.
api: [T:Vixen.Ui.Rendering.UiLayer]
tags: [ui, rendering, opacity, compositing, offscreen, filters]
since: 0.2
status: preview
related: [ui/gradients, ui/utility-composition]
---

## What it is

A **composited group** is a subtree of the interface that is drawn into a surface of its own and then
blended back into the frame as a single image. `UiLayer` is the record that describes one: which
draws belong to it, how big its surface has to be, and what it is faded by.

Today exactly one thing opens a group: an element whose `opacity` is below one. The machinery is
deliberately larger than that one use, because three other features need the same surface and
[none of them can be built without it](#see-also).

The pieces, top to bottom:

| Where | What it does |
|---|---|
| `DrawListBuilder` | Brackets a translucent element's subtree with `LayerPush` / `LayerPop` |
| `DrawBatcher` | Gives each bracket a `BatchKind.Layer` batch of its own, never merged |
| `UiGeometryBuilder` | Resolves the brackets into `UiGeometry.Layers` and emits the compositing quad |
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
- **The three features this pass was built for, none of which is implemented yet.**
  `filter: blur()` needs a *second* pass over a finished layer, with a separable kernel and a bounds
  expansion by the blur radius — the existing blur is the shadow path, which blurs a solid rather
  than rendered content. `bg-clip-text` needs the layer to be used as a *mask* rather than as
  colour, which means glyphs rendered to coverage instead of drawn directly. `scale-*` and
  `rotate-*` remain refused: a scaled subtree needs re-shaping because glyph advances are shaped at
  layout time, and a rotated clip cannot be the axis-aligned rectangle the clip stack requires — a
  surface makes the *raster* transformable but changes neither of those two facts.
