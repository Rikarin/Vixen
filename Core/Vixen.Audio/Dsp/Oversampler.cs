// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Dsp;

/// <summary>Runs a nonlinearity at a higher rate, so the harmonics it makes have somewhere to go.</summary>
/// <remarks>
///     <para>
///         <b>Bending a waveform makes harmonics, and harmonics above Nyquist do not vanish — they
///         fold.</b> A 5 kHz tone through a cubic curve produces 15 kHz, 25 kHz and 35 kHz. At 48 kHz
///         everything above 24 comes back down: 25 reappears at 23, and 35 at 13. Those are not
///         harmonics of 5 kHz. They are inharmonic tones that move the <em>wrong way</em> when the
///         input pitch changes, which is exactly what makes aliased distortion sound like grit rather
///         than like distortion.
///     </para>
///     <para>
///         <b>The fix is room.</b> Interpolate to four times the rate, shape there — where the first
///         several harmonics fit below the new Nyquist — filter them off, and come back down. What
///         folds is then far quieter and far higher, and the audible result is the harmonic series
///         the curve was supposed to produce.
///     </para>
///     <para>
///         <b>It is not always worth it.</b> For sustained tonal material — a guitar, a synth — the
///         difference is the whole character of the effect. For an explosion, a radio voice or a
///         damaged machine, the source is already noisy and nobody can pick the aliasing out of it.
///         So this is a switch and not a default.
///     </para>
///     <para>
///         <b>One filter design, used both ways.</b> Going up, the prototype is a polyphase
///         interpolator; coming down it is a plain low-pass at the same cutoff running at the high
///         rate, of which one output in <see cref="Factor" /> is kept. Two filters designed
///         separately would be two chances to put the cutoff in the wrong place.
///     </para>
/// </remarks>
public sealed class Oversampler {
    /// <summary>How many input samples each interpolated point is drawn from.</summary>
    const int Taps = 32;

    readonly float[] phases;
    readonly float[] prototype;
    readonly float[] upHistory;
    readonly float[] downHistory;
    readonly int[] upCursors;
    readonly int[] downCursors;
    readonly int downTaps;

    /// <summary>An oversampler over some channels.</summary>
    /// <param name="channels">How many. Each keeps its own filter state.</param>
    /// <param name="factor">2 or 4. Four is what a distortion wants.</param>
    /// <exception cref="ArgumentOutOfRangeException">The factor is not one of the two, or there are no channels.</exception>
    public Oversampler(int channels, int factor = 4) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        if (factor is not (2 or 4)) {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Oversampling is by 2 or by 4.");
        }

        Channels = channels;
        Factor = factor;
        downTaps = factor * Taps;

        upHistory = new float[channels * Taps];
        downHistory = new float[channels * downTaps];
        upCursors = new int[channels];
        downCursors = new int[channels];

        // The prototype: a sinc at the oversampled rate, cut at the original Nyquist, Blackman
        // windowed — the stopband is what matters, because what leaks through it is the aliasing this
        // exists to prevent.
        prototype = new float[downTaps];
        var centre = (downTaps - 1) / 2.0;

        for (var i = 0; i < downTaps; i++) {
            var x = (i - centre) / factor;
            var sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            var t = 2.0 * Math.PI * i / (downTaps - 1);
            var window = 0.42 - (0.5 * Math.Cos(t)) + (0.08 * Math.Cos(2.0 * t));
            prototype[i] = (float)(sinc * window);
        }

        // Going down, the filter is the prototype with unit gain — it is an average, so its
        // coefficients sum to one.
        var total = 0.0;

        foreach (var tap in prototype) {
            total += tap;
        }

        if (Math.Abs(total) > 1e-9) {
            for (var i = 0; i < downTaps; i++) {
                prototype[i] /= (float)total;
            }
        }

        // Going up, each phase is its own interpolator and is normalised on its own, so a constant
        // input interpolates to the same constant rather than to a scaled one.
        phases = new float[downTaps];
        prototype.CopyTo(phases, 0);

