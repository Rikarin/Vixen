// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Core.Curves;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

public class ResponseCurveTests {
    [Fact]
    public void LinearIsTheIdentityAtItsDefaults() {
        Assert.Equal(0f, ResponseCurve.Identity.Evaluate(0f), 4);
        Assert.Equal(0.5f, ResponseCurve.Identity.Evaluate(0.5f), 4);
        Assert.Equal(1f, ResponseCurve.Identity.Evaluate(1f), 4);
    }

    [Fact]
    public void APolynomialRisesLateAboveOneAndEarlyBelowIt() {
        var late = new ResponseCurve { Kind = ResponseCurveKind.Polynomial, Exponent = 3f };
        var early = new ResponseCurve { Kind = ResponseCurveKind.Polynomial, Exponent = 0.5f };

        Assert.True(late.Evaluate(0.5f) < 0.5f, "a cubic did not rise late.");
        Assert.True(early.Evaluate(0.5f) > 0.5f, "a square root did not rise early.");
        Assert.Equal(1f, late.Evaluate(1f), 4);
        Assert.Equal(1f, early.Evaluate(1f), 4);
    }

    [Fact]
    public void AThresholdIsHalfWayAtItsCentreAndSaturatesEitherSide() {
        var curve = ResponseCurve.Threshold(0.5f);

        Assert.Equal(0.5f, curve.Evaluate(0.5f), 3);
        Assert.True(curve.Evaluate(0.1f) < 0.05f, "the threshold had not fallen by a tenth.");
        Assert.True(curve.Evaluate(0.9f) > 0.95f, "the threshold had not risen by nine tenths.");
    }

    [Fact]
    public void ABellPeaksAtItsCentre() {
        var curve = ResponseCurve.Bell(0.4f, 0.1f);

        Assert.Equal(1f, curve.Evaluate(0.4f), 3);
        Assert.True(curve.Evaluate(0.7f) < 0.05f, "the bell was still wide three tenths out.");
        Assert.True(curve.Evaluate(0.1f) < 0.05f, "the bell was still wide three tenths out the other way.");
    }

    [Fact]
    public void ALogitIsMonotonicAndDoesNotRunOffToInfinityAtTheEnds() {
        var curve = new ResponseCurve { Kind = ResponseCurveKind.Logit, Centre = 0f, Exponent = 5f };

        Assert.InRange(curve.Evaluate(0f), 0f, 1f);
        Assert.InRange(curve.Evaluate(1f), 0f, 1f);
        Assert.True(curve.Evaluate(0.3f) < curve.Evaluate(0.7f), "the logit was not increasing.");
    }

    [Fact]
    public void ASampledCurveFollowsItsKeys() {
        var curve = new ResponseCurve {
            Kind = ResponseCurveKind.Sampled,
            Keys = [
                new(0f, 0f, 0f, 0f, TangentMode.Linear),
                new(0.5f, 0f, 0f, 0f, TangentMode.Linear),
                new(1f, 1f, 0f, 0f, TangentMode.Linear)
            ]
        };

        // "Ignore it entirely, then suddenly care" — the shape no formula gives, which is why
        // Sampled is in the list rather than a grudging seventh option.
        Assert.Equal(0f, curve.Evaluate(0.25f), 3);
        Assert.Equal(0.5f, curve.Evaluate(0.75f), 2);
        Assert.Equal(1f, curve.Evaluate(1f), 3);
    }

    /// <summary>
    ///     ⚠ Everything is clamped to <c>[0,1]</c>, and a NaN reads as zero. Both are reachable from an
    ///     editor, and either would poison a geometric mean rather than merely look wrong.
    /// </summary>
    [Fact]
    public void NothingEscapesTheUnitIntervalAndNaNIsAVeto() {
        var loud = new ResponseCurve { Slope = 40f };
        var negative = new ResponseCurve { Slope = -3f };
        var broken = new DelegateResponseCurve(_ => float.NaN);

        Assert.Equal(1f, loud.Evaluate(0.9f), 4);
        Assert.Equal(0f, negative.Evaluate(0.9f), 4);
        Assert.Equal(0f, broken.Evaluate(0.5f), 4);
    }
}

