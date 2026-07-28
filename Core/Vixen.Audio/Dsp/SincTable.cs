// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Dsp;

/// <summary>A windowed-sinc interpolator, precomputed once and shared by every voice.</summary>
/// <remarks>
///     <para>
///         <b>Linear interpolation is a filter, and a bad one.</b> Reading between two samples with a
///         straight line is a convolution with a triangle, whose response falls away from the first
///         sample and never reaches zero — so it dulls what it keeps and lets through everything it
///         should have removed. Pitched down by an octave it is barely audible; pitched up by one it
///         folds the top half of the spectrum back over the music as inharmonic noise, which is the
///         gritty edge every cheap sampler has.
///     </para>
///     <para>
///         <b>The right filter is a sinc, and the whole art is where to cut it off.</b> An infinite
///         sinc is a perfect low-pass and cannot be computed; truncating it rings. Windowing the
///         truncation trades ripple for transition width, and a Blackman window over sixteen taps is
///         about eighty decibels of stopband for a transition of a couple of per cent — well past
///         audibility, and sixteen multiply-accumulates a sample.
///     </para>
///     <para>
///         <b>Polyphase, because the phase is not arbitrary.</b> The filter is evaluated at a
///         fractional position between samples, and precomputing it at a few hundred positions turns
///         a transcendental per tap per sample into a table lookup. The residual error from snapping
///         to the nearest phase is the same order as the window's own ripple, so a finer table would
///         buy nothing.
///     </para>
///     <para>
///         <b>Static, immutable, and half a megabyte.</b> One table serves every voice in the process
///         — it depends on nothing about the device or the sound — so it is built on first use and
///         never again. First use means the first voice that is actually pitched: a ratio of one
///         never reaches here.
///     </para>
/// </remarks>
public static class SincTable {
    /// <summary>How many samples each output is made from.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Thirty-two, and the reason is the narrow bands rather than the wide one.</b> At full
    ///         cutoff sixteen taps are already past audibility. What sixteen cannot do is filter
    ///         steeply at a <em>low</em> cutoff: narrowing the cutoff widens the sinc, so a fixed
    ///         number of taps covers fewer of its lobes and the transition band grows — which means
    ///         the filter gets gentlest exactly where a voice needs it sharpest, and a tone half an
    ///         octave above the new cutoff comes through about twenty decibels down instead of buried.
    ///     </para>
    ///     <para>
    ///         Doubling the taps halves the transition and costs thirty-two multiply-accumulates a
    ///         sample — on the voices that are pitched, which are a minority, because unity is a
    ///         bit-exact passthrough that never reaches this table at all.
    ///     </para>
    /// </remarks>
    public const int Taps = 32;

    /// <summary>How many fractional positions between two samples the filter is precomputed at.</summary>
    public const int Phases = 512;

    /// <summary>How many cutoffs it is precomputed at, half an octave apart.</summary>
    /// <remarks>
    ///     Eight, which reaches a ratio of about eleven — well past any pitch anybody plays a sound
    ///     effect at. Half an octave of granularity means a voice is over-filtered by at most that,
    ///     which costs a little treble it was going to lose anyway and is far cheaper than the
    ///     alternative of designing a filter per voice per block.
    /// </remarks>
    public const int Bands = 8;

    static readonly float[] Coefficients = Build();

    /// <summary>The filter for a fractional position and a playback ratio.</summary>
    /// <param name="fraction">How far between two samples, from 0 to just under 1.</param>
    /// <param name="ratio">How many source frames go into one output frame. One is no change.</param>
    /// <returns><see cref="Taps" /> coefficients, to be applied to the samples around that position.</returns>
    public static ReadOnlySpan<float> Window(double fraction, double ratio = 1.0) {
        var phase = (int)(fraction * Phases);

        if ((uint)phase >= Phases) {
            phase = Phases - 1;
        }

        return Coefficients.AsSpan(((BandFor(ratio) * Phases) + phase) * Taps, Taps);
    }

