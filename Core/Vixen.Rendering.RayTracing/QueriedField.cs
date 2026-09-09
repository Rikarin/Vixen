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
/// <param name="Normal">The committed triangle's geometric normal, facing the ray, or the up
///     vector for a miss.</param>
/// <param name="Primitive">Which triangle the query committed, as an index into the build's list,
///     or −1 for a miss.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The last two are what <c>RayQueryField.rvn</c> throws away one line after the
///         intrinsic answers them</b>, and this is the reference half of #1169. A hardware
///         <c>Trace</c> answers <c>(t, primitive, instance, hit)</c>; the shader keeps <c>t</c>,
///         builds a <c>DistanceFieldHit</c> that has nowhere to put the rest, and every consumer
///         then asks <c>GradientField(hit.position)</c> — a position, which names no triangle. So
///         the honest answer there is the up vector, and
///         <c>SurfaceRadiance(position, normal)</c> picks a card <em>by</em> normal: a constant
///         upward answer picks every horizontal card in the atlas whatever the surface is, which
///         reads as the surface cache being wrong rather than as a normal bug.
///     </para>
///     <para>
///         ⚠ <b>And the CPU half turned out to be smaller than #1169 describes.</b> The issue says
///         "the CPU pair can compute the geometric normal from the same triangle the BVH
///         committed"; <see cref="TriangleBvh.Trace" /> already computes it, already faces it at
///         the ray, and already returns the triangle index beside it — <c>QueriedField</c> was
///         discarding both while constructing this. Nothing was computed here; two fields were
///         carried.
///     </para>
///     <para>
///         Both, rather than only the normal the consumers want. The index is the <em>mechanism</em>
///         the device half has to use — a shader has no cross product to fall back on and must read
///         the vertex buffer the structure was built from — so a referee that carries only the
///         answer cannot referee the step that produces it.
///     </para>
/// </remarks>
public readonly record struct QueriedHit(
    bool Hit,
    float Distance,
    Vector3 Position,
    int Steps,
    Vector3 Normal,
    int Primitive
);

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
///         — and <c>GradientField(Vector3)</c> answers the up vector, which is
///         <c>NoDistanceField</c>'s answer too.
///     </para>
///     <para>
///         ⚠ <b>A <em>hit</em> is a different question, and this is where its reference now
///         lands.</b> A hit's true surface normal is the committed triangle's, and
///         <see cref="QueriedHit.Normal" /> carries it beside the
///         <see cref="QueriedHit.Primitive" /> a shader would have to read the vertex buffer with
///         — the two things <c>RayQueryField.rvn</c> discards one line after the intrinsic answers
///         them. <see cref="GradientField(in QueriedHit)" /> is the overload the shared protocol
///         has to grow, expressed here first because here it can be checked. #1169's device half —
///         a field on <c>DistanceFieldHit</c>, every <c>IDistanceFieldSource</c> filling it, and
///         the vertex and index buffers bound beside <c>sceneStructure</c> — is still owed, and
///         lands in the one place nothing in this repository can referee.
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
    /// <remarks>Static, with <see cref="GradientField(Vector3)" /> and <see cref="OcclusionField" />,
    ///     because these answers are the tracer kind's, not any one hierarchy's — every
    ///     <c>RayQueryField</c> answers them identically whatever was built.</remarks>
    public static float SampleField(Vector3 world) => Nothing;

    /// <summary>The distance gradient at a world position — the up vector, always.</summary>
    public static Vector3 GradientField(Vector3 world) => Up;

    /// <summary>The surface normal at a hit, which is the question a position cannot answer.</summary>
    /// <param name="hit">What <see cref="TraceField" /> returned.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The overload #1169 names as the alternative to widening the protocol</b>, and it
    ///         is here rather than only on the struct because it is the <em>shape</em> the shader
    ///         side has to grow: a consumer holding a hit asks the tracer, and the tracer answers
    ///         from whatever it committed. <see cref="GradientField(Vector3)" /> stays exactly as
    ///         wrong as it was, deliberately — a position names no triangle in either language, and
    ///         a method that guessed would be worse than one that says so.
    ///     </para>
    ///     <para>
    ///         A miss answers up, which is the same <c>NoDistanceField</c> answer the position form
    ///         gives: there is no surface, so there is no normal, and the caller's card lookup wants
    ///         a unit vector rather than a zero.
    ///     </para>
    /// </remarks>
    public static Vector3 GradientField(in QueriedHit hit) => hit.Hit ? hit.Normal : Up;

    /// <summary>What every point question answers when there is no surface to answer from.</summary>
    static Vector3 Up => new(0f, 1f, 0f);

    /// <summary>The trace, answered with a query: the nearest triangle within the budget.</summary>
    /// <param name="origin">Where the ray starts.</param>
    /// <param name="direction">Where it goes, normalised.</param>
    /// <param name="maxDistance">How far it looks.</param>
    /// <remarks>
    ///     ⚠ <b>The normal and the index are carried, not recomputed.</b>
    ///     <see cref="TriangleBvh.Trace" /> already crosses the committed triangle's edges and
    ///     already flips the result toward the ray; this used to construct its answer from the
    ///     distance alone and drop both, which is the same line
    ///     <c>RayQueryField.TraceField</c> drops <c>answer.y</c> and <c>answer.z</c> on.
    /// </remarks>
    public QueriedHit TraceField(Vector3 origin, Vector3 direction, float maxDistance) {
        var hit = bvh.Trace(origin, direction, maxDistance);

        return hit.Hit
            ? new(true, hit.Distance, origin + (direction * hit.Distance), 1, hit.Normal, hit.Triangle)
            : new(false, maxDistance, origin + (direction * maxDistance), 1, Up, -1);
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
