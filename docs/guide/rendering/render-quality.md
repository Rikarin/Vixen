---
title: Render quality presets
slug: rendering/render-quality
kind: guide
area: Rendering
summary: The quality waterfall behind the Standard Frame's tiers — engine defaults, a project's RenderQuality.vxpreset and per-document overrides, folded per parameter into the numbers the expansion consumes.
api: [T:Vixen.Rendering.PostFx.RenderQualityAsset, T:Vixen.Rendering.PostFx.QualityTierOverrides, T:Vixen.Rendering.PostFx.ResolutionQuality, T:Vixen.Rendering.PostFx.ShadowQuality, T:Vixen.Rendering.PostFx.GlobalIlluminationQuality, T:Vixen.Rendering.PostFx.ReflectionQuality, T:Vixen.Rendering.PostFx.PostFidelityQuality, T:Vixen.Rendering.PostFx.LightQuality, T:Vixen.Rendering.PostFx.GeometryQuality, T:Vixen.Rendering.PostFx.VegetationQuality, T:Vixen.Rendering.PostFx.TextureQuality, T:Vixen.Rendering.PostFx.CullingMode, T:Vixen.Rendering.PostFx.FxaaPreset, T:Vixen.Rendering.PostFx.ResolvedQuality, T:Vixen.Rendering.PostFx.RenderQuality, T:Vixen.Rendering.Compositor.QualityTier]
tags: [rendering, presets, scalability, quality]
since: 0.1
status: experimental
related: [rendering/standard-frame, rendering/choosing-a-frame, rendering/post-processing]
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
`SurfaceCacheSize`, `TraceScale`, the `LightQuality` capacities, `VirtualGeometry`, `LodBias`, all
of `VegetationQuality` and all of `TextureQuality`): they map to systems the compositor does not
construct today, and they land in the asset first so a project's tiers do not change shape when
their consumers learn to read them. `VegetationQuality` is the newest of these — the terrain,
grass and foliage libraries have landed with exactly the parameters its fields name (the scatter
kernels' density scales, `GrassResidency`'s cell capacity, `TerrainLodRanges.NearRange`), and the
seam that constructs those renderers from a frame is what remains owed.

## Examples

A project preset that sharpens High's sun and drops Low to a three-quarter render scale:

```yaml
# RenderQuality.vxpreset
high: !QualityTierOverrides
  shadows: !ShadowQuality { cascadeResolution: 4096 }
low: !QualityTierOverrides
  resolution: !ResolutionQuality { renderScale: 0.75 }
```

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
