// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using CsCheck;
using Vixen.Core.Imaging.BlockCompression;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Core.Imaging.Tests;

/// <summary>
///     What the encoders promise. Exactness where the format can be exact, a stated bound where it
///     cannot, and the mode decisions that are invisible in the output until the one block that
///     needed the other mode.
/// </summary>
public sealed class BlockEncoderTests {
    /// <summary>Every block of one value is exact in either BC4 mode, so this is the floor.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(254)]
    [InlineData(255)]
    public void ABc4BlockOfOneValueIsExact(byte value) {
        Span<byte> values = stackalloc byte[16];
        values.Fill(value);

        Assert.Equal(values.ToArray(), RoundTripBc4(values));
    }

    /// <summary>
    ///     The eight values of the eight-value mode are the endpoints and six sevenths between them,
    ///     so a block made of exactly those is lossless — which is the strongest statement the
    ///     format allows and a direct check that the encoder finds the right two endpoints.
    /// </summary>
    [Fact]
    public void ABc4BlockOnTheSeventhsIsLossless() {
        Span<byte> values = [
            0, 36, 72, 109, 145, 182, 218, 255,
            255, 218, 182, 145, 109, 72, 36, 0
        ];

        Assert.Equal(values.ToArray(), RoundTripBc4(values));
    }

    /// <summary>
    ///     The point of the six-value mode. A block that is fourteen texels of nearly one value plus
    ///     one black and one white texel is hopeless in the eight-value mode — the endpoints are
    ///     dragged to 0 and 255 and the steps between them are 36 apart — and exact in the other,
    ///     which gets 0 and 255 for free and spends both endpoints on the cluster.
    /// </summary>
    [Fact]
    public void ABc4BlockWithBothExtremesAndATightClusterPicksTheSixValueMode() {
        Span<byte> values = [
            0, 255, 100, 101, 102, 103, 104, 100,
            101, 102, 103, 104, 100, 101, 102, 103
        ];

        Span<byte> block = stackalloc byte[8];
        Bc4Block.Encode(values, block);

        Assert.True(block[0] <= block[1], "the six-value mode is selected by ordering the endpoints the other way");
        Assert.Equal(values.ToArray(), RoundTripBc4(values));
    }

    /// <summary>
    ///     A 565-representable colour survives BC1 untouched. Anything less and every flat region of
    ///     every texture would shift, which is the artefact people notice first.
    /// </summary>
    [Fact]
    public void ABc1BlockOfOneRepresentableColourIsExact() {
        // Representable means what five and six bits can say: 8 is one of thirty-one steps of red,
        // 134 is thirty-three of sixty-three of green, 206 is twenty-five of thirty-one of blue.
        var rgba = Flat(8, 134, 206, 255);

        Assert.Equal(rgba, RoundTrip(PixelFormat.Bc1RgbaUNorm, rgba));
    }

    /// <summary>
    ///     Two representable colours are exact as well: they become the two endpoints, and every
    ///     texel takes one of them. A fit that landed the endpoints anywhere else would show here.
    /// </summary>
    [Fact]
    public void ABc1BlockOfTwoRepresentableColoursIsExact() {
        var rgba = new byte[64];

        for (var texel = 0; texel < 16; texel++) {
            var white = texel % 3 == 0;
            rgba[texel * 4] = white ? (byte)255 : (byte)0;
            rgba[(texel * 4) + 1] = white ? (byte)255 : (byte)0;
            rgba[(texel * 4) + 2] = white ? (byte)255 : (byte)0;
            rgba[(texel * 4) + 3] = 255;
        }

        Assert.Equal(rgba, RoundTrip(PixelFormat.Bc1RgbaUNorm, rgba));
    }