    /// <summary>Which band a playback ratio needs.</summary>
    /// <param name="ratio">How many source frames go into one output frame.</param>
    /// <returns>The band, from 0 for no narrowing at all.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Pitching up is decimation, and decimation aliases unless you filter first.</b>
    ///         Playing a clip at twice the rate asks for content up to twice Nyquist, and everything
    ///         above Nyquist comes back down mirrored as inharmonic noise — which is the gritty edge
    ///         every cheap sampler has. Narrowing the interpolator's cutoff removes it before it can
    ///         fold: the sound loses its top octave, which is exactly what it would have lost had it
    ///         been recorded an octave higher.
    ///     </para>
    ///     <para>
    ///         <b>Rounded up, never down.</b> A band whose cutoff is above <c>1/ratio</c> lets
    ///         something through to fold, and the whole point is that nothing does. Rounding the other
    ///         way costs at most half an octave of treble on a sound that is being pitched up anyway.
    ///     </para>
    ///     <para>
    ///         Pitching <em>down</em> is interpolation rather than decimation and needs no narrowing,
    ///         which is why band zero is the answer for every ratio at or below one.
    ///     </para>
    /// </remarks>
    public static int BandFor(double ratio) {
        if (ratio <= 1.0) {
            return 0;
        }

        var band = (int)Math.Ceiling(Math.Log2(ratio) * 2.0);
        return Math.Clamp(band, 0, Bands - 1);
    }

    /// <summary>The cutoff a band filters at, as a fraction of Nyquist.</summary>
    /// <param name="band">Which band.</param>
    /// <returns>The cutoff.</returns>
    public static float Cutoff(int band) => (float)Math.Pow(2.0, -band / 2.0);

    /// <summary>Fills the table: a Blackman-windowed sinc at every cutoff and every phase.</summary>
    static float[] Build() {
        var table = new float[Bands * Phases * Taps];
        const int Half = Taps / 2;

        for (var band = 0; band < Bands; band++) {
            var cutoff = Cutoff(band);

            for (var phase = 0; phase < Phases; phase++) {
                var fraction = (double)phase / Phases;
                var sum = 0.0;
                var offset = ((band * Phases) + phase) * Taps;

                for (var tap = 0; tap < Taps; tap++) {
                    // Where this tap sits relative to the point being interpolated. The taps straddle
                    // it, which is why the window is centred on the fraction rather than on a sample.
                    var x = tap - Half + 1 - fraction;

                    // Narrowing the cutoff widens the sinc, so sixteen taps cover fewer of its lobes
                    // and the filter gets gentler as the band gets lower. That is the standard trade
                    // and it is the right way round: a heavily pitched-up sound has less to lose.
                    var value = Sinc(cutoff * x) * Blackman((x + Half) / Taps);
                    table[offset + tap] = (float)value;
                    sum += value;
                }

                // Normalised per phase, so a constant signal comes out constant. Without it the gain
                // wobbles with the fractional position — a slow ripple on a sustained note, which is
                // more audible than the aliasing this exists to remove. It also puts the narrowed
                // bands back at unity, which the cutoff scaling would otherwise take away.
                if (sum > 0.0) {
                    var inverse = (float)(1.0 / sum);

                    for (var tap = 0; tap < Taps; tap++) {
                        table[offset + tap] *= inverse;
                    }
                }
            }
        }

        return table;
    }

    static double Sinc(double x) {
        if (Math.Abs(x) < 1e-9) {
            return 1.0;
        }

        var pi = Math.PI * x;
        return Math.Sin(pi) / pi;
    }

    /// <summary>The Blackman window over 0..1.</summary>
    static double Blackman(double t) =>
        t is < 0.0 or > 1.0
            ? 0.0
            : 0.42 - (0.5 * Math.Cos(2.0 * Math.PI * t)) + (0.08 * Math.Cos(4.0 * Math.PI * t));
}
