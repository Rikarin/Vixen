// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Testing;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>Formats, frames and the pool that stops playback allocating.</summary>
public sealed class VideoFrameTests {
    [Fact]
    public void AFourTwoZeroFrameHasThreePlanesAtTheRightSizes() {
        var format = new VideoFormat(1920, 1080, VideoPixelLayout.Yuv420Planar);

        Assert.Equal(3, format.PlaneCount);
        Assert.Equal(1920, format.PlaneWidth(0));
        Assert.Equal(1080, format.PlaneHeight(0));
        Assert.Equal(960, format.PlaneWidth(1));
        Assert.Equal(540, format.PlaneHeight(1));
        Assert.Equal(1920 * 1080 * 3 / 2, format.FrameSize);
    }

    [Fact]
    public void AnOddSizedPictureRoundsItsChromaUp() {
        // Rounding down loses the right-hand column and the bottom row of every odd-sized frame,
        // which is the defect that survives review because the test video was 1920 wide.
        var format = new VideoFormat(3, 5, VideoPixelLayout.Yuv420Planar);

        Assert.Equal(2, format.PlaneWidth(1));
        Assert.Equal(3, format.PlaneHeight(1));
    }

    [Fact]
    public void AFourTwoTwoFrameSubsamplesHorizontallyOnly() {
        var format = new VideoFormat(64, 32, VideoPixelLayout.Yuv422Planar);

        Assert.Equal(32, format.PlaneWidth(1));
        Assert.Equal(32, format.PlaneHeight(1));
    }

    [Fact]
    public void APackedFrameIsOnePlaneOfFourBytesATexel() {
        var format = new VideoFormat(8, 4, VideoPixelLayout.Bgra8);

        Assert.Equal(1, format.PlaneCount);
        Assert.Equal(32, format.PlaneWidth(0));
        Assert.Equal(8 * 4 * 4, format.FrameSize);
    }

    [Fact]
    public void PlanesAreLaidOutEndToEndInOneBuffer() {
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(16, 16, VideoPixelLayout.Yuv420Planar));

        Assert.Equal(0, frame.Offset(0));
        Assert.Equal(256, frame.Offset(1));
        Assert.Equal(256 + 64, frame.Offset(2));
        Assert.Equal(16, frame.Stride(0));
        Assert.Equal(8, frame.Stride(1));
    }

    [Fact]
    public void ARowIsWritableAndLandsWhereTheOffsetSaysItDoes() {
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(4, 4, VideoPixelLayout.Yuv420Planar));
        frame.Row(0, 2).Fill(200);

        Assert.Equal(200, frame.Pixels[8]);
        Assert.Equal(0, frame.Pixels[7]);
    }

    [Fact]
    public void ClearingAYuvFrameIsBlackRatherThanGreen() {
        // Zeroing a YUV frame gives chroma zero, which is a strong green — the picture everybody has
        // seen once and attributed to the decoder.
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(4, 4, VideoPixelLayout.Yuv420Planar));
        frame.Clear();

        Assert.Equal(16, frame.Plane(0)[0]);
        Assert.Equal(128, frame.Plane(1)[0]);
        Assert.Equal(128, frame.Plane(2)[0]);
    }

    [Fact]
    public void AFullRangeFrameClearsToZeroLuma() {
        var frame = new VideoFrame();

        frame.Reset(
            new VideoFormat(4, 4, VideoPixelLayout.Yuv420Planar, Range: VideoColourRange.Full)
        );

        frame.Clear();

        Assert.Equal(0, frame.Plane(0)[0]);
    }

    [Fact]
    public void AnInvalidFormatIsRefusedRatherThanAllocated() {
        var frame = new VideoFrame();

        Assert.Throws<ArgumentException>(
            () => frame.Reset(new VideoFormat(0, 16, VideoPixelLayout.Yuv420Planar))
        );
    }

    [Fact]
    public void CopyingAFrameCopiesItsTimingToo() {
        var source = new VideoFrame();

        source.Reset(new VideoFormat(4, 4, VideoPixelLayout.Grey8));
        source.Plane(0).Fill(77);
        source.Timestamp = TimeSpan.FromSeconds(3);
        source.IsKeyFrame = true;

        var copy = new VideoFrame();

        copy.CopyFrom(source);

        Assert.Equal(TimeSpan.FromSeconds(3), copy.Timestamp);
        Assert.True(copy.IsKeyFrame);
        Assert.Equal(77, copy.Plane(0)[15]);
    }

    [Fact]
    public void ThePoolReusesFramesRatherThanAllocatingThem() {
        var pool = new VideoFramePool(4);
        var format = new VideoFormat(64, 64, VideoPixelLayout.Yuv420Planar);

        for (var index = 0; index < 100; index++) {
            pool.Return(pool.Rent(in format));
        }

        Assert.Equal(1, pool.Allocations);
    }

    [Fact]
    public void ThePoolStopsHoldingFramesAtItsCapacity() {
        var pool = new VideoFramePool(2);
        var format = new VideoFormat(8, 8, VideoPixelLayout.Grey8);

        var frames = new[] { pool.Rent(in format), pool.Rent(in format), pool.Rent(in format) };

        foreach (var frame in frames) {
            pool.Return(frame);
        }

        Assert.Equal(2, pool.Available);
    }

    [Fact]
    public void RentingAndReturningAtASteadySizeAllocatesNothing() {
        var pool = new VideoFramePool(4);
        var format = new VideoFormat(320, 240, VideoPixelLayout.Yuv420Planar);

        // The claim playback rests on: three megabytes a frame at sixty a second is 180 MB/s into
        // gen 2 if this is ever false.
        Measured.NothingAllocated(
            () => pool.Return(pool.Rent(in format)),
            because: "Three megabytes a frame at sixty a second is 180 MB/s into gen 2."
        );
    }
}
