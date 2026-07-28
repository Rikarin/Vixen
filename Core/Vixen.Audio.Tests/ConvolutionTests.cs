// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     A convolution reverb has one job and it is exactly specified: the output must be the input
///     convolved with the impulse response. So it is checked against the direct sum, which is
///     unarguable and far too slow to ship.
/// </summary>
public sealed class ConvolutionTests {
    const int Rate = 48_000;

    static AudioClip Response(params float[] samples) => AudioTestData.FromFloats(samples, Rate, 1);

    /// <summary>The answer, computed the way nobody can afford to.</summary>
    static float[] Directly(ReadOnlySpan<float> input, ReadOnlySpan<float> impulse) {
        var output = new float[input.Length];

        for (var i = 0; i < input.Length; i++) {
            for (var k = 0; k < impulse.Length && i - k >= 0; k++) {
                output[i] += input[i - k] * impulse[k];
            }
        }

        return output;
    }

    [Fact]
    public void ConvolvingWithASingleSpikeGivesBackTheSignalDelayedByNothing() {
        var effect = new ConvolutionReverbEffect(Response(1f)) { Wet = 1f, Dry = 0f };
        effect.Prepare(AudioFormat.Mono48k, 256);

        var input = new float[2_048];

        for (var i = 0; i < input.Length; i++) {
            input[i] = MathF.Sin(i * 0.1f);
        }

        var buffer = (float[])input.Clone();
        effect.Process(buffer, buffer.Length, 1);

        var latency = effect.LatencyFrames;

        for (var i = 0; i + latency < input.Length; i++) {
            Assert.Equal(input[i], buffer[i + latency], 1e-4f);
        }
    }

    /// <summary>
    ///     The claim the whole effect rests on. If this holds for an arbitrary response, the
    ///     partitioning, the delay line and the overlap-add are all correct.
    /// </summary>
    [Fact]
    public void ItMatchesTheDirectConvolution() {
        var impulse = new float[700];
        var random = 12_345;

        for (var i = 0; i < impulse.Length; i++) {
            // A deterministic pseudo-random tail: no symmetry for a mistake to hide behind, and the
            // same numbers on every machine.
            random = (random * 1_103_515_245) + 12_345;
            impulse[i] = ((random >> 16 & 0x7FFF) / 16_384f - 1f) * MathF.Exp(-i / 200f);
        }

        var input = new float[3_000];

        for (var i = 0; i < input.Length; i++) {
            input[i] = MathF.Sin(i * 0.07f) * (i < 1_500 ? 1f : 0.2f);
        }

        var expected = Directly(input, impulse);

        var effect = new ConvolutionReverbEffect(Response(impulse)) { Wet = 1f, Dry = 0f };
        effect.Prepare(AudioFormat.Mono48k, 256);

        var buffer = (float[])input.Clone();

        // Processed in uneven chunks, because the caller's block size need not be the effect's and
        // an effect that only worked at one is an effect that breaks on a different device.
        var offset = 0;

        foreach (var chunk in new[] { 100, 256, 33, 500, 1_000, 1_111 }) {
            effect.Process(buffer.AsSpan(offset, chunk), chunk, 1);
            offset += chunk;
        }

        var latency = effect.LatencyFrames;

        for (var i = 0; i + latency < offset; i++) {
            Assert.Equal(expected[i], buffer[i + latency], 1e-3f);
        }
    }

    [Fact]
    public void ItReportsWhatThePartitioningCost() {
        var effect = new ConvolutionReverbEffect(Response(new float[Rate])) { Wet = 1f, Dry = 0f };
        effect.Prepare(AudioFormat.Mono48k, 480);

        // A block of 480 rounds up to a partition of 512, and a second of response is 94 of them.
        Assert.Equal(512, effect.LatencyFrames);
        Assert.Equal(94, effect.PartitionCount);
        Assert.Equal(Rate, effect.ResponseFrames);
    }

