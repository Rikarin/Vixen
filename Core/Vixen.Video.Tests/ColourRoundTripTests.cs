// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Video.Tests;

/// <summary>
///     The conversion, against an independent forward transform rather than against itself.
/// </summary>
/// <remarks>
///     <para>
///         Every other colour test here asserts a property — black is black, the ranges differ, the
///         matrices disagree about green. None of them would catch the whole table being consistently
///         wrong, because they are all written against the same six numbers.
///     </para>
///     <para>
///         So this authors colours in RGB, encodes them with the ITU's forward equations written out
///         from the specification, decodes them with the engine's own inverse, and asks for the
///         original back. Two implementations that agree are worth more than one that is
///         self-consistent — and it is the same round trip <c>Samples/11-VideoPlayback</c> makes
///         visible, where the encoder is the generator and the decoder is the fragment shader.
///     </para>
/// </remarks>
public sealed class ColourRoundTripTests {
    /// <summary>The seven bars, in the order a test pattern puts them.</summary>
    public static TheoryData<byte, byte, byte> Bars => new() {
        { 255, 255, 255 },
        { 255, 255, 0 },
        { 0, 255, 255 },
        { 0, 255, 0 },
        { 255, 0, 255 },
        { 255, 0, 0 },
        { 0, 0, 255 },
        { 0, 0, 0 },
        { 128, 128, 128 },
        { 30, 90, 200 }
    };

    [Theory]
    [MemberData(nameof(Bars))]
    public void ABt709LimitedColourSurvivesTheRoundTrip(byte red, byte green, byte blue) =>
        AssertRoundTrip(red, green, blue, VideoColourMatrix.Bt709, VideoColourRange.Limited);

    [Theory]
    [MemberData(nameof(Bars))]
    public void ABt709FullRangeColourSurvivesTheRoundTrip(byte red, byte green, byte blue) =>
        AssertRoundTrip(red, green, blue, VideoColourMatrix.Bt709, VideoColourRange.Full);

    [Theory]
    [MemberData(nameof(Bars))]
    public void ABt601LimitedColourSurvivesTheRoundTrip(byte red, byte green, byte blue) =>
        AssertRoundTrip(red, green, blue, VideoColourMatrix.Bt601, VideoColourRange.Limited);

    [Fact]
    public void TheWrongMatrixIsVisiblyWrongRatherThanSubtly() {
        // Worth stating, because it is the argument for carrying the metadata at all. A BT.601 clip
        // decoded as BT.709 is not a rounding difference: on a saturated colour it is tens of levels,
        // which is a picture whose greens and magentas are perceptibly off.
        var (y, u, v) = Encode(30, 200, 90, VideoColourMatrix.Bt601, VideoColourRange.Limited);
        var right = Decode(y, u, v, VideoColourMatrix.Bt601, VideoColourRange.Limited);
        var wrong = Decode(y, u, v, VideoColourMatrix.Bt709, VideoColourRange.Limited);

        Assert.True(Math.Abs(right.Green - wrong.Green) > 10);
    }

    static void AssertRoundTrip(
        byte red,
        byte green,
        byte blue,
        VideoColourMatrix matrix,
        VideoColourRange range
    ) {
        var (y, u, v) = Encode(red, green, blue, matrix, range);
        var back = Decode(y, u, v, matrix, range);

        // Two levels, which is what quantising three eight-bit channels through two eight-bit chroma
        // channels costs. A wrong coefficient is tens of levels out, and a wrong range is more.
        Assert.InRange(back.Red - red, -2, 2);
        Assert.InRange(back.Green - green, -2, 2);
        Assert.InRange(back.Blue - blue, -2, 2);
    }

    /// <summary>The ITU's forward equations, written out rather than derived from the decoder's.</summary>
    static (byte Y, byte U, byte V) Encode(
        byte red,
        byte green,
        byte blue,
        VideoColourMatrix matrix,
        VideoColourRange range
    ) {
        var (kr, kb) = matrix == VideoColourMatrix.Bt601 ? (0.299f, 0.114f) : (0.2126f, 0.0722f);
        var r = red / 255f;
        var g = green / 255f;
        var b = blue / 255f;
        var luma = (kr * r) + ((1f - kr - kb) * g) + (kb * b);

        if (range == VideoColourRange.Full) {
            return (
                Clamp(luma * 255f),
                Clamp(128f + (255f * 0.5f * (b - luma) / (1f - kb))),
                Clamp(128f + (255f * 0.5f * (r - luma) / (1f - kr)))
            );
        }

        return (
            Clamp(16f + (219f * luma)),
            Clamp(128f + (224f * 0.5f * (b - luma) / (1f - kb))),
            Clamp(128f + (224f * 0.5f * (r - luma) / (1f - kr)))
        );
    }

    /// <summary>The engine's own inverse, through a real frame and the real converter.</summary>
    static (int Blue, int Green, int Red) Decode(
        byte y,
        byte u,
        byte v,
        VideoColourMatrix matrix,
        VideoColourRange range
    ) {
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(2, 2, VideoPixelLayout.Yuv420Planar, Range: range, Matrix: matrix));
        frame.Plane(0).Fill(y);
        frame.Plane(1).Fill(u);
        frame.Plane(2).Fill(v);

        var bgra = new byte[VideoColourConversion.BgraSize(frame.Format)];

        VideoColourConversion.ToBgra(frame, bgra);

        return (bgra[0], bgra[1], bgra[2]);
    }

    static byte Clamp(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);
}
