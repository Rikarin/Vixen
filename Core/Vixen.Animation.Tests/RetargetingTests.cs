// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Retargeting;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

public class RetargetingTests {
    /// <summary>The rig a clip is authored on: unit bones up the Y axis.</summary>
    static Skeleton Source() =>
        Skeleton.Create(
            TestRigs.Build(
                "Source",
                ("Hips", -1, new Vector3(0f, 1f, 0f)),
                ("Spine", 0, new Vector3(0f, 1f, 0f)),
                ("Arm", 1, new Vector3(0f, 1f, 0f))
            )
        );

    /// <summary>The same rig at half the size, and named the same.</summary>
    static Skeleton Half() =>
        Skeleton.Create(
            TestRigs.Build(
                "Half",
                ("Hips", -1, new Vector3(0f, 0.5f, 0f)),
                ("Spine", 0, new Vector3(0f, 0.5f, 0f)),
                ("Arm", 1, new Vector3(0f, 0.5f, 0f))
            )
        );

    /// <summary>
    ///     The same proportions as <see cref="Source" />, with the arm bound forty-five degrees away
    ///     from it — the A-pose-against-T-pose case the model-space transfer exists for.
    /// </summary>
    static Skeleton Splayed() =>
        Skeleton.Create(
            TestRigs.BuildPosed(
                "Splayed",
                ("Hips", -1, new Vector3(0f, 1f, 0f), Quaternion.Identity),
                ("Spine", 0, new Vector3(0f, 1f, 0f), Quaternion.Identity),
                ("Arm", 1, new Vector3(0f, 1f, 0f), Quaternion.FromAxisAngle(Vector3.UnitZ, MathUtil.PiOverFour))
            )
        );

    static SkeletonRetarget Retarget(Skeleton source, Skeleton target) =>
        new(RetargetMap.Between(source, target).ByName().Build());

    [Fact]
    public void ByName_IdenticallyNamedRigs_MapsEveryJoint() {
        var map = RetargetMap.Between(Source(), Half()).ByName().Build();

        Assert.Equal(3, map.MappedJointCount);
        Assert.Equal([0, 1, 2], map.SourceOf.ToArray());
    }

    [Fact]
    public void ByName_WithAPrefix_StripsItBeforeComparing() {
        var source = Skeleton.Create(
            TestRigs.Build(
                "Mixamo",
                ("mixamorig:Hips", -1, new Vector3(0f, 1f, 0f)),
                ("mixamorig:Spine", 0, new Vector3(0f, 1f, 0f)),
                ("mixamorig:Arm", 1, new Vector3(0f, 1f, 0f))
            )
        );

        var bare = RetargetMap.Between(source, Half()).ByName().Build();
        var stripped = RetargetMap.Between(source, Half()).ByName("mixamorig:").Build();

        Assert.Equal(0, bare.MappedJointCount);
        Assert.Equal(3, stripped.MappedJointCount);
    }

    [Fact]
    public void Map_UnknownJointNames_AreIgnored() {
        var map = RetargetMap.Between(Source(), Half())
            .Map("Tail", "Tail")
            .Map("Hips", "Hips")
            .Build();

        Assert.Equal(1, map.MappedJointCount);
    }

    [Fact]
    public void Build_NothingMarkedForTranslation_PromotesTheTopmostMappedJoint() {
        var map = RetargetMap.Between(Source(), Half()).ByName().Build();

        Assert.Equal(0, map.TranslationJoint);
        Assert.Equal(RetargetMode.RotationAndTranslation, map.Modes[0]);
        Assert.Equal(RetargetMode.Rotation, map.Modes[1]);
    }

    [Fact]
    public void Build_AnExplicitTranslationJoint_Wins() {
        var map = RetargetMap.Between(Source(), Half())
            .ByName()
            .SetMode("Spine", RetargetMode.RotationAndTranslation)
            .Build();

        Assert.Equal(1, map.TranslationJoint);
    }

    [Fact]
    public void Apply_BindPose_ProducesTheTargetsOwnBindPose() {
        var target = Half();
        var retarget = Retarget(Source(), target);
        var result = new BoneTransform[target.JointCount];

        retarget.Apply(Source().BindPose, result);

        for (var index = 0; index < target.JointCount; index++) {
            TestRigs.Near(target.BindPose[index].Translation, result[index].Translation, $"joint {index}");
            TestRigs.Near(target.BindPose[index].Rotation, result[index].Rotation, $"joint {index}");
        }
    }

