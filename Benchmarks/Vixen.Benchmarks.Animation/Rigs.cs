// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core.Mathematics;
using Vixen.Rendering;

namespace Vixen.Benchmarks.Animation;

/// <summary>
///     A character-shaped skeleton and clips to play on it, sized to what a game actually ships.
/// </summary>
/// <remarks>
///     Sixty-four joints and a key a frame at thirty hertz — a humanoid without fingers, and an
///     exporter that emitted every frame. Benchmarking a three-joint chain measures the loop
///     overhead and nothing else.
/// </remarks>
static class Rigs {
    /// <summary>A humanoid-sized skeleton: a spine with four limbs hanging off it.</summary>
    public static Skeleton Humanoid(int joints = 64) {
        var built = new SkeletonJoint[joints];
        var model = new Matrix4x4[joints];

        for (var index = 0; index < joints; index++) {
            // A chain for the first eight, then four limbs branching off it, so the hierarchy has
            // depth and breadth rather than being one long chain the cache loves.
            var parent = index == 0 ? -1 : index < 8 ? index - 1 : ((index - 8) % 4) + 4;
            var offset = new Vector3(0.1f, 0.2f, 0f);
            var local = Matrix4x4.FromTranslation(offset);

            model[index] = parent < 0 ? local : local * model[parent];
            Matrix4x4.Invert(model[index], out var inverse);

            built[index] = new() { Name = $"Joint{index}", Parent = parent, InverseBindPose = inverse };
        }

        return Skeleton.Create(new() { Name = "Humanoid", Joints = built });
    }

    /// <summary>A clip driving every joint of a skeleton, with a key every frame.</summary>
    public static AnimationClipData Clip(Skeleton skeleton, string name, float duration, int rate) {
        var keys = (int)(duration * rate) + 1;
        var times = new float[keys];

        for (var index = 0; index < keys; index++) {
            times[index] = index / (float)rate;
        }

        var channels = new AnimationChannel[skeleton.JointCount];

        for (var joint = 0; joint < skeleton.JointCount; joint++) {
            var rotations = new Quaternion[keys];
            var positions = new Vector3[keys];

            for (var index = 0; index < keys; index++) {
                var phase = (index / (float)rate) + (joint * 0.37f);

                rotations[index] = Quaternion.FromAxisAngle(
                    Vector3.Normalize(new(1f, 2f, 3f)),
                    MathF.Sin(phase) * 0.4f
                );

                positions[index] = new(0.1f, 0.2f + (MathF.Sin(phase) * 0.01f), 0f);
            }

            channels[joint] = new() {
                Target = skeleton.NameOf(joint),
                RotationTimes = times,
                Rotations = rotations,
                PositionTimes = times,
                Positions = positions
            };
        }

        return new() { Name = name, Duration = duration, Channels = channels };
    }

    /// <summary>An animator running a two-motion blend tree — a locomotion state, roughly.</summary>
    public static Animator Character(Skeleton skeleton, AnimationClip walk, AnimationClip run) {
        var animator = new Animator(skeleton);

        var tree = new BlendTree1D(
            animator.Parameters,
            "Speed",
            [new(new ClipMotion(walk), 0f), new(new ClipMotion(run), 6f)]
        );

        animator.AddLayer("Base", new([new AnimationState("Locomotion", tree)]));
        animator.Parameters.SetFloat("Speed", 3f);

        return animator;
    }
}
