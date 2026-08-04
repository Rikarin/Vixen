// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.RayTracing;

/// <summary>One answer of a <see cref="QueriedField" /> trace — <c>DistanceFieldHit</c>, by shape.</summary>
/// <param name="Hit">Whether the query committed a triangle.</param>
/// <param name="Distance">How far along the ray it stands, or the budget for a miss.</param>
/// <param name="Position">Where — on the triangle, or at the budget's end.</param>
/// <param name="Steps">One. A query is one step, and the field's tracers report their cost, so
///     this reports its.</param>
public readonly record struct QueriedHit(bool Hit, float Distance, Vector3 Position, int Steps);

/// <summary>The hardware tracer's answers, written first and device-free — doc 19 § L6's referee
///     for <c>RayQueryField.rvn</c>.</summary>
/// <remarks>
///     <para>
///         <b>One class answering exactly what the shader answers</b>, the arrangement every
///         dispatch comparison here leans on: where the kernel opens a ray query against the
///         two-level structure, this asks the <see cref="TriangleBvh" /> built from the same
///         triangles — the traversal already held hit-for-hit against brute force — so a device
///         disagreement is the device's, not the fixture's.
///     </para>
///     <para>
///         <b>An acceleration structure holds surfaces, not distances</b>, and the honest answers
///         follow from that. The trace and the shadow are queries and exact. The point questions
///         are not askable: <c>SampleField</c> answers "nothing is near" — the same
///         <see cref="Nothing" /> <c>NoDistanceField</c> answers, a step any march may safely take
///         — and <c>GradientField</c> answers the up vector, which is <c>NoDistanceField</c>'s
///         answer too. A hit's true surface normal is the committed triangle's, reachable from the
///         primitive index the query already returns plus the very vertex buffer the structure was
///         built from; that read is owed, named in the package README, and this class is where its
///         reference will land.
///     </para>
/// </remarks>
public sealed class QueriedField {
    /// <summary>What the point questions answer — further than any march goes, by value the
    ///     library's <c>NoDistanceField.Nothing</c>.</summary>
    public const float Nothing = 1e8f;

    readonly TriangleBvh bvh;

    /// <summary>Wraps the hierarchy the queries walk.</summary>
    /// <param name="bvh">The triangles, built once.</param>
    /// <exception cref="ArgumentNullException">There is no hierarchy.</exception>
    public QueriedField(TriangleBvh bvh) {
        ArgumentNullException.ThrowIfNull(bvh);

        this.bvh = bvh;
    }

    /// <summary>The signed distance at a world position — "nothing is near", always.</summary>
    /// <remarks>Static, with <see cref="GradientField" /> and <see cref="OcclusionField" />,
    ///     because these answers are the tracer kind's, not any one hierarchy's — every
    ///     <c>RayQueryField</c> answers them identically whatever was built.</remarks>
    public static float SampleField(Vector3 world) => Nothing;

    /// <summary>The distance gradient at a world position — the up vector, always.</summary>
    public static Vector3 GradientField(Vector3 world) => new(0f, 1f, 0f);

    /// <summary>The trace, answered with a query: the nearest triangle within the budget.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">Where it goes, normalised.</param>
    /// <param name="maxDistance">How far it looks.</param>
    public QueriedHit TraceField(Vector3 origin, Vector3 direction, float maxDistance) {
        var hit = bvh.Trace(origin, direction, maxDistance);

        return hit.Hit
            ? new(true, hit.Distance, origin + (direction * hit.Distance), 1)
            : new(false, maxDistance, origin + (direction * maxDistance), 1);
    }

    /// <summary>How much of a light reaches a point: one or zero, from one query.</summary>
    /// <param name="origin">The lit point.</param>
    /// <param name="toLight">Toward the light, normalised.</param>
    /// <param name="lightDistance">How far the light stands.</param>
    /// <param name="bias">How far along the ray occlusion starts counting — the query's own
    ///     minimum distance, where the field form steps off the surface instead.</param>
    /// <remarks>
    ///     Hard, deliberately: a query answers <i>whether</i>, and the penumbra the field's shadow
    ///     derives from how near a march grazed is exactly the information a hierarchy of triangles
    ///     does not carry. Softness is therefore not a parameter here — a caller that wants both
    ///     composes both tracers, which is what the slots are for.
    /// </remarks>
    public float ShadowField(Vector3 origin, Vector3 toLight, float lightDistance, float bias) {
        // The query's minimum distance, spelled as an offset origin: hits at t ∈ [bias,
        // lightDistance] and nothing nearer — the same set the kernel's tMin selects.
        var hit = bvh.Trace(origin + (toLight * bias), toLight, lightDistance - bias);

        return hit.Hit ? 0f : 1f;
    }

    /// <summary>How enclosed a point is — fully open, always: enclosure is a distance question.</summary>
    public static float OcclusionField(Vector3 position, Vector3 normal) => 1f;
}
