// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Xunit;

namespace Vixen.Audio.Tests;

public sealed class DynamicsTests {
    const int Rate = 48_000;

    static float[] Constant(float value, int frames) {
        var buffer = new float[frames];
        Array.Fill(buffer, value);
        return buffer;
    }

    static float[] Sine(float amplitude, float frequency, int frames) {
        var buffer = new float[frames];

        for (var i = 0; i < frames; i++) {
            buffer[i] = amplitude * MathF.Sin(2f * MathF.PI * frequency * i / Rate);
        }

        return buffer;
    }

    [Fact]
    public void ADecibelIsTwentyLogTen() {
        Assert.Equal(1f, Decibels.ToLinear(0f), 1e-6f);
        Assert.Equal(0.5f, Decibels.ToLinear(-6.0206f), 1e-4f);
        Assert.Equal(-6.0206f, Decibels.FromLinear(0.5f), 1e-3f);

        // An infinity that reaches an envelope makes every sample after it a NaN.
        Assert.Equal(-120f, Decibels.FromLinear(0f));
        Assert.True(float.IsFinite(Decibels.FromLinear(-1f)));
    }

    [Fact]
    public void SomethingBelowTheThresholdIsLeftAlone() {
        var compressor = new CompressorEffect {
            ThresholdDb = -6f,
            Ratio = 4f,
            KneeDb = 0f,
            AttackSeconds = 0f
        };

        compressor.Prepare(AudioFormat.Mono48k, 1_024);
        var buffer = Constant(0.1f, 1_024);
        compressor.Process(buffer, 1_024, 1);

        Assert.Equal(0.1f, buffer[^1], 1e-5f);
        Assert.Equal(0f, compressor.GainReductionDb, 1e-5f);
    }

    [Fact]
    public void SomethingAboveItComesDownByTheRatio() {
        var compressor = new CompressorEffect {
            ThresholdDb = -20f,
            Ratio = 4f,
            KneeDb = 0f,
            AttackSeconds = 0f
        };

        compressor.Prepare(AudioFormat.Mono48k, 1_024);
        var buffer = Constant(0.5f, 1_024);
        compressor.Process(buffer, 1_024, 1);

        // 0.5 is −6 dB, which is 14 dB over. At 4:1 that is 10.5 dB of reduction, so −16.5 dB out.
        Assert.Equal(Decibels.ToLinear(-16.5f), buffer[^1], 1e-3f);
        Assert.Equal(-10.5f, compressor.GainReductionDb, 0.1f);
    }

    [Fact]
    public void MakeupPutsTheLevelBack() {
        var compressor = new CompressorEffect {
            ThresholdDb = -20f,
            Ratio = 4f,
            KneeDb = 0f,
            AttackSeconds = 0f,
            MakeupDb = 10.5f
        };

        compressor.Prepare(AudioFormat.Mono48k, 1_024);
        var buffer = Constant(0.5f, 1_024);
        compressor.Process(buffer, 1_024, 1);

        Assert.Equal(0.5f, buffer[^1], 1e-3f);
    }

    /// <summary>
    ///     Compressing channels independently pulls a stereo image apart: a loud transient on the
    ///     left turns the left down and the sound walks to the right.
    /// </summary>
    [Fact]
    public void EveryChannelGetsTheSameGain() {
        var compressor = new CompressorEffect {
            ThresholdDb = -30f,
            Ratio = 8f,
            KneeDb = 0f,
            AttackSeconds = 0f
        };

        compressor.Prepare(AudioFormat.Stereo48k, 512);

        var buffer = new float[512 * 2];

        for (var frame = 0; frame < 512; frame++) {
            buffer[frame * 2] = 0.8f;
            buffer[(frame * 2) + 1] = 0.2f;
        }

        compressor.Process(buffer, 512, 2);

        // The ratio between the channels is what it was; only the level moved.
        Assert.Equal(4f, buffer[^2] / buffer[^1], 1e-3f);
    }

    [Fact]
    public void AttackAndReleaseTakeTheTimeTheyAreGiven() {
        var compressor = new CompressorEffect {
            ThresholdDb = -30f,
            Ratio = 10f,
            KneeDb = 0f,
            AttackSeconds = 0.05f,
            ReleaseSeconds = 0.05f
        };

        compressor.Prepare(AudioFormat.Mono48k, Rate);

        // One millisecond in, a 50 ms attack has barely started.
        var buffer = Constant(0.9f, 48);
        compressor.Process(buffer, 48, 1);
        Assert.True(compressor.GainReductionDb > -3f, $"reduction reached {compressor.GainReductionDb:F1} dB in 1 ms");

        // A quarter of a second in, it has arrived.
        var settled = Constant(0.9f, Rate / 4);
        compressor.Process(settled, Rate / 4, 1);
        Assert.True(compressor.GainReductionDb < -20f, $"reduction only reached {compressor.GainReductionDb:F1} dB");
    }

