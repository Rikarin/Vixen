// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Dsp;
using Vixen.Audio.Effects;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>True peak and loudness range, the two numbers certification asks for that a mix does not.</summary>
public sealed class TruePeakTests {
    const int Rate = 48_000;

    static void Feed(TruePeakMeter meter, ReadOnlySpan<float> mono) {
        foreach (var sample in mono) {
            meter.Push(0, sample);
        }
    }

    static float[] Tone(float hertz, int frames, float amplitude = 1f, float phase = 0f) {
        var samples = new float[frames];

        for (var i = 0; i < frames; i++) {
            samples[i] = amplitude * MathF.Sin((2f * MathF.PI * hertz * i / Rate) + phase);
        }

        return samples;
    }

    /// <summary>
    ///     The invariant that a broken interpolation filter breaks first: a curve drawn through the
    ///     samples cannot dip below the samples it passes through.
    /// </summary>
    [Theory]
    [InlineData(100f)]
    [InlineData(1_000f)]
    [InlineData(7_997f)]
    [InlineData(19_000f)]
    public void TruePeakIsNeverBelowSamplePeak(float hertz) {
        var meter = new TruePeakMeter(1);
        var tone = Tone(hertz, Rate / 10, 0.5f);

        Feed(meter, tone);

        var samplePeak = 0f;

        foreach (var sample in tone) {
            samplePeak = MathF.Max(samplePeak, MathF.Abs(sample));
        }

        Assert.True(
            meter.Peak >= samplePeak - 1e-4f,
            $"true peak {meter.Peak:F4} came out below sample peak {samplePeak:F4}"
        );
    }

    /// <summary>
    ///     The case the whole thing exists for. A sine at a quarter of the sample rate, offset so no
    ///     sample lands on a crest, has every sample well below its own amplitude — and a converter
    ///     reproduces the crest anyway.
    /// </summary>
    [Fact]
    public void ItFindsAPeakThatNoSampleTouches() {
        // 12 kHz at 48 kHz is four samples a cycle. Shifted by an eighth of a cycle, the samples sit
        // at ±0.707 of the amplitude and the crests fall exactly between them.
        var tone = Tone(Rate / 4f, 4_000, 0.7f, MathF.PI / 4f);

        var samplePeak = 0f;

        foreach (var sample in tone) {
            samplePeak = MathF.Max(samplePeak, MathF.Abs(sample));
        }

        var meter = new TruePeakMeter(1);
        Feed(meter, tone);

        // The samples never exceed 0.707 × 0.7 ≈ 0.495 …
        Assert.True(samplePeak < 0.52f, $"the samples already reached {samplePeak:F4}");

        // … and the waveform between them reaches very nearly the full 0.7.
        Assert.True(meter.Peak > 0.66f, $"the interpolation only found {meter.Peak:F4}");
    }

    /// <summary>A sine sitting exactly on the sample grid has no overshoot to find.</summary>
    [Fact]
    public void AToneOnTheGridReadsWhatItsSamplesRead() {
        var meter = new TruePeakMeter(1);
        Feed(meter, Tone(Rate / 4f, 4_000, 0.5f, MathF.PI / 2f));

        Assert.Equal(0.5f, meter.Peak, 0.02f);
    }

    [Fact]
    public void SilenceHasNoPeakAndSaysSoInDecibels() {
        var meter = new TruePeakMeter(2);

        Assert.Equal(0f, meter.Peak);
        Assert.Equal(float.NegativeInfinity, meter.PeakDbTp);
    }

    [Fact]
    public void FullScaleReadsAboutZeroDecibels() {
        var meter = new TruePeakMeter(1);
        Feed(meter, Tone(997f, Rate / 10, 1f));

        Assert.Equal(0f, meter.PeakDbTp, 0.3f);
    }

    /// <summary>Each channel is interpolated from its own history, not from whatever came last.</summary>
    [Fact]
    public void ChannelsDoNotContaminateEachOther() {
        var meter = new TruePeakMeter(2);

        // A loud left and a silent right, interleaved. If the histories were shared, the right would
        // drag the left's interpolation down and the reading would come out low.
        var tone = Tone(Rate / 4f, 4_000, 0.7f, MathF.PI / 4f);

        foreach (var sample in tone) {
            meter.Push(0, sample);
            meter.Push(1, 0f);
        }

        Assert.True(meter.Peak > 0.66f, $"it read {meter.Peak:F4}");
    }

    [Fact]
    public void ResetForgetsIt() {
        var meter = new TruePeakMeter(1);
        Feed(meter, Tone(997f, 1_000));

        Assert.True(meter.Peak > 0.5f);
        meter.Reset();
        Assert.Equal(0f, meter.Peak);
    }

