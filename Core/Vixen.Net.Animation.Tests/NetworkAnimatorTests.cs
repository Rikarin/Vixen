// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Ecs;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Replication;
using Vixen.Net.Rules;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Animation.Tests;

/// <summary>Networked animation: the inputs go on the wire, not the pose.</summary>
public sealed class NetworkAnimatorTests {
    static readonly PlayerId Receiving = new(4);

    /// <summary>The authority publishes what its animator is doing.</summary>
    [Fact]
    public void TheAuthorityPublishesItsParametersAndState() {
        using var world = new World("animator-capture");
        var capture = new NetworkAnimatorCaptureSystem();

        var animator = Build(out var speed);
        animator.Parameters.SetFloat(speed, 4.5f);
        animator.Speed = 0.5f;
        animator.Update(0.1f);

        var entity = Spawn(world, animator);
        capture.Publish(world);

        var state = world.Read<NetworkAnimator>(entity);
        var parameters = world.Read<NetworkAnimatorParameters>(entity);

        Assert.Equal(0.5f, state.Speed, 3);
        Assert.Equal(1, parameters.Count);
        Assert.Equal(4.5f, parameters.Values[0], 3);
        Assert.Equal(1, capture.PublishedCount);
    }

    /// <summary>A receiver drives its own animator from the parameters it was sent.</summary>
    /// <remarks>
    ///     The whole design: the receiving animator evaluates its own transitions and produces its
    ///     own pose, so events, root motion and IK all keep working locally. Sending the pose would
    ///     have replaced all of that with a puppet.
    /// </remarks>
    [Fact]
    public void AReceiverDrivesItsOwnAnimatorFromTheParameters() {
        using var world = new World("animator-apply");
        var apply = new NetworkAnimatorApplySystem { Local = Receiving };

        var animator = Build(out var speed);
        var entity = Spawn(world, animator);

        ref var parameters = ref world.Get<NetworkAnimatorParameters>(entity);
        parameters.Count = 1;
        parameters.Values[0] = 7.5f;

        world.Get<NetworkAnimator>(entity).Speed = 2f;

        apply.Apply(world);

        Assert.Equal(7.5f, animator.Parameters.GetFloat(speed), 3);
        Assert.Equal(2f, animator.Speed, 3);
        Assert.Equal(1, apply.AppliedCount);
    }

    /// <summary>An animator already in the right state is left to run.</summary>
    /// <remarks>
    ///     <para>
    ///         The obvious implementation calls <c>Play</c> every tick, which restarts the state
    ///         every tick, and nothing ever animates. It presents as the animation being broken
    ///         rather than as the network being wrong, which is why it is worth a test of its own
    ///         rather than a comment.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AnAnimatorAlreadyInTheRightState_IsNotRestarted() {
        using var world = new World("animator-no-restart");
        var apply = new NetworkAnimatorApplySystem { Local = Receiving };

        var animator = Build(out _);
        animator.Update(0.25f);

        var entity = Spawn(world, animator);
        var before = animator.Layers[0].States.NormalizedTime;

        Assert.True(before > 0f, "The animator should have advanced before this is meaningful.");

        for (var tick = 0; tick < 5; tick++) {
            apply.Apply(world);
        }

