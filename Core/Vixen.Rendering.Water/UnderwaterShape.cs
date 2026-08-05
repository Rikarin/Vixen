// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Rendering.Ecs;
using Vixen.Water;

namespace Vixen.Rendering.Water;

/// <summary>
///     A zone's water, below its surface, as a post-process volume's containment test.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D9](../../docs/plan/35-water.md#d9-underwater-is-a-post-process-volume-and-the-waterline-is-named-as-the-hard-part),
///         and it costs the shape generalisation and nothing else.</b> [32](../../docs/plan/32-post-process-volumes.md)
///         built the optional-field settings, the priority, the blend radius and the per-frame fold;
///         underwater is a volume whose shape is <em>the water, below its surface</em>, and this is
///         that shape. Nothing about the volume path changes, which is what
///         [§ B2](../../docs/plan/35-water.md#b2-doc-32s-volumes-are-boxes)'s generalisation bought.
///     </para>
///     <para>
///         ⚠ <b>Per zone rather than per body, and that diverges from the reference deliberately.</b>
///         Unreal hangs an <c>UnderwaterPostProcessSettings</c> on each water body, so a river running
///         into a lake is two volumes an author has to keep in agreement and a camera at the mouth is
///         inside both. The zone's field has already resolved every body once — that is what § D3
///         exists for — so "am I under water" is one question about a field rather than N questions
///         about bodies, and a river mouth grades as one place because it <em>is</em> one place.
///     </para>
///     <para>
///         ⚠ <b>It reads the surface at the water time it was built with, waves included.</b> A test
///         against the rest height would put the boundary at the mean sea level, and a camera sitting
///         in a swell would cross it half a second before and after the water actually reached it.
///         Which is also why the shape is rebuilt per frame rather than cached: the surface moves.
///     </para>
///     <para>
///         <b>It does not answer the waterline.</b> A fold produces one weight for the whole frame,
///         and a camera straddling the surface needs two treatments divided by a curve — which is the
///         mask <c>!Water</c> writes in alpha, read per pixel. § D9 separates the two explicitly
///         because designing the volume path first and discovering the waterline second is how the
///         transition ends up a hard cut whose fix is architectural.
///     </para>
/// </remarks>
public sealed class UnderwaterShape : IPostProcessShape {
    readonly WaterZoneState state;
    readonly WaterWaveSpectrum spectrum;
    readonly WaterAttenuation attenuation;
    readonly float waterTime;
    readonly float feather;

    /// <summary>Builds the shape for one zone, at one water time.</summary>
    /// <param name="state">The zone, whose field says where the water is.</param>
    /// <param name="spectrum">The sea state, so the surface is where it is drawn.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <param name="attenuation">How the sea state falls off as the ground rises.</param>
    /// <param name="feather">How many metres of fade a shoreline's coverage ramp stands for.</param>
    /// <exception cref="ArgumentNullException"><paramref name="state" /> is null.</exception>
    public UnderwaterShape(
        WaterZoneState state,
        in WaterWaveSpectrum spectrum,
        float waterTime,
        WaterAttenuation attenuation = default,
        float feather = 2f
    ) {
        ArgumentNullException.ThrowIfNull(state);

        this.state = state;
        this.spectrum = spectrum;
        this.waterTime = waterTime;
        this.attenuation = attenuation.Depth > 0f ? attenuation : WaterAttenuation.Default;
        this.feather = MathF.Max(feather, 0f);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         Two distances, and the answer is the larger — which is what makes the fade correct at a
    ///         shoreline seen from above the water. A camera a metre above the surface and a metre
    ///         outside the lake is two metres from being underwater, not one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The lateral distance is derived from coverage rather than measured against the
    ///         bodies, and the conversion is the feather the shape was built with.</b> The field carries how
    ///         much water is at a place and not how far the nearest shoreline is; asking the bodies
    ///         would be a second distance query per volume per frame, and it would disagree with the
    ///         field at exactly the texel resolution the shoreline was resolved at. So a coverage of
    ///         zero reads as one feather outside, which is a fade rather than a cut and is honest
    ///         about being an approximation.
    ///     </para>
    /// </remarks>
    public bool Contains(Vector3 world, out float distanceOutside) {
        distanceOutside = float.MaxValue;

        if (state.Field is null) {
            return false;
        }

        var query = state.Query(spectrum, attenuation);
        var surface = query.Sample(new(world.X, world.Z), waterTime);

        if (!surface.IsWet) {
            return false;
        }

        // Above the surface is outside by how far above; below it is inside.
        var vertical = world.Y - surface.Height;

        // And outside the water laterally by however much coverage is missing.
        var lateral = (1f - Math.Clamp(surface.Coverage, 0f, 1f)) * feather;

        distanceOutside = MathF.Max(MathF.Max(vertical, lateral), 0f);

        return distanceOutside <= 0f;
    }
}
