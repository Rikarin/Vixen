// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging.BlockCompression;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>Vixen's BCn decoders and encoders against an unrelated decoder's reading of the same bits.</summary>
/// <remarks>
///     <para>
///         The oracle is <see href="https://github.com/iOrange/bcdec">bcdec</see>, written by
///         somebody who has never seen this repository, wrapped by
///         <c>Tools/Vixen.BcnOracle/build.sh</c>. It is not vendored; the script downloads it into a
///         cache outside the tree, which is why this suite skips loudly rather than failing on a
///         machine that has not built it. See <see cref="ExternalTools" />.
///     </para>
///     <para>
///         <b>Two directions, and they are not the same question.</b> The first family feeds both
///         decoders arbitrary blocks and demands identical texels — that checks the <i>decoder</i>,
///         over bit patterns Vixen's encoder never produces. The second encodes real images with
///         Vixen's encoder and has the reference read the result — that checks the <i>encoder</i>,
///         which is the direction that ships. A block that only Vixen can read would pass the second
///         and fail the first; a decoder that agrees on random noise but an encoder that writes a
///         malformed header would pass the first and fail the second.
///     </para>
///     <para>
///         ⚠ <b>BC7 and BC6H are checked over one mode each, because that is all Vixen speaks.</b>
///         <see cref="Bc7Block" /> writes and reads mode 6 of eight and <see cref="Bc6HBlock" />
///         mode 11 of fourteen; a block in any other mode throws rather than decoding, so the corpus
///         forces the mode bits. That is a real limit on what this suite proves and it is the limit
///         of the code, not of the oracle: bcdec reads all fourteen. The seven BC7 modes and
///         thirteen BC6H modes Vixen does not write remain unverified in both directions, and will
///         stay that way until there is an encoder that emits them.
///     </para>
/// </remarks>
public sealed class BcnReferenceDecoderTests {
    /// <summary>How many arbitrary blocks each format is checked over.</summary>
    /// <remarks>
    ///     Small on purpose: the oracle is a subprocess and this is not a fuzzing budget. Every
    ///     disagreement these found was in the first hundred blocks, because a decoder that differs
    ///     differs on a whole class of inputs rather than on a rare one.
    /// </remarks>
    const int Blocks = 4096;

    /// <summary>The formats, and what one decoded block of each is in bytes.</summary>
    public static TheoryData<string> LowDynamicRange => ["bc1", "bc3", "bc4", "bc5", "bc7"];

    /// <summary>
    ///     ⚠ The guard against a suite that quietly checked nothing — a filter that matched no
    ///     format, or a corpus that shrank to zero blocks, fails here rather than passing silently.
    /// </summary>
    [Fact]
    public void TheCorpusIsTheSizeItIsMeantToBe() {
        Assert.Equal(5, LowDynamicRange.Count);
        Assert.Equal(4096, Blocks);
    }

    [Theory]
    [MemberData(nameof(LowDynamicRange))]
    public void OurDecoderReadsAnArbitraryBlockTheWayTheReferenceDoes(string format) {
        var pixelFormat = PixelFormatOf(format);

        Compare(format, "arbitrary", ArbitraryBlocks(format, pixelFormat.BlockSize()), pixelFormat);
    }

    [Theory]
    [MemberData(nameof(LowDynamicRange))]
    public void TheReferenceReadsWhatOurEncoderWroteTheWayWeDo(string format) {
        var pixelFormat = PixelFormatOf(format);
        var encoded = BlockCompressor.Encode(Source(), pixelFormat);

        Assert.Equal(64 * 64 / 16 * pixelFormat.BlockSize(), encoded.Level(0).Length);

        Compare(format, "encoded", encoded.Level(0).ToArray(), pixelFormat);
    }

    /// <summary>Puts every block through both decoders and demands the same texels.</summary>
    static void Compare(string format, string corpus, byte[] blocks, PixelFormat pixelFormat) {
        if (Oracle() is not { } oracle) {
            return;
        }

        var blockBytes = pixelFormat.BlockSize();
        var count = blocks.Length / blockBytes;
        var ours = new byte[64];

        foreach (var lanes in LanesOf(format)) {
            var input = lanes.Offset == 0 && lanes.Stride == blockBytes
                ? blocks
                : Slice(blocks, blockBytes, lanes.Offset, lanes.Stride);

            var reference = ExternalTools.Pipe(oracle, input, lanes.Oracle);

            Assert.Equal(count * 16 * lanes.Width, reference.Length);

            for (var index = 0; index < count; index++) {
                BlockCompressor.DecodeBlock(pixelFormat, blocks.AsSpan(index * blockBytes, blockBytes), ours);

                for (var texel = 0; texel < 16; texel++) {
                    foreach (var (theirLane, ourLane) in lanes.Pairs) {
                        var theirs = reference[((((index * 16) + texel) * lanes.Width) + theirLane)];
                        var mine = ours[(texel * 4) + ourLane];

                        Assert.True(
                            mine == theirs,
                            $"{format} ({corpus}, read by bcdec's {lanes.Oracle}) block {index} texel {texel} "
                            + $"channel {ourLane}: Vixen says {mine}, bcdec says {theirs}. Block bytes: "
                            + Convert.ToHexString(blocks.AsSpan(index * blockBytes, blockBytes))
                        );
                    }
                }
            }
        }
    }

