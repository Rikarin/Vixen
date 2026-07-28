// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Audio.Codecs;
using Vixen.Video.Audio;
using Vixen.Video.Containers;
using Vixen.Video.Tests;
using Xunit;

namespace Vixen.Video.Codecs.Tests;

/// <summary>A WebM's Opus track, encoded and decoded by the two halves of this repository.</summary>
/// <remarks>
///     The tone is encoded with <c>OpusPacketEncoder</c>, muxed with the tests' own writer, and read
///     back through the demuxer, the registry and the adapter — so what is asserted is the whole
///     path a real file takes, with only the codec itself shared between the two ends. Opus is lossy,
///     so nothing here compares samples: what a round trip through it can honestly assert is that the
///     sound arrives, at the right rate, at roughly the right level, and at the right moment.
/// </remarks>
public sealed class OpusAudioTrackTests {
    const int Rate = 48_000;
    const int PacketFrames = 960;     // 20 ms, which is what every muxer writes
    const int Packets = 50;           // one second of it

    public OpusAudioTrackTests() => VideoAudioCodecs.RegisterOpus();

    [Fact]
    public void RegisteringIsIdempotent() {
        VideoAudioCodecs.RegisterOpus();
        VideoAudioCodecs.RegisterOpus();

        Assert.Single(AudioPacketDecoderRegistry.RegisteredNames(), name => name == "opus");
    }

    [Fact]
    public void AnOpusTrackOpensAndSounds() {
        using var demuxer = new MatroskaDemuxer(Tone().Stream());

        Assert.True(MatroskaAudioStreamDecoder.TryOpen(demuxer, out var stream));
        using var sound = stream!;

        // Opus decodes at 48 kHz whatever the track's SamplingFrequency says, because that element
        // describes what went into the encoder.
        Assert.Equal(Rate, sound.Format.SampleRate);
        Assert.Equal(1, sound.Format.Channels);

        var buffer = new float[Rate];
        var total = 0;
        var peak = 0f;

        while (total < buffer.Length) {
            var frames = sound.Decode(buffer.AsSpan(total), Math.Min(4_096, buffer.Length - total));

            if (frames == 0) {
                break;
            }

            for (var index = total; index < total + frames; index++) {
                peak = Math.Max(peak, Math.Abs(buffer[index]));
            }

            total += frames;
        }

        // Most of a second: the tail of the last packet is where a muxer and a decoder disagree by a
        // frame or two, and asserting the exact count would be asserting Concentus's rounding.
        Assert.InRange(total, Rate - PacketFrames, Rate);

        // The tone was written at half scale. Opus at 64 kbit/s keeps that to well within a factor
        // of two, and silence — which is what a broken pre-skip or a mis-parsed packet gives — is
        // nowhere near it.
        Assert.InRange(peak, 0.2f, 0.9f);
    }

    [Fact]
    public void ThePrimingSamplesAreNotPlayed() {
        // Every Opus stream begins with samples the encoder needed and the listener must not hear. A
        // decoder that plays them starts every track with a few milliseconds of artefact — and, more
        // measurably, with more frames than the file claims to hold.
        const int preSkip = 312;

        using var demuxer = new MatroskaDemuxer(Tone(preSkip).Stream());

        Assert.True(MatroskaAudioStreamDecoder.TryOpen(demuxer, out var stream));
        using var sound = stream!;

        var buffer = new float[Rate * 2];
        var total = 0;

        while (true) {
            var frames = sound.Decode(buffer.AsSpan(total), Math.Min(4_096, buffer.Length - total));

            if (frames == 0) {
                break;
            }

            total += frames;
        }

        Assert.Equal((Packets * PacketFrames) - preSkip, total);
    }

    [Fact]
    public void TheContainersDelayWinsOverTheCodecsOwnHeader() {
        // A remux that trimmed the start updates CodecDelay and cannot update the codec header,
        // which is passed through untouched. Believing the header there would clip the start.
        var track = new AudioTrackInfo(
            "A_OPUS",
            Rate,
            1,
            CodecPrivate: OpusHead(1, 312),
            CodecDelay: TimeSpan.FromSeconds(600d / Rate)
        );

        Assert.Equal(600, OpusAudioPacketDecoder.PreSkipOf(in track));
    }

