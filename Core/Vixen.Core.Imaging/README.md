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

**Two more that are not it either, and both exist for the same kind of reason.** A baked map is a
measurement and eight bits is not enough of one, so `PngCodec.Encode16` writes grey, RGB or RGBA at
sixteen bits, `Encode8` does the same at eight so a one-channel map is one channel in the file, and `ExrCodec` writes 16- and 32-bit float scanline OpenEXR with ZIP compression. ⚠ **The
EXR writer is managed on purpose**: the reference library is four native binaries to build and sign per
platform, and a flat image with one compression method is about two hundred lines over the deflate
this assembly already has. It reads what it writes, and a tiled, deep or multi-part file is refused.
Checked against macOS ImageIO and FFmpeg, neither of which shares a line with it.

## Padding and resizing

`ImagePadding` fills a picture's background from its islands — infinite dilation, or dilation by a
distance followed by transparency, a colour or a pull-push diffusion — on `MapBaker.Dilate`'s two rules:
coverage is read and never written, and a round commits after it finishes. `ImageResize` changes a
`FloatImage`'s size with a box, bilinear or nearest filter. Both are for exporting a texture, where the
background bleeds into mips and a smaller size has to average every texel. See
[padding and resizing](../../docs/guide/core/padding-and-resizing.md).

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

**Validated against Khronos's `ktx validate`,** over every format `VkFormats` names and every
container shape the writer can produce — mip chains, array layers, cube maps, an array of cube maps,
a single texel, a non-square block-compressed chain — with `--warnings-as-errors`. That is
`Ktx2ConformanceTests`, and it needs `brew install ktx`; without the tool it *skips*, and
`VIXEN_REQUIRE_EXTERNAL_TOOLS=1` turns every skip into a failure.

⚠ **All twenty-two files failed the first time it ran**, on five defects the hand-computed fixtures
had been agreeing with since they were written — which is the argument for the suite, stated as a
result rather than as methodology.

| what was wrong | what it was |
| --- | --- |
| every level's `byteOffset` | levels were packed end to end; the spec requires `mipPadding` to `lcm(texelBlockSize, 4)` |
| alpha's `channelType` | 3, the sample's position. Alpha's id is 15 |
| float formats' samples | no `SIGNED`, no `FLOAT`, and an integer's `sampleLower`/`sampleUpper` instead of ∓1.0f |
| sRGB formats' alpha sample | missing the `LINEAR` qualifier — alpha is not sRGB-encoded even in an sRGB format |
| BC3 and BC5's descriptors | one sample of 128 bits each; both are **two** 64-bit samples |
| ⚠ three `VkFormat` numbers | BC6H unsigned was 144 (**that is the signed block**), ETC2 RGB8A1 was 151 and ETC2 RGBA8 was 153 (**that is EAC R11**) |

The `VkFormat` row is the one worth dwelling on. `From` and `To` agree with each other whatever the
number is, so a round trip could never see it; a file carrying the unsigned BC6H payload this engine
encodes was telling every reader in the world to decode it as signed.

**Still not verified: reading a file this did not write.** The suite proves other people's tools
accept ours. Nothing yet feeds a file produced by `ktx create` — or by any other writer — into
`Ktx2.Read`, so the reader's tolerance of layouts Vixen would not choose is untested.

**Level offsets now carry padding, and a mip tail reads it.** `Ktx2Layout.DataLength` and
`TailLength` are computed from real offsets, so streaming is unaffected in correctness; a tail is a
handful of bytes longer than the sum of its levels and the loader discards them. `R8UNorm`'s
sixteen-square chain went from 341 bytes to 344.

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

R, RG and RGBA reduce in eight-bit, sixteen-bit norm, half and single float, under the same
`MipOptions`. Eight-bit chains keep a lookup table for the sRGB curve; the wide formats have too many
values for one, so they run the curve per texel, a row at a time across the thread pool. A direction
is the usual zero-to-one encoding whatever the format, floats included. Float above one is averaged
as it is, and only `Srgb` clamps, because the curve ends at one.

Block-compressed formats cannot be reduced, and that is not a gap: a chain is generated *before*
compression, because reducing compressed blocks means decode, filter, re-encode, and each round
loses more than the filter gains. Asking for the other order says so. A volume with a chain is
refused too: a box of four texels has no third axis.

⚠ **Two defects turned up writing the second path beside the first.** A fully transparent footprint
in an sRGB cut-out came out white, because the fallback added raw bytes to a sum that was about to be
encoded from linear light. And only the first image of a level was filled, so an array's other layers
and a cube's other faces stayed zero with nothing to say so.

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

## DDS

`DdsCodec` writes the DX10 header, always, for every BC format and the uncompressed formats in its
table, with mips, arrays and cube maps. `Read` takes back what `Write` wrote and refuses anything else
by name. The tolerant reader for files other tools wrote already exists and is import-time code:
`Vixen.Interchange.DdsDecoder`.

⚠ **DDS orders by element and this assembly by level**, and a 2D texture is the same either way, so a
round trip passes with neither direction transposing. The cube test asserts the bytes after face
zero's whole chain instead.