    /// <summary>Pulls one fixed-size run out of every block, for a format checked in two passes.</summary>
    static byte[] Slice(byte[] blocks, int blockBytes, int offset, int stride) {
        var count = blocks.Length / blockBytes;
        var sliced = new byte[count * stride];

        for (var index = 0; index < count; index++) {
            blocks.AsSpan((index * blockBytes) + offset, stride).CopyTo(sliced.AsSpan(index * stride));
        }

        return sliced;
    }

    /// <summary>Which bcdec entry point answers for which channels, and over which bytes of the block.</summary>
    /// <param name="Oracle">The oracle's format argument.</param>
    /// <param name="Offset">Where in the block its input starts.</param>
    /// <param name="Stride">How many bytes of the block it reads.</param>
    /// <param name="Width">How many bytes per texel it writes.</param>
    /// <param name="Pairs">Its output lane against Vixen's RGBA lane.</param>
    readonly record struct Lanes(string Oracle, int Offset, int Stride, int Width, (int, int)[] Pairs);

    /// <summary>
    ///     ⚠ <b>BC3's alpha is checked against bcdec's BC4 and not against its BC3, because bcdec
    ///     decodes the same sixty-four bits two different ways and Vixen agrees with the right
    ///     one.</b> A BC3 block's alpha half <i>is</i> a BC4 block, but <c>bcdec_bc3</c> routes it
    ///     through a fast path that truncates the interpolation while <c>bcdec_bc4</c>, built with
    ///     <c>BCDEC_BC4BC5_PRECISE</c>, rounds it. For endpoints 96 and 13 at index 5 the exact value
    ///     is 340/7 = 48.571: <c>bcdec_bc4</c> and Vixen both say 49, <c>bcdec_bc3</c> says 48. The
    ///     specification's formula is a division of unpacked reals, so 49 is the value; this is the
    ///     one place in the whole comparison where the reference is the one that is wrong, and
    ///     pointing the check at its own precise entry point records that rather than hiding it.
    /// </summary>
    static Lanes[] LanesOf(string format) =>
        format switch {
            "bc4" => [new("bc4", 0, 8, 1, [(0, 0)])],
            "bc5" => [new("bc5", 0, 16, 2, [(0, 0), (1, 1)])],
            "bc3" => [
                new("bc3", 0, 16, 4, [(0, 0), (1, 1), (2, 2)]),
                new("bc4", 0, 8, 1, [(0, 3)])
            ],
            _ => [new(format, 0, format == "bc1" ? 8 : 16, 4, [(0, 0), (1, 1), (2, 2), (3, 3)])]
        };

    /// <summary>BC6H, whose texels are half-floats rather than bytes and so does not share the loop.</summary>
    [Fact]
    public void OurDecoderReadsAnArbitraryBc6HBlockTheWayTheReferenceDoes() {
        if (Oracle() is not { } oracle) {
            return;
        }

        var blocks = ArbitraryBlocks("bc6h", 16);
        var reference = ExternalTools.Pipe(oracle, blocks, "bc6h");

        Assert.Equal(Blocks * 16 * 3 * 2, reference.Length);

        Span<ushort> ours = stackalloc ushort[48];

        for (var index = 0; index < Blocks; index++) {
            BlockCompressor.DecodeHdrBlock(blocks.AsSpan(index * 16, 16), ours);

            for (var component = 0; component < 48; component++) {
                var at = ((index * 48) + component) * 2;
                var theirs = (ushort)(reference[at] | (reference[at + 1] << 8));

                Assert.True(
                    ours[component] == theirs,
                    $"bc6h block {index} component {component}: Vixen says 0x{ours[component]:X4}, "
                    + $"bcdec says 0x{theirs:X4}. Block bytes: "
                    + Convert.ToHexString(blocks.AsSpan(index * 16, 16))
                );
            }
        }
    }

