---
title: Evaluating a texture plan
slug: editor/texture-graph-evaluation
kind: guide
area: Editor
summary: The plan of compute kernels a texture graph and a layer stack both compile to, the image pool that runs it, and the resolution rule that keeps a graph the same material at every size.
api: [T:Vixen.Editor.TextureGraph.TexturePlan, T:Vixen.Editor.TextureGraph.TextureOp, T:Vixen.Editor.TextureGraph.TextureImage, T:Vixen.Editor.TextureGraph.TextureParameter, T:Vixen.Editor.TextureGraph.TextureParameterUnit, T:Vixen.Editor.TextureGraph.TextureFormat, T:Vixen.Editor.TextureGraph.TextureFormats, T:Vixen.Editor.TextureGraph.TexturePoolSlot, T:Vixen.Editor.TextureGraph.TexturePoolSchedule, T:Vixen.Editor.TextureGraph.TextureKernels, T:Vixen.Editor.TextureGraph.TexturePlanEvaluator, T:Vixen.Editor.TextureGraph.TextureBake, T:Vixen.Editor.TextureGraph.TextureProblem, T:Vixen.Editor.TextureGraph.TextureProblemSeverity, T:Vixen.Editor.TextureGraph.ITextureCpuOperation, T:Vixen.Editor.TextureGraph.TextureCpuImage, T:Vixen.Editor.TextureGraph.TextureCpuInvocation, T:Vixen.Editor.TextureGraph.TextureUploads]
tags: [editor, texture-graph, material-authoring, compute, raven, baking]
since: 0.1
status: preview
related: [editor/shader-graph-previews, editor/vfx-graph, editor/node-port-editing]
---

## What it is

A `TexturePlan` is a table of images and an ordered list of `TextureOp`s over it. Each op names a
compute kernel, the images it reads as indices into the table, the one image it writes, and the numbers
the kernel takes. `TexturePlanEvaluator` runs one on an `IGraphicsDevice` and hands back a
`TextureBake`, which owns the textures and can read one out as a `Bitmap` or write it as a PNG.

That is the whole surface. There is no graph here and no layer stack — both of those compile *to* a
plan, which is what stops the two from acquiring two evaluators and then two opinions about what
"overlay" means.

## What it is for

Computing a material's textures at author time: a blur into a levels into a blend, at 2K, written into
`Assets/` as ordinary PNGs that the existing importer and the existing content build already
understand. A shipped game never evaluates one.

You do not want it for anything a frame draws. A texture graph is evaluated **once, into an image**; a
shader graph is evaluated **per pixel, per frame, on the mesh**. A blur cannot exist in the second and
a lighting model cannot exist in the first.

## Using it

```csharp no-compile="a fragment against a caller's own device and its imported bitmap"
var plan = new TexturePlan {
    BaseWidth = 2048,
    BaseHeight = 2048,
    Seed = 41823,
    Images = [
        new(TextureFormat.Rgba8, External: true),   // 0 — supplied by the caller
        new(TextureFormat.Rgba16Float),             // 1 — blurred along x
        new(TextureFormat.Rgba16Float),             // 2 — and along y
        new(TextureFormat.Rgba8)                    // 3 — the output
    ],
    Ops = [
        new() {
            Kernel = "Blur",
            Output = 1,
            Inputs = [0],
            Parameters = [
                new("radius", 8f, TextureParameterUnit.TexelsAtBase),
                new("stepX", 1f),
                new("stepY", 0f)
            ]
        },
        new() {
            Kernel = "Blur",
            Output = 2,
            Inputs = [1],
            Parameters = [
                new("radius", 8f, TextureParameterUnit.TexelsAtBase),
                new("stepX", 0f),
                new("stepY", 1f)
            ]
        },
        new() {
            Kernel = "Levels",
            Output = 3,
            Inputs = [2],
            Parameters = [
                new("inputBlack", 0.1f), new("inputWhite", 0.9f), new("gamma", 0.8f),
                new("outputBlack", 0f), new("outputWhite", 1f), new("dither", 1f)
            ]
        }
    ],
    Outputs = [3]
};

using var evaluator = new TexturePlanEvaluator(device);
using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = source });

bake.Save(3, "Assets/Materials/hull-height.png");
```

