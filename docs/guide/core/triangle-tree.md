---
title: Triangle tree
slug: core/triangle-tree
kind: guide
area: Core
summary: A bounding-volume hierarchy over a triangle soup, for the two questions a bake asks it — what is nearest, and what does this ray hit.
api: [T:Vixen.Core.Mathematics.TriangleTree, T:Vixen.Core.Mathematics.ClosestTriangle]
tags: [mathematics, geometry, bvh, raycast, bake]
since: 0.1
status: preview
related: [engine/retopology, engine/edit-meshes]
---

## What it is

A median-split bounding-volume hierarchy built once over a triangle soup, answering three questions
about it: how far away is the nearest surface, what does a ray hit, and — the one attribute transfer
needs — *which* triangle is nearest and where on it.

`ClosestTriangle` is what that last question returns: the triangle's index, the point on it, the
squared distance, and the barycentric coordinates of the point within the triangle.

## What it is for

Anything that asks a mesh a question many times over. A signed-distance bake samples a grid against the
surface; a remesh's attribute transfer asks, for every output vertex, which source triangle it came
from so that normals, texture coordinates, colours and skinning weights can be interpolated there; an
occlusion estimate fires rays and counts what they hit.

⚠ **The barycentric coordinates are the reason `Closest` exists beside `DistanceSquared`.** A scalar
distance tells you how far the surface is and nothing about what it was carrying. Interpolating a
per-vertex quantity at the nearest point needs the triangle and the three weights, and recovering them
afterwards from the point alone is both slower and less accurate than reading them out of the
traversal that already found them.

You do not want it for a single query. Building the tree is a sort; one `DistanceSquared` against a
hundred triangles is faster done directly.

## Using it

```csharp compile
using Vixen.Core.Mathematics;

public static class Nearest {
    public static ClosestTriangle Run() {
        var vertices = new[] {
            new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f)
        };

        var tree = new TriangleTree(vertices, [0, 1, 2]);

        // Which triangle, where on it, and the weights to interpolate a per-vertex quantity with.
        return tree.Closest(new(0.25f, 0.25f, 2f));
    }
}
```

The constructor takes the soup as positions and indices, three per triangle, and copies what it needs —
the spans do not have to outlive it.

## Examples

Transferring a per-vertex quantity from a source mesh onto a new one, which is what a remesh's
attribute stage does for normals, texture coordinates and skin weights alike:

```csharp no-compile="a fragment; the source arrays and the output positions come from the caller"
var tree = new TriangleTree(sourcePositions, sourceIndices);

foreach (var position in outputPositions) {
    var hit = tree.Closest(position);
    var corner = hit.Triangle * 3;

    var value = (source[sourceIndices[corner]] * hit.Barycentric.X)
        + (source[sourceIndices[corner + 1]] * hit.Barycentric.Y)
        + (source[sourceIndices[corner + 2]] * hit.Barycentric.Z);
}
```

⚠ **The barycentric coordinates sum to exactly one, including in the degenerate cases** — a point that
projects onto an edge, onto a vertex, or onto a triangle with no area. An interpolation written against
that guarantee does not need to renormalise, and one that renormalises anyway will not be wrong.

## Deterministic, which is why it can sit under a bake

⚠ **Ties in the centroid comparison break on triangle index, so the tree does not depend on the sort's
stability.** That is a deliberate build rule rather than an accident of the implementation, and it is
what lets a bake assert that two runs over the same mesh produce byte-identical output — a property the
distance-field bakes and the content hash both rely on.

## See also

- [Retopology settings and reports](engine/retopology) — the attribute transfer this exists for.
- [Edit meshes](engine/edit-meshes) — where a triangle soup usually comes from.
