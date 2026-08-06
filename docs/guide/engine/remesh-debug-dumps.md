---
title: Remesh debug dumps
slug: engine/remesh-debug-dumps
kind: guide
area: Engine
summary: Every stage of the remesher as an inspectable artefact — conditioned triangles, the field as a line set, the layout as regions, the quantization as a labelled graph.
api: [T:Vixen.Geometry.Remeshing.RemeshDump, T:Vixen.Geometry.Remeshing.RemeshSegment, T:Vixen.Geometry.Remeshing.RemeshRegion, T:Vixen.Geometry.Remeshing.RemeshArcLabel, T:Vixen.Geometry.Remeshing.RemeshStage]
tags: [geometry, remesh, retopology, quad, debugging, diagnostics]
since: 0.1
status: preview
related: [engine/retopology, engine/quad-remeshing, engine/edit-meshes, engine/uv-packing]
---

## What it is

`RemeshDump.Capture` runs the remesher's first five stages and keeps what each one produced: the
conditioned triangles as a mesh, the feature polylines as a line set, the cross field as a cross per
vertex, the singularities as points, the patch layout as one region per triangle, and the quantization
as one label per arc.

It returns the data. It never writes a file.

## What it is for

When a remesh looks wrong, *which stage* is the first question, and a monolith cannot answer it.
`RemeshReport` says which stage was slow and which one dropped something. This says what each one
produced, which is the half you need in order to look at it.

The four artefacts map onto the four things that go wrong:

| Artefact | What it shows you |
|---|---|
| `Conditioned` | Whether the input survived the weld, the de-speck and the pre-remesh at all |
| `Features` + `Singularities` | Whether a hard edge was found, and whether an irregular vertex landed on one |
| `Layout` | Whether the partition is compact or snaky — a patch's quad count is a *product*, so a snaky one overshoots quadratically |
| `Quantization` | Where the consistency system had to spend: `Quads` against `Target`, per arc |

## Using it

```csharp no-compile="a fragment; `triangles` is any EditMesh"
var dump = RemeshDump.Capture(triangles, new RemeshSettings { TargetQuads = 5000 });

dump.Conditioned;      // stage ①, as a mesh
dump.Features;         // stage ②, as a line set
dump.Field;            // stage ③, two crossed segments per vertex
dump.Singularities;    // stage ③, as points
dump.Layout;           // stage ④, one region per conditioned triangle
dump.Quantization;     // stage ⑤, one label per arc
```

⚠ **It returns arrays and never touches the filesystem**, because `Core/` is under the virtual-path
rule: no `System.IO.Path`, no `File`. Turn a `RemeshSegment` list into an `.obj`, a gizmo batch or an
editor overlay in the layer that is allowed to — the format that suits an editor is not the one that
suits a bug report.

⚠ **A patch index rather than a colour.** "The layout as coloured regions" is what the design asks
for, and a palette baked in here would be one more thing to disagree about while throwing away the
identity you need to point at a patch in the report. Colouring an index is one modulus away.

## Crosses, not arrows

A 4-RoSy field has no arrow. `Field` emits two crossed segments per vertex — the representative
direction and its quarter turn — because drawing only the representative invents a sign the solver
never had, and the discontinuity that draws is a rendering artefact somebody will spend an afternoon
chasing as a solver bug.

⚠ **The arm length is a fraction of the bounding box's diagonal and never a length.** `CrossArm` is
`0.004`. A fixed number would draw nothing on a millimetre-wide part and a hairball on a
kilometre-wide one, which is the mistake this repository has made three times.

## It re-runs the stages

`Capture` does the work again rather than reaching into a remesh that already happened, and it costs
what a remesh costs. The alternative is a capture hook threaded through `Remesher`, which puts a
debugging concern in the middle of a pipeline every caller pays for.

⚠ **What makes that legitimate is the determinism gate.** The same input and settings give the same
answer at any worker count, so the artefacts captured here are the artefacts the remesh had. If that
ever stopped being true, this facility would be the second thing to break and the report would be the
first.

It stops before extraction, because the extraction's artefact is the mesh `Remesher.Remesh` already
returns.

## Examples

**Find the stage that refused.**

```csharp no-compile="a fragment; `broken` is a mesh that came back empty"
var quads = Remesher.Remesh(broken, settings, out var report);

if (report.QuadCount == 0) {
    var dump = RemeshDump.Capture(broken, settings);

    Report(dump.Conditioned.FaceCount);    // zero means stage ① took everything
    Report(dump.Quantization.Count);       // zero means the layout never produced arcs
    Report(dump.Warnings);
}
```

**See where the quantizer had to spend.** The cost is the squared distance between what an arc got and
what the density field asked for, summed over arcs — so the arcs where the two are far apart are the
ones the consistency system paid for.

```csharp no-compile="a fragment; continues from above"
var dump = RemeshDump.Capture(source, new RemeshSettings { TargetQuads = 5000 });

foreach (var label in dump.Quantization) {
    if (MathF.Abs(label.Quads - label.Target) > 1f) {
        Report(label.Arc, label.Quads, label.Target, label.IsFeature);
    }
}
```

**Draw the layout.** One region per conditioned triangle, `-1` where the partition claimed nothing.

```csharp no-compile="a fragment; continues from above"
foreach (var region in dump.Layout) {
    var colour = region.Patch < 0 ? Colour.Grey : Palette[region.Patch % Palette.Length];

    Draw(dump.Conditioned, region.Triangle, colour);
}
```

## See also

- [Retopology settings and reports](retopology.md) — what the report says, stage by stage.
- [Quad remeshing](quad-remeshing.md) — the pipeline these artefacts come out of.
- [docs/plan/41](https://github.com/rikarin/Vixen/blob/master/docs/plan/41-automatic-retopology.md) —
  § D1's seven stages and § R4's audit.
