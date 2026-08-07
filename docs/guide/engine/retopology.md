---
title: Retopology settings and reports
slug: engine/retopology
kind: guide
area: Engine
summary: What to ask a quad remesh for, and everything it measured about what it gave back.
api: [T:Vixen.Geometry.Remeshing.RemeshSettings, T:Vixen.Geometry.Remeshing.AtlasSettings, T:Vixen.Geometry.Remeshing.ConditioningSettings, T:Vixen.Geometry.Remeshing.RemeshGuide, T:Vixen.Geometry.Remeshing.RemeshReport, T:Vixen.Geometry.Remeshing.ConditioningReport, T:Vixen.Geometry.Remeshing.RemeshStage, T:Vixen.Geometry.Remeshing.RemeshStageTiming, T:Vixen.Geometry.Remeshing.Singularity]
tags: [geometry, retopology, remesh, quad, mesh]
since: 0.1
status: preview
related: [engine/attribute-transfer, engine/map-baking, engine/uv-packing, engine/remesh-debug-dumps, engine/edit-meshes, engine/mesh-operations, engine/mesh-booleans, core/triangle-tree]
---

## What it is

The two halves of a quad remesh that a caller holds: `RemeshSettings` is what you ask for, and
`RemeshReport` is everything that was measured about what came back. Between them sit
`ConditioningSettings` and `ConditioningReport`, which are the same pair for stage one — the part that
turns broken input into something the rest of the pipeline can read at all.

`RemeshStage` names the seven stages, `RemeshStageTiming` says how long one took and how much it
handled, `Singularity` is one irregular vertex, and `RemeshGuide` is a curve the edge flow should
follow.

## What it is for

Turning a triangle soup into an all-quad mesh with clean edge loops — a generated blob, a boolean
result from the blockout tools, a scanned part — inside the content build, with no external binary and
no round trip through a modelling package.

You want the report as much as the mesh. A remesh has no single right answer, and the difference
between one that is usable and one that is not shows up as numbers: how far the output strays from the
source, how many irregular vertices there are and where they landed, whether the worst quad is
inverted. ⚠ **A remesher that cannot tell you it went wrong will be trusted until it embarrasses
somebody**, which is the whole argument for running this unattended.

You do not want it for a mesh whose topology is already the point. Retopology replaces topology by
definition; a mesh that only needs texture coordinates wants
[the unwrapper](engine/uv-packing) instead.

## Using it

```csharp no-compile="Remesher is R3's entry point and lands with the layout stage"
var settings = new RemeshSettings {
    TargetQuads = 5000,
    Adaptivity = 0.7f,
    FeatureAngle = 35f
};

var quads = Remesher.Remesh(triangles, settings, out var report);
```

Two fields decide the size and only one of them may be set: `TargetQuads` is a budget, and
`TargetEdgeLength` is the same statement in world units. One implies the other through the surface
area, so giving both is an error rather than a preference.

`Adaptivity` runs from uniform squares at zero to curvature-driven at one. ⚠ **The curvature term is
weighted by *anisotropy* — the difference between the two principal curvatures — and not by curvature
itself.** On a sphere the two are equal, the weight is zero, and the field is free to be smooth, which
is the correct answer. Weighting by magnitude instead is the classic failure: it chases noise on a
sphere and produces topology that looks agitated for no reason. Measured on this implementation, mean
anisotropy is **0.146** on a sphere against **8.26** on a cylinder, where the corresponding
*magnitudes* are 3.55 and 6.66 — so magnitude says a sphere is half as curved as a cylinder, and
anisotropy says a sphere has no direction.

### What the report says

| Field | What to read it for |
|---|---|
| `QuadCount`, `NonQuadCount` | ⚠ The second must be zero. A non-zero is a bug rather than a setting |
| `Singularities` | The headline quality number — fewer and better placed is better |
| `SingularitiesOnFeatures` | How many landed on a hard edge, where they read as a pinch |
| `MaxDeviation`, `MeanDeviation` | As a fraction of the bounding-box diagonal, so they compare across models |
| `MinScaledJacobian` | The worst quad's shape. A negative one is inverted |
| `FeatureReproductionError` | How far a feature polyline sits from the nearest output edge |
| `Conditioning` | What stage one changed, and whether it had to reach for the shrinkwrap |
| `Mesh` | The kernel's own `MeshReport` — manifold, closed, consistent, no degenerates |
| `Stages` | Which stage was slow, and which one dropped something |
| `Warnings` | Components dropped · a patch collapsed · the budget not met |

