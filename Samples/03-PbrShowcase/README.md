# 03 — PBR Showcase

Twenty-five spheres: metallic up the grid, roughness across it, under one directional light, rendered
to an HDR target and tone-mapped onto the swapchain.

```bash
dotnet run --project Samples/03-PbrShowcase
```

`--vixen-frames N` renders N frames and exits, which is how CI proves the whole stack starts, runs
two passes and stops without a validation error or a hang.

## What it is for

The arrangement is the point. Down the grid a dielectric becomes a metal; across it a mirror becomes
a diffuse surface. Any mistake in the microfacet model shows up as one row or one column behaving
unlike its neighbours — which is a far better test of a BRDF than a single object under a single
light, where almost anything looks plausible.

| Axis | What changes | What to look for |
|---|---|---|
| Up | metallic, 0 → 1 | The diffuse lobe disappears and the reflection takes the base colour |
| Across | roughness, 0 → 1 | One tight highlight spreads into the whole lit hemisphere |

## How it is built

Two passes. The scene renders into `Rgba16Float` and a full-screen pass tone-maps that onto the sRGB
swapchain. Rendering straight to the swapchain would clamp every specular highlight to white at the
moment it was written, and the difference between a smooth metal and a rough one would vanish with
it.

Both are declared to `Vixen.Graphics.RenderGraph`, which derives the barrier between them from the
fact that the second pass says it reads what the first wrote. The HDR target and the depth buffer are
graph transients — the graph owns their lifetimes and their aliasing — which is why the tonemap pass
takes its descriptor set from a `DescriptorAllocator` rather than owning one: a set written once at
start-up would point at whatever the pool assigned on the first frame.

Per-object data goes through push constants: a model matrix and a material vector, eighty bytes,
inside the RHI's guaranteed 128 and therefore inside what every backend can carry. A real renderer
does not do this — `Vixen.Rendering`'s `TransformRenderFeature` packs transforms into one dynamic
uniform buffer and binds it with an offset per draw, which scales to thousands of objects. Twenty-five
spheres do not need that, and the push-constant path keeps the binding story to one page.

### Why it does not use the compositor

`Samples/03` drives the RHI and the render graph directly, like `Samples/01`, rather than going
through `Vixen.Rendering`'s `GraphicsCompositor`. That is deliberate for a sample about *shading*:
the compositor's node graph, its render features and its effect system are each worth a sample of
their own, and putting all three here would bury the dozen lines that are actually about the BRDF.

The shaders are committed GLSL and SPIR-V beside it rather than Raven modules, for the same reason
`Samples/01`'s are: the RHI never parses shader source and Raven is not yet wired into the build
([docs/plan/07](../../docs/plan/07-raven-shader-pipeline.md)). Regenerating one is
`glslc Shaders/pbr.frag -o Shaders/pbr.frag.spv`.

## What it does not have yet

[docs/plan/14](../../docs/plan/14-roadmap.md) Phase 5 asks this sample for materials, image-based
lighting, shadows and post FX. It has the first and a slice of the last. Stated plainly so that
nobody mistakes a placeholder for the design:

- **No image-based lighting.** The ambient term is analytic: the constant-radiance environment that a
  prefiltered radiance cube and a BRDF integration LUT integrate to. It is enough to keep the unlit
  side of a sphere from being black and to make a metal's rim behave differently from a dielectric's,
  and it is not enough to show a *reflection* — which is the thing IBL is for. Real IBL needs an
  importer that produces the cube and the LUT, which is content-pipeline work.
- **No shadows.** Nothing casts. A shadow atlas belongs to the renderer rather than to a sample.
- **One post effect.** Exposure and a filmic curve. `Vixen.Rendering.PostFx` has bloom, TAA, FXAA and
  the rest; wiring them here means going through the compositor, which is the sample this is
  deliberately not.

## Two bugs this sample had, and what found them

Both worth recording, because both produced a program that started, ran, and reported nothing wrong.

**The matrix was on the wrong side.** `Conventions.md` fixes the row-vector convention — `mul(v, M)` —
so the shader was written `vec4(position, 1.0) * push.model`. That is wrong, and ADR-003 explains why
in a sentence that is easy to read past: the host stores matrices row-major and GLSL reads a `mat4`
column-major, so the matrix the shader sees is the *transpose* of the one that was written, and
`M * v` is therefore exactly `v * M_host`. Written the other way round, every vertex landed outside
the clip volume and the sample rendered an empty frame — with no validation error, because nothing
about it is invalid.

**The V coordinate was not inverted.** Clip `+y` is up and texel row zero is the top, so a full-screen
pass has to flip V when it turns a clip position into a texture coordinate. Without it the tone-mapped
image is upside down. `Vixen.Graphics.Golden.Tests/Shaders/fullscreen.vert` carries a comment saying
exactly this — "the single most common way a post pass is wrong and the easiest to see" — which is
true, and which is no help at all if nobody looks at the picture.

Neither was caught by the sample running cleanly for five frames. Both were caught in a minute by
rendering one frame offscreen and opening the PNG. **Look at the output.**
