// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Video.Tests;

/// <summary>
///     The arithmetic that is wrong in every project once: the range, the matrix, and which chroma
///     plane is which.
/// </summary>
public sealed class VideoColourConversionTests {
    [Fact]
    public void LimitedRangeBlackAndWhiteLandOnZeroAndTwoFiftyFive() {
        Assert.Equal(0, Convert(16, 128, 128, VideoColourRange.Limited).R);
        Assert.Equal(255, Convert(235, 128, 128, VideoColourRange.Limited).R);
    }

    [Fact]
    public void FullRangeBlackAndWhiteAreTheSampleValuesThemselves() {
        Assert.Equal(0, Convert(0, 128, 128, VideoColourRange.Full).R);
        Assert.Equal(255, Convert(255, 128, 128, VideoColourRange.Full).R);
    }

    [Fact]
    public void AFullRangeClipDecodedAsLimitedIsWashedOutRatherThanBroken() {
        // Stated because it is the failure this metadata exists to prevent, and because "a bit grey"
        // is exactly the kind of wrong that ships.
        var correct = Convert(128, 128, 128, VideoColourRange.Full).R;
        var wrong = Convert(128, 128, 128, VideoColourRange.Limited).R;

        Assert.True(wrong > correct);
        Assert.InRange(wrong - correct, 1, 40);
    }

    [Fact]
    public void ChromaAtTheExtremesGivesRedAndBlue() {
        var red = Convert(81, 90, 240, VideoColourRange.Limited, VideoColourMatrix.Bt601);

        Assert.InRange(red.R, 230, 255);
        Assert.InRange(red.G, 0, 30);
        Assert.InRange(red.B, 0, 30);
    }

    [Fact]
    public void TheTwoMatricesDisagreeAboutGreen() {
        var bt601 = Convert(150, 60, 90, VideoColourRange.Limited, VideoColourMatrix.Bt601);
        var bt709 = Convert(150, 60, 90, VideoColourRange.Limited);

        Assert.NotEqual(bt601.G, bt709.G);
    }

    [Fact]
    public void GreyIsGrey() {
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(2, 2, VideoPixelLayout.Grey8, Range: VideoColourRange.Full));
        frame.Plane(0).Fill(200);

        var bgra = new byte[VideoColourConversion.BgraSize(frame.Format)];

        VideoColourConversion.ToBgra(frame, bgra);

        Assert.Equal(200, bgra[0]);
        Assert.Equal(200, bgra[1]);
        Assert.Equal(200, bgra[2]);
        Assert.Equal(255, bgra[3]);
    }

    [Fact]
    public void PackedBgraIsCopiedRatherThanConverted() {
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(1, 1, VideoPixelLayout.Bgra8));
        frame.Plane(0)[0] = 1;
        frame.Plane(0)[1] = 2;
        frame.Plane(0)[2] = 3;
        frame.Plane(0)[3] = 4;

        var bgra = new byte[4];

        VideoColourConversion.ToBgra(frame, bgra);

        Assert.Equal([1, 2, 3, 4], bgra);
    }

    [Fact]
    public void ChromaIsSharedByFourLumaSamplesInFourTwoZero() {
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(2, 2, VideoPixelLayout.Yuv420Planar));
        frame.Plane(0).Fill(128);
        frame.Plane(1).Fill(90);
        frame.Plane(2).Fill(240);

        var bgra = new byte[VideoColourConversion.BgraSize(frame.Format)];

        VideoColourConversion.ToBgra(frame, bgra);

        // One chroma sample, four pixels, all the same colour.
        for (var pixel = 1; pixel < 4; pixel++) {
            Assert.Equal(bgra[0], bgra[pixel * 4]);
            Assert.Equal(bgra[1], bgra[(pixel * 4) + 1]);
            Assert.Equal(bgra[2], bgra[(pixel * 4) + 2]);
        }
    }

    [Fact]
    public void ADestinationTooSmallSaysSoRatherThanWritingPastIt() {
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(4, 4, VideoPixelLayout.Grey8));

        Assert.Throws<ArgumentException>(() => VideoColourConversion.ToBgra(frame, new byte[8]));
    }

    static (byte B, byte G, byte R) Convert(
        byte luma,
        byte blue,
        byte red,
        VideoColourRange range,
        VideoColourMatrix matrix = VideoColourMatrix.Bt709
    ) {
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(2, 2, VideoPixelLayout.Yuv420Planar, Range: range, Matrix: matrix));
        frame.Plane(0).Fill(luma);
        frame.Plane(1).Fill(blue);
        frame.Plane(2).Fill(red);

        var bgra = new byte[VideoColourConversion.BgraSize(frame.Format)];

        VideoColourConversion.ToBgra(frame, bgra);

        return (bgra[0], bgra[1], bgra[2]);
    }
}
