---
title: Map baking
slug: engine/map-baking
kind: guide
area: Engine
summary: Casting the output's normal at the source to fill an atlas with a normal map and a displacement map, on the CPU, with no device anywhere.
api: [T:Vixen.Geometry.Remeshing.MapBaker, T:Vixen.Geometry.Remeshing.BakeSettings, T:Vixen.Geometry.Remeshing.BakedMaps, T:Vixen.Geometry.Remeshing.BakeSpace]
tags: [geometry, retopology, remesh, bake, normal-map, displacement, atlas]
since: 0.1
status: preview
related: [engine/retopology, engine/attribute-transfer, engine/uv-packing, core/triangle-tree]
---

## What it is

`MapBaker.Bake` takes a high-resolution source, a remeshed output that already has texture
coordinates, and fills that output's atlas with two maps: a normal map and a signed displacement map.
`BakedMaps` is the pixels and what was measured about them; `BakeSettings` is the size, the gutter and
how far a ray looks.

There is no device, no shader and no file. A bake returns arrays.

## What it is for

This is where a retopology pipeline's arithmetic closes. Five thousand quads plus a 2K normal map is
smaller than four million triangles, looks better under a moving light, subdivides, and can be rigged.
Retopology *without* baking is a downgrade; retopology *with* baking is the pipeline.

It runs at import time inside a content build, which is why it is CPU work with no graphics reference
— the same reason the whole remesher is under `Core/`. The two things in this engine that already bake
a mesh into a texture are GPU-only and project along an *axis* rather than through a parameterization,
so neither answers the question an atlas asks.

⚠ **The bake returns pixels and writing them is the caller's job.** `Core/` is under the virtual-path
rule, so an asset compiler, a CLI and an editor each write these where their own conventions say.

## Using it

```csharp compile
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

public static class Baking {
    public static BakedMaps Run(EditMesh source, EditMesh quads) =>
        MapBaker.Bake(
            source,
            quads,
            new BakeSettings { Resolution = 2048, Gutter = 4, SearchRadius = 0.05f }
        );
}
```

`Resolution` is required and both maps come back that square, row-major from the bottom-left. The
target must already carry a texture-coordinate layer — [the atlas from the patch
layout](engine/retopology) is the usual source of one — and a target without gets an
`ArgumentException` rather than a blank bake.

⚠ **`SearchRadius` is a fraction of the source's bounding-box diagonal and never a distance.** A ray
cage measured in metres is a claim about how big a model is: a bake tuned on a character silently
finds nothing on the same character exported in centimetres, every texel takes the fallback, and the
map still looks plausible.

### The ray is cast both ways

Casting only outward loses every part of the source the output enclosed, which on a smoothed remesh of
a noisy surface is about half of it. Casting only inward loses the other half. A cage mesh is the
production answer to the ambiguity and it is a thing an artist authors; the nearer of two opposed hits
is the answer available to a content build with nobody watching.

⚠ **A texel that finds nothing falls back to the closest point rather than to a default.** A default
normal in the middle of a chart is a flat patch that reads as a modelling error. `BakedMaps.Missed`
counts them: a handful is a thin feature the output cut through, and a large fraction means the search
radius is smaller than the deviation the remesh actually produced.

⚠ **A cage lying exactly *on* the source has every ray rejected at its own origin** — correctly, since
a hit at zero distance is the origin — so the whole bake takes the fallback. If `Missed` is close to
`Covered`, that is usually why.

### Coverage is conservative, and it has to be

A texel is covered when the chart triangle touches its *square*, not when it contains its centre. The
half-space rule a rasterizer uses is a pixel-centre rule, which is correct for a framebuffer — a
triangle covering no centre covers no pixel by definition — and wrong for an atlas, because the
outermost row of texels along every chart is exactly the row whose centres the chart misses. Those
texels read as background, and a hole at a chart's edge survives dilation, since dilation only fills
what nothing claimed.

⚠ **Conservative coverage means a texel's centre is regularly outside the triangle**, so the position
sampled there is clamped onto the triangle rather than extrapolated. Without the clamp the outermost
row's ray origins are off the surface.

### The gutter never writes over another chart

Content is rasterized in one full pass and dilated in a second. Two charts whose texels abut is the
common case rather than the exotic one — the packer's whole job is to make it common — and a dilation
interleaved with the rasterization would let whichever chart was drawn first bleed over the second
chart's content, which shows up as a wrong-coloured stripe at mip 3 and gets blamed on the sampler.

⚠ **The gutter only writes where `Coverage` is false**, and `Coverage` is never written by the
dilation. Each round also commits after it finishes rather than as it goes: writing in place would let
a texel filled early in the scan seed one later in the same round, so the gutter would reach further
right and upward than left and downward — a lopsided halo.

`Gutter` defaults to four, the same as the packer's `Margin`, and that is not a coincidence: the
gutter has to reach at least as far as the packer's spacing or the two disagree about where a chart
ends.

### Displacement is in the model's own units

⚠ **Deliberately not normalised.** A displacement map quantized into `[0, 1]` needs a scale stored
beside it or it means nothing, and half of the ways that goes wrong are the pixels and the scale being
written by different code. `DisplacementRange` is the largest absolute value the bake actually found,
and it is what a caller quantizes with. Positive is outward along the output's normal — the source
stands proud of the cage.

## Examples

Baking after a remesh, and refusing the result if the cage was too far from the source:

```csharp compile
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

public static class Pipeline {
    public static BakedMaps? Run(EditMesh generated) {
        var quads = Remesher.Remesh(generated, new RemeshSettings { TargetQuads = 5000 }, out var report);

        if (!report.IsAllQuad) {
            return null;
        }

        var maps = MapBaker.Bake(generated, quads, new BakeSettings { Resolution = 2048 });

        // More than a few percent on the fallback means the cage does not fit the source.
        return maps.Missed * 20 < maps.Covered ? maps : null;
    }
}
```

Quantizing the displacement into bytes, with the scale that makes them mean something:

```csharp compile
using Vixen.Geometry.Remeshing;

public static class Quantizing {
    public static (byte[] Pixels, float Scale) Run(BakedMaps maps) {
        var pixels = new byte[maps.Displacement.Count];
        var range = maps.DisplacementRange > 0f ? maps.DisplacementRange : 1f;

        for (var index = 0; index < pixels.Length; index++) {
            var signed = maps.Displacement[index] / range;

            pixels[index] = (byte) System.Math.Clamp(((signed * 0.5f) + 0.5f) * 255f, 0f, 255f);
        }

        return (pixels, range);
    }
}
```

Asking for object space, which is what to compare against when a tangent-space bake looks wrong:

```csharp compile
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

public static class Comparing {
    public static BakedMaps Run(EditMesh source, EditMesh quads) =>
        MapBaker.Bake(
            source,
            quads,
            new BakeSettings { Resolution = 1024, Space = BakeSpace.Object }
        );
}
```

Object space is unambiguous and undeformable — worth having for a static prop, and worth reaching for
when a tangent-space map is wrong, because it takes the handedness convention out of the question.
Tangent space is the default because it survives the mesh being deformed, which for a skinned
character is the entire point of baking one.

## See also

- [Retopology settings and reports](engine/retopology) — where the atlas being baked into comes from.
- [Attribute transfer](engine/attribute-transfer) — the other half of stage seven.
- [UV packing](engine/uv-packing) — the margin rule the gutter has to agree with.
- [Triangle tree](core/triangle-tree) — the rays and the closest-point fallback both go through it.
