---
title: Map baking
slug: engine/map-baking
kind: guide
area: Engine
summary: Casting the output's normal at the source to fill an atlas with a normal map, a displacement map and seven more mesh maps, on the CPU, with no device anywhere.
api: [T:Vixen.Geometry.Remeshing.MapBaker, T:Vixen.Geometry.Remeshing.BakeSettings, T:Vixen.Geometry.Remeshing.BakedMaps, T:Vixen.Geometry.Remeshing.BakeSpace, T:Vixen.Geometry.Remeshing.MeshMaps, T:Vixen.Geometry.Remeshing.BakeMask, T:Vixen.Geometry.Remeshing.BakeParts, T:Vixen.Geometry.Remeshing.MeshMapKind, T:Vixen.Geometry.Remeshing.MeshMapPlane, T:Vixen.Geometry.Remeshing.MeshMapPlanes]
tags: [geometry, retopology, remesh, bake, normal-map, displacement, atlas, ambient-occlusion, curvature, mesh-maps, cage, hard-edge, supersampling]
since: 0.1
status: preview
related: [engine/retopology, engine/attribute-transfer, engine/uv-packing, core/triangle-tree, editor/mesh-map-assets]
---

## What it is

`MapBaker.Bake` takes a high-resolution source, a remeshed output that already has texture
coordinates, and fills that output's atlas with a normal map and a signed displacement map — and, on
request, seven more measurements at the same texels: ambient occlusion, a bent normal, curvature,
thickness, position, world normal and an id. `BakedMaps` is the pixels and what was measured about
them; `BakeSettings` is the size, the gutter, how far a ray looks and which maps to fill.

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
`MeshMapPlanes.Of` turns a bake into planes a writer can take at any depth: the measurement itself for
a float file, and `MeshMapPlane.Unit` for an integer one. See [deep images](core/deep-images).

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

### A hard edge does not seam

A hard edge is two normals on one point, and each side is its own chart. Rays cast along the corner's
own normal leave a wedge of the source nothing strikes and show the two sides of the edge different
surfaces, which is the seam a hard edge is known for.

⚠ **So the ray follows the direction averaged over every corner on the point, and the tangent frame
still uses the corner's own normal.** The averaged direction is continuous across the edge: both charts
cast the same ray and find the same surface, and each then expresses it in the frame that will shade
it. On a smooth surface the corners agree and nothing changes. Corners are matched by coordinates and
not by position index, because a file splits a vertex for a hard edge and a weld is an importer's to
do. `ModelGeometry.Flatten` is what reads a file with its normals intact.

### Cages, skew and parts

`BakeSettings.Cage` is a mesh with the target's faces in the target's order, moved outward. A ray then
runs from the cage through the target and on for `SearchRadius` behind it, and the first thing it
strikes wins, so an artist decides what a ray can reach by moving the cage. A cage with another layout
is refused, because its points have no partner.

⚠ **Rays near a hard edge are slanted, and so is what they bake.** A bolt on a flat face next to an edge
comes out sheared towards it. `BakeSettings.Skew` leans the rays back towards the target's own normal —
one is the old behaviour, with the seam — and `BakeSettings.SkewMap` says where, so the lean can be
painted in over the bolt and left out at the edge.

`BakeSettings.Parts` matches parts of the target to parts of the source, so that two parts modelled close
together do not strike each other's rays and nothing has to be exploded apart by hand. `BakeParts.ByName`
makes one from names: `bolt_high` and `bolt_low` are the same part. ⚠ **Occlusion ignores it on purpose**:
a neighbouring part stays out of the normal map and still shades this one.

### Supersampling, threads and what they do not change

`Supersampling` divides each texel along each side, so two is four probes. ⚠ **`OcclusionSamples` stays a
count per texel**: the one stratified set is dealt out between the samples, so occlusion costs the same
at any setting. A sample that falls off the triangle owning its texel asks the second triangle there,
and is clamped only where three meet.

⚠ **The atlas does not depend on the thread count.** Triangles claim texels first, the lowest index
winning; each row is then baked on whichever thread reaches it, into its own texels, and totals are
sums and maximums, which have no order. `Threads` limits how many, which is about what else the machine
is doing, and never about the result. Cancellation and progress are per atlas row.

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

## The seven mesh maps

`Maps` asks for them and it is empty by default, because three of them cast rays. Each is a different
measurement at a texel whose surface point, normal and tangent frame the raster already handed over —
a map that was not asked for comes back `null` rather than as an array of zeroes, so "not requested"
and "fully occluded" are not the same answer.

