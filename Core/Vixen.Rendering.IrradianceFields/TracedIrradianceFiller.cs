// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;

namespace Vixen.Rendering.IrradianceFields;

/// <summary>How many rays a probe casts, how far, and how fast it is allowed to change its mind.</summary>
public readonly record struct IrradianceFillSettings {
    /// <summary>The defaults: sixty-four rays, a hundred units, no hysteresis, no sun.</summary>
    public IrradianceFillSettings() { }

    /// <summary>How many directions each probe looks in.</summary>
    /// <remarks>
    ///     Sixty-four is enough for four coefficients. The projection is an integral against basis
    ///     functions that are constant and linear, and neither has any detail for more rays to
    ///     resolve — past a point the extra samples buy noise reduction and nothing else, which is
    ///     what the hysteresis is for and cheaper at.
    /// </remarks>
    public int RayCount { get; init; } = 64;

    /// <summary>How far a ray looks before deciding it hit nothing.</summary>
    public float MaxDistance { get; init; } = 100f;

    /// <summary>How much of the previous answer survives a fill, from zero to one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Zero — take the new answer whole — is the default because it is the one that can be
    ///         tested.</b> A single fill then has a closed form: sixty-four rays into a uniform
    ///         environment of radiance <i>L</i> give a probe that lights everything with exactly
    ///         <i>L</i>, and a blend against whatever was there before would hide a projection error
    ///         behind a convergence rate.
    ///     </para>
    ///     <para>
    ///         A filler running per frame wants something near nine-tenths. Sixty-four rays is a noisy
    ///         estimate of an integral over the sphere and the noise is different every frame; keeping
    ///         most of the previous answer averages it away over time, at the cost of lighting that
    ///         lags a light that moved. That trade is doc 19 § L2's "blended against the previous value
    ///         with a hysteresis term", and it is the whole reason a probe converges rather than
    ///         flickering.
    ///     </para>
    /// </remarks>
    public float Hysteresis { get; init; }

    /// <summary>Which way the directional light is, or nothing for a scene without one.</summary>
    /// <remarks>
    ///     Set it and each probe marches one more ray to fill <see cref="IrradianceProbe.SunShadow" />
    ///     — which is what doc 19 § 4 retires baked static shadow data in favour of.
    /// </remarks>
    public Vector3? SunDirection { get; init; }

    /// <summary>How sharp the sun's shadow is. Larger is sharper.</summary>
    public float SunSoftness { get; init; } = 8f;

    /// <summary>Throws if these settings cannot fill anything.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfLessThan(RayCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDistance);
        ArgumentOutOfRangeException.ThrowIfNegative(Hysteresis);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Hysteresis, 1f);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SunSoftness);
    }
}

/// <summary>Fills probes by marching a distance field, on the CPU.</summary>
/// <remarks>
///     <para>
///         <b>Doc 19 § L2's filler A, written where it can be checked.</b> The shipping version is a
///         compute shader that does N bricks a frame; this is the same arithmetic with nothing between
///         it and a closed form. That is the arrangement <c>DistanceFieldTracer</c> already has with
///         its Raven port, and it exists for the same reason: a shader can only be compared against
///         something, and this is the something.
///     </para>
///     <para>
///         <b>The directions are a Fibonacci spiral, not a random set.</b> Even coverage of the sphere,
///         no seed, and therefore two fills of one scene agree to the bit — which is what lets a test
///         assert an exact number rather than a tolerance around a stochastic one. It is the same
///         choice, for the same reason, as the sign vote in the distance-field bake.
///     </para>
///     <para>
///         <b>Validity comes from the field's sign, with the backface vote behind it — and which of
///         those two answers depends on how good the field is.</b> A negative sample <i>is</i>
///         "inside", so a probe the field calls solid is invalid before any ray is cast. Against an
///         <i>exact</i> field that is the end of it: sphere tracing stops where the field crosses zero
///         on the way down, and the gradient there always opposes the ray, so no ray can report a
///         backface. The vote — doc 19 § L2's stated mechanism — earns its place against a
///         <i>sampled</i> field, where an over-reported step lands past a thin wall and the surface it
///         then finds is seen from behind. The probe's own position says nothing about that, so
///         something else has to.
///     </para>
///     <para>
///         <b>The storage does not know this exists.</b> A brick filled here and a brick filled by an
///         offline cube capture are the same brick, which is the property doc 19 § 3 builds the whole
///         platform commitment on — and it is why this is a separate type reading the field through
///         its public surface rather than a method on it.
///     </para>
/// </remarks>
public sealed class TracedIrradianceFiller {
    readonly IDistanceField geometry;
    readonly IRadianceSource radiance;
    readonly Vector3[] directions;
    readonly float solidAngle;

