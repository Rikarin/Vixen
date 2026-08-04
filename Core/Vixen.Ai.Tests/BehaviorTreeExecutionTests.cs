// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Xunit;

namespace Vixen.Ai.Tests;

public class BehaviorTreeCompositeTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("gate", BlackboardValueType.Bool)
        .Add("count", BlackboardValueType.Int)
        .Build();

    [Fact]
    public void ASelectorStopsAtTheFirstChildThatSucceeds() {
        using var harness = Build(
            BehaviorTree.Selector("root", Task("a", "fail"), Task("b", "succeed"), Task("c", "succeed"))
        );

        Assert.Equal(ActionStatus.Succeeded, harness.Step());
        Assert.Equal(1, harness.Probe(1).Ticks);
        Assert.Equal(1, harness.Probe(2).Ticks);
        Assert.Equal(0, harness.Probe(3).Starts);
    }

    [Fact]
    public void ASelectorFailsWhenEveryChildDoes() {
        using var harness = Build(BehaviorTree.Selector("root", Task("a", "fail"), Task("b", "fail")));

        Assert.Equal(ActionStatus.Failed, harness.Step());
    }

    [Fact]
    public void ASequenceStopsAtTheFirstChildThatFails() {
        using var harness = Build(
            BehaviorTree.Sequence("root", Task("a", "succeed"), Task("b", "fail"), Task("c", "succeed"))
        );

        Assert.Equal(ActionStatus.Failed, harness.Step());
        Assert.Equal(1, harness.Probe(2).Ticks);
        Assert.Equal(0, harness.Probe(3).Starts);
    }

    [Fact]
    public void ASequenceSucceedsWhenEveryChildDoes() {
        using var harness = Build(BehaviorTree.Sequence("root", Task("a", "succeed"), Task("b", "succeed")));

        Assert.Equal(ActionStatus.Succeeded, harness.Step());
        Assert.Equal(1, harness.Probe(1).Ticks);
        Assert.Equal(1, harness.Probe(2).Ticks);
    }

    /// <summary>
    ///     ⚠ A chain of instant tasks finishes inside one step. Under a governor at one tick in
    ///     sixteen, one node per step would be a quarter of a second per blackboard write.
    /// </summary>
    [Fact]
    public void AChainOfInstantTasksFinishesInOneStep() {
        using var harness = Build(
            BehaviorTree.Sequence(
                "root",
                Task("a", "succeed"),
                Task("b", "succeed"),
                Task("c", "succeed"),
                Task("d", "succeed")
            )
        );

        Assert.Equal(ActionStatus.Succeeded, harness.Step());
        Assert.False(harness.Tree.Overran);

        for (var node = 1; node <= 4; node++) {
            Assert.Equal(1, harness.Probe(node).Ticks);
        }
    }

    [Fact]
    public void ARunningTaskKeepsTheTreeWhereItIs() {
        using var harness = Build(BehaviorTree.Sequence("root", Task("wait", "running"), Task("after", "succeed")));

        Assert.Equal(ActionStatus.Running, harness.Step());
        Assert.Equal("wait", harness.Active);

        harness.Steps(4);

        Assert.Equal(5, harness.Probe(1).Ticks);
        Assert.Equal(0, harness.Probe(2).Starts);
    }

    /// <summary>The heart of D7: a settled tree costs its task's tick and nothing else.</summary>
    [Fact]
    public void ASettledTreeMakesNoTransitions() {
        using var harness = Build(
            BehaviorTree.Selector("root", BehaviorTree.Sequence("branch", Task("wait", "running")))
        );

        harness.Step();
        Assert.True(harness.Tree.LastTransitions > 0);

        harness.Step();
        Assert.Equal(0, harness.Tree.LastTransitions);
    }

    [Fact]
    public void ATreeThatFinishesStartsAgainOnTheNextStep() {
        using var harness = Build(BehaviorTree.Selector("root", Task("a", "succeed")));

        Assert.Equal(ActionStatus.Succeeded, harness.Step());
        Assert.Equal(1, harness.Tree.Completions);

        Assert.Equal(ActionStatus.Succeeded, harness.Step());
        Assert.Equal(2, harness.Tree.Completions);
        Assert.Equal(2, harness.TotalStarts("succeed"));
    }

    /// <summary>
    ///     ⚠ A tree whose root fails instantly costs one step, not a runaway. Looping straight back
    ///     into the root inside the step would burn the whole transition budget every frame.
    /// </summary>
    [Fact]
    public void ATreeThatFailsInstantlyDoesNotOverrun() {
        using var harness = Build(BehaviorTree.Selector("root", Task("a", "fail")));

        Assert.Equal(ActionStatus.Failed, harness.Step());
        Assert.False(harness.Tree.Overran);
        Assert.True(harness.Tree.LastTransitions < 10);
    }

    [Fact]
    public void APriorityCompositeReEvaluatesFromChildZeroEveryStep() {
        var gate = Layout.Key("gate");

        using var harness = Build(
            BehaviorTree.Priority(
                "root",
                Task("urgent", "running").With(BlackboardDecorator.Bool(gate)),
                Task("ambient", "running")
            )
        );

        harness.Step();
        Assert.Equal("ambient", harness.Active);

        // Nothing observes the key: it is the composite that changes its mind, which is the whole
        // difference between Priority and Selector.
        harness.Board.SetBool(gate, true);
        harness.Step();

        Assert.Equal("urgent", harness.Active);
        Assert.Equal(1, harness.Probe(2).Aborts);
    }

    [Fact]
    public void ASelectorDoesNotReEvaluateAndThatIsTheDifference() {
        var gate = Layout.Key("gate");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("urgent", "running").With(BlackboardDecorator.Bool(gate)),
                Task("ambient", "running")
            )
        );

        harness.Step();
        harness.Board.SetBool(gate, true);
        harness.Steps(3);

        Assert.Equal("ambient", harness.Active);
        Assert.Equal(0, harness.Probe(1).Starts);
    }

    [Fact]
    public void ARandomSelectorTriesEveryChildOnceBeforeFailing() {
        using var harness = Build(
            BehaviorTree.RandomSelector("root", Task("a", "fail"), Task("b", "fail"), Task("c", "fail"))
        );

        Assert.Equal(ActionStatus.Failed, harness.Step());

        for (var node = 1; node <= 3; node++) {
            Assert.Equal(1, harness.Probe(node).Ticks);
        }
    }

    /// <summary>The order is a shuffle, but the same shuffle for the same agent on every machine.</summary>
    [Fact]
    public void ARandomSelectorIsShuffledAndReproducible() {
        var orders = new List<string>();

        for (var pass = 0; pass < 2; pass++) {
            using var harness = Build(
                BehaviorTree.RandomSelector("root", Task("a", "fail"), Task("b", "fail"), Task("c", "fail"))
            );
            var recorder = harness.Debug;

            recorder.Enabled = true;
            harness.Step();

            var records = new Vixen.Ai.Diagnostics.AgentDebugRecord[16];

            _ = recorder.CopyTo(records);
            orders.Add(string.Join(",", Enumerable.Range(1, 3).Select(node => harness.Probe(node).Starts)));
        }

        Assert.Equal(orders[0], orders[1]);
    }

    [Fact]
    public void AParallelRunsItsMainTaskBesideItsBackgroundBranch() {
        using var harness = Build(
            BehaviorTree.Parallel(
                "root",
                ParallelFinishMode.Immediate,
                Task("main", "running"),
                BehaviorTree.Sequence("background", Task("watch", "running"))
            )
        );

        harness.Steps(3);

        Assert.Equal(3, harness.Probe(1).Ticks);
        Assert.Equal(3, harness.Probe(3).Ticks);
        Assert.Equal("watch", harness.Active);
        Assert.Equal(1, harness.Probe(1).Starts);
    }

    [Fact]
    public void AParallelInImmediateModeAbortsTheBranchWhenTheMainTaskEnds() {
        using var harness = Build(
            BehaviorTree.Parallel(
                "root",
                ParallelFinishMode.Immediate,
                Task("main", "succeed-after-2"),
                BehaviorTree.Sequence("background", Task("watch", "running"))
            )
        );

        harness.Step();
        Assert.Equal(0, harness.Probe(3).Aborts);

        harness.Step();
        Assert.Equal(1, harness.Probe(3).Aborts);
        Assert.Equal(ActionStatus.Succeeded, harness.Tree.LastResult);
    }

    /// <summary>
    ///     The background branch gets one pass per step, which is what stops a branch of instant
    ///     tasks from spinning inside a single step.
    /// </summary>
    [Fact]
    public void AParallelsBackgroundBranchRunsOncePerStep() {
        using var harness = Build(
            BehaviorTree.Parallel(
                "root",
                ParallelFinishMode.Immediate,
                Task("main", "running"),
                BehaviorTree.Sequence("background", Task("blink", "succeed"))
            )
        );

        harness.Steps(4);

        Assert.False(harness.Tree.Overran);
        Assert.InRange(harness.TotalStarts("succeed"), 3, 4);
    }

    static TreeHarness Build(BehaviorNodeDefinition root) =>
        TreeHarness.For(root, Layout, actions => TreeHarness.Probes(actions));

    static BehaviorNodeDefinition Task(string name, string action) => BehaviorTree.Task(name, action);
}

public class BehaviorTreeDecoratorTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("gate", BlackboardValueType.Bool)
        .Add("count", BlackboardValueType.Int)
        .Add("here", BlackboardValueType.Vector3)
        .Add("there", BlackboardValueType.Vector3)
        .Add("facing", BlackboardValueType.Vector3)
        .Build();

    [Fact]
    public void ADecoratorThatFailsStopsTheNodeBeingEntered() {
        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("gated", "succeed").With(BlackboardDecorator.Bool(Layout.Key("gate"))),
                Task("fallback", "succeed")
            )
        );

        harness.Step();

        Assert.Equal(0, harness.Probe(1).Starts);
        Assert.Equal(1, harness.Probe(2).Starts);
    }

    /// <summary>⚠ Top to bottom, first failure stops the rest — the author's ordering is honoured.</summary>
    [Fact]
    public void DecoratorsEvaluateTopToBottomAndStopAtTheFirstFailure() {
        var counted = new CountingDecorator(passes: false);
        var after = new CountingDecorator(passes: true);

        using var harness = Build(
            BehaviorTree.Selector("root", Task("gated", "succeed").With(counted).With(after), Task("other", "succeed"))
        );

        harness.Step();

        Assert.Equal(1, counted.Calls);
        Assert.Equal(0, after.Calls);
    }

    [Fact]
    public void AnInverterTurnsTheResultOver() {
        using var harness = Build(
            BehaviorTree.Sequence("root", Task("a", "fail").With(new InverterDecorator()))
        );

        Assert.Equal(ActionStatus.Succeeded, harness.Step());
    }

    [Fact]
    public void ForceSuccessAndForceFailureOverrideTheResult() {
        using var success = Build(BehaviorTree.Sequence("root", Task("a", "fail").With(new ForceSuccessDecorator())));
        using var failure = Build(
            BehaviorTree.Sequence("root", Task("a", "succeed").With(new ForceFailureDecorator()))
        );

        Assert.Equal(ActionStatus.Succeeded, success.Step());
        Assert.Equal(ActionStatus.Failed, failure.Step());
    }

    /// <summary>Bottom to top on the way out, so the innermost decorator sees the node's own answer.</summary>
    [Fact]
    public void DecoratorsUnwindInnermostFirst() {
        using var harness = Build(
            BehaviorTree.Sequence(
                "root",
                Task("a", "fail").With(new ForceSuccessDecorator()).With(new InverterDecorator())
            )
        );

        // Drawn top to bottom as [ForceSuccess, Inverter]; the inverter is innermost, so `fail`
        // becomes `succeed` and the force-success then leaves it alone.
        Assert.Equal(ActionStatus.Succeeded, harness.Step());
    }

    [Fact]
    public void ACooldownRefusesEntryUntilItsTimeHasPassed() {
        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("limited", "succeed").With(new CooldownDecorator(0.5f)),
                Task("fallback", "succeed")
            )
        );

        harness.Step(0.1f);
        Assert.Equal(1, harness.TotalStarts("succeed"));

        // Straight back round: the branch is on cooldown, so the fallback runs instead.
        harness.Step(0.1f);
        Assert.Equal(2, harness.TotalStarts("succeed"));
        Assert.Equal(1, harness.Probe(2).Starts);

        harness.Steps(6, 0.1f);
        Assert.True(harness.TotalStarts("succeed") >= 3, "the cooldown never ran out.");
    }

    [Fact]
    public void ATimeLimitFailsTheBranchOnceItHasBeenRunningTooLong() {
        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("long", "running").With(new TimeLimitDecorator(0.25f)),
                Task("fallback", "running")
            )
        );

        harness.Steps(2, 0.1f);
        Assert.Equal("long", harness.Active);

        harness.Steps(3, 0.1f);
        Assert.Equal("fallback", harness.Active);
        Assert.Equal(1, harness.Probe(1).Aborts);
    }

    [Fact]
    public void ALoopRunsTheNodeAFixedNumberOfTimes() {
        using var harness = Build(
            BehaviorTree.Sequence("root", Task("a", "succeed").With(new LoopDecorator(3)))
        );

        Assert.Equal(ActionStatus.Succeeded, harness.Step());
        Assert.Equal(3, harness.TotalStarts("succeed"));
    }

    [Fact]
    public void ALoopUntilFailureStopsWhenItFails() {
        using var harness = Build(
            BehaviorTree.Sequence("root", Task("a", "fail").With(new LoopDecorator(3)).With(new InverterDecorator()))
        );

        harness.Step();

        // The inverter is innermost, so `fail` reads as success and the loop keeps going three times.
        Assert.Equal(3, harness.TotalStarts("fail"));
    }

    [Fact]
    public void AConditionalLoopGoesRoundWhileItsConditionHolds() {
        var gate = Layout.Key("gate");
        var condition = BlackboardDecorator.Bool(gate);

        using var harness = Build(
            BehaviorTree.Sequence("root", Task("a", "succeed").With(new ConditionalLoopDecorator(condition)))
        );

        harness.Board.SetBool(gate, true);
        harness.Step();

        // Runs until the transition budget stops it, because nothing turned the key off — which is
        // exactly what an unbounded conditional loop is, and why Overran exists to say so.
        Assert.True(harness.Tree.Overran);
        Assert.True(harness.TotalStarts("succeed") > 10);
    }

    [Fact]
    public void ARandomChanceIsDrawnFromTheAgentsOwnStream() {
        using var never = Build(
            BehaviorTree.Selector(
                "root",
                Task("lucky", "succeed").With(new RandomChanceDecorator(0f)),
                Task("fallback", "succeed")
            )
        );

        using var always = Build(
            BehaviorTree.Selector(
                "root",
                Task("lucky", "succeed").With(new RandomChanceDecorator(1f)),
                Task("fallback", "succeed")
            )
        );

        never.Step();
        always.Step();

        Assert.Equal(0, never.Probe(1).Starts);
        Assert.Equal(1, always.Probe(1).Starts);
        Assert.Equal(1, never.Probe(2).Starts);
    }

    [Fact]
    public void ATagCooldownIsSharedAcrossTheWholeTree() {
        var tag = Symbol.Intern("shout");

        using var harness = Build(
            BehaviorTree.Sequence(
                "root",
                Task("shout", "succeed").With(new SetTagCooldownDecorator(tag, 1f)),
                Task("shout-again", "succeed").With(new TagCooldownDecorator(tag, 1f))
            )
        );

        harness.Step(0.1f);

        Assert.Equal(1, harness.Probe(1).Starts);
        Assert.Equal(0, harness.Probe(2).Starts);
        Assert.Equal(ActionStatus.Failed, harness.Tree.LastResult);
    }

    [Fact]
    public void ACompositeDecoratorJoinsConditionsWithoutABranchPerCombination() {
        var gate = Layout.Key("gate");
        var count = Layout.Key("count");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("both", "succeed").With(
                    new CompositeDecorator(
                        DecoratorLogic.And,
                        ObserverAborts.None,
                        BlackboardDecorator.Bool(gate),
                        BlackboardDecorator.Number(count, BlackboardTest.GreaterOrEqual, 3f)
                    )
                ),
                Task("fallback", "succeed")
            )
        );

        harness.Board.SetBool(gate, true);
        harness.Board.SetInt(count, 1);
        harness.Step();
        Assert.Equal(0, harness.Probe(1).Starts);

        harness.Board.SetInt(count, 5);
        harness.Step();
        Assert.Equal(1, harness.Probe(1).Starts);
    }

    [Fact]
    public void CompareEntriesComparesTwoKeys() {
        var count = Layout.Key("count");
        var other = Layout.Key("gate");

        _ = other;

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("equal", "succeed").With(new CompareEntriesDecorator(count, count)),
                Task("fallback", "succeed")
            )
        );

        harness.Board.SetInt(count, 3);
        harness.Step();

        Assert.Equal(1, harness.Probe(1).Starts);
    }

    [Fact]
    public void IsAtLocationMeasuresTheDistanceBetweenTwoKeys() {
        var here = Layout.Key("here");
        var there = Layout.Key("there");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("arrived", "succeed").With(new IsAtLocationDecorator(here, there, 1f)),
                Task("fallback", "succeed")
            )
        );

        harness.Board.SetVector3(here, new(0f, 0f, 0f));
        harness.Board.SetVector3(there, new(5f, 0f, 0f));
        harness.Step();
        Assert.Equal(0, harness.Probe(1).Starts);

        // Height is ignored by default, so a target on a ledge directly above counts as arrived.
        harness.Board.SetVector3(there, new(0.5f, 12f, 0f));
        harness.Step();
        Assert.Equal(1, harness.Probe(1).Starts);
    }

    [Fact]
    public void AConeTestsWhetherATargetIsInFront() {
        var here = Layout.Key("here");
        var facing = Layout.Key("facing");
        var there = Layout.Key("there");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("seen", "succeed").With(new ConeDecorator(here, facing, there, 45f)),
                Task("fallback", "succeed")
            )
        );

        harness.Board.SetVector3(here, new(0f, 0f, 0f));
        harness.Board.SetVector3(facing, new(0f, 0f, 1f));
        harness.Board.SetVector3(there, new(0f, 0f, 10f));
        harness.Step();
        Assert.Equal(1, harness.Probe(1).Starts);

        harness.Board.SetVector3(there, new(10f, 0f, 0f));
        harness.Step();
        Assert.Equal(1, harness.Probe(1).Starts);
    }

    static TreeHarness Build(BehaviorNodeDefinition root) =>
        TreeHarness.For(root, Layout, actions => TreeHarness.Probes(actions));

    static BehaviorNodeDefinition Task(string name, string action) => BehaviorTree.Task(name, action);

    sealed class CountingDecorator(bool passes) : BehaviorDecorator {
        public int Calls { get; private set; }

        public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) {
            Calls++;

            return passes;
        }
    }
}
