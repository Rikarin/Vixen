// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.DistanceFields;

/// <summary>What a march found, or did not.</summary>
/// <param name="Hit">Whether it reached a surface.</param>
/// <param name="Distance">How far along the ray, when it did.</param>
/// <param name="Position">Where it stopped.</param>
/// <param name="Normal">The surface's normal there, from the field's gradient.</param>
/// <param name="Steps">How many samples it took, which is the cost and worth being able to see.</param>
public readonly record struct DistanceFieldHit(
    bool Hit,
    float Distance,
    Vector3 Position,
    Vector3 Normal,
    int Steps
);

/// <summary>How hard to march, and when to stop.</summary>
/// <remarks>
///     <para>
///         <b><see cref="StepScale" /> below one is not timidity, it is correctness.</b> Sphere
///         tracing is exact when the field is exact: a step of <i>d</i> can never cross a surface
///         that is <i>d</i> away. A sampled field is not exact — a trilinear interpolation
///         over-reports near a convex corner, which is precisely a step that crosses the surface and
///         a ray that comes out the other side. Scaling the step back down is what buys that margin,
///         and it is why the number lives here rather than being hard-coded at nine-tenths and
///         forgotten.
///     </para>
///     <para>
///         <see cref="SurfaceThreshold" /> should be a fraction of the field's cell: much smaller and
///         the march creeps toward a surface it cannot resolve anyway; much larger and it stops
///         short, which reads as everything being slightly inflated.
///     </para>
/// </remarks>
public readonly record struct DistanceFieldTraceSettings {
    /// <summary>The defaults: a hundred units, a hundred and twenty-eight steps, nine-tenths of a step.</summary>
    public DistanceFieldTraceSettings() { }

    /// <summary>How far along the ray to look.</summary>
    public float MaxDistance { get; init; } = 100f;

    /// <summary>How many samples the march may take before giving up.</summary>
    /// <remarks>
    ///     A march that runs out of steps reports a miss. It is a miss in the only sense that
    ///     matters — nothing was found — and <see cref="DistanceFieldHit.Steps" /> is what says the
    ///     budget rather than the geometry ended it.
    /// </remarks>
    public int MaxSteps { get; init; } = 128;

    /// <summary>How near counts as arrived.</summary>
    public float SurfaceThreshold { get; init; } = 0.01f;

    /// <summary>What fraction of the reported distance each step actually takes.</summary>
    public float StepScale { get; init; } = 0.9f;

    /// <summary>The shortest step worth taking, so a march grazing a surface cannot stall.</summary>
    public float MinStep { get; init; } = 1e-4f;

    /// <summary>Where along the ray to begin.</summary>
    /// <remarks>
    ///     Non-zero for a ray leaving a surface, which would otherwise report an immediate hit on the
    ///     surface it started from. Shadow rays are the case, and it is the same bias a shadow map
    ///     needs for the same reason.
    /// </remarks>
    public float StartDistance { get; init; }

    /// <summary>Throws if these settings cannot march.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDistance);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSteps, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SurfaceThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(StepScale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(StepScale, 1f);
        ArgumentOutOfRangeException.ThrowIfNegative(StartDistance);
    }
}

/// <summary>Sphere tracing, and the two things everybody wants from it.</summary>
/// <remarks>
///     <para>
///         The CPU half of what a shader will do per pixel. It exists here first because it can be
///         checked: marching an analytic sphere has a closed-form answer, and a tracer that agrees
///         with arithmetic is a tracer whose port can be compared against something.
///     </para>
///     <para>
///         All three routines are the published formulations — sphere tracing from Hart, and the cone
///         shadow and the occlusion integral in the shape Quilez popularised. Re-derived and
///         credited, not copied.
///     </para>
/// </remarks>
public static class DistanceFieldTracer {
    /// <summary>Marches a ray until it reaches a surface or runs out of road.</summary>
    /// <param name="field">What to march through.</param>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">Which way it goes. Normalised for you, because a distance along a ray
    ///     of any other length is not a distance.</param>
    /// <param name="options">How hard to march. Omitted takes the defaults.</param>
    /// <returns>What it found.</returns>
    /// <exception cref="ArgumentNullException">There is no field.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings are out of range.</exception>
    /// <remarks>
    ///     A ray that starts inside geometry reports a hit at once, at its own origin. The
    ///     alternative — marching out of the solid it began in — answers a question nobody asked, and
    ///     the caller cannot tell the two apart afterwards unless this one is chosen.
    /// </remarks>
    public static DistanceFieldHit Trace(
        IDistanceField field,
        Vector3 origin,
        Vector3 direction,
        DistanceFieldTraceSettings? options = null
    ) {
        ArgumentNullException.ThrowIfNull(field);
        var settings = options ?? new DistanceFieldTraceSettings();
        settings.Validate();

        var ray = Vector3.Normalize(direction);
        var travelled = settings.StartDistance;

        for (var step = 0; step < settings.MaxSteps; step++) {
            var position = origin + (ray * travelled);
            var distance = field.Sample(position);

            if (distance < settings.SurfaceThreshold) {
                return new(true, travelled, position, field.SampleGradient(position), step + 1);
            }

            travelled += MathF.Max(distance * settings.StepScale, settings.MinStep);

            if (travelled > settings.MaxDistance) {
                return new(false, settings.MaxDistance, origin + (ray * settings.MaxDistance), Vector3.Zero, step + 1);
            }
        }

        return new(false, travelled, origin + (ray * travelled), Vector3.Zero, settings.MaxSteps);
    }