    IrradianceBrickCursor cursor;
    IrradianceBrick[] taken = [];

    /// <summary>Builds a filler over a scene's geometry and its lighting.</summary>
    /// <param name="geometry">What the rays march through.</param>
    /// <param name="radiance">What they see when they stop.</param>
    /// <param name="options">How hard to trace. Omitted takes the defaults.</param>
    /// <exception cref="ArgumentNullException">There is no geometry or no lighting.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings are out of range.</exception>
    public TracedIrradianceFiller(
        IDistanceField geometry,
        IRadianceSource radiance,
        IrradianceFillSettings? options = null
    ) {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(radiance);

        Settings = options ?? new IrradianceFillSettings();
        Settings.Validate();

        this.geometry = geometry;
        this.radiance = radiance;

        directions = Sphere(Settings.RayCount);
        solidAngle = 4f * MathF.PI / Settings.RayCount;
    }

    /// <summary>How this filler was told to trace.</summary>
    public IrradianceFillSettings Settings { get; }

    /// <summary>Which indirection cell the next budgeted fill starts at.</summary>
    /// <remarks>
    ///     Public because it is the only way to see that a round-robin is making progress, and a
    ///     round-robin that silently stopped moving produces a field that is correct where it got to
    ///     and stale everywhere else — which looks like a lighting bug rather than a scheduling one.
    /// </remarks>
    public int Cursor => cursor.Position;

    /// <summary>Fills every brick of a field.</summary>
    /// <param name="field">The field to fill.</param>
    /// <returns>How many bricks were filled.</returns>
    /// <exception cref="ArgumentNullException">There is no field.</exception>
    /// <remarks>
    ///     Borders are not touched and are not filled — run <see cref="IrradianceField.Dilate" /> and
    ///     then <see cref="IrradianceField.SyncBorders" /> afterwards, in that order.
    /// </remarks>
    public int Fill(IrradianceField field) {
        ArgumentNullException.ThrowIfNull(field);

        return Fill(field, field.BrickCount);
    }

    /// <summary>Fills a bounded number of bricks, carrying on from where the last call stopped.</summary>
    /// <param name="field">The field to fill.</param>
    /// <param name="budget">How many bricks to fill.</param>
    /// <returns>How many were filled.</returns>
    /// <exception cref="ArgumentNullException">There is no field.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative budget.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Bricks, not probes, which is doc 19 § L2's "N bricks per frame round-robin".</b> A
    ///         brick is the unit because it is the unit of the pool and of a GPU dispatch — sixty-four
    ///         probes sharing one slot, filled together or not at all. Half a brick is a state nothing
    ///         downstream has a use for.
    ///     </para>
    ///     <para>
    ///         Which bricks, and in what order, is <see cref="IrradianceBrickCursor" />'s — because
    ///         the compute filler walks the same order and comparing the two is how that one is
    ///         checked at all.
    ///     </para>
    /// </remarks>
    public int Fill(IrradianceField field, int budget) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfNegative(budget);

        if (taken.Length < budget) {
            taken = new IrradianceBrick[budget];
        }

        var count = cursor.Take(field, taken.AsSpan(0, budget));

