// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Video.Codecs;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>The demuxer and the codec, joined — which is what a player actually holds.</summary>
public sealed class WebMVideoStreamDecoderTests {
    [Fact]
    public void EveryFrameComesOutInOrderWithItsPicture() {
        using var decoder = new WebMVideoStreamDecoder(VideoTestContent.Video(16, 16, 4).Stream());

        var frame = new VideoFrame();

        for (var index = 0; index < 4; index++) {
            Assert.Equal(VideoDecodeStatus.Decoded, decoder.DecodeNext(frame));
            Assert.Equal(TimeSpan.FromMilliseconds(40 * index), frame.Timestamp);
            Assert.Equal(16 + index, frame.Plane(0)[0]);
        }

        Assert.Equal(VideoDecodeStatus.EndOfStream, decoder.DecodeNext(frame));
    }

    [Fact]
    public void TheFormatComesFromTheTrackAndItsFourCc() {
        using var decoder = new WebMVideoStreamDecoder(VideoTestContent.Video(64, 48, 1).Stream());

        Assert.Equal(64, decoder.Format.Width);
        Assert.Equal(48, decoder.Format.Height);
        Assert.Equal(VideoPixelLayout.Yuv420Planar, decoder.Format.Layout);
    }

    [Fact]
    public void ADefaultDurationBecomesAnExactFrameRate() {
        // 33 366 667 ns is NTSC. A player that rounded it to 29.97 would drift a frame every
        // thirty-three seconds.
        var builder = new WebMBuilder()
            .VideoTrack(1, 16, 16, defaultDurationNanoseconds: 33_366_667)
            .Cluster(0)
            .SimpleBlock(1, 0, keyFrame: true, VideoTestContent.I420(16, 16, 30));

        using var decoder = new WebMVideoStreamDecoder(builder.Stream());

        Assert.InRange(decoder.Format.FrameRate.Hz, 29.9699, 29.9701);
    }

    [Fact]
    public void TheDurationIsTheSegmentsOwn() {
        using var decoder = new WebMVideoStreamDecoder(VideoTestContent.Video(16, 16, 5).Stream());

        Assert.Equal(TimeSpan.FromMilliseconds(200), decoder.Duration);
    }

    [Fact]
    public void SeekingLandsAtOrBeforeThePositionAsked() {
        using var decoder = new WebMVideoStreamDecoder(
            VideoTestContent.Video(16, 16, 6, cues: true).Stream()
        );

        var frame = new VideoFrame();

        decoder.Seek(TimeSpan.FromMilliseconds(170));
        Assert.Equal(VideoDecodeStatus.Decoded, decoder.DecodeNext(frame));
        Assert.True(frame.Timestamp <= TimeSpan.FromMilliseconds(170));
        Assert.Equal(TimeSpan.FromMilliseconds(160), frame.Timestamp);
    }

    [Fact]
    public void ACodecNobodyRegisteredSaysWhatIsRegistered() {
        var builder = new WebMBuilder()
            .VideoTrack(1, 16, 16, fourCc: "", codecId: "V_VP9")
            .Cluster(0)
            .SimpleBlock(1, 0, keyFrame: true, [1, 2, 3]);

        var thrown = Assert.Throws<NotSupportedException>(
            () => new WebMVideoStreamDecoder(builder.Stream())
        );

        Assert.Contains("V_VP9", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("uncompressed", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithNoVideoTrackSaysSo() {
        var builder = new WebMBuilder()
            .AudioTrack(1, 48_000, 2)
            .Cluster(0)
            .SimpleBlock(1, 0, keyFrame: true, [1]);

        Assert.Throws<InvalidDataException>(() => new WebMVideoStreamDecoder(builder.Stream()));
    }

    [Fact]
    public void AShortPacketIsRejectedRatherThanDecodedIntoWhateverIsThere() {
        var builder = new WebMBuilder()
            .VideoTrack(1, 16, 16)
            .Cluster(0)
            .SimpleBlock(1, 0, keyFrame: true, [1, 2, 3]);

        using var decoder = new WebMVideoStreamDecoder(builder.Stream());

        Assert.Throws<InvalidDataException>(() => decoder.DecodeNext(new VideoFrame()));
    }

    [Fact]
    public void Yv12IsI420WithTheChromaPlanesSwapped() {
        var builder = new WebMBuilder()
            .VideoTrack(1, 4, 4, "YV12")
            .Cluster(0)
            .SimpleBlock(1, 0, keyFrame: true, VideoTestContent.Yv12(4, 4, 100, blue: 40, red: 200));

        using var decoder = new WebMVideoStreamDecoder(builder.Stream());

        var frame = new VideoFrame();

        Assert.Equal(VideoDecodeStatus.Decoded, decoder.DecodeNext(frame));
        Assert.Equal(40, frame.Plane(1)[0]);
        Assert.Equal(200, frame.Plane(2)[0]);
    }

    [Fact]
    public void ARegisteredCodecIsPreferredOverTheBuiltInOne() {
        VideoCodecRegistry.Register(new CountingFactory());

        // Seven by seven, so this factory claims a shape no other test in the process uses: the
        // registry is static, and a test that hijacked every 4x4 video would be a test that broke
        // its neighbours depending on which ran first.
        var builder = new WebMBuilder()
            .VideoTrack(1, 7, 7)
            .Cluster(0)
            .SimpleBlock(1, 0, keyFrame: true, VideoTestContent.I420(7, 7, 60));

        using var decoder = new WebMVideoStreamDecoder(builder.Stream());

        var frame = new VideoFrame();

        Assert.Equal(VideoDecodeStatus.Decoded, decoder.DecodeNext(frame));

        // The registered codec paints a value the uncompressed one never would.
        Assert.Equal(200, frame.Plane(0)[0]);
        Assert.Equal("counting", VideoCodecRegistry.RegisteredNames()[0]);
    }

    /// <summary>A codec that ignores its input, so that "which one ran" is answerable.</summary>
    sealed class CountingFactory : IVideoCodecFactory {
        public string Name => "counting";

        public bool CanDecode(in VideoTrackInfo track) => track.Width == 7 && track.Height == 7;

        public IVideoCodec Create(in VideoTrackInfo track) =>
            new CountingCodec(new VideoFormat(track.Width, track.Height, VideoPixelLayout.Yuv420Planar));
    }

    sealed class CountingCodec(VideoFormat format) : IVideoCodec {
        public VideoFormat Format { get; } = format;

        public VideoDecodeStatus Decode(in VideoPacket packet, VideoFrame destination) {
            destination.Reset(Format);
            destination.Plane(0).Fill(200);
            destination.Timestamp = packet.Timestamp;

            return VideoDecodeStatus.Decoded;
        }

        public VideoDecodeStatus Drain(VideoFrame destination) => VideoDecodeStatus.EndOfStream;

        public void Reset() { }

        public void Dispose() { }
    }
}