        Assert.Equal(before, animator.Layers[0].States.NormalizedTime, 4);
        Assert.Equal(0, apply.CorrectedCount);
    }

    /// <summary>An animator that has diverged is corrected onto the state it was told.</summary>
    /// <remarks>
    ///     A late joiner has no history to have derived the state from, and a state machine's
    ///     position is a function of every parameter it has ever seen — so a single missed edge is
    ///     permanent without this.
    /// </remarks>
    [Fact]
    public void AnAnimatorThatHasDivergedIsCorrected() {
        using var world = new World("animator-correct");
        var apply = new NetworkAnimatorApplySystem { Local = Receiving };

        var animator = Build(out _, twoStates: true);
        animator.Update(0.1f);

        var entity = Spawn(world, animator);

        Assert.Equal(0, animator.Layers[0].States.CurrentState);

        world.Get<NetworkAnimator>(entity).State = 1;
        apply.Apply(world);

        Assert.Equal(1, apply.CorrectedCount);

        // Crossfaded rather than cut, so the machine is transitioning toward it rather than snapped.
        Assert.True(animator.Layers[0].States.IsTransitioning || animator.Layers[0].States.CurrentState == 1);
    }

    /// <summary>Authority comes from the rules, the same question everything else asks.</summary>
    [Fact]
    public void AuthorityComesFromTheRules() {
        using var world = new World("animator-authority");

        var ownership = new NetworkOwnership();
        var rules = new NetworkRulesRegistry(ownership);
        var mine = new PlayerId(4);

        ownership.SetOwner(new(1), mine);
        rules.Set(new(1), NetworkRules.OwnerAuthoritative);

        var capture = new NetworkAnimatorCaptureSystem { Rules = rules, Local = mine };
        var apply = new NetworkAnimatorApplySystem { Rules = rules, Local = mine };

        Assert.True(capture.IsAuthority(new(1)));
        Assert.True(apply.IsAuthority(new(1)));

        // And a peer that is not the owner takes the other branch on the same rule.
        var theirs = new NetworkAnimatorCaptureSystem { Rules = rules, Local = new(5) };
        Assert.False(theirs.IsAuthority(new(1)));
    }

    /// <summary>The parameters survive the wire.</summary>
    [Fact]
    public void ParametersRoundTrip() {
        using var world = new World("animator-wire");
        var replicator = new NetworkAnimatorParametersReplicator();
        var buffer = new byte[256];

        var entity = world.Create(new NetworkId(1), default(NetworkAnimatorParameters));

        ref var parameters = ref world.Get<NetworkAnimatorParameters>(entity);
        parameters.Count = 3;
        parameters.Values[0] = 1.5f;
        parameters.Values[1] = -2.25f;
        parameters.Values[2] = 1024f;

        var writer = new Messaging.BitWriter(buffer);
        replicator.Write(world, entity, ref writer);
        Assert.True(writer.TryFinish(out var bits));

        using var receiving = new World("animator-wire-client");
        var arrived = receiving.Create(new NetworkId(1));
        var reader = new Messaging.BitReader(bits);

        Assert.True(replicator.Apply(receiving, arrived, ref reader));

        var got = receiving.Read<NetworkAnimatorParameters>(arrived);

        Assert.Equal(3, got.Count);
        Assert.Equal(1.5f, got.Values[0]);
        Assert.Equal(-2.25f, got.Values[1]);
        Assert.Equal(1024f, got.Values[2]);
    }

    static Entity Spawn(World world, Animator animator) {
        var entity = world.Create(
            new NetworkId(1),
            new AnimatorComponent { Value = animator },
            default(NetworkAnimator),
            default(NetworkAnimatorParameters)
        );

        return entity;
    }

    /// <summary>A one-joint rig with a state machine over it, which is all these tests need.</summary>
    /// <remarks>
    ///     The motion is a stub rather than a real clip, deliberately. What is under test is which
    ///     values reach the wire and what the receiver does with them — a real clip would drag the
    ///     asset types in and make this a test of the animation system as well as of the networking.
    /// </remarks>
    static Animator Build(out int speedParameter, bool twoStates = false) {
        var animator = new Animator(Rig());
        speedParameter = animator.Parameters.Declare("Speed", AnimationParameterType.Float);

        var states = twoStates
            ? new[] { new AnimationState("Idle", new Still()), new AnimationState("Run", new Still()) }
            : [new AnimationState("Idle", new Still())];

        animator.AddLayer("Base", new AnimationStateMachine(states));

        return animator;
    }

    static Skeleton Rig() {
        Assert.True(
            Skeleton.TryCreate(
                new() {
                    Name = "Test",
                    Joints = [new() { Name = "Root", Parent = -1, InverseBindPose = Matrix4x4.Identity }]
                },
                out var skeleton,
                out var error
            ),
            error
        );

        return skeleton!;
    }

    /// <summary>A motion that poses nothing and lasts a second.</summary>
    /// <remarks>
    ///     Rooted, because this namespace is <c>Vixen.Net.Animation.Tests</c> and <c>Vixen.Net.Motion</c>
    ///     is a real namespace an enclosing scope finds first — and an enclosing namespace beats a
    ///     using-alias, so the alias does not help here. The same collision <c>Vixen.Net.Physics</c>
    ///     has with <c>Vixen.Physics</c>.
    /// </remarks>
    sealed class Still : global::Vixen.Animation.Motions.Motion {
        public override float Length(AnimationParameters parameters) => 1f;

        public override RootMotionDelta Evaluate(in MotionContext context, Span<BoneTransform> destination) =>
            RootMotionDelta.None;
    }
}