    /// <summary>How much of a light reaches a point, marching once for a whole penumbra.</summary>
    /// <param name="field">What to march through.</param>
    /// <param name="origin">The point being lit. Bias it off the surface with
    ///     <see cref="DistanceFieldTraceSettings.StartDistance" />.</param>
    /// <param name="toLight">Which way the light is. Normalised for you.</param>
    /// <param name="lightDistance">How far the light is, or
    ///     <see cref="DistanceFieldTraceSettings.MaxDistance" /> for a directional one.</param>
    /// <param name="softness">How wide the penumbra is. Larger is sharper; it is the reciprocal of
    ///     the light's angular radius.</param>
    /// <param name="options">How hard to march. Omitted takes the defaults.</param>
    /// <returns>One for lit, zero for fully shadowed, and the penumbra in between.</returns>
    /// <exception cref="ArgumentNullException">There is no field.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The settings are out of range.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>This is the trick that makes distance fields worth having for shadows.</b> A shadow
    ///         map or a ray cast answers one binary question and needs many samples to soften it.
    ///         Here the field already knows how close the ray passed to something: at distance
    ///         <i>t</i> along the ray, a clearance of <i>d</i> means the occluder subtends roughly
    ///         <i>d/t</i>, and the smallest such ratio over the whole march <b>is</b> the penumbra.
    ///         One march, a soft shadow, and the softness grows with distance from the occluder for
    ///         free — which is the thing that actually reads as real.
    ///     </para>
    ///     <para>
    ///         The march stops at zero the moment it touches something, because nothing further along
    ///         can make a fully blocked ray less blocked.
    ///     </para>
    /// </remarks>
    public static float Shadow(
        IDistanceField field,
        Vector3 origin,
        Vector3 toLight,
        float lightDistance,
        float softness = 8f,
        DistanceFieldTraceSettings? options = null
    ) {
        ArgumentNullException.ThrowIfNull(field);
        var settings = options ?? new DistanceFieldTraceSettings();
        settings.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(softness);

        var ray = Vector3.Normalize(toLight);
        var reach = MathF.Min(lightDistance, settings.MaxDistance);
        var travelled = MathF.Max(settings.StartDistance, settings.MinStep);
        var light = 1f;

        for (var step = 0; step < settings.MaxSteps && travelled < reach; step++) {
            var distance = field.Sample(origin + (ray * travelled));

            if (distance < settings.SurfaceThreshold) {
                return 0f;
            }

            light = MathF.Min(light, softness * distance / travelled);
            travelled += MathF.Max(distance * settings.StepScale, settings.MinStep);
        }

        return Math.Clamp(light, 0f, 1f);
    }

    /// <summary>How enclosed a point is, from a few samples up its own normal.</summary>
    /// <param name="field">What to sample.</param>
    /// <param name="position">The point, on or near a surface.</param>
    /// <param name="normal">Which way is out. Normalised for you.</param>
    /// <param name="radius">How far up the normal to look.</param>
    /// <param name="samples">How many steps to take along it.</param>
    /// <param name="strength">How dark the occlusion goes.</param>
    /// <param name="cell">
    ///     The field's own resolution, in world units — a cell of the sampled volume, and where the
    ///     first shell stands. Zero, the default, spaces the shells evenly instead, which is right
    ///     for an analytic field that resolves everything.
    /// </param>
    /// <returns>One for open, toward zero for enclosed.</returns>
    /// <exception cref="ArgumentNullException">There is no field.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <remarks>
    ///     <para>
    ///         Above open ground the field a metre up reads a metre, and the difference is nothing.
    ///         In a corner it reads less than a metre — something else is nearer than the floor —
    ///         and that shortfall <i>is</i> the occlusion. No hemisphere, no rays, no noise to
    ///         denoise, and it costs one sample per step.
    ///     </para>
    ///     <para>
    ///         Each shell is normalised to its own height — "what fraction of this distance is
    ///         blocked" — and each counts for less than the one before it, because a surface a long
    ///         way up the normal occludes a smaller part of the hemisphere than one against the
    ///         point. Without that falloff a distant wall darkens a floor as much as a near one
    ///         does. <c>DistanceField.Occlusion</c> in the shader library is this arithmetic
    ///         exactly, and the two are kept in lockstep.
    ///     </para>
    ///     <para>
    ///         With a <paramref name="cell" />, the first shell stands one cell out and the rest
    ///         spread evenly to the radius. A first shell inside the cell reads the surface's own
    ///         interpolation and measures nothing; one several cells out never measures the corner
    ///         the point actually touches.
    ///     </para>
    ///     <para>
    ///         This is large-scale occlusion, not a replacement for the screen-space kind: it sees
    ///         what the field sees, which is geometry at the field's own resolution and nothing
    ///         finer. The two answer different questions and a renderer wants both.
    ///     </para>
    /// </remarks>
    public static float AmbientOcclusion(
        IDistanceField field,
        Vector3 position,
        Vector3 normal,
        float radius = 1f,
        int samples = 5,
        float strength = 1f,
        float cell = 0f
    ) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(strength);
        ArgumentOutOfRangeException.ThrowIfNegative(cell);

        var up = Vector3.Normalize(normal);
        var first = cell > 0f ? MathF.Min(cell, radius) : radius / samples;
        var occlusion = 0f;
        var weight = 1f;
        var total = 0f;

        for (var sample = 1; sample <= samples; sample++) {
            var height = first + ((radius - first) * (sample - 1) / Math.Max(samples - 1, 1));
            var clearance = field.Sample(position + (up * height));

            // The fraction of this shell's own distance that is blocked, which is zero over open
            // ground and one against a surface — comparable across shells, where metres were not.
            occlusion += Math.Clamp(1f - (clearance / height), 0f, 1f) * weight;
            total += weight;
            weight = 1f / (1f + height);
        }

        return total > 0 ? Math.Clamp(1f - (strength * occlusion / total), 0f, 1f) : 1f;
    }
}
