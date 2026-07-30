// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.IrradianceFields;

namespace Vixen.Rendering.Reflections;

/// <summary>What a reflection ray that found nothing should see.</summary>
/// <remarks>
///     <para>
///         <b>This seam is where doc 06's reflection probes plug in.</b> Their row carries "⚠
///         blended against the sky rather than against a second probe" — a probe fades out over its
///         blend distance and there is nothing behind it but sky. Traced reflections invert the
///         arrangement: the trace answers the near field, and the probe becomes what a <i>miss</i>
///         sees — the far field it is actually good at — which retires the caveat the day the
///         device half hands a probe through this interface.
///     </para>
///     <para>
///         An interface rather than the probe type itself, because the probes live above this
///         package with the devices — the same seam <c>IRadianceSource</c> is, one layer up.
///     </para>
/// </remarks>
public interface IReflectionFallback {
    /// <summary>What a reflection ray that escaped the scene sees.</summary>
    /// <param name="position">Where the reflecting surface is, in world space — a probe selector's
    ///     input, which is why a miss carries it.</param>
    /// <param name="direction">Which way the ray went, normalised.</param>
    /// <param name="roughness">How rough the reflecting surface is, zero to one — a prefiltered
    ///     probe picks its mip by it.</param>
    /// <returns>The radiance arriving from there.</returns>
    Vector3 Miss(Vector3 position, Vector3 direction, float roughness);
}

/// <summary>The sky as the miss answer — a project with no probes, honestly.</summary>
/// <remarks>Exactly what every reflection in doc 06 sees beyond the probes today, so composing the
///     tracer over an existing scene with this fallback changes only what the trace hits.</remarks>
public sealed class SkyFallback(IRadianceSource source) : IReflectionFallback {
    readonly IRadianceSource source = source ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc />
    public Vector3 Miss(Vector3 position, Vector3 direction, float roughness) => source.Sky(direction);
}
