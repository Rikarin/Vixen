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

Licensed under Apache-2.0.
