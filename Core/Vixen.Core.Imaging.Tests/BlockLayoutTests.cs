// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging.BlockCompression;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>
///     The external check on the block formats, and the counterpart of
///     <see cref="Ktx2Tests.ASingleTexelFileIsExactlyWhatTheSpecificationSaysItShouldBe" />: every
///     block here is written out bit by bit from the specification and handed to the decoder, and
///     every expected texel is worked out with the specification's own arithmetic. Nothing in this
///     file calls an encoder, so none of it can be satisfied by an encoder and a decoder agreeing
///     with each other about something that is wrong.
///     <para>
///         ⚠ <b>And two of the numbers in it were wrong anyway, which is the limit of the method.</b>
///         The BC4 fixture below said 185 where the specification's division gives 185.71, and it
///         said so because the code truncated and the arithmetic here was done the same way by the
///         same hand. A fixture computed from the spec catches a misread of the spec; it cannot catch
///         a misunderstanding of it. <see cref="BcnReferenceDecoderTests" /> is the suite that can.
///     </para>
/// </summary>
public sealed class BlockLayoutTests {
    /// <summary>
    ///     BC4's eight-value mode: red0 above red1, and the six between them in sevenths. The
    ///     indices are three bits each packed little-endian across the last six bytes, which is the
    ///     part that cannot be checked by inspection — 0xFAC688 is the pattern 0,1,2,…,7.
    /// </summary>
    [Fact]
    public void ABc4BlockInTheEightValueModeInterpolatesInSevenths() {
        ReadOnlySpan<byte> block = [200, 100, 0x88, 0xC6, 0xFA, 0x88, 0xC6, 0xFA];
        Span<byte> values = stackalloc byte[16];

        Bc4Block.Decode(block, values);

        // 200, 100, then (6·200+100)/7, (5·200+2·100)/7 … (200+6·100)/7, rounded.
        //
        // ⚠ This fixture used to say 185 for the first of them and the word at the end of the line
        // used to be "truncated" — 1300/7 is 185.71, and both the code and the number written down
        // here dropped the fraction because that is what an int division does. It is a bias, not a
        // rounding choice, and it took an outside decoder to see it: see BcnReferenceDecoderTests.
        Assert.Equal(
            [200, 100, 186, 171, 157, 143, 129, 114, 200, 100, 186, 171, 157, 143, 129, 114],
            values.ToArray()
        );
    }

    /// <summary>
    ///     And the six-value mode: red0 at or below red1, four values interpolated in fifths, and
    ///     the last two palette entries spent on exactly zero and exactly 255.
    /// </summary>
    [Fact]
    public void ABc4BlockInTheSixValueModeKeepsTwoEntriesForTheExtremes() {
        ReadOnlySpan<byte> block = [100, 200, 0x88, 0xC6, 0xFA, 0x88, 0xC6, 0xFA];
        Span<byte> values = stackalloc byte[16];

        Bc4Block.Decode(block, values);

        Assert.Equal(
            [100, 200, 120, 140, 160, 180, 0, 255, 100, 200, 120, 140, 160, 180, 0, 255],
            values.ToArray()
        );
    }

    /// <summary>
    ///     Two equal endpoints are not "greater than", so the block is in the six-value mode — and
    ///     every interpolated entry lands on the same value anyway, which is why a flat block is
    ///     exact whichever mode an encoder picks.
    /// </summary>
    [Fact]
    public void ABc4BlockWithEqualEndpointsIsFlatInEitherMode() {
        Span<byte> palette = stackalloc byte[8];

        Bc4Block.Palette(77, 77, palette);

        Assert.Equal([77, 77, 77, 77, 77, 77, 0, 255], palette.ToArray());
    }

    /// <summary>
    ///     BC1's four-colour mode, selected by comparing the two endpoints as sixteen-bit numbers.
    ///     0xF800 is pure red and 0x001F pure blue; the two between them are thirds.
    /// </summary>
    [Fact]
    public void ABc1BlockWithTheLargerEndpointFirstHasFourOpaqueColours() {
        ReadOnlySpan<byte> block = [0x00, 0xF8, 0x1F, 0x00, 0xE4, 0xE4, 0xE4, 0xE4];
        Span<byte> rgba = stackalloc byte[64];

        Bc1Block.Decode(block, opaque: false, rgba);

        // Indices run 0,1,2,3 across every row: the two endpoints then (2c0+c1)/3 and (c0+2c1)/3.
        Assert.Equal([255, 0, 0, 255], rgba[..4].ToArray());
        Assert.Equal([0, 0, 255, 255], rgba[4..8].ToArray());
        Assert.Equal([170, 0, 85, 255], rgba[8..12].ToArray());
        Assert.Equal([85, 0, 170, 255], rgba[12..16].ToArray());
    }

