// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Streaming;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     A lossy codec is judged on what it costs and what it loses, so both are measured — and the
///     property that makes it the right one for effects, which is that a block decodes on its own.
/// </summary>
public sealed class AdpcmTests {
    const int Rate = 48_000;
    const int Block = 505;

    static float[] Tone(int frames, float hertz, float amplitude = 0.6f, int channels = 1) {
        var samples = new float[frames * channels];

        for (var i = 0; i < frames; i++) {
            var value = amplitude * MathF.Sin(2f * MathF.PI * hertz * i / Rate);

            for (var channel = 0; channel < channels; channel++) {
                // A different level per channel, so a test that crossed them would show it.
                samples[(i * channels) + channel] = value * (channel == 0 ? 1f : 0.5f);
            }
        }

        return samples;
    }

    static float Error(ReadOnlySpan<float> original, ReadOnlySpan<float> decoded) {
        var noise = 0.0;
        var signal = 0.0;

        for (var i = 0; i < original.Length && i < decoded.Length; i++) {
            var difference = original[i] - decoded[i];
            noise += difference * difference;
            signal += original[i] * (double)original[i];
        }

        return signal > 0 ? (float)Math.Sqrt(noise / signal) : 0f;
    }

    static float[] RoundTrip(float[] samples, int channels, out byte[] compressed) {
        compressed = Adpcm.Compress(samples, channels, Block);

        using var decoder = new AdpcmStreamDecoder(
            compressed,
            new AudioFormat(Rate, channels),
            Block,
            samples.Length / channels
        );

        var output = new float[samples.Length];
        var frames = decoder.Decode(output, samples.Length / channels);

        Assert.Equal(samples.Length / channels, frames);
        return output;
    }

    /// <summary>The number that justifies the format at all.</summary>
    [Fact]
    public void ItIsFourToOneAgainstSixteenBitAndEightToOneAgainstFloat() {
        var samples = Tone(48_000, 440f);
        var compressed = Adpcm.Compress(samples, 1, Block);

        var asFloat = samples.Length * sizeof(float);
        var asPcm16 = samples.Length * sizeof(short);

        Assert.True(compressed.Length < asPcm16 / 3.8, $"{compressed.Length} bytes against {asPcm16} as 16-bit PCM");
        Assert.True(compressed.Length < asFloat / 7.5, $"{compressed.Length} bytes against {asFloat} as float");
    }

    [Fact]
    public void WhatComesBackIsWhatWentIn() {
        var samples = Tone(24_000, 440f);
        var decoded = RoundTrip(samples, 1, out _);

        // Four bits a sample is about 20 dB of signal to noise on a tone, which is audible on a solo
        // pad and inaudible on a footstep under gunfire — which is the whole positioning of the
        // format.
        var error = Error(samples, decoded);
        Assert.True(error < 0.12f, $"the error came to {error:P1} of the signal");
    }

    /// <summary>Quiet material is where a fixed-step quantiser falls apart and an adaptive one does not.</summary>
    [Fact]
    public void ItAdaptsToQuietMaterialRatherThanQuantisingItToNothing() {
        var quiet = Tone(24_000, 440f, amplitude: 0.01f);
        var decoded = RoundTrip(quiet, 1, out _);

        var loudest = 0f;

        foreach (var sample in decoded) {
            loudest = MathF.Max(loudest, MathF.Abs(sample));
        }

        Assert.True(loudest > 0.005f, $"a quiet tone came back at {loudest:F5}, which is nearly silence");
        Assert.True(Error(quiet, decoded) < 0.25f, $"and its error was {Error(quiet, decoded):P1}");
    }

    [Fact]
    public void StereoChannelsStayApart() {
        var samples = Tone(12_000, 440f, channels: 2);
        var decoded = RoundTrip(samples, 2, out _);

        var left = 0f;
        var right = 0f;

        for (var i = 0; i < decoded.Length; i += 2) {
            left = MathF.Max(left, MathF.Abs(decoded[i]));
            right = MathF.Max(right, MathF.Abs(decoded[i + 1]));
        }

        // The right was authored at half the left's level and has to come back that way.
        Assert.Equal(0.5f, right / left, 0.08f);
    }