    [Fact]
    public void TheCodecsHeaderIsUsedWhenTheContainerSaysNothing() {
        var track = new AudioTrackInfo("A_OPUS", Rate, 1, CodecPrivate: OpusHead(1, 312));

        Assert.Equal(312, OpusAudioPacketDecoder.PreSkipOf(in track));
    }

    [Fact]
    public void AHeaderThatIsNotAnOpusHeadIsNoPreSkipRatherThanAnError() {
        // A track whose CodecPrivate is missing or damaged still plays; it simply starts with the
        // priming samples audible, which is better than refusing the file.
        var track = new AudioTrackInfo("A_OPUS", Rate, 1, CodecPrivate: new byte[19]);

        Assert.Equal(0, OpusAudioPacketDecoder.PreSkipOf(in track));
    }

    [Fact]
    public void MoreChannelsThanConcentusHasAreRefusedWithTheReason() {
        var track = new AudioTrackInfo("A_OPUS", Rate, 6, CodecPrivate: OpusHead(6, 312));

        var thrown = Assert.Throws<NotSupportedException>(() => new OpusAudioPacketDecoder(in track));

        Assert.Contains("6 channels", thrown.Message, StringComparison.Ordinal);
        Assert.False(new OpusAudioPacketDecoderFactory().CanDecode(in track));
    }

    [Fact]
    public void ThePictureAndTheSoundComeOutOfOneReader() {
        using var video = new WebMVideoStreamDecoder(ToneWithPicture().Stream());

        Assert.True(MatroskaAudioStreamDecoder.TryOpen(video.Container, out var stream));
        using var sound = stream!;

        var frame = new VideoFrame();
        var buffer = new float[4_096];

        Assert.Equal(VideoDecodeStatus.Decoded, video.DecodeNext(frame));
        Assert.True(sound.Decode(buffer, 960) > 0);
    }

    /// <summary>A WebM with one Opus track holding a second of a 440 Hz tone.</summary>
    static WebMBuilder Tone(int preSkip = 0) => Build(preSkip, withPicture: false);

    static WebMBuilder ToneWithPicture() => Build(0, withPicture: true);

    static WebMBuilder Build(int preSkip, bool withPicture) {
        var builder = new WebMBuilder();

        if (withPicture) {
            builder.VideoTrack(1, 16, 16, defaultDurationNanoseconds: 40_000_000);
        }

        builder.AudioTrack(
            2,
            Rate,
            1,
            bitDepth: 0,
            codecId: "A_OPUS",
            codecPrivate: OpusHead(1, preSkip),
            codecDelayNanoseconds: preSkip * 1_000_000_000L / Rate,
            seekPreRollNanoseconds: 80_000_000
        );

        using var encoder = new OpusPacketEncoder(channels: 1, frameMilliseconds: 20, bitrate: 64_000);

        var pcm = new float[PacketFrames];
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];

        for (var index = 0; index < Packets; index++) {
            for (var frame = 0; frame < PacketFrames; frame++) {
                var time = ((index * PacketFrames) + frame) / (float)Rate;

                pcm[frame] = 0.5f * MathF.Sin(2f * MathF.PI * 440f * time);
            }

            var written = encoder.Encode(pcm, packet);

            builder.Cluster(index * 20);

            if (withPicture) {
                builder.SimpleBlock(1, 0, keyFrame: true, VideoTestContent.I420(16, 16, 30));
            }

            builder.SimpleBlock(2, 0, keyFrame: true, packet[..written]);
        }

        return builder;
    }

    /// <summary>The nineteen bytes an Opus track's CodecPrivate is.</summary>
    static byte[] OpusHead(int channels, int preSkip) {
        var header = new byte[19];

        "OpusHead"u8.CopyTo(header);
        header[8] = 1;
        header[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), Rate);

        return header;
    }
}
