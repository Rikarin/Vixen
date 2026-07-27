# Vixen.Core.Imaging

Engine texture data: the KTX2 container the runtime reads, mip chains, and the format arithmetic both
depend on.

Spec: [docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md), ADR-015.

```csharp
var texture = new TextureData(PixelFormat.Rgba8UNorm, 512, 512);
source.CopyTo(texture.LevelSpan(0));
MipChain.Generate(texture);
File.WriteAllBytes("hero.ktx2", Ktx2.Write(texture));
```

## No image codec, on purpose

There is no PNG decoder here. Decoding a PNG is import-time work, and ADR-015 keeps ImageSharp out of
every runtime assembly — for its licence as much as its size. What ships is KTX2, where the bytes in
the file are the bytes the GPU wants, so loading a texture is a header parse and an upload.

## KTX2

**Level data is stored smallest first.** The level index is ordered largest first and the bytes it
points at run the other way, so a streaming loader can read the small mips off the front of the file
and show something before the rest arrives. It is the one part of the format that reads like a
mistake and is not, so it has a test of its own.

**Implemented:** identifier, header, level index, data format descriptor, key/value data, and level
data for uncompressed and block-compressed formats. **Not implemented:** supercompression — neither
Basis Universal nor Zstd — and so no supercompression global data; it is written as absent and
refused on read. A build that wants smaller bundles compresses the chunk the texture lives in, which
doc 08 already does per bundle.

**Not yet done: validated against an independent KTX2 implementation.** The layout is written from
the specification and checked byte-for-byte against a file computed by hand in the tests, which
catches a misread of the spec but not a misunderstanding of it. Running Khronos's `ktx validate` over
what this writes is an owed step; until then, "valid KTX2" is a claim about intent.

`VkFormats` lists only the formats the engine actually ships textures in. A number nobody writes is a
number nobody has tested, and transcribing hundreds of entries from a header is hundreds of chances
to transcribe one wrongly.

## Mip chains

Box filter, and **the averaging is deliberately done on the stored values rather than in linear
light**. That is wrong for an sRGB texture, and it is left wrong here because the fix belongs one
layer up: the importer knows a texture's colour space — it is a setting in the `.meta` file — and a
filter that guessed from the format would get it wrong for the normal maps and masks that are stored
in an sRGB format and are not colour. `MipChain.Srgb` is the table for that caller to convert with.
Half black and half white is 188 in linear light and 127 if you average the encoded bytes; there is a
test that says so.

Only uncompressed eight-bit formats can be reduced, and that is not a gap: a chain is generated
*before* compression, because reducing compressed blocks means decode, filter, re-encode, and each
round loses more than the filter gains. Asking for the other order says so.

## Still to come

The block encoders — BCn, ASTC, ETC2 — which is where the native dependencies start (`astcenc` is in
doc 01's register). Nothing here encodes yet; `TextureData` and `Ktx2` carry compressed formats
through, they simply do not produce them. IBL prefiltering is owed from doc 08 as well.

Licensed under Apache-2.0.