    /// <summary>
    ///     And the three-colour mode, which is the whole of BC1's alpha channel: the same two
    ///     endpoints the other way round, one colour halfway between them, and a fourth index that
    ///     means transparent black.
    /// </summary>
    [Fact]
    public void ABc1BlockWithTheSmallerEndpointFirstHasThreeColoursAndATransparentIndex() {
        ReadOnlySpan<byte> block = [0x1F, 0x00, 0x00, 0xF8, 0xE4, 0xE4, 0xE4, 0xE4];
        Span<byte> rgba = stackalloc byte[64];

        Bc1Block.Decode(block, opaque: false, rgba);

        Assert.Equal([0, 0, 255, 255], rgba[..4].ToArray());
        Assert.Equal([255, 0, 0, 255], rgba[4..8].ToArray());

        // ⚠ 128 and not 127. Halfway between five bits of nothing and five bits of everything is
        // 15.5 of 31, which is 127.5 of 255 — an exact tie, and the old code reached 127 by
        // expanding both endpoints to bytes first and then truncating. Rounding it up is what the
        // reference decoder does and what the specification's real-valued division means.
        Assert.Equal([128, 0, 128, 255], rgba[8..12].ToArray());
        Assert.Equal([0, 0, 0, 0], rgba[12..16].ToArray());
    }

    /// <summary>
    ///     Inside BC3 the comparison is not read: the colour block is always four opaque colours,
    ///     because BC3's alpha lives in its other eight bytes. The same block that decoded to a
    ///     transparent texel above decodes to an interpolated colour here, and an encoder that
    ///     forgot this would put holes in every BC3 texture.
    /// </summary>
    [Fact]
    public void TheSameBlockInsideBc3IsFourOpaqueColoursRegardless() {
        ReadOnlySpan<byte> block = [0x1F, 0x00, 0x00, 0xF8, 0xE4, 0xE4, 0xE4, 0xE4];
        Span<byte> rgba = stackalloc byte[64];

        Bc1Block.Decode(block, opaque: true, rgba);

        Assert.Equal([85, 0, 170, 255], rgba[8..12].ToArray());
        Assert.Equal([170, 0, 85, 255], rgba[12..16].ToArray());
    }

    /// <summary>
    ///     Five bits of red must come back as 255, not 248. The low bits are filled by replicating
    ///     the high ones, and a shift instead would darken every white texel in the engine by three
    ///     levels and every fully-lit specular highlight with it.
    /// </summary>
    [Fact]
    public void FiveBitsOfWhiteUnpackToWhiteAndNotTo248() {
        Span<byte> rgba = stackalloc byte[4];

        Bc1Block.Unpack565(0xFFFF, rgba);

        Assert.Equal([255, 255, 255, 255], rgba.ToArray());
    }

    [Fact]
    public void TheLowestNonZeroLevelOfEachChannelReplicatesToo() {
        Span<byte> rgba = stackalloc byte[4];

        Bc1Block.Unpack565(0x0821, rgba);

        // One of thirty-one is 8; one of sixty-three is 4.
        Assert.Equal([8, 4, 8, 255], rgba.ToArray());
    }

    /// <summary>
    ///     A BC7 mode 6 block, every field placed by hand. The seven mode bits are a unary count —
    ///     six zeros then a one — followed by the eight seven-bit endpoint halves in the order
    ///     R0 R1 G0 G1 B0 B1 A0 A1, the two shared parity bits, and then the indices: three bits for
    ///     texel zero, whose top bit the format does not store, and four for every other.
    /// </summary>
    [Fact]
    public void ABc7Mode6BlockIsLaidOutFieldByFieldAsTheSpecificationSays() {
        // R0=64 R1=0 G0=0 G1=64 B0=0 B1=0 A0=127 A1=127, P0=1 P1=0,
        // indices 0, 15, 1, then fourteen zeros.
        ReadOnlySpan<byte> block = [
            0x40, 0x20, 0x00, 0x00, 0x04, 0x00, 0xFE, 0xFF,
            0xF0, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];

        Span<byte> rgba = stackalloc byte[64];

        Bc7Block.Decode(block, rgba);

        // The parity bit is the endpoint's low bit: (64<<1)|1 = 129 and (0<<1)|0 = 0.
        Assert.Equal([129, 1, 1, 255], rgba[..4].ToArray());
        Assert.Equal([0, 128, 0, 254], rgba[4..8].ToArray());

        // Index one is four sixty-fourths along: (129·60 + 0·4 + 32) >> 6 = 121.
        Assert.Equal([121, 9, 1, 255], rgba[8..12].ToArray());
        Assert.Equal([129, 1, 1, 255], rgba[12..16].ToArray());
    }

