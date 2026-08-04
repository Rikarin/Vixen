// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>A ripple simulation's contribution, where one covers a place.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Separate from the wave sum in the evaluator's signature, and the asymmetry is the
///         point</b> —
///         [35 § D12](../../docs/plan/35-water.md#d12-ripples-are-a-sliding-window-height-field-and-they-are-displacement-not-geometry).
///         The closed-form part is exact and is held to a tolerance of exact-to-the-float; the
///         simulated part is a height field advanced a step at a time, is compared at a stated
///         non-exact tolerance, and cannot answer "where was the surface six ticks ago" at all.
///     </para>
///     <para>
///         So a caller that needs the exact one — the network path, rolling back six ticks — asks for
///         it alone, by passing nothing here. That is a decision the signature enforces rather than a
///         convention a comment asks for.
///     </para>
/// </remarks>
public interface IWaterRipples {
    /// <summary>How far this simulation lifts the surface at a place.</summary>
    /// <param name="position">Where, on the ground plane.</param>
    /// <param name="displacement">How far up, in metres. Zero where the window does not reach.</param>
    /// <returns>Whether the window covers the place at all.</returns>
    bool TryDisplacement(Vector2 position, out float displacement);
}

/// <summary>What the surface is like at one place and time.</summary>
/// <param name="Height">Where the surface is, in world units.</param>
/// <param name="Normal">Which way it faces. Unit length, and up where there is no water.</param>
/// <param name="Flow">How fast the water moves, in metres per second, on the ground plane.</param>
/// <param name="Depth">How deep the water is, in metres — surface minus ground.</param>
/// <param name="Coverage">Whether there is water here at all, 0…1.</param>
/// <remarks>
///     The one answer the vertex stage, the buoyancy solver and a gameplay query all get. That there
///     is exactly one of these is
///     [§ D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test), and it
///     is what makes a boat float at the height the water is drawn at.
/// </remarks>
public readonly record struct WaterSample(
    float Height,
    Vector3 Normal,
    Vector2 Flow,
    float Depth,
    float Coverage
) {
    /// <summary>Dry land.</summary>
    public static WaterSample None => new(0f, Vector3.UnitY, Vector2.Zero, 0f, 0f);

    /// <summary>Whether there is water here.</summary>
    public bool IsWet => Coverage > 0f;
}

/// <summary>How a sea state attenuates as the ground rises to meet it.</summary>
/// <param name="Depth">
///     The depth at which waves are at their full height. Shallower than this they are damped, and at
///     zero depth they are gone.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>In the evaluator and not in the material</b>
///         ([§ D7](../../docs/plan/35-water.md#d7-waves-are-a-spectrum-summed-as-gerstner-and-the-fft-is-deferred-with-arithmetic)).
///         A wave whose amplitude does not fall off as the ground rises produces a crest that
///         intersects the beach; every project that hits it solves the visible half in a material,
///         and then discovers that buoyancy is still using the unattenuated height and a boat in the
///         shallows is bobbing through the sand.
///     </para>
///     <para>
///         Unreal exposes the same number per body as <c>Wave Attenuation Water Depth</c>. It is here
///         instead because it belongs where everything else about the surface is.
///     </para>
/// </remarks>
public readonly record struct WaterAttenuation(float Depth) {
    /// <summary>Full height by two metres of depth, which is a beach rather than a harbour wall.</summary>
    public static WaterAttenuation Default => new(2f);

    /// <summary>How much of its amplitude a wave keeps at a depth, 0…1.</summary>
    /// <param name="depth">How deep the water is, in metres.</param>
    /// <returns>The factor.</returns>
    /// <remarks>
    ///     ⚠ <b>Smooth at both ends, and exactly zero at zero.</b> A linear ramp meets the full-height
    ///     water at a crease that runs along the whole shoreline and catches the light, and a ramp
    ///     that reaches zero only asymptotically leaves a millimetre of swell lapping over dry sand.
    /// </remarks>
    public float At(float depth) => WaterMath.SmoothStep(0f, MathF.Max(Depth, 0f), depth);
}

