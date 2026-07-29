# Vixen.Ui.Renderer

The GPU half of the user interface: three pipelines, two buffers, one atlas texture, and the render
feature that draws a frame inside somebody else's renderer.

Everything above this is a pure function of a draw list. `UiGeometryBuilder` turns a frame's commands
into vertices without a device, which is why all of it can be tested without one. This is where that
stops.

## Why a separate assembly

`Vixen.Ui` describes a frame and knows nothing about a graphics API. `Vixen.Rendering` draws things
and knows nothing about a user interface. Putting the join in the first would drag a graphics API
into a UI framework meant to be usable without one — the same argument that keeps `Vixen.Ui` away
from `Vixen.Engine`, checked by `CheckArchitecture`. Putting it in the second would drag a UI
framework into every renderer.

## What is in it

| Type | What it does |
|---|---|
| `UiShaders` | The four modules a frame is drawn with, supplied rather than compiled |
| `UiRenderer` | Pipelines, buffers, the atlas texture, and recording a frame |
| `UiRenderFeature` | A `RootRenderFeature` so a `RenderSystem` can reach one |
| `UiSurface` | One interface as the renderer sees it: geometry, atlas, size, order |

### Three pipelines, one vertex layout

They differ only in the fragment stage, so a frame binds a different pipeline per batch kind and
never a different buffer — one upload however many kinds of thing it draws.

- **Box** — a rounded rectangle or a border, as a signed distance evaluated per pixel. Exact at any
  radius, four vertices whatever the shape, and the border is the *difference of two coverages* so
  the two share one antialiased outer edge and cannot disagree about where it is. Four **elliptical**
  corners, each with its own pair of radii, and a two-stop linear gradient.

  ⚠ Those parameters live in a **storage buffer, one record per box**, and the vertex carries the
  index. Fourteen floats on the vertex would take it from forty-eight bytes to a hundred and four,
  and every glyph in the frame would carry fields no shader reads on them; per box it is eighty bytes
  against the sixty-four its four vertices already spend, and the vertex layout does not move at all.

  ⚠ The exact distance to an ellipse has no closed form. The corner quadrant is scaled into a circle
  and the distance scaled back by the *smaller* semi-axis — exact on the axes, within a fraction of a
  pixel between them, which is all a one-pixel antialiasing band can tell apart. Scaling back by the
  larger axis leaves the edge soft on the flat side of a wide corner.
- **Text** — a multi-channel distance field, reconstructed by the median of three channels. That is
  the whole trick and it is why the atlas must never be sampled as sRGB: the values are distances,
  not light.
- **Solid** — tessellated paths. ⚠ The one primitive with no distance function, so the one whose edge
  has to be *drawn*: the tessellator emits the interior at full coverage and a half-pixel strip along
  the outline ramping to zero, and this shader multiplies alpha by it. The coverage rides in
  `shape.x`, where the text shader keeps its pixel range. Multisampling the pass is the other answer
  and remains the compositor's — `UiGeometryBuilder.Fringe = 0` turns this one off, because two
  antialiasing schemes over one edge make a seam rather than a smoother line.

### One pipeline layout, deliberately

All three pipelines share one layout, **including the two whose shaders never sample the atlas**. The
obvious arrangement is a layout each, and it is the one that has to be got right per draw: Vulkan
disturbs every descriptor set from the first one two layouts disagree about, so a box drawn between
two runs of text unbinds the atlas and the second run reads whatever is left.

That is undefined behaviour rather than an error. This machine's driver keeps the binding and the
validation layers do not object, so **no golden image here can see the difference** — which is how it
was found: a sabotage that deleted the re-bind changed nothing, twice, through two rewrites of the
fixture designed to catch it. Making the layouts identical makes the question not arise. Declaring a
binding a shader ignores costs nothing.

### Host-visible buffers, rewritten every frame

No staging copy. The usual advice is the opposite and it is about data the GPU reads many times;
interface geometry is read once, by one draw, and thrown away. A staging copy would add a transfer
and a barrier to save nothing.

The atlas is the exception — it is a texture, it persists, and it is uploaded only when its
**revision** changes. `AtlasUploads` counts them, because "a frame drawing text it has drawn before
uploads nothing" is a claim nobody can check otherwise.

⚠ **The revision and not the version, and the two are not interchangeable.** A version moves when the
packing changes, which only compaction does; a glyph merely added leaves every existing region where
it was, so it moves the revision and not the version. Gating the upload on the version uploads on the
first frame and never again — and every glyph the interface meets after that samples whatever its
region held in the GPU's copy before it was allocated. That is text with characters missing out of
the middle of words, and another glyph's field in their place wherever the atlas reused a slot; it
appears as the user opens menus and expands trees, which is why it reads as a fault in the controls
rather than in the upload.

### Where the shaders come from

`UiShaders` is handed over, not built. Compiling shader source belongs to `Vixen.Shaders` and, once
it lands, to Raven — which already carries `Raven/Library/Ui/Msdf.rvn` and `RoundedRect.rvn` for
exactly this. Until then a caller supplies whatever it has: the golden fixture uses hand-written GLSL
compiled by `glslc` and committed as SPIR-V, and a game will supply an effect. What this assembly
must not grow is a compiler.

⚠ The Raven shaders take the box's size and radii as **uniforms**, so one draw is one box. The
pipelines here take them **per vertex**, so one draw is a whole batch. That is a real divergence and
the reason for it is that a user interface draws hundreds of boxes with different sizes per frame; it
has to be reconciled when Raven takes over, and the vertex layout is the thing that stays.

## One render object per surface

⚠ **This corrects a guess.** `DrawBatch`'s remarks reasoned that because `RenderSortMode.ByGroup`
exists "for UI and anything else already ordered", the batch index would have to be the sort group.
It is not, and it cannot be: the store's objects live across frames and are indexed by a dense id
every feature's parallel array is keyed on, so an object per batch would churn the whole store every
time a label changed. The painting order *within* a surface is already the order of
`UiGeometry.Draws`, and no sort can reach it.

What the group orders is surfaces against each other — a modal over a document, a tooltip over the
modal. That is a real ordering problem and a sort is the right answer to it, so the stage a
`UiRenderFeature` is drawn in still has to sort `ByGroup`: every other mode puts depth in the key,
and an interface has none.

The batch list was not a wasted guess and is not the thing to delete. It is what `UiGeometryBuilder`
turns into one `UiDraw` each, behind the frame diff, so a still interface regroups nothing.

## Gates

`Platform/Vixen.Graphics.Golden.Tests` — `ui-interface` and `ui-clipped`. A picture is the only thing
that can see whether the shaders agree with the geometry: a distance read with the wrong sign, a
projection that flips y, an atlas sampled at the wrong scale, a border drawn as a fill. Every one of
those passes every unit test in `Vixen.Ui`.

⚠ **The first version of this was drawn upside down**, and the comment above the projection argued at
length that it should not be — Vulkan's clip space does have +y down, but nothing here ever sees it,
because `VulkanCommandList.SetViewport` submits a negative-height viewport so the engine's +y-up
convention holds everywhere. The clip fixture did not notice, because its box was symmetric about the
scissor's edge. Both are now written down where they happened.
