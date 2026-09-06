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
| `UiShaders` | The modules a frame is drawn with, supplied rather than compiled — four required, `Image`, `Blur`, `Colour` and `Mask` optional |
| `UiRenderer` | Pipelines, buffers, the atlas texture, and recording a frame |
| `UiRenderFeature` | A `RootRenderFeature` so a `RenderSystem` can reach one |
| `UiInterface` | One interface as the renderer sees it: geometry, atlas, size, order |

⚠ The last row said `UiSurface`, which is a different type in a different assembly — see
`UiInterface`'s own remarks for why the name moved and why the two being confusable is a trap rather
than a nuisance.

### Who registers the feature

`WorldRenderer` does, in its constructor, as `WorldRenderer.Ui` — and the editor's viewport gets it
from there, because `EditorWorldRenderer` owns a `WorldRenderer` rather than assembling features of
its own. A host then supplies four things in order: `Renderer`, because building a `UiRenderer`
needs the shader modules and the formats of the pass; `Mount`, once, with the stages that draw it;
`Set`, every frame, with the geometry the document's builder produced; and `Upload`, every frame,
on a command list that is **not inside a render pass**.

⚠ **`Upload` is not optional and forgetting it draws from memory nothing has written.** `Draw` runs
inside the pass and can only `Record`; the vertices, indices, box records and the glyph atlas are
written by `UiRenderer.Upload`, and a texture copy is the one thing a Vulkan command list may not do
inside a pass — which is why the renderer splits the two at all. The feature had no upload half for
as long as it had no registration, and the evidence was in plain sight: every `UiInterface` carried
the `Atlas` its text draws from and not one line read the field.