    [Fact]
    public void TheReferenceReadsWhatOurBc6HEncoderWroteTheWayWeDo() {
        if (Oracle() is not { } oracle) {
            return;
        }

        var encoded = BlockCompressor.Encode(HdrSource(), PixelFormat.Bc6HRgbUFloat);
        var blocks = encoded.Level(0).ToArray();
        var count = blocks.Length / 16;

        var reference = ExternalTools.Pipe(oracle, blocks, "bc6h");
        Span<ushort> ours = stackalloc ushort[48];

        for (var index = 0; index < count; index++) {
            BlockCompressor.DecodeHdrBlock(blocks.AsSpan(index * 16, 16), ours);

            for (var component = 0; component < 48; component++) {
                var at = ((index * 48) + component) * 2;
                var theirs = (ushort)(reference[at] | (reference[at + 1] << 8));

                Assert.True(
                    ours[component] == theirs,
                    $"bc6h encoded block {index} component {component}: Vixen says 0x{ours[component]:X4}, "
                    + $"bcdec says 0x{theirs:X4}. Block bytes: "
                    + Convert.ToHexString(blocks.AsSpan(index * 16, 16))
                );
            }
        }
    }

    /// <summary>
    ///     Deterministic blocks, and for BC7 and BC6H the mode bits are forced to the one mode Vixen
    ///     reads. Everything above the mode field is noise, which is a legal block whatever it says:
    ///     a mode-6 BC7 block is seven mode bits, fifty-six endpoint bits, two parity bits and
    ///     sixty-three index bits, and no combination of those is invalid.
    /// </summary>
    static byte[] ArbitraryBlocks(string format, int blockBytes) {
        var blocks = new byte[Blocks * blockBytes];
        var random = new Random(0x1CE_B00C);

        random.NextBytes(blocks);

        for (var index = 0; index < Blocks; index++) {
            var at = index * blockBytes;

            switch (format) {
                // Mode 6 is six zeros then a one, least significant bit first.
                case "bc7":
                    blocks[at] = (byte)((blocks[at] & 0x80) | 0x40);
                    break;

                // Mode 11's five mode bits are 0b00011, least significant bit first.
                case "bc6h":
                    blocks[at] = (byte)((blocks[at] & 0xE0) | 0x03);
                    break;
            }
        }

        return blocks;
    }

    /// <summary>Something with gradients, hard edges and noise in it, because those compress differently.</summary>
    static TextureData Source() {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 64, 64, levelCount: 1);
        var pixels = texture.PixelSpan();
        var random = new Random(0x5EED);

        for (var y = 0; y < 64; y++) {
            for (var x = 0; x < 64; x++) {
                var at = ((y * 64) + x) * 4;
                var edge = x > 31 ? 255 : 0;

                pixels[at] = (byte)(x * 4);
                pixels[at + 1] = (byte)(y * 4);
                pixels[at + 2] = (byte)((edge + random.Next(0, 64)) & 0xFF);
                pixels[at + 3] = (byte)(((x + y) * 2) & 0xFF);
            }
        }

        return texture;
    }

    static TextureData HdrSource() {
        var texture = new TextureData(PixelFormat.Rgba16Float, 64, 64, levelCount: 1);
        var pixels = texture.PixelSpan();

        for (var y = 0; y < 64; y++) {
            for (var x = 0; x < 64; x++) {
                var at = ((y * 64) + x) * 8;

                // A range a tone mapper would see rather than a 0-1 one: BC6H exists for values
                // above white, and an encoder tested only on 0-1 is tested on a corner of itself.
                Write(pixels[(at + 0)..], (Half)(x * 0.75f));
                Write(pixels[(at + 2)..], (Half)(y * 0.25f));
                Write(pixels[(at + 4)..], (Half)((x + y) * 0.05f));
                Write(pixels[(at + 6)..], (Half)1f);
            }
        }

        return texture;

        static void Write(Span<byte> destination, Half value) =>
            BitConverter.TryWriteBytes(destination, value);
    }

    static PixelFormat PixelFormatOf(string format) =>
        format switch {
            "bc1" => PixelFormat.Bc1RgbaUNorm,
            "bc3" => PixelFormat.Bc3RgbaUNorm,
            "bc4" => PixelFormat.Bc4RUNorm,
            "bc5" => PixelFormat.Bc5RgUNorm,
            "bc7" => PixelFormat.Bc7RgbaUNorm,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No such format.")
        };

    static string? Oracle() {
        if (ExternalTools.BcnOracle is { } oracle) {
            return oracle;
        }

        ExternalTools.Missing("the BCn reference decoder", "Build it: sh Tools/Vixen.BcnOracle/build.sh.");

        return null;
    }
}
