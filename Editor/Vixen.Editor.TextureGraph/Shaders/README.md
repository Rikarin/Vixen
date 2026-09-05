# Shaders

The texture graph's atomic kernels, in Raven. Three of the forty-four
[doc 48 § D5](../../../docs/plan/48-material-authoring.md) plans, and the shape the rest take.

| Kernel | Reads | Writes | What it is |
|---|---|---|---|
| `Blend` | `background`, `foreground` | one image | Eight of the sixteen modes, under an opacity |
| `Blur` | `source` | one image | One axis of a box blur, with a fractional radius |
| `Levels` | `source` | one image | Input range, gamma, output range, and a dither |

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

**Every tap is clamped to the source's edge, never wrapped.** A blur that wrapped would pull the
opposite edge of the image into this one, which is the artefact a tileable graph exists to avoid — and
tileability is a property of what the *generators* draw, not something a filter can bolt on afterwards.

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

⚠ **`MaxRadius` is a correctness property and not a performance one.** A radius arriving as a NaN, or
as a number an artist typed four zeros into, would be a loop no invocation leaves — which on a GPU is a
device loss and a desktop that stops repainting, not a slow bake.

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