| `MeshMaps` | What it holds |
|---|---|
| `AmbientOcclusion` | The unoccluded fraction of a cosine-weighted hemisphere. One is open sky, zero is sealed |
| `BentNormal` | The average unoccluded direction, in `Space` alongside `Normals` |
| `Curvature` | Mean curvature, in reciprocal model units, with `CurvatureRange` beside it |
| `Thickness` | The occluded fraction of the same hemisphere, turned through the surface |
| `Position` | The surface point, each axis `[0, 1]` across the source's bounding box |
| `WorldNormal` | The source's normal, unrotated and independent of `Space` |
| `Ids` | The source's material or island index as an `int`, `-1` where there is none |

⚠ **Every one of them is measured at the *source*'s point and about the source's normal, never the
cage's.** The cage is a few thousand quads that deliberately do not carry the geometry doing the
occluding; an occlusion measured on it is a picture of the cage, which looks like a smoothed version
of the right answer and is not one.

⚠ **The occlusion, the bent normal and the thickness are one hemisphere and not three.** The bent
normal is the average of the directions the occlusion found unblocked, and the thickness is those same
directions reflected through the tangent plane — so the three cost what one costs, and a bent normal
always agrees with the occlusion beside it. `OcclusionSamples` is the ray count per texel and the
estimator's error falls as its square root; `OcclusionRadius` is how far an occluder still counts, as a
fraction of the diagonal.

⚠ **`Thickness` is a fraction, not a distance**, and it saturates at `OcclusionRadius` — a part thicker
than the rays reach reads as fully enclosed. Measuring the inside of a closed shape wants a radius of
one or more.

⚠ **`Curvature` is one over a length, so it moves with the model's scale, deliberately.** A sphere of
radius *r* reads `1/r`; the same sphere modelled a hundred times larger reads a hundredth of it. Every
other curvature in this library multiplies by the diagonal to give a dimensionless number for a
threshold to compare against — a map is quantized rather than compared, and `CurvatureRange` is the
scale that goes beside the pixels for the same reason `DisplacementRange` does. ⚠ An **open rim reads
zero**: the operator wants a closed one-ring and the missing half of one is not a measurement, so
without the refusal every sheet and cut-out in a project bakes a bright border that no generator can
tell from a crease.

### The id map is nearest, everywhere, including through the gutter

⚠ **An id is a label and not a quantity.** The dilation *copies* a neighbour's id instead of averaging
four of them, because the average of ids 0 and 2 is id 1 — a material that exists nowhere in the source
— and every generator keyed off the map then grows a hairline of it along every chart border, in a
colour belonging to nothing. That is also why the channel is an `int`: `MapBaker.IdColour` turns one
into a distinct colour at the point the pixels are written, where no filter can reach it.

⚠ **And it is the face group only where `EditMesh.GroupSource` says somebody assigned one.** A mesh out
of `EditMesh.FromTriangles` — every generated or sculpted blob — carries `Regroup`'s coplanarity guess,
which on a faceted surface is one group per triangle: 13 965 of them on a 25 439-triangle image-to-3D
mesh. Baked straight that is per-triangle confetti in as many hues, and nothing about the map says so.
So a bake of a guessed grouping labels the source's **connected shells** instead — two props in one
file are two ids, one closed blob is one — and says which it did in `BakedMaps.Warnings`. A caller that
knows the real assignment sets `GroupSource` to `MeshGroupSource.Assigned` and gets its own ids back.

### Nothing samples randomly

The hemisphere is a fixed Hammersley set, and the only thing that varies between texels is an azimuthal
rotation that is a hash of the texel index. ⚠ **A content hash rests on this**: a sampler seeded from a
clock, a thread or an accumulation order would make two builds of one asset differ, which is not a
visible defect but a cache that never hits.

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

Baking the mesh maps a texturing stack reads, and writing the id one as pixels:

```csharp compile
using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

public static class MeshMapping {
    public static Vector3[] Run(EditMesh source, EditMesh quads) {
        var maps = MapBaker.Bake(
            source,
            quads,
            new BakeSettings {
                Resolution = 2048,
                Maps = MeshMaps.AmbientOcclusion | MeshMaps.BentNormal | MeshMaps.Curvature | MeshMaps.Id,
                OcclusionSamples = 256,
                OcclusionRadius = 0.5f
            }
        );

        var ids = maps.Ids;

        if (ids is null) {
            return [];
        }

        var pixels = new Vector3[ids.Count];

        // ⚠ The colour is applied to each texel's own id and never to a blend of two — the map that
        // gets filtered is the one that grows a fourth material along every border.
        for (var index = 0; index < pixels.Length; index++) {
            pixels[index] = MapBaker.IdColour(ids[index]);
        }

        return pixels;
    }
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
- [Triangle tree](core/triangle-tree) — the two-question tree a transfer uses; a bake has a faster one of its own for the same two questions.
- [Deep images](core/deep-images) — 16-bit PNG and EXR, which is what these go to disk as.
- [Mesh maps as project assets](editor/mesh-map-assets) — the caller that writes these pixels to
  files, what the files are called, and how a generator finds one by usage.
