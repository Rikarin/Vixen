// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     P5's first exit criterion: two actions tuned to within 2 % of each other, and an agent that
///     switches fewer than three times in sixty seconds with the defaults and more than fifty with
///     inertia disabled.
/// </summary>
/// <remarks>
///     ⚠ <b>Oscillation is the single most visible failure mode of a utility agent</b>, and it is what
///     makes the technique look broken to anybody watching: an agent with two actions at 0.51 and 0.49
///     flaps between them several times a second while every individual number looks perfectly
///     reasonable. The three mechanisms are on the set rather than on the selector because they are
///     about the action that is <i>running</i>, and a selector is handed scores and does not know that
///     there is one.
/// </remarks>
public class UtilityOscillationTests {
    const int Frames = 60 * 60;
    const float Delta = 1f / 60f;

    [Fact]
    public void AnAgentWithTheDefaultsHoldsItsChoiceAndOneWithoutInertiaFlaps() {
        var settled = Switches(commitment: 0.15f, interval: 0.2f);
        var flapping = Switches(commitment: 0f, interval: 0f);
        var report = $"{settled} switches with the defaults, {flapping} with inertia disabled.";

        Assert.True(settled < 3, report);
        Assert.True(flapping > 50, report);
    }

    /// <summary>
    ///     Two actions a hair apart, one of them wobbling across the other once a second. Steady is
    ///     0.50; wobble runs between 0.47 and 0.51, so it is inside 2 % of steady and crosses it twice
    ///     a cycle.
    /// </summary>
    static int Switches(float commitment, float interval) {
        var clock = 0f;
        var set = new UtilitySet(
            Symbol.Intern("close-run"),
            new UtilityAction(Symbol.Intern("steady"), 0, Reading(_ => 0.50f)),
            new UtilityAction(Symbol.Intern("wobble"), 1, Reading(_ => 0.49f + (0.02f * MathF.Sin(MathF.Tau * clock))))
        ) {
            CommitmentBonus = commitment,
            DecisionInterval = interval
        };

        var state = UtilityState.Fresh;
        var context = Harness.Context();
        var cooldowns = new float[set.Count];
        var scores = new float[set.Count];
        var switches = 0;
        var last = -1;

        for (var frame = 0; frame < Frames; frame++) {
            clock = frame * Delta;

            var chosen = set.Choose(in context, ref state, cooldowns, Delta, scores);

            if (last >= 0 && chosen != last) {
                switches++;
            }

            last = chosen;
        }

        return switches;
    }

    static UtilityConsideration Reading(Func<float, float> value) =>
        new(
            Symbol.Intern("axis"),
            UtilityInputs.From((in AgentContext context) => value(0f)),
            ResponseCurve.Identity
        );
}

/// <summary>
///     P5's second exit criterion: adding a neutral consideration to an action does not change its
///     rank.
/// </summary>
/// <remarks>
///     ⚠ <b>The naive product is what everybody writes first, and it is wrong in a way that is hard to
///     see.</b> With every term in <c>[0,1]</c>, an action with more considerations is
///     <i>structurally</i> worse than one with fewer — so tuning an action by adding an axis quietly
///     demotes it, and the demotion is invisible because every individual number still looks right.
///     Every assertion here fails under a plain product.
/// </remarks>
public class UtilityCompensationTests {
    [Fact]
    public void TheCountOfConsiderationsDoesNotChangeTheScore() {
        Assert.Equal(0.6f, UtilityScoring.Combine([0.6f]), 4);
        Assert.Equal(0.6f, UtilityScoring.Combine([0.6f, 0.6f]), 4);
        Assert.Equal(0.6f, UtilityScoring.Combine([0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f]), 4);
    }

    /// <summary>Two axes at 0.6 must beat one at 0.55. Under a product they are 0.36 against 0.55.</summary>
    [Fact]
    public void AnActionIsNotDemotedForHavingMoreAxes() {
        var context = Harness.Context();
        var thorough = Action("thorough", 0, 0.6f, 0.6f);
        var terse = Action("terse", 1, 0.55f);

        Assert.True(
            thorough.Score(in context) > terse.Score(in context),
            $"two axes at 0.6 scored {thorough.Score(in context):0.000} against one at 0.55 scoring {terse.Score(in context):0.000}."
        );
    }

    [Fact]
    public void AddingANeutralConsiderationDoesNotChangeTheRank() {
        var context = Harness.Context();
        var before = Rank(Action("a", 0, 0.6f, 0.6f), Action("b", 1, 0.55f), in context);
        var after = Rank(Action("a", 0, 0.6f, 0.6f, 1f), Action("b", 1, 0.55f), in context);

        Assert.Equal(before, after);
        Assert.Equal(0, after);
    }

    /// <summary>⚠ And the half a mean would lose: one zero is still a veto, at any count.</summary>
    [Fact]
    public void OneZeroVetoesTheWholeAction() {
        var context = Harness.Context();

        Assert.Equal(0f, Action("vetoed", 0, 1f, 1f, 0f, 1f).Score(in context));
        Assert.Equal(0f, UtilityScoring.Combine([1f, 1f, 0f, 1f], weight: 5f));
    }

    /// <summary>The detail span reports every consideration, including the ones after the veto.</summary>
    [Fact]
    public void AskingForTheDetailReportsEveryAxisEvenPastAVeto() {
        var context = Harness.Context();
        var action = Action("vetoed", 0, 0.8f, 0f, 0.4f);
        var detail = new float[3];

        Assert.Equal(0f, action.Score(in context, detail));
        Assert.Equal([0.8f, 0f, 0.4f], detail);
    }

    static int Rank(UtilityAction first, UtilityAction second, ref readonly AgentContext context) =>
        first.Score(in context) >= second.Score(in context) ? 0 : 1;

    static UtilityAction Action(string name, ushort index, params float[] values) =>
        new(
            Symbol.Intern(name),
            index,
            values.Select(
                    value => new UtilityConsideration(
                        Symbol.Intern("axis"),
                        UtilityInputs.Constant(value),
                        ResponseCurve.Identity
                    )
                )
                .ToArray()
        );
}

/// <summary>An agent context with nothing in it, for a set whose inputs read no world.</summary>
static class Harness {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("value", BlackboardValueType.Float)
        .Add("flag", BlackboardValueType.Bool)
        .Build();

    public static AgentContext Context(World? world = null, Blackboard? blackboard = null) {
        var entity = new Entity(7, 1, 0);

        return new(
            world ?? new World("utility-test"),
            entity,
            blackboard ?? new Blackboard(Layout),
            null,
            GameTime.Zero,
            AgentRandom.SeedOf(entity)
        );
    }

    public static Blackboard Board() => new(Layout);

    public static BlackboardKey Key(string name) {
        Assert.True(Layout.TryGetKey(Symbol.Intern(name), out var key));

        return key;
    }
}
