// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Video.Containers;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>What a container reader has to get right for a file to play at all.</summary>
public sealed class MatroskaDemuxerTests {
    [Fact]
    public void TheHeaderIsReadAndTheTracksAreFound() {
        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 640, 480, defaultDurationNanoseconds: 40_000_000)
                .AudioTrack(2, 48_000, 2)
                .Cluster(0)
                .SimpleBlock(1, 0, keyFrame: true, [1, 2, 3])
        );

        Assert.Equal("webm", demuxer.DocType);
        Assert.Equal(2, demuxer.Tracks.Count);

        var video = demuxer.FindTrack(MatroskaTrackKind.Video);

        Assert.NotNull(video);
        Assert.Equal(640, video.PixelWidth);
        Assert.Equal(480, video.PixelHeight);
        Assert.Equal("V_UNCOMPRESSED", video.CodecId);
        Assert.Equal("I420", video.ColourSpace);
        Assert.Equal(TimeSpan.FromMilliseconds(40), video.DefaultDuration);

        var audio = demuxer.FindTrack(MatroskaTrackKind.Audio);

        Assert.NotNull(audio);
        Assert.Equal(48_000, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
    }

    [Fact]
    public void ADisplaySizeDefaultsToTheCodedSize() {
        using var demuxer = Open(new WebMBuilder().VideoTrack(1, 720, 480).Cluster(0));

        var video = demuxer.FindTrack(MatroskaTrackKind.Video)!;

        Assert.Equal(720, video.DisplayWidth);
        Assert.Equal(480, video.DisplayHeight);
    }

    [Fact]
    public void SomethingThatIsNotMatroskaSaysSo() {
        var bytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0 };

        Assert.Throws<InvalidDataException>(
            () => new MatroskaDemuxer(new MemoryStream(bytes, writable: false))
        );
    }

    [Fact]
    public void ADocTypeNobodyKnowsIsRefusedRatherThanGuessedAt() {
        var builder = new WebMBuilder { DocType = "wobm" };

        builder.VideoTrack(1, 16, 16).Cluster(0);

        Assert.Throws<InvalidDataException>(() => Open(builder));
    }

    [Fact]
    public void BlocksComeOutInOrderWithTheirClusterTimestampAdded() {
        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 16, 16)
                .Cluster(0)
                .SimpleBlock(1, 0, keyFrame: true, [1])
                .SimpleBlock(1, 10, keyFrame: false, [2])
                .Cluster(100)
                .SimpleBlock(1, 5, keyFrame: true, [3])
        );

        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(105)],
            Timestamps(demuxer, 1, 3)
        );
    }

    [Fact]
    public void ANegativeRelativeTimestampGoesBackwards() {
        // Legal, and how a muxer puts a frame that belongs before the cluster's own time into it.
        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 16, 16)
                .Cluster(100)
                .SimpleBlock(1, -20, keyFrame: true, [1])
        );

        var packet = demuxer.ReadPacket(1);

        Assert.NotNull(packet);
        Assert.Equal(TimeSpan.FromMilliseconds(80), packet.Timestamp);
    }

    [Fact]
    public void TheKeyFrameBitIsRead() {
        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 16, 16)
                .Cluster(0)
                .SimpleBlock(1, 0, keyFrame: true, [1])
                .SimpleBlock(1, 1, keyFrame: false, [2])
        );

        Assert.True(demuxer.ReadPacket(1)!.IsKeyFrame);
        Assert.False(demuxer.ReadPacket(1)!.IsKeyFrame);
    }

    [Fact]
    public void ABlockGroupIsAKeyFrameExactlyWhenNothingReferencesForward() {
        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 16, 16)
                .Cluster(0)
                .BlockGroup(1, 0, [1], durationTicksOrZero: 40)
                .BlockGroup(1, 40, [2], referenced: true)
        );

        var first = demuxer.ReadPacket(1)!;

        Assert.True(first.IsKeyFrame);
        Assert.Equal(TimeSpan.FromMilliseconds(40), first.Duration);
        Assert.False(demuxer.ReadPacket(1)!.IsKeyFrame);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryLacingSchemeSplitsIntoTheSameThreeFrames(int lacing) {
        // Fixed lacing cannot state per-frame sizes, so its frames have to be equal length.
        byte[][] frames = lacing == 2
            ? [[1, 1, 1], [2, 2, 2], [3, 3, 3]]
            : [[1], [2, 2], [3, 3, 3]];

        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 16, 16, defaultDurationNanoseconds: 20_000_000)
                .Cluster(0)
                .LacedBlock(1, 0, keyFrame: true, lacing, frames)
        );

        for (var index = 0; index < frames.Length; index++) {
            var packet = demuxer.ReadPacket(1);

            Assert.NotNull(packet);
            Assert.Equal(frames[index], packet.Data.ToArray());

            // A lace's frames share the block's time and are spread by the track's default duration.
            Assert.Equal(TimeSpan.FromMilliseconds(20 * index), packet.Timestamp);
        }
    }

    [Fact]
    public void ATrackNobodyAsksForIsSkippedRatherThanBuffered() {
        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 16, 16)
                .AudioTrack(2, 48_000, 1)
                .Cluster(0)
                .SimpleBlock(2, 0, keyFrame: true, new byte[4096])
                .SimpleBlock(1, 0, keyFrame: true, [7])
        );

        var packet = demuxer.ReadPacket(1);

        Assert.NotNull(packet);
        Assert.Equal([7], packet.Data.ToArray());
    }

    [Fact]
    public void BothTracksComeOutWhenBothAreRead() {
        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 16, 16)
                .AudioTrack(2, 48_000, 1)
                .Cluster(0)
                .SimpleBlock(2, 0, keyFrame: true, [9])
                .SimpleBlock(1, 0, keyFrame: true, [7])
        );

        // Following the audio track before anything is read is what makes the order of these two
        // calls stop mattering: the audio packet sits in front of the video one in the file, so
        // without it the video read would skip past it and it would be gone.
        demuxer.Follow(2);

        Assert.Equal([7], demuxer.ReadPacket(1)!.Data.ToArray());
        Assert.Equal([9], demuxer.ReadPacket(2)!.Data.ToArray());
    }

    [Fact]
    public void ATrackFollowedTooLateHasAlreadyBeenSkippedPast() {
        // Stated rather than left to be discovered: this is why both stream decoders call Follow in
        // their constructors, and why a caller assembling a demuxer by hand has to as well.
        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 16, 16)
                .AudioTrack(2, 48_000, 1)
                .Cluster(0)
                .SimpleBlock(2, 0, keyFrame: true, [9])
                .SimpleBlock(1, 0, keyFrame: true, [7])
        );

        demuxer.ReadPacket(1);

        Assert.Null(demuxer.ReadPacket(2));
    }

    [Fact]
    public void AnUnknownSizeClusterEndsAtTheNextThingThatCannotBeInsideOne() {
        var builder = new WebMBuilder { UnknownSegmentSize = true, UnknownLastClusterSize = true };

        builder
            .VideoTrack(1, 16, 16)
            .Cluster(0)
            .SimpleBlock(1, 0, keyFrame: true, [1])
            .SimpleBlock(1, 10, keyFrame: true, [2]);

        using var demuxer = Open(builder);

        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(10)],
            Timestamps(demuxer, 1, 2)
        );

        Assert.Null(demuxer.ReadPacket(1));
    }

    [Fact]
    public void AnUnknownElementIsSkippedRatherThanFatal() {
        // The whole reason EBML exists. An element the reader has never heard of, in the middle of a
        // cluster, must not stop the blocks either side of it arriving.
        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 16, 16)
                .Cluster(0)
                .SimpleBlock(1, 0, keyFrame: true, [1])
                .Void(16)
                .SimpleBlock(1, 10, keyFrame: true, [2])
        );

        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(10)],
            Timestamps(demuxer, 1, 2)
        );
    }

    [Fact]
    public void TheDurationIsScaledByTheSegmentsOwnTick() {
        var builder = new WebMBuilder { TimestampScale = 1_000_000 };

        builder.VideoTrack(1, 16, 16).Duration(2_500).Cluster(0).SimpleBlock(1, 0, true, [1]);

        using var demuxer = Open(builder);

        Assert.Equal(TimeSpan.FromMilliseconds(2_500), demuxer.Duration);
    }

    [Fact]
    public void ANonDefaultTimestampScaleChangesWhatABlocksTimeMeans() {
        var builder = new WebMBuilder { TimestampScale = 100_000 };

        builder.VideoTrack(1, 16, 16).Cluster(10).SimpleBlock(1, 0, true, [1]);

        using var demuxer = Open(builder);

        // Ten ticks of a tenth of a millisecond is one millisecond, not ten.
        Assert.Equal(TimeSpan.FromMilliseconds(1), demuxer.ReadPacket(1)!.Timestamp);
    }

    [Fact]
    public void SeekingWithCuesLandsOnTheClusterAtOrBeforeThePosition() {
        using var demuxer = Open(VideoTestContent.Video(16, 16, 5, cues: true));

        Assert.True(demuxer.HasCues);

        demuxer.SeekTo(TimeSpan.FromMilliseconds(130), 1);

        var packet = demuxer.ReadPacket(1);

        Assert.NotNull(packet);
        Assert.Equal(TimeSpan.FromMilliseconds(120), packet.Timestamp);
    }

    [Fact]
    public void SeekingWithNoCuesRewindsAndScans() {
        using var demuxer = Open(VideoTestContent.Video(16, 16, 5));

        Assert.False(demuxer.HasCues);

        Timestamps(demuxer, 1, 3);
        demuxer.SeekTo(TimeSpan.FromMilliseconds(130), 1);

        Assert.Equal(TimeSpan.Zero, demuxer.ReadPacket(1)!.Timestamp);
    }

    [Fact]
    public void SeekingDropsWhatWasBuffered() {
        using var demuxer = Open(VideoTestContent.Video(16, 16, 5, cues: true));

        demuxer.ReadPacket(1);
        demuxer.SeekTo(TimeSpan.Zero, 1);

        Assert.Equal(TimeSpan.Zero, demuxer.ReadPacket(1)!.Timestamp);
    }

    [Fact]
    public void ANonSeekableStreamStillPlays() {
        using var stream = new ForwardOnlyStream(VideoTestContent.Video(16, 16, 3).Build());
        using var demuxer = new MatroskaDemuxer(stream);

        Assert.False(demuxer.CanSeek);
        Assert.Equal(3, Timestamps(demuxer, 1, 3).Count);
        Assert.Throws<NotSupportedException>(() => demuxer.SeekTo(TimeSpan.Zero, 1));
    }

    [Fact]
    public void ColourMetadataIsCarriedThrough() {
        using var demuxer = Open(
            new WebMBuilder()
                .VideoTrack(1, 16, 16, matrixCoefficients: 5, range: 2)
                .Cluster(0)
                .SimpleBlock(1, 0, true, [1])
        );

        var track = demuxer.FindTrack(MatroskaTrackKind.Video)!;

        Assert.Equal(VideoColourMatrix.Bt601, track.ColourMatrix);
        Assert.Equal(VideoColourRange.Full, track.ColourRange);
    }

    static MatroskaDemuxer Open(WebMBuilder builder) => new(builder.Stream());

    static List<TimeSpan> Timestamps(MatroskaDemuxer demuxer, int track, int count) {
        var times = new List<TimeSpan>();

        for (var index = 0; index < count; index++) {
            var packet = demuxer.ReadPacket(track);

            if (packet is null) {
                break;
            }

            times.Add(packet.Timestamp);
            demuxer.Release(packet);
        }

        return times;
    }

    /// <summary>A stream that reads forwards and refuses to say how long it is.</summary>
    sealed class ForwardOnlyStream(byte[] bytes) : Stream {
        readonly MemoryStream inner = new(bytes, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            if (disposing) {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