    /// <summary>
    ///     One texel below the cutoff puts the whole block in the three-colour mode, because that is
    ///     the only place BC1 has to say "transparent". The cost is real — three colours instead of
    ///     four for the fifteen opaque texels — and it is the format's, not a choice made here.
    /// </summary>
    [Fact]
    public void OneTexelBelowTheAlphaCutoffMakesTheWholeBc1BlockCutOut() {
        var rgba = Flat(200, 100, 50, 255);
        rgba[(5 * 4) + 3] = 0;

        var decoded = RoundTrip(PixelFormat.Bc1RgbaUNorm, rgba);

        Assert.Equal(0, decoded[(5 * 4) + 3]);
        Assert.Equal(255, decoded[3]);

        for (var texel = 0; texel < 16; texel++) {
            if (texel != 5) {
                Assert.Equal(255, decoded[(texel * 4) + 3]);
            }
        }
    }

    /// <summary>
    ///     A wholly transparent block has no colours to fit a line through, and asking for the
    ///     principal axis of nothing is a division by zero. It takes the shortest path: two equal
    ///     endpoints and every index on the transparent one.
    /// </summary>
    [Fact]
    public void AWhollyTransparentBc1BlockIsAllTransparent() {
        var rgba = Flat(37, 210, 9, 0);

        var decoded = RoundTrip(PixelFormat.Bc1RgbaUNorm, rgba);

        for (var texel = 0; texel < 16; texel++) {
            Assert.Equal(0, decoded[(texel * 4) + 3]);
        }
    }

    /// <summary>
    ///     BC3 keeps colour and alpha apart: the alpha block is a full BC4 with eight levels, and a
    ///     zero there does not reach into the colour block. Encoding a colour gradient behind fully
    ///     transparent alpha and getting the gradient back is the check that the two halves were
    ///     written in the right order and read in the same one.
    /// </summary>
    [Fact]
    public void Bc3KeepsItsColourWhereAlphaIsZero() {
        var rgba = new byte[64];

        // A gradient the width of one 4×4 tile of a real texture rather than the whole range: BC3's
        // colour half has four steps to spend, and asking it for sixteen distinct colours would be
        // measuring the format's resolution instead of whether the two halves interfere.
        for (var texel = 0; texel < 16; texel++) {
            rgba[texel * 4] = (byte)(texel * 4);
            rgba[(texel * 4) + 1] = 0;
            rgba[(texel * 4) + 2] = (byte)(200 - (texel * 4));
            rgba[(texel * 4) + 3] = 0;
        }

        var decoded = RoundTrip(PixelFormat.Bc3RgbaUNorm, rgba);

        for (var texel = 0; texel < 16; texel++) {
            Assert.Equal(0, decoded[(texel * 4) + 3]);
        }

        Assert.True(MaxError(rgba, decoded, channels: 3) <= 16, "the colour gradient should survive");
    }

    /// <summary>
    ///     And BC3's alpha has eight levels rather than BC1's one bit, so a ramp comes back as a
    ///     ramp. Sixteen values across the full range is more than eight levels can hold exactly;
    ///     what matters is that it is monotonic and close.
    /// </summary>
    [Fact]
    public void Bc3AlphaIsAGradientAndNotACutout() {
        var rgba = new byte[64];

        for (var texel = 0; texel < 16; texel++) {
            rgba[(texel * 4) + 3] = (byte)(texel * 17);
        }

        var decoded = RoundTrip(PixelFormat.Bc3RgbaUNorm, rgba);
        var previous = -1;

        for (var texel = 0; texel < 16; texel++) {
            var alpha = decoded[(texel * 4) + 3];
            Assert.True(Math.Abs(alpha - (texel * 17)) <= 20, $"texel {texel}: {alpha} for {texel * 17}");
            Assert.True(alpha >= previous, "a ramp should stay a ramp");
            previous = alpha;
        }
    }

