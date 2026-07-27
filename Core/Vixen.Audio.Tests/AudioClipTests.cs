// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core.Serialization;
using Xunit;

namespace Vixen.Audio.Tests;

public sealed class AudioClipTests {
    [Fact]
    public void ADurationIsFramesOverTheSampleRateAndNotBytesOverIt() {
        // One second of stereo 16-bit at 48 kHz is 192 000 bytes, and a clip that divided those by
        // the sample rate would report four seconds.
        var clip = new AudioClip {
            SampleRate = 48_000,
            Channels = 2,
            Format = AudioSampleFormat.Int16,
            Samples = new byte[48_000 * 2 * 2]
        };

        Assert.Equal(48_000, clip.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(1), clip.Duration);
    }

    [Fact]
    public void AClipWithNothingInItAnswersRatherThanDividingByZero() {
        var clip = new AudioClip();

        Assert.Equal(0, clip.FrameCount);
        Assert.Equal(TimeSpan.Zero, clip.Duration);
        Assert.True(clip.AsInt16().IsEmpty);
    }

    [Fact]
    public void SamplesAreReinterpretedWithoutACopy() {
        var samples = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(samples, -32_768);
        BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(2), 32_767);

        var clip = new AudioClip { SampleRate = 8_000, Channels = 1, Samples = samples };

        Assert.Equal([-32_768, 32_767], clip.AsInt16().ToArray());
    }

    /// <summary>
    ///     Empty rather than converting, so a caller that asked for the wrong format finds out where
    ///     it asked. Converting quietly would hide a clip that shipped as float when the settings
    ///     said otherwise.
    /// </summary>
    [Fact]
    public void AskingForTheOtherFormatGivesNothingRatherThanNonsense() {
        var clip = new AudioClip {
            SampleRate = 8_000,
            Channels = 1,
            Format = AudioSampleFormat.Int16,
            Samples = new byte[8]
        };

        Assert.False(clip.AsInt16().IsEmpty);
        Assert.True(clip.AsFloat32().IsEmpty);
    }

    [Fact]
    public void AClipSurvivesTheObjectDatabaseUnchanged() {
        var samples = new byte[64];

        for (var index = 0; index < samples.Length; index++) {
            samples[index] = (byte)(index * 7);
        }

        var clip = new AudioClip {
            SampleRate = 44_100,
            Channels = 2,
            Format = AudioSampleFormat.Float32,
            Samples = samples
        };

        var loaded = Serializer.Read<AudioClip>(Serializer.ToBytes(clip));

        Assert.Equal(clip.SampleRate, loaded.SampleRate);
        Assert.Equal(clip.Channels, loaded.Channels);
        Assert.Equal(clip.Format, loaded.Format);
        Assert.Equal(clip.Samples, loaded.Samples);
    }
}
