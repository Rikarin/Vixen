# Vixen.Editor.TextureGraph

Images computed on the GPU from a plan of compute kernels.

This is the evaluator half of [doc 48](../../docs/plan/48-material-authoring.md) — § D1's split, copied
exactly from [`Vixen.Editor.ShaderGraph`](../Vixen.Editor.ShaderGraph/README.md): **an assembly that
holds a graphics device and knows nothing about a project, a document or a panel.** A `TexturePlan` is
built by hand in a test, by a graph compiler in M4, or by a layer stack in M7, and one evaluator runs
all three.

```csharp
var plan = new TexturePlan {
    BaseWidth = 2048,
    BaseHeight = 2048,
    Seed = 41823,
    Images = [
        new(TextureFormat.Rgba8, External: true),   // 0 — the bitmap the caller supplies
        new(TextureFormat.Rgba16Float),             // 1 — blurred along x
        new(TextureFormat.Rgba16Float),             // 2 — and along y
        new(TextureFormat.Rgba8)                    // 3 — the output
    ],
    Ops = [
        new() { Kernel = "Blur", Output = 1, Inputs = [0], Parameters = [
            new("radius", 8f, TextureParameterUnit.TexelsAtBase), new("stepX", 1f), new("stepY", 0f)
        ] },
        new() { Kernel = "Blur", Output = 2, Inputs = [1], Parameters = [
            new("radius", 8f, TextureParameterUnit.TexelsAtBase), new("stepX", 0f), new("stepY", 1f)
        ] },
        new() { Kernel = "Levels", Output = 3, Inputs = [2], Parameters = [
            new("inputBlack", 0.1f), new("inputWhite", 0.9f), new("gamma", 0.8f),
            new("outputBlack", 0f), new("outputWhite", 1f), new("dither", 1f)
        ] }
    ],
    Outputs = [3]
};

using var evaluator = new TexturePlanEvaluator(device);
using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

bake.Save(3, "Assets/Materials/hull-height.png");
```

## What this deliberately does not do

- **No node classes, no compiler, no `.vxtexgraph`.** A plan is the artefact; how one is produced is
  M4's (a graph) and M7's (a layer stack). This assembly does not reference
  `Vixen.Editor.NodeGraph` at all, which is what makes every test here a test of the evaluator.
- **No CPU implementation of any kernel** — [§ D3](../../docs/plan/48-material-authoring.md). A parity
  test against a C# re-implementation proves the two transcriptions agree, not that either is right,
  and this repository has already fallen into that trap once. What the device tests assert are
  **closed forms**: a box filter's impulse response is `1/(2r+1)` over exactly `2r+1` texels, a levels
  curve maps three known inputs to three known outputs. `TexturePixels` converts half-floats to bytes
  on the way to a file and is an encoder, not a twin — nothing in a graph does it.
- **No UI, no document, no project.** There is no panel here and none is needed to check any of it.
- **No frame.** `Evaluate` opens its own, submits one command list and waits. A bake is a modal
  operation; the interactive per-node preview of § M4 will want the recording half split out rather
  than this called sixty times a second.
- **No mip chains, no block compression, no `.vxmat`.** A bake's last mile is § M5's, through
  `Vixen.Core.Imaging`, which already has all three.

## The plan, and why it is flat

`TexturePlan` is a table of images and an ordered list of `TextureOp`s over it. Two properties make
everything else cheap:

**An image is written exactly once** — `Validate` refuses a plan where it is not — so an image is live
from the op that writes it until the last op that reads it, and `TexturePoolSchedule` needs no analysis
of its own. Liveness is the op order, and the plan already fixes it.

**An op has no resolution of its own.** Its resolution is the resolution of the image it writes, so two
ops cannot disagree about how big one image is. § M1 lists the resolution as a field of the op;
carrying it twice would be a second place for it to be wrong.

⚠ **`Validate` refuses rather than copes, and every refusal is a picture somebody would otherwise have
had to explain.** An op reading the image it writes is a dispatch reading whichever half of itself has
already run; an op reading an image nothing has written is whatever the allocator left; an index
outside the table is a `KeyNotFoundException` three frames away.

## The pool

Allocate on first write, free when the last reader has run, reuse a freed slot — and take the output
**before** giving the inputs back, because an op whose input dies on the same dispatch would otherwise
be handed its own input's texture.

