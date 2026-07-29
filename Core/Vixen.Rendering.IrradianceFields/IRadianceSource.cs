// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.IrradianceFields;

/// <summary>What a ray found, expressed as light.</summary>
/// <remarks>
///     <para>
///         <b>A distance field says where the geometry is and nothing about what colour it is</b>, so a
///         filler that traces one has to ask somebody. This is who it asks, and separating it out is
///         what keeps the tracing honest: the filler is responsible for <i>which directions matter and
///         how much of the sphere each stands for</i>, and nothing else.
///     </para>
///     <para>
///         On a GPU the answer to <see cref="Surface" /> comes from Lumen's surface cache — the
///         thing that makes multi-bounce possible, and <c>docs/plan/19</c> § L4. Until that exists the
///         answer comes from whatever a caller can work out, which for a bootstrap pass is the
///         previous iteration's own irradiance times an albedo. That is filler B's iteration in
///         doc 19 § L2, and it is why this is an interface rather than a constant.
///     </para>
/// </remarks>
public interface IRadianceSource {
    /// <summary>What a ray that hit nothing sees.</summary>
    /// <param name="direction">Which way it went, normalised.</param>
    /// <returns>The radiance arriving from there.</returns>
    Vector3 Sky(Vector3 direction);

    /// <summary>What a ray that hit something sees.</summary>
    /// <param name="position">Where it hit.</param>
    /// <param name="normal">The surface's normal there.</param>
    /// <param name="direction">Which way the ray was going, normalised.</param>
    /// <returns>The radiance leaving that surface toward the probe.</returns>
    Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction);
}
