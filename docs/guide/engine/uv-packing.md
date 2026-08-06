---
title: UV packing
slug: engine/uv-packing
kind: guide
area: Engine
summary: Islands in, transforms out — a standalone atlas packer whose margin is counted in texels and whose output does not move between runs.
api: [T:Vixen.Geometry.Uv.UvUnwrap, T:Vixen.Geometry.Uv.UvIsland, T:Vixen.Geometry.Uv.UvPlacement, T:Vixen.Geometry.Uv.PackSettings, T:Vixen.Geometry.Uv.PackQuality, T:Vixen.Geometry.Uv.PackOverflow, T:Vixen.Geometry.Uv.UvReport, T:Vixen.Geometry.Uv.UvStage, T:Vixen.Geometry.Uv.UvStageTiming, T:Vixen.Geometry.Uv.UvDistortion, T:Vixen.Geometry.Uv.UvTexelDensity]
tags: [geometry, uv, atlas, packing, texture, texel-density, udim]
since: 0.1
status: preview
related: [engine/edit-meshes, engine/mesh-operations]
---

## What it is

`UvUnwrap.Pack` takes islands that are already unwrapped and rearranges them to fill a sheet. It does
not cut seams and it does not flatten anything. Islands go in, a `UvPlacement` per island comes out —
an offset, a scale, a quarter turn and a UDIM tile — and the island's own coordinates are never
rewritten.

## What it is for

The artist who cut their seams by hand in another package and wants those seams kept and the islands
rearranged. That is the case a one-button unwrapper cannot serve at all, and it is most of why
standalone packers are products people buy.

It is also what the remesher's atlas calls, and what a bake writes into.

## Using it

```csharp no-compile="a fragment; `islands` came from a file, an artist or another stage"
var placements = UvUnwrap.Pack(islands, new() { Resolution = 2048, Margin = 4 }, out var report);

foreach (var placement in placements) {
    var island = islands[placement.Island];

    foreach (var coordinate in island.Coordinates) {
        var uv = placement.Apply(island, coordinate);   // offset, turn, scale, tile
    }
}
```

`Resolution` is required. Everything else has an answer that is right most of the time.

## The margin is in texels, and that is the whole design

⚠ **A margin expressed as a fraction of UV space is a bug with a two-year fuse.** It looks right at the
resolution it was tuned at, and the same asset shipped at half of it bleeds across islands at mip 3 —
in a build nobody associates with the packing change, misdiagnosed as a sampler problem roughly always.

So `Margin` is an integer count of texels and the packer is told `Resolution`. The same islands packed
at 512², 1024², 2048² and 4096² have the same texel gap at every one of them.

⚠ **One margin between two islands, not two.** Each island padding itself by a full margin puts twice
the gap between neighbours and throws away a quarter of a 2K sheet; each padding itself by half leaves
the right gap between neighbours and half of one against the sheet's edge, which bleeds off it. Both
look completely fine. Here every separation — island to island, island to the atlas edge, island to a
tile boundary — is exactly `Margin` empty texels.

Spacing is distributed evenly across every chart rather than applied per island as it is placed.
Uneven gaps read as carelessness in an atlas even when nothing bleeds.

## Four rungs

`PackQuality` chooses how hard to try.

| Rung | What it does |
|---|---|
| `Rectangle` | Skyline over bounding boxes, with quarter turns. The fast path, and the fallback |
| `Irregular` | The same skyline against each island's rasterized *underside*, so a concave island settles onto a bump instead of resting a box on it |
| `SuperPatch` | Near-rectangular neighbours grouped into one composite rectangle first, then packed |

Everything past `CoreLimit` takes a tail sweep that scans the skyline's steps rather than every
column, which is what makes a mesh with thousands of tiny islands finish. It is a cap on cost and not
on output: every island is still placed, and the report says how many took it.

⚠ **Rasterized masks rather than no-fit polygons, and the reason is rotation.** An NFP needs a unique
polygon per *pair* per *orientation*, so sixteen orientations of a thousand islands is a quarter of a
billion polygons. A bitmask overlap test is a word-wise `AND`, it is trivially parallel, it is
trivially deterministic, and it gets *more* accurate as the atlas grows.

## Efficiency is two numbers

`UvReport.PackingEfficiency` is the island area over the atlas area. `EffectiveEfficiency` is the same
after the margin — island *plus the band it reserves* — so the gap between them is exactly what the
margin setting cost.

⚠ **The second one ranks nothing.** A bounding-box packer draws its margin band around a box, so it
consumes more of the sheet while delivering less of it: measured on 422 irregular islands at 2048²
with a four-texel margin, the rectangle rung consumes 85.1 % of the atlas and delivers 32.99 % of it
as texture, where the irregular rung consumes 68.3 % and delivers 52.96 %. Compare packers on
`PackingEfficiency`; read `EffectiveEfficiency` as the margin's bill.

## Density, and what happens when it does not fit

`TexelDensity` is a constraint rather than an observation — set it and every island is brought to the
same texels per world unit, which is the default because non-uniform density is invisible in the atlas
and glaring in the game. Leave it at zero and the packer keeps each island's own scale and grows the
lot until the sheet is full.

`PackOverflow` decides what happens when the density does not fit: `Scale` brings everything down
uniformly and says so in `UvReport.Warnings`, `NextTile` spills into the next UDIM tile, and `Refuse`
throws and names the shortfall. ⚠ **An island may not straddle a tile boundary**, which is a placement
rule rather than a second packer.

## Deterministic

Same islands, same settings, byte-identical placements — at one worker, four or sixteen, and at any
batch size. Handing the islands over in a different order gives the same layout: the ordering is by
descending area, then by the island's *shape*, and only then by the index it arrived at.

⚠ **There is no annealing, no genetic search and no random restart anywhere in it.** Those are the
irregular-packing literature's three standard answers and every one of them is excluded, because the
content hash has to be a function of the input and a golden must not move.

## See also

- [docs/plan/42](https://github.com/rikarin/Vixen/blob/master/docs/plan/42-uv-unwrapping.md) — the
  design, and the references it is drawn from.
