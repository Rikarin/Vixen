// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Net.Animation.Tests;

/// <summary>The path from a rig to a wire, without a test writing the selection by hand.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This assembly had no production caller at all</b>
///         (<see href="https://github.com/Rikarin/Vixen/issues/481" />): the four systems and both
///         replicators were constructed only by <c>NetworkBonesTests</c>, and
///         <see cref="NetworkBoneSelection.Joints" /> was written in exactly one place in the
///         repository — line 227 of that file. Both pose systems bail out when it is null, so a game
///         that registered everything correctly would still have had a pose path that did nothing, by
///         construction, silently.
///     </para>
///     <para>
///         <b>So the test that matters here is the last one</b>, which drives the whole path with
///         nobody naming a joint index: register, schedule, attach an animator, and assert that bones
///         crossed. The suite next door still owns the packing and the precision table.
///     </para>
/// </remarks>
public sealed class NetworkAnimationWiringTests {
    static readonly PlayerId Receiving = new(4);

    /// <summary>The default selection is root-first and stops at what one record holds.</summary>
    /// <remarks>
    ///     Root-first is not decoration: <c>NetworkBonePrecision</c> is indexed by slot, so a narrowed
    ///     table only means anything if slot 0 is the same kind of joint on every rig in the game.
    /// </remarks>
    [Fact]
    public void TheDefaultSelectionIsBreadthFirstFromTheRoot() {
        var skeleton = Chain();
        var joints = NetworkBoneSelector.Trunk(skeleton);

        Assert.Equal([0, 1, 2], joints);
        Assert.Equal(2, NetworkBoneSelector.Trunk(skeleton, 2).Length);
        Assert.Equal([0, 1], NetworkBoneSelector.Trunk(skeleton, 2));
    }

    /// <summary>⚠ Breadth-first, not depth-first, and a wide rig is what tells them apart.</summary>
    /// <remarks>
    ///     A depth-first walk with a cap spends the whole budget on one arm and never reaches the
    ///     other, which is a character with one animated limb — read as a broken clip rather than as a
    ///     selection. The rig here is a root with two branches so the two orders genuinely differ:
    ///     breadth-first takes one joint from each branch, depth-first takes two from the first.
    /// </remarks>
    [Fact]
    public void AWideRigTakesFromEveryBranchBeforeGoingDeep() {
        var joints = NetworkBoneSelector.Trunk(Branching(), 3);

        // Root, then both of its children — rather than root, one child and that child's child.
        Assert.Equal([0, 1, 3], joints);
    }

    /// <summary>Named joints keep the caller's order, and a name the rig lacks is dropped.</summary>
    /// <remarks>
    ///     Dropping matches what the capture system already does with a joint index the rig does not
    ///     have — a re-exported rig is content changing under code, and it presents as a limb that
    ///     does not move. What must not happen is the order being compacted or sorted: the precision
    ///     table is indexed by slot, so renumbering gives every bone somebody else's bit budget.
    /// </remarks>
    [Fact]
    public void NamedJointsKeepTheirOrderAndSkipWhatIsMissing() {
        var joints = NetworkBoneSelector.Named(Chain(), ["Head", "Tail", "Root"]);

        Assert.Equal([2, 0], joints);
    }

    /// <summary>The selection system adds the component as well as filling it.</summary>
    /// <remarks>
    ///     The alternative is a second thing a game has to know to attach, and forgetting it produces
    ///     exactly the silence this issue is about.
    /// </remarks>
    [Fact]
    public void TheSelectionSystemAddsTheComponentItFills() {
        using var world = new World("bones-selection");
        var system = new NetworkBoneSelectionSystem();

        var entity = world.Create(
            new NetworkId(1),
            new AnimatorComponent { Value = new Animator(Chain()) },
            default(NetworkBones)
        );

        Assert.False(world.Has<NetworkBoneSelection>(entity));
        Assert.Equal(1, system.Select(world));

        Assert.Equal([0, 1, 2], world.Read<NetworkBoneSelection>(entity).Joints!);
        Assert.Equal(1, system.SelectedCount);
        Assert.Equal(3, system.JointCount);

        // Once per character rather than once per tick: the selection is a function of the rig.
        Assert.Equal(0, system.Select(world));
        Assert.Equal(1, system.SelectedCount);
    }

    /// <summary>A character whose animator has not arrived is counted and looked at again.</summary>
    /// <remarks>
    ///     ⚠ Left uncounted, a rig that never loads is indistinguishable from a system nobody
    ///     scheduled — and both look like a character standing in its bind pose.
    /// </remarks>
    [Fact]
    public void ACharacterWithNoAnimatorYetIsCountedRatherThanForgotten() {
        using var world = new World("bones-waiting");
        var system = new NetworkBoneSelectionSystem();

        var entity = world.Create(new NetworkId(1), default(AnimatorComponent), default(NetworkBones));

        Assert.Equal(0, system.Select(world));
        Assert.Equal(1, system.WaitingCount);
        Assert.False(world.Has<NetworkBoneSelection>(entity));

        world.Get<AnimatorComponent>(entity).Value = new Animator(Chain());

        Assert.Equal(1, system.Select(world));
        Assert.Equal(3, world.Read<NetworkBoneSelection>(entity).Joints!.Length);
    }