public class UtilityInputTests {
    [Fact]
    public void ABlackboardInputNormalisesBetweenItsBounds() {
        var board = Harness.Board();
        var input = new BlackboardUtilityInput(Harness.Key("value"), 20f, 120f);
        var context = Harness.Context(blackboard: board);

        // ⚠ Unset reads as zero rather than as the minimum: "nobody has written this" and "this is at
        // its lowest" are different facts, and with the zero rule the safe direction is a veto.
        Assert.Equal(0f, input.Read(in context), 4);

        board.SetFloat(Harness.Key("value"), 70f);
        Assert.Equal(0.5f, input.Read(in context), 4);

        board.SetFloat(Harness.Key("value"), 400f);
        Assert.Equal(1f, input.Read(in context), 4);
    }

    [Fact]
    public void ABoolKeyReadsAsZeroOrOne() {
        var board = Harness.Board();
        var input = new BlackboardUtilityInput(Harness.Key("flag"));
        var context = Harness.Context(blackboard: board);

        board.SetBool(Harness.Key("flag"), true);
        Assert.Equal(1f, input.Read(in context), 4);

        board.SetBool(Harness.Key("flag"), false);
        Assert.Equal(0f, input.Read(in context), 4);
    }

    /// <summary>
    ///     ⚠ With no position lookup the distance input reads "far", which is the answer that does not
    ///     make an agent act on a distance nobody could measure.
    /// </summary>
    [Fact]
    public void ADistanceInputWithoutALookupReadsAsFar() {
        var board = Harness.Board();
        var context = Harness.Context(blackboard: board);
        var input = new DistanceUtilityInput(Harness.Key("value"), 10f);

        Assert.Equal(1f, input.Read(in context), 4);
    }
}

public class UtilitySelectorTests {
    static readonly float[] Scores = [0.2f, 0.9f, 0.5f, 0f];

    [Fact]
    public void HighestTakesTheBest() {
        Assert.Equal(1, Pick(UtilitySelectors.Highest));
    }

    [Fact]
    public void NothingAboveZeroIsNoChoiceAtAll() {
        Assert.Equal(-1, Pick(UtilitySelectors.Highest, [0f, 0f, 0f]));
        Assert.Equal(-1, Pick(UtilitySelectors.WeightedRandom, [0f, 0f, 0f]));
        Assert.Equal(-1, Pick(UtilitySelectors.Bucketed, [0f, 0f, 0f]));
    }

    [Fact]
    public void AWeightedRandomNeverPicksAVetoedAction() {
        for (var id = 1; id <= 200; id++) {
            var chosen = Pick(UtilitySelectors.WeightedRandom, Scores, id);

            Assert.NotEqual(3, chosen);
        }
    }

    [Fact]
    public void TopWeightedRandomStaysInsideTheBestFew() {
        for (var id = 1; id <= 200; id++) {
            Assert.Equal(1, Pick(UtilitySelectors.TopWeightedRandom(1), Scores, id));
            Assert.Contains(Pick(UtilitySelectors.TopWeightedRandom(2), Scores, id), new[] { 1, 2 });
        }
    }

    /// <summary>
    ///     ⚠ The one that stops a guard being shot at from scoring "drink coffee". The ambient action
    ///     scores far better and still loses, because its whole bucket does.
    /// </summary>
    [Fact]
    public void BucketedTakesTheHighestGroupWithAnythingInItAtAll() {
        var set = new UtilitySet(
            Symbol.Intern("guard"),
            new UtilityAction(Symbol.Intern("coffee"), 0) { Bucket = 0 },
            new UtilityAction(Symbol.Intern("shoot"), 1) { Bucket = 5 }
        );

        var context = Harness.Context();

        Assert.Equal(1, UtilitySelectors.Bucketed.Pick(in context, set, [0.95f, 0.05f]));

        // And when the emergency bucket has nothing at all, the ambient one is chosen after all.
        Assert.Equal(0, UtilitySelectors.Bucketed.Pick(in context, set, [0.95f, 0f]));
    }

    /// <summary>Two agents scoring identically must not agree, or a crowd moves as one.</summary>
    [Fact]
    public void AWeightedRandomIsDrawnFromTheAgentsOwnStream() {
        var picks = Enumerable.Range(1, 64)
            .Select(id => Pick(UtilitySelectors.WeightedRandom, [0.5f, 0.5f, 0.5f, 0.5f], id))
            .Distinct()
            .Count();

        Assert.True(picks > 1, "sixty-four agents with identical scores all chose the same action.");
    }

