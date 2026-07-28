// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Motions;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

public class BlendTreeTests {
    readonly Skeleton skeleton = TestRigs.Chain();
    readonly AnimationParameters parameters = new();

    ClipMotion Held(string name, float x, float duration = 1f) =>
        new(
            AnimationClip.Create(
                TestRigs.Hold(name, "Mid", new Vector3(x, 0f, 0f), duration),
                skeleton
            )
        );

    MotionContext Context() =>
        new(parameters, new PoseScratch(skeleton.JointCount), 0.5f, 0.4f, 0, false, null, 0, "Test", 1f);

    [Fact]
    public void BlendTree1D_BetweenThresholds_MixesTheTwoNeighbours() {
        var tree = new BlendTree1D(
            parameters,
            "Speed",
            [new(Held("Idle", 0f), 0f), new(Held("Walk", 10f), 2f), new(Held("Run", 100f), 6f)]
        );

        parameters.SetFloat("Speed", 1f);

        var pose = new BoneTransform[skeleton.JointCount];
        tree.Evaluate(Context(), pose);

        Assert.Equal(5f, pose[1].Translation.X, TestRigs.Tolerance);
    }

    [Fact]
    public void BlendTree1D_OutsideTheThresholds_PlaysTheEndMotionAlone() {
        var tree = new BlendTree1D(
            parameters,
            "Speed",
            [new(Held("Idle", 0f), 0f), new(Held("Run", 100f), 6f)]
        );

        var weights = new float[2];

        parameters.SetFloat("Speed", -20f);
        tree.ComputeWeights(parameters, weights);
        Assert.Equal([1f, 0f], weights);

        parameters.SetFloat("Speed", 500f);
        tree.ComputeWeights(parameters, weights);
        Assert.Equal([0f, 1f], weights);
    }

    [Fact]
    public void BlendTree1D_Length_IsTheWeightedAverageOfItsChildren() {
        var tree = new BlendTree1D(
            parameters,
            "Speed",
            [new(Held("Walk", 0f, 1.2f), 0f), new(Held("Run", 1f, 0.8f), 1f)]
        );

        parameters.SetFloat("Speed", 0.5f);

        // Both feet land together only if the tree plays over one cycle that is neither clip's.
        Assert.Equal(1.0f, tree.Length(parameters), TestRigs.Tolerance);
    }

    [Fact]
    public void BlendTree1D_ThresholdsGivenOutOfOrder_AreSorted() {
        var tree = new BlendTree1D(
            parameters,
            "Speed",
            [new(Held("Run", 100f), 6f), new(Held("Idle", 0f), 0f)]
        );

        Assert.Equal(0f, tree.Children[0].Threshold);
        Assert.Equal(6f, tree.Children[1].Threshold);
    }

    [Fact]
    public void BlendTree1D_NoChildren_IsRejected() =>
        Assert.Throws<ArgumentException>(() => new BlendTree1D(0, []));

    [Fact]
    public void BlendTree2D_AtAMotionsOwnPoint_ThatMotionTakesEverything() {
        var tree = new BlendTree2D(
            parameters,
            "X",
            "Y",
            [
                new(Held("Idle", 0f), Vector2.Zero),
                new(Held("Forward", 1f), new Vector2(0f, 1f)),
                new(Held("Right", 2f), new Vector2(1f, 0f))
            ]
        );

        parameters.SetFloat("X", 1f);
        parameters.SetFloat("Y", 0f);

        var weights = new float[3];
        tree.ComputeWeights(parameters, weights);

        Assert.Equal(1f, weights[2], TestRigs.Tolerance);
        Assert.Equal(0f, weights[0], TestRigs.Tolerance);
        Assert.Equal(0f, weights[1], TestRigs.Tolerance);
    }

