<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Terrain

The terrain kernel. Today it is the brush — [docs/plan/31 § B7](../../docs/plan/31-terrain-grass-and-trees.md)
and [§ D12](../../docs/plan/31-terrain-grass-and-trees.md) — and the heightfield, edit layers and
sculpt kernels land here in [§ T1](../../docs/plan/31-terrain-grass-and-trees.md).

**No device, no document, no editor.** One project reference, to `Vixen.Core.Mathematics`, for the
reason `Vixen.Geometry` has the same one: a kernel that needed the render assembly to describe a
height sample would be backwards. Everything here is a function over arrays, so the tests need no
world and run in milliseconds.

## The brush

| Type | What it is |
|---|---|
| `TerrainBrush` | A radius in metres, a strength, a falloff fraction and curve, a shape, a spacing and a rotation mode. Answers `WeightAt(sample, stamp)` |
| `BrushFalloff` | The four curves — smooth, linear, spherical, tip — as arithmetic on one number |
| `BrushStamp` | One application: where it landed, how it was turned, how much flow it carried |
| `BrushStroke` | A drag, accumulated one pointer move at a time into evenly spaced stamps |
| `IBrushMask` | Where a masked brush reads its weights. A function from the unit square to a number, so this assembly needs no image type |
| `BrushFootprint` | What a stamp or a stroke touched, which is what a tool marks dirty and what an undo record is sized by |

**One brush, three consumers.** Sculpt strength, paint weight and foliage density over a falloff are
the same function applied to different targets. Unreal implements them three times, which is why a
soft edge sculpted at strength 0.3 and a soft edge painted at strength 0.3 are different shapes
there. `TerrainBrush` does not know what its answer is multiplied into.

## Two things worth knowing

**`Falloff` is the fraction of the radius that falls off**, not where the falloff starts. Zero is a
hard-edged disc; one falls off from the centre. Read the other way round it gives a brush that is
hardest where it should be softest, and the result still looks like a brush.

**A stroke is spaced by distance, not by pointer events.** Stamping once per event makes a brush
whose density depends on the frame rate and on how fast the artist moved. The leftover distance is
carried between segments, so the spacing is even across the join as well as within it — and the
random rotation is a hash of the stamp index rather than a shared generator, so a stroke can be
replayed, undone and redone to the same result.
