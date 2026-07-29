// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Video.Gpu;
using Vixen.Video.Playback;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>
///     The upload path, against the backend that records what was asked of it rather than doing it.
/// </summary>
public sealed class VideoTextureTests {
    [Fact]
    public void AFourTwoZeroFrameBecomesThreePlanesAndThreeCopies() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var texture = new VideoTexture(device, "test");

        Upload(device, texture, Frame(64, 32));

        Assert.Equal(3, texture.PlaneCount);
        Assert.Equal(3, device.Recorder!.CountOf(RecordedCommandKind.CopyBufferToTexture));

        // One barrier group in and one out, each covering all three planes — grouped, because a
        // driver given three barriers separately inserts three stalls.
        var barriers = device.Recorder.OfKind(RecordedCommandKind.Barrier);

        Assert.Equal(2, barriers.Count);
        Assert.All(barriers, barrier => Assert.Equal(3, barrier.B));
    }

    [Fact]
    public void EachPlaneIsCopiedAtItsOwnSize() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var texture = new VideoTexture(device);

        Upload(device, texture, Frame(64, 32));

        var copies = device.Recorder!.OfKind(RecordedCommandKind.CopyBufferToTexture);

        // The E slot is the copy's width in texels: luma full size, chroma halved.
        Assert.Equal(64, copies[0].E);
        Assert.Equal(32, copies[1].E);
        Assert.Equal(32, copies[2].E);
    }

    [Fact]
    public void APackedFrameIsOnePlaneOfTexelsRatherThanOfBytes() {
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var texture = new VideoTexture(device);

        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(16, 8, VideoPixelLayout.Bgra8));
        Upload(device, texture, frame);

        var copies = device.Recorder!.OfKind(RecordedCommandKind.CopyBufferToTexture);

        Assert.Single(copies);
        Assert.Equal(16, copies[0].E);
    }

    [Fact]
    public void TheCoefficientsFollowTheFramesOwnColourMetadata() {
        using var device = new NullDevice();
        using var texture = new VideoTexture(device);

        var frame = new VideoFrame();

        frame.Reset(
            new VideoFormat(
                16,
                16,
                VideoPixelLayout.Yuv420Planar,
                Range: VideoColourRange.Full,
                Matrix: VideoColourMatrix.Bt601
            )
        );

        Upload(device, texture, frame);

        Assert.Equal(
            VideoColourCoefficients.For(VideoColourMatrix.Bt601, VideoColourRange.Full),
            texture.Coefficients
        );
    }

    [Fact]
    public void AResolutionChangeRebuildsThePlanes() {
        using var device = new NullDevice();
        using var texture = new VideoTexture(device);

        Upload(device, texture, Frame(32, 32));

        var before = texture.Plane(0);

        Upload(device, texture, Frame(64, 64));

        Assert.NotEqual(before, texture.Plane(0));
        Assert.Equal(64, texture.Format.Width);
    }

    [Fact]
    public void APlayerWhosePictureHasNotChangedIsNotUploadedAgain() {
        // A 24 fps video in a 144 fps game: one upload in six frames, not one per frame.
        using var device = new NullDevice(new NullDeviceOptions { Record = true });
        using var texture = new VideoTexture(device);
        using var player = new VideoPlayer(
            new WebMVideoStreamDecoder(VideoTestContent.Video(16, 16, 3).Stream()),
            new VideoPlayerOptions { UseDecodeThread = false, QueueCapacity = 2 }
        );

        player.Play();
        player.Update(TimeSpan.Zero);

        using (var commands = device.BeginCommandList()) {
            Assert.True(texture.Upload(commands, player));
            Assert.False(texture.Upload(commands, player));
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        Assert.Equal(3, device.Recorder!.CountOf(RecordedCommandKind.CopyBufferToTexture));
    }

    [Fact]
    public void UploadingAfterDisposalSaysSoRatherThanTouchingFreedHandles() {
        using var device = new NullDevice();
        var texture = new VideoTexture(device);

        texture.Dispose();

        using var commands = device.BeginCommandList();

        Assert.Throws<ObjectDisposedException>(() => texture.Upload(commands, Frame(16, 16)));
    }

    static VideoFrame Frame(int width, int height) {
        var frame = new VideoFrame();

        frame.Reset(new VideoFormat(width, height, VideoPixelLayout.Yuv420Planar));
        frame.Clear();

        return frame;
    }

    /// <summary>
    ///     Records an upload and submits it, because the null backend hands its recorder the list at
    ///     submit rather than as the calls arrive.
    /// </summary>
    static void Upload(NullDevice device, VideoTexture texture, VideoFrame frame) {
        using var commands = device.BeginCommandList();

        texture.Upload(commands, frame);
        commands.Finish();
        device.GraphicsQueue.Submit([commands]);
    }
}