    static int Pick(IUtilitySelector selector, float[]? scores = null, int id = 7) {
        var values = scores ?? Scores;
        var set = new UtilitySet(
            Symbol.Intern("probe"),
            [.. Enumerable.Range(0, values.Length).Select(index => new UtilityAction(Symbol.Intern($"a{index}"), (ushort)index))]
        );

        var entity = new Entity(id, 1, 0);
        var context = new AgentContext(
            new World("selector-test"),
            entity,
            Harness.Board(),
            null,
            GameTime.Zero,
            AgentRandom.SeedOf(entity)
        );

        return selector.Pick(in context, set, values);
    }
}

public class UtilityInertiaTests {
    [Fact]
    public void ACooldownKeepsAnActionOutUntilItHasElapsed() {
        var set = new UtilitySet(
            Symbol.Intern("cooling"),
            Constant("once", 0, 0.9f, cooldown: 1f),
            Constant("other", 1, 0.5f)
        ) { CommitmentBonus = 0f, DecisionInterval = 0f };

        var state = UtilityState.Fresh;
        var cooldowns = new float[set.Count];
        var scores = new float[set.Count];
        var context = Harness.Context();

        Assert.Equal(0, set.Choose(in context, ref state, cooldowns, 0.1f, scores));

        // It finishes, so its cooldown starts and the other one has the floor.
        set.Finished(ref state, cooldowns);
        Assert.Equal(1, set.Choose(in context, ref state, cooldowns, 0.1f, scores));

        Assert.Equal(1, set.Choose(in context, ref state, cooldowns, 0.5f, scores));
        Assert.Equal(0, set.Choose(in context, ref state, cooldowns, 0.6f, scores));
    }

    /// <summary>
    ///     ⚠ The bonus is applied after the veto, so an action whose condition has genuinely gone false
    ///     cannot hold on to itself. Commitment is for a score that wobbled, not for one that stopped
    ///     being true.
    /// </summary>
    [Fact]
    public void CommitmentDoesNotSurviveAVeto() {
        var live = 1f;
        var set = new UtilitySet(
            Symbol.Intern("gated"),
            new UtilityAction(
                Symbol.Intern("gated"),
                0,
                new UtilityConsideration(
                    Symbol.Intern("possible"),
                    UtilityInputs.From((in AgentContext context) => live),
                    ResponseCurve.Identity
                )
            ),
            Constant("other", 1, 0.1f)
        ) { CommitmentBonus = 5f, DecisionInterval = 0f };

        var state = UtilityState.Fresh;
        var cooldowns = new float[set.Count];
        var scores = new float[set.Count];
        var context = Harness.Context();

        Assert.Equal(0, set.Choose(in context, ref state, cooldowns, 0.1f, scores));

        live = 0f;
        Assert.Equal(1, set.Choose(in context, ref state, cooldowns, 0.1f, scores));
    }

    /// <summary>Between decisions it does not score at all, which is the cheapest of the three.</summary>
    [Fact]
    public void BetweenDecisionsNothingIsRead() {
        var reads = 0;
        var set = new UtilitySet(
            Symbol.Intern("counted"),
            new UtilityAction(
                Symbol.Intern("only"),
                0,
                new UtilityConsideration(
                    Symbol.Intern("axis"),
                    UtilityInputs.From(
                        (in AgentContext context) => {
                            reads++;

                            return 1f;
                        }
                    ),
                    ResponseCurve.Identity
                )
            )
        ) { DecisionInterval = 0.2f };

        var state = UtilityState.Fresh;
        var cooldowns = new float[set.Count];
        var scores = new float[set.Count];
        var context = Harness.Context();

        for (var frame = 0; frame < 60; frame++) {
            set.Choose(in context, ref state, cooldowns, 1f / 60f, scores);
        }

        // One second at sixty frames and a fifth-of-a-second interval: five decisions, not sixty.
        Assert.Equal(5, reads);
    }

    static UtilityAction Constant(string name, ushort index, float score, float cooldown = 0f) =>
        new(
            Symbol.Intern(name),
            index,
            new UtilityConsideration(Symbol.Intern("axis"), UtilityInputs.Constant(score), ResponseCurve.Identity)
        ) {
            Cooldown = cooldown
        };
}