    /// <summary>
    ///     BC3's colour half must never be written in the ordering that would select three colours,
    ///     because BC3 readers do not look at that ordering — they always assume four. A BC3 encoder
    ///     that passed its alpha down to the colour fit would produce blocks whose fourth index is
    ///     written meaning "transparent" and read meaning "two thirds of the way to the second
    ///     endpoint", and the block with every texel transparent is where it shows.
    /// </summary>
    [Fact]
    public void ABc3ColourBlockIsNeverOrderedForTheThreeColourMode() {
        var rgba = new byte[64];

        for (var texel = 0; texel < 16; texel++) {
            rgba[texel * 4] = (byte)(texel * 4);
            rgba[(texel * 4) + 2] = (byte)(200 - (texel * 4));
            rgba[(texel * 4) + 3] = 0;
        }

        Span<byte> block = stackalloc byte[16];
        BlockCompressor.EncodeBlock(PixelFormat.Bc3RgbaUNorm, rgba, block);

        var colour0 = BinaryPrimitives.ReadUInt16LittleEndian(block[8..]);
        var colour1 = BinaryPrimitives.ReadUInt16LittleEndian(block[10..]);

        Assert.True(colour0 > colour1, $"BC3's colour endpoints were ordered {colour0} then {colour1}");
    }

    /// <summary>
    ///     And the reading half of the same rule, for a block this did not write: another encoder is
    ///     free to order a BC3 colour block either way, and both mean four opaque colours. Reading
    ///     one as three would put a transparent hole wherever the fourth index was used.
    /// </summary>
    [Fact]
    public void ABc3BlockFromElsewhereIsReadAsFourColoursWhicheverWayItIsOrdered() {
        // Alpha block of a flat 255, then a colour block whose endpoints are the "wrong" way round.
        ReadOnlySpan<byte> block = [
            255, 255, 0, 0, 0, 0, 0, 0,
            0x1F, 0x00, 0x00, 0xF8, 0xFF, 0xFF, 0xFF, 0xFF
        ];

        Span<byte> rgba = stackalloc byte[64];

        BlockCompressor.DecodeBlock(PixelFormat.Bc3RgbaUNorm, block, rgba);

        // Every index is three. Read as four colours that is (c0 + 2·c1)/3; read as three it would
        // be transparent black.
        Assert.Equal([170, 0, 85, 255], rgba[..4].ToArray());
    }

    /// <summary>
    ///     BC5's two halves are independent BC4 blocks, and a normal map is the case that finds it
    ///     when they are not: red varying while green is flat has to come back that way round.
    /// </summary>
    [Fact]
    public void Bc5KeepsItsTwoChannelsApart() {
        var rgba = new byte[64];

        for (var texel = 0; texel < 16; texel++) {
            rgba[texel * 4] = (byte)(texel * 17);
            rgba[(texel * 4) + 1] = 128;
            rgba[(texel * 4) + 2] = 99;      // Not stored: BC5 has two channels.
            rgba[(texel * 4) + 3] = 7;
        }

        var decoded = RoundTrip(PixelFormat.Bc5RgUNorm, rgba);

        for (var texel = 0; texel < 16; texel++) {
            Assert.True(Math.Abs(decoded[texel * 4] - (texel * 17)) <= 20);
            Assert.Equal(128, decoded[(texel * 4) + 1]);
            Assert.Equal(0, decoded[(texel * 4) + 2]);
            Assert.Equal(255, decoded[(texel * 4) + 3]);
        }
    }

    /// <summary>
    ///     BC7 mode 6 spends seven bits per channel plus one parity bit shared by all four, so a
    ///     flat block is exact exactly when its four channels agree about their low bit — and off by
    ///     one when they do not. That is worth stating as a test rather than discovering in an
    ///     artefact report.
    /// </summary>
    [Fact]
    public void ABc7BlockOfOneColourIsExactWhenItsChannelsShareTheirLowBit() {
        var even = Flat(200, 100, 50, 254);

        Assert.Equal(even, RoundTrip(PixelFormat.Bc7RgbaUNorm, even));

        var mixed = Flat(200, 101, 50, 255);

        Assert.True(MaxError(mixed, RoundTrip(PixelFormat.Bc7RgbaUNorm, mixed), channels: 4) <= 1);
    }