    /// <summary>
    ///     The point of the limiter: a ceiling that holds without the flat tops a clamp produces.
    /// </summary>
    [Fact]
    public void NothingGetsPastTheCeiling() {
        var limiter = new LimiterEffect();
        limiter.Prepare(AudioFormat.Mono48k, 4_096);

        var buffer = Sine(2.5f, 200f, 4_096);
        limiter.Process(buffer, 4_096, 1);

        var ceiling = Decibels.ToLinear(limiter.CeilingDb);

        Assert.True(
            AudioTestData.Peak(buffer) <= ceiling + 1e-4f,
            $"the loudest sample was {AudioTestData.Peak(buffer):F4} against a ceiling of {ceiling:F4}"
        );

        // And it used the headroom rather than just being quiet.
        Assert.True(AudioTestData.Peak(buffer.AsSpan(2_048)) > ceiling * 0.9f);
        Assert.True(limiter.GainReductionDb < -6f);
    }

    /// <summary>
    ///     A one-pole envelope only approaches the peak, so a fast enough transient escapes it. The
    ///     sliding-window maximum is what turns the ceiling from a tendency into a guarantee.
    /// </summary>
    [Fact]
    public void ATransientOutOfSilenceDoesNotEscape() {
        var limiter = new LimiterEffect();
        limiter.Prepare(AudioFormat.Mono48k, 2_048);

        var buffer = new float[2_048];
        buffer[1_000] = 4f;
        limiter.Process(buffer, 2_048, 1);

        Assert.True(
            AudioTestData.Peak(buffer) <= Decibels.ToLinear(limiter.CeilingDb) + 1e-4f,
            $"a lone spike came out at {AudioTestData.Peak(buffer):F4}"
        );
    }

    [Fact]
    public void SomethingAlreadyUnderTheCeilingIsUntouchedApartFromTheDelay() {
        var limiter = new LimiterEffect();
        limiter.Prepare(AudioFormat.Mono48k, 2_048);

        var buffer = Sine(0.5f, 100f, 2_048);
        var expected = (float[])buffer.Clone();
        limiter.Process(buffer, 2_048, 1);

        Assert.Equal(0f, limiter.GainReductionDb, 1e-5f);

        // The whole point of look-ahead is that it costs latency, and this is that latency.
        var latency = limiter.LatencyFrames;
        Assert.True(latency > 0);

        for (var i = latency; i < 2_048; i++) {
            Assert.Equal(expected[i - latency], buffer[i], 1e-5f);
        }
    }

    [Fact]
    public void TheLimiterIsOnTheMasterUnlessItIsTurnedOff() {
        var (with, _) = AudioTestData.Engine(limiter: true);
        using var a = with;
        var (without, __) = AudioTestData.Engine();
        using var b = without;

        Assert.NotNull(with.Limiter);
        Assert.Contains(with.Limiter!, with.Master.Effects);
        Assert.Null(without.Limiter);
        Assert.Empty(without.Master.Effects);
    }

    /// <summary>
    ///     The clamp is still there behind the limiter, demoted to what it should always have been —
    ///     a guard, not a level control.
    /// </summary>
    [Fact]
    public void ALoudSceneIsLimitedRatherThanClipped() {
        var (limited, limitedDevice) = AudioTestData.Engine(channels: 1, voices: 8, limiter: true);
        using var a = limited;
        var (clipped, clippedDevice) = AudioTestData.Engine(channels: 1, voices: 8);
        using var b = clipped;

        for (var i = 0; i < 6; i++) {
            var settings = new PlaybackSettings { Gain = 1f, Pitch = 1f };
            limited.Play(AudioTestData.Constant(48_000, 0.8f), settings);
            clipped.Play(AudioTestData.Constant(48_000, 0.8f), settings);
        }

        AudioTestData.Render(limitedDevice, 512);
        AudioTestData.Render(clippedDevice, 512);

        var limitedPeak = AudioTestData.Peak(AudioTestData.Render(limitedDevice, 512));
        var clippedPeak = AudioTestData.Peak(AudioTestData.Render(clippedDevice, 512));

        Assert.Equal(1f, clippedPeak, 1e-4f);
        Assert.True(limitedPeak < 1f, $"the limited master reached {limitedPeak:F4}");
        Assert.Equal(Decibels.ToLinear(-0.3f), limitedPeak, 1e-3f);
    }

    /// <summary>A NaN out of a misbehaving effect must not reach a driver.</summary>
    [Fact]
    public void ANotANumberIsCaughtAtTheMaster() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        engine.Master.AddEffect(new NaughtyEffect());
        engine.Play(AudioTestData.Constant(4_800, 0.5f));

        var rendered = AudioTestData.Render(device, 64);

        Assert.All(rendered, value => Assert.True(float.IsFinite(value)));
    }

    sealed class NaughtyEffect : IAudioEffect {
        public bool Enabled { get; set; } = true;

        public void Prepare(in AudioFormat format, int maxFrames) { }

        public void Process(Span<float> buffer, int frameCount, int channels) => buffer.Fill(float.NaN);

        public void Reset() { }
    }
}
