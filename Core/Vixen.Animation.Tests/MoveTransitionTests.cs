// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Motions;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>Getting from one move to another without the seam showing.</summary>
/// <remarks>
///     <para>
///         <b>Measured rather than eyeballed, which is the phase's exit criterion.</b> A walk↔run
///         ladder is checked with a foot-slide metric over a recorded run, and an upper-body carry
///         set over that ladder is checked for contact alignment the same way. "It looks fine" is
///         not a thing a test can say and not a thing a regression can fail.
///     </para>
///     <para>
///         The rigs are the suite's usual measurable ones: a joint driven on a known cycle, so the
///         phase a pose implies can be recovered from the pose and compared to the phase that was
///         asked for.
///     </para>
/// </remarks>
public sealed class MoveTransitionTests {
    static readonly Skeleton Rig = TestRigs.Chain();

    [Fact]
    public void AWildcardRuleIsTheDefaultAndTheFirstMatchWins() {
        var policy = new RuleTransitionPolicy(
            new TransitionRule(FacetPredicate.Of(("gait", "run")), FacetPredicate.Any, new(0.2f)),
            new TransitionRule(FacetPredicate.Any, FacetPredicate.Of(("role", "stop")), new(0.12f)),
            new TransitionRule(FacetPredicate.Any, FacetPredicate.Any, new(0.25f))
        );

        var run = Move("run", ("gait", "run"));
        var walk = Move("walk", ("gait", "walk"));
        var stop = Move("stop", ("role", "stop"));

        Assert.True(policy.TryResolve(run, walk, out var fromRun));
        Assert.Equal(0.2f, fromRun.Duration);

        Assert.True(policy.TryResolve(walk, stop, out var toStop));
        Assert.Equal(0.12f, toStop.Duration);

        // ⚠ Run → stop matches both of the first two rules, and the first one wins. That is the
        // whole of "first match wins" and the reason order is the authoring surface.
        Assert.Equal(0, policy.RuleFor(run, stop));
        Assert.True(policy.TryResolve(run, stop, out var both));
        Assert.Equal(0.2f, both.Duration);

        Assert.True(policy.TryResolve(walk, run, out var fallback));
        Assert.Equal(0.25f, fallback.Duration);
        Assert.Equal(2, policy.RuleFor(walk, run));
    }

    /// <summary>Nothing playing matches only the wildcard, because there is no "from".</summary>
    [Fact]
    public void TheFirstMoveMatchesOnlyRulesThatNameNoSource() {
        var policy = new RuleTransitionPolicy(
            new TransitionRule(FacetPredicate.Of(("gait", "run")), FacetPredicate.Any, new(0.2f)),
            new TransitionRule(FacetPredicate.Any, FacetPredicate.Any, new(0.25f))
        );

        Assert.Equal(1, policy.RuleFor(null, Move("walk", ("gait", "walk"))));
    }

    [Fact]
    public void AForbiddenTransitionLeavesTheMovePlaying() {
        var set = MoveSet.Of("s", Move("walk", ("gait", "walk")), Move("swim", ("gait", "swim")));

        var motion = new MoveSetMotion(
            set,
            transitions: new RuleTransitionPolicy(
                new TransitionRule(FacetPredicate.Of(("gait", "walk")), FacetPredicate.Of(("gait", "swim")), TransitionSpec.Forbidden),
                new TransitionRule(FacetPredicate.Any, FacetPredicate.Any, new(0f))
            )
        );

        motion.Ask(Query("walk"));
        Assert.Equal("walk", motion.Current!.Name);

        motion.Ask(Query("swim"));
        Assert.Equal("walk", motion.Current!.Name);
    }

    [Fact]
    public void EasingShapesTheBlendAndACutIsInstant() {
        var linear = new TransitionSpec(1f);
        var smooth = new TransitionSpec(1f, BlendEasing.SmoothStep);

        Assert.Equal(0.25f, linear.WeightAt(0.25f), 1e-4f);
        Assert.Equal(0.5f, smooth.WeightAt(0.5f), 1e-4f);
        Assert.True(smooth.WeightAt(0.25f) < linear.WeightAt(0.25f));
        Assert.Equal(1f, new TransitionSpec(0f).WeightAt(0f));
    }

    /// <summary>⚠ Contacts align, not fractions. The reason a move carries a foot phase.</summary>
    [Fact]
    public void ClosestFootAlignsContactsWhereverTheClipsWereTrimmed() {
        // Two cycles whose starts disagree by a quarter and whose contacts therefore also do.
        var from = Move("walk", 0f, ("gait", "walk"));
        var to = Move("carry", 0.25f, ("gait", "carry"));

        var source = new StubPhase(phase: 0.6f, footPhase: from.Traits.FootPhase);

        // Following the fraction puts the follower at 0.6 and its contact a quarter out of step.
        Assert.Equal(0.6f, PhaseSource.Follow(source).Resolve(0f, to.Traits.FootPhase), 1e-4f);

        // Following the contact offsets by the difference between them, so the two plant together.
        Assert.Equal(0.85f, PhaseSource.FollowFootfall(source).Resolve(0f, to.Traits.FootPhase), 1e-4f);
    }