    /// <summary>
    ///     The anchor rule. BC7 does not store the top bit of texel zero's index, so the encoder has
    ///     to order the endpoints such that it is zero — and a block whose first texel is its
    ///     brightest is the one that makes the natural fit produce the other order. Without the swap
    ///     the index is written as four bits into a three-bit field and the whole rest of the block
    ///     shifts.
    /// </summary>
    [Fact]
    public void ABc7BlockWhoseFirstTexelIsTheBrightestStillDecodesCorrectly() {
        var rgba = Flat(0, 0, 0, 255);
        rgba[0] = 255;
        rgba[1] = 255;
        rgba[2] = 255;

        var decoded = RoundTrip(PixelFormat.Bc7RgbaUNorm, rgba);

        // Within one: black-with-opaque-alpha and white-with-opaque-alpha disagree about the low bit
        // the two endpoints share, so one of the four channels gives it up. Without the swap the
        // whole index stream would shift and neither texel would be close to either colour.
        for (var channel = 0; channel < 4; channel++) {
            Assert.True(255 - decoded[channel] <= 1, $"texel zero, channel {channel}: {decoded[channel]}");
            Assert.True(decoded[4 + channel] <= 1 || channel == 3, $"texel one, channel {channel}");
        }

        Assert.True(255 - decoded[7] <= 1, "texel one's alpha");
    }

    /// <summary>
    ///     Mode 6 fits alpha on the same line as colour, which makes constant colour with varying
    ///     alpha its best case and BC1's worst.
    /// </summary>
    [Fact]
    public void Bc7CarriesAlphaOnTheSameLineAsColour() {
        var rgba = new byte[64];

        for (var texel = 0; texel < 16; texel++) {
            rgba[texel * 4] = 60;
            rgba[(texel * 4) + 1] = 60;
            rgba[(texel * 4) + 2] = 60;
            rgba[(texel * 4) + 3] = (byte)(texel * 17);
        }

        Assert.True(MaxError(rgba, RoundTrip(PixelFormat.Bc7RgbaUNorm, rgba), channels: 4) <= 4);
    }

