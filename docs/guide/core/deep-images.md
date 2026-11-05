---
title: Deep images
slug: core/deep-images
kind: guide
area: Core
summary: Sixteen-bit PNG and floating-point EXR in managed code, for pictures that are measurements.
api: [T:Vixen.Core.Imaging.ExrCodec, T:Vixen.Core.Imaging.ExrPixels, T:Vixen.Core.Imaging.FloatImage]
tags: [imaging, png, exr, openexr, bake, 16-bit]
since: 0.1
status: preview
related: [engine/map-baking, rendering/capturing-a-frame]
---

## What it is

`PngCodec.Encode16` writes grey, RGB or RGBA at sixteen bits, and `Decode16` reads eight or sixteen.
`ExrCodec` writes and reads OpenEXR scanline files with ZIP compression, at sixteen or thirty-two bits
of float per channel.

## What it is for

A baked map is a measurement. An 8-bit displacement map has 256 heights in it, which shows up as
terraces under a grazing light, and a position map needs a range an integer format cannot hold.

## Using it

```csharp compile
using Vixen.Core.Imaging;

public static class Writing {
    public static byte[] Height(int size, float[] heights) =>
        ExrCodec.Encode(new(size, size, 1, heights), ExrPixels.Float);
}
```

Rows run from the top, channels are interleaved, and `FloatImage.Channels` is one, three or four: those
are written as `Y`, `R G B` and `R G B A`. ⚠ **A file stores channels in name order whatever order they
were interleaved in**, so a reader that assumes otherwise sees the wrong channel everywhere.

⚠ **Managed on purpose.** The reference library is four native binaries to build and sign on each
platform, and a flat image with one compression method is about two hundred lines over the deflate
.NET already ships. So this is a subset: a tiled, deep or multi-part file, or another compression, is
refused on read rather than guessed at. Its output is checked against FFmpeg's decoder and macOS's
image reader, neither of which shares a line with it.

⚠ **A 16-bit PNG is big-endian**, and a round trip through one codec cannot see a swap: both ends would
agree on the wrong order. `DeepImageTests` reads the bytes.

## See also

- [Map baking](engine/map-baking) — what writes these.
- [Capturing a frame](rendering/capturing-a-frame) — the 8-bit half of `PngCodec`.
