# Shaders

The texture graph's atomic kernels, in Raven. **Forty-five `.rvn` files**, which is
[doc 48 § 4.11](../../../docs/plan/48-material-authoring.md)'s forty-one adjusted one way — and the
adjustment is a fact about the catalogue rather than an arithmetic slip:

| | | |
|---|---:|---|
| § 4.11's compute kernels | **41** | The catalogue's rows, less the three that are not compute shaders |
| + `MinMaxReduce`, `JumpFlood`, `FloodBounds`, `FloodResidual` | +4 | **Dispatches, not nodes**: three nodes need a chain |
| = files in this folder | **45** | `TextureKernels.Names` at run time |

⚠ **This table used to start from forty-four and subtract three, and it double-counted a correction**
([#728](https://github.com/Rikarin/Vixen/issues/728)). Forty-four was § 4.11's count of catalogue
*rows*; § 4.11 has since taken `Text`, `Svg Path` and `Normal → Height` off it and states forty-one
compute kernels, so subtracting them a second time here made the arithmetic reach forty-five by
cancelling two errors. The three are still worth naming, and this is what they are:
`Text` and `Svg Path` are **not kernels and cannot be**
([#687](https://github.com/Rikarin/Vixen/issues/687)), and `Normal → Height` is a **CPU Poisson
solve** by doc 48's own exception ([#688](https://github.com/Rikarin/Vixen/issues/688)).

**Why the first two cannot be kernels.** A compute shader has no rasteriser, and
`TexturePlanEvaluator` compiles each kernel alone through `RavenEffectCompiler.FromSources` with no
reference paths — so no kernel can reach a font, a glyph outline or a path parser. Both shapes are
filled on the CPU and enter a plan as an **external image**; `TextureUploads` is the seam, and the
assembly README says where the two nodes themselves have to live. `TextureSurfaceKernelTests` asserts
`NormalToHeight`'s absence **by name**, so "nobody has built it" and "somebody built it as a kernel"
are different colours rather than the same silence. And the four extra files are separate for the
reason `Distance` is separate from `JumpFlood`: a chain writes a *record* and a node writes a
*picture*, and folding them would make the last dispatch of a chain a different kernel from the ones
before it.

⚠ **The folder is the list, and no table here is.** `TextureKernels.Names` reads the embedded
resources at run time, and `TextureKernelTests.Every_kernel_the_folder_holds_is_embedded` compares
that against the directory — so dropping a `.rvn` in registers it, and a table nobody updated cannot
make the folder wrong. What follows is a *reading* of the folder rather than a manifest of it: this
file listed three kernels for as long as the folder held twenty-four and then forty-five
([#695](https://github.com/Rikarin/Vixen/issues/695)), which is what a second list that has to agree
with a directory always eventually does.

## The catalogue, by doc 48's own sections

**§ 4.1 sources — six.** `Uniform` · `Bitmap` · `Gradient` · `Shape` · `Noise` · `Checker`. Analytic
rather than rasterised wherever the shape allows it, which is half of § D8's scale invariance passing
for free. ⚠ `Noise`'s basis is a **uniform and not a permutation**, because a plan has no way to name
one ([#638](https://github.com/Rikarin/Vixen/issues/638)); `Gradient` and `GradientMap` read a ramp
the CPU baked into a 256×1 row, so there is one `Gradient` evaluator in this repository and not two.

**§ 4.2 colour and channels — ten for nine nodes.** `Levels` · `Curve` · `GradientMap` · `Hsl` ·
`Grayscale` · `Invert` · `ChannelShuffle` · `Blend` · `AutoLevels` + `MinMaxReduce`. ⚠ `Blend`
carries **all sixteen** of § 4.2's modes, and
`TexturePlacementKernelTests.Every_blend_mode_named_in_C_sharp_has_a_case_in_the_kernel` counts them
on both sides — the eight of § M1 are numbered 0–7 and the rest were **appended**, because a plan is
a file and renumbering to match the prose would silently turn every plan already written into another
perfectly plausible picture. ⚠ `Blend` also carries `atop`, which is a **different question from the
mode**: whether the foreground *arrives on top of* the backdrop or *reinterprets* it. Over's alpha
rule is monotonic, so a filter layer composited over the picture it adjusts raises the coverage it was
handed and under-applies itself; no operator and no opacity can express "the coverage that leaves is
the coverage that arrived" ([#845](https://github.com/Rikarin/Vixen/issues/845)). ⚠ And `Blend` is
**not** the only kernel that reads `w` as coverage, however long this repository said so:
`TileSampler` and `Splatter` fold overlapping instances under the same rule and carried the same
premultiply defect because of that sentence ([#864](https://github.com/Rikarin/Vixen/issues/864)).

**§ 4.3 space — five.** `Transform2D` · `Mirror` · `Tile` · `Crop` · `Resample`. ⚠ **Minification is
supersampled by hand** in three of them, because the evaluator binds no samplers — there is no
hardware mip chain here and no anisotropic tap, so each derives the footprint of an output texel and
boxes over it.

**§ 4.4 filters — eleven.** `Blur` · `BlurHq` · `DirectionalBlur` · `RadialBlur` · `NonUniformBlur` ·
`Sharpen` · `Emboss` · `Warp` · `DirectionalWarp` · `VectorWarp` · `SlopeBlur`. ⚠ **Nine of the
eleven take a length, not all eleven** ([#728](https://github.com/Rikarin/Vixen/issues/728)):
`Emboss` and `RadialBlur` take none and each says so in its own header, so this file contradicted two
kernels that were already right. It is still the group § D8's scaling rule is mostly *about* — one of
§ 4.5's three has a length and three of § 4.6's six, and § 4.1, § 4.2, § 4.3 and § 4.7 have none at
all. ⚠ Two carry a convention rather than a
number: `VectorWarp` decodes `(rg · 2 − 1) · intensity`, so **128 is rest** and the one-sided reading
produces a picture that drifts one way at half the amplitude and looks entirely plausible; and
`SlopeBlur` is **iterative**, so its sample count changes the answer exactly where the field curves,
which is the only property a single-pass approximation does not have.

**§ 4.5 analysis — six for three nodes.** `JumpFlood` → `Distance`; `FloodBounds` + `FloodResidual` →
`FloodFill`; `EdgeDetect`. The floods are log₂(n) ping-ponged dispatches with a **ceiling that reports
truncation** rather than a `while` on the device. ⚠ **Both chains carry a coordinate in a half-float
and are therefore refused above 2048 texels** — doc 48 § D5 admits no 32-bit float format, a half is
exact on the integers only to 2048, and `TextureAnalysis.ExactExtent` refuses a larger image rather
than quantising one ([#690](https://github.com/Rikarin/Vixen/issues/690)).

**§ 4.6 surface — five for six nodes.** `HeightToNormal` · `NormalCombine` · `NormalTransform` ·
`Curvature` · `AmbientOcclusion`, and `Normal → Height` is the CPU exception above. ⚠ The **green
convention is derived and not chosen**: `MaterialSurface.rvn` decodes `2v − 1` with no flip anywhere
in the sampling path, and `v` increases downwards, so a height that rises as you move *down* the
picture is green below a half. `NormalCombine` is reoriented normal mapping and not whiteout, and
⚠ **the two agree exactly whenever either input is flat** — which is the case a lazy test reaches for
first, so the assertion tilts both.

**§ 4.7 placement — two.** `TileSampler` · `Splatter`, § D7's replacement for FX-Map. ⚠ **A scatter
written as a gather**: an instance is drawn by a texel asking which instances reach it, never by an
instance writing into the image, because a storage image has no blend hardware and no ordering
between invocations. The loop bounds are the trade FX-Map's recursion was refused for, and the CPU
side **refuses** a parameter that would exceed them rather than clamping it —
[#678](https://github.com/Rikarin/Vixen/issues/678)'s lesson, applied one node over.

## No `.spv` is committed here, and that is the one real departure

`Editor/Vixen.Editor.Host/Shaders` commits a `.spv` and a `.reflect.json` beside every `.rvn`, and
`CheckShaders`' `EditorSources` recompiles them and diffs the bytes. These are not that, and the reason
is a property of the target rather than a preference:

⚠ **A storage image's texel format is part of its *type*.** SPIR-V puts it in `OpTypeImage`, GLSL in
the layout qualifier, and Raven requires it on the declaration for exactly that reason — see
`Raven/README.md`'s storage-image paragraph. **And Raven's `[Permutation]` values are bool, int and
uint**, so a format cannot be one. A kernel that writes `rgba8` in one plan and `rgba16f` in the next is
therefore two modules, and there is no spelling of the source that makes it one.

So `TextureKernels.Variant` rewrites the single `[Format("…")]` each source carries, and the evaluator
compiles what comes out through the in-process Raven compiler — the same
`RavenEffectCompiler.FromSources` the shader graph's node previews use. Committing a module would mean
committing the `rgba16f` one and generating the other two anyway, which is a stale binary and a
generated one side by side.

**What is given up, and what replaces it.** `CheckShaders` proves a committed module matches the source
beside it; there is no committed module here to be stale. `TextureKernelTests` proves something
stronger: every kernel compiles, through the real front end, in every format a plan can ask it to write,
on a machine with no GPU and no Vulkan loader — where a device test skips and a gate that only runs on a
GPU reports success on the day it does not run.

⚠ **`ShaderSourceInventory` therefore does not ask for an `EditorSources` entry for these**, because it
asks only where a module is committed. That is deliberate and it is the walk's own rule, not a hole it
was not looking through.

## Shapes every kernel here keeps

**One compute entry point, `[ComputeShader(8, 8, 1)]`.** ⚠ The size is duplicated in
`TexturePlanEvaluator.GroupSize` because Raven puts it on the stage attribute and not in the
reflection, so a host still has to know it — and a kernel declaring sixteens against a host dispatching
eights leaves three quarters of every image unwritten, which on a fresh device usually looks like a
kernel that ran and produced black. `TextureKernelTests` asserts the two agree.

**Inputs are `Texture2D` read with `Load`, and the output is the one `RWTexture2D<float4>`.** A sampled
read converts whatever the storage was into four floats, which is what lets a plan feed an `R8` mask
into a kernel that computes in `rgba16f`. `BindingPlan` puts the uniform block at binding 0 and then the
textures **in declaration order**, and the evaluator binds an op's inputs positionally over them — so
the declaration order is the contract. ⚠ Nothing in the C# would notice a kernel declaring its
foreground before its background; the picture would simply be composited the wrong way round, which is a
perfectly plausible picture. `TextureKernelTests` writes the order down.

**Every tail invocation returns.** The dispatch is rounded up to whole groups, and storing outside a
storage image is undefined in both targets.

**Every tap is clamped to the *source's* dimensions, and a filter never wraps.** A blur that wrapped
would pull the opposite edge of the image into this one, which is the artefact a tileable graph exists
to avoid — and tileability is a property of what the *generators* draw, not something a filter can bolt
on afterwards. ⚠ The clamp is to the **source's** extent and never the target's: an op whose output is
a different size from its input is ordinary here, and a kernel that clamped to the image it writes
reads outside a smaller source, where what an implementation returns is not the edge.

⚠ **Two kernels break the no-wrapping half deliberately, and neither is a filter.** `Tile` wraps every
tap by construction — a tile's edge meets the opposite edge of the source, which is what makes it a
tile — and `Transform2D` takes a **tiling mode**, where 0 clamps, 1 wraps and 2 mirrors. Both are
§ 4.3 space nodes whose whole subject is where a coordinate lands, so for them the wrap is the
operation rather than an artefact. A third group — `Noise`, `TileSampler` and `Splatter` — wraps a
lattice or a cell index it *generates*, which is a different thing again: no tap into a source image
is wrapped there, and the seam it removes is the one in the pattern rather than one in an input.

**Every length arrives already scaled.** ⚠ `radius` is in the texels of the image being written, not in
texels at the base resolution: doc 48 § D8's rule lives on the plan and `TexturePlan.Resolve` is what
applies it. A kernel that scaled it itself would need to know the base, and then two places would have
to agree about what a half-resolution image is.

## Why the blend mode is a uniform and not a permutation

A permutation would specialise the branch away at the cost of one compiled module per mode per output
format — sixteen modes times three formats is forty-eight modules for a kernel whose body is eight
instructions. A texture bake is bandwidth-bound on the loads and the store; the branch is free and the
compilations are not.

## Why `Blur` is one axis

A radius-`r` box over a `w×h` image is `2r+1` taps per texel per axis rather than `(2r+1)²` in one
pass — at `r = 32` that is 65 taps against 4 225. **The plan is what separates it**: two ops with
`stepX`/`stepY` swapped, which also gives an artist a directional blur out of the same kernel.

⚠ **The outermost pair of taps is weighted by the fractional part of the radius rather than dropped.**
Without it a radius sweeping from 3.0 to 4.0 does nothing and then jumps, which reads as a slider with
steps in it — and it is also what would make § D8's bake-at-1K-against-4K comparison fail for a reason
that has nothing to do with resolution.

⚠ **`MaxTaps` is a correctness property and not a performance one.** A radius arriving as a NaN, or
as a number an artist typed four zeros into, would be a loop no invocation leaves — which on a GPU is a
device loss and a desktop that stops repainting, not a slow bake.

⚠ **But it budgets the *taps* and not the width, and the difference is § D8.** It was a
`clamp(radius, 0, 64)` applied to the radius *as it arrived* — that is, after `TexturePlan.Resolve`
had already scaled it into the written image's texels. A plan authored with a radius of 20 at 1K
resolves to 80 at a 4× bake and was silently clipped to 64, so **the 4× bake was a narrower filter
than the 1× one, with no message anywhere**
([#678](https://github.com/Rikarin/Vixen/issues/678)) — the two-year fuse
[#619](https://github.com/Rikarin/Vixen/issues/619) was opened to remove, relit one layer below the
fix, and invisible because the § D8 device test happened to pick a radius of 12. Past the budget the
same width is now covered by 64 taps spaced further apart: the box thins rather than narrowing, and
below the budget the spacing is exactly one and the filter is texel for texel what it always was. A
refusal on the plan would be better still and is not this kernel's to make; there is no
`TexturePlan.Validate` check that a resolved length fits the kernel that receives it.

## Why `Levels` is the kernel that carries the seed

A levels curve that lifts a narrow input range fills an 8-bit output with visible bands, and a bake is a
**file** — so the banding is permanent and nothing downstream removes it. One step of ordered noise
costs nothing and is invisible. That is what `dither` and `seed` are, and it is why the plan carries a
seed at all in M1: `TexturePlan.SeedFor` mixes the plan's seed with the op's index on the CPU, so two
levels nodes in one graph do not dither identically and a re-bake on the same machine is byte-identical.

⚠ **`seed` is declared as a `float`, and the hashing has already happened.** What a kernel needs of a
seed is that two ops disagree; carrying it as a float keeps these sources free of the integer-literal
and shift-operator spellings that differ between Raven's two backends. M2's noise generators may want a
`uint` and that is a change to them, not to the plan.

## The trap that eats one of these every time

⚠ **A newline ends a statement.** `sum = sum + w * Tap(...)` fits on one line here for that reason. An
expression continued on the next line is two statements and the second is discarded — silently, when
the continuation starts with `+`. `RVN1001` catches the trailing-operator form and some of the leading
ones; it does not catch all of them.

## Regenerating

Nothing to regenerate. `dotnet test Editor/Vixen.Editor.TextureGraph.Tests` compiles every kernel in
every variant, and a kernel that does not compile is a red test rather than a stale binary.

Licensed under Apache-2.0.
