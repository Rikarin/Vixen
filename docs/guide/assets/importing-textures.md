---
title: Importing textures
slug: assets/importing-textures
kind: guide
area: Assets
summary: Which image formats the pipeline reads, what each decoder decides, and the two settings that are not cosmetic.
api: [T:Vixen.Editor.Assets.Textures.TextureImporter, T:Vixen.Editor.Assets.Textures.TextureImportSettings, T:Vixen.Editor.Assets.Textures.TextureContent, T:Vixen.Editor.Assets.Textures.TextureCompression, T:Vixen.Editor.Assets.Textures.IImageDecoder, T:Vixen.Editor.Assets.Textures.ImageDecoders, T:Vixen.Editor.Assets.Textures.StbImageDecoder, T:Vixen.Editor.Assets.Textures.Ktx2Decoder, T:Vixen.Editor.Assets.Textures.DdsDecoder]
tags: [assets, textures, importers, ktx2, dds]
since: 0.2
status: preview
related: [assets/content-in-a-game]
---

## What it is

Everything under `Assets/` that is a picture goes through one importer. `TextureImporter` decodes it,
limits it, labels it, builds its mip chain, compresses it and writes a KTX2 — which is the only
texture container the runtime reads.

The decoding is the one part that is swappable, because it is the one part with a licence attached.
It sits behind `IImageDecoder`, and `ImageDecoders.BuiltIn` is the set that ships:

| Decoder | Reads | Gives back |
|---|---|---|
| `StbImageDecoder` | `.png` `.jpg` `.jpeg` `.bmp` `.tga` `.psd` `.gif` | `Rgba8UNorm`, one level |
| `StbImageDecoder` | `.hdr` | `Rgba32Float`, one level — a Radiance file holds radiance, and a byte would throw the sun away |
| `Ktx2Decoder` | `.ktx2` | Whatever the file holds, untouched |
| `DdsDecoder` | `.dds` | Compressed: the blocks and the whole mip chain. Uncompressed: level zero as `Rgba8UNorm` |

## What it is for

Getting an artist's file onto a device without anyone having to think about block formats. Two of the
settings do have to be thought about, and they are the two this page exists for.

### `Content` is a claim about what the bytes mean

`TextureContent` is not a label, it is the input to three separate decisions:

- **the sRGB flag.** `Colour` ships in an sRGB format so the sampler converts on the way out; `Linear`
  and `NormalMap` must not, because applying a transfer function to a roughness map bends the whole
  material response.
- **the mip filter.** Colour is averaged in the light it stands for; a normal map's mips come back
  unit length, which a plain box filter would not give.
- **the compressed format** when `Compression` is `Automatic`: BC5's two channels for a normal map,
  because the third is reconstructed in the shader and storing it costs precision the other two want;
  BC7 otherwise.

Get it wrong and nothing fails. The scene looks washed out, or crushed, or lit slightly oddly, and it
takes a week.

### A compressed source keeps its own answer

A `.ktx2` or a block-compressed `.dds` is **passed through**, not decoded and re-encoded — a second
round of lossy compression only ever loses. That has a consequence worth stating: on that path the
file's header, not `Content`, decides the transfer function. `BC7_UNORM` and `BC7_UNORM_SRGB` are
different formats and the exporter picked one. When the file's choice contradicts `Content`, the
import emits a warning naming both; it does not silently re-label, because re-labelling compressed
blocks is not a thing that can be done.

### A high-range source keeps its range

A `.hdr` decodes to `Rgba32Float`, and the eight-bit path cannot hold it — so it does not take it. A
high-range source ships as `Bc6HRgbUFloat` under `Automatic` or `Bc6H`, and as the decoded floats
under `None`. Nothing narrows it to a byte and nothing tone-maps it: the sun being ten thousand times
the sky is the content of the image, and an exposure baked in at import time would belong to a scene
the asset has not been put in yet.

Three of the eight-bit path's decisions cannot be made here, and each is reported rather than
approximated:

| Setting | What a `.hdr` gets | Why |
|---|---|---|
| `Content` | Linear, whatever it says | No float format has an sRGB form, and Radiance is linear by definition. Reported as information, because `Colour` is the default and is what an artist importing a sky will leave alone |
| `GenerateMips` | One level, with a warning | The mip filter averages eight-bit channels. A float form of it is owed; a chain built by narrowing to bytes first would throw the range away |
| `MaxSize` | Full size, with a warning | Reducing runs through that same filter |

`Compression` is refused rather than approximated in both directions: BC1, BC3, BC4, BC5 and BC7 all
clamp at one, so a `.hdr` in one of them would be a low-range picture under a high-range name; and
`Bc6H` asked of an eight-bit source drops its alpha and spends its precision above one, where an
eight-bit source has nothing. Each says so by name.

### Which way up

Everything here is **top-left-first**: row zero is the top row, and `SpriteRect.Y` is measured down
from the top of a sheet. PNG, DDS and KTX2 all agree with that by construction.

TGA does not — it stores an origin bit, and both settings are files a paint program emits. The bit is
honoured, and `TgaOrientationTests` builds the same asymmetric picture both ways up from the format
and requires the same pixels back from each. This is worth a test rather than a comment because a
flipped albedo and a flipped normal map both render *plausibly*.

## Using it

Nothing to call. Drop the file in; the `.meta` beside it carries the settings.

```csharp no-compile="a fragment; the settings object is what the .meta deserialises into"
new TextureImportSettings {
    Content = TextureContent.NormalMap,     // ⇒ linear, unit-length mips, BC5
    Compression = TextureCompression.Automatic,
    GenerateMips = true,
    MaxSize = 2048
}
```

To read a format nothing here reads, implement `IImageDecoder` and hand the set to the importer's
constructor. `.exr`, `.tif` and `.webp` are the ones doc 08's table still asks for.

## Examples

**What DDS claims, and what it refuses.** `DdsDecoder` reads a plain 2D texture: one array element,
one face, any number of mip levels, in BC1/BC3/BC4/BC5/BC6H/BC7 or an eight-bit-a-channel
uncompressed layout, through either a DX10 extension header or a pre-D3D10 four-character code or bit
mask.

It refuses, by name and with an error rather than a guess:

| Refused | Why not just read it |
|---|---|
| Cube maps and texture arrays | DDS stores them element-major — one whole mip chain per face — and KTX2 stores them level-major. Half-reading gives six faces interleaved into the wrong levels, which is not an error anywhere |
| Volume textures | Same layout question, and nothing asks for one |
| BC2 | The engine has no BC2 format, and it has BC3's block size and colour half — so reading it as BC3 gives a picture with garbage alpha, which looks like a bad mask rather than a bug |
| Uncompressed 16- and 32-bit surfaces | They would have to be narrowed to a byte, which is the one thing a high-range image exists to avoid. BC6H is read, and so is `.hdr` |

The way out of all four is the same and the message says it: convert to `.ktx2`, which the pipeline
passes through untouched.

**Why there is no DDS library.** Doc 01 planned `Pfim` for DDS and TGA. Neither half was needed: TGA
was already read by `StbImageDecoder`, and DDS is a *container* over BCn that `Vixen.Core.Imaging`
has understood since it was written. What was missing was header parsing and a DXGI-to-`PixelFormat`
table.

## See also

- [Getting content into a running game](content-in-a-game.md)
- `docs/plan/08-asset-pipeline-and-addressables.md` — the importer table this is measured against
- `docs/plan/01-technology-decisions.md` — the image-codec licence decision, and the struck Pfim row