    [Fact]
    public void AnOwnPhaseSourceLeavesTheClockAlone() {
        Assert.Equal(0.4f, PhaseSource.Own.Resolve(0.4f, 0.25f), 1e-4f);

        // And a source with no cycle right now is the same as having none.
        Assert.Equal(0.4f, PhaseSource.Follow(new StubPhase(0f, 0f, has: false)).Resolve(0.4f, 0.25f), 1e-4f);
    }

    /// <summary>A move set reports its own cycle, so an upper body can hang off it.</summary>
    [Fact]
    public void AMoveSetIsAPhaseSource() {
        var set = MoveSet.Of("legs", Move("walk", 0.1f, ("gait", "walk")));
        var motion = new MoveSetMotion(set);

        Assert.False(motion.TryGetPhase(out _, out _));

        motion.Ask(Query("walk"));
        Evaluate(motion, 0.7f, 0.6f);

        Assert.True(motion.TryGetPhase(out var phase, out var foot));
        Assert.Equal(0.7f, phase, 1e-4f);
        Assert.Equal(0.1f, foot, 1e-4f);
    }

    /// <summary>The exit criterion: a gait ladder whose feet do not skate through the changes.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What is measured.</b> Each move drives one joint through a full turn over its cycle,
    ///         so the joint's angle <i>is</i> the phase and a pose can be read back as one. A skate is
    ///         a discontinuity in that phase between consecutive frames beyond what the frame's own
    ///         advance accounts for — a foot that jumped rather than stepped.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Recorded over a run rather than sampled at the transition</b>, because the failure
    ///         this catches is not at the moment of the change: a phase carried wrongly looks correct
    ///         on the first frame and drifts. The whole ladder is walked, up and down.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AGaitLadderDoesNotSkate() {
        var set = MoveSet.Of(
            "ladder",
            // ⚠ Trimmed differently on purpose. With every contact at zero the offset never moves
            // and the metric below measures nothing but the frame advance — the test would pass
            // against a build that dropped the phase entirely.
            Cycle("walk", 1.4f, 0f, ("gait", "walk")),
            Cycle("run", 4.2f, 0.37f, ("gait", "run")),
            Cycle("sprint", 6.8f, 0.71f, ("gait", "sprint"))
        );

        var motion = new MoveSetMotion(
            set,
            transitions: new RuleTransitionPolicy(
                new TransitionRule(FacetPredicate.Any, FacetPredicate.Any, new(0.2f, Sync: SyncMode.ClosestFoot))
            )
        );

        var worst = Walk(motion, [1.4f, 4.2f, 6.8f, 4.2f, 1.4f]);

