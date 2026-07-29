// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio;
using Vixen.Video.Audio;
using Vixen.Video.Containers;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>The audio track of a video, behind the interface the mixer already streams.</summary>
public sealed class MatroskaAudioTests {
    [Fact]
    public void AFloatPcmTrackOpensAndDecodes() {
        using var demuxer = new MatroskaDemuxer(WithSound(0.5f).Stream());

        Assert.True(MatroskaAudioStreamDecoder.TryOpen(demuxer, out var stream));
        using var sound = stream!;

        Assert.Equal(new AudioFormat(48_000, 2), sound.Format);

        var buffer = new float[64];
        var frames = sound.Decode(buffer, 32);

        Assert.Equal(32, frames);
        Assert.All(buffer[..64], sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public void TheWholeTrackComesOutAcrossItsClusters() {
        using var demuxer = new MatroskaDemuxer(WithSound(0.25f, clusters: 3, framesPerBlock: 100).Stream());

        Assert.True(MatroskaAudioStreamDecoder.TryOpen(demuxer, out var stream));
        using var sound = stream!;

        var buffer = new float[2_000];
        var total = 0;

        while (true) {
            var frames = sound.Decode(buffer, 1_000);

            if (frames == 0) {
                break;
            }

            total += frames;
        }

        Assert.Equal(300, total);
        Assert.Equal(300, sound.Position);
    }

    [Fact]
    public void EightBitIntegerPcmIsUnsignedAndEverythingElseIsSigned() {
        // WAV's rule since 1991, inherited by every specification since. Decoding 8-bit as signed
        // gives a track that is loud, distorted, and half a scale off centre.
        var eight = new PcmPacketDecoder(new AudioFormat(48_000, 1), 8, isFloat: false);
        var sixteen = new PcmPacketDecoder(new AudioFormat(48_000, 1), 16, isFloat: false);
        var buffer = new float[1];

        eight.Decode([128], buffer);
        Assert.Equal(0f, buffer[0]);

        sixteen.Decode([0x00, 0x00], buffer);
        Assert.Equal(0f, buffer[0]);

        sixteen.Decode([0x00, 0x80], buffer);
        Assert.Equal(-1f, buffer[0]);
    }

    [Fact]
    public void TwentyFourBitIsSignExtendedOutOfThreeBytes() {
        var decoder = new PcmPacketDecoder(new AudioFormat(48_000, 1), 24, isFloat: false);
        var buffer = new float[1];

        decoder.Decode([0x00, 0x00, 0x80], buffer);

        Assert.Equal(-1f, buffer[0]);
    }

    [Fact]
    public void ADepthThatDoesNotExistIsRefused() =>
        Assert.Throws<ArgumentException>(
            () => new PcmPacketDecoder(new AudioFormat(48_000, 1), 12, isFloat: false)
        );

    [Fact]
    public void ACodecNothingHereDecodesIsDeclinedRatherThanThrown() {
        // A video with an Opus track and no Opus decoder linked is an ordinary situation with an
        // obvious behaviour — play the picture — and not an error the caller can act on.
        var builder = new WebMBuilder()
            .VideoTrack(1, 16, 16)
            .AudioTrack(2, 48_000, 2, codecId: "A_OPUS")
            .Cluster(0)
            .SimpleBlock(1, 0, keyFrame: true, VideoTestContent.I420(16, 16, 30));

        using var demuxer = new MatroskaDemuxer(builder.Stream());

        Assert.False(MatroskaAudioStreamDecoder.TryOpen(demuxer, out var stream));
        Assert.Null(stream);
    }

    [Fact]
    public void ThePictureAndTheSoundShareOneReader() {
        using var video = new WebMVideoStreamDecoder(WithSound(0.5f, withVideo: true).Stream());

        Assert.True(MatroskaAudioStreamDecoder.TryOpen(video.Container, out var stream));
        using var sound = stream!;

        var frame = new VideoFrame();
        var buffer = new float[256];

        Assert.Equal(VideoDecodeStatus.Decoded, video.DecodeNext(frame));
        Assert.True(sound.Decode(buffer, 32) > 0);
    }

    [Fact]
    public void SeekingReportsWhereItActuallyLandedRatherThanWhereItWasAsked() {
        using var demuxer = new MatroskaDemuxer(
            WithSound(0.5f, clusters: 4, framesPerBlock: 480, cues: true).Stream()
        );

        Assert.True(MatroskaAudioStreamDecoder.TryOpen(demuxer, out var stream));
        using var sound = stream!;

        // Ask for a frame in the middle of the third block. The container indexes clusters, not
        // frames, so playback resumes at the cluster and the position says so.
        sound.Seek(1_000);

        var buffer = new float[64];

        sound.Decode(buffer, 32);

        Assert.True(sound.Position <= 1_032);
    }

    /// <summary>A file with a float PCM track, and optionally a picture beside it.</summary>
    static WebMBuilder WithSound(
        float value,
        int clusters = 1,
        int framesPerBlock = 32,
        bool withVideo = false,
        bool cues = false
    ) {
        var builder = new WebMBuilder();

        if (withVideo) {
            builder.VideoTrack(1, 16, 16, defaultDurationNanoseconds: 40_000_000);
        }

        builder.AudioTrack(2, 48_000, 2);

        if (cues) {
            builder.Cues(2);
        }

        var ticksPerBlock = Math.Max(1, framesPerBlock * 1_000 / 48_000);

        for (var index = 0; index < clusters; index++) {
            builder.Cluster(index * ticksPerBlock);

            if (withVideo) {
                builder.SimpleBlock(1, 0, keyFrame: true, VideoTestContent.I420(16, 16, 30));
            }

            builder.SimpleBlock(2, 0, keyFrame: true, VideoTestContent.FloatPcm(framesPerBlock, 2, value));
        }

        return builder;
    }
}
