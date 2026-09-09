// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.RayTracing.Tests;

/// <summary>The hardware tracer's answers, held to their closed forms.</summary>
public class QueriedFieldTests {
    [Fact]
    public void TheTraceIsTheQueryAndTheBudgetIsTheMiss() {
        var field = Wall(out _);

        // Through the wall at z = 2: a hit, at the plane, one step.
        var hit = field.TraceField(Vector3.Zero, new(0f, 0f, 1f), 10f);

        Assert.True(hit.Hit);
        Assert.Equal(2f, hit.Distance, 1e-5f);
        Assert.Equal(2f, hit.Position.Z, 1e-5f);
        Assert.Equal(1, hit.Steps);

        // A budget shorter than the wall is a miss that walked its whole road — the same answer
        // NoDistanceField gives, so a composed consumer cannot tell which said it.
        var miss = field.TraceField(Vector3.Zero, new(0f, 0f, 1f), 1.5f);

        Assert.False(miss.Hit);
        Assert.Equal(1.5f, miss.Distance, 1e-5f);
        Assert.Equal(1.5f, miss.Position.Z, 1e-5f);
    }

    [Fact]
    public void ThePointQuestionsAnswerWhatAStructureCanSay() {
        // Inside, outside, wherever: an acceleration structure holds surfaces, not distances, so
        // the point answers are NoDistanceField's — nothing near, up for a gradient, fully open.
        Assert.Equal(QueriedField.Nothing, QueriedField.SampleField(new(0f, 0f, 1.999f)));
        Assert.Equal(new Vector3(0f, 1f, 0f), QueriedField.GradientField(new(5f, -3f, 2f)));
        Assert.Equal(1f, QueriedField.OcclusionField(Vector3.Zero, Vector3.UnitY));
    }

    [Fact]
    public void TheShadowIsHardAndTheBiasIsTheMinimum() {
        var field = Wall(out _);

        // The wall stands between the point and a light six away: shadowed, whole.
        Assert.Equal(0f, field.ShadowField(Vector3.Zero, new(0f, 0f, 1f), 6f, 0.01f));

        // The light in front of the wall: lit, whole — the query stops at the light.
        Assert.Equal(1f, field.ShadowField(Vector3.Zero, new(0f, 0f, 1f), 1.5f, 0.01f));

        // A point ON the wall looking away from it: the bias steps the query past its own
        // surface, which is the whole reason the parameter exists.
        Assert.Equal(1f, field.ShadowField(new(0f, 0f, 2f), new(0f, 0f, 1f), 4f, 0.01f));
    }

    /// <summary>A hit carries the committed triangle and its geometric normal, facing the ray.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both were computed and both were dropped.</b> <see cref="TriangleBvh.Trace" />
    ///         already crosses the committed triangle's edges and already flips the answer toward the
    ///         ray; <c>QueriedField.TraceField</c> built its hit out of the distance alone. That is the
    ///         same discard <c>RayQueryField.rvn</c> makes on the device, where the intrinsic answers
    ///         <c>(t, primitive, instance, hit)</c> and the shader keeps only <c>t</c> — #1169.
    ///     </para>
    ///     <para>
    ///         The wall is a closed form and, deliberately, one whose normal is <em>perpendicular</em>
    ///         to the up vector the point question answers: an assertion against a horizontal floor
    ///         would pass whether the normal was carried or invented.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AHitCarriesTheCommittedTriangleAndItsNormal() {
        var field = Wall(out var bvh);
        var hit = field.TraceField(Vector3.Zero, new(0f, 0f, 1f), 10f);

        Assert.True(hit.Hit);

        // The one triangle in the build, not "some index": a −1 or a stale zero would be a plausible
        // number for a field that was never filled.
        Assert.Equal(1, bvh.TriangleCount);
        Assert.Equal(0, hit.Primitive);

        Assert.Equal(new Vector3(0f, 0f, -1f), hit.Normal);

        // And the reference is the hierarchy's own answer over the same ray, which is what makes this
        // a referee for the device rather than a second opinion.
        Assert.Equal(bvh.Trace(Vector3.Zero, new(0f, 0f, 1f), 10f).Normal, hit.Normal);
        Assert.Equal(bvh.Trace(Vector3.Zero, new(0f, 0f, 1f), 10f).Triangle, hit.Primitive);
    }

    /// <summary>Asking the hit is a different question from asking its position, and answers so.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The consequence is a wrong colour, not a rough one.</b>
    ///         <c>SurfaceRadiance(position, normal)</c> picks a surface-cache card <em>by</em> normal,
    ///         so the constant upward answer picks every horizontal card in the atlas whatever the
    ///         surface actually is — and it reads as the cache being wrong rather than as a normal
    ///         bug. This wall is vertical, so the two answers are ninety degrees apart and the
    ///         difference cannot be a rounding one.
    ///     </para>
    ///     <para>
    ///         The position form is deliberately left exactly as wrong as it was: a position names no
    ///         triangle in either language, and a method that guessed would be worse than one that
    ///         says so.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheGradientAtAHitIsTheSurfacesAndNotTheUpVector() {
        var field = Wall(out _);
        var hit = field.TraceField(Vector3.Zero, new(0f, 0f, 1f), 10f);

        Assert.Equal(new Vector3(0f, 0f, -1f), QueriedField.GradientField(hit));
        Assert.Equal(new Vector3(0f, 1f, 0f), QueriedField.GradientField(hit.Position));

        // Perpendicular, which is the strongest form the claim takes: the old answer is not a worse
        // approximation of this one, it is unrelated to it.
        Assert.Equal(0f, Vector3.Dot(QueriedField.GradientField(hit), QueriedField.GradientField(hit.Position)), 1e-6f);

        // A miss has no surface, so it has no normal — the up vector, which is what a composed
        // consumer already handles, rather than a zero it would normalise.
        var miss = field.TraceField(Vector3.Zero, new(0f, 0f, 1f), 1.5f);

        Assert.Equal(-1, miss.Primitive);
        Assert.Equal(new Vector3(0f, 1f, 0f), QueriedField.GradientField(miss));
    }

    /// <summary>One triangle spanning the z = 2 plane, large enough that axis rays cross it.</summary>
    static QueriedField Wall(out TriangleBvh bvh) {
        Span<Vector3> vertices = [new(-8f, -8f, 2f), new(24f, -8f, 2f), new(-8f, 24f, 2f)];
        Span<int> indices = [0, 1, 2];

        bvh = new(vertices, indices);

        return new(bvh);
    }
}
