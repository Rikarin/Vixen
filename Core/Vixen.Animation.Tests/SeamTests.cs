// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>
///     Doc 34's Part 4, enforced: every seam implemented twice, and the second one different enough
///     to prove the shape is not the default's shape wearing a mask.
/// </summary>
public class SeamTests {
    /// <summary>
    ///     ⚠ <b>The rule the plan says is enforced in review, enforced by a test instead.</b> Review
    ///     catches this on the day the interface is added and never again; the assembly can be asked
    ///     every build. A seam whose only implementation is the default is a seam shaped like the
    ///     default, and nobody finds that out until the second implementation is somebody's deadline.
    /// </summary>
    [Theory]
    [InlineData(typeof(IMoveSelector))]
    [InlineData(typeof(IMoveScorer))]
    [InlineData(typeof(IGaitModel))]
    [InlineData(typeof(ITransitionPolicy))]
    [InlineData(typeof(IConstraintFrame))]
    [InlineData(typeof(IBindingSource))]
    [InlineData(typeof(IConstraintArbiter))]
    [InlineData(typeof(IConstraintScheduler))]
    [InlineData(typeof(IChainSolver))]
    [InlineData(typeof(IProxyShapePoser))]
    [InlineData(typeof(IVariationSource))]
    public void EverySeamIsImplementedTwice(Type seam) {
        var shipped = Implementations(typeof(ConstraintStack).Assembly, seam);
        var tested = Implementations(typeof(SeamTests).Assembly, seam);

        Assert.NotEmpty(shipped);

        Assert.True(
            shipped.Count + tested.Count >= 2,
            $"{seam.Name} has {shipped.Count} shipped and {tested.Count} test implementation(s). "
            + "Part 4 asks for at least two, and one of them somewhere other than the default."
        );
    }

    static List<Type> Implementations(Assembly assembly, Type seam) =>
        [.. assembly.GetTypes().Where(type => type is { IsAbstract: false, IsInterface: false } && seam.IsAssignableFrom(type))];

    // ── Each one, exercised through the interface ────────────────────────────

    /// <summary>A table-driven chooser never consults the scorer, and the stack does not mind.</summary>
    [Fact]
    public void ASelectorMayIgnoreTheScorerEntirely() {
        var moves = Locomotion();
        var selector = new TableTestSelector(("role=loop", "run"));

        var chosen = selector.Choose(
            moves,
            new MoveQuery { Required = FacetSet.Of(MoveRole.Facet(MoveRole.Loop)), Numeric = new() { Speed = 0.2f } },
            DefaultMoveScorer.Shared
        );

        // The shipped selector would have picked the walk at that speed. The table says otherwise and
        // the table wins, which is the whole of what the seam is for.
        Assert.True(chosen.HasMove);
        Assert.Equal("run", moves[chosen.Index].Name);
        Assert.Equal(0, selector.ScorerCalls);

        Assert.False(selector.Choose(moves, new MoveQuery(), DefaultMoveScorer.Shared).HasMove);
    }

    /// <summary>A scorer is "rank a candidate" and not "the default's arithmetic with a hook in it".</summary>
    [Fact]
    public void AScorerMayRankOnAnythingAtAll() {
        var moves = Locomotion();

        var chosen = QueryMoveSelector.Shared.Choose(
            moves,
            new MoveQuery { Numeric = new() { Speed = 9f } },
            new AlphabeticalTestScorer()
        );

        // Nine metres a second is the run's question and the alphabet's answer is the idle.
        Assert.Equal("idle", moves[chosen.Index].Name);
    }

    /// <summary>
    ///     ⚠ <b>A wheeled body's speed is signed and its turn rate is a function of it.</b> Reverse is
    ///     its own move, not a walk played backwards, and a model that answered an unsigned speed
    ///     would make that unsayable.
    /// </summary>
    [Fact]
    public void AGaitModelMayDescribeSomethingThatIsNotABiped() {
        var wheels = new WheeledTestGaitModel();
        var targets = new MoveTargets();

        // Facing +Z and moving backwards along it.
        wheels.Describe(new(new Vector2(0f, -3f), 0f, 2f, true), ref targets);

        Assert.Equal(-3f, targets.Speed, 3);
        Assert.True(targets.TurnRate < 2f, "a vehicle cannot turn as fast as it was asked to");

        // Stopped: it cannot turn at all, which a biped very much can.
        wheels.Describe(new(Vector2.Zero, 0f, 2f, true), ref targets);
        Assert.Equal(0f, targets.TurnRate, 4);

        var biped = new BipedGaitModel();
        var onTheSpot = new MoveTargets();

        biped.Describe(new(Vector2.Zero, 0f, 2f, true), ref onTheSpot);
        Assert.NotEqual(0f, onTheSpot.TurnRate);
    }

    /// <summary>A policy with no rules at all, which is the case the seam was added for.</summary>
    [Fact]
    public void ATransitionPolicyMayAskSomethingElse() {
        var moves = Locomotion();
        var committed = false;

        var policy = new AskingTestPolicy((_, _) => committed ? null : new TransitionSpec(0.05f));

        Assert.True(policy.TryResolve(moves[0], moves[1], out var quick));
        Assert.Equal(0.05f, quick.Duration, 3);

        committed = true;

        Assert.False(policy.TryResolve(moves[0], moves[1], out var refused));
        Assert.False(refused.Allowed);
        Assert.Equal(2, policy.Asked);
    }

    /// <summary>
    ///     ⚠ <b>And the rule holds through the type that consumes them.</b> An interface implemented
    ///     twice and reached only through the default is a seam nobody is forced through, which the
    ///     plan says rots — so the stack is asked for its arbiter and its solver by type.
    /// </summary>
    [Fact]
    public void NoDefaultIsReachableExceptThroughItsInterface() {
        var skeleton = TestRigs.Chain();
        var stack = new ConstraintStack(skeleton);

        Assert.IsAssignableFrom<IConstraintArbiter>(stack.Arbiter);
        Assert.IsAssignableFrom<IChainSolver>(stack.Solver);

        // The properties are the interfaces, not the shipped classes: a caller cannot reach a default
        // member without casting, and nothing in the assembly does.
        Assert.Equal(typeof(IConstraintArbiter), typeof(ConstraintStack).GetProperty(nameof(ConstraintStack.Arbiter))?.PropertyType);
        Assert.Equal(typeof(IChainSolver), typeof(ConstraintStack).GetProperty(nameof(ConstraintStack.Solver))?.PropertyType);
    }

    static MoveSet Locomotion() =>
        MoveSet.Of(
            "locomotion",
            new MoveEntry("idle", StillTestMotion.Shared, FacetSet.Of(MoveRole.Facet(MoveRole.Idle))),
            new MoveEntry(
                "walk",
                StillTestMotion.Shared,
                FacetSet.Of(MoveRole.Facet(MoveRole.Loop)),
                new() { Speed = 1.4f, MinRate = 0.85f, MaxRate = 1.15f }
            ),
            new MoveEntry(
                "run",
                StillTestMotion.Shared,
                FacetSet.Of(MoveRole.Facet(MoveRole.Loop)),
                new() { Speed = 3.6f, MinRate = 0.8f, MaxRate = 1.2f }
            )
        );
}
