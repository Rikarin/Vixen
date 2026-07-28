// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Animation.Tests;

/// <summary>The pose itself, for the poses that cannot be derived from inputs.</summary>
public sealed class NetworkBonesTests {
    static readonly PlayerId Receiving = new(4);

    /// <summary>The authority publishes the joints it was told to.</summary>
    [Fact]
    public void TheAuthorityPublishesTheSelectedJoints() {
        using var world = new World("bones-capture");
        var capture = new NetworkBonesCaptureSystem();

        var animator = Build();
        var turn = Quaternion.FromAxisAngle(Vector3.UnitY, 0.5f);
        animator.Pose[2].Rotation = turn;

        var entity = Spawn(world, animator, [2]);
        capture.Publish(world);

        var bones = world.Read<NetworkBones>(entity);

        Assert.Equal(1, bones.Count);
        Assert.Equal(1, capture.PublishedCount);
        Assert.Equal(1, capture.BoneCount);
        AssertSameRotation(turn, MathCodec.UnpackRotation(bones.Rotations[0]));
    }

    /// <summary>A receiver is posed by what arrived, over whatever its own animator produced.</summary>
    /// <remarks>
    ///     Overwriting is the point rather than a compromise. This exists for the cases where the
    ///     receiving animator cannot reproduce the authority's pose at all — a ragdoll, IK against
    ///     local geometry — so what it computed is not an approximation to blend with.
    /// </remarks>
    [Fact]
    public void AReceiverIsPosedByWhatArrived() {
        using var world = new World("bones-apply");
        var apply = new NetworkBonesApplySystem { Local = Receiving };

        var animator = Build();
        animator.Pose[1].Rotation = Quaternion.FromAxisAngle(Vector3.UnitX, 1.2f);

        var entity = Spawn(world, animator, [1]);
        var turn = Quaternion.FromAxisAngle(Vector3.UnitZ, -0.75f);

        ref var bones = ref world.Get<NetworkBones>(entity);
        bones.Count = 1;
        bones.Rotations[0] = MathCodec.PackRotation(turn);

        apply.Apply(world);

        AssertSameRotation(turn, animator.Pose[1].Rotation);
        Assert.Equal(1, apply.AppliedCount);
        Assert.Equal(0, apply.MismatchedCount);
    }

    /// <summary>The translation and scale a rig already has are left alone.</summary>
    /// <remarks>
    ///     A skeleton is rigid: bone lengths do not change, so a joint's translation is its bind pose
    ///     and sending it would be sending a constant. Writing a zeroed one over the bind pose is how
    ///     a character comes apart at the joints, which is why this is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void OnlyTheRotationIsTakenFromTheWire() {
        using var world = new World("bones-rigid");
        var apply = new NetworkBonesApplySystem { Local = Receiving };

        var animator = Build();
        animator.Pose[1].Translation = new(0f, 3f, 0f);
        animator.Pose[1].Scale = new(2f, 2f, 2f);

        var entity = Spawn(world, animator, [1]);

        ref var bones = ref world.Get<NetworkBones>(entity);
        bones.Count = 1;
        bones.Rotations[0] = MathCodec.PackRotation(Quaternion.FromAxisAngle(Vector3.UnitY, 0.3f));

        apply.Apply(world);

