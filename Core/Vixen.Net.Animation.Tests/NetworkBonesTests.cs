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

    /// <summary>The shipped replicator's bytes, pinned — a narrowing table must not move them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A length is not coverage and a round trip is not either.</b> Both halves of a
    ///         round trip are compiled from this tree in this build, so anything that moves the write
    ///         and the read together — a reordered lane list, a swapped pair of bones — leaves a
    ///         round-trip test green and changes what a peer built yesterday decodes. Only bytes
    ///         nobody can move on both sides at once say that.
    ///     </para>
    ///     <para>
    ///         The name is pinned beside them for the same reason: it is what
    ///         <c>ReplicationRegistry.ManifestHash</c> is computed from, so a peer whose table differs
    ///         is refused at the handshake rather than decoding poses into plausible wrong rotations.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheFullTableIsTheWireThatShipsToday() {
        var replicator = new NetworkBonesReplicator();

        Assert.True(replicator.Precision.IsFull);
        Assert.Equal("Vixen.Net.Animation.NetworkBones", replicator.TypeName);
        Assert.Equal(ReplicationRegistry.HashTypeName("Vixen.Net.Animation.NetworkBones"), replicator.TypeId);

        // 8 bits of count and twenty-four whole rotations.
        Assert.Equal(8 + (24 * 32), DeltaCodec.TotalBits(replicator.Lanes));

        Assert.Equal(
            "18C31826868F1AA59EE70BE0B3BF0959E1FB1A14EB220C2BEC820966D40058AABAD014B39F5E0BC583FAECF367EAED674CD2096032B69A1A1A3711F26D6B550B42BB74993E7705A0508BD7615EDB9760798768E191FFF9E78FB70BA0AC9BDC59BD",
            Encode(replicator)
        );
    }

    /// <summary>A narrowed table is a different type on the wire, and different bytes.</summary>
    /// <remarks>
    ///     The second string is the whole claim. It is not derivable from the first by any rule the
    ///     reader of a diff has to trust — it is what the codec emitted, committed, so that a change
    ///     to the packing, to the lane order or to the narrowing arithmetic is a failing test rather
    ///     than a silent renegotiation with every build that already shipped.
    /// </remarks>
    [Fact]
    public void ANarrowedTableIsADifferentTypeAndDifferentBytes() {
        var narrowed = new NetworkBonesReplicator(Ragdoll);

        Assert.False(narrowed.Precision.IsFull);
        Assert.Equal("Vixen.Net.Animation.NetworkBones[AAAA88886666666666666666]", narrowed.TypeName);
        Assert.NotEqual(new NetworkBonesReplicator().TypeId, narrowed.TypeId);

        // Four bones at 32 bits, four at 26 and sixteen at 20, plus the count.
        Assert.Equal(8 + (4 * 32) + (4 * 26) + (16 * 20), DeltaCodec.TotalBits(narrowed.Lanes));

        Assert.Equal("18C31826868F1AA59EE70BE0B3BF0959E1BF42AD2BCCCA2E26263580A9BA4CF3695B80CE73E67D4E9E20A3AA1913F276B5404BD97305527BE1F5077A8B21F9798EBBE0BA9CBD", Encode(narrowed));
    }

    /// <summary>The record shrinks by the amount the table says, and the pose still arrives.</summary>
    [Fact]
    public void ANarrowedPoseIsSmallerAndStillArrives() {
        using var world = new World("bones-narrow");
        var full = new NetworkBonesReplicator();
        var narrowed = new NetworkBonesReplicator(Ragdoll);

        var spine = Quaternion.FromAxisAngle(Vector3.UnitY, 0.25f);
        var finger = Quaternion.FromAxisAngle(Vector3.UnitX, -1f);
        var entity = world.Create(new NetworkId(1), default(NetworkBones));

        ref var bones = ref world.Get<NetworkBones>(entity);
        bones.Count = 24;
        bones.Rotations[0] = MathCodec.PackRotation(spine);
        bones.Rotations[20] = MathCodec.PackRotation(finger);

        Assert.Equal(776, DeltaCodec.TotalBits(full.Lanes));
        Assert.Equal(560, DeltaCodec.TotalBits(narrowed.Lanes));

        var buffer = new byte[256];
        var writer = new BitWriter(buffer);
        narrowed.Write(world, entity, ref writer);
        Assert.True(writer.TryFinish(out var packet));

        using var receiving = new World("bones-narrow-client");
        var arrived = receiving.Create(new NetworkId(1));
        var reader = new BitReader(packet);
        Assert.True(narrowed.Apply(receiving, arrived, ref reader));

        var got = receiving.Read<NetworkBones>(arrived);

        // Slot 0 is at full precision, so it is exactly what was sent.
        Assert.Equal(bones.Rotations[0], got.Rotations[0]);

        // Slot 20 lost four bits a component. Six bits over ±1/√2 is a step of about 0.022, so the
        // rotation is within a couple of degrees rather than within a tenth of one.
        AssertRotationWithin(finger, MathCodec.UnpackRotation(got.Rotations[20]), 0.9995f);
    }

    /// <summary>A bone that did not move still costs one bit when its lane is narrower.</summary>
    /// <remarks>
    ///     The property the packed storage exists for, and the one narrowing could plausibly have
    ///     broken: if the narrowing went through <c>UnpackRotation</c> and re-encoded, a re-normalised
    ///     quaternion would come back with different bits from one tick to the next and every lane
    ///     would look changed.
    /// </remarks>
    [Fact]
    public void AStillBoneStillCostsOneBitWhenNarrowed() {
        using var world = new World("bones-narrow-delta");
        var narrowed = new NetworkBonesReplicator(NetworkBonePrecision.Uniform(NetworkBonePrecision.MinBits));
        var entity = world.Create(new NetworkId(1), default(NetworkBones));

        ref var bones = ref world.Get<NetworkBones>(entity);
        bones.Count = 24;

        for (var index = 0; index < 24; index++) {
            bones.Rotations[index] = MathCodec.PackRotation(Quaternion.FromAxisAngle(Vector3.UnitY, index * 0.1f));
        }

        var first = Write(narrowed, world, entity);

        world.Get<NetworkBones>(entity).Rotations[3] =
            MathCodec.PackRotation(Quaternion.FromAxisAngle(Vector3.UnitX, 1.4f));

        var second = Write(narrowed, world, entity);

        var previous = new BitReader(first);
        var now = new BitReader(second);
        var delta = new BitWriter(new byte[256]);

        Assert.True(DeltaCodec.TryEncode(narrowed.Lanes, ref previous, ref now, ref delta, default));

        // Twenty-five lane bits, plus the one changed lane whole and its two-bit selector.
        Assert.Equal(25 + 14, delta.BitsWritten);
    }

    /// <summary>Widening a narrowed rotation gives one that narrows back to itself.</summary>
    /// <remarks>
    ///     What lets a peer receive a pose and re-send it — a host, a listen server — without losing a
    ///     second helping of precision each time it goes round.
    /// </remarks>
    [Fact]
    public void NarrowingIsIdempotentThroughWidening() {
        foreach (var rotation in Rotations()) {
            var packed = MathCodec.PackRotation(rotation);

            for (var bits = NetworkBonePrecision.MinBits; bits <= NetworkBonePrecision.MaxBits; bits++) {
                var narrow = NetworkBonesReplicator.Narrow(packed, bits);
                var wide = NetworkBonesReplicator.Widen(narrow, bits);

                Assert.Equal(narrow, NetworkBonesReplicator.Narrow(wide, bits));
            }
        }
    }

    /// <summary>Widening picks the middle of the interval rather than its floor.</summary>
    /// <remarks>
    ///     ⚠ <b>Measured against the alternative on the same inputs, not against a tolerance.</b> A
    ///     tolerance loose enough for four bits is loose enough for the biased reconstruction too, so
    ///     it would pass either way. Shifting back up alone picks the smallest of the run of
    ///     full-precision levels a narrowed one stands for, every time and on all three components at
    ///     once, which is a systematic lean and not noise.
    /// </remarks>
    [Fact]
    public void WideningCentresTheIntervalRatherThanLeaningOnItsFloor() {
        var centred = 0.0;
        var floored = 0.0;
        var count = 0;

        for (var step = 0; step < 512; step++) {
            var rotation = Quaternion.Normalize(
                new(MathF.Sin(step * 0.37f), MathF.Cos(step * 0.11f), MathF.Sin(step * 0.53f), MathF.Cos(step * 0.29f))
            );

            var packed = MathCodec.PackRotation(rotation);
            var narrow = NetworkBonesReplicator.Narrow(packed, 6);

            centred += Error(rotation, MathCodec.UnpackRotation(NetworkBonesReplicator.Widen(narrow, 6)));
            floored += Error(rotation, MathCodec.UnpackRotation(Floor(narrow, 6)));
            count++;
        }

        Assert.True(
            centred < floored * 0.8,
            $"Centred reconstruction was {centred / count:F6} a rotation and the floored one {floored / count:F6}; "
            + "centring is supposed to be substantially the better of the two."
        );

        // The same shift Widen does, without the half-step, which is the mistake being ruled out.
        static uint Floor(uint narrow, int bits) {
            var drop = NetworkBonePrecision.MaxBits - bits;
            var mask = (1u << bits) - 1;
            var result = narrow & 3u;

            for (var level = 0; level < 3; level++) {
                result |= ((narrow >> (2 + (level * bits))) & mask) << (drop + 2 + (level * NetworkBonePrecision.MaxBits));
            }

            return result;
        }

        static double Error(Quaternion expected, Quaternion actual) {
            var dot = MathF.Abs(
                (expected.X * actual.X) + (expected.Y * actual.Y) + (expected.Z * actual.Z) + (expected.W * actual.W)
            );

            return 1.0 - Math.Min(1.0, dot);
        }
    }

    /// <summary>A width the codec cannot pack, or a table longer than a pose, is refused.</summary>
    [Fact]
    public void ATableTheCodecCannotPackIsRefused() {
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkBonePrecision.Uniform(NetworkBonePrecision.MinBits - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkBonePrecision.Uniform(NetworkBonePrecision.MaxBits + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkBonePrecision.For(new int[NetworkBonesReplicator.MaxBones + 1]));

        // A table shorter than the pose leaves the rest whole rather than guessing.
        var partial = NetworkBonePrecision.For([4, 4]);

        Assert.Equal(4, partial[0]);
        Assert.Equal(NetworkBonePrecision.MaxBits, partial[NetworkBonesReplicator.MaxBones - 1]);
    }

    /// <summary>Four bones a spine and a head, four at eight bits, the rest of the ragdoll at six.</summary>
    static NetworkBonePrecision Ragdoll { get; } = NetworkBonePrecision.For(
        [10, 10, 10, 10, 8, 8, 8, 8, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6]
    );

    /// <summary>Every slot distinct and none of them zero, which is what makes the pin say anything.</summary>
    /// <remarks>
    ///     ⚠ The first version of this filled three slots and left twenty-one at their default. Every
    ///     unset slot packs to zero, zero narrows to zero at every width, and the narrowed listing came
    ///     out byte-identical to the full one but shorter — so the pin was asserting a <i>length</i>,
    ///     which is precisely the thing this repository has already been caught calling coverage.
    /// </remarks>
    static string Encode(NetworkBonesReplicator replicator) {
        using var world = new World("bones-pin");
        var entity = world.Create(new NetworkId(1), default(NetworkBones));

        ref var bones = ref world.Get<NetworkBones>(entity);
        bones.Count = NetworkBonesReplicator.MaxBones;

        for (var slot = 0; slot < NetworkBonesReplicator.MaxBones; slot++) {
            var axis = Vector3.Normalize(new(1f + (slot % 3), 2f - (slot % 5), 0.5f + (slot % 7)));
            bones.Rotations[slot] = MathCodec.PackRotation(Quaternion.FromAxisAngle(axis, 0.31f * (slot + 1)));
        }

        return Convert.ToHexString(Write(replicator, world, entity));
    }

    static byte[] Write(NetworkBonesReplicator replicator, World world, Entity entity) {
        var writer = new BitWriter(new byte[256]);
        replicator.Write(world, entity, ref writer);
        Assert.True(writer.TryFinish(out var packet));

        return packet.ToArray();
    }

    static void AssertRotationWithin(Quaternion expected, Quaternion actual, float dot) {
        var measured = MathF.Abs(
            (expected.X * actual.X) + (expected.Y * actual.Y) + (expected.Z * actual.Z) + (expected.W * actual.W)
        );

        Assert.True(measured > dot, $"Expected {expected} and got {actual}; |dot| was {measured}.");
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
