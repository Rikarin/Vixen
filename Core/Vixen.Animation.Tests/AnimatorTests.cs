// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Ik;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

public class AnimatorTests {
    readonly Skeleton skeleton = TestRigs.Branching();

    ClipMotion Held(string name, string joint, float x) => Held(skeleton, name, joint, x);

    static ClipMotion Held(Skeleton rig, string name, string joint, float x) =>
        new(AnimationClip.Create(TestRigs.Hold(name, joint, new Vector3(x, 0f, 0f)), rig));

    static AnimationStateMachine Single(string name, Motion motion) => new([new AnimationState(name, motion)]);

    [Fact]
    public void Update_OneLayer_PosesFromTheGraph() {
        var animator = new Animator(skeleton);
        animator.AddLayer("Base", Single("Idle", Held("Idle", "Spine", 3f)));

        animator.Update(0.1f);

        Assert.Equal(3f, animator.Pose[skeleton.IndexOf("Spine")].Translation.X);
    }

    [Fact]
    public void Update_NoLayers_LeavesTheBindPose() {
        var animator = new Animator(skeleton);
        animator.Update(0.1f);

        TestRigs.Near(skeleton.BindPose[1].Translation, animator.Pose[1].Translation);
    }

    [Fact]
    public void Update_MaskedOverrideLayer_ReplacesOnlyTheMaskedJoints() {
        var animator = new Animator(skeleton);
        animator.AddLayer("Base", Single("Locomotion", Held("Run", "Spine", 0f)));

        var upper = animator.AddLayer("UpperBody", Single("Wave", Held("Wave", "Spine", 10f)));
        upper.Mask = BoneMask.Excluding(skeleton).Set("LeftArm", 1f).Build();

        animator.Update(0.1f);

        // The layer's clip drives Spine, which the mask does not let through.
        Assert.Equal(0f, animator.Pose[skeleton.IndexOf("Spine")].Translation.X);
    }

    [Fact]
    public void Update_LayerWeight_ScalesHowMuchOfItArrives() {
        var animator = new Animator(skeleton);
        animator.AddLayer("Base", Single("Locomotion", Held("Run", "Spine", 0f)));

        var upper = animator.AddLayer("UpperBody", Single("Wave", Held("Wave", "Spine", 10f)));
        upper.Weight = 0.25f;

        animator.Update(0.1f);

        Assert.Equal(2.5f, animator.Pose[skeleton.IndexOf("Spine")].Translation.X, TestRigs.Tolerance);
    }

    [Fact]
    public void Update_AdditiveLayer_AddsItsDifferenceRatherThanReplacing() {
        var animator = new Animator(skeleton);
        animator.AddLayer("Base", Single("Locomotion", Held("Run", "Spine", 4f)));

        var lean = new ClipMotion(
            AnimationClip.Create(
                TestRigs.Translate("Lean", "Spine", new(1f, 0f, 0f), new(3f, 0f, 0f)),
                skeleton
            ),
            additive: true
        );

        var additive = animator.AddLayer("Lean", Single("Lean", lean));
        additive.Blend = LayerBlend.Additive;

        // Halfway through the lean clip the difference from its own first frame is +1.
        animator.Update(0.5f);

        Assert.Equal(5f, animator.Pose[skeleton.IndexOf("Spine")].Translation.X, TestRigs.Tolerance);
    }

    [Fact]
    public void Update_DisabledLayer_ContributesNothing() {
        var animator = new Animator(skeleton);
        animator.AddLayer("Base", Single("Locomotion", Held("Run", "Spine", 4f)));

        var upper = animator.AddLayer("UpperBody", Single("Wave", Held("Wave", "Spine", 10f)));
        upper.Enabled = false;

        animator.Update(0.1f);

        Assert.Equal(4f, animator.Pose[skeleton.IndexOf("Spine")].Translation.X);
    }

    [Fact]
    public void Update_Speed_ScalesTheStepEveryLayerTakes() {
        var animator = new Animator(skeleton) { Speed = 2f };
        var layer = animator.AddLayer("Base", Single("Idle", Held("Idle", "Spine", 0f)));

        animator.Update(0.25f);

        Assert.Equal(0.5f, layer.States.NormalizedTime, TestRigs.Tolerance);
    }

