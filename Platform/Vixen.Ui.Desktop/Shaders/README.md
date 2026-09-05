<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Shaders

The interface's eight modules, from one Raven source.

| Source | Shaders | Modules |
|---|---|---|
| `Ui.rvn` | `UiVertex`, `UiBox`, `UiText`, `UiSolid`, `UiImage`, `UiBlur`, `UiColour`, `UiMask` | one `.vert.spv`, seven `.frag.spv` |

## Why one vertex stage and seven fragment stages

`UiRenderer` takes one vertex module and a fragment module per pipeline, because the seven pipelines
read one vertex layout — two layouts would mean two buffers and two uploads to save sixteen bytes on
a vertex count in the thousands. A Raven shader carrying both stages would emit the vertex module
seven times over, and seven copies of one stage is seven chances for six of them to be wrong. So
`UiVertex` has only a vertex entry point and the other seven have only a fragment one.

**The stream declarations are the contract between them, and their order is the whole of it.** A
stream's location is its index in the shader's declaration list, so `UiVertex` writing
`uv, colour, shape` and `UiBox` reading `uv, colour, shape` arrive at the same three numbers without
either naming one. A stream a fragment stage does not read is still declared, and has to be: only a
stage's *reads* become interface variables, so declaring one costs nothing and keeps the ones after
it where they are. `UiSolid` never touches `uv`, and if it stopped declaring it, `colour` would move
to location 0 and the stage would read texture coordinates as a colour.

## Where these came from

These were hand-written GLSL until 2026-08-23, and they were committed three times — here, in the
`vixen-app` template, and in `Vixen.Graphics.Golden.Tests` — each compiled by whatever `glslc` was on
the machine of whoever last touched them. `SharedUiShaderTests` existed to police that, and it was
written *after* two of the three copies had already lost the whole shadow path: the struct is shared
and reserves `axis.z` for a shadow's blur, so the stale copies declared the field and never read it,
and a shape asking for a soft shadow got a hard-edged box at full opacity. Nothing rendered blank.

The editor had been driven from a Raven `Ui.rvn` for some time. This is that file with the three
compositing stages — `UiBlur`, `UiColour`, `UiMask` — ported into it, so the interface now has one
Raven source and one hand-maintained GLSL copy: the golden suite's, which is what its reference
images were rendered with.

⚠ **That last copy is the gap worth naming.** The reference images were rendered with GLSL and every
shipping application renders with these modules, and nothing compares the two. They are two
implementations of one specification in two languages, so no byte comparison can; the only real check
is a golden image rendered through each. The end state is that suite driving these modules, which
regenerates every reference image in it and belongs on its own.

## The three things a host has to get right, and how each is checked

**The vertex attribute locations are 3 to 6, not 0 to 3.** Raven's `StreamPlan` locates a stage's own
parameters *after* the shader's streams, and `Ui.rvn` declares three. `UiShaderLibrary` reads them out
of `UiVertex.reflect.json` through `Vixen.Shaders.Generators` rather than writing them down, so a
stream added here moves them and no C# has to notice. ⚠ A wrong location is not a validation error:
the pipeline binds nothing to that attribute and the stage reads whatever the driver left there.

**The three compositing stages' push constants start at 16, and each pads to get there.** A Vulkan
push-constant block is shared by every stage of a pipeline, and `UiRenderer` writes the fragment half
at offset 16 — which the GLSL said with `layout(offset = 16)`. Raven has no such spelling:
`ReflectionBuilder.BuildPushConstants` emits a shader's whole block from offset zero. So `UiBlur`,
`UiColour` and `UiMask` each declare `reserved: float4` first, standing where the projection is. ⚠ A
stage that forgot it would read the viewport scale as its kernel or its red row.

**The pipeline layout is one range for both stages.** It used to be two, disjoint — vertex `[0, 16]`
and fragment `[16, 112]` — mirroring the GLSL, and a Raven block declared from zero is outside it.
Widening the fragment range to `[0, 128]` and leaving the vertex one alone is the obvious fix and is
*also* invalid: two ranges may overlap, but every `vkCmdPushConstants` covering a byte must name every
stage whose range covers it. One `Vertex | Fragment` range has neither problem.

`Vixen.Ui.Desktop.Tests`' `ShaderReflectionTests` asserts all three against the committed reflection.
It was sabotage-checked: dropping `reserved` from `UiBlur` fails the offset theory.

## Regenerating

The gate does it:

```bash
./build.sh CheckShaders --update-shaders
```

`CheckShaders` recompiles every Raven source whose modules are *committed* — this one included, since
2026-08-23 — and fails when a committed module differs from what the compiler produces. It fails the
other way too: a `.spv` or `.reflect.json` in this directory the target produces nothing for is a
module nothing recompiles, and is reported rather than skipped. So a `.rvn` edited without recompiling
cannot sit in a commit, and `ci.yml`'s `checks` leg is what says so on a pull request.

⚠ **"Every source" is now literal.** The target used to name four sources in a hand-written list, so
"every" meant "every one somebody remembered". It walks the tree instead: a `.rvn` with a `.spv` for
one of its shaders beside it *is* an entry, discovered on the next run, and a source added here needs
no edit to `build/Build.Shaders.cs` to be gated.

To do one by hand, from the repository root:

```bash
dotnet run --project Raven/Vixen.Raven.Cli -- compile --target spirv Platform/Vixen.Ui.Desktop/Shaders/Ui.rvn Platform/Vixen.Ui.Desktop/Shaders/ --emit-reflection
```

⚠ **`--emit-reflection` is not optional.** The `.reflect.json` is what `Vixen.Shaders.Generators`
reads, and what it tells `UiShaderLibrary` is where each vertex attribute lives. Regenerating without
it leaves the host binding against the previous source's numbering. It is also what
`UiShapeLayoutTests` compares `Vixen.Ui.Rendering.UiShape` against field by field, so a stale
`UiBox.reflect.json` is a host being told the wrong struct offsets by the only artefact that knows
them.

`spirv-val --target-env vulkan1.2` over the eight `.spv` is worth running after: Raven's SPIR-V is
checked against the validator in its own tests, but these are the modules an application actually
loads.