    [Fact]
    public void Apply_KeepsTheTargetsBoneLengths() {
        var source = Source();
        var target = Half();
        var retarget = Retarget(source, target);

        var posed = source.BindPose.ToArray();
        posed[1].Rotation = Quaternion.FromAxisAngle(Vector3.UnitZ, 0.6f);

        var result = new BoneTransform[target.JointCount];
        retarget.Apply(posed, result);

        var model = new BoneTransform[target.JointCount];
        SkeletonPose.ComputeModelSpace(target, result, model);

        // Half-size rig, half-length bones, whatever the source's are.
        Assert.Equal(0.5f, (model[1].Translation - model[0].Translation).Length(), 1e-4f);
        Assert.Equal(0.5f, (model[2].Translation - model[1].Translation).Length(), 1e-4f);
    }

    [Fact]
    public void Apply_CopiesTheModelSpaceRotationOfEveryMappedJoint() {
        var source = Source();
        var target = Half();
        var retarget = Retarget(source, target);

        var posed = source.BindPose.ToArray();
        posed[1].Rotation = Quaternion.FromAxisAngle(Vector3.UnitZ, 0.6f);
        posed[2].Rotation = Quaternion.FromAxisAngle(Vector3.UnitX, -0.4f);

        var result = new BoneTransform[target.JointCount];
        retarget.Apply(posed, result);

        var sourceModel = new BoneTransform[source.JointCount];
        var targetModel = new BoneTransform[target.JointCount];
        SkeletonPose.ComputeModelSpace(source, posed, sourceModel);
        SkeletonPose.ComputeModelSpace(target, result, targetModel);

        // The two rigs share a bind orientation, so the model-space rotations transfer unchanged.
        for (var index = 0; index < target.JointCount; index++) {
            TestRigs.Near(sourceModel[index].Rotation, targetModel[index].Rotation, $"joint {index}");
        }
    }

    [Fact]
    public void Apply_DifferentBindOrientations_TransfersTheAnimationAndNotThePose() {
        var source = Source();
        var target = Splayed();
        var retarget = Retarget(source, target);

        var posed = source.BindPose.ToArray();
        posed[2].Rotation = Quaternion.FromAxisAngle(Vector3.UnitZ, 0.5f);

        var result = new BoneTransform[target.JointCount];
        retarget.Apply(posed, result);

        var model = new BoneTransform[target.JointCount];
        SkeletonPose.ComputeModelSpace(target, result, model);

        // The target's arm rests forty-five degrees round from the source's. What transfers is the
        // half-radian the animation *added*, applied to where the target's arm already was — not the
        // source's absolute orientation, which would snap the arm to the other rig's rest pose and
        // undo the forty-five degrees.
        var wanted = Quaternion.Concatenate(
            Quaternion.FromAxisAngle(Vector3.UnitZ, MathUtil.PiOverFour),
            Quaternion.FromAxisAngle(Vector3.UnitZ, 0.5f)
        );

        TestRigs.Near(wanted, model[2].Rotation);

        // Which is not what copying the source's rotation would have given.
        var sourceModel = new BoneTransform[source.JointCount];
        SkeletonPose.ComputeModelSpace(source, posed, sourceModel);
        Assert.False(Quaternion.SameRotation(sourceModel[2].Rotation, model[2].Rotation, 1e-3f));
    }

    [Fact]
    public void TranslationScale_IsDerivedFromTheTwoRigsProportions() {
        var retarget = Retarget(Source(), Half());
        Assert.Equal(0.5f, retarget.TranslationScale, 1e-4f);
    }

    [Fact]
    public void Apply_TheTranslationJoint_MovesByTheScaledDisplacement() {
        var source = Source();
        var target = Half();
        var retarget = Retarget(source, target);

        var posed = source.BindPose.ToArray();
        posed[0].Translation += new Vector3(0f, 0f, -2f);

        var result = new BoneTransform[target.JointCount];
        retarget.Apply(posed, result);

        // Half the character, half the stride.
        TestRigs.Near(new(0f, 0.5f, -1f), result[0].Translation);
    }