    /// <summary>A selection a game set by hand is left alone.</summary>
    [Fact]
    public void AHandWrittenSelectionIsNotOverwritten() {
        using var world = new World("bones-hand-written");
        var system = new NetworkBoneSelectionSystem();

        var entity = world.Create(
            new NetworkId(1),
            new AnimatorComponent { Value = new Animator(Chain()) },
            default(NetworkBones),
            new NetworkBoneSelection { Joints = [2] }
        );

        Assert.Equal(0, system.Select(world));
        Assert.Equal([2], world.Read<NetworkBoneSelection>(entity).Joints!);
    }

    /// <summary>One registration call puts all three records in the registry.</summary>
    [Fact]
    public void OneCallRegistersEveryReplicator() {
        var registry = new ReplicationRegistry().AddNetworkAnimation();

        Assert.True(registry.TryGet(new NetworkBonesReplicator().TypeId, out _));
        Assert.True(registry.TryGet(new NetworkAnimatorReplicator().TypeId, out _));
        Assert.True(registry.TryGet(new NetworkAnimatorParametersReplicator().TypeId, out _));
        Assert.Equal(3, registry.Replicators.Count);

        // ⚠ A narrowed table is part of the wire layout rather than a local quality setting, so the
        // manifest hash has to move with it — two peers registering different tables must be refused
        // at the handshake and not at the first record that means something different to each.
        Assert.NotEqual(
            new ReplicationRegistry().AddNetworkAnimation().ManifestHash,
            new ReplicationRegistry().AddNetworkAnimation(NetworkBonePrecision.Uniform(8)).ManifestHash
        );
    }

    /// <summary>The whole path, with nobody naming a joint.</summary>
    /// <remarks>
    ///     ⚠ <b>The test the issue asks for.</b> The existing suite drives the systems by hand and
    ///     writes the selection itself, so it cannot tell you the path is unreachable — which it was.
    ///     This registers the replicators, schedules nothing by name, gives a character an animator,
    ///     and asserts that a rotation set on the authority arrives on a peer that never saw the rig's
    ///     joint indices from anywhere but its own copy of the rig.
    /// </remarks>
    [Fact]
    public void APoseCrossesWithoutAnybodyNamingAJoint() {
        var registry = new ReplicationRegistry().AddNetworkAnimation();

        using var server = new World("bones-end-to-end-server");
        using var client = new World("bones-end-to-end-client");

        var sender = new ReplicationServer(registry);
        var receiver = new ReplicationClient(registry);

        var selection = new NetworkBoneSelectionSystem();
        var capture = new NetworkBonesCaptureSystem();
        var apply = new NetworkBonesApplySystem { Local = Receiving };

        var authority = new Animator(Chain());
        var turn = Quaternion.FromAxisAngle(Vector3.UnitY, 0.5f);
        authority.Pose[1].Rotation = turn;

        server.Create(
            new NetworkId(1),
            new AnimatorComponent { Value = authority },
            default(NetworkBones)
        );

        Assert.Equal(1, selection.Select(server));
        capture.Publish(server);

        Assert.Equal(1, capture.PublishedCount);
        Assert.Equal(3, capture.BoneCount);

        var buffer = new byte[4096];
        sender.Capture(server, new(1));

        Assert.True(sender.TryWriteSnapshot(server, Receiving, new(1), buffer, out var snapshot));
        Assert.True(receiver.TryApply(client, snapshot));
        Assert.True(receiver.TryGetEntity(new(1), out var arrived));

        // The receiving peer builds its own animator from its own copy of the same content, which is
        // the premise the selection rests on: nothing about the joints is sent, and this side has to
        // work them out for itself.
        var mirror = new Animator(Chain());
        client.Add(arrived, new AnimatorComponent { Value = mirror });

        Assert.Equal(1, selection.Select(client));
        Assert.Equal([0, 1, 2], client.Read<NetworkBoneSelection>(arrived).Joints!);

        apply.Apply(client);

        Assert.Equal(1, apply.AppliedCount);
        Assert.Equal(0, apply.MismatchedCount);

        // Within the rotation codec's own error, which is what the pose spent crossing the wire.
        Assert.True(
            MathF.Abs(Quaternion.Dot(turn, mirror.Pose[1].Rotation)) > 0.9999f,
            $"the pose did not arrive: {mirror.Pose[1].Rotation} against {turn}"
        );
    }

    /// <summary>A three-joint chain: root, spine, head.</summary>
    static Skeleton Chain() =>
        Rig(
            "Chain",
            [("Root", -1), ("Spine", 0), ("Head", 1)]
        );

    /// <summary>A root with two branches, so breadth-first and depth-first differ.</summary>
    static Skeleton Branching() =>
        Rig(
            "Branching",
            [("Root", -1), ("Left", 0), ("LeftHand", 1), ("Right", 0), ("RightHand", 3)]
        );

    static Skeleton Rig(string name, (string Name, int Parent)[] joints) {
        Assert.True(
            Skeleton.TryCreate(
                new() {
                    Name = name,
                    Joints = [
                        .. joints.Select(
                            joint => new SkeletonJoint {
                                Name = joint.Name, Parent = joint.Parent, InverseBindPose = Matrix4x4.Identity
                            }
                        )
                    ]
                },
                out var skeleton,
                out var error
            ),
            error
        );

        return skeleton!;
    }
}