        // A tenth of a cycle. A carried phase is exact and a dropped one is half a cycle out, so the
        // bar is well clear of both and there is nothing to tune.
        Assert.True(worst < 0.1f, $"worst phase jump was {worst:F4} of a cycle");
    }

    /// <summary>And the other half of it: an upper body that stays with the feet.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what a masked layer alone does not give.</b> A carry cycle on its own clock
    ///     over a walk drifts against the footfalls, and the drift is what reads as two animations.
    ///     The same recorded ladder is walked and the two cycles' contacts are compared every frame.
    /// </remarks>
    [Fact]
    public void AnUpperBodySetStaysWithTheFootfallsThroughEveryGaitChange() {
        var legs = new MoveSetMotion(
            MoveSet.Of(
                "legs",
                Cycle("walk", 1.4f, 0f, ("gait", "walk")),
                Cycle("run", 4.2f, 0f, ("gait", "run"))
            ),
            transitions: new RuleTransitionPolicy(
                new TransitionRule(FacetPredicate.Any, FacetPredicate.Any, new(0.2f, Sync: SyncMode.ClosestFoot))
            )
        );

        // ⚠ The carry cycle is trimmed a quarter of a turn away from the walk's, which is the whole
        // point: aligning fractions would leave it a quarter out of step for ever.
        var arms = new MoveSetMotion(MoveSet.Of("arms", Cycle("carry", 1.4f, 0.25f, ("gait", "carry"))));
        arms.Phase = PhaseSource.FollowFootfall(legs);

        legs.Ask(Query("walk"));
        arms.Ask(Query("carry"));

        var worst = 0f;

        foreach (var speed in new[] { 1.4f, 4.2f, 1.4f }) {
            legs.Ask(Query(speed > 3f ? "run" : "walk", speed));

            for (var frame = 1; frame <= 40; frame++) {
                var time = frame / 40f % 1f;
                var previous = (frame - 1) / 40f % 1f;

                Evaluate(legs, time, previous);
                Evaluate(arms, time, previous);

                Assert.True(legs.TryGetPhase(out var legPhase, out var legFoot));
                Assert.True(arms.TryGetPhase(out _, out var armFoot));

                // Where each one's next contact falls, as a fraction of the cycle. If the upper body
                // is in step these agree; if it is free-running they drift apart.
                var driven = arms.Phase.Resolve(time, armFoot);
                var contact = Distance(driven, armFoot);
                var reference = Distance(legPhase, legFoot);

                worst = MathF.Max(worst, MathF.Abs(contact - reference));
            }
        }

        Assert.True(worst < 1e-3f, $"contacts drifted by {worst:F4} of a cycle");
    }

    /// <summary>Re-asking the same question does not restart the transition.</summary>
    /// <remarks>
    ///     ⚠ A character that keeps asking never finishes changing, and the query is asked every
    ///     frame by design.
    /// </remarks>
    [Fact]
    public void AskingForTheMoveAlreadyPlayingChangesNothing() {
        var set = MoveSet.Of("s", Cycle("walk", 1.4f, 0f, ("gait", "walk")), Cycle("run", 4.2f, 0f, ("gait", "run")));

        var motion = new MoveSetMotion(
            set,
            transitions: new RuleTransitionPolicy(new TransitionRule(FacetPredicate.Any, FacetPredicate.Any, new(0.5f)))
        );

        motion.Ask(Query("walk"));
        Assert.True(motion.Ask(Query("run", 4.2f)));

        Evaluate(motion, 0.1f, 0f);
        var partway = motion.TransitionWeight;

        Assert.False(motion.Ask(Query("run", 4.2f)));
        Evaluate(motion, 0.2f, 0.1f);

        Assert.True(motion.TransitionWeight > partway);
    }

    /// <summary>Walks a ladder of speeds and reports the worst discontinuity in the contact clock.</summary>
    /// <remarks>
    ///     ⚠ <b>The contact timeline, not the raw phase.</b> A move whose cycle is trimmed elsewhere
    ///     is <i>meant</i> to jump in phase when it comes in — that jump is the offset doing its job.
    ///     What must not jump is when the next foot lands, because that is what an eye reads and what
    ///     a skating foot actually is. Measuring the phase would pass a build with no offset at all.
    /// </remarks>
    static float Walk(MoveSetMotion motion, float[] speeds) {
        var worst = 0f;
        var previousContact = float.NaN;

        foreach (var speed in speeds) {
            motion.Ask(Query(speed > 5f ? "sprint" : speed > 3f ? "run" : "walk", speed));

            for (var frame = 1; frame <= 40; frame++) {
                var time = frame / 40f % 1f;
                var previous = (frame - 1) / 40f % 1f;

                Evaluate(motion, time, previous);
                Assert.True(motion.TryGetPhase(out var phase, out var foot));

                // How long until the next contact, as a fraction of a cycle. It counts down by one
                // frame each frame and wraps at a landing.
                var contact = Distance(phase, foot);

                if (!float.IsNaN(previousContact)) {
                    var moved = Wrap(previousContact - contact);
                    worst = MathF.Max(worst, MathF.Abs(moved - (1f / 40f)));
                }

                previousContact = contact;
            }
        }

        return worst;
    }

    static void Evaluate(MoveSetMotion motion, float time, float previous) {
        var pose = new BoneTransform[Rig.JointCount];

        motion.Evaluate(
            new MotionContext(new AnimationParameters(), new PoseScratch(Rig.JointCount), time, previous, 0, false, null, 0, "s", 1f),
            pose
        );
    }

    static float Distance(float phase, float contact) {
        var ahead = contact - phase;
        return ahead < 0f ? ahead + 1f : ahead;
    }

    static float Wrap(float phase) {
        var wrapped = phase % 1f;
        return wrapped < 0f ? wrapped + 1f : wrapped;
    }

    static MoveQuery Query(string gait, float speed = 1.4f) =>
        new() { Required = FacetSet.Of(("gait", gait)), Numeric = new() { Speed = speed } };

    static MoveEntry Move(string name, params (string Key, string Value)[] facets) => Move(name, 0f, facets);

    static MoveEntry Move(string name, float footPhase, params (string Key, string Value)[] facets) =>
        new(name, Clip(), FacetSet.Of(facets), new() { FootPhase = footPhase });

    static MoveEntry Cycle(string name, float speed, float footPhase, params (string Key, string Value)[] facets) =>
        new(name, Clip(), FacetSet.Of(facets), new() { Speed = speed, FootPhase = footPhase });

    static ClipMotion Clip() => new(AnimationClip.Create(TestRigs.Hold("held", "Mid", Vector3.UnitY), Rig));

    sealed class StubPhase(float phase, float footPhase, bool has = true) : IPhaseSource {
        public bool TryGetPhase(out float value, out float foot) {
            value = phase;
            foot = footPhase;

            return has;
        }
    }
}