        Assert.Equal(new Vector3(0f, 3f, 0f), animator.Pose[1].Translation);
        Assert.Equal(new Vector3(2f, 2f, 2f), animator.Pose[1].Scale);
    }

    /// <summary>Two ends that disagree about a rig say so rather than posing the wrong joints.</summary>
    [Fact]
    public void TwoEndsThatDisagreeAboutARigAreCounted() {
        using var world = new World("bones-mismatch");
        var apply = new NetworkBonesApplySystem { Local = Receiving };

        var animator = Build();
        var entity = Spawn(world, animator, [1, 2]);

        // The sender had one joint selected and this peer has two — a rig that was re-exported into
        // one build and not the other.
        ref var bones = ref world.Get<NetworkBones>(entity);
        bones.Count = 1;
        bones.Rotations[0] = MathCodec.PackRotation(Quaternion.FromAxisAngle(Vector3.UnitY, 0.3f));

        apply.Apply(world);

        Assert.Equal(1, apply.MismatchedCount);

        // Applied as far as the shorter of the two goes: half a right answer beats a limb frozen.
        Assert.NotEqual(Quaternion.Identity, animator.Pose[1].Rotation);
        Assert.Equal(Quaternion.Identity, animator.Pose[2].Rotation);
    }

    /// <summary>A pose survives the wire, and a bone that did not move costs almost nothing.</summary>
    /// <remarks>
    ///     The bandwidth claim, measured rather than asserted. Storing the rotation <i>packed</i> is
    ///     what makes an unchanged bone bit-identical to the last one, which is what lets the delta
    ///     codec spend a single bit on it — and it is the whole reason a pose is affordable for
    ///     anything less than a ragdoll in free fall.
    /// </remarks>
    [Fact]
    public void APoseRoundTripsAndAStillBoneCostsOneBit() {
        using var world = new World("bones-wire");
        var replicator = new NetworkBonesReplicator();
        var buffer = new byte[256];

        var entity = world.Create(new NetworkId(1), default(NetworkBones));
        var turn = Quaternion.FromAxisAngle(Vector3.UnitY, 0.25f);

        ref var bones = ref world.Get<NetworkBones>(entity);
        bones.Count = 3;
        bones.Rotations[0] = MathCodec.PackRotation(turn);
        bones.Rotations[1] = MathCodec.PackRotation(Quaternion.Identity);
        bones.Rotations[2] = MathCodec.PackRotation(Quaternion.FromAxisAngle(Vector3.UnitX, -1f));

        var writer = new BitWriter(buffer);
        replicator.Write(world, entity, ref writer);
        Assert.True(writer.TryFinish(out var whole));

        // The fixed width the lane layout promises, which is what the delta codec checks against.
        Assert.Equal(DeltaCodec.TotalBits(replicator.Lanes), writer.BitsWritten);

        using var receiving = new World("bones-wire-client");
        var arrived = receiving.Create(new NetworkId(1));
        var reader = new BitReader(whole);

        Assert.True(replicator.Apply(receiving, arrived, ref reader));

        var got = receiving.Read<NetworkBones>(arrived);
        Assert.Equal(3, got.Count);
        AssertSameRotation(turn, MathCodec.UnpackRotation(got.Rotations[0]));

        // Now one bone moves and the rest do not. Every unchanged lane is one bit, so the difference
        // is a fraction of the whole record rather than proportional to the rig.
        world.Get<NetworkBones>(entity).Rotations[0] =
            MathCodec.PackRotation(Quaternion.FromAxisAngle(Vector3.UnitY, 0.26f));

        var second = new BitWriter(buffer);
        replicator.Write(world, entity, ref second);
        Assert.True(second.TryFinish(out var moved));

        var previous = new BitReader(whole);
        var now = new BitReader(moved);
        var deltaBuffer = new byte[256];
        var delta = new BitWriter(deltaBuffer);

        Assert.True(DeltaCodec.TryEncode(replicator.Lanes, ref previous, ref now, ref delta, default));

        // One changed 32-bit lane and twenty-four unchanged ones, against 776 bits whole.
        Assert.True(
            delta.BitsWritten < writer.BitsWritten / 8,
            $"A one-bone change cost {delta.BitsWritten} bits of {writer.BitsWritten}."
        );
    }

    /// <summary>Packing a rotation and writing one are the same bits.</summary>
    /// <remarks>
    ///     Not a tautology: <see cref="MathCodec.WriteRotation" /> is now written in terms of
    ///     <see cref="MathCodec.PackRotation" />, and this is what says the two cannot drift apart if
    ///     somebody unpicks that. The wire golden pins the value itself.
    /// </remarks>
    [Fact]
    public void PackingARotationAndWritingOneAgree() {
        var buffer = new byte[8];

        foreach (var rotation in Rotations()) {
            var writer = new BitWriter(buffer);
            writer.WriteRotation(rotation);
            Assert.True(writer.TryFinish(out var packet));

            var reader = new BitReader(packet);
            Assert.True(reader.TryRead(32, out var read));
            Assert.Equal(MathCodec.PackRotation(rotation), read);

            AssertSameRotation(rotation, MathCodec.UnpackRotation(read));
        }
    }

    static IEnumerable<Quaternion> Rotations() {
        yield return Quaternion.Identity;
        yield return Quaternion.FromAxisAngle(Vector3.UnitX, 1f);
        yield return Quaternion.FromAxisAngle(Vector3.UnitY, -2f);
        yield return Quaternion.FromAxisAngle(Vector3.UnitZ, 3f);
        yield return Quaternion.Normalize(new(0.5f, 0.5f, 0.5f, 0.5f));
    }

    /// <summary>Within a tenth of a degree, and either sign, because q and −q are one rotation.</summary>
    static void AssertSameRotation(Quaternion expected, Quaternion actual) {
        var dot = MathF.Abs(
            (expected.X * actual.X) + (expected.Y * actual.Y) + (expected.Z * actual.Z) + (expected.W * actual.W)
        );

        Assert.True(dot > 0.9999f, $"Expected {expected} and got {actual}; |dot| was {dot}.");
    }

    static Entity Spawn(World world, Animator animator, int[] joints) =>
        world.Create(
            new NetworkId(1),
            new AnimatorComponent { Value = animator },
            default(NetworkBones),
            new NetworkBoneSelection { Joints = joints }
        );

    /// <summary>A three-joint chain, which is all these need.</summary>
    static Animator Build() {
        Assert.True(
            Skeleton.TryCreate(
                new() {
                    Name = "Chain",
                    Joints = [
                        new() { Name = "Root", Parent = -1, InverseBindPose = Matrix4x4.Identity },
                        new() { Name = "Spine", Parent = 0, InverseBindPose = Matrix4x4.Identity },
                        new() { Name = "Head", Parent = 1, InverseBindPose = Matrix4x4.Identity }
                    ]
                },
                out var skeleton,
                out var error
            ),
            error
        );

        return new(skeleton!);
    }
}