    /// <summary>
    ///     A response at the wrong rate is a room of the wrong size and colour. Resampling it here
    ///     would hide that; the fix belongs in the content build, where it is paid for once.
    /// </summary>
    [Fact]
    public void AResponseAtTheWrongRateSaysSo() {
        var response = AudioTestData.FromFloats(new float[1_000], 44_100, 1);
        var effect = new ConvolutionReverbEffect(response);

        effect.Prepare(AudioFormat.Mono48k, 256);
        Assert.False(effect.IsRateMatched);

        effect.Prepare(new AudioFormat(44_100, 1), 256);
        Assert.True(effect.IsRateMatched);
    }

    [Fact]
    public void AMonoResponseIsAppliedToEveryChannel() {
        var effect = new ConvolutionReverbEffect(Response(0f, 0f, 1f)) { Wet = 1f, Dry = 0f };
        effect.Prepare(AudioFormat.Stereo48k, 128);

        var buffer = new float[1_024 * 2];
        buffer[0] = 1f;
        buffer[1] = 0.5f;
        effect.Process(buffer, 1_024, 2);

        var latency = effect.LatencyFrames;

        Assert.Equal(1f, buffer[(latency + 2) * 2], 1e-4f);
        Assert.Equal(0.5f, buffer[((latency + 2) * 2) + 1], 1e-4f);
    }

    [Fact]
    public void AnEmptyResponseIsRefused() {
        Assert.Throws<ArgumentException>(() => new ConvolutionReverbEffect(new AudioClip()));
        Assert.Throws<ArgumentNullException>(() => new ConvolutionReverbEffect(null!));
    }

    [Fact]
    public void ResettingThrowsAwayTheTail() {
        var effect = new ConvolutionReverbEffect(Response(new float[2_000])) { Wet = 1f, Dry = 0f };
        effect.Prepare(AudioFormat.Mono48k, 256);

        var buffer = new float[4_096];
        buffer[0] = 1f;
        effect.Process(buffer, buffer.Length, 1);

        effect.Reset();
        Array.Clear(buffer);
        effect.Process(buffer, buffer.Length, 1);

        Assert.Equal(0f, AudioTestData.Peak(buffer));
    }

    [Fact]
    public void ItWorksAsABusEffect() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var room = engine.CreateBus("Room");
        var impulse = new float[4_000];
        impulse[0] = 1f;
        impulse[2_000] = 0.5f;
        room.AddEffect(new ConvolutionReverbEffect(Response(impulse)) { Wet = 1f, Dry = 0f });

        engine.Play(AudioTestData.Constant(64, 0.5f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = room.Index
        });

        // The rendered buffer rather than PeakLevel: the peak is a per-block figure and Render walks
        // several blocks per call, so sampling it afterwards reports the last one — which for a
        // 64-frame sound in a 4 000-frame room is usually silence.
        var first = 0f;
        var echo = 0f;

        for (var block = 0; block < 40; block++) {
            var loudest = AudioTestData.Peak(AudioTestData.Render(device, 256));

            if (block < 4) {
                first = MathF.Max(first, loudest);
                continue;
            }

            echo = MathF.Max(echo, loudest);
        }

        // The response is a spike at 0 and a half-height one 2 000 frames later, so the room gives
        // back the sound and then gives back half of it again.
        Assert.Equal(0.5f, first, 0.01f);
        Assert.Equal(0.25f, echo, 0.01f);
    }
}

public sealed class SpectrumAnalyzerTests {
    const int Rate = 48_000;

    static float[] Sine(float frequency, int frames, float amplitude = 1f) {
        var buffer = new float[frames];

        for (var i = 0; i < frames; i++) {
            buffer[i] = amplitude * MathF.Sin(2f * MathF.PI * frequency * i / Rate);
        }

        return buffer;
    }

