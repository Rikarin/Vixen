// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Dsp;
using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>The rate conversion, and what a straight line between two samples costs.</summary>
public sealed class ResamplerTests {
    const int Rate = 48_000;

    /// <summary>The magnitudes of a rendered stretch, so an unwanted tone can be found in it.</summary>
    static float[] Spectrum(ReadOnlySpan<float> mono, int size = 8_192) {
        var analyzer = new SpectrumAnalyzerEffect(size) { Smoothing = 0f };
        analyzer.Prepare(AudioFormat.Mono48k, mono.Length);

        var copy = mono.ToArray();
        analyzer.Process(copy, copy.Length, 1);

        var magnitudes = new float[analyzer.BinCount];
        Assert.True(analyzer.TryCopyTo(magnitudes));
        return magnitudes;
    }

    static float[] RenderPitched(float toneHz, float pitch, int frames) {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 4);

        using (engine) {
            engine.Play(AudioTestData.Tone(toneHz, frames * 4, 0.8f), new PlaybackSettings { Pitch = pitch });

            // The first blocks contain the gain ramp and the window filling, which are not what this
            // is measuring.
            AudioTestData.Render(device, 2_048);
            return AudioTestData.Render(device, frames);
        }
    }

    /// <summary>
    ///     The claim the resampler exists for, and the only one that separates it from a straight line.
    ///     A 9 kHz tone played at three times the rate asks for 27 kHz, which is past Nyquist and
    ///     cannot exist — so there is no right answer, only two wrong ones. Linear interpolation lets
    ///     it fold back down to 21 kHz, where it is a loud inharmonic whistle over the music. A sinc
    ///     with its cutoff brought down to match the ratio removes the 9 kHz before it can fold, and
    ///     what comes out is quiet.
    /// </summary>
    [Fact]
    public void PitchingUpRemovesWhatWouldAliasRatherThanLettingItFold() {
        var rendered = RenderPitched(9_000f, 3f, 8_192);
        var magnitudes = Spectrum(rendered);
        var binWidth = Rate / 8_192f;

        // 27 kHz mirrors about Nyquist to 21 kHz. Nothing should be there.
        var mirrored = (int)MathF.Round((Rate - 27_000f) / binWidth);
        var alias = 0f;

        for (var bin = mirrored - 6; bin <= mirrored + 6; bin++) {
            alias = MathF.Max(alias, magnitudes[bin]);
        }

        Assert.True(alias < 0.02f, $"the fold came through at {alias:F4}");
        Assert.True(AudioTestData.Peak(rendered) < 0.1f, "something loud came out of a tone that cannot exist");
    }

    /// <summary>The band has to cover the ratio, and rounding it the other way is what lets a fold through.</summary>
    [Fact]
    public void TheBandIsAlwaysNarrowEnoughForTheRatio() {
        Assert.Equal(0, SincTable.BandFor(1.0));
        Assert.Equal(0, SincTable.BandFor(0.25));

        foreach (var ratio in new[] { 1.1, 1.5, 2.0, 2.5, 3.0, 4.0, 6.0, 8.0 }) {
            var band = SincTable.BandFor(ratio);

            Assert.True(
                SincTable.Cutoff(band) <= 1.0 / ratio + 1e-6,
                $"ratio {ratio} got band {band}, whose cutoff {SincTable.Cutoff(band):F4} lets something fold"
            );
        }

        // And past what the table covers it saturates rather than reading off the end.
        Assert.Equal(SincTable.Bands - 1, SincTable.BandFor(1_000.0));
    }

    /// <summary>A ratio of exactly one never leaves a sample, so nothing is interpolated at all.</summary>
    [Fact]
    public void UnityIsBitExactPassthrough() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 4);

        using (engine) {
            var clip = AudioTestData.Ramp(4_096);
            engine.Play(clip);

            var rendered = AudioTestData.Render(device, 512);

            for (var frame = 0; frame < 512; frame++) {
                Assert.Equal(frame * AudioTestData.RampStep, rendered[frame], 1e-7f);
            }
        }
    }

    /// <summary>Otherwise the gain wobbles with the fractional position — a ripple on a sustained note.</summary>
    [Fact]
    public void EveryPhaseOfTheFilterSumsToOne() {
        for (var phase = 0; phase < SincTable.Phases; phase++) {
            var window = SincTable.Window(phase / (float)SincTable.Phases);
            var sum = 0f;

            foreach (var tap in window) {
                sum += tap;
            }

            Assert.Equal(1f, sum, 1e-5f);
        }
    }

    /// <summary>At no offset at all the ideal filter is a single tap, and the table's is too.</summary>
    [Fact]
    public void ThePhaseAtZeroIsADelta() {
        var window = SincTable.Window(0.0);

        for (var tap = 0; tap < SincTable.Taps; tap++) {
            Assert.Equal(tap == (SincTable.Taps / 2) - 1 ? 1f : 0f, window[tap], 1e-6f);
        }
    }

    /// <summary>Narrowing when pitching down would throw away treble for nothing.</summary>
    [Fact]
    public void PitchingDownIsNotNarrowedAtAll() {
        Assert.Equal(1f, SincTable.Cutoff(SincTable.BandFor(0.5)));
        Assert.Equal(1f, SincTable.Cutoff(SincTable.BandFor(1.0)));
        Assert.True(SincTable.Cutoff(SincTable.BandFor(2.0)) < 1f);
    }

    /// <summary>
    ///     A clip shorter than the interpolation window used to end before it was heard: the window
    ///     is filled ahead of the playhead, so running out of source is not the end of the sound.
    /// </summary>
    [Fact]
    public void AClipShorterThanTheWindowIsStillHeard() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 4);

        using (engine) {
            engine.Play(AudioTestData.Constant(4, 1f));
            var rendered = AudioTestData.Render(device, 64);

            Assert.True(AudioTestData.Peak(rendered) > 0.9f, "the whole clip was inside the window");
        }
    }

    [Fact]
    public void AVoiceStillEndsRatherThanDrainingForever() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 4);

        using (engine) {
            var handle = engine.Play(AudioTestData.Constant(64, 1f));

            AudioTestData.Render(device, 512);
            engine.Update(0f);

            Assert.False(engine.IsPlaying(handle));
        }
    }

    /// <summary>Down an octave is interpolation rather than decimation, and must not lose the tone.</summary>
    [Fact]
    public void PitchingDownKeepsTheSound() {
        var rendered = RenderPitched(4_000f, 0.5f, 4_096);
        Assert.True(AudioTestData.Peak(rendered) > 0.5f, "it went quiet");

        var magnitudes = Spectrum(rendered, 4_096);
        var binWidth = Rate / 4_096f;
        var expected = (int)MathF.Round(2_000f / binWidth);
        var loudest = 0;

        for (var bin = 1; bin < magnitudes.Length; bin++) {
            if (magnitudes[bin] > magnitudes[loudest]) {
                loudest = bin;
            }
        }

        Assert.InRange(loudest, expected - 2, expected + 2);
    }
}
