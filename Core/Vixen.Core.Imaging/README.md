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

## Block compression

`BlockCompressor.Encode` produces **BC1, BC3, BC4, BC5, BC7 and BC6H**. It is build-time code: the
runtime never decodes a block, because a shipped texture is already in the format the GPU samples.
`Decode` exists so an editor can preview what compression will do and so the encoders can be tested
against something other than themselves.

**BC1, BC3, BC4 and BC5 are complete.** They fit the principal axis of the block's own colours and
refine the endpoints by least squares, and those formats have no modes left to choose between. BC4
picks between its two interpolation modes by measured error, which is the whole of the decision.

**BC7 and BC6H write one mode each — mode 6 of eight, and mode 11 of fourteen.** Both are the
single-subset modes: one line through colour space, no partitioning. On smooth content that is the
mode a full encoder picks anyway; on a block with a hard edge running through it a partitioned mode
would be visibly better and this will not find it. What comes out is valid, correctly sized, and any
decoder reads it. Doc 03 calls for the native encoder for production quality and doc 01 registers
`ispc_texcomp` and `astcenc`; this is what a build uses until those are bound.

**ASTC and ETC2 have no encoder here and are not getting one in managed code.** Doc 03 gives the
reason — ASTC encoding is measured in minutes per gigabyte outside a vectorised native encoder. Both
formats have sizes, block extents and KTX2 numbers so a build with the native encoder can ship them,
and asking `BlockCompressor` for one names what is missing.

**Not validated against an independent BC decoder.** Same standard and same limit as KTX2: every
block layout is written from the specification and checked byte-for-byte against a hand-computed
block, which catches a misread but not a misunderstanding. Running the output past a GPU or a
reference decoder is owed.

What *is* measured, over twenty thousand random blocks each: texels lying on a line come back within
40 for BC1 and BC3, 21 and 23 for BC4 and BC5, and 9 for BC7 — the formats' own ordering, four steps
along the line against eight against sixteen. The tests hold those as bounds.

## Still to come

IBL prefiltering — GGX cubemap prefiltering and SH-9 irradiance projection — is owed from doc 03,
along with alpha-weighted mips and normal-map renormalisation. The DDS reader doc 03 names for legacy
interop is not written either.

Licensed under Apache-2.0.
