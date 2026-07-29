// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Video.Playback;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>The clock, and the rule that the current frame is the newest one whose time has passed.</summary>
/// <remarks>
///     Every test here runs the player without its decode thread. That is not a workaround: the pump
///     is public precisely so that a single-threaded platform — and a test that wants an answer rather
///     than a race — drives it directly.
/// </remarks>
public sealed class VideoPlayerTests {
    static readonly VideoPlayerOptions Deterministic = new() { UseDecodeThread = false, QueueCapacity = 4 };

    [Fact]
    public void TheClockAdvancesByTheFrameDelta() {
        var clock = new VideoClock();

        clock.Start();
        clock.Advance(TimeSpan.FromMilliseconds(16));
        clock.Advance(TimeSpan.FromMilliseconds(16));

        Assert.Equal(TimeSpan.FromMilliseconds(32), clock.Time);
    }

    [Fact]
    public void AStoppedClockDoesNotMove() {
        var clock = new VideoClock();

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, clock.Time);
    }

    [Fact]
    public void AMasterClockIsReadRatherThanIntegrated() {
        var position = TimeSpan.FromSeconds(4);
        var clock = new VideoClock { Master = () => position };

        clock.Start();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(4), clock.Time);

        position = TimeSpan.FromSeconds(9);
        clock.Advance(TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(9), clock.Time);
    }

    [Fact]
    public void DroppingTheMasterResumesFromWhereItLeftOff() {
        var clock = new VideoClock { Master = () => TimeSpan.FromSeconds(7) };

        clock.Start();
        clock.Advance(TimeSpan.Zero);
        clock.Master = null;
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(8), clock.Time);
    }

    [Fact]
    public void ARateOfAHalfHoldsEachFrameTwiceAsLong() {
        var clock = new VideoClock { Rate = 0.5 };

        clock.Start();
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.Time);
    }

    [Fact]
    public void ANegativeRateIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoClock { Rate = -1 });

    [Fact]
    public void FramesAreShownInOrderAsTheirTimePasses() {
        using var player = new VideoPlayer(new StepDecoder(5), Deterministic);

        player.Play();

        for (var index = 0; index < 5; index++) {
            player.Update(index == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(40));

            Assert.NotNull(player.CurrentFrame);
            Assert.Equal(TimeSpan.FromMilliseconds(40 * index), player.CurrentFrame.Timestamp);
        }

        Assert.Equal(5, player.FramesShown);
        Assert.Equal(0, player.FramesDropped);
    }

    [Fact]
    public void AFrameThatIsNotDueYetIsNotShown() {
        using var player = new VideoPlayer(new StepDecoder(5), Deterministic);

        player.Play();
        player.Update(TimeSpan.Zero);
        player.Update(TimeSpan.FromMilliseconds(20));

        Assert.Equal(TimeSpan.Zero, player.CurrentFrame!.Timestamp);
        Assert.Equal(1, player.FramesShown);
    }

    [Fact]
    public void AStallShowsTheNewestDueFrameAndCountsTheRest() {
        using var player = new VideoPlayer(new StepDecoder(10), Deterministic);

        player.Play();
        player.Update(TimeSpan.Zero);

        // A breakpoint, a page fault, a game at 20 fps against 60 fps content: three frames became
        // due at once. Showing them in sequence would put the picture behind and keep it there.
        player.Update(TimeSpan.FromMilliseconds(120));

        Assert.Equal(TimeSpan.FromMilliseconds(120), player.CurrentFrame!.Timestamp);
        Assert.Equal(2, player.FramesShown);
        Assert.Equal(2, player.FramesDropped);
    }

    [Fact]
    public void APausedPlayerHoldsItsFrame() {
        using var player = new VideoPlayer(new StepDecoder(5), Deterministic);

        player.Play();
        player.Update(TimeSpan.Zero);
        player.Pause();
        player.Update(TimeSpan.FromSeconds(1));

        Assert.Equal(VideoPlaybackState.Paused, player.State);
        Assert.Equal(TimeSpan.Zero, player.CurrentFrame!.Timestamp);
    }

    [Fact]
    public void TheEndIsReachedRatherThanStalledOn() {
        using var player = new VideoPlayer(new StepDecoder(2), Deterministic);

        player.Play();

        for (var index = 0; index < 6; index++) {
            player.Update(TimeSpan.FromMilliseconds(40));
        }

        Assert.Equal(VideoPlaybackState.Ended, player.State);
    }

    [Fact]
    public void LoopingRestartsWithTimestampsThatKeepGoingForwards() {
        // The frames of the second pass carry the stream's own timestamps, which start again at
        // zero. Without an offset every one of them would be instantly late and the whole second
        // pass would be dropped.
        using var player = new VideoPlayer(new StepDecoder(3), Deterministic with { Loop = true });

        player.Play();

        var seen = new List<TimeSpan>();

        for (var index = 0; index < 8; index++) {
            player.Update(index == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(40));

            if (player.CurrentFrame is { } frame) {
                seen.Add(frame.Timestamp);
            }
        }

        Assert.Equal(VideoPlaybackState.Playing, player.State);
        Assert.Equal(seen.OrderBy(time => time), seen);
        Assert.Contains(TimeSpan.FromMilliseconds(160), seen);
        Assert.Equal(0, player.FramesDropped);
    }

    [Fact]
    public void SeekingMovesTheClockAtOnceAndThePictureFollows() {
        using var player = new VideoPlayer(new StepDecoder(20), Deterministic);

        player.Play();
        player.Update(TimeSpan.Zero);
        player.Seek(TimeSpan.FromMilliseconds(400));

        Assert.Equal(TimeSpan.FromMilliseconds(400), player.Position);

        player.Update(TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMilliseconds(400), player.CurrentFrame!.Timestamp);
    }

    [Fact]
    public void StoppingReturnsToTheStart() {
        using var player = new VideoPlayer(new StepDecoder(20), Deterministic);

        player.Play();
        player.Update(TimeSpan.FromMilliseconds(200));
        player.Stop();

        Assert.Equal(VideoPlaybackState.Stopped, player.State);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Fact]
    public void AStallIsCountedWhenNothingIsDecodedAndTheVideoIsStillPlaying() {
        using var player = new VideoPlayer(new StarvedDecoder(), Deterministic);

        player.Play();
        player.Update(TimeSpan.FromMilliseconds(40));

        Assert.Equal(1, player.DecodeStalls);
    }

    [Fact]
    public void TheFrameVersionMovesOnlyWhenThePictureDoes() {
        using var player = new VideoPlayer(new StepDecoder(5), Deterministic);

        player.Play();
        player.Update(TimeSpan.Zero);

        var version = player.FrameVersion;

        player.Update(TimeSpan.FromMilliseconds(10));

        Assert.Equal(version, player.FrameVersion);

        player.Update(TimeSpan.FromMilliseconds(40));

        Assert.NotEqual(version, player.FrameVersion);
    }

    [Fact]
    public void AWholeWebMPlaysThroughThePlayer() {
        using var player = new VideoPlayer(
            new WebMVideoStreamDecoder(VideoTestContent.Video(16, 16, 4).Stream()),
            Deterministic
        );

        player.Play();

        for (var index = 0; index < 4; index++) {
            player.Update(index == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(40));

            Assert.Equal(16 + index, player.CurrentFrame!.Plane(0)[0]);
        }
    }

    [Fact]
    public void TheDecodeThreadFillsTheQueueOnItsOwn() {
        using var player = new VideoPlayer(new StepDecoder(100));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (player.QueuedFrames == 0 && DateTime.UtcNow < deadline) {
            Thread.Sleep(1);
        }

        Assert.True(player.QueuedFrames > 0, "the decode thread produced nothing within five seconds");
    }

    /// <summary>A decoder that produces solid frames forty milliseconds apart, and can seek.</summary>
    sealed class StepDecoder(int frames) : IVideoStreamDecoder {
        int next;

        public VideoFormat Format { get; } = new(
            16,
            16,
            VideoPixelLayout.Yuv420Planar,
            new VideoRational(25, 1)
        );

        public TimeSpan Duration => TimeSpan.FromMilliseconds(40 * frames);

        public TimeSpan Position { get; private set; }

        public bool CanSeek => true;

        public VideoDecodeStatus DecodeNext(VideoFrame destination) {
            if (next >= frames) {
                return VideoDecodeStatus.EndOfStream;
            }

            destination.Reset(Format);
            destination.Plane(0).Fill((byte)(16 + (next % 200)));
            destination.Timestamp = TimeSpan.FromMilliseconds(40 * next);
            destination.IsKeyFrame = true;
            Position = destination.Timestamp;
            next++;

            return VideoDecodeStatus.Decoded;
        }

        public void Seek(TimeSpan position) {
            next = (int)(position.TotalMilliseconds / 40);
            Position = position;
        }

        public void Dispose() { }
    }

    /// <summary>A decoder that never has anything ready, which is what a stall looks like.</summary>
    sealed class StarvedDecoder : IVideoStreamDecoder {
        public VideoFormat Format { get; } = new(16, 16, VideoPixelLayout.Yuv420Planar);

        public TimeSpan Duration => TimeSpan.FromSeconds(10);

        public TimeSpan Position => TimeSpan.Zero;

        public bool CanSeek => false;

        public VideoDecodeStatus DecodeNext(VideoFrame destination) => VideoDecodeStatus.NeedMoreData;

        public void Seek(TimeSpan position) => throw new NotSupportedException();

        public void Dispose() { }
    }
}
