// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Rules;
using Vixen.Net.Sessions;

namespace Vixen.Net.Animation;

/// <summary>Reads the selected joints of a pose into the component the wire carries.</summary>
/// <remarks>
///     <para>
///         <b>In <see cref="SystemPhase.LateUpdate" />, which is a decision rather than a default.</b>
///         The pose exists after <c>AnimationSystem</c> has run, in <see cref="SystemPhase.Animation" />,
///         and it is consumed by <c>SkinningSystem</c> in <see cref="SystemPhase.PreRender" />. Sitting
///         between the two puts this on the right side of both — and phases hard-sync, so it is the
///         ordering guarantee rather than a hope about how the dependency graph resolves. Declaring it
///         in <c>PreRender</c> beside the skinning would leave the order to the graph, and the graph
///         cannot see it: what this touches is a managed <c>Animator</c>'s pose, which no declared
///         component access describes.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.LateUpdate)]
public sealed class NetworkBonesCaptureSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription posed = new QueryDescription()
        .WithAll<AnimatorComponent, NetworkBones, NetworkBoneSelection, NetworkId>();

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<AnimatorComponent>()
        .Read<NetworkBoneSelection>()
        .Read<NetworkId>()
        .Write<NetworkBones>()
        .Build();

    /// <summary>Who decides what, and who this peer is. Null is server-authoritative.</summary>
    public NetworkRulesRegistry? Rules { get; set; }

    /// <summary>Which player this peer is, or <see cref="PlayerId.None" /> for a server.</summary>
    public PlayerId Local { get; set; } = PlayerId.None;

    /// <summary>How many poses have been published.</summary>
    public long PublishedCount { get; private set; }

    /// <summary>How many bones have been packed, which is what the bandwidth is proportional to.</summary>
    public long BoneCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Publish(context.World);

        return dependency;
    }

    /// <summary>Reads every replicated pose this peer decides.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Publish(World world) {
        ArgumentNullException.ThrowIfNull(world);

        foreach (var chunk in world.Chunks(posed)) {
            var bones = chunk.Values<NetworkBones>();
            var ids = chunk.ReadValues<NetworkId>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!IsAuthority(ids[index])) {
                    continue;
                }

                // One at a time: both the animator and the selection are managed, so the chunk holds
                // handles rather than the objects and there is no span of either to sweep.
                if (world.Read<AnimatorComponent>(entities[index]).Value is not { } animator
                    || world.Read<NetworkBoneSelection>(entities[index]).Joints is not { } joints) {
                    continue;
                }

                BoneCount += Read(animator, joints, ref bones[index]);
                PublishedCount++;
            }
        }
    }

    /// <summary>Whether this peer decides how an object is posed.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether it does.</returns>
    public bool IsAuthority(NetworkId id) =>
        Rules is { } rules
            ? rules.MayWrite(id, Local)
            : NetworkRules.Allows(RuleAudience.ServerOnly, Local, isOwner: false);

    static int Read(Animator animator, int[] joints, ref NetworkBones bones) {
        var pose = animator.Pose;
        var count = Math.Min(joints.Length, NetworkBonesReplicator.MaxBones);
        bones.Count = (byte)count;

        for (var index = 0; index < count; index++) {
            var joint = joints[index];

            // A selection naming a joint the rig does not have is content that has changed under the
            // code — a rig re-exported with fewer joints, most likely. Skipping the entry keeps the
            // rest of the pose going out; the identity it leaves behind is visible as a limb that
            // does not move, which is the failure that gets reported.
            if ((uint)joint >= (uint)pose.JointCount) {
                bones.Rotations[index] = MathCodec.PackRotation(Quaternion.Identity);

                continue;
            }

            bones.Rotations[index] = MathCodec.PackRotation(pose[joint].Rotation);
        }

        return count;
    }
}