    /// <summary>
    ///     The property that makes this the format for effects: any block decodes on its own, so a
    ///     sound starts instantly and a loop point costs a division.
    /// </summary>
    [Fact]
    public void ABlockDecodesWithoutTheOnesBeforeIt() {
        var samples = Tone(24_000, 440f);
        var compressed = Adpcm.Compress(samples, 1, Block);

        using var straight = new AdpcmStreamDecoder(compressed, new AudioFormat(Rate, 1), Block, 24_000);
        using var sought = new AdpcmStreamDecoder(compressed, new AudioFormat(Rate, 1), Block, 24_000);

        // One decoded from the beginning, the other jumped straight to the middle.
        var whole = new float[24_000];
        straight.Decode(whole, 24_000);

        const int target = Block * 20;
        sought.Seek(target);

        var jumped = new float[4_096];
        Assert.Equal(4_096, sought.Decode(jumped, 4_096));
        Assert.Equal(target, sought.Position - 4_096);

        for (var i = 0; i < jumped.Length; i++) {
            Assert.Equal(whole[target + i], jumped[i], 1e-6f);
        }
    }

    /// <summary>And seeking into the middle of a block is exact too, not just to a boundary.</summary>
    [Fact]
    public void SeekingLandsWhereItWasAskedTo() {
        var samples = Tone(24_000, 440f);
        var compressed = Adpcm.Compress(samples, 1, Block);

        using var straight = new AdpcmStreamDecoder(compressed, new AudioFormat(Rate, 1), Block, 24_000);
        using var sought = new AdpcmStreamDecoder(compressed, new AudioFormat(Rate, 1), Block, 24_000);

        var whole = new float[24_000];
        straight.Decode(whole, 24_000);

        const int target = (Block * 7) + 113;
        sought.Seek(target);

        var jumped = new float[512];
        sought.Decode(jumped, 512);

        for (var i = 0; i < jumped.Length; i++) {
            Assert.Equal(whole[target + i], jumped[i], 1e-6f);
        }
    }

    [Fact]
    public void DecodingPastTheEndStops() {
        var samples = Tone(1_000, 440f);
        var compressed = Adpcm.Compress(samples, 1, Block);

        using var decoder = new AdpcmStreamDecoder(compressed, new AudioFormat(Rate, 1), Block, 1_000);

        var buffer = new float[4_096];
        Assert.Equal(1_000, decoder.Decode(buffer, 4_096));
        Assert.Equal(0, decoder.Decode(buffer, 4_096));
    }

    /// <summary>The encoder has to run the decoder, or the two drift apart within a few dozen samples.</summary>
    [Fact]
    public void TheEncoderAndTheDecoderAgreeSampleForSample() {
        var encoder = default(Adpcm.State);
        var decoder = default(Adpcm.State);

        var random = new Random(4);

        for (var i = 0; i < 4_000; i++) {
            var sample = (int)((random.NextDouble() * 2.0 - 1.0) * 20_000);
            var code = Adpcm.Encode(sample, ref encoder);
            var back = Adpcm.Decode(code, ref decoder);

            Assert.Equal(encoder.Predictor, back);
            Assert.Equal(encoder.Index, decoder.Index);
        }
    }

    [Fact]
    public void ItIsAnOrdinaryStreamDecoder() {
        var samples = Tone(5_000, 440f);
        var compressed = Adpcm.Compress(samples, 1, Block);

        using var decoder = new AdpcmStreamDecoder(compressed, new AudioFormat(Rate, 1), Block, 5_000);

        // Through the interface, which is all the streaming pump ever sees of it.
        static void AsThePumpWould(IAudioStreamDecoder seam) {
            Assert.Equal(Rate, seam.Format.SampleRate);
            Assert.Equal(1, seam.Format.Channels);
            Assert.Equal(5_000, seam.FrameCount);
            Assert.True(seam.CanSeek);
            Assert.True(seam.Decode(new float[1_024], 1_024) > 0);
        }

        AsThePumpWould(decoder);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(504)]
    public void ABlockSizeThatCannotHoldPairsIsRefused(int samplesPerBlock) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Adpcm.Compress(new float[16], 1, samplesPerBlock));

    [Fact]
    public void TheBlockArithmeticRoundTrips() {
        foreach (var channels in new[] { 1, 2 }) {
            var bytes = Adpcm.BlockBytes(Block, channels);
            Assert.Equal(Block, Adpcm.BlockFrames(bytes, channels));
        }
    }
}
