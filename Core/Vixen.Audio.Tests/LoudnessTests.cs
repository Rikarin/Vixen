// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>BS.1770 loudness, which is what a platform's requirement is written in.</summary>
public sealed class LoudnessTests {
    const int Rate = 48_000;

    /// <summary>The peak amplitude of the sine the standard calibrates against.</summary>
    /// <remarks>
    ///     <b>Peak, with no root of two anywhere.</b> A sine of peak <c>A</c> has a mean square of
    ///     <c>A²/2</c>, and two channels of it sum to <c>A²</c> — so a peak of <c>10^(−23/20)</c> puts
    ///     the sum at exactly −23 dB, which the −0.691 offset and the K-weighting's gain at 1 kHz then
    ///     cancel each other out of. Converting the peak to an RMS on the way in double-counts the
    ///     halving and reads 3.01 LU high, which is a factor of two in power wearing a plausible face.
    /// </remarks>
    static float Calibrated => MathF.Pow(10f, -23f / 20f);

    static LoudnessMeterEffect Meter(int channels = 2) {
        var meter = new LoudnessMeterEffect();
        meter.Prepare(new AudioFormat(Rate, channels), 4_800);
        return meter;
    }

    /// <summary>Interleaved sine at a peak amplitude, in every channel it is told to fill.</summary>
    static float[] Tone(float peak, float seconds, int channels, params int[] only) {
        var frames = (int)(Rate * seconds);
        var buffer = new float[frames * channels];

        for (var frame = 0; frame < frames; frame++) {
            var value = peak * MathF.Sin(2f * MathF.PI * 1_000f * frame / Rate);

            for (var channel = 0; channel < channels; channel++) {
                if (only.Length == 0 || Array.IndexOf(only, channel) >= 0) {
                    buffer[(frame * channels) + channel] = value;
                }
            }
        }

        return buffer;
    }

    static void Feed(LoudnessMeterEffect meter, float[] buffer, int channels) =>
        meter.Process(buffer, buffer.Length / channels, channels);

    /// <summary>
    ///     The standard's own calibration: a 1 kHz sine at −23 dBFS in both channels of a stereo
    ///     programme reads −23 LUFS. If the filters or the offset are wrong this is the test that says
    ///     so, and it is the only absolute anchor there is.
    /// </summary>
    [Fact]
    public void AMinusTwentyThreeSineReadsMinusTwentyThreeLufs() {
        var meter = Meter();
        var peak = Calibrated;

        Feed(meter, Tone(peak, 5f, 2), 2);

        Assert.Equal(-23f, meter.Integrated, 0.3f);
        Assert.Equal(-23f, meter.Momentary, 0.3f);
        Assert.Equal(-23f, meter.ShortTerm, 0.3f);
    }

    [Fact]
    public void TenDecibelsLouderIsTenLouderUnits() {
        var quiet = Meter();
        var loud = Meter();
        var peak = Calibrated;

        Feed(quiet, Tone(peak, 3f, 2), 2);
        Feed(loud, Tone(peak * MathF.Pow(10f, 10f / 20f), 3f, 2), 2);

        Assert.Equal(10f, loud.Integrated - quiet.Integrated, 0.05f);
    }

    [Fact]
    public void SilenceReadsNothingAtAllRatherThanZero() {
        var meter = Meter();
        Feed(meter, new float[Rate * 2], 2);

        Assert.Equal(float.NegativeInfinity, meter.Momentary);
        Assert.Equal(float.NegativeInfinity, meter.Integrated);
        Assert.Equal(0, meter.GatedBlocks);
    }

    /// <summary>
    ///     Without the gate, a minute of silence at the end of a level halves the reported loudness of
    ///     the level.
    /// </summary>
    [Fact]
    public void SilenceIsGatedOutOfTheIntegratedReading() {
        var peak = Calibrated;

        var alone = Meter();
        Feed(alone, Tone(peak, 4f, 2), 2);

        var padded = Meter();
        Feed(padded, Tone(peak, 4f, 2), 2);
        Feed(padded, new float[Rate * 2 * 8], 2);

        Assert.Equal(alone.Integrated, padded.Integrated, 0.2f);
    }

    /// <summary>The second gate: quiet passages count, but not ones ten below the rest.</summary>
    [Fact]
    public void AVeryQuietPassageIsGatedOutButAModeratelyQuietOneIsNot() {
        var peak = Calibrated;

        var withQuiet = Meter();
        Feed(withQuiet, Tone(peak, 4f, 2), 2);
        Feed(withQuiet, Tone(peak * MathF.Pow(10f, -20f / 20f), 4f, 2), 2);

        // Twenty decibels down is past the relative gate, so it does not pull the mean down.
        Assert.Equal(-23f, withQuiet.Integrated, 0.4f);

        var withModerate = Meter();
        Feed(withModerate, Tone(peak, 4f, 2), 2);
        Feed(withModerate, Tone(peak * MathF.Pow(10f, -5f / 20f), 4f, 2), 2);

        // Five down is inside the gate, so it does.
        Assert.True(withModerate.Integrated < -23.5f, $"it read {withModerate.Integrated:F2}");
    }

