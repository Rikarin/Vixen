---
title: Render quality presets
slug: rendering/render-quality
kind: guide
area: Rendering
summary: The quality waterfall behind the Standard Frame's tiers — engine defaults, a project's RenderQuality.vxpreset and per-document overrides, folded per parameter into the numbers the expansion consumes.
api: [T:Vixen.Rendering.PostFx.RenderQualityAsset, T:Vixen.Rendering.PostFx.QualityTierOverrides, T:Vixen.Rendering.PostFx.ResolutionQuality, T:Vixen.Rendering.PostFx.ShadowQuality, T:Vixen.Rendering.PostFx.GlobalIlluminationQuality, T:Vixen.Rendering.PostFx.ReflectionQuality, T:Vixen.Rendering.PostFx.PostFidelityQuality, T:Vixen.Rendering.PostFx.LightQuality, T:Vixen.Rendering.PostFx.GeometryQuality, T:Vixen.Rendering.PostFx.VegetationQuality, T:Vixen.Rendering.PostFx.TextureQuality, T:Vixen.Rendering.PostFx.CullingMode, T:Vixen.Rendering.PostFx.FxaaPreset, T:Vixen.Rendering.PostFx.ResolvedQuality, T:Vixen.Rendering.PostFx.RenderQuality, T:Vixen.Rendering.Compositor.QualityTier]
tags: [rendering, presets, scalability, quality]
since: 0.1
status: preview
related: [rendering/standard-frame, rendering/choosing-a-frame, rendering/post-processing, rendering/volumetric-fog, rendering/texture-streaming, editor/frame-panel]
---

## What it is

`RenderQualityAsset` is a project's `RenderQuality.vxpreset`: per-tier overrides of the engine's
quality table, doc 39's scalability layer as an asset rather than an ini. Four tiers — Low, Medium,
High, Epic (`QualityTier`, which lives beside the compositor schema because the *host* states it) —
each carrying up to nine groups of knobs: `ResolutionQuality`, `ShadowQuality`,
`GlobalIlluminationQuality`, `ReflectionQuality`, `PostFidelityQuality`, `LightQuality`,
`GeometryQuality`, `VegetationQuality` and `TextureQuality`. Every field is nullable on the volume
model's terms: a tier
names only what it overrides, and an unset field falls through the waterfall — an unset field is
not a zero, exactly as on `PostProcessSettings`.

The waterfall is Unreal's, as assets: `RenderQuality.EngineDefaults` (the engine's own complete
table, expressed as this same asset type) → the project's preset → a per-document overlay on
`StandardFrameAsset.Preset` — folded **per parameter** by `RenderQuality.Resolve`, whose product is
`ResolvedQuality`, the flat struct of decided numbers the Standard Frame expansion consumes. Which
tier's column is read is the document's `quality:`, or, when the document declines, the platform's
pick in `GraphicsOptions.Quality`.

## What it is for

Everything quality-shaped lives in this one asset, and the boundary with the look profile is one
rule: look changes the intent, quality changes only the fidelity and cost of the same intent. Bloom
threshold is look; bloom pyramid levels are quality. DoF aperture is the camera's; DoF sample count
is quality. A runtime settings screen becomes one assignment — `GraphicsOptions.Quality` — with no
document edits, and a project that thinks Epic's shadows are still too coarse overrides exactly
`cascadeResolution` in exactly the Epic tier and inherits every sibling number from the engine.

The fold refuses a hole by name rather than reading it as zero: the engine layer is complete by
construction, so a resolved field can never be an accidental default — a zero here would be a
shadow distance of nothing and an atlas of no tiles, silently.

## Using it

The host that constructs `PostEffectFactory` hands it the loaded project preset; the platform's
tier travels through `CompositorBuilder.Quality`, which `AppGraphics` sets from
`GraphicsOptions.Quality`:

```csharp no-compile="the host wires this in Game.OnConfigure; see GraphicsOptions"
options.Quality = QualityTier.Medium;                       // the platform's pick
options.Factories.Add(new PostEffectFactory { Preset = projectPreset });
```

`Resolve` is a pure function and takes assets, not addresses — resolving an address from inside the
document transform would put content IO inside a build that must stay pure. Loading the
`.vxpreset` by address on the host's behalf is a later increment; until then the host loads it and
hands the asset over.

Some entries are carried, not yet consumed, and say so on their doc comments (`DfaoSamples`,
`SurfaceCacheSize`, `TraceScale`, `LightQuality.maxLights`, `VirtualGeometry`, `LodBias` and
`TextureQuality.ParticleBudgetScale`): they map to systems the compositor does not construct today,
and they land in the asset first so a project's tiers do not change shape when their consumers
learn to read them.