/// <summary>Puts an arrived pose onto a receiving animator.</summary>
/// <remarks>
///     <para>
///         <b>After the animator has run, and overwriting what it produced.</b> That is the whole
///         point: this is used precisely where the receiving animator <i>cannot</i> reproduce the
///         authority's pose, so what it computed locally is not an approximation to be blended with —
///         it is an answer to a different question. A receiver that has nothing else to animate can
///         stop its animator entirely; leaving it running costs an evaluation whose result is
///         discarded, which is wasteful rather than wrong.
///     </para>
///     <para>
///         <b>No crossfade, unlike the state correction in <see cref="NetworkAnimatorApplySystem" />.</b>
///         A state correction is rare and a visible cut is a bug; a pose arrives every tick and
///         blending each one into the last would be a low-pass filter on the animation — a ragdoll
///         that lands softly on every impact. Smoothing a pose is <c>SnapshotBuffer</c>'s job, at the
///         layer that already knows about interpolation delay.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.LateUpdate)]
public sealed class NetworkBonesApplySystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription posed = new QueryDescription()
        .WithAll<AnimatorComponent, NetworkBones, NetworkBoneSelection, NetworkId>();

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<AnimatorComponent>()
        .Read<NetworkBoneSelection>()
        .Read<NetworkBones>()
        .Read<NetworkId>()
        .Build();

    /// <summary>Who decides what, and who this peer is. Null is server-authoritative.</summary>
    public NetworkRulesRegistry? Rules { get; set; }

    /// <summary>Which player this peer is, or <see cref="PlayerId.None" /> for a server.</summary>
    public PlayerId Local { get; set; } = PlayerId.None;

    /// <summary>How many poses have been applied.</summary>
    public long AppliedCount { get; private set; }

    /// <summary>How many arrived saying a different number of bones than this peer selected.</summary>
    /// <remarks>
    ///     Anything but zero means the two ends disagree about a character's rig — a re-export that
    ///     reached one build and not the other, or two different selections for one prefab. The pose
    ///     is applied as far as the shorter of the two goes, because half a right answer beats a limb
    ///     frozen mid-air, and this is how anybody finds out.
    /// </remarks>
    public long MismatchedCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Apply(context.World);

        return dependency;
    }

    /// <summary>Poses every replicated character this peer does not decide.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Apply(World world) {
        ArgumentNullException.ThrowIfNull(world);

        foreach (var chunk in world.Chunks(posed)) {
            var bones = chunk.ReadValues<NetworkBones>();
            var ids = chunk.ReadValues<NetworkId>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (IsAuthority(ids[index])) {
                    continue;
                }

                if (world.Read<AnimatorComponent>(entities[index]).Value is not { } animator
                    || world.Read<NetworkBoneSelection>(entities[index]).Joints is not { } joints) {
                    continue;
                }

                if (bones[index].Count != joints.Length) {
                    MismatchedCount++;
                }

                Pose(animator, joints, bones[index]);
                AppliedCount++;
            }
        }
    }

    /// <summary>Whether this peer decides how an object is posed.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether it does.</returns>
    public bool IsAuthority(NetworkId id) =>
        Rules is { } rules
            ? rules.MayWrite(id, Local)
            : NetworkRules.Allows(RuleAudience.ServerOnly, Local, isOwner: false);

    static void Pose(Animator animator, int[] joints, in NetworkBones bones) {
        var pose = animator.Pose;
        var count = Math.Min(Math.Min(joints.Length, bones.Count), NetworkBonesReplicator.MaxBones);

        for (var index = 0; index < count; index++) {
            var joint = joints[index];

            if ((uint)joint >= (uint)pose.JointCount) {
                continue;
            }

            // The rotation only. The translation and scale are the ones the local rig already has,
            // which is what "a skeleton is rigid" means in practice — and writing a zeroed
            // translation over a bind pose is how a character comes apart at the joints.
            pose[joint].Rotation = MathCodec.UnpackRotation(bones.Rotations[index]);
        }
    }
}
