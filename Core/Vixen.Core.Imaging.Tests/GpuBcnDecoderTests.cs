// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging.BlockCompression;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>Vixen's BC7 and BC6H decoders and encoders against the texture unit of a real GPU.</summary>
/// <remarks>
///     <para>
///         <b>Nothing in this comparison was written here except the bytes going in.</b> The oracle,
///         <c>Tools/Vixen.BcnOracle/bcn-metal-oracle.swift</c>, puts raw blocks in a compressed Metal
///         texture and reads back what the hardware returns. Every table in
///         <see cref="BptcTables" /> and every run of <see cref="Bc6HBlock.Layouts" /> was written
///         from the specification and has only this to check it: a fixture worked out from the same
///         reading of the same page would agree with any mistake.
///     </para>
///     <para>
///         <b>The corpus is forced, because random blocks are not uniform over what matters.</b> A
///         random byte is mode 0 half the time and mode 7 once in 256. So every mode is forced, and
///         within each mode every partition, rotation and index selection; everything else is noise,
///         which is a legal block whatever it says. A wrong anchor shifts every index after it and a
///         wrong pattern entry swaps a texel's subset, so one of these fails on nearly every block
///         of its case rather than on a rare one.
///     </para>
///     <para>
///         <b>Two directions, and they are not the same question.</b> Arbitrary blocks check the
///         decoder over bit patterns the encoder never writes. The encoder's own output, at every
///         quality, checks that what ships means what the search thought it meant.
///     </para>
///     <para>
///         ⚠ <b>BC7's reserved mode is left out.</b> A first byte of zero names no mode. Vixen returns
///         zero in every channel; this GPU returns opaque black. Nothing writes such a block. BC6H's
///         four reserved modes are in, and those agree.
///     </para>
/// </remarks>
public sealed class GpuBcnDecoderTests {
    /// <summary>How many arbitrary blocks each mode-partition-option combination gets.</summary>
    const int BlocksPerCase = 256;

    /// <summary>How many a mode with no partition gets: it has only one case to spend them on.</summary>
    const int BlocksPerUnpartitionedMode = 4096;

    /// <summary>The eight BC7 modes.</summary>
    public static TheoryData<int> Bc7Modes => [0, 1, 2, 3, 4, 5, 6, 7];

    /// <summary>The fourteen BC6H modes, with both signednesses.</summary>
    public static TheoryData<int, bool> Bc6HModes {
        get {
            var data = new TheoryData<int, bool>();

            for (var mode = 0; mode < Bc6HBlock.ModeCount; mode++) {
                data.Add(mode, false);
                data.Add(mode, true);
            }

            return data;
        }
    }

    /// <summary>Every quality.</summary>
    public static TheoryData<BlockQuality> Qualities => [BlockQuality.Fast, BlockQuality.Balanced, BlockQuality.Best];

    /// <summary>⚠ The guard against a suite that quietly checked nothing.</summary>
    [Fact]
    public void TheCorpusIsTheSizeItIsMeantToBe() {
        Assert.Equal(8, Bc7Modes.Count);
        Assert.Equal(28, Bc6HModes.Count);

        // Sixteen, sixty-four, sixty-four, sixty-four patterns, then four rotations of two selections,
        // four rotations, one, and sixty-four: that is everything mode 0 to 7 can name.
        Assert.Equal(16 * BlocksPerCase, Bc7Corpus(0).Length / 16);
        Assert.Equal(64 * BlocksPerCase, Bc7Corpus(1).Length / 16);
        Assert.Equal(8 * BlocksPerCase, Bc7Corpus(4).Length / 16);
        Assert.Equal(4 * BlocksPerCase, Bc7Corpus(5).Length / 16);
        Assert.Equal(BlocksPerUnpartitionedMode, Bc7Corpus(6).Length / 16);
        Assert.Equal(32 * BlocksPerCase, Bc6HCorpus(0).Length / 16);
        Assert.Equal(BlocksPerUnpartitionedMode, Bc6HCorpus(10).Length / 16);
    }

    [Theory]
    [MemberData(nameof(Bc7Modes))]
    public void OurBc7DecoderReadsEveryCaseOfAModeTheWayTheGpuDoes(int mode) {
        var blocks = Bc7Corpus(mode);

        foreach (var block in Chunks(blocks)) {
            Assert.Equal(1 << mode, block.Span[0] & ((1 << (mode + 1)) - 1));
        }

        CompareBc7($"mode {mode}", blocks);
    }

    [Theory]
    [MemberData(nameof(Bc6HModes))]
    public void OurBc6HDecoderReadsEveryCaseOfAModeTheWayTheGpuDoes(int mode, bool signed) =>
        CompareBc6H($"mode {mode + 1}", Bc6HCorpus(mode), signed);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReservedBc6HModeIsBlackOnBothSides(bool signed) {
        var blocks = new byte[4 * 4 * 16];
        new Random(0x0BAD).NextBytes(blocks);

        for (var index = 0; index < blocks.Length / 16; index++) {
            // 10011, 10111, 11011 and 11111, least significant bit first.
            blocks[index * 16] = (byte)((blocks[index * 16] & 0xE0) | (0b10011 + ((index % 4) << 2)));
        }

        CompareBc6H("reserved", blocks, signed);
    }

