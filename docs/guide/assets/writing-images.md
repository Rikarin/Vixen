---
title: Writing images for other tools
slug: assets/writing-images
kind: guide
area: Assets
summary: TGA, JPEG, TIFF, lossless WebP and Radiance HDR writers in managed code, for files another program opens.
api: [T:Vixen.Interchange.TgaWriter, T:Vixen.Interchange.JpegWriter, T:Vixen.Interchange.TiffWriter, T:Vixen.Interchange.WebPWriter, T:Vixen.Interchange.HdrWriter]
tags: [imaging, tga, jpeg, tiff, webp, hdr, export, interchange]
since: 0.1
status: preview
related: [core/deep-images, assets/importing-textures]
---

## What it is

Five writers in `Vixen.Interchange`, beside the readers: `TgaWriter`, `JpegWriter`, `TiffWriter`,
`WebPWriter` and `HdrWriter`. Each takes interleaved samples, top row first, and returns the file.

## What it is for

A texture handed to another program — a game engine's importer, a painting tool, a web page — in the
format that program asks for. A game that Vixen runs reads KTX2; these are for everything else, which
is why they live with the importers and not in `Vixen.Core.Imaging`.

## Using it

```csharp compile
using Vixen.Core.Imaging;
using Vixen.Interchange;

public static class Handing {
    public static byte[] Albedo(int size, byte[] rgba) => TgaWriter.Encode(size, size, 4, rgba);

    public static byte[] Preview(int size, byte[] rgb) => JpegWriter.Encode(size, size, 3, rgb, 90);

    public static byte[] Height(FloatImage height) => TiffWriter.EncodeFloat(height);
}
```

| Writer | Channels | Depth | Notes |
|---|---|---|---|
| `TgaWriter` | 1, 3, 4 | 8 | Uncompressed, top row first and marked so |
| `JpegWriter` | 1, 3 | 8 | Baseline, no chroma subsampling, tables made for the picture |
| `TiffWriter` | 1 to 4 | 8, 16, 32-bit float | Deflate strips, unassociated alpha |
| `WebPWriter` | 1, 3, 4 | 8 | Lossless only |
| `HdrWriter` | 1, 3, 4 | RGBE | Run-length scanlines; alpha is dropped |

⚠ **Every writer is checked by a reader nobody here wrote**: StbImageSharp for TGA, JPEG and HDR,
libtiff's `tiffinfo` and macOS ImageIO for TIFF, and libwebp's `dwebp` for WebP. A writer checked only
by its own reader agrees with its own misreading of the format.

⚠ **No chroma subsampling in JPEG.** Half-resolution colour is invisible in a photograph and obvious
along every chart edge of a base colour map.

## Examples

A Radiance file from an emissive map, which keeps its range past one:

```csharp compile
using Vixen.Core.Imaging;
using Vixen.Interchange;

public static class Glow {
    public static byte[] Run(FloatImage emissive) => HdrWriter.Encode(emissive);
}
```

## See also

- [Deep images](core/deep-images) — EXR and 16-bit PNG, in `Vixen.Core.Imaging`.
- [Importing textures](assets/importing-textures) — the readers beside these writers.
