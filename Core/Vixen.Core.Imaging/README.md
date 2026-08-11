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

## No *authoring* image codec, on purpose

Nothing here decodes the formats an artist saves. Reading a JPEG, a TGA or a 16-bit interlaced PNG is
import-time work, and ADR-015 keeps a general imaging library out of every runtime assembly — for its
licence as much as its size. What ships is KTX2, where the bytes in the file are the bytes the GPU
wants, so loading a texture is a header parse and an upload.

`PngCodec` is not that, and the distinction is the whole reason it is allowed to be here. It reads
and writes one thing — baseline 8-bit RGBA, non-interlaced — in about two hundred lines over
`System.IO.Compression`, and it exists because a *picture somebody is going to look at* has to be
openable in a file browser. Three things in this repository write one: the golden-image suites, the
UI baselines, and `--vixen-capture`. None of them can reference the others, so the encoder sits under
all three. See [capturing a frame](../../docs/guide/rendering/capturing-a-frame.md).

## KTX2

**Level data is stored smallest first.** The level index is ordered largest first and the bytes it
points at run the other way, so a streaming loader can read the small mips off the front of the file
and show something before the rest arrives. It is the one part of the format that reads like a
mistake and is not, so it has a test of its own.

**And the loader that bargain was for.** `Ktx2.ReadLayout` parses the header and the level index —
`80 + 24n` bytes at the front — and answers what the texture is and where each of its levels lives,
having read none of them. `Ktx2.ReadTail` and `ReadTailAsync` then read levels *n* through the
smallest, which is one contiguous run from the front of the level data: one seek and one read
whatever the resolution asked for, and the large levels are never touched. A tail decodes to a
*complete smaller* `TextureData` rather than a larger one with holes, so a partially streamed texture
is created, viewed and sampled by code that knows nothing about streaming.

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

Box filter, and **what "average" means is the caller's statement about what the texture holds**, not
something guessed from the pixel format. A normal map and an albedo map are both `Rgba8UNorm`; a mask
packed into an sRGB format is neither colour nor a direction. `MipOptions` is where that is said, and
in practice the importer says it from the `.meta` file.

| | what it changes |
| --- | --- |
| `Srgb` | averages in linear light. Half black and half white is 188, not 127 |
| `AlphaWeighted` | a transparent texel's colour gets no vote — the fix for the dark halo around distant foliage |
| `RenormaliseNormals` | reconstructs, averages and normalises directions. Two-channel maps get their z back first |

The result is **rounded, not truncated**. Truncating loses half a level per step and a chain is ten
steps deep, so the smallest mips come out visibly darker than the largest.

Only uncompressed eight-bit formats can be reduced, and that is not a gap: a chain is generated
*before* compression, because reducing compressed blocks means decode, filter, re-encode, and each
round loses more than the filter gains. Asking for the other order says so.

## Image-based lighting

The split-sum approximation, in its three pieces. `SphericalHarmonicsL2.Project` is diffuse — nine
RGB numbers that reproduce any environment's irradiance to about a per cent, which is what makes a
light probe small enough to put one in every room. `EnvironmentPrefilter.Specular` is the GGX
convolution, one mip level per roughness with level zero copied because a mirror reflects what is
there. `BrdfLut.Generate` is the BRDF's own response, which depends on nothing about the scene and is
the same texture for every game ever shipped.

`SphericalHarmonicsL2.Irradiance` returns irradiance **divided by π** — the quantity a shader
multiplies by albedo. That factor is the classic "everything is 3.14 times too bright" bug, so it is
asserted exactly rather than approximately.

**A cube map's texels do not cover equal amounts of sky.** The one at the centre of a face covers
about five times what the one at its corner does, and every integral here is wrong by that factor if
it pretends otherwise. `CubeMap.SolidAngleOfTexel` returns the *exact* area of the texel's spherical
quadrilateral rather than the projected area of its centre, which means all of them sum to exactly 4π
at any face size — an equality to test against rather than a tolerance.

This is the CPU form. Doc 03 asks for a compute one as well, because reflection probes update at run
time; that is owed. What is here samples with nearest filtering inside a single face, so a
high-roughness level wants a high sample count — the saving grace being that those are the small
levels.

## Still to come

The DDS reader doc 03 names for legacy interop, and the compute form of the IBL convolutions.

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