    /// <summary>
    ///     A first byte of zero names no mode. Nothing writes one, and the only thing a decoder can
    ///     usefully do is say nothing: zero in every channel, rather than throwing on a preview.
    /// </summary>
    [Fact]
    public void ABc7BlockThatNamesNoModeDecodesToNothing() {
        var block = new byte[16];
        block[1] = 0xFF;

        var rgba = new byte[64];
        rgba.AsSpan().Fill(0xAA);

        Bc7Block.Decode(block, rgba);

        Assert.All(rgba, value => Assert.Equal(0, value));
    }

    /// <summary>
    ///     Mode 5 laid out by hand, because it is where the two index sets and the rotation meet:
    ///     seven-bit colour, eight-bit alpha with no parity, two two-bit index sets. Rotation 1 swaps
    ///     red and alpha after interpolation, so the alpha line turns up in red.
    /// </summary>
    [Fact]
    public void ABc7Mode5BlockRotatesItsAlphaLineIntoRed() {
        var writer = new byte[16];
        var bits = new BlockBitWriter(writer);

        bits.Write(1u << 5, 6);     // mode 5
        bits.Write(1, 2);           // rotation: swap red and alpha
        bits.Write(0, 7);           // R0
        bits.Write(127, 7);         // R1
        bits.Write(127, 7);         // G0
        bits.Write(127, 7);         // G1
        bits.Write(0, 7);           // B0
        bits.Write(0, 7);           // B1
        bits.Write(10, 8);          // A0
        bits.Write(250, 8);         // A1

        for (var texel = 0; texel < 16; texel++) {
            bits.Write(0, texel == 0 ? 1 : 2);
        }

        for (var texel = 0; texel < 16; texel++) {
            bits.Write(texel == 1 ? 3u : 0u, texel == 0 ? 1 : 2);
        }

        var rgba = new byte[64];

        Bc7Block.Decode(writer, rgba);

        // Colour index 0 everywhere: red's endpoint is 0, and it lands in alpha.
        Assert.Equal([10, 255, 0, 0], rgba[..4]);
        Assert.Equal([250, 255, 0, 0], rgba[4..8]);
    }

    /// <summary>
    ///     A BC6H mode 11 block, laid out by hand: five mode bits holding 0b00011, then rw gw bw and
    ///     rx gx bx at ten bits each, then the same index layout BC7 uses. The values are half-float
    ///     bit patterns, which is what BC6H produces — 15887 is 0x3E0F, or about 1.515.
    /// </summary>
    [Fact]
    public void ABc6HMode11BlockIsLaidOutFieldByFieldAsTheSpecificationSays() {
        // rw=512, gw=bw=0, rx=0, gx=512, bx=0; indices 0, 15, then fourteen zeros.
        ReadOnlySpan<byte> block = [
            0x03, 0x40, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00,
            0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ];

        Span<ushort> rgb = stackalloc ushort[48];

        Bc6HBlock.Decode(block, rgb);

        Assert.Equal([15887, 0, 0], rgb[..3].ToArray());
        Assert.Equal([0, 15887, 0], rgb[3..6].ToArray());
        Assert.Equal([15887, 0, 0], rgb[6..9].ToArray());
    }

    /// <summary>
    ///     The two steps that turn a ten-bit endpoint into a half: widen to sixteen bits, then scale
    ///     by thirty-one sixty-fourths. Both ends are special-cased in the specification, and the
    ///     scale is the reason the top of the range lands where it does.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 96, 46)]
    [InlineData(512, 32800, 15887)]
    [InlineData(1022, 65440, 31697)]
    [InlineData(1023, 65535, 31743)]
    public void Bc6HWidensAndScalesItsEndpointsInTwoStatedSteps(int endpoint, int widened, int half) {
        Assert.Equal(widened, Bc6HBlock.Unquantise(endpoint, 10, signed: false));
        Assert.Equal(half, Bc6HBlock.Finish(widened, signed: false));
    }

    /// <summary>
    ///     Unsigned BC6H cannot express infinity, and it is the format that arranges this rather than
    ///     the encoder avoiding a value: the largest endpoint at every mode's precision finishes at
    ///     0x7BFF, the largest finite half, and interpolating between two of those cannot exceed it.
    /// </summary>
    [Fact]
    public void NoUnsignedEndpointOfAnyPrecisionCanReachInfinity() {
        foreach (var mode in Bc6HBlock.Layouts) {
            var largest = (1 << mode.EndpointBits) - 1;

            Assert.Equal(0x7BFF, Bc6HBlock.Finish(Bc6HBlock.Unquantise(largest, mode.EndpointBits, false), false));
        }
    }

