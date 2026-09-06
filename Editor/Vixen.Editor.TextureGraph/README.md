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

- **No `.vxtexgraph` file, no document, no panel.** A plan is the artefact and a *graph* is one way of
  producing it; opening a file, drawing it and undoing an edit are
  [`Vixen.Editor.Texturing`](../Vixen.Editor.Texturing/README.md)'s.
  ⚠ **This bullet used to read "no node classes, no compiler, no `.vxtexgraph`", and the first two
  clauses stopped being true at M4** ([#736](https://github.com/Rikarin/Vixen/issues/736)). They are
  here: `TextureGraphCompiler` *is* a `NodeGraphCompiler<TexturePlan>`, [`Nodes/`](#the-node-library)
  holds a `[Node]` class per catalogue entry, and the assembly references `Vixen.Editor.NodeGraph` —
  the csproj says at length what that reference cost and
  [#720](https://github.com/Rikarin/Vixen/issues/720) is the bill.
  **What the deleted claim was for is worth keeping, so it is stated as a convention rather than as a
  wall**: it used to be true *by construction* that every test here tested the evaluator, and it is
  now true by agreement — the compiler suites build graphs, the evaluator suites hand-build plans, and
  a differential crosses the line deliberately.
- **No CPU implementation of any kernel** — [§ D3](../../docs/plan/48-material-authoring.md). ⚠ Which
  is not the same as "no CPU op": `NormalToHeightOperation` is § 4.6's stated exception, and what
  makes it one is that there is no *kernel* it duplicates. `TextureNodeLibraryTests` refuses a CPU
  operation that shares a name with an embedded shader, which is this rule made mechanical. A parity
  test against a C# re-implementation proves the two transcriptions agree, not that either is right,
  and this repository has already fallen into that trap once. What the device tests assert are
  **closed forms**: a box filter's impulse response is `1/(2r+1)` over exactly `2r+1` texels, a levels
  curve maps three known inputs to three known outputs. `TexturePixels` converts half-floats to bytes
  on the way to a file and is an encoder, not a twin — nothing in a graph does it.
- **No UI and no project.** Nothing here draws, and no test here needs a panel to check any of it. ⚠
  The `Vixen.Editor.NodeGraph` reference above means the *assembly closure* is a UI one even so, which
  is the difference [#720](https://github.com/Rikarin/Vixen/issues/720) exists to restore.
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

## The node library

`Nodes/` is the graph half: a `[Node]` class per catalogue entry, one file per doc 48 group, each
declaring its ports and settings as fields and emitting ops in `Compile`. The menu path is the
attribute's first argument and is doc 48's own section name — `Source/Noise`, `Filters/Slope Blur`,
`Surface/Normal to Height` — so the create menu is the catalogue.

⚠ **The number of them is deliberately not written down here.** `TextureNodeLibraryTests` reads both
surfaces rather than a list: the kernels come out of the assembly by reflection and the nodes out of a
plan the whole library actually compiles to, so a kernel added without a node is red and a node added
without being wired into the fixture is red for a *different* reason and says which. A second list in
this file would be the thing that drifts, and this README has already been that thing twice
([#695](https://github.com/Rikarin/Vixen/issues/695),
[#728](https://github.com/Rikarin/Vixen/issues/728)).

**⚠ Every kernel is reachable from a graph but one, and the exception is not a gap in the library.**
`FloodResidual` reports whether a flood's last propagation changed anything — one number, and a plan
output is a picture, so an author has nowhere to read it. Everything else that has no class of its own
is a *step of a chain* rather than a missing node: `JumpFlood`, `FloodBounds` and `MinMaxReduce` are
dispatched by `Analysis/Distance`, `Analysis/Flood Fill` and `Colour/Auto Levels`, and the roll call
counts a kernel as covered when a graph reaches it, never when a class is named after it.

**⚠ A node's default and its kernel's are two answers to one question and nothing makes them agree.**
The evaluator writes every parameter of every op, so a `.rvn`'s initializer is read only by a person —
which is the arrangement that drifts silently, because both numbers draw a picture. The
disagreements that are *meant* are a table in `TextureNodeLibraryTests` with a defence per row
(`Source/Uniform` is white so an unwired node is visible; `Filters/Blur`'s 8 is a blur you can see);
everything else has to match.

**Doc 48 § 4.8's five structure nodes are not five classes here, and three of them never will be.**
`Output` is one — it carries the usage a bake writes it under. `Input` and `Sub-graph` are
`Vixen.Editor.NodeGraph`'s: `SubGraphs.InputType` / `OutputType` are *boundary* types built per graph
rather than registered, and `TextureGraphLibrary.Publish` registers a published `.vxtexgraph` as a
node type through `SubGraphLibrary`. ⚠ **`Material Output` turned out not to be needed at all** — the
bake takes a dictionary keyed by `MaterialMapUsage` and every `Output` node already carries one, so
the grouped node would have been a second way to say the same thing. `Mesh Map Input` is § D12's and
arrives with the mesh-map slice.

⚠ **`Filters/Pixel Processor` is a node with no catalogue row**, because § D6 describes it as a
decision rather than as a node and Part 4 never lists it. It is the escape hatch, and the section
below on § D6 is where it is written down.

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
A rotation is in radians, a scale is a ratio, an offset is a fraction of the image, a rect is
normalised and a repeat is a count — and ⚠ `Hsl`'s `hue` is the one angle-shaped number in this
folder that is still **turns**, deliberately, because it is a position on a colour wheel rather than a
direction on the image and `0.5` meaning *opposite* is what every colour picker shows
([#735](https://github.com/Rikarin/Vixen/issues/735) unified the geometric ones) — they are resolution-independent *by construction* rather than by
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

⚠ **The other direction is where a box filter is a point sample under another name.**
`Resample.rvn` takes `clamp(ceil(extent / size), 1, MaxSamples)` sub-samples per axis, which is
exactly one whenever the target is the *larger* image — so `Box` going up reads a single texel at the
output texel's centre. `Rescale` derives the filter from the two level offsets
([#829](https://github.com/Rikarin/Vixen/issues/829)) and the node derives it from `Size`
([#865](https://github.com/Rikarin/Vixen/issues/865)); the setting's default is `Auto` rather than a
filter name, because no one name is right in both directions.

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

**⚠ This is the group § D8 is mostly about: nine of the eleven take a length, against one of § 4.5's
three and three of § 4.6's six, and none at all in § 4.1, § 4.2, § 4.3 or § 4.7.**

⚠ **This paragraph used to say "the only one where every node takes a length", and both halves were
wrong** ([#728](https://github.com/Rikarin/Vixen/issues/728)). `Emboss` (`angle`, `elevation`,
`intensity`) and `RadialBlur` (`centreX`, `centreY`, `amount`, `samples`) take none, and **each says
so in its own file header** — so the README contradicted two kernels that were already right. It was
a stale claim of exactly the class this file exists to remove, reintroduced by the rewrite that
removed the last one, which is why the number is not repeated as a total anywhere: `TextureParameter`
carries `TexelsAtBase` at the declaration, so a `grep -rn TexelsAtBase Editor/Vixen.Editor.TextureGraph`
is the count and this sentence is only a reading of it. ⚠ It finds `Blur`'s own radius in
`Nodes/FilterNodes.cs` rather than in `TextureKernels.Filters.cs`, because § M1 wrote that kernel and
§ 4.4 only owns it.

`TexturePlan.Resolve`'s scaling therefore passes straight through § 4.2 and § 4.3 and does most of its
work here. That also
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
· `NormalTransform` · `Curvature` · `AmbientOcclusion` — and `TextureKernels.Cpu.cs` for the sixth,
which declares no shader.

**⚠ `Normal → Height` is the sixth, it is built, and it is deliberately not a `.rvn`.** Doc 48 § 4.6
makes it the catalogue's one CPU exception — a Poisson solve — and `NormalToHeightOperation` is it,
over doc 42 § B1's warm-started conjugate gradient in `Vixen.Geometry.Uv/Solving`.
`TextureSurfaceKernelTests` asserts that a plan expresses **exactly one** kind of op that is not a
dispatch, which is a claim about `TextureOp`'s shape rather than about a missing file — so a second
execution model arriving is red, and a `Shaders/NormalToHeight.rvn` appearing is red for the other
reason.

⚠ **The solver was reachable only through a csproj line.** Every type under `Solving/` is `internal`
and had no caller outside its own assembly, so `Vixen.Geometry.Uv` names this one in an
`InternalsVisibleTo`. That is a wart with a written fix —
[#752](https://github.com/Rikarin/Vixen/issues/752), which is to give the solver an assembly of its
own, since nothing in it mentions a chart or a triangle. Writing a second conjugate gradient here to
avoid the line is the outcome doc 42 argued against.

⚠ **The answer has mean zero and is therefore signed.** A gradient field fixes a height only up to a
constant; that constant is the one part of the answer the input does not contain, and picking it by
min–max would make the node depend on a single extreme texel. A `Levels` after it is what makes a
`[0, 1]` map, and that is the node whose whole job it is.

⚠ **And its `iterations` is a budget rather than a target**, which is doc 42 § D5's trade: a residual
comparison decides differently on different hardware and a bake is meant to be byte-identical. So a
graph baked large and looking gently tilted wants a larger number, and no number announces itself as
enough. The cost of the exception is real and visible: a CPU op ends the command list, waits, copies
both ways and starts a new one — two pipeline drains — and `TextureNormalToHeightDeviceTests` is
where the two-byte-per-texel side of that transport is proved, since the only CPU op before it moved
four bytes each way.

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

**`Text` is built and lives here; `Svg Path` is refused, and the measurement that refused it was
wrong.**

`TextureText.Rasterize` shapes a string with `TextShaper`, reads each glyph through
`FontFace.GetOutline` and fills it with `GlyphRasterizer` — the **`Outlines` path, not
`GlyphAtlas`**, because an atlas caches small rasterisations behind a distance field so a *label*
scales smoothly, while a texture graph fills one outline once at whatever size a 4K bake asks for.
The result goes through `AddCoverage`, and `TextureTextDeviceTests` closes the whole path on an
adapter in eight bits, texel for texel.

⚠ **It takes a `FontFace` and never a path, which is what keeps this assembly's ignorance intact.**
The paragraph this replaced was right about the real obstacle: resolving an asset to bytes is the
project-and-document question this project deliberately knows nothing about. It is still not asked —
the caller supplies the face, exactly as the caller supplies an external image's texels.

⚠ **And there is no `Text` node**, for a reason that has nothing to do with fonts: a node has to
allocate an *external* image, and `TextureGraphCompiler.Allocate` only ever builds a pooled one. That
is [#732](https://github.com/Rikarin/Vixen/issues/732), shared with `Bitmap`, `Gradient`, `Curve` and
`Gradient Map`, and it is why the roll call's `Unnoded` list will not grow a `Text` entry — there is
no kernel to excuse.

⚠ **The closure argument against `Svg Path` does not survive re-derivation.** This file said
`Vixen.Ui`'s closure was twenty against this assembly's seventeen with eleven new. Re-derived over
every `ProjectReference` in the tree on 2026-09-05:

| | |
|---|---|
| this assembly's closure | **29** projects — and `Vixen.Ui` and `Vixen.Ui.Text` are already two of them |
| `Vixen.Ui`'s closure | **14**, a strict subset of those 29 |
| new projects a `Vixen.Ui` reference would add | **zero** |

The interface framework arrived with the `Vixen.Editor.NodeGraph` reference M4 could not do without,
and the csproj already says so. ⚠ And the wrap really is a wrap: `PathVerb` and `OutlineVerb` are the
same five verbs, one fixed-size struct per verb, in both files, each citing the other's decision —
`PathBuilder` → `GlyphOutline` is a five-case switch.

**What refuses it instead is a compile surface, and that argument does hold.**
`DisableTransitiveProjectReferences` makes what this project may *spell* exactly what it names.
`Vixen.Ui.Text` is a leaf — its own closure is one project — and naming it buys a font and a scanline
fill. Naming `Vixen.Ui` buys `UiElement`, `Signal`, styling, layout and input inside an assembly
[#720](https://github.com/Rikarin/Vixen/issues/720) is trying to make *less* of a UI assembly. On top
of that, § 4.1 wants a fill rule and `GlyphRasterizer` is non-zero winding only, deliberately.

So `Svg Path` belongs on the far side of #720's split: a path is rasterised where a *node* is
compiled and never where a *plan* is evaluated, so the evaluator half — the one the headless content
build loads — never needs `SvgPath` at all.
[#753](https://github.com/Rikarin/Vixen/issues/753) carries it.

## What a node may ask the plan for

A node reads the image arriving at a port, asks for one to write and lists the dispatches between
them — everything structural is the compiler's. Two of those asks were missing, and their absence was
visible only as six kernels with no node.

**⚠ An image the *caller* supplies** ([#732](https://github.com/Rikarin/Vixen/issues/732)).
`TextureEmitter.External` allocates one and records what fills it on
`TextureGraphCompiler.Externals`, in one of two shapes: a ramp or a curve table is baked here and now
by `TextureRamp`, out of the editor's own gradient and Hermite evaluators, so the compiler carries the
bytes; an imported image is a *reference*, because a compilation runs on every edit and must not open
an asset database. `TextureGraphExternals.Upload` puts the first kind on a device and hands the second
kind back for a host to resolve. ⚠ That last sentence used to end *"has no in-tree consumer yet, so a
graph containing a `Source/Bitmap` compiles and does not bake"*, and
[#818](https://github.com/Rikarin/Vixen/issues/818) made it one:
`LayerStackPreview.Evaluate` walks the owed list, reads each named asset out of the project and
uploads it, and turns every one it cannot read into a sentence naming all of them at once. A stack's
texture-fill layers bake. ⚠ The *graph* panel still does not, and that is a different gap — it
evaluates a fixed checkerboard and never asks the document for its plan
([#792](https://github.com/Rikarin/Vixen/issues/792)).

**⚠ An image at a resolution of its own** ([#733](https://github.com/Rikarin/Vixen/issues/733)).
`Write` and `Scratch` take a level offset. Before that every image any node allocated was at the
plan's base, which made three kernels unreachable — a `MinMaxReduce` ladder onto same-sized images has
a block of one texel and never converges, and a `Resample` writing at its input's level is an
*identity copy*, because the target's size is the whole of the scale. ⚠ **A ladder is measured from
the image the node reads, not from the graph's base**: counted from the base, an `Auto Levels` after a
half-resolution `Resample` asks the kernel for a 16×16 block, the kernel clamps to its own 8×8
`MaxBlock`, and three quarters of every block is never read — the extremes of a corner, and a slightly
flat picture.

## The knobs, the expressions and the escape hatch — doc 48 § D6 and § D9

A published graph is a node, and its **exposed parameters** are that node's settings:
`TextureGraphParameter` carries a name, a type, a default, a range and a group, and
`TextureGraphLibrary.Publish` registers the node type. ⚠ `SubGraphLibrary`'s own registration writes a
definition with the interface as ports and **no settings**, which for a texture graph is a node with
every knob missing — so the definition is built here instead. ⚠ `SettingDefinition` carries a kind, a
range and a group of its own since [#730](https://github.com/Rikarin/Vixen/issues/730), so all five
of § D9's fields cross the node boundary; they used to ride in the setting's *summary*, which put a
declared `0…1` in a tooltip and drew the row as a text box.

**And the graph declares its own base resolution, seed and parameter list**
([#719](https://github.com/Rikarin/Vixen/issues/719)) — `NodeGraphModel.Settings` and
`.Parameters`, read by `TextureGraphSettings` and `TextureGraphParameters.Declared`. They were
properties of the *compiler*, so a `.vxtexgraph` reopened at whatever its host defaulted to; ⚠ the
seed is the half that mattered most, because § D5 says a texture whose output changes between runs is
not a source asset and a seed the host chose is exactly that. `BakeLevelOffset` deliberately stays a
property of the run: it says how big *this* bake is, and a saved one would be somebody's preview
resolution baked into the asset.

**Every scalar port accepts a Raven expression over those parameters** instead of a number, stored in
`GraphNode.Texts` under the port's name with an `=` in front of it. The whole graph's expressions
become one generated `.rvn` — a `const val` per parameter, then a `const val` per expression — which
the real `Compilation` binds, and the value read back is what `ConstantEvaluator` folded.

- ⚠ **Only what Raven folds folds.** Literals, `const` references, unary and binary operators and
  conversions do; a **call does not**, so `sin(amount)` is a stated refusal rather than a zero. That is
  the price of § D6's refusal to own a second evaluator, and widening it is a change to Raven's folder
  that every shader in the repository would also get.
- ⚠ **`const val amount: float = 0.5` folds to a *double*.** The field is a float and the folder keeps
  the literal's type, so the next line's `amount * 8f` is an operator over two types and folds to
  *nothing* — not an error, not a wrong number, **no** number. `Literal.Of` deliberately writes no
  suffix because a shader graph interpolates it into a typed context; here the literal is the type.
- ⚠ **An expression is one line**, because a newline ends a statement in Raven — and because every
  line after it is what a diagnostic's line number is mapped back through.
- ⚠ **An inlined node's expressions bind against the graph it was *written* in.** After
  `SubGraphs.Flatten` they sit in a graph whose parameters belong to somebody else, and a container
  with a knob of the same name would otherwise drive them silently. The scope comes from
  `NodeGraphInlining.Origins`. What an author types into the sub-graph node's settings still does not
  reach them — [#742](https://github.com/Rikarin/Vixen/issues/742).

The **Pixel Processor** is the same idea one layer down: its setting is a Raven expression compiled
into a whole generated kernel of the same shape as the forty-five committed ones, and its complaints
are Raven's own, carrying Raven's ids, addressed to the node and the setting.
⚠ **The op it emits used to name a kernel nothing could resolve** — the evaluator read every name
through this assembly's *embedded* sources, so a graph that looked complete threw at bake time about
a manifest resource nobody could have added. `TexturePlan.Kernels` is where an authored source rides
now ([#729](https://github.com/Rikarin/Vixen/issues/729)), and `TexturePlan.Source` is what both
paths go through. ⚠ **A name is authored or embedded and never both**: a compiled module is cached on
`(kernel name, output format)` across every plan an evaluator runs, so a plan redefining `Blur` would
either take the module already built from the embedded source or leave its own behind for the next
plan — one op, two pictures, decided by evaluation order. `Validate` refuses the collision.

## Per-node previews needed no split of `Evaluate`

⚠ **The claim that they did is refuted, and the reason is worth keeping.** What makes an intermediate
unreadable after a bake is the *pool* — its texture goes to the next image that needs one — but which
images the pool may reuse is the **plan's** decision: `TexturePoolSchedule` never reuses a slot holding
an image in `TexturePlan.Outputs`. So `TextureGraphCompiler.PreviewEveryNode` keeps every node's image,
and one ordinary `Evaluate` then holds all of them at once. `TextureGraphPreviewDeviceTests` proves
both halves on a real adapter: three greys read back as three greys with the flag set, and the first
one reads back as the *third node's* picture without it.

⚠ **A preview hands over a `Bitmap` and not a texture, and that is a Vulkan rule.** The images are
written on `ComputeQueue` and are `ResourceSharing.Exclusive`, so reading one from the queue family the
interface draws on — with no ownership transfer — is undefined by specification. It would look perfect
on this Mac, where MoltenVK reports one family for both. That is [#617](https://github.com/Rikarin/Vixen/issues/617)
and [#679](https://github.com/Rikarin/Vixen/issues/679) already.

Licensed under Apache-2.0.
