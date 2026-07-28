// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Dsp;

/// <summary>Finds the peak of the waveform a converter will actually produce.</summary>
/// <remarks>
///     <para>
///         <b>Sample peak is not the peak.</b> The samples are points on a curve, and the curve a
///         reconstruction filter draws through them passes <em>between</em> them — over the top of the
///         highest one, when the signal is near full scale and not aligned to the sample grid. A mix
///         that reads −1.0 dBFS on sample peak can be at −0.3 dBTP in the analogue domain, and clip a
///         converter that had every right to expect headroom.
///     </para>
///     <para>
///         <b>Two things care, and neither of them is subtle.</b> Console certification specifies a
///         true-peak ceiling — typically −1 dBTP — and sample peak is not what it measures. And lossy
///         encoding: a signal sitting at 0 dBFS sample peak clips when it is encoded to Opus and
///         decoded again, because the codec's own reconstruction overshoots too. The headroom protects
///         against both.
///     </para>
///     <para>
///         <b>Four times, because that is what the standard asks for</b> and because it is enough:
///         the residual error of a 4× oversampled peak is a couple of tenths of a decibel, which is
///         inside the margin any sane ceiling leaves. Sixteen times would be more accurate and would
///         cost four times as much to find out something nobody acts on.
///     </para>
///     <para>
///         <b>The filter is derived rather than tabled.</b> BS.1770 prints one set of coefficients for
///         48 kHz; a meter that used them at 44.1 would be interpolating with a filter whose cutoff is
///         in the wrong place. A windowed sinc built for the phase count works at any rate, because
///         the interpolation is between samples and knows nothing about how fast they arrive.
///     </para>
/// </remarks>
public sealed class TruePeakMeter {
    /// <summary>How many points are evaluated between each pair of samples, including the sample.</summary>
    public const int Oversampling = 4;

    /// <summary>How many input samples each interpolated point is drawn from.</summary>
    /// <remarks>
    ///     Twelve is what the standard's own filter uses. Sixteen here, because the cost is a metering
    ///     effect's rather than a mixing one's and the extra taps take the passband ripple below the
    ///     point where it could push a reading across a ceiling.
    /// </remarks>
    public const int Taps = 16;

    readonly float[] phases;
    readonly float[] history;
    readonly int channels;
    readonly int[] cursors;

    /// <summary>A meter over some channels.</summary>
    /// <param name="channelCount">How many. Each keeps its own history.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelCount" /> is not positive.</exception>
    public TruePeakMeter(int channelCount) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);

        channels = channelCount;
        history = new float[channelCount * Taps];
        cursors = new int[channelCount];

        // The prototype: a sinc at the oversampled rate, Blackman-windowed, cut so that it passes
        // everything below the original Nyquist and nothing above it.
        phases = new float[Oversampling * Taps];
        var length = Oversampling * Taps;
        var centre = (length - 1) / 2.0;

        for (var i = 0; i < length; i++) {
            var x = (i - centre) / Oversampling;
            var sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

            // Blackman, for the same reason SincTable uses one: the stopband matters more than the
            // width of the transition, because what leaks through is an aliased image of the signal.
            var t = 2.0 * Math.PI * i / (length - 1);
            var window = 0.42 - (0.5 * Math.Cos(t)) + (0.08 * Math.Cos(2.0 * t));

            phases[i] = (float)(sinc * window);
        }

        // Each phase normalised on its own. A phase whose coefficients did not sum to one would put a
        // gain on the points it interpolates, and a gain on a peak reading is a false failure or a
        // false pass depending on which way it went.
        for (var phase = 0; phase < Oversampling; phase++) {
            var sum = 0.0;

            for (var tap = 0; tap < Taps; tap++) {
                sum += phases[(tap * Oversampling) + phase];
            }

            if (Math.Abs(sum) < 1e-9) {
                continue;
            }

            for (var tap = 0; tap < Taps; tap++) {
                phases[(tap * Oversampling) + phase] /= (float)sum;
            }
        }
    }

    /// <summary>The highest true peak seen since the last <see cref="Reset" />, as a linear amplitude.</summary>
    public float Peak { get; private set; }

    /// <summary>The same in decibels relative to full scale, which is the unit a ceiling is written in.</summary>
    public float PeakDbTp => Peak > 0f ? 20f * MathF.Log10(Peak) : float.NegativeInfinity;

    /// <summary>Takes one sample of one channel.</summary>
    /// <param name="channel">Which channel.</param>
    /// <param name="sample">The sample.</param>
    public void Push(int channel, float sample) {
        var cursor = cursors[channel];
        var offset = channel * Taps;

        history[offset + cursor] = sample;
        cursors[channel] = (cursor + 1) % Taps;

        // The sample itself counts. Interpolation can only find a peak between samples that is higher
        // than the ones either side of it, so true peak is never below sample peak — and asserting
        // that is how a filter that has gone wrong announces itself.
        var loudest = MathF.Abs(sample);

        // Phase zero is the sample, so only the points strictly between are worth evaluating.
        for (var phase = 1; phase < Oversampling; phase++) {
            var sum = 0f;

            for (var tap = 0; tap < Taps; tap++) {
                // Oldest first, so the taps line up with a filter written left to right.
                var index = (cursors[channel] + tap) % Taps;
                sum += phases[(tap * Oversampling) + phase] * history[offset + index];
            }

            loudest = MathF.Max(loudest, MathF.Abs(sum));
        }

        Peak = MathF.Max(Peak, loudest);
    }

    /// <summary>Forgets the reading and the history.</summary>
    public void Reset() {
        Array.Clear(history);
        Array.Clear(cursors);
        Peak = 0f;
    }

    /// <summary>How many channels it is metering.</summary>
    public int Channels => channels;
}
