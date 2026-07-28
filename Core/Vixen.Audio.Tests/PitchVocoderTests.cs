// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Dsp;
using Vixen.Audio.Effects;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     The claim a pitch shifter makes is about frequency, so every test here measures one. The
///     interesting comparison is against the time-domain shifter, which is not worse at everything —
///     it is worse at exactly the two things a phase vocoder exists to fix.
/// </summary>
public sealed class PitchVocoderTests {
    const int Rate = 48_000;
    const int Size = 16_384;

    static float[] Run(PitchVocoderEffect effect, ReadOnlySpan<float> input) {
        effect.Prepare(new AudioFormat(Rate, 1), 1_024);

        var output = input.ToArray();

        for (var at = 0; at < output.Length; at += 512) {
            effect.Process(output.AsSpan(at, 512), 512, 1);
        }

        return output;
    }

    static float[] Tone(int count, float hertz, float amplitude = 0.5f) {
        var samples = new float[count];

        for (var i = 0; i < count; i++) {
            samples[i] = amplitude * MathF.Sin(2f * MathF.PI * hertz * i / Rate);
        }

        return samples;
    }

    /// <summary>The spectrum of the settled part, past the latency and the ramp-up.</summary>
    static float[] Spectrum(ReadOnlySpan<float> signal, int from) {
        var block = signal.Slice(from, Size).ToArray();

        for (var i = 0; i < Size; i++) {
            block[i] *= 0.5f - (0.5f * MathF.Cos(2f * MathF.PI * i / (Size - 1)));
        }

        var fft = new RealFft(Size);
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];
        var magnitudes = new float[fft.Bins];

