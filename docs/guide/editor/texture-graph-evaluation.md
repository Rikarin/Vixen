---
title: Evaluating a texture plan
slug: editor/texture-graph-evaluation
kind: guide
area: Editor
summary: The plan of compute kernels a texture graph and a layer stack both compile to, the image pool that runs it, and the resolution rule that keeps a graph the same material at every size.
api: [T:Vixen.Editor.TextureGraph.TexturePlan, T:Vixen.Editor.TextureGraph.TextureOp, T:Vixen.Editor.TextureGraph.TextureImage, T:Vixen.Editor.TextureGraph.TextureParameter, T:Vixen.Editor.TextureGraph.TextureParameterUnit, T:Vixen.Editor.TextureGraph.TextureFormat, T:Vixen.Editor.TextureGraph.TextureFormats, T:Vixen.Editor.TextureGraph.TexturePoolSlot, T:Vixen.Editor.TextureGraph.TexturePoolSchedule, T:Vixen.Editor.TextureGraph.TextureKernels, T:Vixen.Editor.TextureGraph.TexturePlanEvaluator, T:Vixen.Editor.TextureGraph.TextureBake]
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

The plan declares a base resolution. Every image is a power of two away from it — `LevelOffset` `1` is
half, `-1` is double — and only an external image has a size of its own.

**Every radius, width and length is authored in texels at the base resolution** and scaled by the
evaluator to the image the op writes. `TextureParameterUnit.TexelsAtBase` is what says so, and
`TexturePlan.Resolve` is the one place the scaling happens.

> ⚠ A radius stored as absolute texels looks right at the resolution it was tuned at and is half as
> wide at 4K — so a graph authored at 1K and shipped at 4K is a **different material**, and nobody
> associates the change with the resolution field. Storing it as a fraction of the image has the
> mirror-image failure at a non-square resolution. Texels-at-base, with the base written in the plan,
> is the only form in which both questions have one answer.

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

## Formats

`R8` · `Rg8` · `Rgba8` · `R16Float` · `Rgba16Float`. 32-bit float is deliberately absent.

> ⚠ **`R8` and `Rg8` can be read and cannot be written.** Raven declares no `r8` or `rg8` storage image
> and Vulkan requires storage support for neither, so a kernel writing one fails at pipeline creation.
> `TexturePlan.Validate` refuses it where the plan is built; compute in one of the three storable
> formats and narrow at the encode.

## The seed

`TexturePlan.Seed` is hashed **per op** — `SeedFor(op)` — so a bake is reproducible and inserting a
node upstream does not change the numbers every node downstream draws. A kernel that declares a `seed`
uniform is given it by the evaluator; `Levels` uses it for the ordered dither that keeps a lifted curve
from banding in an 8-bit file.

## What a bake does not do yet

Mip chains, block compression, the `.vxmat` write and the `texturing:` provenance block are the baking
phase's, over `Vixen.Core.Imaging`. What lands today is one image per output, as a PNG.

## Testing it

`TexturePlanTests`, `TexturePoolTests` and `TextureKernelTests` need no device at all — the resolution
rule, the pool bound and every kernel's compilation in every format are asserted on any machine.

`TexturePlanDeviceTests` needs one, and **names its adapter in every message**.

> ⚠ Without `--vixen-offscreen` a headless run falls back to the Null device on every platform, exits 0
> and prints identical healthy counters. A texture-graph test that passed there would have proved that
> a black image equals a black image. These skip loudly instead, and `VIXEN_REQUIRE_VULKAN=1` turns the
> skip into a failure.

What they assert are closed forms rather than goldens, and never a CPU re-implementation: a box
filter's impulse response is `1/(2r+1)` over exactly `2r+1` texels; a levels curve maps three known
inputs to three known outputs; the same authored radius produces a 17-texel bar at the base resolution
and a 9-texel bar at half of it.
