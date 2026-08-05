// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>Where a query's field comes from, when the field itself can be replaced.</summary>
/// <remarks>
///     ⚠ <b>A field is not always the same object for the query's whole life.</b> A zone reshaped to
///     a new resolution builds a new <see cref="WaterField" /> — the old arrays cannot hold a window
///     with a different texel count — and a query holding the field object itself would keep
///     answering from the dead one forever. A query holding a source instead reads whichever field
///     is live at the moment it is asked, which is the liveness <see cref="WaterZoneState.Query" />
///     promises.
/// </remarks>
public interface IWaterFieldSource {
    /// <summary>The field that is live right now, or null before the first rasterisation.</summary>
    WaterField? Field { get; }
}

/// <summary>Every zone's water, as a simulation asks about it.</summary>
/// <remarks>
///     <para>
///         <b>The seam that lets the physics join exist at all</b> —
///         [35 § D1](../../docs/plan/35-water.md#d1-three-assemblies-and-the-kernel-touches-no-device).
///         <c>Vixen.Water.Physics</c> is a separate small assembly precisely so that nothing linking
///         Jolt has to link a graphics device, and the thing that folds a world's zones —
///         <c>WaterZoneSystem</c> — lives in the renderer. Without a seam here, a buoyancy solver
///         would have to reference the renderer to find out where the water is, and a dedicated
///         server could not run it.
///     </para>
///     <para>
///         ⚠ <b>The clock is on this interface and it is read-only, which is the whole "one water
///         clock" rule made structural.</b> A solver that reached for its own frame time would be a
///         force that changes when the frame rate does, and a solver a frame behind the vertex stage
///         is a boat that hovers — invisible until the frame rate changes. Whoever implements this
///         owns the number; everybody else reads it.
///     </para>
/// </remarks>
public interface IWaterSurface {
    /// <summary>The simulation's water time, in seconds.</summary>
    float WaterTime { get; }

    /// <summary>The query covering a place, or <see langword="null" /> where no zone reaches.</summary>
    /// <param name="position">Where, on the ground plane — world X and Z.</param>
    /// <returns>The query, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>Null is dry, and it is not an error.</b> A boat outside every zone's window is a boat
    ///     on ground the water stack has not rasterised; the solver leaves it to gravity rather than
    ///     guessing, which is the same answer <c>WaterZoneSystem.ZonelessBodies</c> counts.
    /// </remarks>
    WaterQuery? QueryAt(Vector2 position);
}

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
    WaterField? standalone;
    WaterAttenuation attenuation;

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
        Attenuation = attenuation;
        waves = new GerstnerWave[(int)WaterWaveCount.ThirtyTwo];

        SetSpectrum(spectrum);
    }

    /// <summary>Where the field is read from when it can be replaced, or null to stand on <see cref="Field" /> alone.</summary>
    /// <remarks>
    ///     ⚠ <b>What makes a query survive a zone's reshape.</b> A resolution change is a new
    ///     <see cref="WaterField" /> object, so a query holding the field itself would read the dead
    ///     one forever. <see cref="WaterZoneState.Query" /> sets this to the state, and the query then
    ///     reads whatever field is live at the moment it is asked. Setting <see cref="Field" />
    ///     directly detaches it.
    /// </remarks>
    public IWaterFieldSource? FieldSource { get; set; }

    /// <summary>The field this reads bodies and ground from, or null for open water everywhere.</summary>
    /// <remarks>
    ///     Read through <see cref="FieldSource" /> when one is attached. Setting this detaches the
    ///     source: a caller assigning a field by hand has said which object it means.
    /// </remarks>
    public WaterField? Field {
        get => FieldSource is { } live ? live.Field : standalone;
        set {
            FieldSource = null;
            standalone = value;
        }
    }

    /// <summary>How the sea state falls off as the ground rises.</summary>
    /// <remarks>
    ///     ⚠ <b>A depth of zero or less takes <see cref="WaterAttenuation.Default" />, on every entry
    ///     path.</b> Zero is what a defaulted argument and a zeroed struct both hold, and an
    ///     attenuation of zero is a swell lapping over dry sand — the contract is that waves are gone
    ///     at zero depth. A sea state that genuinely wants no shallow-water damping states a small
    ///     positive depth rather than an unset one.
    /// </remarks>
    public WaterAttenuation Attenuation {
        get => attenuation;
        set => attenuation = value.Depth > 0f ? value : WaterAttenuation.Default;
    }

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