The number this exists to bound is the count of textures created, and it is asserted with **no device**:
a chain of forty ops threaded through two live images allocates two textures and not forty. At 2K that
is 32 MB against 640 MB — and the version that allocates forty works perfectly on the six-op plan a
spike would have used.

A slot is reused only by an image of the same format *and* the same size. Aliasing across shapes is
what a transient allocator does with a memory heap; this is a list of textures, and a texture is not
reinterpretable.

## Resolution, and the bug with a two-year fuse

[§ D8](../../docs/plan/48-material-authoring.md). The plan declares the resolution the graph was
**authored** at (`BaseWidth` / `BaseHeight`); every image is a power of two away from it
(`LevelOffset`); and **every radius, width and length is in texels at that authoring resolution**
(`TextureParameterUnit.TexelsAtBase`), scaled by the evaluator to the image the op writes.

⚠ **A radius stored as absolute texels looks right at the resolution it was tuned at and is half as
wide at 4K**, so a graph authored at 1K and shipped at 4K is a different material and nobody associates
the change with the resolution field. Storing it as a fraction of the image has the mirror-image failure
at a non-square resolution.

### Authoring resolution and bake resolution are two numbers

`BakeLevelOffset` is how big the whole graph is being made *this time*, in the same currency and with
the same sign as an image's own level: `0` bakes at the authoring resolution, `-2` bakes a 1K graph at
4K, `1` bakes a 512 preview. `TexturePlan.BakeLevelFor(authored, baked)` reads one off a pair of
resolutions and refuses a ratio that is not a power of two. `SizeOf` adds the two levels; `ScaleOf`
therefore reports **4** for a level-0 image in a bake two levels up, and `Resolve` turns 8 texels-at-base
into 32 — the same physical width, which is what makes the two bakes one material.

⚠ **One number rather than a bake width and a bake height**, deliberately: two would let a caller ask
for 4096×2048 out of a 1024² graph, and then a radius would be either four times wider in x and twice
in y — a filter that is no longer round — or wrong in one axis.