    /// <summary>
    ///     ⚠ The signed format's one hole: mode 14's lowest code has no special case, so it finishes
    ///     one past the largest finite half and the block decodes to negative infinity. The GPU
    ///     agrees, in <see cref="GpuBcnDecoderTests" />. What matters is that the encoder never takes
    ///     that code, whatever it is asked for.
    /// </summary>
    [Fact]
    public void TheOneSignedCodeThatReachesInfinityIsNeverChosen() {
        Assert.Equal(-0x7C00, Bc6HBlock.Finish(Bc6HBlock.Unquantise(-32768, 16, true), true));

        foreach (var mode in Bc6HBlock.Layouts) {
            var chosen = Bc6HBlock.Quantise(float.NegativeInfinity, mode.EndpointBits, true);

            Assert.True(Bc6HBlock.Finish(Bc6HBlock.Unquantise(chosen, mode.EndpointBits, true), true) >= -0x7BFF);
        }

        var rgb = new ushort[48];
        rgb.AsSpan().Fill(0x8000 | 0x7BFF);

        var block = new byte[16];
        var decoded = new ushort[48];

        foreach (var quality in Enum.GetValues<BlockQuality>()) {
            Bc6HBlock.Encode(rgb, block, quality, signed: true);
            Bc6HBlock.Decode(block, decoded, signed: true);

            Assert.All(decoded, value => Assert.True((value & 0x7FFF) <= 0x7BFF, $"{value:X4} is not finite"));
        }
    }

    /// <summary>
    ///     The four reserved mode values decode to black, which is what the specification says and
    ///     what the GPU does: <see cref="GpuBcnDecoderTests" /> compares them.
    /// </summary>
    [Fact]
    public void ABc6HBlockInAReservedModeIsBlack() {
        var block = new byte[16];
        block.AsSpan().Fill(0xFF);
        block[0] = 0b10011;

        var rgb = new ushort[48];
        rgb.AsSpan().Fill(0xAAAA);

        Bc6HBlock.Decode(block, rgb);

        Assert.All(rgb, value => Assert.Equal(0, value));
    }

    /// <summary>
    ///     Every header is the size the format says and names each field bit once. The GPU proves
    ///     these layouts are the right ones; this is what makes a typo in one of them fail with a
    ///     sentence rather than with a block of wrong texels.
    /// </summary>
    [Fact]
    public void EveryBc6HHeaderCoversItsFieldsExactlyOnce() {
        foreach (var mode in Bc6HBlock.Layouts) {
            Assert.Equal(mode.Subsets == 2 ? 82 : 65, mode.ModeBitCount + mode.Bits.Length);
            Assert.Equal(mode.Bits.Length, mode.Bits.Distinct().Count());

            for (var channel = 0; channel < 3; channel++) {
                Assert.Equal(mode.EndpointBits, mode.Bits.Count(bit => bit.Field == channel));

                for (var endpoint = 1; endpoint < mode.Subsets * 2; endpoint++) {
                    var stored = mode.Transformed ? mode.DeltaBits(channel) : mode.EndpointBits;

                    Assert.Equal(stored, mode.Bits.Count(bit => bit.Field == (endpoint * 3) + channel));
                }
            }

            Assert.Equal(mode.Subsets == 2 ? 5 : 0, mode.Bits.Count(bit => bit.Field == Bc6HBlock.PartitionField));
        }
    }

    /// <summary>The BPTC tables are shaped the way the format needs: the checks a reader can do.</summary>
    [Fact]
    public void EveryPartitionPatternHasAnAnchorInEachOfItsSubsets() {
        for (var partition = 0; partition < 64; partition++) {
            var two = BptcTables.TwoSubsets.Slice(partition * 16, 16);
            var three = BptcTables.ThreeSubsets.Slice(partition * 16, 16);

            Assert.Equal(0, two[0]);
            Assert.Equal(0, three[0]);
            Assert.Equal(1, two[BptcTables.TwoSubsetAnchors[partition]]);
            Assert.Equal(1, three[BptcTables.ThreeSubsetAnchors1[partition]]);
            Assert.Equal(2, three[BptcTables.ThreeSubsetAnchors2[partition]]);
        }
    }

    /// <summary>
    ///     The weight tables are symmetric about their middles, which is what makes swapping the two
    ///     endpoints and inverting every index describe the identical palette — and that is the only
    ///     reason an encoder is free to satisfy the anchor rule after the fact.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheWeightTablesAreSymmetric(int bits) {
        var weights = BptcTables.WeightsOf(bits);

        for (var index = 0; index < weights.Length; index++) {
            Assert.Equal(64 - weights[index], weights[weights.Length - 1 - index]);
        }
    }
}