    [Fact]
    public void ItLeavesTheSignalAlone() {
        var analyzer = new SpectrumAnalyzerEffect(256);
        analyzer.Prepare(AudioFormat.Mono48k, 512);

        var buffer = Sine(1_000f, 512);
        var expected = (float[])buffer.Clone();
        analyzer.Process(buffer, 512, 1);

        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void AToneShowsUpInItsOwnBin() {
        var analyzer = new SpectrumAnalyzerEffect(1_024) { Smoothing = 0f };
        analyzer.Prepare(AudioFormat.Mono48k, 4_096);

        // Exactly on bin 64 of 1 024 at 48 kHz, so the window has nothing to smear.
        var frequency = 64f * Rate / 1_024f;
        analyzer.Process(Sine(frequency, 4_096, 0.5f), 4_096, 1);

        var magnitudes = new float[analyzer.BinCount];
        Assert.True(analyzer.TryCopyTo(magnitudes));

        var loudest = 0;

        for (var bin = 1; bin < magnitudes.Length; bin++) {
            if (magnitudes[bin] > magnitudes[loudest]) {
                loudest = bin;
            }
        }

        Assert.Equal(64, loudest);

        // And the amplitude comes back out, which is what the window and mirror corrections are for.
        Assert.Equal(0.5f, magnitudes[64], 0.05f);
    }

    [Fact]
    public void ItReportsWhereTheBinsAre() {
        var analyzer = new SpectrumAnalyzerEffect(1_024);
        analyzer.Prepare(AudioFormat.Mono48k, 512);

        Assert.Equal(1_024, analyzer.Size);
        Assert.Equal(513, analyzer.BinCount);
        Assert.Equal(46.875f, analyzer.BinWidthHz, 1e-3f);
    }

    [Fact]
    public void SilenceReadsAsNothing() {
        var analyzer = new SpectrumAnalyzerEffect(256) { Smoothing = 0f };
        analyzer.Prepare(AudioFormat.Mono48k, 1_024);
        analyzer.Process(new float[1_024], 1_024, 1);

        var magnitudes = new float[analyzer.BinCount];
        Assert.True(analyzer.TryCopyTo(magnitudes));
        Assert.All(magnitudes, value => Assert.Equal(0f, value, 1e-6f));
    }

    /// <summary>
    ///     A visualiser driven by raw transforms flickers, because consecutive blocks of real music
    ///     genuinely differ that much.
    /// </summary>
    [Fact]
    public void SmoothingSlowsThePictureDown() {
        var analyzer = new SpectrumAnalyzerEffect(256) { Smoothing = 0.9f };
        analyzer.Prepare(AudioFormat.Mono48k, 256);

        var magnitudes = new float[analyzer.BinCount];
        analyzer.Process(Sine(3_000f, 256), 256, 1);
        analyzer.TryCopyTo(magnitudes);
        var first = AudioTestData.Peak(magnitudes);

        for (var block = 0; block < 20; block++) {
            analyzer.Process(Sine(3_000f, 256), 256, 1);
        }

        analyzer.TryCopyTo(magnitudes);
        var settled = AudioTestData.Peak(magnitudes);

        // It only got a tenth of the way there on the first block, and is most of the way after
        // twenty.
        Assert.True(settled > first * 3f, $"first {first:F4}, settled {settled:F4}");
    }

    [Fact]
    public void ADestinationThatIsTooSmallIsRefusedRatherThanOverrun() {
        var analyzer = new SpectrumAnalyzerEffect(256);
        analyzer.Prepare(AudioFormat.Mono48k, 256);

        Assert.False(analyzer.TryCopyTo(new float[4]));
    }

    [Fact]
    public void ItWorksAsABusEffect() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var analyzer = new SpectrumAnalyzerEffect(512) { Smoothing = 0f };
        engine.Master.AddEffect(analyzer);

        engine.Play(AudioTestData.Tone(2_000f, 48_000, 0.5f), new PlaybackSettings { Gain = 1f, Pitch = 1f });
        AudioTestData.Render(device, 4_096);

        var magnitudes = new float[analyzer.BinCount];
        Assert.True(analyzer.TryCopyTo(magnitudes));

        var loudest = 0;

        for (var bin = 1; bin < magnitudes.Length; bin++) {
            if (magnitudes[bin] > magnitudes[loudest]) {
                loudest = bin;
            }
        }

        Assert.Equal(2_000f, loudest * analyzer.BinWidthHz, analyzer.BinWidthHz);
    }
}
