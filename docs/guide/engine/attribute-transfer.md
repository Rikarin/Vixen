---
title: Attribute transfer
slug: engine/attribute-transfer
kind: guide
area: Engine
summary: Carrying a source mesh's normals, coordinates, colours, materials and skinning weights onto the quads a remesh produced — and mirroring them when the remesh was symmetric.
api: [T:Vixen.Geometry.Remeshing.AttributeTransfer, T:Vixen.Geometry.Remeshing.SourceAttributes, T:Vixen.Geometry.Remeshing.TransferSettings, T:Vixen.Geometry.Remeshing.TransferResult, T:Vixen.Geometry.Remeshing.SkinInfluence, T:Vixen.Geometry.Remeshing.SkinBinding]
tags: [geometry, retopology, remesh, skinning, materials, normals, symmetry, mirroring]
since: 0.1
status: preview
related: [engine/retopology, engine/map-baking, engine/edit-meshes, core/triangle-tree]
---

## What it is

`AttributeTransfer.Transfer` takes a source mesh and a remeshed output and moves everything the
source was carrying onto the output. Normals, texture coordinates and face groups are written into the
output mesh itself; vertex colours and skinning weights come back in a `TransferResult`, because
`EditMesh` has nowhere to put them.

`SourceAttributes` is the channels that travel beside the mesh, `TransferSettings` says what may be
carried and what the target can hold, and `SkinBinding` with `SkinInfluence` is a whole mesh's worth
of bone weights as one flat block.

## What it is for

A remesh replaces every vertex in a mesh. Without this stage the result has no materials, no shading
normals worth having and — for a character — no skinning weights, which makes it not a character. A
four-million-triangle generated blob is not expensive because it is four million triangles; it is
expensive because it is four million triangles *of noise with no attributes*. Five thousand quads that
kept them is a pipeline. Five thousand quads that did not is a downgrade.

You will normally not call this directly. [`Remesher.Remesh`](engine/retopology) runs it as stage
seven, and its overload taking a `SourceAttributes` is how a rigged mesh goes through. Call it
yourself when the two meshes did not come from one remesh — conforming a garment to a body, or
re-applying an old mesh's weights to a hand-modelled replacement.

## Using it

```csharp compile
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

public static class Transferring {
    public static TransferResult Run(EditMesh source, EditMesh quads, SkinBinding weights) =>
        AttributeTransfer.Transfer(
            source,
            new SourceAttributes { Weights = weights },
            quads,
            new TransferSettings { MaxInfluences = 4 }
        );
}
```

Every query runs against the source through a [triangle tree](core/triangle-tree), from a point
*inside* the output face rather than from the vertex itself. ⚠ **An output vertex sitting on a hard
edge is equidistant from both sides of it**, so a closest-point query there returns whichever triangle
the tree happened to reach first — and the normal, the coordinate and the colour it hands back all
belong to a face chosen by the shape of a data structure. Insetting the query toward the face's own
centroid asks the question the corner is actually asking: *what is the source like on my side of the
crease*. It is the same mechanism that stops a texture-coordinate seam being interpolated across.

### Face groups are decided by area, not by the nearest face

⚠ **This is the rule that looks like a micro-optimisation and is not.** Assigning each output quad the
group of the source face nearest to it *shreds* along a material boundary: which face is nearest flips
from one quad to the next, so every other quad flips with it, and the result is a sawtooth seam that
reads as a UV bug and gets debugged as one.

Integrating over the quad instead makes the boundary a chain, because a quad that is mostly on one
side is mostly on one side however its corners fell. Measured on a plane split into two groups on a
straight line, with a fourteen-quad target: **the area rule gives fourteen boundary edges, which is
the floor for a straight cut, and the nearest-face rule gives seventeen.** Both boundaries are simple
paths; the rejected one is twenty-one percent longer than a boundary that is known to be straight.

`TransferGroups` turns it off, and leaving it off is not the same as leaving the groups alone — the
extraction gives every quad of a patch the group of that patch's *first* triangle, which is a
whole-patch block rather than a boundary.

### Normals are reconstructed, not just interpolated

Interpolated normals are computed per corner and then averaged among the corners that stand at one
position **inside one shading group**. Without the averaging a smooth surface is faceted for no
reason: two neighbouring quads query the source at two slightly different points and get two slightly
different answers, which is invisible in the numbers and perfectly visible under a moving light.

The shading groups are found by flooding the output's faces, crossing an edge only where both sides
inherited the same source smoothing group **and** the fold between them is gentler than
`SmoothingAngle`. ⚠ **Both conditions, not either.** Trusting the source's groups alone fails on the
overwhelmingly common source that has none — every face is group zero, the whole mesh floods into one
component, and a box comes back with its eight corners smoothed round.

### Skinning weights

Weights are interpolated per position, summed per bone, sorted by descending weight, clamped to
`MaxInfluences` and renormalised.

⚠ **The clamp is not optional and the reason is *which* influence gets dropped.** An output vertex
between two source vertices with four influences each can inherit up to eight. A target with room for
four that is handed five silently loses one, and which one depends on the order the interpolation
happened to accumulate them in — so the same asset re-imported after an unrelated change comes back
bound differently. Sorting by descending weight, with the bone index as the tie-break, makes the
survivor a function of the input.

⚠ **A total of zero stays zero.** An unrigged prop inside a rigged mesh is a real input, and
normalising its zeros would divide by zero or, worse, attach the prop to bone zero — which on a
humanoid is the pelvis. `TransferResult.UnboundVertices` counts them, and a whole mesh of them means
the source binding was indexed wrongly.