    [Fact]
    public void Apply_AnUnmappedJoint_StaysInTheTargetsBindPose() {
        var source = Source();

        var target = Skeleton.Create(
            TestRigs.Build(
                "WithTail",
                ("Hips", -1, new Vector3(0f, 1f, 0f)),
                ("Spine", 0, new Vector3(0f, 1f, 0f)),
                ("Arm", 1, new Vector3(0f, 1f, 0f)),
                ("Tail", 0, new Vector3(0f, -1f, 0f))
            )
        );

        var retarget = Retarget(source, target);
        var posed = source.BindPose.ToArray();
        posed[1].Rotation = Quaternion.FromAxisAngle(Vector3.UnitZ, 0.6f);

        var result = new BoneTransform[target.JointCount];
        retarget.Apply(posed, result);

        var tail = target.IndexOf("Tail");
        TestRigs.Near(target.BindPose[tail].Translation, result[tail].Translation);
        TestRigs.Near(Quaternion.Identity, result[tail].Rotation);
    }

    [Fact]
    public void Bake_ProducesAClipOnTheTargetSkeletonWithTheSameShape() {
        var source = Source();
        var target = Half();

        var clip = AnimationClip.Create(
            TestRigs.Rotate(
                "Wave",
                "Spine",
                Quaternion.Identity,
                Quaternion.FromAxisAngle(Vector3.UnitZ, 1f),
                2f
            ),
            source,
            [new("Peak", 1f)]
        );

        var baked = Retarget(source, target).Bake(clip, 60f);

        Assert.Same(target, baked.Skeleton);
        Assert.Equal("Wave", baked.Name);
        Assert.Equal(2f, baked.Duration, TestRigs.Tolerance);
        Assert.Equal(0, baked.UnresolvedChannels);
        Assert.Equal(1, baked.Events.Length);
        Assert.Equal("Peak", baked.Events[0].Name);
    }

    [Fact]
    public void Bake_SampledBack_MatchesRetargetingThePoseDirectly() {
        var source = Source();
        var target = Half();
        var retarget = Retarget(source, target);

        var clip = AnimationClip.Create(
            TestRigs.Rotate(
                "Wave",
                "Spine",
                Quaternion.Identity,
                Quaternion.FromAxisAngle(Vector3.UnitZ, 1f),
                1f
            ),
            source
        );

        var baked = retarget.Bake(clip, 120f);

        var sourcePose = new BoneTransform[source.JointCount];
        var expected = new BoneTransform[target.JointCount];
        var actual = new BoneTransform[target.JointCount];

        foreach (var time in new[] { 0f, 0.137f, 0.5f, 0.813f, 1f }) {
            clip.Sample(time, sourcePose);
            retarget.Apply(sourcePose, expected);
            baked.Sample(time, actual);

            for (var index = 0; index < target.JointCount; index++) {
                // Loose to a thousandth: the baked clip is a resampled grid, so between its samples
                // it is a linear approximation of a curve rather than the curve.
                Assert.True(
                    Quaternion.SameRotation(expected[index].Rotation, actual[index].Rotation, 1e-3f),
                    $"joint {index} at {time}: {expected[index].Rotation} vs {actual[index].Rotation}"
                );
            }
        }
    }

    [Fact]
    public void Bake_CarriesRootMotionAcrossAtTheTargetsScale() {
        var source = Source();
        var target = Half();

        var clip = AnimationClip.Create(
            TestRigs.Translate("Walk", "Hips", Vector3.Zero, new(0f, 0f, -4f)),
            source,
            rootJoint: "Hips"
        );

        var baked = Retarget(source, target).Bake(clip, 60f);

        Assert.Equal(target.IndexOf("Hips"), baked.RootJoint);
        TestRigs.Near(new(0f, 0f, -2f), baked.ExtractRootMotion(0f, 1f).Translation);
    }

    [Fact]
    public void Bake_AClipFromAnotherSkeleton_IsRefused() {
        var clip = AnimationClip.Create(TestRigs.Hold("Idle", "Mid", Vector3.Zero), TestRigs.Chain());
        var retarget = Retarget(Source(), Half());

        Assert.Throws<ArgumentException>(() => retarget.Bake(clip));
    }
}