⚠ **This field did not exist until [#619](https://github.com/Rikarin/Vixen/issues/619), and this
section used to claim `TexturePlanDeviceTests` proved the scaling.** It did not, and could not: moving
`BaseWidth` moves the unit a radius is counted in by exactly as much, so `Resolve` returned 8 at a base
of 1024 and 8 at a base of 4096, and the test that was meant to catch this asserted that the two agreed
— the opposite of what its own name said. What that device test does prove, and still does, is the mip
difference *within* one plan: two plans writing 64-texel images, one at base 64 / level 0 and one at
base 128 / level 1, produce a 17-texel bar and a 9-texel bar.

§ D8's own criterion — **bake at 1K and at 4K, downsample the second, require agreement** — is
`TexturePlanDeviceTests.The_same_plan_baked_at_four_times_the_resolution_agrees_with_the_smaller_bake`:
one plan, baked at `BakeLevelOffset` 0 and −2 over a step edge sampled at both sizes, the 256² result
box-downsampled 4:1, worst column agreeing to 4/255 against a tolerance of 8. An unscaled radius parts
them by 92/255. That test is what protects every kernel added to `Shaders/` afterwards.

## One queue, and why not a barrier pair

Every command list a bake records — the dispatches and every later read-back — is a compute list
submitted to `IGraphicsDevice.ComputeQueue`. `TextureBake` is handed the submitter and takes both its
command-list kind and its submission from it, so the two halves cannot name different queues.

⚠ **[#617](https://github.com/Rikarin/Vixen/issues/617): they used to.** The dispatches went to the
compute queue and the read-back's `CopyTextureToBuffer` to the graphics queue, on textures created
`ResourceSharing.Exclusive`, with no queue-family ownership transfer anywhere — **undefined by
specification** on any adapter whose `QueueFamilySelection` found a compute family of its own. The
validation layers say nothing, because it is undefined behaviour and not invalid usage, and
`Platform/Vixen.Graphics.Vulkan/VulkanBarriers.cs` records in as many words that a separate compute
family is no device this engine has been developed on. So it was a clean bake on every machine here and
undefined texels on a discrete card.

**One queue rather than the transfer pair, and a future reader should not "optimise" it back.** An
ownership transfer is two barriers with identical parameters plus a semaphore edge; the release half
would have to be recorded at the end of the bake's own list, for every image, before anybody knows
which will ever be read — and a texture released to a queue that never acquires it is exactly the
corruption the pair exists to prevent. There is also nothing to buy: a bake is modal, `Evaluate` waits
for the device before it returns, and a read-back has no frame to overlap. `Vixen.Raven.Gpu.Tests`'
`ShaderRun` dispatches and copies on one compute list for the same reason.

`TextureQueueTests` is what says so, and it runs on the **Null** device — the only backend in this tree
whose three submitters are three objects. It asserts a queue and never a pixel.

## Formats, and the two that turned out to be read-only

`R8` · `Rg8` · `Rgba8` · `R16Float` · `Rgba16Float`. **32-bit float is deliberately not one of them** —
a material map that needs it has a mistake upstream, and an intermediate at 4K is 16 MB as
`Rgba16Float` against 32 MB as four 32-bit floats.

⚠ **`R8` and `Rg8` can be read and cannot be written, which refutes § M1's and
[#566](https://github.com/Rikarin/Vixen/issues/566)'s format list.** Both name the five as though a
kernel could write any of them. `Raven/Vixen.Raven/Symbols/ImageFormats.cs` admits sixteen storage-image
formats and neither `r8` nor `rg8` is among them — and that table is right, because Vulkan's list of
formats an implementation *must* support for `STORAGE_IMAGE` contains neither. So a kernel writing one
would fail at pipeline creation, on a conformant device, with a driver message about a format nobody
chose by hand. `TexturePlan.Validate` refuses it where the plan is built instead. Reading one is fine:
an imported bitmap is sampled, and `Load` hands back `(r, 0, 0, 1)` whatever the storage was.

## The kernels are embedded, and no `.spv` is committed

See [`Shaders/README.md`](Shaders/README.md). The short version: a storage image's format is part of its
*type* in both targets and Raven's `[Permutation]` values are bool, int and uint, so one kernel cannot
write two formats. Variants are rewritten out of the embedded source at load — which means there is no
committed binary anybody can leave stale, and what replaces `CheckShaders`' editor half is a test that
compiles every kernel in every variant with **no device**, and therefore never skips.

## Device tests name their adapter

⚠ Without `--vixen-offscreen` a headless run falls back to the Null device on every platform, exits 0
and prints character-for-character identical healthy counters. **A texture-graph test that passed on the
Null device would have proved that a black image equals a black image.** Every device test here opens
through one helper that names the adapter into every failure message and skips loudly when there is
none; `VIXEN_REQUIRE_VULKAN=1` turns the skip into a failure.

⚠ **`TextureQueueTests` is the one exception and it is deliberate.** It opens a Null device on purpose,
because a unified adapter cannot tell the compute queue from the graphics one and that is the whole
question it asks. It never reads a texel.

## The colour, channel and space kernels — doc 48 § 4.2 and § 4.3

Thirteen `.rvn` files — fifteen in all, counting § M1's `Levels` and `Blend` — and
`TextureKernels.Colour.cs`, which is where the integer contracts they read live: `Curve` ·
`GradientMap` · `Hsl` · `Grayscale` · `Invert` · `ChannelShuffle` · `MinMaxReduce` + `AutoLevels` ·
`Transform2D` · `Mirror` · `Tile` · `Crop` · `Resample`.

**A curve and a gradient reach the GPU as a baked table, not as a spline or a stop list.**
`Core/Vixen.Core/Curves/CurveEvaluation.cs` is the one Hermite evaluator in this repository and
`Vixen.Ui.Controls.Advanced`'s `Gradient` is the one thing that decides which of three spaces a ramp
is mixed in. `TextureRamp` samples them into a 256×1 row that a kernel interpolates. ⚠ **That is the
opposite of § D3's ban rather than a dodge of it**: the ban is on a second *transcription* of an
operation, and this arrangement guarantees there is only ever one. `Gradient.Evaluate` is passed as a
delegate, which is also what keeps this assembly from referencing a UI control.

**⚠ Not one parameter of the thirteen is a length in texels, so § D8's scaling never touches them.**
A rotation is in turns, a scale is a ratio, an offset is a fraction of the image, a rect is
normalised and a repeat is a count — they are resolution-independent *by construction* rather than by
`TexturePlan.Resolve`'s arithmetic, and [#619](https://github.com/Rikarin/Vixen/issues/619)'s rework
of the base resolution cannot change what any of them does.
`TextureColourKernelTests.No_kernel_here_takes_a_length_in_texels` is what keeps that true, and
`TextureSpaceDeviceTests` asserts § D8's own criterion — a 64² bake and a downsampled 256² one, which
agree to 1/255 on this machine.

**⚠ Minification is supersampled by hand, in `Transform2D`, `Tile` and `Resample`, because the
evaluator binds no samplers.** `TexturePlanEvaluator.Bind` handles a uniform block, sampled textures
and one storage image and throws on anything else, so a `DescriptorKind.Sampler` is not available —
which means no hardware mip chain and no anisotropic tap. Each of those three derives the footprint
of an output texel and boxes over it, which is the mip level a sampler would have chosen. The closed
form is a one-texel column checkerboard: its mean is exactly one half, so a correct minification of
it is 128 everywhere and a point-sampled one is 0 or 255 everywhere.

**⚠ `Auto Levels` is more than the two dispatches § 4.2 names, and nothing in the plan records what
makes it different.** It is the first op whose output depends on *every texel of its input*, so it is
one `MinMaxReduce` dispatch per level down to a 1×1 image and then the map — three at 64², five at
4K. That much a plan expresses perfectly well. What a plan cannot say is that the op **can never be
evaluated in tiles**: `TextureOp` has no such field, so a future tiled evaluator would run it per
tile and produce a plausible picture with a different stretch in each one.

**⚠ `Crop` is the one node whose output resolution is not its input's, and `TextureImage` cannot
express most of the answers.** The rect is in the source's normalised space and the target's size is
the plan's, so a 1:1 crop is available exactly where the rect is a power of two — because
`LevelOffset` is the only way to size an image. A crop to 37% of the width has nothing to write into.
See #619, which is reworking that model.

**⚠ A kernel here cannot `import` the Raven library.** `TexturePlanEvaluator` compiles through
`RavenEffectCompiler.FromSources([…])` with no `referencePaths`, so a kernel binds against nothing but
itself. `Hsl`'s hue rotation is therefore `Raven/Library/Material/ComputeColor.rvn:78`'s, transcribed
— and the two agreeing matters, because an artist who matches a hue in the shader graph and sees it
shift here has found a bug.

## The filters — doc 48 § 4.4

Eleven `.rvn` files and `TextureKernels.Filters.cs`: `Blur` · `BlurHq` · `DirectionalBlur` ·
`RadialBlur` · `NonUniformBlur` · `Sharpen` · `Emboss` · `Warp` · `DirectionalWarp` · `VectorWarp` ·
`SlopeBlur`.

**⚠ This is the group § D8 is actually about, because it is the only one where every node takes a
length.** Not one of § 4.2's or § 4.3's thirteen does — a rotation is in turns, a repeat is a count —
so `TexturePlan.Resolve`'s scaling passes straight through them and does its work here. That also
means this is where a resolved length can outgrow a kernel's own budget: a radius authored as 20 at 1K
arrives as 80 at a 4× bake, and a kernel that clamped it would make the 4× bake a *narrower filter*
than the 1× one with no message anywhere ([#678](https://github.com/Rikarin/Vixen/issues/678)).
`Blur`'s answer is that the budget bounds the **taps** and never the width — past it the same width is
covered by the same number of taps spaced further apart, so the box thins rather than narrowing. ⚠ A
refusal on the plan would be better and there is still no `TexturePlan.Validate` check that a resolved
length fits the kernel receiving it ([#692](https://github.com/Rikarin/Vixen/issues/692)).

**⚠ Two of the eleven carry a *convention* rather than a number, and a convention is what a picture
cannot show you.** `VectorWarp` reads a signed displacement out of an unorm map — `(rg · 2 − 1) ·
intensity`, so 128 is rest — and the one-sided reading, that the channels are already the
displacement, produces a picture that drifts one way at half the amplitude, which an artist corrects
by doubling the intensity and never reports. `SlopeBlur` is **iterative**: it re-reads the gradient at
each of `samples` successive steps, and the single-pass approximation everybody writes instead is
indistinguishable from it on a blob and wrong on every edge — which is the only place a slope blur is
ever used. Both are asserted as *pairs*, because either half alone is satisfied by the wrong
implementation.

**⚠ The harness's `Unique` pattern cannot measure any of them.** Its four channels are affine in x and
y, and the mean of an affine field over a window symmetric about a texel is its value at that texel —
so it is a **fixed point of every symmetric averaging filter at every strength**, and two tests written
over it passed with the parameter they were about hard-coded
([#694](https://github.com/Rikarin/Vixen/issues/694)). `TextureHarnessPatternTests` is that property
written down as a test rather than as a remark, and `TextureKernelHarness.AssertHeldStill` is the
guard: it refuses to believe "this texel is untouched" until the same op has visibly moved something.

## Analysis — doc 48 § 4.5

Six `.rvn` files for three nodes, plus `TextureKernels.Analysis.cs`: `JumpFlood` → `Distance`,
`FloodBounds` + `FloodResidual` → `FloodFill`, and `EdgeDetect`.

**The chain writes a record and the node writes a picture, which is why each is two kernels.** A jump
flood settles a nearest-seed record over log₂(n) ping-ponged dispatches; `Distance` is one load and a
length over the settled record, and its three modes read different fields of the same record rather
than needing different floods. The same split is `AutoLevels` over `MinMaxReduce`, for the same
reason: folding them would make the last dispatch of a chain a different kernel from the ones before
it.

**⚠ The iteration ceiling reports truncation rather than being a `while` on the device.** A label
propagation's cost depends on the *shape* of its input rather than its size, so the count cannot be
derived from the resolution — and a loop no invocation leaves is a device loss, not a slow bake.
`FloodResidual` is what says the fixed point was not reached.

**⚠ Both chains are refused above 2048 texels, and the reason is a format doc 48 ruled out.** They
carry a *coordinate*, a half-float is exact on the integers only to 2048, and § D5 admits no 32-bit
float format — so at 4K a seed coordinate would quantise and the flood would settle onto the wrong
texel. `TextureAnalysis.ExactExtent` refuses rather than quantising
([#690](https://github.com/Rikarin/Vixen/issues/690)). ⚠ A plan holding one of these chains is also
built for **one** `BakeLevelOffset` — the dispatch count is in the op list — so it cannot be re-baked
at another size ([#689](https://github.com/Rikarin/Vixen/issues/689)), which is the one place § D8's
promise does not hold and the plan does not say so.

## Surface — doc 48 § 4.6

Five `.rvn` files for six nodes, plus `TextureKernels.Surface.cs`: `HeightToNormal` · `NormalCombine`
· `NormalTransform` · `Curvature` · `AmbientOcclusion`.

**⚠ `Normal → Height` is the sixth and is deliberately not here.** Doc 48 § 4.6 makes it the
catalogue's one CPU exception — a Poisson solve over `Vixen.Geometry.Uv`'s conjugate gradient — and a
`TexturePlan` has no op that is not a compute dispatch, so it cannot be expressed at all yet
([#688](https://github.com/Rikarin/Vixen/issues/688)). `TextureSurfaceKernelTests` asserts its absence
**by name**, so "nobody has built it" and "somebody built it as a kernel" are different colours rather
than the same silence.

**⚠ The green convention is derived, not chosen, and it is the defect that survives every review
because it looks like lighting.** § 4.6 says the convention is whatever `TexturedNormalMapSurface`
samples; following that through gives one answer and no freedom — `MaterialSurface.rvn` decodes
`2v − 1` with **no green flip anywhere in the sampling path**, the tangent frame's bitangent is the
direction `v` increases in, and `v` increases *downwards*. So green is `−∂h/∂v` with v pointing down
the image, and a height that rises as you move down the picture is green below a half. It is asserted
against a known ramp rather than claimed by a comment.

**⚠ `NormalCombine` is reoriented normal mapping, and whiteout agrees with it exactly whenever either
input is flat** — which is the case every lazy test reaches for. The assertion therefore tilts *both*
inputs 45°, where the two give colours nobody could confuse.

**⚠ Two of the five are the *cheap* measurement and their inspectors say so.** `Curvature` and
`AmbientOcclusion` read a picture of a surface; § D12's mesh bake reads the surface. A height field
cannot represent an overhang, so nothing here can occlude under one, and a bevel that was modelled but
not baked into the normal map is invisible to the curvature. A wear generator driven by the wrong one
looks merely uninspired rather than broken, which is why the choice is stated at the node as well as
here.

## Placement — doc 48 § 4.7

Two `.rvn` files and `TextureKernels.Placement.cs`: `TileSampler` and `Splatter` — § D7's replacement
for FX-Map, and the pair nearly every pattern in § 4.9 is built out of.

**⚠ A scatter written as a gather, and there is no other choice.** An instance is drawn by a texel
asking which instances could reach it, never by an instance writing into the image: a storage image
has no blend hardware and no ordering between invocations. That is the same shape the engine's
virtualized geometry uses for per-instance deformation, for the same reason.

**The loop bounds are the trade, and they are visible on purpose.** Refusing FX-Map's recursion buys a
kernel whose cost is knowable before it runs, and that promise is only worth something if a reader can
multiply it out: `MaxSearch` and `MaxSamples` bound the two nested loops and there is no `while`
anywhere. ⚠ **The CPU side refuses a parameter that would exceed them rather than clamping it**, which
is #678's lesson applied one node over — a silent clamp is a different picture at a different bake
size with nothing anywhere saying so. `TileSampler`'s search radius is *exact* rather than a guess
because every random modulation shrinks an instance and none grows it, so the largest footprint any
instance can have is known on the CPU.

**Both wrap, and the wrap is what makes them assertable.** A cell index folds into the grid while the
geometry uses the unfolded one, so an instance overhanging an edge is the same instance found again
one period over: the image is seamless *and* no instance is ever clipped, which is what makes the mean
of an accumulated field a closed form with no statistics in it.

## Pixels that did not come from a kernel — § 4.1's `Text` and `Svg Path`

`TextureUploads` turns CPU texels into the texture a plan's **external** image is read from, and its
`Externals` is what goes into `Evaluate`'s second parameter. `AddCoverage` takes one float per texel —
which is the shape `Vixen.Ui.Text`'s `CoverageBitmap.Coverage` already has — and uploads it as an
`R8` mask.

**⚠ Why an upload rather than a kernel.** Doc 48 § 4.1 lists `Text` and `Svg Path` among its eight
sources and § 4.11 counts them into the forty-four, and neither can be a compute kernel: a compute
shader has no rasteriser, and `TexturePlanEvaluator` compiles each kernel alone through
`RavenEffectCompiler.FromSources` with no reference paths, so no kernel can reach a font, a glyph
outline or a path parser. What a plan *has* always been able to express is a picture the caller
supplies — `TextureImage(…, External: true)` — and what was missing was any way in this assembly to
produce one ([#687](https://github.com/Rikarin/Vixen/issues/687)). That is what this is.

**⚠ A type of its own rather than a method on the evaluator, and the reason is a lifetime.** A
`TextureBake` destroys its textures when it is disposed; an uploaded bitmap outlives any one bake,
because § M4's interactive preview re-evaluates the same plan over the same picture many times a
second. An upload owned by a bake would be destroyed by the next evaluation, and re-uploading a 4K
bitmap per keystroke is the cost that arrangement hides.

**⚠ `R8` is uploadable although no kernel can write it, and the guard that must not be added is
`IsStorable`.** That predicate answers "may a kernel write this", and neither `R8` nor `Rg8` is a
storage image on a conformant device — but a mask is *read*, it is a quarter of the bytes, and a
sampled read hands a kernel `(r, 0, 0, 1)`. Both halves are asserted together so the pairing is
visible.

**⚠ An upload's size is the caller's, and `TexturePlan.SizeOf` cannot tell you it.** That method reads
a size off the image's level and the plan's base resolution; nothing allocates an external image, and
`Validate` skips its level entirely — so for one it returns a number no picture produced.
`TextureUploads.SizeOf` is what remembers the real one, and a device test asserts the two disagree so
that the trap is written down rather than discovered.

**Where the two nodes themselves have to live, which is not here.** Neither rasteriser can be in this
assembly as it stands, and the two costs are very different:

- **`Svg Path`** needs `Core/Vixen.Ui`'s `SvgPath` and `PathTessellator`. `Vixen.Ui`'s project closure
  is twenty projects against this assembly's seventeen, and eleven of them would be new here — the
  whole UI framework plus `Vixen.Input`. ⚠ A bake would then have the element tree, the cascade and
  the input enum behind it, which is a cost no content build should pay for a parser.
- **`Text`** needs `Core/Vixen.Ui.Text`, whose closure is `Vixen.Core` and nothing else — **already in
  this assembly's closure**, so the projects added are zero and the only new dependency is
  HarfBuzzSharp's native assets. ⚠ So "`Text` is blocked by the same wall as `Svg Path`" is false; what
  makes it not this assembly's work is narrower, and it is *which font*: `FontFace.Load` takes bytes,
  and resolving an asset to bytes is the project-and-document question this assembly deliberately
  knows nothing about.

Both belong with the § M4 node classes, whose own closure already contains `Vixen.Ui` through
`Vixen.Editor.NodeGraph` — so neither reference costs anything *there*, and the evaluator keeps the
property that makes every test in this project a test of the evaluator.

Licensed under Apache-2.0.
