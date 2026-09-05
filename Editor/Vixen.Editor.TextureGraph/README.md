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

[§ D8](../../docs/plan/48-material-authoring.md). The plan declares a base resolution; every image is a
power of two away from it; **every radius, width and length is in texels at the base resolution** and is
scaled by the evaluator to the image the op writes.

⚠ **A radius stored as absolute texels looks right at the resolution it was tuned at and is half as
wide at 4K**, so a graph authored at 1K and shipped at 4K is a different material and nobody associates
the change with the resolution field. Storing it as a fraction of the image has the mirror-image failure
at a non-square resolution. `TexturePlanDeviceTests` proves the scaling on a device with two plans that
differ only in the base and write images of exactly the same size: the impulse bar is 17 texels wide in
one and 9 in the other, and a radius that reached the kernel unscaled would make them identical.

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

Licensed under Apache-2.0.
