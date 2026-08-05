// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Water;

/// <summary>A sea state as a file: what a <c>.vxwaves</c> holds.</summary>
/// <remarks>
///     <para>
///         <b>The one new asset kind
///         [35 § D6](../../docs/plan/35-water.md#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline)
///         admits, and the reason it is admitted is sharing.</b> Every other water thing a scene
///         carries is per-body or per-zone, so it goes in a component and moves with the entity. A sea
///         state is neither: it is shared between every body in a region <em>and between levels</em>,
///         so a coastline authored across four streamed sublevels would otherwise hold four copies of
///         one wind and drift out of step the first time somebody edited three of them.
///     </para>
///     <para>
///         ⚠ <b>A wrapper around <see cref="WaterWaveSpectrum" /> rather than the spectrum itself, and
///         the wrapper is what carries the name.</b> A scene names this asset by name — see
///         <c>WaterZoneComponent.WaveAsset</c> for why a name and not a handle — so the file has to
///         hold one, and a bare spectrum has nowhere to put it.
///     </para>
///     <para>
///         ⚠ <b>It is deliberately not a list of waves.</b>
///         [§ D7](../../docs/plan/35-water.md#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic)
///         is explicit: wind, a spread, a wavelength range and a seed are the things somebody
///         describing a sea has words for, and thirty-two amplitudes and phases are not. The waves are
///         <em>derived</em>, on every host, from <see cref="WaterWaveSpectrum.Seed" /> — which is what
///         makes a client and a server agree about where a boat is.
///     </para>
/// </remarks>
[DataContract]
public sealed class WaterWavesAsset {
    /// <summary>What a sea state is called on disk.</summary>
    public const string Extension = ".vxwaves";

    /// <summary>What it is called, which is what a zone names it by.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The sea state itself.</summary>
    public WaterWaveSpectrum Spectrum { get; set; } = WaterWaveSpectrum.Default;

    /// <summary>Why this sea state cannot be summed, or <see langword="null" /> if it can.</summary>
    /// <remarks>
    ///     The spectrum's own rule and not a second one beside it. An importer that refused what the
    ///     evaluator accepts — or accepted what it refuses — is a file that passes the build and
    ///     produces a flat sea.
    /// </remarks>
    public string? Validate() => Spectrum.Validate();
}