        fft.Forward(block, real, imaginary);
        fft.Magnitudes(real, imaginary, magnitudes);
        return magnitudes;
    }

    /// <summary>Which frequency holds the most energy.</summary>
    static float Loudest(ReadOnlySpan<float> spectrum) {
        var best = 1;

        for (var k = 2; k < spectrum.Length; k++) {
            if (spectrum[k] > spectrum[best]) {
                best = k;
            }
        }

        return best * (float)Rate / Size;
    }

    [Theory]
    [InlineData(12f, 2f)]
    [InlineData(7f, 1.4983f)]
    [InlineData(-12f, 0.5f)]
    [InlineData(-5f, 0.7492f)]
    public void ItShiftsByTheIntervalItWasAskedFor(float semitones, float expectedRatio) {
        var effect = new PitchVocoderEffect { Semitones = semitones, Mix = 1f };

        Assert.Equal(expectedRatio, effect.Ratio, 1e-3f);

        var output = Run(effect, Tone(Size * 4, 1_000f));
        var peak = Loudest(Spectrum(output, Size * 2));

        // Within a bin or two of where it was asked to put it.
        Assert.Equal(1_000f * expectedRatio, peak, 1_000f * expectedRatio * 0.03f);
    }

    [Fact]
    public void ANoShiftIsLeftAloneRatherThanRunThrough() {
        var effect = new PitchVocoderEffect { Semitones = 0f, Mix = 1f };
        var input = Tone(4_096, 440f);
        var output = Run(effect, input);

        // Byte for byte, because a shift of zero that still cost a window of latency would be a
        // surprise nobody asked for.
        for (var i = 0; i < input.Length; i++) {
            Assert.Equal(input[i], output[i]);
        }
    }

    /// <summary>
    ///     <b>What was measured, rather than what was assumed.</b> The obvious test here would be
    ///     "the vocoder is cleaner than <see cref="PitchShiftEffect" />", and on the signals a test
    ///     reaches for first — a held sine, a steady sawtooth — that is simply false. A two-tap
    ///     delay-line shifter reading a <em>stationary periodic</em> signal is very nearly exact,
    ///     because both taps sit on the same repeating waveform and the crossfade splices between two
    ///     points of one cycle. Measured on a 220 Hz sawtooth shifted up a fifth, the crossfade put
    ///     0.00% of its energy off the harmonic grid and this put 1.45%.
    /// </summary>
    /// <remarks>
    ///     So the comparison is not asserted, because it would not be true. What is asserted is that
    ///     a held tone comes out as a tone: nearly all of the energy on the harmonic grid of the
    ///     shifted fundamental, which is the claim this effect can actually keep. Where it earns its
    ///     window of latency is material that does <em>not</em> repeat — speech, vibrato, anything
    ///     whose partials move — and that is a judgement about sound rather than a number, so it
    ///     belongs in the README and not in an assertion pretending to be objective.
    /// </remarks>
    [Fact]
    public void AHeldToneComesOutOnTheHarmonicGrid() {
        var input = new float[Size * 4];

        for (var i = 0; i < input.Length; i++) {
            var value = 0f;

            for (var harmonic = 1; harmonic <= 20; harmonic++) {
                var hertz = 220f * harmonic;

                if (hertz > 16_000f) {
                    break;
                }

                value += MathF.Sin(2f * MathF.PI * hertz * i / Rate) / harmonic;
            }

            input[i] = 0.3f * value;
        }

        var output = Run(new PitchVocoderEffect { Semitones = 7f, Mix = 1f }, input);
        var spectrum = Spectrum(output, Size * 2);
        var shifted = 220f * MathF.Pow(2f, 7f / 12f);
        var binHertz = (float)Rate / Size;

        var total = 0f;
        var off = 0f;

        for (var k = 2; k < spectrum.Length; k++) {
            var hertz = k * binHertz;

            if (hertz > 16_000f) {
                break;
            }

            var energy = spectrum[k] * spectrum[k];
            total += energy;

            var nearest = MathF.Round(hertz / shifted) * shifted;

            if (nearest < 1f || MathF.Abs(hertz - nearest) > shifted * 0.12f) {
                off += energy;
            }
        }

        Assert.True(total > 0f);
        Assert.True(off / total < 0.05f, $"{off / total:P2} of the energy landed off the harmonic grid");
    }

    [Fact]
    public void ItSaysHowLateItIs() {
        var effect = new PitchVocoderEffect { FftSize = 2_048 };
        effect.Prepare(new AudioFormat(Rate, 1), 512);

        // Three quarters of the window, from the four-fold overlap.
        Assert.Equal(1_536, effect.Latency);
    }

    [Theory]
    [InlineData(100, 256)]
    [InlineData(2_048, 2_048)]
    [InlineData(3_000, 4_096)]
    [InlineData(99_999, 8_192)]
    public void TheWindowIsAPowerOfTwoAndWithinReason(int asked, int expected) {
        var effect = new PitchVocoderEffect { FftSize = asked };
        Assert.Equal(expected, effect.FftSize);
    }

    [Fact]
    public void MixBlendsRatherThanReplacing() {
        var input = Tone(Size * 3, 1_000f);
        var dry = Run(new PitchVocoderEffect { Semitones = 12f, Mix = 0f }, input);

        for (var i = 0; i < input.Length; i++) {
            Assert.Equal(input[i], dry[i], 1e-5f);
        }
    }

    [Fact]
    public void StereoChannelsAreShiftedIndependently() {
        var effect = new PitchVocoderEffect { Semitones = 12f, Mix = 1f };
        effect.Prepare(new AudioFormat(Rate, 2), 512);

        var buffer = new float[Size * 2 * 2];

        // A tone on the left, silence on the right.
        for (var i = 0; i < Size * 2; i++) {
            buffer[i * 2] = 0.5f * MathF.Sin(2f * MathF.PI * 1_000f * i / Rate);
        }

        for (var at = 0; at < Size * 2; at += 512) {
            effect.Process(buffer.AsSpan(at * 2, 512 * 2), 512, 2);
        }

        var right = 0f;

        for (var i = Size; i < Size * 2; i++) {
            right = MathF.Max(right, MathF.Abs(buffer[(i * 2) + 1]));
        }

        Assert.True(right < 1e-3f, $"the silent channel came out at {right:F5}");
    }

    /// <summary>Turning transient handling off has to actually change something, or it is a lie.</summary>
    [Fact]
    public void TransientHandlingIsSomethingRatherThanNothing() {
        // A click every 4 096 samples: nothing but onsets.
        var input = new float[Size * 2];

        for (var i = 0; i < input.Length; i++) {
            input[i] = i % 4_096 < 64 ? 0.8f : 0f;
        }

        var guarded = Run(new PitchVocoderEffect { Semitones = 5f, Mix = 1f, TransientSensitivity = 1.5f }, input);
        var smeared = Run(new PitchVocoderEffect { Semitones = 5f, Mix = 1f, TransientSensitivity = 0f }, input);

        var difference = 0f;

        for (var i = Size / 2; i < input.Length; i++) {
            difference = MathF.Max(difference, MathF.Abs(guarded[i] - smeared[i]));
        }

        Assert.True(difference > 1e-3f, "transient handling made no difference at all on a signal that is all transient");
    }

    [Fact]
    public void ResetForgetsTheSignal() {
        var effect = new PitchVocoderEffect { Semitones = 12f, Mix = 1f };
        Run(effect, Tone(Size, 1_000f));

        effect.Reset();

        var quiet = new float[2_048];
        effect.Process(quiet, 2_048, 1);

        var loudest = 0f;

        foreach (var sample in quiet) {
            loudest = MathF.Max(loudest, MathF.Abs(sample));
        }

        Assert.Equal(0f, loudest);
    }
}
