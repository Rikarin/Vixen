---
title: Terrain brushes
slug: engine/terrain-brushes
kind: guide
area: Engine
summary: One brush — a shape, a falloff, a radius in metres and a spacing — answering one question for three different tools.
api: [T:Vixen.Terrain.TerrainBrush, T:Vixen.Terrain.BrushShape, T:Vixen.Terrain.BrushRotation, T:Vixen.Terrain.BrushStamp, T:Vixen.Terrain.BrushFootprint, T:Vixen.Terrain.BrushFalloff, T:Vixen.Terrain.BrushFalloffKind, T:Vixen.Terrain.BrushStroke, T:Vixen.Terrain.IBrushMask]
tags: [terrain, brush, falloff, authoring]
since: 0.1
status: preview
related: [engine/terrain-heightfield, engine/terrain-sculpting, editor/terrain-mode]
---

## What it is

`TerrainBrush` answers one question — *for this world-space sample, what is the weight of this
stamp?* — and it does not know whether the answer will scale a height, a layer weight or a scatter
probability. It carries a shape, a falloff curve and fraction, a radius in metres, a strength, a stamp
spacing and a rotation mode. `BrushStamp` is one landing of it; `BrushStroke` turns a path into evenly
spaced stamps; `BrushFootprint` is the region a stamp can reach.

## What it is for

Three consumers, one implementation, one settings panel section, one set of tests. Unreal implements
the sculpt brush, the paint brush and the foliage brush three times, so a soft edge sculpted at
strength 0.3 and a soft edge painted at strength 0.3 are different shapes there. Here they are the
same shape by construction.

You do not want it for anything whose falloff is not radial — a ramp is defined by two picked points
and a width, which is why `TerrainSculpt.Ramp` does not take a brush at all.

## Using it

```csharp no-compile="a fragment; the settings normally come from the terrain panel"
var brush = TerrainBrush.Default with { Radius = 8f, Strength = 0.5f, Falloff = 0.5f };
var weight = brush.WeightAt(sample, new BrushStamp(centre));
```

⚠ **`Falloff` is the fraction of the radius that falls off, not where the falloff starts.** The two
read almost the same on a slider and are inverses of each other. Zero is a hard disc; one is falloff
all the way in, with no plateau at all.

⚠ **A half-falloff brush has a plateau, and that surprises the tools that read slopes.** Every sample
within the inner half is at full weight, so a mound built with the default brush is a *mesa* — the
3×3 neighbourhood at its top is flat, a smooth changes nothing there and an erosion finds no slope to
exceed the talus angle. That is correct behaviour for all three; a test of any of them has to build a
cone.

⚠ **`Spacing` is a fraction of the radius, not a distance.** Making it a distance means that turning
the radius up thins the stroke out until it is a row of discs. Unity and every paint application spell
it this way.

## Strokes

`BrushStroke` accumulates a drag one pointer move at a time and hands back stamps:

```csharp no-compile="a fragment; the positions come from a pointer over the ground"
var path = new BrushStroke(brush);
var stamps = new List<BrushStamp>();

path.MoveTo(start, stamps);
path.MoveTo(next, stamps);
```

⚠ **The first call always stamps**, because an artist who clicks without dragging expects one stamp
rather than none — which is what makes the same type the Single tool as well as the Paint one.

⚠ **Spacing is by distance travelled, with the leftover carried across pointer events.** Stamping per
event ties the density of a stroke to the frame rate and to how fast the pointer was moving, so the
same gesture leaves a different mark on a fast machine.

⚠ **A random rotation is a hash of the stamp index, not a draw from a generator.** The angle of stamp
N depends only on N, so a stroke can be undone and redone to the same result — the same property the
scatter needs, for the same reason.

## Masks

`IBrushMask` is a function from the unit square to a weight. It is an interface rather than a texture
so that this assembly needs no image type; the editor hands it something backed by an alpha texture,
and a test hands it a lambda.

## The four falloffs

`BrushFalloff.Evaluate` is arithmetic on one number, and the four curves are Unreal's:

| | |
|---|---|
| `Smooth` | Smoothstep — the default, and what a landform wants |
| `Linear` | A straight ramp, for a cut with a visible edge |
| `Spherical` | Bulges outward; a dome rather than a mound |
| `Tip` | Falls away fast; a spike with a soft skirt |

## Examples

The same brush, three consumers — which is the whole argument:

```csharp no-compile="a fragment; the layer and the paint channel are the caller's"
TerrainSculpt.Sculpt(terrain, layer, brush, stamp, metres: 2f);
TerrainSculpt.Paint(terrain, paintLayer: 1, brush, stamp, amount: 40);
```

A soft edge sculpted at strength 0.3 and a soft edge painted at strength 0.3 are the same shape,
because both went through `WeightAt`.

A hard-edged brush, for a cut that has to read as deliberate:

```csharp no-compile="a fragment"
var hard = TerrainBrush.Default with { Radius = 4f, Falloff = 0f, Curve = BrushFalloffKind.Linear };
```

⚠ **Falloff 0 is a disc with a step at its rim**, which on a heightfield is a wall one sample thick.
That is sometimes exactly what a road cut wants and is never what a landform does.

## See also

- [The terrain heightfield](terrain-heightfield.md) — what the weights are applied to.
- [Sculpting a heightfield](terrain-sculpting.md) — the kernels that consume a stamp.
- [docs/plan/31 § D12](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  why the brush is one service, and what having three of them costs.