/// <summary>
///     The one definition of where the surface is.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test),
///         which is the decision the rest of the document hangs off.</b> The surface height at a
///         position is arithmetic over the field's surface channel, the Gerstner sum at that position
///         and time attenuated by the local depth, and the ripple field's displacement where a
///         simulation covers it. That arithmetic exists in exactly two places — here and in
///         <c>Raven/Library/Water/Surface.rvn</c> — and the two are held together by a seam test with
///         a stated tolerance of exact-to-the-float.
///     </para>
///     <para>
///         ⚠ <b>Why that is worth a test rather than a convention.</b> Both references evaluate the
///         surface twice and neither pins them together, and the symptoms are the reason people
///         believe water is hard: a boat that hovers a hand's width above the crests in a swell, a
///         character whose swimming state flickers at the shoreline, a buoy that sinks when the frame
///         rate drops. Unreal ships a per-body <c>Max Wave Height Offset</c> to correct exactly this
///         drift — a knob whose existence is a bug report.
///     </para>
///     <para>
///         <b>Time is a water time, not a world time.</b> Every entry point takes an explicit
///         <c>waterTime</c>, and the fixed-step simulation and the render both pass a value derived
///         from the same clock — the render interpolating within the step. A buoyancy solver reading
///         the frame's total time and a shader reading a smoothed one is the drift, and it is
///         invisible until the frame rate changes.
///     </para>
///     <para>
///         <b>A struct over borrowed arrays, so a hundred pontoons and forty swimmers cost nothing to
///         ask.</b> Nothing here allocates, and the tests say so.
///     </para>
///     <para>
///         ⚠ <b>The Gerstner phase is evaluated at the query position rather than at the position the
///         surface point came from, and that is a stated approximation.</b> A Gerstner wave displaces
///         horizontally as well as vertically, so the point that ends up above <c>p</c> did not start
///         there; inverting that is a fixed-point iteration. It is not done, for the reason everything
///         else here is done the way it is — an iteration count is a second thing the two hosts would
///         have to keep in step, and the seam is worth more than the last few centimetres of a steep
///         crest. The consequence is named: at high steepness a float measures the surface slightly
///         late on the windward face of a crest, identically on both sides of the seam.
///     </para>
/// </remarks>
public readonly ref struct WaterEvaluator {
    readonly ReadOnlySpan<GerstnerWave> waves;

    /// <summary>Creates one over a field and a sea state.</summary>
    /// <param name="field">The rasterised bodies and the ground beneath them.</param>
    /// <param name="waves">The sea state, already summed from its spectrum.</param>
    /// <param name="attenuation">How the sea state falls off as the ground rises.</param>
    public WaterEvaluator(WaterField? field, ReadOnlySpan<GerstnerWave> waves, WaterAttenuation attenuation) {
        Field = field;
        this.waves = waves;
        Attenuation = attenuation;
    }

    /// <summary>The field this reads bodies and ground from, or null for open water everywhere.</summary>
    public WaterField? Field { get; }

    /// <summary>How the sea state falls off as the ground rises.</summary>
    public WaterAttenuation Attenuation { get; }

    /// <summary>How many waves the sum runs over.</summary>
    public int WaveCount => waves.Length;

    /// <summary>What the surface is like at a place and a time.</summary>
    /// <param name="position">Where, on the ground plane — world X and Z.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <param name="ripples">
    ///     A simulation to add, or <see langword="null" /> for the closed-form surface alone — which
    ///     is what a rollback asks for. See <see cref="IWaterRipples" />.
    /// </param>
    /// <returns>The sample.</returns>
    public WaterSample Sample(Vector2 position, float waterTime, IWaterRipples? ripples = null) {
        var basis = Field?.Sample(position) ?? WaterFieldSample.None;

        if (Field is not null && basis.Coverage <= 0f) {
            return WaterSample.None;
        }

        var depth = Field is null ? float.PositiveInfinity : basis.Depth;
        var damping = Field is null ? 1f : Attenuation.At(depth);

        Displace(position, waterTime, damping, out var offset, out var normal);

        var height = basis.SurfaceHeight + offset.Y;

        if (ripples is not null && ripples.TryDisplacement(position, out var lift)) {
            height += lift;
        }

        return new(
            height,
            normal,
            basis.Flow,
            Field is null ? depth : MathF.Max(height - basis.GroundHeight, 0f),
            Field is null ? 1f : basis.Coverage
        );
    }

    /// <summary>Where the surface is at a place and a time, and nothing else.</summary>
    /// <param name="position">Where, on the ground plane.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <param name="ripples">A simulation to add, or null for the closed form alone.</param>
    /// <returns>The height, in world units.</returns>
    /// <remarks>
    ///     The hot one. A pontoon wants a height and nothing else, and computing a normal it discards
    ///     is four multiplies per wave per pontoon per fixed step.
    /// </remarks>
    public float Height(Vector2 position, float waterTime, IWaterRipples? ripples = null) {
        var basis = Field?.Sample(position) ?? WaterFieldSample.None;

        if (Field is not null && basis.Coverage <= 0f) {
            return basis.GroundHeight;
        }

        var damping = Field is null ? 1f : Attenuation.At(basis.Depth);
        var height = basis.SurfaceHeight + Lift(position, waterTime, damping);

        if (ripples is not null && ripples.TryDisplacement(position, out var lift)) {
            height += lift;
        }

        return height;
    }

    /// <summary>
    ///     The full Gerstner displacement of a point, and the surface normal there.
    /// </summary>
    /// <param name="position">Where, on the ground plane.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <param name="damping">How much of the sea state survives here, 0…1.</param>
    /// <param name="offset">How far the point moves, in all three axes.</param>
    /// <param name="normal">Which way the surface faces there. Unit length.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The whole of the wave model, and the only place it exists on this side of the
    ///         seam.</b> The horizontal terms are what make a Gerstner wave read as water rather than
    ///         as corrugated iron: crests sharpen and troughs broaden because the surface bunches
    ///         toward the crest.
    ///     </para>
    ///     <para>
    ///         The normal is analytic — the cross product of the two tangents, differentiated in
    ///         closed form — rather than sampled from neighbours. A finite difference needs a step
    ///         size, a step size is a number the two hosts would have to agree about, and a normal
    ///         that disagrees is a specular highlight that crawls.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every trigonometric call goes through <see cref="WaterMath.SinCos" />.</b> See
    ///         there for why the intrinsic is not used, and what the seam test's stated tolerance
    ///         depends on.
    ///     </para>
    /// </remarks>
    public void Displace(Vector2 position, float waterTime, float damping, out Vector3 offset, out Vector3 normal) {
        // Scalars rather than vectors, because Vector3's components are readonly — and because the
        // Raven side accumulates the same nine numbers in the same order, which is what the seam's
        // stated tolerance rests on.
        float offsetX = 0f, offsetY = 0f, offsetZ = 0f;

        // The Jacobian of the displacement, accumulated as the sum runs: dP/dx and dP/dz. Starting
        // from the identity, because an undisplaced plane's tangents are the axes themselves.
        float dxX = 1f, dxY = 0f, dxZ = 0f;
        float dzX = 0f, dzY = 0f, dzZ = 1f;

        foreach (var wave in waves) {
            var amplitude = wave.Amplitude * damping;

            if (amplitude == 0f || wave.Wavelength <= 0f) {
                continue;
            }

            var k = WaterMath.Tau / wave.Wavelength;
            var speed = MathF.Sqrt(GerstnerWave.Gravity * k);
            var phase = (k * ((wave.Direction.X * position.X) + (wave.Direction.Y * position.Y)))
                + (speed * waterTime)
                + wave.Phase;

            WaterMath.SinCos(phase, out var sin, out var cos);

            // Horizontal bunching toward the crest. Steepness is already the per-wave share — see
            // WaterWaveSpectrum.Generate — so the sum of them is what the author asked for.
            var lateral = wave.Steepness * amplitude;

            offsetX += wave.Direction.X * lateral * cos;
            offsetY += amplitude * sin;
            offsetZ += wave.Direction.Y * lateral * cos;

            var kSin = k * sin;
            var kCos = k * cos;

            dxX -= wave.Direction.X * wave.Direction.X * lateral * kSin;
            dxY += wave.Direction.X * amplitude * kCos;
            dxZ -= wave.Direction.X * wave.Direction.Y * lateral * kSin;

            dzX -= wave.Direction.Y * wave.Direction.X * lateral * kSin;
            dzY += wave.Direction.Y * amplitude * kCos;
            dzZ -= wave.Direction.Y * wave.Direction.Y * lateral * kSin;
        }

        offset = new(offsetX, offsetY, offsetZ);

        var crossed = Vector3.Cross(new(dzX, dzY, dzZ), new(dxX, dxY, dxZ));
        var length = crossed.Length();

        // ⚠ A degenerate cross product answers up rather than a NaN. It happens where the surface has
        // folded through itself — steepness above one — and a NaN normal reaches the lighting as a
        // black pixel that persists for as long as the crest does.
        normal = length > 0f ? crossed / length : Vector3.UnitY;

        if (normal.Y < 0f) {
            normal = -normal;
        }
    }

    /// <summary>How far up the wave sum lifts a point, without the normal.</summary>
    /// <param name="position">Where, on the ground plane.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <param name="damping">How much of the sea state survives here, 0…1.</param>
    /// <returns>The lift, in metres.</returns>
    public float Lift(Vector2 position, float waterTime, float damping) {
        var height = 0f;

        foreach (var wave in waves) {
            var amplitude = wave.Amplitude * damping;

            if (amplitude == 0f || wave.Wavelength <= 0f) {
                continue;
            }

            var k = WaterMath.Tau / wave.Wavelength;
            var speed = MathF.Sqrt(GerstnerWave.Gravity * k);
            var phase = (k * ((wave.Direction.X * position.X) + (wave.Direction.Y * position.Y)))
                + (speed * waterTime)
                + wave.Phase;

            height += amplitude * WaterMath.Sin(phase);
        }

        return height;
    }

    /// <summary>How much of a capsule is under the surface, 0…1.</summary>
    /// <param name="position">Where it stands, on the ground plane.</param>
    /// <param name="bottom">Where its lowest point is, in world units.</param>
    /// <param name="height">How tall it is, in metres.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <param name="ripples">A simulation to add, or null for the closed form alone.</param>
    /// <returns>The fraction submerged.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The only new number a swimming character needs</b>
    ///         ([§ D11](../../docs/plan/35-water.md#d11-swimming-is-a-fourth-move-mode-and-immersion-is-the-only-new-number)).
    ///         Every rule about wading, swimming and climbing out is a threshold on this, which is why
    ///         it is one function here rather than four in a movement mode.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Monotone in the capsule's height by construction</b>, which the property tests
    ///         assert. A non-monotone immersion is a character that becomes <em>less</em> submerged by
    ///         sinking, and the movement mode's hysteresis cannot save a threshold whose input goes
    ///         backwards.
    ///     </para>
    /// </remarks>
    public float Immersion(
        Vector2 position,
        float bottom,
        float height,
        float waterTime,
        IWaterRipples? ripples = null
    ) {
        if (!(height > 0f)) {
            return 0f;
        }

        var surface = Sample(position, waterTime, ripples);

        if (!surface.IsWet) {
            return 0f;
        }

        return WaterMath.Saturate((surface.Height - bottom) / height);
    }
}
