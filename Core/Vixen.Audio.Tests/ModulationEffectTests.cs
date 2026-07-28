// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     Modulation effects are notoriously hard to test, because "does it swirl" is not a number. What
///     <em>is</em> a number: whether the output changes over time when the input does not, whether the
///     channels differ, and whether the thing stays bounded.
/// </summary>
public sealed class ModulationEffectTests {
    const int Rate = 48_000;

    static float[] Sine(float frequency, int frames, int channels = 1, float amplitude = 0.5f) {
        var buffer = new float[frames * channels];

        for (var frame = 0; frame < frames; frame++) {
            var value = amplitude * MathF.Sin(2f * MathF.PI * frequency * frame / Rate);

            for (var channel = 0; channel < channels; channel++) {
                buffer[(frame * channels) + channel] = value;
            }
        }

        return buffer;
    }

    /// <summary>The envelope of a stretch of samples, which is what a sweep moves.</summary>
    static float Level(ReadOnlySpan<float> buffer, int from, int count, int channels = 1, int channel = 0) {
        var loudest = 0f;

        for (var frame = from; frame < from + count; frame++) {
            loudest = MathF.Max(loudest, MathF.Abs(buffer[(frame * channels) + channel]));
        }

        return loudest;
    }

    [Fact]
    public void AFlangerMovesTheSignalEvenThoughTheInputIsSteady() {
        var flanger = ModulatedDelayEffect.Flanger();
        flanger.RateHz = 2f;
        flanger.Prepare(AudioFormat.Mono48k, Rate);

        var buffer = Sine(1_000f, Rate);
        flanger.Process(buffer, Rate, 1);

        // A steady tone through a swept comb comes out with a level that wanders, because the notch
        // is passing over it. Sample the envelope at four points across one cycle.
        var levels = new[] {
            Level(buffer, 8_000, 2_000),
            Level(buffer, 14_000, 2_000),
            Level(buffer, 20_000, 2_000),
            Level(buffer, 26_000, 2_000)
        };

        Assert.True(levels.Max() > levels.Min() * 1.2f, $"the level barely moved: {string.Join(", ", levels)}");
        Assert.All(buffer, value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public void AChorusPutsSomethingDifferentInEachChannel() {
        var chorus = ModulatedDelayEffect.Chorus();
        chorus.Prepare(AudioFormat.Stereo48k, Rate);

        var buffer = Sine(440f, Rate, channels: 2);
        chorus.Process(buffer, Rate, 2);

        var difference = 0f;

        for (var frame = 20_000; frame < 30_000; frame++) {
            difference = MathF.Max(difference, MathF.Abs(buffer[frame * 2] - buffer[(frame * 2) + 1]));
        }

        // The input was identical in both channels; the sweep phase was not.
        Assert.True(difference > 0.01f, $"the channels differ by only {difference:F4}");
    }

    [Fact]
    public void TheChannelsAreIdenticalWhenTheSpreadIsZero() {
        var chorus = ModulatedDelayEffect.Chorus();
        chorus.StereoSpread = 0f;
        chorus.Prepare(AudioFormat.Stereo48k, 4_096);

        var buffer = Sine(440f, 4_096, channels: 2);
        chorus.Process(buffer, 4_096, 2);

        for (var frame = 2_000; frame < 2_100; frame++) {
            Assert.Equal(buffer[frame * 2], buffer[(frame * 2) + 1], 1e-5f);
        }
    }

    [Fact]
    public void AVibratoKeepsNoneOfTheDrySignal() {
        var vibrato = ModulatedDelayEffect.Vibrato();

        Assert.Equal(0f, vibrato.Dry);
        Assert.Equal(1f, vibrato.Wet);
        Assert.Equal(ModulatedDelayKind.Vibrato, vibrato.Kind);
    }

    /// <summary>
    ///     At a feedback of one a flanger's resonance never decays, and the clamp is what stops a
    ///     preset with a typo in it becoming the loudest thing in the game.
    /// </summary>
    [Fact]
    public void AFlangersFeedbackCannotRunAway() {
        var flanger = ModulatedDelayEffect.Flanger();
        flanger.Feedback = 4f;
        flanger.Prepare(AudioFormat.Mono48k, Rate);

        var buffer = Sine(1_000f, Rate);
        flanger.Process(buffer, Rate, 1);

        Assert.All(buffer, value => Assert.True(float.IsFinite(value)));
        Assert.True(AudioTestData.Peak(buffer) < 50f, $"it reached {AudioTestData.Peak(buffer):F1}");
    }

    [Fact]
    public void AModulatedDelayWithNoDepthIsAPlainDelay() {
        var effect = new ModulatedDelayEffect {
            DelaySeconds = 0.01f,
            DepthSeconds = 0f,
            Feedback = 0f,
            Voices = 1,
            Wet = 1f,
            Dry = 0f
        };

        effect.Prepare(AudioFormat.Mono48k, 4_096);

        var buffer = new float[4_096];
        buffer[0] = 1f;
        effect.Process(buffer, 4_096, 1);

        // 10 ms at 48 kHz is 480 samples, and with no sweep it stays there.
        Assert.Equal(1f, buffer[480], 1e-3f);
        Assert.Equal(0f, buffer[100], 1e-4f);
    }

    /// <summary>
    ///     A phaser's notches are unrelated to each other, where a flanger's are harmonics of one
    ///     frequency. That is the difference between a swirl and a jet, and it is why both exist.
    /// </summary>
    [Fact]
    public void APhaserPutsNotchesInTheSpectrumThatMove() {
        var phaser = new PhaserEffect { RateHz = 2f, Stages = 6, Feedback = 0.7f };
        phaser.Prepare(AudioFormat.Mono48k, Rate);

        var buffer = Sine(800f, Rate);
        phaser.Process(buffer, Rate, 1);

        var levels = new[] {
            Level(buffer, 8_000, 2_000),
            Level(buffer, 14_000, 2_000),
            Level(buffer, 20_000, 2_000),
            Level(buffer, 26_000, 2_000)
        };

        Assert.True(levels.Max() > levels.Min() * 1.2f, $"the level barely moved: {string.Join(", ", levels)}");
        Assert.All(buffer, value => Assert.True(float.IsFinite(value)));
    }

    /// <summary>
    ///     An all-pass filter changes nothing you can hear on its own — its magnitude response is
    ///     flat, and all it does is delay some frequencies more than others. Only adding it back to
    ///     the dry signal turns that into cancellation.
    /// </summary>
    [Fact]
    public void ThePhaserPassesEverythingWhenNoneOfTheDrySignalIsKept() {
        var phaser = new PhaserEffect {
            RateHz = 0f,
            Stages = 4,
            Feedback = 0f,
            Wet = 1f,
            Dry = 0f
        };

        phaser.Prepare(AudioFormat.Mono48k, 8_192);

        var low = Amplitude(200f);
        var high = Amplitude(5_000f);

        Assert.Equal(0.5f, low, 0.02f);
        Assert.Equal(0.5f, high, 0.02f);

        float Amplitude(float frequency) {
            phaser.Reset();
            var buffer = Sine(frequency, 8_192);
            phaser.Process(buffer, 8_192, 1);
            return Level(buffer, 6_000, 2_000);
        }
    }

    [Fact]
    public void ADisabledModulationChangesNothing() {
        var chorus = ModulatedDelayEffect.Chorus();
        chorus.Enabled = false;
        chorus.Prepare(AudioFormat.Mono48k, 512);

        var buffer = Sine(440f, 512);
        var expected = (float[])buffer.Clone();
        chorus.Process(buffer, 512, 1);

        Assert.Equal(expected, buffer);
    }
}