        for (var phase = 0; phase < factor; phase++) {
            var sum = 0.0;

            for (var tap = 0; tap < Taps; tap++) {
                sum += phases[(tap * factor) + phase];
            }

            if (Math.Abs(sum) < 1e-9) {
                continue;
            }

            for (var tap = 0; tap < Taps; tap++) {
                phases[(tap * factor) + phase] /= (float)sum;
            }
        }
    }

    /// <summary>How many channels it holds state for.</summary>
    public int Channels { get; }

    /// <summary>How many points it produces per input sample.</summary>
    public int Factor { get; }

    /// <summary>How many input samples of delay the two filters add, together.</summary>
    /// <remarks>
    ///     <b>Worth knowing before it surprises somebody.</b> Both filters are linear phase, so the
    ///     signal comes out intact but late — about two thirds of a millisecond at 48 kHz. On a
    ///     distortion that is inaudible on its own and audible the moment it is mixed against a dry
    ///     copy of the same source, which is what a parallel path is.
    /// </remarks>
    public int Latency => (int)MathF.Round((downTaps - 1f) / Factor);

    /// <summary>Turns one sample into <see cref="Factor" /> of them.</summary>
    /// <param name="channel">Which channel.</param>
    /// <param name="sample">The sample.</param>
    /// <param name="destination">Where they go. At least <see cref="Factor" /> long.</param>
    public void Expand(int channel, float sample, Span<float> destination) {
        var cursor = upCursors[channel];
        var offset = channel * Taps;

        upHistory[offset + cursor] = sample;
        cursor = (cursor + 1) % Taps;
        upCursors[channel] = cursor;

        // Newest first, and that is not a detail. The polyphase decomposition of an interpolator is
        // y_p[n] = Σ h[k·L + p]·x[n − k] with k = 0 the *newest* sample. Walking the history oldest
        // first instead pairs each coefficient with the wrong sample, which mirrors every phase —
        // and a mirrored phase is still a plausible-looking low-pass, so the symptom is not a filter
        // that obviously fails but one whose image rejection quietly gets worse as taps are added.
        var newest = (cursor + Taps - 1) % Taps;

        for (var phase = 0; phase < Factor; phase++) {
            var sum = 0f;

            for (var tap = 0; tap < Taps; tap++) {
                sum += phases[(tap * Factor) + phase] * upHistory[offset + ((newest - tap + Taps) % Taps)];
            }

            destination[phase] = sum;
        }
    }

    /// <summary>Turns <see cref="Factor" /> shaped points back into one sample.</summary>
    /// <param name="channel">Which channel.</param>
    /// <param name="samples">Them. At least <see cref="Factor" /> long.</param>
    /// <returns>The sample.</returns>
    /// <remarks>
    ///     Every point goes through the filter and only the last output is kept — which is what
    ///     decimation is. Filtering only the one that is kept would leave everything the shaping put
    ///     above the original Nyquist to fold on the way down, which is the bug this whole class
    ///     exists to avoid.
    /// </remarks>
    public float Collapse(int channel, ReadOnlySpan<float> samples) {
        var offset = channel * downTaps;
        var cursor = downCursors[channel];
        var result = 0f;

        for (var i = 0; i < Factor; i++) {
            downHistory[offset + cursor] = samples[i];
            cursor = (cursor + 1) % downTaps;

            var sum = 0f;

            for (var tap = 0; tap < downTaps; tap++) {
                sum += prototype[tap] * downHistory[offset + ((cursor + tap) % downTaps)];
            }

            result = sum;
        }

        downCursors[channel] = cursor;

        // No gain correction, and that is the point worth writing down. The usual polyphase
        // upsampler inserts zeros and needs a gain of Factor to make up for them; this one does not —
        // each phase is a normalised interpolator that computes the intermediate value directly, so
        // it already comes out at the right amplitude, and the decimation filter's coefficients sum
        // to one. Multiplying by Factor here would make the whole chain four times too loud, which
        // through a clipper is not a level error but a completely different sound.
        return result;
    }

    /// <summary>Forgets every filter's history.</summary>
    public void Reset() {
        Array.Clear(upHistory);
        Array.Clear(downHistory);
        Array.Clear(upCursors);
        Array.Clear(downCursors);
    }
}