### Symmetry mirrors the attributes, and skin weights need a bone map

[`RemeshSettings.Symmetry`](engine/retopology) solves one half of the mesh and reflects it, so the
attributes are reflected with it rather than transferred twice. Normals reflect through the plane;
colours, coordinates and face groups copy unchanged. **Skinning weights do not**, because a mirrored
vertex's weights belong to the *mirrored bone* — and `SkinInfluence` is `(int Bone, float Weight)`, an
index with no name, so nothing in this library can work out which bone that is.

`SourceAttributes.BoneMirror` says. Entry *i* is the index of bone *i*'s mirror, and a centre bone maps
to itself:

```csharp compile
using System;
using System.Collections.Generic;
using Vixen.Core.Mathematics;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

public static class MirroringARig {
    // The convention lives with whoever has the skeleton. This one is the engine's own, the same
    // suffix list ProxyShapeDocument.Sided uses for blockout shapes.
    static readonly (string Left, string Right)[] Sides = [("_l", "_r"), ("left", "right"), ("_L", "_R")];

    public static int[] Map(IReadOnlyList<string> bones) {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var bone = 0; bone < bones.Count; bone++) {
            index[bones[bone]] = bone;
        }

        var mirror = new int[bones.Count];

        for (var bone = 0; bone < bones.Count; bone++) {
            // A bone with no side, and a bone whose partner is not in the skeleton, are both their
            // own mirror. Leaving either out would make the map short, and a short map is refused.
            mirror[bone] = Sided(bones[bone]) is { } other && index.TryGetValue(other, out var found)
                ? found
                : bone;
        }

        return mirror;
    }

    public static EditMesh Character(EditMesh scan, SkinBinding weights, IReadOnlyList<string> bones) =>
        Remesher.Remesh(
            scan,
            new SourceAttributes { Weights = weights, BoneMirror = Map(bones) },
            new RemeshSettings { TargetQuads = 6000, Symmetry = new Plane(Vector3.UnitX, 0f) },
            out _,
            out _
        );

    static string? Sided(string name) {
        foreach (var (left, right) in Sides) {
            if (name.EndsWith(left, StringComparison.Ordinal)) {
                return name[..^left.Length] + right;
            }

            if (name.EndsWith(right, StringComparison.Ordinal)) {
                return name[..^right.Length] + left;
            }
        }

        return null;
    }
}
```

⚠ **Symmetry with weights and no map refuses rather than guessing.** The `TransferResult` comes back
empty and the report carries a warning naming `BoneMirror`. The alternative is mirroring a weight onto
the bone it already named, which produces a character whose left arm drives their right leg — found by
an animator three weeks later and never by a test. A map that names a bone outside itself, one that is
not its own inverse, or one shorter than the bones the binding uses is refused the same way and the
warning says which bone.

⚠ **A vertex on the plane is symmetrised, not left alone.** It is one vertex standing in both halves,
so its weights are averaged with their own mirror. This is the one place an influence count can *grow*
— two four-bone sets average to as many as eight — so the seam is the only part of the mesh where
`MaxInfluences` drops anything, and the survivors are rescaled rather than truncated.

⚠ **Asymmetric detail in the source's attributes is discarded, and that is what symmetry asks for.**
Only the kept half is ever read, so a scar painted on one cheek comes back on both cheeks or neither.
Everywhere else the mirror is exact: a mirrored vertex's weights are the kept half's weights relabelled,
which are the *same floats* rather than nearby ones.

## Examples

Remeshing a rigged character and keeping it rigged:

```csharp compile
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

public static class Retopologising {
    public static (EditMesh Quads, SkinBinding? Weights) Character(EditMesh scan, SkinBinding weights) {
        var quads = Remesher.Remesh(
            scan,
            new SourceAttributes { Weights = weights },
            new RemeshSettings { TargetQuads = 6000 },
            out var report,
            out var transferred
        );

        return report.IsAllQuad ? (quads, transferred.Weights) : (quads, null);
    }
}
```

Reading a binding back, one vertex at a time:

```csharp compile
using Vixen.Geometry.Remeshing;

public static class Reading {
    public static int Bones(SkinBinding binding, int vertex) {
        var count = 0;

        // Padding sits at the end, because every producer sorts by descending weight.
        foreach (var influence in binding.At(vertex)) {
            if (influence.Weight > 0f) {
                count++;
            }
        }

        return count;
    }
}
```

Keeping the source's texture coordinates instead of generating an atlas:

```csharp compile
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

public static class KeepingCoordinates {
    public static EditMesh Run(EditMesh source) =>
        Remesher.Remesh(
            source,
            new RemeshSettings {
                TargetQuads = 4000,
                GenerateUvs = false,
                Transfer = new TransferSettings { KeepTexCoords = true }
            },
            out _
        );
}
```

⚠ **`GenerateUvs` overrules `KeepTexCoords` and never the other way round.** Both write the same
layer, and a remesh that regenerated the atlas and then overwrote it with the source's old coordinates
would be indistinguishable from one where the atlas stage failed.

## See also

- [Retopology settings and reports](engine/retopology) — the stage this is seventh of.
- [Map baking](engine/map-baking) — the other half of what makes a remesh a pipeline.
- [Triangle tree](core/triangle-tree) — the structure every query here goes through.
- [Edit meshes](engine/edit-meshes) — the per-corner layers a seam is free in.
