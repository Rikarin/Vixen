// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>
///     The surface, as everything outside this assembly asks about it.
/// </summary>
/// <remarks>
///     <para>
///         <b>One object a buoyancy solver, a movement mode and a gameplay script all hold.</b>
///         <see cref="WaterEvaluator" /> is a <c>ref struct</c> over borrowed spans — which is what
///         makes it free to construct and impossible to store — so this is the thing with a lifetime:
///         it owns the sea state, points at a field, and hands out an evaluator per call.
///     </para>
///     <para>
///         <b>This is what a dedicated server runs</b>
///         ([35 § D1](../../docs/plan/35-water.md#d1-three-assemblies-and-the-kernel-touches-no-device)).
///         A headless build has no device and still has to answer how deep the water is for every
///         swimming character and every boat it simulates, and
///         [§ D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)
///         makes that answer the same one the client draws.
///     </para>
///     <para>
///         ⚠ <b>Every entry point takes an explicit water time.</b> There is no clock in here and
///         there will not be one: the fixed step and the render pass values derived from the same
///         source, the render interpolating within the step, and a solver that reached for a frame
///         time would be a force that changes when the frame rate does — which in a networked game is
///         a client and a server disagreeing about where a boat is.
///     </para>
/// </remarks>
public sealed class WaterQuery {
    readonly GerstnerWave[] waves;
    int waveCount;

    /// <summary>Creates a query over a field and a sea state.</summary>
    /// <param name="field">The rasterised bodies and the ground beneath them, or null for open water.</param>
    /// <param name="spectrum">The sea state, which is summed into waves here.</param>
    /// <param name="attenuation">How the sea state falls off as the ground rises.</param>
    /// <exception cref="ArgumentException">The spectrum is not one that can be summed.</exception>
    public WaterQuery(
        WaterField? field,
        in WaterWaveSpectrum spectrum,
        WaterAttenuation attenuation = default
    ) {
        Field = field;
        Attenuation = attenuation.Depth > 0f ? attenuation : WaterAttenuation.Default;
        waves = new GerstnerWave[(int)WaterWaveCount.ThirtyTwo];

        SetSpectrum(spectrum);
    }

    /// <summary>The field this reads bodies and ground from, or null for open water everywhere.</summary>
    public WaterField? Field { get; set; }

    /// <summary>How the sea state falls off as the ground rises.</summary>
    public WaterAttenuation Attenuation { get; set; }

    /// <summary>The spectrum the waves were summed from.</summary>
    public WaterWaveSpectrum Spectrum { get; private set; }

    /// <summary>The sea state, as the evaluator reads it.</summary>
    public ReadOnlySpan<GerstnerWave> Waves => waves.AsSpan(0, waveCount);

    /// <summary>The tallest the surface can be above its rest height, in metres.</summary>
    /// <remarks>
    ///     What the node error metric, the far-mesh cut and the collision bounds are all sized from,
    ///     and what the zone panel shows an author who has just raised the wind — because that number
    ///     decides all three and raising it from half a metre to four should cost something visible
    ///     before it costs frame time.
    /// </remarks>
    public float MaximumAmplitude => WaterWaveSpectrum.MaximumAmplitude(Waves);

    /// <summary>A simulation whose displacement is added, or null.</summary>
    /// <remarks>
    ///     ⚠ <b>Not consulted by <see cref="ClosedForm" />, which is the whole reason both exist.</b>
    ///     A rollback re-simulating six ticks needs the surface at six past times, and a height field
    ///     advanced a step at a time cannot answer that — so the network path asks for the closed form
    ///     alone and gets an answer that is exact and reproducible. See
    ///     [§ D12](../../docs/plan/35-water.md#d12-ripples-are-a-sliding-window-height-field-and-they-are-displacement-not-geometry).
    /// </remarks>
    public IWaterRipples? Ripples { get; set; }

    /// <summary>Re-sums the waves from a spectrum.</summary>
    /// <param name="spectrum">The sea state.</param>
    /// <exception cref="ArgumentException">The spectrum is not one that can be summed.</exception>
    /// <remarks>
    ///     Allocation-free after the first call: the buffer is the largest quantised count, so a sea
    ///     state changing from eight waves to thirty-two reuses it. An author dragging a wind slider
    ///     calls this once a frame.
    /// </remarks>
    public void SetSpectrum(in WaterWaveSpectrum spectrum) {
        if (spectrum.Validate() is { } why) {
            throw new ArgumentException(why, nameof(spectrum));
        }

        Spectrum = spectrum;
        waveCount = spectrum.Generate(waves);
    }

    /// <summary>What the surface is like at a place and a time.</summary>
    /// <param name="position">Where, on the ground plane — world X and Z.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <returns>The sample, with the ripple simulation included if there is one.</returns>
    public WaterSample Sample(Vector2 position, float waterTime) =>
        Evaluator().Sample(position, waterTime, Ripples);

    /// <summary>What the surface is like, ignoring any ripple simulation.</summary>
    /// <param name="position">Where, on the ground plane.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <returns>The sample.</returns>
    /// <remarks>
    ///     <b>What a rollback asks for.</b> It is a closed-form function of position and time, so the
    ///     surface six ticks ago costs exactly what the surface now costs — which is what makes a
    ///     predicted, rewound buoyancy force answerable at all, and is one of the two reasons
    ///     [§ D7](../../docs/plan/35-water.md#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic)
    ///     puts Gerstner before FFT.
    /// </remarks>
    public WaterSample ClosedForm(Vector2 position, float waterTime) =>
        Evaluator().Sample(position, waterTime);

    /// <summary>Where the surface is, and nothing else.</summary>
    /// <param name="position">Where, on the ground plane.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <returns>The height, in world units.</returns>
    public float Height(Vector2 position, float waterTime) =>
        Evaluator().Height(position, waterTime, Ripples);

    /// <summary>How much of a capsule is under the surface, 0…1.</summary>
    /// <param name="position">Where it stands, on the ground plane.</param>
    /// <param name="bottom">Where its lowest point is, in world units.</param>
    /// <param name="height">How tall it is, in metres.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <returns>The fraction submerged.</returns>
    public float Immersion(Vector2 position, float bottom, float height, float waterTime) =>
        Evaluator().Immersion(position, bottom, height, waterTime, Ripples);

    /// <summary>An evaluator over what this query holds.</summary>
    /// <returns>The evaluator, which borrows and does not own.</returns>
    /// <remarks>
    ///     For a caller with a batch — a hundred pontoons in one fixed step — so that the spans are
    ///     resolved once rather than per sample.
    /// </remarks>
    public WaterEvaluator Evaluator() => new(Field, Waves, Attenuation);
}