        for (var index = 0; index < count; index++) {
            var brick = taken[index];

            for (var z = 0; z < IrradianceBrickPool.BrickResolution; z++) {
                for (var y = 0; y < IrradianceBrickPool.BrickResolution; y++) {
                    for (var x = 0; x < IrradianceBrickPool.BrickResolution; x++) {
                        field.SetProbe(
                            brick,
                            x,
                            y,
                            z,
                            Trace(field.ProbePosition(brick, x, y, z), field.GetProbe(brick, x, y, z))
                        );
                    }
                }
            }
        }

        return count;
    }

    /// <summary>What one probe sees from where it stands.</summary>
    /// <param name="position">Where it stands.</param>
    /// <param name="previous">What it held, which the hysteresis blends against.</param>
    /// <returns>The probe.</returns>
    /// <remarks>
    ///     Exposed because it is the whole of the arithmetic and a test wants it without a field
    ///     around it — the closed-form checks are about one probe in a known environment, and a field
    ///     would only add addressing to something that is about projection.
    /// </remarks>
    public IrradianceProbe Trace(Vector3 position, IrradianceProbe previous) {
        var fresh = geometry.Sample(position) < 0f ? IrradianceProbe.Empty : Gathered(position);

        return Settings.Hysteresis > 0f
            ? IrradianceProbe.Lerp(previous, fresh, 1f - Settings.Hysteresis)
            : fresh;
    }

    /// <summary>Everything one probe's rays found.</summary>
    /// <param name="position">Where the probe stands.</param>
    /// <returns>The probe.</returns>
    IrradianceProbe Gathered(Vector3 position) {
        var trace = new DistanceFieldTraceSettings { MaxDistance = Settings.MaxDistance };
        var projection = SphericalHarmonicsL1.Zero;
        var backfaces = 0;

        foreach (var direction in directions) {
            var hit = DistanceFieldTracer.Trace(geometry, position, direction, trace);

            var arriving = hit.Hit
                ? radiance.Surface(hit.Position, hit.Normal, direction)
                : radiance.Sky(direction);

            // A surface seen from behind is a surface this probe is on the wrong side of. One such
            // ray means nothing; most of them mean the probe is buried, and that is the vote.
            if (hit.Hit && Vector3.Dot(hit.Normal, direction) > 0f) {
                backfaces++;
            }

            projection = projection.Accumulated(direction, arriving, solidAngle);
        }

        var validity = Math.Clamp(1f - ((float)backfaces / directions.Length), 0f, 1f);

        return new(projection, validity, Sun(position));
    }

    /// <summary>How much of the directional light reaches a probe.</summary>
    /// <param name="position">Where the probe stands.</param>
    /// <returns>One for lit, zero for shadowed, and the penumbra between.</returns>
    float Sun(Vector3 position) {
        if (Settings.SunDirection is not { } sun) {
            return 1f;
        }

        return DistanceFieldTracer.Shadow(
            geometry,
            position,
            sun,
            Settings.MaxDistance,
            Settings.SunSoftness,
            new DistanceFieldTraceSettings { MaxDistance = Settings.MaxDistance }
        );
    }

    /// <summary>Directions covering the sphere evenly, off a Fibonacci spiral.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The directions, normalised.</returns>
    /// <remarks>
    ///     No seed, so two fills of one scene are identical — and no direction in the set is
    ///     axis-aligned, which matters because a scene built of axis-aligned surfaces is exactly the
    ///     scene an axis-aligned ray grazes.
    /// </remarks>
    static Vector3[] Sphere(int count) {
        const float GoldenAngle = 2.399963f;

        var result = new Vector3[count];

        for (var index = 0; index < count; index++) {
            var z = 1f - (2f * (index + 0.5f) / count);
            var radius = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));
            var angle = index * GoldenAngle;

            result[index] = new(radius * MathF.Cos(angle), radius * MathF.Sin(angle), z);
        }

        return result;
    }
}