    // ── Through the meter ─────────────────────────────────────────────────────────────────────

    static LoudnessMeterEffect Meter(bool truePeak = true) {
        var meter = new LoudnessMeterEffect { MeasureTruePeak = truePeak };
        meter.Prepare(new AudioFormat(Rate, 2), 1_024);
        return meter;
    }

    static void Run(LoudnessMeterEffect meter, Func<int, float> signal, int frames) {
        var block = new float[512 * 2];

        for (var written = 0; written < frames; written += 512) {
            for (var i = 0; i < 512; i++) {
                var value = signal(written + i);
                block[i * 2] = value;
                block[(i * 2) + 1] = value;
            }

            meter.Process(block, 512, 2);
        }
    }

    [Fact]
    public void TheMeterReportsBothPeaksAndTheTrueOneIsHigher() {
        var meter = Meter();

        // The between-samples case again, this time all the way through the effect.
        Run(meter, i => 0.7f * MathF.Sin((2f * MathF.PI * (Rate / 4f) * i / Rate) + (MathF.PI / 4f)), Rate);

        Assert.True(meter.SamplePeak < 0.52f, $"sample peak was {meter.SamplePeak:F4}");
        Assert.True(meter.TruePeak > meter.SamplePeak, "the true peak was not above the sample peak");
        Assert.True(meter.TruePeakDbTp > -4f, $"it read {meter.TruePeakDbTp:F2} dBTP");
    }

    /// <summary>Turning it off has to leave every loudness reading exactly where it was.</summary>
    [Fact]
    public void TurningItOffChangesNothingElse() {
        var on = Meter();
        var off = Meter(truePeak: false);

        static float Signal(int i) => 0.5f * MathF.Sin(2f * MathF.PI * 997f * i / Rate);

        Run(on, Signal, Rate * 2);
        Run(off, Signal, Rate * 2);

        Assert.Equal(on.Integrated, off.Integrated, 1e-4f);
        Assert.Equal(on.SamplePeak, off.SamplePeak, 1e-6f);

        Assert.True(on.TruePeak > 0f);
        Assert.Equal(0f, off.TruePeak);
        Assert.Equal(float.NegativeInfinity, off.TruePeakDbTp);
    }

    // ── Loudness range ────────────────────────────────────────────────────────────────────────

    /// <summary>A programme that never changes level has no range, however long it goes on.</summary>
    [Fact]
    public void AFlatProgrammeHasNoRange() {
        var meter = Meter(truePeak: false);

        Run(meter, i => 0.25f * MathF.Sin(2f * MathF.PI * 997f * i / Rate), Rate * 12);

        Assert.True(meter.LoudnessRange < 1f, $"a constant tone showed a range of {meter.LoudnessRange:F2} LU");
    }

    /// <summary>And one that alternates between two levels has about the range between them.</summary>
    [Fact]
    public void AProgrammeThatMovesHasTheRangeItMovedOver() {
        var meter = Meter(truePeak: false);

        // Four seconds quiet, four loud, repeated: 20 dB apart, which the short-term window has time
        // to settle on at both ends.
        Run(
            meter,
            i => {
                var loud = i / (Rate * 4) % 2 == 1;
                var amplitude = loud ? 0.5f : 0.05f;
                return amplitude * MathF.Sin(2f * MathF.PI * 997f * i / Rate);
            },
            Rate * 24
        );

        Assert.True(
            meter.LoudnessRange is > 12f and < 26f,
            $"20 dB of movement measured as {meter.LoudnessRange:F2} LU"
        );
    }

    [Fact]
    public void ThereIsNoRangeBeforeThereIsProgramme() {
        var meter = Meter(truePeak: false);

        Assert.Equal(0f, meter.LoudnessRange);

        // Under three seconds, so not one whole short-term window has closed.
        Run(meter, i => 0.3f * MathF.Sin(2f * MathF.PI * 997f * i / Rate), Rate);

        Assert.Equal(0f, meter.LoudnessRange);
    }

    [Fact]
    public void ResetClearsTheRangeAndThePeak() {
        var meter = Meter();

        Run(meter, i => 0.5f * MathF.Sin(2f * MathF.PI * 997f * i / Rate), Rate * 6);
        Assert.True(meter.TruePeak > 0f);

        meter.Reset();

        Assert.Equal(0f, meter.TruePeak);
        Assert.Equal(0f, meter.LoudnessRange);
        Assert.Equal(0f, meter.SamplePeak);
    }
}