    /// <summary>
    ///     BC6H's error is proportional rather than absolute, because it is fitted in half-float bit
    ///     space: a per cent of the value at radiance one half and a per cent of it at radiance ten
    ///     thousand. Both ends are asserted, because an encoder that fitted in linear light would
    ///     pass the second and be hopeless at the first.
    /// </summary>
    [Theory]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(64f)]
    [InlineData(10000f)]
    public void ABc6HBlockOfOneRadianceIsWithinAPerCentOfIt(float radiance) {
        var rgb = new ushort[48];

        for (var value = 0; value < rgb.Length; value++) {
            rgb[value] = BitConverter.HalfToUInt16Bits((Half)radiance);
        }

        var block = new byte[16];
        var decoded = new ushort[48];
        Bc6HBlock.Encode(rgb, block);
        Bc6HBlock.Decode(block, decoded);

        foreach (var bits in decoded) {
            var value = (float)BitConverter.UInt16BitsToHalf(bits);
            // The endpoint grid is about thirty-one units of half's bit pattern apart, which is
            // three per cent of a binade; half of that is what a flat block can be off by.
            Assert.True(
                Math.Abs(value - radiance) / radiance < 0.01f,
                $"{value} is more than one per cent away from {radiance}"
            );
        }
    }

    /// <summary>
    ///     Unsigned BC6H has no way to say "negative" or "not a number". Both are clamped where they
    ///     arrive, because a half's bit pattern is what the fit runs on and a negative one read as
    ///     unsigned is an enormous positive number that would drag the whole block's endpoints with
    ///     it.
    /// </summary>
    [Fact]
    public void NegativeAndNotANumberAreClampedBeforeTheyReachTheFit() {
        var texture = new TextureData(PixelFormat.Rgba16Float, 4, 4, levelCount: 1);
        var pixels = texture.PixelSpan();

        for (var texel = 0; texel < 16; texel++) {
            Write(pixels[(texel * 8)..], texel == 0 ? Half.NegativeOne : (Half)2f);
            Write(pixels[((texel * 8) + 2)..], texel == 1 ? Half.NaN : (Half)2f);
            Write(pixels[((texel * 8) + 4)..], texel == 2 ? Half.PositiveInfinity : (Half)2f);
        }

        var decoded = BlockCompressor.Decode(BlockCompressor.Encode(texture, PixelFormat.Bc6HRgbUFloat));
        var back = decoded.Pixels;

        for (var texel = 0; texel < 16; texel++) {
            for (var channel = 0; channel < 3; channel++) {
                var value = (float)Read(back[((texel * 8) + (channel * 2))..]);
                Assert.True(value >= 0f && float.IsFinite(value), $"texel {texel} channel {channel} is {value}");
            }
        }

        static void Write(Span<byte> destination, Half value) =>
            BitConverter.TryWriteBytes(destination, BitConverter.HalfToUInt16Bits(value));

        static Half Read(ReadOnlySpan<byte> source) =>
            BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(source));
    }

    /// <summary>
    ///     <para>
    ///         The property every one of these formats actually promises: sixteen texels lying on a
    ///         line through colour space come back close, because a line with a fixed number of steps
    ///         along it is the entirety of what a BC block stores. Arbitrary colours do not have this
    ///         property and cannot be given it by a better encoder — the error on a random block is
    ///         dominated by how far the texels sit <i>off</i> any line, which no choice of endpoints
    ///         changes.
    ///     </para>
    ///     <para>
    ///         The bounds were measured over twenty thousand random lines each and then given room:
    ///         BC1 and BC3 reached 40, BC4 and BC5 21 and 23, BC7 9. That ordering is the formats'
    ///         own — four steps along the line for BC1, eight for BC4, sixteen for BC7 — and a
    ///         change that broke one encoder's fit would land it in another's band.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bc1RgbaUNorm, 3, 64)]
    [InlineData(PixelFormat.Bc3RgbaUNorm, 4, 64)]
    [InlineData(PixelFormat.Bc4RUNorm, 1, 32)]
    [InlineData(PixelFormat.Bc5RgUNorm, 2, 32)]
    [InlineData(PixelFormat.Bc7RgbaUNorm, 3, 20)]
    public void TexelsOnALineComeBackCloseToTheLine(PixelFormat format, int channels, int bound) =>
        Gen.Select(Gen.Byte.Array[4], Gen.Byte.Array[4], Gen.Byte[0, 255].Array[16])
            .Sample(sample => {
                    var (from, to, positions) = sample;
                    var rgba = new byte[64];

                    for (var texel = 0; texel < 16; texel++) {
                        for (var channel = 0; channel < 3; channel++) {
                            rgba[(texel * 4) + channel] =
                                (byte)(from[channel] + ((to[channel] - from[channel]) * positions[texel] / 255));
                        }

                        rgba[(texel * 4) + 3] = 255;
                    }

                    var error = MaxError(rgba, RoundTrip(format, rgba), channels);
                    Assert.True(error <= bound, $"{format} was off by {error}, and the bound is {bound}");
                },
                iter: 2_000
            );

    /// <summary>
    ///     And the guard for everything else. A random block has no line through it, so there is no
    ///     useful bound on the error — but a fit that has gone wrong shows up as being no better than
    ///     giving up and storing the block's mean colour, which is a statement that holds whatever
    ///     the input is. Measured across twenty thousand blocks the worst ratio was 0.76 for BC1 and
    ///     BC3, 0.75 for BC7 and under 0.05 for BC4 and BC5.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bc1RgbaUNorm, 3)]
    [InlineData(PixelFormat.Bc3RgbaUNorm, 3)]
    [InlineData(PixelFormat.Bc4RUNorm, 1)]
    [InlineData(PixelFormat.Bc5RgUNorm, 2)]
    [InlineData(PixelFormat.Bc7RgbaUNorm, 3)]
    public void AnyBlockIsFittedBetterThanItsOwnMeanColour(PixelFormat format, int channels) =>
        Gen.Byte.Array[64].Sample(rgba => {
                for (var texel = 0; texel < 16; texel++) {
                    rgba[(texel * 4) + 3] = 255;
                }

                var decoded = RoundTrip(format, rgba);
                var fitted = 0.0;
                var flat = 0.0;

                for (var channel = 0; channel < channels; channel++) {
                    var mean = 0.0;

                    for (var texel = 0; texel < 16; texel++) {
                        mean += rgba[(texel * 4) + channel];
                    }

                    mean /= 16;

                    for (var texel = 0; texel < 16; texel++) {
                        var fit = rgba[(texel * 4) + channel] - (double)decoded[(texel * 4) + channel];
                        var constant = rgba[(texel * 4) + channel] - mean;
                        fitted += fit * fit;
                        flat += constant * constant;
                    }
                }

                Assert.True(
                    flat <= 0 || fitted <= flat * 0.9,
                    $"{format} fitted no better than the block's mean colour: {fitted:F0} against {flat:F0}"
                );
            },
            iter: 2_000
        );

    /// <summary>
    ///     The case real content is made of. A gradient is a line, so BC7's sixteen steps along it
    ///     should be all but exact where BC1's four cannot be — and the gap between the two bounds
    ///     here is the whole reason a build spends sixteen bytes a block instead of eight.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bc1RgbaUNorm, 24)]
    [InlineData(PixelFormat.Bc3RgbaUNorm, 24)]
    [InlineData(PixelFormat.Bc7RgbaUNorm, 4)]
    public void ASmoothGradientIsNearlyExact(PixelFormat format, int bound) {
        var rgba = new byte[64];

        for (var texel = 0; texel < 16; texel++) {
            rgba[texel * 4] = (byte)(20 + (texel * 12));
            rgba[(texel * 4) + 1] = (byte)(40 + (texel * 6));
            rgba[(texel * 4) + 2] = (byte)(200 - (texel * 9));
            rgba[(texel * 4) + 3] = 255;
        }

        var error = MaxError(rgba, RoundTrip(format, rgba), channels: 3);
        Assert.True(error <= bound, $"{format} was off by {error}, and the bound is {bound}");
    }

    /// <summary>
    ///     <para>
    ///         The regression test for a defect these encoders shipped with for about an hour. The
    ///         power iteration that finds a block's principal axis used to start at (1, 1, 1); a block
    ///         whose colours run along (1, 0, −1) — red rising exactly as blue falls — is orthogonal
    ///         to that, so the first multiply returned zero, the iteration stopped, and the axis was
    ///         left pointing in the one direction the block has no extent in. Both endpoints landed
    ///         on the same texel and the whole block decoded as a single flat colour.
    ///     </para>
    ///     <para>
    ///         It is worth a test of its own rather than being left to the general bounds, because
    ///         the case is not exotic — red up and blue down is a sunset, a specular falloff and the
    ///         warm-to-cool ramp in half the hand-painted textures ever made — and because a
    ///         collapsed block does not look like a compression artefact. It looks like a flat
    ///         square.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Bc1RgbaUNorm)]
    [InlineData(PixelFormat.Bc3RgbaUNorm)]
    [InlineData(PixelFormat.Bc7RgbaUNorm)]
    public void ABlockRunningAlongTheAxisOrthogonalToGreyDoesNotCollapse(PixelFormat format) {
        var rgba = new byte[64];

        for (var texel = 0; texel < 16; texel++) {
            rgba[texel * 4] = (byte)(texel * 16);
            rgba[(texel * 4) + 1] = 128;
            rgba[(texel * 4) + 2] = (byte)(240 - (texel * 16));
            rgba[(texel * 4) + 3] = 255;
        }

        var decoded = RoundTrip(format, rgba);
        var reds = new HashSet<byte>();

        for (var texel = 0; texel < 16; texel++) {
            reds.Add(decoded[texel * 4]);
        }

        Assert.True(reds.Count >= 4, $"{format} collapsed the block to {reds.Count} colours");
        Assert.True(decoded[0] < decoded[60], "the ramp should still run the same way");
    }

    /// <summary>
    ///     <para>
    ///         And the same for BC6H, where a warm-to-cool ramp is most of what an environment map
    ///         is. The values here are written as half bit patterns rather than as radiances, because
    ///         BC6H fits in bit-pattern space and that is the space the axis has to be orthogonal to
    ///         grey in: a ramp that looks symmetric in linear light is not symmetric once half's
    ///         exponent is in the arithmetic, and a first attempt at this test that picked radiances
    ///         survived the defect it was written for.
    ///     </para>
    ///     <para>
    ///         Red rises by 200 a texel and blue falls by 200, so every texel's three channels sum to
    ///         the same number and the block is exactly orthogonal to (1, 1, 1).
    ///     </para>
    /// </summary>
    [Fact]
    public void AnHdrBlockRunningAlongTheSameAxisDoesNotCollapseEither() {
        var rgb = new ushort[48];

        for (var texel = 0; texel < 16; texel++) {
            rgb[texel * 3] = (ushort)(14000 + (texel * 200));
            rgb[(texel * 3) + 1] = 15000;
            rgb[(texel * 3) + 2] = (ushort)(17000 - (texel * 200));

            Assert.Equal(46000, rgb[texel * 3] + rgb[(texel * 3) + 1] + rgb[(texel * 3) + 2]);
        }

        var block = new byte[16];
        var decoded = new ushort[48];
        Bc6HBlock.Encode(rgb, block);
        Bc6HBlock.Decode(block, decoded);

        var reds = new HashSet<ushort>();

        for (var texel = 0; texel < 16; texel++) {
            reds.Add(decoded[texel * 3]);
        }

        Assert.True(reds.Count >= 4, $"the block collapsed to {reds.Count} values");
        Assert.True(decoded[0] < decoded[45], "the ramp should still run the same way");
    }

    /// <summary>
    ///     The fix itself, tested where it lives: a covariance matrix whose data varies only along
    ///     (1, 0, −1) has to produce that direction, not the direction the iteration started from.
    /// </summary>
    [Fact]
    public void ThePrincipalAxisOfDataAlongOneDirectionIsThatDirection() {
        // The covariance of points along (1, 0, -1): outer product of the direction with itself.
        ReadOnlySpan<float> covariance = [
            100f, 0f, -100f,
            0f, 0f, 0f,
            -100f, 0f, 100f
        ];

        Span<float> axis = stackalloc float[3];
        PrincipalAxis.Find(covariance, 3, axis);

        Assert.Equal(0.7071f, Math.Abs(axis[0]), 3);
        Assert.Equal(0f, axis[1], 3);
        Assert.Equal(0.7071f, Math.Abs(axis[2]), 3);
        Assert.True(axis[0] * axis[2] < 0, "the two ends of the axis should have opposite signs");
    }

    [Fact]
    public void ThePrincipalAxisOfNothingIsNotNaN() {
        Span<float> axis = stackalloc float[3];

        PrincipalAxis.Find(stackalloc float[9], 3, axis);

        foreach (var component in axis) {
            Assert.False(float.IsNaN(component));
        }
    }

    static byte[] RoundTripBc4(ReadOnlySpan<byte> values) {
        Span<byte> block = stackalloc byte[8];
        Span<byte> decoded = stackalloc byte[16];

        Bc4Block.Encode(values, block);
        Bc4Block.Decode(block, decoded);

        return decoded.ToArray();
    }

    static byte[] RoundTrip(PixelFormat format, ReadOnlySpan<byte> rgba) {
        Span<byte> block = stackalloc byte[16];
        Span<byte> decoded = stackalloc byte[64];

        BlockCompressor.EncodeBlock(format, rgba, block[..format.BlockSize()]);
        BlockCompressor.DecodeBlock(format, block[..format.BlockSize()], decoded);

        return decoded.ToArray();
    }

    static byte[] Flat(byte red, byte green, byte blue, byte alpha) {
        var rgba = new byte[64];

        for (var texel = 0; texel < 16; texel++) {
            rgba[texel * 4] = red;
            rgba[(texel * 4) + 1] = green;
            rgba[(texel * 4) + 2] = blue;
            rgba[(texel * 4) + 3] = alpha;
        }

        return rgba;
    }

    static int MaxError(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, int channels) {
        var worst = 0;

        for (var texel = 0; texel < 16; texel++) {
            for (var channel = 0; channel < channels; channel++) {
                var index = (texel * 4) + channel;
                worst = Math.Max(worst, Math.Abs(expected[index] - actual[index]));
            }
        }

        return worst;
    }
}