⚠ **One `Renderer` serves one mounted interface, whatever `Mount` allows.** `UiRenderer` advances its
ring region inside `Upload` and `Record` draws from the region the *last* upload wrote, so two
surfaces uploaded through one renderer are both drawn from the second one's geometry. The `Order` a
surface is mounted with is therefore ahead of the arrangement that would need it — that wants a
renderer per surface, which is [#909](https://github.com/Rikarin/Vixen/issues/909).

⚠ **Until this existed the feature had no caller at all.** It compiled, it was documented, three
other files' prose referred to it — and nothing in the tree constructed one, so whether the
composition it was written for worked had never been observed. What made that survivable is that the
interface still rendered: `Vixen.Ui.Desktop` paints a document through `UiRenderer` directly, which
is the path the editor's chrome and every UI-only application take. What had no caller was drawing an
interface *inside a scene's frame*.

⚠ **`Mount` is the half that was missing, not `Set`.** `Set` takes a `RenderObjectId` and no caller
could obtain one: the object has to be added to the store carrying this feature's index, and a host
that guessed at the index writes a record some other feature is handed and quietly skips. `Mount`
also decides the bounds, which are `float.MaxValue` and not a mistake — an interface is in screen
space and has no place in the world, so anything finite there is a HUD that appears and disappears as
the player turns around.

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

`UiShaders` is handed over, not built. Compiling shader source belongs to `Vixen.Shaders` and to
Raven; a caller supplies whatever it has. The golden fixture uses hand-written GLSL compiled by
`glslc` and committed as SPIR-V; `Vixen.Ui.Desktop.UiShaderLibrary` supplies Raven's output from
`Shaders/Ui.rvn`, which is what an application gets; `Vixen.Editor.Host` builds its own table from a
copy of that shader; and a game will supply an effect. What this assembly must not grow is a compiler.

⚠ **`Raven/Library/Ui/Msdf.rvn` and `RoundedRect.rvn` are not these shaders**, and the difference is
not a porting gap. They take the box's size and radii as **uniforms**, so one draw is one box; these
pipelines take them **per vertex** and out of a shape buffer, so one draw is a whole batch. A user
interface draws hundreds of boxes of different sizes per frame, so the batched form is the one that
stays — which is what `Ui.rvn` is, and what a library shader would have to become before it could be
used here.

⚠ **Which is the answer to "reconcile the two", and the answer is that they are not reconcilable into
one file.** The blocker that row was filed under — Raven taking the UI shaders over — was discharged
on 2026-08-23, and reconciliation did not follow from it, because the two parameter conventions are
two different jobs rather than two spellings of one. Porting `Raven/Library/Ui` to the batched form
would produce a second `Ui.rvn` in a package nothing binds; deleting it would take the reference
implementations of the distance-field, MSDF and gradient techniques with it. What the five library
files owed was a **notice**, and four of them carried none — a reader who opened `Msdf.rvn` to fix a
glyph had nothing telling them it was the wrong file. They all carry one now.

### The four optional stages, and what their absence looks like

`Image`, `Blur`, `Colour` and `Mask` are `init` properties rather than positional parameters, so a
host that hands over none of them keeps the four-argument constructor. Their absences are not equally serious
and it is worth knowing which is which.

**No `Image` is the bad one.** It is the stage `Compose` composites a group's surface back with, so
without it `Compose` returns having done nothing at all — and a frame whose groups were left
uncomposited does not draw the old approximation, it draws the group's contents *in place at full
strength*. A disabled control comes out opaque. See `composed`.

**No `Blur` is the mild one.** Groups still composite, at the right opacity; a `filter: blur()` in a
stylesheet simply draws sharp. `Blurred` is what says so, and it is the only thing that can: the
picture of a blur that did not happen is a correct picture of something else.

**No `Colour` is milder still to run and harder to notice.** It is the composite of a group whose
`filter` carries one of the seven colour functions — `grayscale`, `brightness`, `contrast`,
`invert`, `saturate`, `sepia`, `hue-rotate` — composed by `DrawListBuilder` into one `UiColorMatrix`
and applied in the composite's fragment stage. Without it the group composites through `Image`, in
full colour. ⚠ `Filtered` is what says so, and it is worth more than `Blurred` is: a blur that did
not happen at least draws a *different* picture, whereas a matrix that did not happen draws the
right one wherever the group's colours sit near its fixed points — so no screenshot and no
comparison of the two executors can see it.

⚠ **It costs no pass and no surface**, which is the whole reason it is folded into the composite
rather than run as a filter pass of its own. A blurred group needs a scratch target and two more
passes because a convolution cannot read and write one attachment; a per-pixel matrix has nothing to
read from a neighbour. What it costs is one pipeline switch and forty-eight bytes of push constant
on a draw that was happening anyway.

**No `Mask` is the same story with one extra trap.** It is the composite of a group whose
`mask-image` list fades it out, and without it the group composites through `Image` unmasked. `Masked` is
what says so. ⚠ The trap is that this stage carries the colour matrix *as well*: a pipeline is chosen
once per draw, so a group with both a `filter` and a `mask-image` has to be served by one module that
does both. A host that supplies `Mask` and not `Colour` therefore gets `grayscale` working on masked
elements and nowhere else, which is a stranger picture than either stage being absent — supply both
or neither.

⚠ **It costs no pass and no surface either**, on the colour matrix's terms. What it costs is a
pipeline switch, forty-eight bytes of push constant for the matrix, and sixteen more naming a range
of the mask storage buffer.

⚠ **The mask entries are in a storage buffer rather than in push constants, and that is a ceiling
reached rather than a design preference.** `mask-image` is a list — twelve of Tailwind's mask roots
are per-edge ramps that only mean anything combined — and one entry was already sixty-four bytes when
the decision was made. With the matrix's forty-eight that came to a hundred and twelve, which set the
pipeline layout's fragment range, and 16 + 112 is exactly the 128 Vulkan guarantees on every device.
A second entry could not have fitted, never mind eight. So the entries ride binding 2 of the shared
descriptor set — the one `UiShape` already uses — and the draw pushes an index and a count.

⚠ **An entry is eighty bytes now**, since `mask-size`, `mask-position` and `mask-repeat` added a
fifth `vec4`, so the push-constant arrangement it was compared against would no longer fit even for
one. The ceiling was reached earlier than the numbers above suggest, and the storage buffer is the
reason growing the entry was a lane and a `MaskFloats` constant rather than a redesign.

⚠ **That buffer has a fixed capacity and is allocated once, which is what keeps every image
descriptor set valid.** A composite draw binds an *image* set, so that set's binding 2 is what
`ui-mask.frag` reads its list through; a buffer that grew would be a new handle and every set in the
ring would be pointing at freed memory on the frame it grew. It is sized for `MaskCapacity` entries
per frame in flight and never replaced. A frame asking for more composites the groups past the line
unmasked, which is the fail-open answer `DrawListBuilder` already gives an unreadable mask.

⚠ **`Vixen.Editor.Host` supplies `Image` and neither of the other two**, so the editor composites,
does not blur, does not filter and does not mask.

⚠ **The obstacle this paragraph used to name is gone, and the missing stages are now a wiring gap
rather than a shader one.** It said push-constant layout: a blur's kernel and a colour matrix ride in
a fragment-stage range at byte 16, past the vertex stage's projection, and Raven's `[PushConstant]`
lays a shader's constants out from zero with no way to say otherwise. That is still true of Raven and
is no longer an obstacle — `Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn` declares a leading
`reserved: float4` in each of `UiBlur`, `UiColour` and `UiMask`, which *is* the projection's sixteen
bytes, and all three work. `UiShaderLibrary.Load` hands over the complete set of eight.

⚠ **And the wiring gap is closed too: there is one `Ui.rvn` again.** `Vixen.Editor.Host` kept its
own copy — 558 lines against the desktop copy's 1006, identical in every line of the five shaders both
carried and simply missing the other three — and hand-rolled a five-module table beside a
`UiShaderLibrary` it already referenced. The copy is deleted and `EditorHost` calls
`UiShaderLibrary.Load`, so the editor blurs, filters and masks like every other host.

⚠ **What that cost while it stood is worth stating, because none of it was a failure.** All three
stages degrade to a picture rather than to an error, so the editor drew a correct picture of the wrong
stylesheet for as long as the copy existed: no validation error, no log line, no counter out of range.
`EditorUiCompositingDeviceTests` is what says so now — it renders one blurred, one filtered and one
masked group through `UiShaderLibrary.Load`'s table and through that table with the three optional
stages cleared, which reconstructs the old editor exactly, and asserts the opposite of each oracle on
the second arm.

### And where the numbers in a vertex layout come from

`Locations` is the second thing a caller may hand over, and it exists because an attribute's location
is a property of the *shader*: Raven locates a stage's own parameters after the shader's streams, so
the editor's four attributes are at 3 to 6 where `glslc` output has them at 0 to 3.

⚠ Getting it wrong is not a validation error. The pipeline binds nothing to that attribute and the
stage reads whatever the driver left there. So it is passed rather than assumed, and what passes it is
`Vixen.Shaders.Generators` reading the shader's own reflection — a number nobody wrote down cannot be
out of date.

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