public class UtilityAgentTests {
    /// <summary>A utility agent through the whole system: it runs what the set chose, and swaps.</summary>
    [Fact]
    public void TheSystemRunsWhatTheSetChoseAndSwapsWhenItChangesItsMind() {
        var registry = new AgentActionRegistry();
        var walking = new Counting();
        var fleeing = new Counting();

        registry.Register("walk", walking, Counting.Size);
        registry.Register("flee", fleeing, Counting.Size);

        var danger = 0f;
        var system = new AiSystem(registry, LayoutOf());
        var set = new UtilitySet(
            Symbol.Intern("villager"),
            Scored("walk", 0, _ => 0.5f),
            Scored("flee", 1, _ => danger)
        );

        var index = system.Sets.Add(set);
        using var world = new World("utility-agent");
        var entity = world.Create(AiAgent.Scoring(index));

        for (var frame = 0; frame < 30; frame++) {
            system.Step(world, Frame(frame));
        }

        Assert.Equal(0, world.Get<AiAgent>(entity).Action);
        Assert.True(walking.Ticks > 0, "the chosen action never ran.");
        Assert.Equal(0, fleeing.Ticks);

        danger = 0.95f;

        for (var frame = 30; frame < 60; frame++) {
            system.Step(world, Frame(frame));
        }

        Assert.Equal(1, world.Get<AiAgent>(entity).Action);
        Assert.Equal(1, fleeing.Starts);

        // ⚠ The block is zeroed on the swap and only on the swap. An action that inherited the last
        // one's bytes would start half-way through whatever that one was doing.
        Assert.True(fleeing.FirstTickSawZero, "the new action was handed the old one's state.");
    }

    [Fact]
    public void ATaskCanRunAWholeSetInsideATree() {
        var registry = new AgentActionRegistry();
        var quiet = new Counting();
        var loud = new Counting();

        registry.Register("quiet", quiet, Counting.Size);
        registry.Register("loud", loud, Counting.Size);

        var volume = 0f;
        var set = new UtilitySet(
            Symbol.Intern("mood"),
            Scored("quiet", 0, _ => 0.5f),
            Scored("loud", 1, _ => volume)
        ) { DecisionInterval = 0f };

        var task = new RunUtilitySetTask(set, registry);
        var state = new byte[task.RequiredState];
        var context = Harness.Context();

        task.Start(in context, state);

        Assert.Equal(ActionStatus.Running, task.Tick(in context, state, 0.1f));
        Assert.True(quiet.Ticks > 0);

        volume = 0.99f;
        Assert.Equal(ActionStatus.Running, task.Tick(in context, state, 0.1f));
        Assert.Equal(1, loud.Starts);
        Assert.Equal(1, quiet.Aborts);

        // ⚠ A set is a standing judgement rather than a procedure with an end, so the task never
        // finishes on its own — it is meant to be aborted by a decorator above it.
        for (var tick = 0; tick < 20; tick++) {
            Assert.Equal(ActionStatus.Running, task.Tick(in context, state, 0.1f));
        }

        task.Abort(in context, state);
        Assert.Equal(1, loud.Aborts);
    }

    [Fact]
    public void ATaskWhoseWholeSetIsVetoedFailsRatherThanStanding() {
        var registry = new AgentActionRegistry();

        registry.Register("never", new Counting(), Counting.Size);

        var set = new UtilitySet(Symbol.Intern("nothing"), Scored("never", 0, _ => 0f)) { DecisionInterval = 0f };
        var task = new RunUtilitySetTask(set, registry);
        var state = new byte[task.RequiredState];
        var context = Harness.Context();

        task.Start(in context, state);
        Assert.Equal(ActionStatus.Failed, task.Tick(in context, state, 0.1f));
    }

    static UtilityAction Scored(string name, ushort index, Func<int, float> score) =>
        new(
            Symbol.Intern(name),
            index,
            new UtilityConsideration(
                Symbol.Intern("axis"),
                UtilityInputs.From((in AgentContext context) => score(0)),
                ResponseCurve.Identity
            )
        );

    static BlackboardLayout LayoutOf() => new BlackboardLayoutBuilder().Add("value", BlackboardValueType.Float).Build();

    static GameTime Frame(int index) =>
        new(TimeSpan.FromSeconds(index * 0.1), TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.1), index, 1f);

    /// <summary>An action that counts what happened to it — in its span, and on itself for the totals.</summary>
    sealed class Counting : IAgentAction {
        public int Starts;
        public int Aborts;
        public int Ticks;
        public bool FirstTickSawZero = true;

        public static int Size => Marshal.SizeOf<int>();

        public void Start(in AgentContext context, Span<byte> state) => Starts++;

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
            ref var counted = ref MemoryMarshal.AsRef<int>(state);

            if (Ticks == 0) {
                FirstTickSawZero = counted == 0;
            }

            counted++;
            Ticks++;

            return ActionStatus.Running;
        }

        public void Abort(in AgentContext context, Span<byte> state) => Aborts++;
    }
}