`LightQuality.maxLightsPerObject` is consumed, and it takes two steps rather than one because the
number is agreed with a *shader*. `AppGraphics` hands the resolved value to
`ForwardLightingRenderFeature.MaxLightsPerObject`, which sizes the per-object block the feature
writes; `CompositorBuilder` then publishes that same number as the `MaxLights` permutation of every
shading pass the document declares, because `ClusteredShading.rvn` sizes `lights[MaxLights]` from
it. Both halves or neither: the shorter of the two wins in silence, so a host that raised its
budget without publishing it shades with the shader's declared sixteen, and a tier asking for four
that reached neither drew eight — which is what every tier did before the wire existed. It is the
same shape as `cascadeCount:`, one array along, and `MaxLightsDeviceTests` measures both
directions. A game that wants its own budget sets the feature's property after the host has started
and reloads the frame document, which republishes the permutation from it.

`TextureQuality`'s other two are consumed on the vegetation's terms, by the same host and from the
same single fold: `streamingPoolMegabytes` and `mipBias` become `WorldRenderer.Textures`, sized
before the texture source is mounted because a pool that could be resized afterwards would not be a
budget.

`VegetationQuality` is consumed, by hand-off rather than by reference. `Vixen.Rendering.Terrain`
cannot see this assembly — the dependency runs the other way — so `TerrainFactory` declares its own
plain-numbered `TerrainVegetationQuality`, and `AppGraphics` folds a resolved tier into
`TerrainFactory.Vegetation` for every terrain factory it finds in `GraphicsOptions.Factories`. That
fold is the hand-off: registering the factory is still the whole installation, and a game that
filled the budgets itself is left alone. All seven entries land — the two density scales, the two
cull scales, the grass and foliage cell counts and the near range — plus
`terrainStreamingMegabytes`, which becomes `TerrainStreamer`'s byte budget.

A `!Terrain` node may state any of those numbers directly, per field, and a written value out-votes
the factory's tier while its siblings still fall through:

```yaml
- !Terrain
  name: Ground
  foliageCellBudget: 96      # this document has decided
  # every other budget is the host's resolved tier
```

⚠ **A knob added to `VegetationQuality` is wired only when both halves are added**:
`TerrainVegetationQuality` must grow the same field *and* `AppGraphics`' fold must assign it.
Either one missing and the number is carried the whole length of the waterfall and dropped at the
last step, which is what happened to the foliage budgets. `TerrainNodeTests` checks the two records
against each other so the omission fails a test rather than a frame.

The terrain fold sees the whole waterfall, a `!StandardFrame`'s own `quality:` and inline `preset:`
included. It has to be read off the document *before* the build: the expansion replaces the frame
node as the compositor builds, so a host asking afterwards would be asking a document that no
longer says anything — which is why, for a while, a frame naming its own tier moved the post chain
and not the ground. `PostEffectFactory.QualityOf(document, fallback, project)` is that reading, and
it is the same fold the expansion performs rather than a second one that agrees today. The
`!Terrain` node's own scalars are a further document-level vote and out-vote the result per field.

`GrassBladesPerCell` exists on the terrain-side record and in no tier: it is the scatter dispatch's
shape rather than a budget, so a document or the game sets it.

## Examples

A project preset that sharpens High's sun and drops Low to a three-quarter render scale:

```yaml
# RenderQuality.vxpreset
high: !QualityTierOverrides
  shadows: !ShadowQuality { cascadeResolution: 4096 }
low: !QualityTierOverrides
  resolution: !ResolutionQuality { renderScale: 0.75 }
```

⚠ **`renderScale` is not finished, and every shipped tier sets it to 1 for that reason.** Declaring
the scene planes at a fraction was only ever half of a render scale; the other half is that
everything reading them measures in *their* grid rather than the window's. That half is now true of
the neighbour taps (FXAA, sharpen, the outline, TAA's clamp, motion blur, the occlusion march), the
SMAA chain, the reduced depth pyramid, the screen-space reflection march, the screen-probe lattice
and the visibility buffer. It is **not** yet true of the post chain's intermediate targets, which
are declared at the frame's size — so the upscale lands at the first effect after shading and every
pass below it still costs full resolution, which is most of what the scale was meant to buy. TAA's
history is allocated there too, and the camera's Halton jitter is aimed at the window, so a scaled
frame accumulates against a native history at offsets that are a fraction of a rendered pixel.

Until those land, a project setting this below 1 buys a fraction of the saving and pays for it in a
frame nothing reports as wrong — the upscale itself is a bilinear resample in the tonemap's linear
sampler and nothing more, because the engine has no upscaler yet.

Resolving and reading the fold in code — what the expansion does internally, and what a test
asserts against:

```csharp compile
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;

public static class QualityFold {
    public static ResolvedQuality ForHigh() {
        var project = new RenderQualityAsset {
            High = new() { Shadows = new() { CascadeResolution = 4096 } }
        };

        // The override lands; every sibling falls through to the engine's High column.
        return RenderQuality.Resolve(QualityTier.High, project);
    }
}
```

## See also

- [The Standard Frame](standard-frame.md) — the node whose expansion consumes the resolved
  numbers, and the `quality:` / `preset:` knobs that pick the column and stack the top layer.
- [The post-processing node kinds](post-processing.md) — the nodes the fidelity group's numbers
  land on.
- `docs/plan/39-standard-frame-and-render-presets.md` — the design, and the waterfall's Unreal and
  Unity ancestry.
