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

    /// <summary>One triangle spanning the z = 2 plane, large enough that axis rays cross it.</summary>
    static QueriedField Wall(out TriangleBvh bvh) {
        Span<Vector3> vertices = [new(-8f, -8f, 2f), new(24f, -8f, 2f), new(-8f, 24f, 2f)];
        Span<int> indices = [0, 1, 2];

        bvh = new(vertices, indices);

        return new(bvh);
    }
}