`Evaluate` opens its own frame, submits one command list and waits, so it must not be called between a
caller's own `BeginFrame` and `EndFrame`. A bake is a modal operation somebody started.

## Resolution is relative, and every length is in texels at the base

The plan declares the resolution the graph was **authored** at. Every image is a power of two away from
it — `LevelOffset` `1` is half, `-1` is double — and only an external image has a size of its own.

**Every radius, width and length is authored in texels at the base resolution** and scaled by the
evaluator to the image the op writes. `TextureParameterUnit.TexelsAtBase` is what says so, and
`TexturePlan.Resolve` is the one place the scaling happens.

> ⚠ A radius stored as absolute texels looks right at the resolution it was tuned at and is half as
> wide at 4K — so a graph authored at 1K and shipped at 4K is a **different material**, and nobody
> associates the change with the resolution field. Storing it as a fraction of the image has the
> mirror-image failure at a non-square resolution. Texels-at-base, with the base written in the plan,
> is the only form in which both questions have one answer.

## Baking the same graph at another resolution

`BakeLevelOffset` says how big the whole graph is being made this time, in the same currency and with
the same sign as an image's own level. `0` bakes at the authoring resolution, `-2` bakes a 1K graph at
4K, `1` bakes a 512 preview:

```csharp no-compile="the plan above, baked at four times what it was authored at"
var at4K = new TexturePlan {
    BaseWidth = 2048,                                        // still what the graph was authored at
    BaseHeight = 2048,
    BakeLevelOffset = TexturePlan.BakeLevelFor(2048, 8192),   // -2
    Images = plan.Images,
    Ops = plan.Ops,
    Outputs = plan.Outputs
};

// Every image is four times wider, and so is every radius.
var width = at4K.SizeOf(1).X;                                      // 8192, was 2048
var radius = at4K.Resolve(0, at4K.Ops[0].Find("radius")!.Value);   // 32, was 8
```

`BakeLevelFor` refuses a ratio that is not a power of two — a 1536-wide bake of a 1024 graph would put
every image at a size no level names. Bake at the next power of two and resample the file.

