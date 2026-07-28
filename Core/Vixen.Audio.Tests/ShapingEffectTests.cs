// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Distortion, bitcrushing and pitch shifting — the three that change the waveform itself.</summary>
public sealed class ShapingEffectTests {
    const int Rate = 48_000;

    static float[] Sine(float frequency, int frames, float amplitude = 0.5f) {
        var buffer = new float[frames];

        for (var i = 0; i < frames; i++) {
            buffer[i] = amplitude * MathF.Sin(2f * MathF.PI * frequency * i / Rate);
        }

        return buffer;
    }

    [Fact]
    public void EveryCurveLeavesZeroAloneAndIsOddlySymmetric() {
        foreach (var curve in Enum.GetValues<DistortionCurve>()) {
            Assert.Equal(0f, DistortionEffect.Shape(0f, curve), 1e-6f);

            // Every one of these is an odd function: bending the negative half differently from the
            // positive half is a DC offset, which is a thump when the effect switches on.
            Assert.Equal(
                -DistortionEffect.Shape(0.7f, curve),
                DistortionEffect.Shape(-0.7f, curve),
                1e-5f
            );
        }
    }

    [Fact]
    public void HardClipIsFlatPastTheRailAndOverdriveArrivesThereSmoothly() {
        Assert.Equal(1f, DistortionEffect.Shape(4f, DistortionCurve.HardClip));
        Assert.Equal(0.5f, DistortionEffect.Shape(0.5f, DistortionCurve.HardClip));

        // 1.5x − 0.5x³ at x = 1 is exactly 1, and its slope there is exactly 0 — which is what
        // "arrives smoothly" means and what makes it sound softer than a clamp.
        Assert.Equal(1f, DistortionEffect.Shape(1f, DistortionCurve.Overdrive), 1e-6f);
        Assert.Equal(1f, DistortionEffect.Shape(9f, DistortionCurve.Overdrive), 1e-6f);
    }

    /// <summary>
    ///     A tangent never reaches its asymptote, which is why it never quite squares off — though
    ///     past about an input of 5 the difference stops being representable in a float and it does.
    /// </summary>
    [Fact]
    public void SoftClipApproachesTheRailWithoutReachingIt() {
        Assert.True(DistortionEffect.Shape(3f, DistortionCurve.SoftClip) < 1f);
        Assert.True(DistortionEffect.Shape(3f, DistortionCurve.SoftClip) > 0.99f);
        Assert.True(DistortionEffect.Shape(0.5f, DistortionCurve.SoftClip) < 0.5f);
    }

    /// <summary>
    ///     Folding sounds nothing like clipping: past the rail the waveform comes back down instead of
    ///     flattening, so a loud input gets quieter rather than louder.
    /// </summary>
    [Fact]
    public void FoldbackReflectsInsteadOfFlattening() {
        Assert.Equal(1f, DistortionEffect.Shape(1f, DistortionCurve.Foldback), 1e-6f);
        Assert.Equal(0.5f, DistortionEffect.Shape(1.5f, DistortionCurve.Foldback), 1e-6f);
        Assert.Equal(0f, DistortionEffect.Shape(2f, DistortionCurve.Foldback), 1e-6f);
        Assert.Equal(-0.5f, DistortionEffect.Shape(2.5f, DistortionCurve.Foldback), 1e-6f);
        Assert.True(MathF.Abs(DistortionEffect.Shape(37.3f, DistortionCurve.Foldback)) <= 1f);
    }

    [Fact]
    public void DriveIsWhatDecidesHowMuchDistortionThereIs() {
        var quiet = Harmonics(new DistortionEffect { DriveDb = 0f, OutputDb = 0f, Curve = DistortionCurve.SoftClip });
        var loud = Harmonics(new DistortionEffect { DriveDb = 30f, OutputDb = 0f, Curve = DistortionCurve.SoftClip });

        Assert.True(loud > quiet * 5f, $"quiet {quiet:F4}, loud {loud:F4}");

        // How far a sine has been bent out of shape, measured as how much it differs from the
        // best-fitting sine of its own amplitude.
        static float Harmonics(DistortionEffect effect) {
            effect.Prepare(AudioFormat.Mono48k, 4_096);
            var buffer = Sine(1_000f, 4_096, 0.3f);
            var original = (float[])buffer.Clone();
            effect.Process(buffer, 4_096, 1);

            var scale = AudioTestData.Peak(original) / MathF.Max(AudioTestData.Peak(buffer), 1e-9f);
            var error = 0f;

            for (var i = 0; i < buffer.Length; i++) {
                error = MathF.Max(error, MathF.Abs((buffer[i] * scale) - original[i]));
            }

            return error;
        }
    }

    [Fact]
    public void AMixBelowOneKeepsSomeOfTheOriginal() {
        var effect = new DistortionEffect {
            Curve = DistortionCurve.HardClip,
            DriveDb = 0f,
            OutputDb = 0f,
            Mix = 0f
        };

        effect.Prepare(AudioFormat.Mono48k, 512);
        var buffer = Sine(1_000f, 512, 4f);
        var expected = (float[])buffer.Clone();
        effect.Process(buffer, 512, 1);

        Assert.Equal(expected, buffer);
    }

