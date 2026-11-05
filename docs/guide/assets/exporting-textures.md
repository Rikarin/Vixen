---
title: Exporting textures
slug: assets/exporting-textures
kind: guide
area: Assets
summary: From an image to a file an engine loads — a mip chain from source pixels, BC blocks at a chosen quality, and KTX2 or DDS around them.
api: [T:Vixen.Core.Imaging.BlockCompression.BlockQuality, T:Vixen.Core.Imaging.BlockCompression.BlockEncodeOptions, T:Vixen.Core.Imaging.BlockCompression.BlockEncodeProgress, T:Vixen.Core.Imaging.DdsCodec, T:Vixen.Core.Imaging.DdsException, T:Vixen.TextureGraph.TextureRawImage]
tags: [assets, textures, ktx2, dds, compression, bc7, bc6h]
since: 0.2
status: preview
related: [assets/importing-textures, rendering/texture-streaming]
---

## What it is

Three steps, each one a call, and none of them knowing about the others:

| Step | Call | What it does |
|---|---|---|
| Reduce | `MipChain.Generate` | Fills the levels below the first, from the source pixels and in the source's own format |
| Compress | `BlockCompressor.Encode` | Turns every level into BC blocks, on the thread pool |
| Contain | `Ktx2.Write`, `DdsCodec.Write` | Puts a header round the result |

The order is not a preference. A chain is generated *before* compression, because reducing blocks
means decoding, filtering and re-encoding, and each round of that loses more than the filter gains.

## What it is for

Anything that hands a texture to something outside this repository: a build's content step, a command
line tool, an authoring application's export. Sources come in eight-bit, sixteen-bit unsigned norm,
half and single float, because that is what a PNG-16, an EXR or a texture graph hands you. BC1 to BC5
and BC7 hold eight bits, so wider sources round down to them; BC6H is the only format that keeps
range and takes float or sixteen-bit norm.

What it does not do is choose. Whether a texture is colour, a normal map or a mask is the caller's
statement, in `MipOptions`, and which format it ends up in is the caller's too.

## Using it

```csharp no-compile="a fragment; the pixels, the progress bar and the token are the caller's"
var texture = new TextureData(PixelFormat.Rgba16UNorm, width, height);
pixels.CopyTo(texture.LevelSpan(0));
MipChain.Generate(texture, MipOptions.Colour);

var encoded = BlockCompressor.Encode(texture, PixelFormat.Bc7RgbaUNormSrgb, new() {
    Quality = BlockQuality.Balanced,
    SourceIsSrgbEncoded = true,
    Progress = new Progress<BlockEncodeProgress>(report => bar.Value = report.Fraction),
    CancellationToken = token
});

File.WriteAllBytes("albedo.ktx2", Ktx2.Write(encoded));
File.WriteAllBytes("albedo.dds", DdsCodec.Write(encoded));
```

`BlockQuality` only matters to BC7 and BC6H, which are the two with a search to spend time on. The
result does not depend on `MaxDegreeOfParallelism`: every block is a function of its own texels, so
the bytes are the same on one thread as on sixteen, and a content-addressed cache can key on the
inputs. `BlockEncodeOptions.CancellationToken` stops the encode between runs of blocks and throws
`OperationCanceledException`; `Progress` is told about whole runs, in order, from whichever thread
finished one.

`SourceIsSrgbEncoded` is there because there is no sRGB form of `Rgba16UNorm`. Naming an sRGB target
for such a source is otherwise refused, since the hardware would apply a transfer function nobody
asked for. `MipOptions.Colour` says the same thing to the chain.

A texture graph's output reaches this through `TextureBake.ReadRaw`, which keeps the format the
image was computed in:

```csharp no-compile="a fragment; the evaluator, the plan and the uploads are the caller's"
using var bake = evaluator.Evaluate(plan, uploads.Externals);

var raw = bake.ReadRaw(output);        // R16Float, Rgba32Float, whatever the plan made
var texture = raw.ToTexture(levelCount: 0);

MipChain.Generate(texture, MipOptions.Linear);
```

## Examples

**A one-channel map stays one channel.** `PngCodec.Encode8` and `Encode16` write grey, and BC4 is the
compressed form of the same thing, so a roughness map does not have to pay for three channels it does
not hold.

**A height field in BC6H.** `Rgba32Float` to `Bc6HRgbSFloat` keeps a negative displacement, and the
unsigned format clamps it to zero. Infinity and NaN in a source are clamped on the way in, because
neither has anywhere to go in the block.

**DDS for an array or a cube.** `DdsCodec` transposes both ways: DDS stores every mip of one face and
then the next, `TextureData` every face of one level and then the next. `DdsCodec.Read` takes what
`Write` wrote. A DDS from another tool is `DdsDecoder`'s job, on the import side.

## See also

- [Importing textures](importing-textures.md): the other direction.
- [Streaming texture mip tails](../rendering/texture-streaming.md): why KTX2 stores its levels
  smallest first.
- `Core/Vixen.Core.Imaging/README.md`: what each quality level costs and buys, measured, and what
  the block formats were checked against.