/// <summary>A set as a file, compiled against the same resolver a tree is.</summary>
public class UtilityContentTests {
    [Fact]
    public void ASetCompilesItsActionsThroughTheSameSchemaATreeUses() {
        var content = Villager();
        var resolver = new BehaviorTreeResolver();

        Assert.True(UtilitySetContentCompiler.TryCompile(content, resolver, out var diagnostics, out var set));
        Assert.Empty(diagnostics);
        Assert.NotNull(set);
        Assert.Equal(2, set!.Count);
        Assert.Equal(0.2f, set.DecisionInterval, 4);

        // ⚠ The two actions are two `Wait`s at different durations, so they are two registered
        // actions — the same rule a tree's tasks follow, in the same registry.
        Assert.NotEqual(set[0].Action, set[1].Action);
    }

    /// <summary>
    ///     ⚠ A typo in a key name is the failure a designer cannot see: the consideration scores zero
    ///     and a zero is a veto, so the action silently never runs. It is a diagnostic here instead.
    /// </summary>
    [Fact]
    public void AKeyThatIsNotOnTheBlackboardIsADiagnosticAndAVeto() {
        var content = Villager();

        content.Actions[0].Considerations[0].Key = "hungr";

        var resolver = new BehaviorTreeResolver();

        Assert.False(UtilitySetContentCompiler.TryCompile(content, resolver, out var diagnostics, out var set));
        Assert.Contains(diagnostics, problem => problem.Message.Contains("is not a key", StringComparison.Ordinal));

        var board = content.BuildLayout();
        var context = Harness.Context(blackboard: new Blackboard(board));

        Assert.Equal(0f, set![0].Score(in context));
    }

    /// <summary>An input a game registers in code, named by a file.</summary>
    [Fact]
    public void ARegisteredInputIsFoundByNameAndAMissingOneVetoes() {
        var content = Villager();

        content.Actions[0].Considerations[0].Input = UtilityInputKind.Registered;
        content.Actions[0].Considerations[0].Source = "mood";

        var resolver = new BehaviorTreeResolver();

        Assert.False(UtilitySetContentCompiler.TryCompile(content, resolver, out var missing, out var vetoed));
        Assert.Contains(missing, problem => problem.Message.Contains("No utility input called", StringComparison.Ordinal));
        Assert.Equal(0f, vetoed![0].Score(Harness.Context()));

        resolver.AddInput("mood", UtilityInputs.Constant(0.75f));

        Assert.True(UtilitySetContentCompiler.TryCompile(content, resolver, out _, out var found));
        Assert.Equal(0.75f, found![0].Score(Harness.Context()), 3);
    }

    [Fact]
    public void EverySelectorKindResolves() {
        foreach (var kind in Enum.GetValues<UtilitySelectorKind>()) {
            var content = Villager();

            content.Selector = kind;

            Assert.True(UtilitySetContentCompiler.TryCompile(content, new BehaviorTreeResolver(), out _, out var set));
            Assert.NotNull(set!.Selector);
        }
    }

    [Fact]
    public void ASampledCurveSurvivesTheFile() {
        var content = Villager();
        var axis = content.Actions[0].Considerations[0];

        axis.Curve = ResponseCurveKind.Sampled;
        axis.Keys = [
            new() { Time = 0f, Value = 0f },
            new() { Time = 1f, Value = 1f }
        ];

        Assert.True(UtilitySetContentCompiler.TryCompile(content, new BehaviorTreeResolver(), out _, out var set));

        var board = new Blackboard(content.BuildLayout());

        board.SetFloat(new(0), 0.5f);

        Assert.Equal(0.5f, set![0].Score(Harness.Context(blackboard: board)), 2);
    }

    static UtilitySetContent Villager() {
        var eat = new UtilityActionContent { Name = "Eat", Task = "Wait", Fields = { ["Seconds"] = "2" } };
        var rest = new UtilityActionContent { Name = "Rest", Task = "Wait", Fields = { ["Seconds"] = "5" } };

        eat.Considerations.Add(new() { Name = "hungry", Key = "hunger" });
        rest.Considerations.Add(new() { Name = "tired", Key = "hunger", Slope = -1f, Shift = 1f });

        return new() {
            Name = "villager",
            Keys = { new() { Name = "hunger", Type = BlackboardValueType.Float } },
            Actions = { eat, rest }
        };
    }
}