    [Fact]
    public void Update_Events_AreClearedAtTheStartOfEachUpdate() {
        var clip = AnimationClip.Create(
            TestRigs.Hold("Walk", "Spine", Vector3.Zero),
            skeleton,
            [new("Step", 0.5f)]
        );

        var animator = new Animator(skeleton);
        animator.AddLayer("Base", Single("Walk", new ClipMotion(clip)));

        animator.Update(0.6f);
        Assert.Equal(1, animator.Events.Count);

        animator.Update(0.1f);
        Assert.Equal(0, animator.Events.Count);
    }

    [Fact]
    public void Update_TriggersNoOneConsumed_AreClearedSoTheyDoNotLeak() {
        var animator = new Animator(skeleton);
        animator.AddLayer("Base", Single("Idle", Held("Idle", "Spine", 0f)));
        animator.Parameters.SetTrigger("Jump");

        animator.Update(0.1f);

        Assert.False(animator.Parameters.GetBool("Jump"));
    }

    [Fact]
    public void Update_RootMotionEnabled_TakesTheMotionOutOfThePoseAndReportsIt() {
        var chain = TestRigs.Chain();
        var clip = AnimationClip.Create(
            TestRigs.Translate("Walk", "Root", Vector3.Zero, new(0f, 0f, -4f)),
            chain
        );

        var animator = new Animator(chain) { RootMotion = RootMotionMode.Extract };
        animator.AddLayer("Base", new([new AnimationState("Walk", new ClipMotion(clip))]));

        animator.Update(0.25f);

        TestRigs.Near(new(0f, 0f, -1f), animator.LastRootMotion.Translation);
        TestRigs.Near(chain.BindPose[0].Translation, animator.Pose[0].Translation);
    }

    [Fact]
    public void Update_RootMotionDisabled_LeavesTheMotionInThePose() {
        var chain = TestRigs.Chain();
        var clip = AnimationClip.Create(
            TestRigs.Translate("Walk", "Root", Vector3.Zero, new(0f, 0f, -4f)),
            chain
        );

        var animator = new Animator(chain);
        animator.AddLayer("Base", new([new AnimationState("Walk", new ClipMotion(clip))]));

        animator.Update(0.25f);

        TestRigs.Near(new(0f, 0f, -1f), animator.Pose[0].Translation);
        Assert.True(animator.LastRootMotion.IsZero);
    }

    [Fact]
    public void PoseProcessors_RunAfterTheLayersAreMixed() {
        var chain = TestRigs.Chain();
        var animator = new Animator(chain);
        animator.AddLayer("Base", new([new AnimationState("Idle", Held(chain, "Idle", "Root", 0f))]));

        var target = new Vector3(1f, 0f, 1f);
        animator.PoseProcessors.Add(new Reach(target));

        animator.Update(0.1f);

        var model = new BoneTransform[chain.JointCount];
        animator.Pose.ComputeModelSpace(model);

        TestRigs.Near(target, model[2].Translation);
    }

    [Fact]
    public void ComputeSkinningMatrices_APosedSkeleton_MatchesTheModelSpaceComposition() {
        var chain = TestRigs.Chain();
        var animator = new Animator(chain);
        animator.AddLayer("Base", new([new AnimationState("Idle", Held(chain, "Idle", "Root", 0f))]));
        animator.Update(0.1f);

        animator.Pose[1].Rotation = Quaternion.FromAxisAngle(Vector3.UnitZ, 0.5f);

        var palette = new Matrix4x4[chain.JointCount];
        animator.ComputeSkinningMatrices(palette);

        var model = new BoneTransform[chain.JointCount];
        animator.Pose.ComputeModelSpace(model);

        for (var index = 0; index < chain.JointCount; index++) {
            var expected = chain.InverseBindPoses[index] * model[index].ToMatrix();
            Assert.True(Matrix4x4.NearEqual(expected, palette[index], TestRigs.Tolerance));
        }
    }

    [Fact]
    public void Layer_ByName_FindsItOrReportsNothing() {
        var animator = new Animator(skeleton);
        animator.AddLayer("Base", Single("Idle", Held("Idle", "Spine", 0f)));

        Assert.NotNull(animator.Layer("Base"));
        Assert.Null(animator.Layer("Nope"));
    }

    sealed class Reach(Vector3 target) : IPoseProcessor {
        public void Process(Animator animator, Span<BoneTransform> pose, Span<BoneTransform> model) =>
            TwoBoneIk.Solve(animator.Skeleton, pose, model, new(0, 1, 2, target, new(0f, 1f, 5f)));
    }
}
