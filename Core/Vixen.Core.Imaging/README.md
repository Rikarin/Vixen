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

**Validated against [bcdec](https://github.com/iOrange/bcdec), an unrelated decoder,** in both
directions: four thousand arbitrary blocks per format put through both decoders and required to
produce identical texels, and then the blocks Vixen's *encoder* wrote for a real image read back by
the reference. That is `BcnReferenceDecoderTests`; the oracle is built by
`Tools/Vixen.BcnOracle/build.sh`, which downloads bcdec into a cache **outside the tree** — nothing
third-party is committed, and the [tool's README](../../Tools/Vixen.BcnOracle/README.md) explains why
that is the right call rather than a shortcut. Without the oracle the suite skips;
`VIXEN_REQUIRE_EXTERNAL_TOOLS=1` makes a skip a failure.

⚠ **BC7 and BC6H agreed on every block. BC1, BC3, BC4 and BC5 disagreed on the first one.** All four
truncated where the specification divides — `(2·RGB0+RGB1)/3`, `((7−s)·RED0+s·RED1)/7` and the rest
are divisions of *unpacked reals*, and an `int` division shortens every interpolated step by up to a
level. BC1 did it twice over, expanding its endpoints to bytes with bit replication before
interpolating, which rounds twice and is itself off by a level for four of the thirty-two red and
blue values and ten of the sixty-four green ones. BC7 and BC6H were right all along because *their*
specs are bit-exact and say `+32 >> 6` in as many words; the engine rounded where it was told to and
truncated where it was left to choose.

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

**What the comparison does not cover.** BC7 and BC6H are checked over **one mode each** — mode 6 of
eight and mode 11 of fourteen — because that is all `Bc7Block` and `Bc6HBlock` read; a block in any
other mode throws rather than decoding. The corpus forces the mode bits. The other seven BC7 modes
and thirteen BC6H modes are unverified in both directions and will stay so until something encodes
them. Nothing here has been past a **GPU**: this machine is Apple Silicon and Metal has no BC
formats at all, so the hardware's own decoder is not available as a third opinion.

What *is* measured, over twenty thousand random blocks each: texels lying on a line come back within
40 for BC1 and BC3, 21 and 23 for BC4 and BC5, and 9 for BC7 — the formats' own ordering, four steps
along the line against eight against sixteen. The tests hold those as bounds.

## Still to come

IBL prefiltering — GGX cubemap prefiltering and SH-9 irradiance projection — is owed from doc 03,
along with alpha-weighted mips and normal-map renormalisation. The DDS reader doc 03 names for legacy
interop is not written either.

Licensed under Apache-2.0.