**Checked with FFmpeg and ImageIO, neither of which shares a line with it.** FFmpeg reads BC1, BC3,
BC4 and BC5 and `sips` reads BC1, BC3, BC7 and RGBA8, each held to within three levels of Vixen's own
decode, which is proved separately against a GPU. ⚠ FFmpeg has no BC7 at all (*"Unsupported DXGI
format 98"*). BC6H and the sixteen-bit and float formats have been past no outside reader in a DDS.

## Block compression

`BlockCompressor.Encode` produces **BC1, BC3, BC4, BC5, BC7 and BC6H in both signednesses**. It is
build-time code: the runtime never decodes a block, because a shipped texture is already in the
format the GPU samples. `Decode` exists so an editor can preview what compression will do and so the
encoders can be tested against something other than themselves.

Sources are eight-bit, sixteen-bit norm, half or single float, with one, two or four channels. BC1 to
BC5 and BC7 hold eight bits, so anything wider rounds to the nearest byte and float clamps at one.
There is no sRGB form of a sixteen-bit format, so an sRGB target for one is refused until
`SourceIsSrgbEncoded` says the values are encoded.

**BC1, BC3, BC4 and BC5** fit the principal axis of the block's own colours and refine the endpoints
by least squares, and those formats have no modes left to choose between. BC4 picks between its two
interpolation modes by measured error, which is the whole of the decision. `BlockQuality` changes
nothing about them.

**BC7 and BC6H search every mode.** Each scores every partition by the residual around one line per
subset, which is what no choice of endpoints can remove, fits the likeliest few for real and keeps the
best by summed integer error. `Fast` tries one-subset modes and one partition guess, `Balanced` every
mode over the likeliest partitions, and `Best` far more partitions and an endpoint walk on the
winner, because rounding a least-squares answer does not find the best code. Nothing allocates per
block, and the arithmetic that compares candidates is integer.

**A whole texture runs on the thread pool and writes the same bytes on any number of threads**,
because each block is a pure function of its own texels. `BlockEncodeOptions` carries the quality,
the thread limit, a `CancellationToken` and an `IProgress<BlockEncodeProgress>`.

⚠ **BC6H has a format limit that no search gets past.** Its deltas measure every endpoint from one
base, so a two-subset block with a hard edge between far-apart radiances can only use the six-bit
untransformed mode. That is why the HDR row below moves by two decibels where the three-colour one
moves by thirty: it is mostly the light edge's blocks, and they were always going to be expensive.

### What each level is worth

Measured on an Apple M1 Max, ten cores, .NET 10, Release, one process, by
`dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Imaging`. Every picture is a formula, in
`ReferenceImages`. *Old* is the mode-6 and mode-11 encoder this replaced, measured before it went.
BC7 is RGBA PSNR at 1024², peak 255. BC6H is RGB PSNR in half-float bit patterns, where 1024 units is
one stop, peak 31 743.

| picture | format | old | Fast | Balanced | Best | floor held, Balanced |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| gradient | BC7 | 54.69 | 54.24 | 58.70 | 63.13 | 50.0 |
| two-colour edges | BC7 | 52.39 | 56.92 | ∞ | ∞ | 90.0 |
| three-colour edges | BC7 | 26.52 | 53.65 | 58.61 | 58.64 | 57.0 |
| filtered noise | BC7 | 32.46 | 36.47 | 37.68 | 37.81 | 36.5 |
| independent alpha | BC7 | 30.14 | 33.22 | 33.68 | 33.75 | 33.0 |
| normal map | BC7 | 37.40 | 42.69 | 44.37 | 44.59 | 43.5 |
| HDR light edge | BC6H | 69.26 | 70.30 | 71.49 | 71.84 | 70.0 |
| HDR, signed | BC6H signed | — | 64.27 | 64.47 | 64.65 | 63.0 |

`BlockQualityTests` holds the floors at 256², and all three levels. ⚠ With partitioned modes switched
off, 73 tests in that suite and the GPU's go red and three-colour edges falls to 31 dB, or to 26 at
`Fast`, which is what mode 6 alone always scored. Two flat colours along any of the 64 two-subset
patterns come back within 1 level, three along any three-subset pattern within 3, and two radiances on
any of the 32 BC6H patterns within 64 half-float patterns, about 0.6%. Any one flat colour is exact.

Seconds to encode one level, on every core:

| picture | size | old, one thread | Fast | Balanced | Best |
| --- | --- | ---: | ---: | ---: | ---: |
| filtered noise, BC7 | 2048² | 2.66 | 0.50 | 2.71 | 6.29 |
| independent alpha, BC7 | 2048² | — | 0.52 | 2.24 | 5.05 |
| HDR light edge, BC6H | 2048² | 0.73 | 0.14 | 0.61 | 5.41 |
| filtered noise, BC7 | 4096² | 11.69 | 1.91 | 9.97 | 26.81 |
| independent alpha, BC7 | 4096² | — | 2.51 | 10.13 | 21.75 |
| HDR light edge, BC6H | 4096² | 2.90 | 0.69 | 2.84 | 25.01 |

Two-colour edges cost a twentieth of that: a block of one colour takes mode 5 at once, and a block the
first modes hold exactly stops the search.

### What it was checked against

**A GPU, which is the strongest thing available and needs no download.** Metal does have BC formats:
this file used to say it had none, and `MTLDevice.supportsBCTextureCompression` is true on the Apple
Silicon machine that sentence was written on. So `Tools/Vixen.BcnOracle/bcn-metal-oracle.swift` puts
raw blocks in a compressed texture and reads back what the texture unit returns, and
`GpuBcnDecoderTests` requires identical texels. Every mode is forced, and within it every partition,
rotation and index selection, with noise for the rest:

| | blocks | agreed |
| --- | ---: | ---: |
| BC7 modes 1, 2, 3, 7 | 16 384 each | all |
| BC7 modes 0, 6 | 4 096 each | all |
| BC7 mode 4, mode 5 | 2 048, 1 024 | all |
| BC6H modes 1 to 10, each signedness | 8 192 each | all |
| BC6H modes 11 to 14, each signedness | 4 096 each | all |
| BC6H reserved modes | 16 each signedness | all |
| the BC7 encoder's output, every quality | 16 896 | all |
| the BC6H encoder's output, every quality, each signedness | 24 576 | all |

The tables were written from the specification and agreed on the first run, which is what made the
suite worth sabotaging. One wrong anchor fails exactly mode 2, the only mode that reaches that
pattern; one reversed bit run fails exactly mode 13. A mode the encoder never wrote is a failure too,
at `Best`.

⚠ **Two things the hardware said that nothing here had.** BC7's reserved mode reads as opaque black on
this GPU where Vixen returns zero; nothing writes such a block, so the corpus leaves it out. And
"no pair of endpoints can reach infinity" holds for unsigned BC6H only: signed mode 14 has no special
case at its lowest code, so −32768 finishes one past the largest finite half and the GPU returns
0xFC00. The encoder treats −32767 as the lowest code.

**[bcdec](https://github.com/iOrange/bcdec), an unrelated decoder,** checks the same way, over four
thousand arbitrary blocks per format and then the blocks Vixen's encoder wrote. That is
`BcnReferenceDecoderTests`; the oracle is built by `Tools/Vixen.BcnOracle/build.sh`, which downloads
bcdec into a cache **outside the tree**, and the [tool's README](../../Tools/Vixen.BcnOracle/README.md)
explains why. Without either oracle the suites skip; `VIXEN_REQUIRE_EXTERNAL_TOOLS=1` makes a skip a
failure. ⚠ Since BC7 and BC6H went to every mode, that suite's corpus covers them but it has **not
been run**: building it means downloading bcdec, and the GPU suite was the one with the stronger claim.

⚠ **BC1, BC3, BC4 and BC5 disagreed with bcdec on the first block.** All four truncated where the
specification divides — `(2·RGB0+RGB1)/3`, `((7−s)·RED0+s·RED1)/7` and the rest are divisions of
*unpacked reals*, and an `int` division shortens every interpolated step by up to a level. BC1 did it
twice over, expanding its endpoints to bytes with bit replication before interpolating, which rounds
twice and is itself off by a level for four of the thirty-two red and blue values and ten of the
sixty-four green ones. BC7 and BC6H were right all along because *their* specs are bit-exact and say
`+32 >> 6` in as many words; the engine rounded where it was told to and truncated where it was left
to choose.

**What that cost was correctness, not measurable quality — and saying otherwise would be the easy
lie.** Mean absolute round-trip error over twenty thousand blocks of texels on a line moved from
5.0601 to 5.0520 for BC1 and from 2.3617 to 2.3623 for BC4, which is to say it improved by a sixth
of a per cent in one place and got imperceptibly *worse* in the other. The encoder scores its
endpoints against this same palette, so it had simply re-fit around the bias. What was actually
wrong is that the colours the hardware returns for a block Vixen wrote were not the colours Vixen's
own preview and error metric named.

⚠ **One disagreement went the other way, and we are the ones who are right.** A BC3 block's alpha
half is a BC4 block, but bcdec's `bcdec_bc3` routes it through a truncating fast path its own
`bcdec_bc4` does not use, so the reference decodes the identical sixty-four bits two different ways.
For endpoints 96 and 13 at index 5 the exact value is 340/7 = 48.571; `bcdec_bc4` and Vixen say 49
and `bcdec_bc3` says 48. The suite checks BC3's alpha against `bc4` and records why.

**ASTC and ETC2 have no encoder here and are not getting one in managed code.** Doc 03 gives the
reason — ASTC encoding is measured in minutes per gigabyte outside a vectorised native encoder. Both
formats have sizes, block extents and KTX2 numbers so a build with the native encoder can ship them,
and asking `BlockCompressor` for one names what is missing.

What *is* measured, over twenty thousand random blocks each: texels lying on a line come back within
40 for BC1 and BC3, 21 and 23 for BC4 and BC5, and 9 for BC7 — the formats' own ordering, four steps
along the line against eight against sixteen. The tests hold those as bounds.

## Still to come

The compute form of the IBL convolutions from doc 03.

Licensed under Apache-2.0.
