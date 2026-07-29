// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.DistanceFields;

/// <summary>Anything a ray can be marched through.</summary>
/// <remarks>
///     <para>
///         Two things implement this and they are very different — one baked field over one mesh, and
///         a clipmap over a whole scene — but a tracer needs nothing from either beyond "how far is
///         the nearest surface from here, and which way does that grow". Naming that pair is what
///         lets <see cref="DistanceFieldTracer" /> be written once.
///     </para>
///     <para>
///         It also lets a test trace an <i>exact</i> field. Marching a sampled sphere and comparing
///         against the analytic answer measures the tracer and the sampling together, and when it
///         disagrees there is no way to tell which one was wrong. Marching an analytic sphere
///         measures the tracer alone.
///     </para>
///     <para>
///         <b>The contract is a lower bound, not an exact distance.</b> An implementation may report
///         less than the true distance to the nearest surface and a tracer will still be correct —
///         it takes more steps. It may <b>not</b> report more, because a step of that length passes
///         through the surface. Every approximation in this assembly is built to fail in the first
///         direction.
///     </para>
/// </remarks>
public interface IDistanceField {
    /// <summary>How far the nearest surface is, and which side of it the point is on.</summary>
    /// <param name="position">The point.</param>
    /// <returns>The distance, negative inside, never over-reported.</returns>
    float Sample(Vector3 position);

    /// <summary>Which way the distance grows fastest — the surface normal, near one.</summary>
    /// <param name="position">The point.</param>
    /// <returns>The normalised gradient, or zero where the field is flat.</returns>
    Vector3 SampleGradient(Vector3 position);
}