    [Fact]
    public void BlendTree2D_WeightsAlwaysSumToOne() {
        var tree = new BlendTree2D(
            parameters,
            "X",
            "Y",
            [
                new(Held("Idle", 0f), Vector2.Zero),
                new(Held("Forward", 1f), new Vector2(0f, 1f)),
                new(Held("Right", 2f), new Vector2(1f, 0f)),
                new(Held("Back", 3f), new Vector2(0f, -1f))
            ]
        );

        var weights = new float[4];

        for (var x = -2f; x <= 2f; x += 0.25f) {
            for (var y = -2f; y <= 2f; y += 0.25f) {
                parameters.SetFloat("X", x);
                parameters.SetFloat("Y", y);
                tree.ComputeWeights(parameters, weights);

                var total = 0f;

                foreach (var weight in weights) {
                    Assert.True(weight >= 0f, $"negative weight at ({x}, {y})");
                    total += weight;
                }

                Assert.Equal(1f, total, TestRigs.Tolerance);
            }
        }
    }

    [Fact]
    public void BlendTree2D_HalfwayBetweenTwoMotions_SplitsThemEvenly() {
        var tree = new BlendTree2D(
            parameters,
            "X",
            "Y",
            [new(Held("Left", 0f), new Vector2(-1f, 0f)), new(Held("Right", 1f), new Vector2(1f, 0f))]
        );

        parameters.SetFloat("X", 0f);
        parameters.SetFloat("Y", 0f);

        var weights = new float[2];
        tree.ComputeWeights(parameters, weights);

        Assert.Equal(0.5f, weights[0], TestRigs.Tolerance);
        Assert.Equal(0.5f, weights[1], TestRigs.Tolerance);
    }

    [Fact]
    public void BlendTree2D_Directional_KeepsOppositeDirectionsApart() {
        // Forward at (0, 3) and backward at (0, −3). Sampled at (0, 1.5) — half speed forwards —
        // the character should be running forwards, not forwards and backwards at once.
        var tree = new BlendTree2D(
            parameters,
            "X",
            "Y",
            [
                new(Held("Idle", 0f), Vector2.Zero),
                new(Held("Forward", 1f), new Vector2(0f, 3f)),
                new(Held("Backward", 2f), new Vector2(0f, -3f))
            ],
            Blend2DMode.FreeformDirectional
        );

        parameters.SetFloat("X", 0f);
        parameters.SetFloat("Y", 1.5f);

        var weights = new float[3];
        tree.ComputeWeights(parameters, weights);

        Assert.Equal(0f, weights[2], TestRigs.Tolerance);
        Assert.True(weights[1] > 0.4f, $"forward should dominate, got {weights[1]}");
    }

    [Fact]
    public void BlendTree2D_CoincidentMotions_StillProducesAPose() {
        var tree = new BlendTree2D(
            parameters,
            "X",
            "Y",
            [new(Held("A", 1f), Vector2.Zero), new(Held("B", 2f), Vector2.Zero)]
        );

        parameters.SetFloat("X", 5f);
        parameters.SetFloat("Y", 5f);

        var weights = new float[2];
        tree.ComputeWeights(parameters, weights);

        Assert.Equal(1f, weights[0] + weights[1], TestRigs.Tolerance);
    }

    [Fact]
    public void BlendTree_Nested_EvaluatesThroughSeveralLevelsOfScratch() {
        var inner = new BlendTree1D(
            parameters,
            "Speed",
            [new(Held("Idle", 0f), 0f), new(Held("Walk", 10f), 1f)]
        );

        var outer = new BlendTree1D(
            parameters,
            "Crouch",
            [new(inner, 0f), new(Held("Crouched", 100f), 1f)]
        );

        parameters.SetFloat("Speed", 0.5f);
        parameters.SetFloat("Crouch", 0.5f);

        var pose = new BoneTransform[skeleton.JointCount];
        outer.Evaluate(Context(), pose);

        // inner is 5, crouched is 100, half and half.
        Assert.Equal(52.5f, pose[1].Translation.X, 0.01f);
    }
}