    /// <summary>
    ///     Rounding every sample to one of a handful of levels is the whole of a bit crusher, and at
    ///     one bit there are two of them.
    /// </summary>
    [Fact]
    public void TheBitCrusherQuantisesToTheLevelsItWasGiven() {
        var crusher = new BitCrusherEffect { Bits = 2f, Downsample = 1f };
        crusher.Prepare(AudioFormat.Mono48k, 1_024);

        var buffer = Sine(300f, 1_024, 0.9f);
        crusher.Process(buffer, 1_024, 1);

        // Two bits leaves two levels either side of zero, so every sample is a multiple of 0.5.
        foreach (var value in buffer) {
            Assert.Equal(0f, MathF.IEEERemainder(value, 0.5f), 1e-5f);
        }
    }

    [Fact]
    public void SixteenBitsIsEffectivelyTransparent() {
        var crusher = new BitCrusherEffect { Bits = 16f, Downsample = 1f };
        crusher.Prepare(AudioFormat.Mono48k, 1_024);

        var buffer = Sine(300f, 1_024);
        var expected = (float[])buffer.Clone();
        crusher.Process(buffer, 1_024, 1);

        for (var i = 0; i < buffer.Length; i++) {
            Assert.Equal(expected[i], buffer[i], 1e-4f);
        }
    }

    [Fact]
    public void DownsamplingHoldsEachSampleForSeveralOutputs() {
        var crusher = new BitCrusherEffect { Bits = 24f, Downsample = 4f };
        crusher.Prepare(AudioFormat.Mono48k, 64);

        var buffer = new float[64];

        for (var i = 0; i < 64; i++) {
            buffer[i] = i / 64f;
        }

        crusher.Process(buffer, 64, 1);

        // Four at a time, all equal within each group.
        for (var group = 1; group < 15; group++) {
            var first = buffer[group * 4];

            for (var offset = 1; offset < 4; offset++) {
                Assert.Equal(first, buffer[(group * 4) + offset], 1e-6f);
            }
        }
    }

    /// <summary>
    ///     An integer counter cannot sweep the rate without stepping audibly on the way; a phase
    ///     accumulator can, which is what makes "a signal degrading" possible.
    /// </summary>
    [Fact]
    public void TheRateDivisorCanBeFractional() {
        var crusher = new BitCrusherEffect { Bits = 24f, Downsample = 2.5f };
        crusher.Prepare(AudioFormat.Mono48k, 1_000);

        var buffer = new float[1_000];

        for (var i = 0; i < buffer.Length; i++) {
            buffer[i] = i;
        }

        crusher.Process(buffer, 1_000, 1);

        var distinct = buffer.Distinct().Count();

        // A thousand samples at one every 2.5 is four hundred distinct values, give or take the ends.
        Assert.InRange(distinct, 395, 405);
    }

    /// <summary>
    ///     The thing PlaybackSettings.Pitch cannot do: a voice played at 2.0 is an octave up
    ///     <em>and</em> half as long.
    /// </summary>
    [Fact]
    public void ThePitchShifterChangesThePitchAndNotTheLength() {
        var shifter = new PitchShiftEffect { Semitones = 12f, GrainSeconds = 0.04f };
        shifter.Prepare(AudioFormat.Mono48k, 24_000);

        var buffer = Sine(500f, 24_000);
        shifter.Process(buffer, 24_000, 1);

        // Still 24 000 samples of continuous sound, and now an octave up.
        Assert.Equal(24_000, buffer.Length);
        Assert.True(AudioTestData.Peak(buffer.AsSpan(20_000)) > 0.2f, "it ran out before the end");
        Assert.Equal(1_000f, DominantFrequency(buffer.AsSpan(8_000, 8_192)), 40f);
    }

    [Fact]
    public void ShiftingDownWorksToo() {
        var shifter = new PitchShiftEffect { Semitones = -12f, GrainSeconds = 0.04f };
        shifter.Prepare(AudioFormat.Mono48k, 24_000);

        var buffer = Sine(800f, 24_000);
        shifter.Process(buffer, 24_000, 1);

        Assert.Equal(400f, DominantFrequency(buffer.AsSpan(8_000, 8_192)), 40f);
    }

    [Fact]
    public void NoShiftLeavesThePitchWhereItWas() {
        var shifter = new PitchShiftEffect { Semitones = 0f };
        shifter.Prepare(AudioFormat.Mono48k, 16_384);

        var buffer = Sine(1_000f, 16_384);
        shifter.Process(buffer, 16_384, 1);

        Assert.Equal(1_000f, DominantFrequency(buffer.AsSpan(4_000, 8_192)), 20f);
    }

    [Fact]
    public void SemitonesAreTheUsualRatio() {
        Assert.Equal(1f, new PitchShiftEffect { Semitones = 0f }.Ratio, 1e-5f);
        Assert.Equal(2f, new PitchShiftEffect { Semitones = 12f }.Ratio, 1e-5f);
        Assert.Equal(0.5f, new PitchShiftEffect { Semitones = -12f }.Ratio, 1e-5f);
        Assert.Equal(1.4983f, new PitchShiftEffect { Semitones = 7f }.Ratio, 1e-3f);
    }

    /// <summary>Which bin holds the most energy, converted back to hertz.</summary>
    static float DominantFrequency(ReadOnlySpan<float> samples) {
        var analyzer = new SpectrumAnalyzerEffect(8_192) { Smoothing = 0f };
        analyzer.Prepare(AudioFormat.Mono48k, samples.Length);

        var copy = samples.ToArray();
        analyzer.Process(copy, copy.Length, 1);

        var magnitudes = new float[analyzer.BinCount];
        Assert.True(analyzer.TryCopyTo(magnitudes));

        var best = 0;

        for (var bin = 1; bin < magnitudes.Length; bin++) {
            if (magnitudes[bin] > magnitudes[best]) {
                best = bin;
            }
        }

        return best * analyzer.BinWidthHz;
    }
}