    /// <summary>
    ///     ⚠ Sixteen-bit endpoints have no special cases, so signed mode 14's lowest code finishes one
    ///     past the largest finite half. Random blocks land on it about once in a blue moon, so it is
    ///     written by hand; both sides have to say negative infinity.
    /// </summary>
    [Fact]
    public void TheLowestSixteenBitCodeIsNegativeInfinityOnBothSides() {
        var block = new byte[16];

        SetBits(block, 0, 5, Bc6HBlock.Layouts[13].ModeBits);

        // r0[10:15] runs from bit 15, so the first bit after r1's four is the sign.
        SetBits(block, 39, 1, 1);

        CompareBc6H("mode 14 at its lowest code", block, true);

        var rgb = new ushort[48];

        BlockCompressor.DecodeHdrBlock(block, rgb, true);

        Assert.Equal(0xFC00, rgb[0]);
    }

    [Theory]
    [MemberData(nameof(Qualities))]
    public void TheGpuReadsWhatOurBc7EncoderWroteTheWayWeDo(BlockQuality quality) {
        var seen = new int[Bc7Block.ModeCount];

        foreach (var (name, make) in ReferenceImages.LowDynamicRange) {
            var blocks = BlockCompressor.Encode(make(64), PixelFormat.Bc7RgbaUNorm, new() { Quality = quality }).Level(0).ToArray();

            foreach (var block in Chunks(blocks)) {
                seen[System.Numerics.BitOperations.TrailingZeroCount(block.Span[0])]++;
            }

            CompareBc7($"{name} at {quality}", blocks);
        }

        var hard = HardBlocks(BlockQuality.Best);

        foreach (var block in Chunks(hard)) {
            seen[System.Numerics.BitOperations.TrailingZeroCount(block.Span[0])]++;
        }

        CompareBc7("hard blocks", hard);

        // A mode the encoder never wrote is a mode this half of the suite never checked.
        if (quality == BlockQuality.Best) {
            for (var mode = 0; mode < Bc7Block.ModeCount; mode++) {
                Assert.True(seen[mode] > 0, $"nothing in the corpus was encoded in BC7 mode {mode}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Qualities))]
    public void TheGpuReadsWhatOurBc6HEncoderWroteTheWayWeDo(BlockQuality quality) {
        var unsigned = BlockCompressor.Encode(
            ReferenceImages.HighDynamicRange(256), PixelFormat.Bc6HRgbUFloat, new() { Quality = quality }
        ).Level(0).ToArray();

        var signed = BlockCompressor.Encode(
            ReferenceImages.SignedHighDynamicRange(256), PixelFormat.Bc6HRgbSFloat, new() { Quality = quality }
        ).Level(0).ToArray();

        CompareBc6H($"unsigned at {quality}", unsigned, false);
        CompareBc6H($"signed at {quality}", signed, true);

        if (quality != BlockQuality.Best) {
            return;
        }

        var seen = new HashSet<int>();

        foreach (var block in Chunks(unsigned).Concat(Chunks(signed))) {
            seen.Add(ModeOf(block.Span));
        }

        Assert.True(seen.Count >= 4, $"the corpus reached only modes {string.Join(", ", seen.Order())}");
    }

    /// <summary>
    ///     Blocks of nothing in particular, encoded: the search reaches modes on noise that it
    ///     never reaches on anything a person would paint.
    /// </summary>
    static byte[] HardBlocks(BlockQuality quality) {
        var random = new Random(0x4A2D);
        var texture = new TextureData(PixelFormat.Rgba8UNorm, 256, 256, levelCount: 1);
        var pixels = texture.PixelSpan();

        var colours = new byte[3 * 4];

        for (var block = 0; block < 64 * 64; block++) {
            random.NextBytes(colours);

            var opaque = block % 3 != 0;
            var pattern = block % 64;
            var subsets = 1 + (block % 3);

            for (var texel = 0; texel < 16; texel++) {
                var subset = subsets switch {
                    1 => 0,
                    2 => BptcTables.TwoSubsets[(pattern * 16) + texel],
                    _ => BptcTables.ThreeSubsets[(pattern * 16) + texel]
                };

                var x = ((block % 64) * 4) + (texel % 4);
                var y = ((block / 64) * 4) + (texel / 4);
                var at = ((y * 256) + x) * 4;

                for (var channel = 0; channel < 4; channel++) {
                    pixels[at + channel] = (byte)Math.Clamp(colours[(subset * 4) + channel] + random.Next(-6, 7), 0, 255);
                }

                if (opaque) {
                    pixels[at + 3] = 255;
                }
            }
        }

        return BlockCompressor.Encode(texture, PixelFormat.Bc7RgbaUNorm, new() { Quality = quality }).Level(0).ToArray();
    }

    static byte[] Bc7Corpus(int mode) {
        var layout = Bc7Block.Modes[mode];
        var random = new Random(0x7C7 + mode);
        var variants = (1 << layout.PartitionBits) * (1 << layout.RotationBits) * (1 << layout.SelectionBits);
        var count = variants == 1 ? BlocksPerUnpartitionedMode : variants * BlocksPerCase;
        var blocks = new byte[count * 16];

        random.NextBytes(blocks);

        for (var index = 0; index < count; index++) {
            var block = blocks.AsSpan(index * 16, 16);
            var variant = index % variants;

            SetBits(block, 0, mode + 1, 1 << mode);
            SetBits(block, mode + 1, layout.PartitionBits + layout.RotationBits + layout.SelectionBits, variant);
        }

        return blocks;
    }

    static byte[] Bc6HCorpus(int mode) {
        var info = Bc6HBlock.Layouts[mode];
        var random = new Random(0x6C6 + mode);
        var count = info.Subsets == 2 ? Bc6HBlock.PartitionCount * BlocksPerCase : BlocksPerUnpartitionedMode;
        var blocks = new byte[count * 16];

        random.NextBytes(blocks);

        for (var index = 0; index < count; index++) {
            var block = blocks.AsSpan(index * 16, 16);

            SetBits(block, 0, info.ModeBitCount, info.ModeBits);

            // The partition is the last five bits of a two-subset header in every mode.
            if (info.Subsets == 2) {
                SetBits(block, 77, 5, index % Bc6HBlock.PartitionCount);
            }
        }

        return blocks;
    }

    static int ModeOf(ReadOnlySpan<byte> block) {
        var low = block[0] & 3;
        var field = low < 2 ? low : block[0] & 31;

        for (var mode = 0; mode < Bc6HBlock.ModeCount; mode++) {
            if (Bc6HBlock.Layouts[mode].ModeBits == field) {
                return mode;
            }
        }

        return -1;
    }

    static void SetBits(Span<byte> block, int start, int count, int value) {
        for (var bit = 0; bit < count; bit++) {
            var at = start + bit;
            var mask = (byte)(1 << (at & 7));

            block[at >> 3] = ((value >> bit) & 1) != 0 ? (byte)(block[at >> 3] | mask) : (byte)(block[at >> 3] & ~mask);
        }
    }

    static IEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] blocks) {
        for (var at = 0; at < blocks.Length; at += 16) {
            yield return blocks.AsMemory(at, 16);
        }
    }

    static void CompareBc7(string name, byte[] blocks) {
        if (Oracle() is not { } oracle) {
            return;
        }

        var reference = ExternalTools.Pipe(oracle, blocks, "bc7");
        var count = blocks.Length / 16;

        Assert.Equal(count * 64, reference.Length);

        Span<byte> ours = stackalloc byte[64];

        for (var index = 0; index < count; index++) {
            BlockCompressor.DecodeBlock(PixelFormat.Bc7RgbaUNorm, blocks.AsSpan(index * 16, 16), ours);

            if (!ours.SequenceEqual(reference.AsSpan(index * 64, 64))) {
                Assert.Fail(
                    $"bc7 {name} block {index}: Vixen says {Convert.ToHexString(ours)}, the GPU says "
                    + $"{Convert.ToHexString(reference.AsSpan(index * 64, 64))}. Block bytes: "
                    + Convert.ToHexString(blocks.AsSpan(index * 16, 16))
                );
            }
        }
    }

    static void CompareBc6H(string name, byte[] blocks, bool signed) {
        if (Oracle() is not { } oracle) {
            return;
        }

        var reference = ExternalTools.Pipe(oracle, blocks, signed ? "bc6hs" : "bc6h");
        var count = blocks.Length / 16;

        Assert.Equal(count * 96, reference.Length);

        Span<ushort> ours = stackalloc ushort[48];

        for (var index = 0; index < count; index++) {
            BlockCompressor.DecodeHdrBlock(blocks.AsSpan(index * 16, 16), ours, signed);

            for (var component = 0; component < 48; component++) {
                var at = ((index * 48) + component) * 2;
                var theirs = (ushort)(reference[at] | (reference[at + 1] << 8));

                // Negative zero and zero are one value; a half can come back from float as either.
                if ((ours[component] | theirs) == 0x8000) {
                    continue;
                }

                if (ours[component] != theirs) {
                    Assert.Fail(
                        $"bc6h{(signed ? "s" : "")} {name} block {index} component {component}: Vixen says "
                        + $"0x{ours[component]:X4}, the GPU says 0x{theirs:X4}. Block bytes: "
                        + Convert.ToHexString(blocks.AsSpan(index * 16, 16))
                    );
                }
            }
        }
    }

    static string? Oracle() {
        if (ExternalTools.BcnMetalOracle is { } oracle) {
            return oracle;
        }

        ExternalTools.Missing(
            "a Metal GPU with BC formats, read through the oracle",
            "On a Mac, build it: sh Tools/Vixen.BcnOracle/build-metal.sh."
        );

        return null;
    }
}