    /// <summary>Because the ".1" is a band, not a place, and a listener does not hear it as loudness.</summary>
    [Fact]
    public void TheLowFrequencyChannelIsNotCounted() {
        var meter = Meter(6);
        var peak = Calibrated;

        // Everything in the LFE and nothing anywhere else.
        Feed(meter, Tone(peak, 2f, 6, 3), 6);

        Assert.Equal(float.NegativeInfinity, meter.Integrated);

        // And it still saw the samples, which is what the peak meter is for — the LFE is excluded
        // from loudness, not from clipping.
        Assert.Equal(Calibrated, meter.SamplePeak, 1e-4f);
    }

    [Fact]
    public void TheSurroundsAreWeightedUp() {
        var peak = Calibrated;

        var front = Meter(6);
        Feed(front, Tone(peak, 3f, 6, 0), 6);

        var side = Meter(6);
        Feed(side, Tone(peak, 3f, 6, 4), 6);

        // 1.41 in power is +1.5 dB.
        Assert.Equal(1.5f, side.Integrated - front.Integrated, 0.05f);
    }

    /// <summary>A meter that altered what it measured would be a compressor.</summary>
    [Fact]
    public void ItPassesTheSignalThroughUntouched() {
        var meter = Meter();
        var buffer = Tone(0.5f, 0.5f, 2);
        var expected = (float[])buffer.Clone();

        Feed(meter, buffer, 2);

        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void ThePeakIsTheLoudestSampleSinceTheLastReset() {
        var meter = Meter();

        Feed(meter, Tone(0.8f, 0.2f, 2), 2);
        Assert.Equal(0.8f, meter.SamplePeak, 0.01f);

        Feed(meter, Tone(0.3f, 0.2f, 2), 2);
        Assert.Equal(0.8f, meter.SamplePeak, 0.01f);

        meter.Reset();
        Assert.Equal(0f, meter.SamplePeak);
        Assert.Equal(float.NegativeInfinity, meter.Integrated);
    }

    /// <summary>Four hundred milliseconds, which is what "momentary" means and is not a whole block early.</summary>
    [Fact]
    public void MomentaryNeedsFourHundredMillisecondsBeforeItSaysAnything() {
        var meter = Meter();

        Feed(meter, Tone(0.5f, 0.3f, 2), 2);
        Assert.Equal(float.NegativeInfinity, meter.Momentary);

        Feed(meter, Tone(0.5f, 0.2f, 2), 2);
        Assert.True(float.IsFinite(meter.Momentary));
    }

    /// <summary>
    ///     A window summed while one of its four hops is empty reads about 1.2 LU low — which looks
    ///     plausible, and is the worst kind of wrong.
    /// </summary>
    [Fact]
    public void TheMomentaryWindowIsAWholeFourHundredMillisecondsOfSignal() {
        var meter = Meter();
        var peak = Calibrated;

        Feed(meter, Tone(peak, 1f, 2), 2);

        Assert.Equal(-23f, meter.Momentary, 0.3f);
    }

    [Fact]
    public void ADisabledMeterMeasuresNothing() {
        var meter = Meter();
        meter.Enabled = false;

        Feed(meter, Tone(0.5f, 1f, 2), 2);

        Assert.Equal(float.NegativeInfinity, meter.Momentary);
        Assert.Equal(0f, meter.SamplePeak);
    }

    /// <summary>
    ///     BS.1770 prints coefficients for 48 kHz and nothing else, and a meter that used them at
    ///     44 100 would be measuring a shelf an eighth of an octave low.
    /// </summary>
    [Fact]
    public void ItReadsTheSameAtADifferentSampleRate() {
        var meter = new LoudnessMeterEffect();
        meter.Prepare(new AudioFormat(44_100, 2), 4_800);

        var frames = 44_100 * 4;
        var buffer = new float[frames * 2];
        var peak = Calibrated;

        for (var frame = 0; frame < frames; frame++) {
            var value = peak * MathF.Sin(2f * MathF.PI * 1_000f * frame / 44_100f);
            buffer[frame * 2] = value;
            buffer[(frame * 2) + 1] = value;
        }

        meter.Process(buffer, frames, 2);

        Assert.Equal(-23f, meter.Integrated, 0.3f);
    }
}
