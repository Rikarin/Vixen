// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;

namespace Vixen.Rendering.Reflections;

/// <summary>Reflections through the same tracer — doc 19 § L5, reference half.</summary>
/// <remarks>
///     <para>
///         <b>The layer that reuses everything below it, which is § L5's whole argument.</b> A
///         mirror ray marches L1's tracer — the same march every filler and gather here marches — and
///         a hit answers through <see cref="IRadianceSource" />, which is L4's seam: hand this the
///         surface cache's <c>SurfaceCacheRadiance</c> and a reflection carries direct light,
///         emissive and every bounce the radiosity folded in, with no code in this package knowing
///         the cache exists. A miss asks the <see cref="IReflectionFallback" />, which is where
///         doc 06's reflection probes plug in as the far field they are actually good at.
///     </para>
///     <para>
///         <b>Rough reflections read the field instead of tracing.</b> At and above
///         <see cref="RoughnessThreshold" /> the lobe is wide enough that one mirror ray is noise
///         rather than an answer, and the cosine-weighted average the irradiance field already
///         stores — evaluated about the <i>mirror direction</i> rather than the normal — is the
///         wide-lobe limit of the same integral, amortised across every rough surface in the scene.
///         That is Lumen's arrangement and doc 19 § L5's sentence, and it is a hard threshold here
///         because a hard threshold is a testable one: the blend band between the two answers is the
///         device half's concern, named below.
///     </para>
///     <para>
///         <b>The view convention is incident.</b> <c>view</c> points from the camera <i>toward</i>
///         the surface, so the mirror direction is the textbook <c>v − 2(v·n)n</c>. Handing this the
///         direction from the surface to the camera reflects the camera instead of the scene — the
///         picture looks plausible and is wrong everywhere, which is why the convention is stated on
///         the parameter and pinned by a closed form.
///     </para>
/// </remarks>
public sealed class TracedReflections(IDistanceField geometry, IRadianceSource radiance, IReflectionFallback fallback) {
    readonly IDistanceField geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
    readonly IRadianceSource radiance = radiance ?? throw new ArgumentNullException(nameof(radiance));
    readonly IReflectionFallback fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

    /// <summary>How far a mirror ray looks before deciding it escaped.</summary>
    public float MaxDistance { get; set; } = 100f;

    /// <summary>How far off its surface a mirror ray starts, in world units.</summary>
    /// <remarks>⚠ Zero is a mirror that reflects itself: the surface the ray leaves is at distance
    ///     nothing, the march's first sample is already under its threshold, and every reflection
    ///     comes back as the reflector's own colour. The same bias every shadow ray here carries,
    ///     for the same reason, and a test holds both behaviours so the why survives.</remarks>
    public float Bias { get; set; } = 0.01f;

    /// <summary>The roughness at and above which the field answers instead of the trace.</summary>
    /// <remarks>Half by default — past it a GGX lobe's width is most of the hemisphere, and one
    ///     mirror ray through it is a sample, not an estimate.</remarks>
    public float RoughnessThreshold { get; set; } = 0.5f;

    /// <summary>How far below the threshold the cross-fade starts, in roughness.</summary>
    /// <remarks>
    ///     Zero is the hard switch — the testable form, and the default. Nonzero fades the traced
    ///     answer into the field's over the band ending at the threshold, so a roughness map does
    ///     not draw the threshold as a line across a floor: at the band's midpoint the answer is
    ///     the midpoint, which is the closed form the band is held to. Inside the band both reads
    ///     run, which is the band's honest cost.
    /// </remarks>
    public float RoughnessBlend { get; set; }

    /// <summary>The irradiance field the rough path reads — doc 19 § L2's, when the scene has one.</summary>
    /// <remarks>Null sends rough reflections to the fallback whole. A field that does not cover the
    ///     position answers black, which is the field's own honesty convention — the gap should be
    ///     visible rather than quietly extrapolated.</remarks>
    public IrradianceField? Field { get; set; }

    /// <summary>What a surface reflects toward the camera.</summary>
    /// <param name="position">Where the surface is, in world space.</param>
    /// <param name="normal">Which way it faces, normalised.</param>
    /// <param name="view">From the camera toward the surface, normalised — incident, see the type
    ///     remarks for what the other convention silently does.</param>
    /// <param name="roughness">How rough the surface is, zero to one.</param>
    /// <returns>The radiance arriving along the reflection.</returns>
    public Vector3 Reflect(Vector3 position, Vector3 normal, Vector3 view, float roughness) {
        var mirror = Vector3.Normalize(view - (2f * Vector3.Dot(view, normal) * normal));

        // How much of the answer is the field's: zero below the band, one at the threshold, and the
        // ramp between. A zero-width band divides by nothing because the >= took the whole answer.
        var rough = 1f;

        if (roughness < RoughnessThreshold) {
            rough = RoughnessBlend > 0f
                ? Math.Clamp((roughness - (RoughnessThreshold - RoughnessBlend)) / RoughnessBlend, 0f, 1f)
                : 0f;
        }

        var wide = rough > 0f
            ? Field is { } field
                ? field.Irradiance(position, mirror)
                : fallback.Miss(position, mirror, roughness)
            : Vector3.Zero;

        if (rough >= 1f) {
            return wide;
        }

        var hit = DistanceFieldTracer.Trace(
            geometry,
            position + (normal * Bias),
            mirror,
            new DistanceFieldTraceSettings { MaxDistance = MaxDistance }
        );

        var sharp = hit.Hit
            ? radiance.Surface(hit.Position, hit.Normal, mirror)
            : fallback.Miss(position, mirror, roughness);

        return Vector3.Lerp(sharp, wide, rough);
    }
}