> ⚠ **`BaseWidth` alone cannot express this, and until
> [#619](https://github.com/Rikarin/Vixen/issues/619) it was the only field there was.** Moving the
> base moves the unit a radius is counted in by exactly as much, so a plan with a base of 1024 and one
> with a base of 4096 both resolve `8` texels-at-base to `8` — the two-year fuse § D8 was written to
> prevent, lit inside the type meant to prevent it. Two fields; one for what the artist authored, one
> for what this run is producing.

> ⚠ **One number rather than a bake width and a bake height.** Two would let a caller ask for
> 4096×2048 out of a 1024² graph, and then a radius would be either four times wider in x and twice in
> y — a filter that is no longer round — or wrong in one axis.

> ⚠ **Copying `Ops` like this is right for nearly every plan and wrong for three nodes.** `Distance`,
> `Flood Fill` and `Auto Levels` are *chains* whose op **count** is a function of the baked extent — a
> jump flood is `log2(n)` ping-ponged dispatches, a reduction is one per level down to 1×1 — so their
> ops are emitted for one resolution and re-using them at another leaves too few of them. Every op of
> such a chain carries `TextureOp.EmittedForExtent`, and `Validate` refuses the plan rather than baking
> a distance field that is wrong at long range and looks merely soft
> ([#689](https://github.com/Rikarin/Vixen/issues/689)). The fix is to re-emit the chain from the
> front end for the bake you want; a plan is a compiled artefact, and only the *graph* bakes at any
> size.

## The image pool

An image is written by exactly one op, so it is live from that op until the last op that reads it. The
evaluator allocates on first write, frees when the last reader has run, and reuses a freed slot of the
same format and size. `TexturePoolSchedule` works all of that out from the op order alone, with no
device, which is what lets the bound be asserted anywhere:

```csharp no-compile="a fragment against the plan above, as TexturePoolTests asserts it"
var schedule = TexturePoolSchedule.For(plan);

// A chain of forty ops threaded through two live images allocates two textures — at 2K, 32 MB
// rather than 640 MB.
Assert.Equal(2, schedule.Allocations);
```

> ⚠ An op's output is taken **before** its dying inputs are given back. The other order hands an op the
> texture it is about to read, and a dispatch has no ordering between its own invocations — so what
> comes out is half the old image and half the new one, on some drivers, some of the time.

## What a plan refuses, and what it only warns about

`Check()` is the whole answer and returns a `TextureProblem` per problem, each with a
`TextureProblemSeverity`. `Validate()` is the refusals as sentences — `Evaluate` throws on any of them
— and `Warnings()` is the other half, which a bake carries on `TextureBake.Warnings`:

```csharp no-compile="a plan authored at 256 with a sharpen of 4 texels, baked at 4×"
foreach (var problem in plan.Check()) {
    Console.WriteLine($"{problem.Severity}: {problem.Message}");
}

// Warning: Op 0 runs 'Sharpen' with radius 4, which is 16 at the resolution it writes — past the 8
// the kernel loops to. It would be clamped, silently, …
```

> ⚠ **A plan validated its shape and never its numbers, and that is
> [#692](https://github.com/Rikarin/Vixen/issues/692).** Indices, formats, write-once and liveness all
> held while a resolved radius past a kernel's own loop was clipped by the shader with no message
> anywhere — so the same graph was a different material at a larger bake. The number that has to be
> checked is the **resolved** one, which exists only once a bake resolution has been chosen, and the
> plan is the only layer that can see both it and the kernel's ceiling.

> ⚠ **Refusing would have been wrong**, which is why there is a third state. The larger bake is what
> the artist asked for and the clip may be acceptable; what was missing was anywhere for a bake to
> *say* it clipped something. Put `TextureBake.Warnings` in front of whoever chose the resolution.

## An op that is not a dispatch

`TextureOp.Cpu` holds an `ITextureCpuOperation`, and the evaluator ends the list in flight, waits,
reads its inputs back as raw texels, runs it, uploads the answer and opens a new list. The pool, the
liveness and the barriers are unchanged around it — a CPU op writes one image, reads by index, and is
written to once.

> ⚠ **This is doc 48 § 4.6's one stated exception to § D3's "no CPU implementation of any node", and it
> is not an escape hatch from writing a kernel.** It exists for `Normal → Height`, a Poisson solve over
> `Vixen.Geometry.Uv/Solving/ConjugateGradient.cs`, because there is no GPU formulation of that worth
> having — low frequencies converge in O(n²) Jacobi sweeps, so the shader version is thousands of
> dispatches. The test of whether a node belongs here is "is there a GPU formulation at all", never
> "would this be easier in C#": each of these costs two full pipeline drains in the middle of a bake.
> An implementation here that reproduces what a `.rvn` already does is exactly what § D3 bans.

## Formats

`R8` · `Rg8` · `Rgba8` · `R16Float` · `Rgba16Float`. 32-bit float is deliberately absent.

> ⚠ **`R8` and `Rg8` can be read and cannot be written.** Raven declares no `r8` or `rg8` storage image
> and Vulkan requires storage support for neither, so a kernel writing one fails at pipeline creation.
> `TexturePlan.Validate` refuses it where the plan is built; compute in one of the three storable
> formats and narrow at the encode.

> ⚠ **The 32-bit absence is a decision about material maps, not a capability.** Raven admits `r32f`,
> `rg32f` and `rgba32f`, the RHI maps all three, and `HiZPyramid` already dispatches into an
> `R32Float` storage image — so the argument is the memory, and it is larger than it looks: an
> `Rgba16Float` intermediate at 4K is 128 MiB and `Rgba32Float` is 256 MiB. § 4.5's two
> position-carrying records are the case for widening it to `rgba32f`
> ([#690](https://github.com/Rikarin/Vixen/issues/690)); a colour never is.

## Pixels the caller supplies

An image the plan marks `External: true` is not allocated, not pooled and never written by an op — it
is what a bitmap input is, and the only place an absolute size enters a plan. `TextureUploads` is what
turns bytes into the texture behind one, and its `Externals` is what `Evaluate` takes:

```csharp no-compile="a fragment against a caller's own device and its own pixels"
using var uploads = new TextureUploads(device);

uploads.Add(plan, 0, width, height, rgba);                     // the image's own format
uploads.AddCoverage(plan, 1, width, height, glyph.Coverage);   // one float per texel, into an R8

using var bake = evaluator.Evaluate(plan, uploads.Externals);
```

It is a separate object from the bake because the two have different lifetimes: a `TextureBake`
destroys its textures when it is disposed, and an imported bitmap outlives every bake made from it.
Dispose it when the document closes.

> ⚠ **`R8` and `Rg8` are uploadable although no kernel can write them.** `TextureFormats.IsStorable`
> answers "may a kernel write this" and is false for both; a mask is *read*, costs a quarter of what
> RGBA costs, and a sampled read hands the kernel `(r, 0, 0, 1)`.

> ⚠ **Doc 48 § 4.1's `Text` and `Svg Path` arrive this way rather than as kernels.** A compute shader
> has no rasteriser and each kernel is compiled alone with no reference paths, so neither can reach a
> font or a path parser. Both are filled on the CPU and uploaded as coverage — see
> [#687](https://github.com/Rikarin/Vixen/issues/687).

## The seed

`TexturePlan.Seed` is hashed **per op** — `SeedFor(op)` — so a bake is reproducible and inserting a
node upstream does not change the numbers every node downstream draws. A kernel that declares a `seed`
uniform is given it by the evaluator; `Levels` uses it for the ordered dither that keeps a lifted curve
from banding in an 8-bit file.

## What a bake does not do yet

Mip chains, block compression, the `.vxmat` write and the `texturing:` provenance block are the baking
phase's, over `Vixen.Core.Imaging`. What lands today is one image per output, as a PNG.

## Testing it

`TexturePlanTests`, `TexturePlanCheckTests`, `TexturePoolTests` and `TextureKernelTests` need no device
at all — the resolution rule, what a bake clips, the pool bound and every kernel's compilation in every
format are asserted on any machine.

`TexturePlanDeviceTests` needs one, and **names its adapter in every message**.

> ⚠ Without `--vixen-offscreen` a headless run falls back to the Null device on every platform, exits 0
> and prints identical healthy counters. A texture-graph test that passed there would have proved that
> a black image equals a black image. These skip loudly instead, and `VIXEN_REQUIRE_VULKAN=1` turns the
> skip into a failure.

What they assert are closed forms rather than goldens, and never a CPU re-implementation: a box
filter's impulse response is `1/(2r+1)` over exactly `2r+1` texels; a levels curve maps three known
inputs to three known outputs; the same authored radius produces a 17-texel bar at the base resolution
and a 9-texel bar at half of it.

§ D8's own criterion is one of them —
`The_same_plan_baked_at_four_times_the_resolution_agrees_with_the_smaller_bake` bakes one plan at
`BakeLevelOffset` 0 and −2 over a step edge, box-downsamples the larger 4:1, and requires the two
profiles to agree. On an M1 Max the worst column differs by 4/255 against a tolerance of 8; a radius
that did not scale parts them by 92.

`TextureCpuOpDeviceTests` is the round trip through a `TextureOp.Cpu` op: `invert → transpose →
invert`, whose closed form is the transpose of the source, exactly, in every channel. ⚠ Its second
test's name claims the two pictures and **not** the layout barrier that hands an external image back
readable — deleting that barrier leaves both assertions green on an M1 Max, because a unified-memory
adapter reads an image left in a transfer layout perfectly well. The validation layers are the only
witness for a layout, and this suite cannot use them: `VulkanDiagnostics` is process-wide and every
device class here opens its own device in parallel.

> ⚠ **`TextureQueueTests` opens a Null device on purpose**, and it is the only file here that does. A
> unified adapter cannot tell the compute queue from the graphics one — which is why
> [#617](https://github.com/Rikarin/Vixen/issues/617), a bake that wrote on one and read back on the
> other with no ownership transfer, was invisible on every machine this engine has been developed on.
> `NullDevice` builds three distinct submitters, so the question has an answer there. It asserts a
> queue and never a texel.