⚠ **`NonQuadCount` being zero is not a nicety, it is what makes the output editable.** The modelling
verbs are built on the assumption that a loop, a ring and a loop cut are statements about four-sided
faces. A result with triangles and pentagons in it has no rings to cut, and the whole vocabulary of
`MeshOperations` stops working on it.

### Conditioning is a stage with a report, not hygiene

Generated meshes arrive with staircase noise at the voxel frequency, self-intersections, floating
debris and non-manifold edges. `ConditioningSettings` says what may be done about it and
`ConditioningReport` says what was. A caller can refuse to continue on the report.

⚠ **Every tolerance here is relative to the bounding box and never absolute.** A fixed epsilon is a
claim about how big a model is, and this repository has been caught by that more than once — an
absolute degeneracy test once declared sixty-four real triangles of a capsule's pole degenerate, and a
model built at a tenth of the scale would have lost all of them.

⚠ **`Shrinkwrap` is off by default and reading `ConditioningReport.Shrinkwrapped` as true is worth
treating as a warning.** It destroys thin features. It exists for input so broken that nothing else
will run.

`FillHoles` is off by default too, for a different reason: a hole in the input is very often a hole in
the subject, and closing one silently is worse than leaving it.

### Singularities

A `Singularity` is a vertex whose one-ring does not close into four quarter turns — a valence-3 or
valence-5 point in the output. They cannot be eliminated: the sum of their indices is fixed by the
surface's Euler characteristic, so a sphere must carry eight quarter-turns' worth however good the
field is. What can be decided is *where* they go, and the right somewhere is where the surface
genuinely is not developable — a fingertip, a box corner, a sphere's pole, which is where an artist
puts them.

⚠ **"Zero singularities on features" means zero on the *interiors* of the feature chains, not off
every feature vertex.** On a cube every vertex is a feature corner and every edge a feature edge, and
eight quarter-turns have to exist somewhere — so the literal reading makes the simplest hard-surface
shape unsatisfiable. A corner is exactly where one belongs.

## Examples

Retopologising a generated mesh and refusing the result if conditioning had to work too hard:

```csharp no-compile="Remesher is R3's entry point and lands with the layout stage"
var settings = new RemeshSettings {
    TargetQuads = 4000,
    Conditioning = new() { Shrinkwrap = false, FillHoles = false }
};

var quads = Remesher.Remesh(triangles, settings, out var report);

if (report.Conditioning.Despecked > 8 || report.Conditioning.Unorientable > 0) {
    return null;   // the input was not one mesh, and a remesh of it means nothing
}
```

Asking for uniform squares rather than adaptive density, which is what a hard-surface part usually
wants:

```csharp no-compile="Remesher is R3's entry point and lands with the layout stage"
var settings = new RemeshSettings {
    TargetQuads = 2000,
    Adaptivity = 0f,
    KeepGroups = true,
    FreezeBorder = true
};
```

⚠ **`KeepGroups` only reads a group boundary somebody assigned.** It is checked against
`EditMesh.GroupSource`, so a shape out of `MeshShapes`, a boolean of two of them and a mesh whose
material a reader carried across all keep their crease — and a mesh welded straight out of
`EditMesh.FromTriangles`, whose groups are the coplanarity guess, does not. On a faceted surface that
guess is close to one group per triangle, which would make every edge of the mesh a hard feature.

Guides are an asset rather than a paint session, so one authored against an earlier version of a model
still applies:

```csharp no-compile="Remesher is R3's entry point and lands with the layout stage"
var settings = new RemeshSettings {
    TargetQuads = 6000,
    Guides = [new RemeshGuide(spine, Strength: 0.8f)]
};
```

## See also

- [Attribute transfer](engine/attribute-transfer) — stage seven, and what stops a remesh being a downgrade.
- [Map baking](engine/map-baking) — the normal and displacement maps the atlas is filled with.
- [UV packing](engine/uv-packing) — where the atlas comes from, and the packer this shares.
- [Edit meshes](engine/edit-meshes) — the kernel a remesh hands quads back to.
- [Mesh booleans](engine/mesh-booleans) — the hard-surface case, and what a blockout retopologises.
- [Triangle tree](core/triangle-tree) — the acceleration structure the attribute transfer asks.
