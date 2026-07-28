// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Effects;

/// <summary>Several biquads in series, which is all an equaliser has ever been.</summary>
/// <remarks>
///     <para>
///         <b>Composition rather than a new filter.</b> <see cref="BiquadFilterEffect" /> already has
///         the seven shapes, the cookbook coefficients and the per-channel state; a three-band EQ is
///         three of them run one after another, and writing a separate multi-band filter would be the
///         same arithmetic with a second place for a sign error to live.
///     </para>
///     <para>
///         The usual arrangement is a high-pass to get rid of rumble, one or two peaking bands to fix
///         whatever the material is doing, and a shelf at the top. That is three or four biquads,
///         about twenty multiplies a sample a channel, and it is cheap enough to leave on a bus for
///         the whole game.
///     </para>
///     <para>
///         <b>Order matters and is the order the bands were added.</b> Filters do not commute in
///         floating point — the difference is small and it is not zero — so a preset that is reloaded
///         reproduces the same signal only if the bands come back in the same order.
///     </para>
/// </remarks>
public sealed class EqualizerEffect : IAudioEffect {
    static readonly BiquadFilterEffect[] NoBands = [];

    BiquadFilterEffect[] bands = NoBands;
    AudioFormat prepared;
    int preparedFrames;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>The bands, in the order they run.</summary>
    /// <remarks>Each is an ordinary filter, so its frequency and gain can be driven per frame.</remarks>
    public IReadOnlyList<BiquadFilterEffect> Bands => bands;

    /// <summary>Adds a band to the end of the chain.</summary>
    /// <param name="kind">Which shape.</param>
    /// <param name="frequency">Its cutoff or centre, in hertz.</param>
    /// <param name="q">Its resonance, or for a peaking band, how narrow it is.</param>
    /// <param name="gainDb">How much to boost or cut, for the peaking and shelf shapes.</param>
    /// <returns>The band, so it can be adjusted later.</returns>
    public BiquadFilterEffect AddBand(
        BiquadFilterKind kind,
        float frequency,
        float q = 0.70710678f,
        float gainDb = 0f
    ) {
        var band = new BiquadFilterEffect {
            Kind = kind,
            Frequency = frequency,
            Q = q,
            GainDb = gainDb
        };

        if (prepared.IsValid) {
            band.Prepare(prepared, preparedFrames);
        }

        bands = [.. bands, band];
        return band;
    }

    /// <summary>Takes a band off.</summary>
    /// <param name="band">The band.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveBand(BiquadFilterEffect band) {
        var index = Array.IndexOf(bands, band);

        if (index < 0) {
            return false;
        }

        var replacement = new BiquadFilterEffect[bands.Length - 1];
        Array.Copy(bands, replacement, index);
        Array.Copy(bands, index + 1, replacement, index, bands.Length - index - 1);
        bands = replacement;
        return true;
    }

    /// <inheritdoc />
    public void Prepare(in AudioFormat format, int maxFrames) {
        prepared = format;
        preparedFrames = maxFrames;

        foreach (var band in bands) {
            band.Prepare(format, maxFrames);
        }
    }

    /// <inheritdoc />
    public void Process(Span<float> buffer, int frameCount, int channels) {
        if (!Enabled) {
            return;
        }

        foreach (var band in bands) {
            band.Process(buffer, frameCount, channels);
        }
    }

    /// <inheritdoc />
    public void Reset() {
        foreach (var band in bands) {
            band.Reset();
        }
    }
}
