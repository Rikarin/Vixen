---
title: Padding and resizing
slug: core/padding-and-resizing
kind: guide
area: Core
summary: Filling the background of a texture from its islands, and changing a picture's size with the right filter.
api: [T:Vixen.Core.Imaging.ImagePadding, T:Vixen.Core.Imaging.PaddingMode, T:Vixen.Core.Imaging.ImageResize, T:Vixen.Core.Imaging.ResizeFilter]
tags: [imaging, padding, dilation, gutter, resize, export]
since: 0.1
status: preview
related: [core/deep-images, engine/map-baking]
---

## What it is

`ImagePadding.Pad` fills the texels of a `FloatImage` that no island of its layout covers, from the
islands. `ImageResize.Resize` makes a copy of a `FloatImage` at another size.

## What it is for

A texture is sampled bilinearly and through mips, and both read texels beside the one a surface
asked for. At a chart's edge those neighbours are background, so whatever the background holds bleeds
into the chart: a black seam at a distance, or a halo of the wrong colour. Padding gives the
background the chart's own colour, so a tap that strays reads what it would have read inside.

## Using it

```csharp compile
using Vixen.Core.Imaging;

public static class Exporting {
    public static void Pad(FloatImage map, bool[] islands) =>
        ImagePadding.Pad(map, islands, PaddingMode.Infinite, 0);

    public static FloatImage Half(FloatImage map) =>
        ImageResize.Resize(map, map.Width / 2, map.Height / 2, ResizeFilter.Box);
}
```

The coverage is one flag per texel, row zero first, and is read and never written. The modes:

| Mode | The islands grow | The rest |
|---|---|---|
| `None` | not at all | as it was |
| `Infinite` | until every texel is filled | — |
| `Transparent` | by the distance | zero in every lane |
| `Colour` | by the distance | the colour given, one value per lane |
| `Diffusion` | by the distance | a smooth pull-push blend of what is round it |

⚠ **Each round commits after it finishes.** A texel filled early in a round is not read by another in
the same round, so where two islands' fronts meet the seam falls in the middle and not on whichever
side the scan reached first. It is `MapBaker`'s dilation rule, applied to a picture instead of a bake.

⚠ **Bilinear is the wrong filter for making a picture much smaller**: at a quarter of the size it reads
four texels of every sixteen. `ResizeFilter.Box` weighs every source texel by the area it shares with
the target texel, which keeps the mean at any ratio; `Nearest` makes no new values, for ids and masks.

## Examples

Padding past the islands by eight texels and leaving the rest a flat grey:

```csharp compile
using Vixen.Core.Imaging;

public static class Grey {
    public static int Run(FloatImage map, bool[] islands) =>
        ImagePadding.Pad(map, islands, PaddingMode.Colour, 8, [0.5f, 0.5f, 0.5f, 1f]);
}
```

## See also

- [Deep images](core/deep-images) — the EXR and 16-bit PNG writers these feed.
- [Map baking](engine/map-baking) — the dilation this shares its rules with.
